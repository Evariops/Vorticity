# Compaction: when it runs, and what a store that cannot delete does to it

Compaction is the [levels and compaction](13-dataset.md#5-levels-and-compaction) section of the
dataset design, plus what this document describes:

- one planner, whose jobs run one per `CompactAsync` call, one per turn of a background loop, or in
  the commit that takes level 0 past its ceiling ([three drivers](#2-three-drivers-for-one-job))
- a plan that descends the trees rather than reading them
  ([planning](#3-planning-in-the-depth-of-the-tree)), and a partition's objects merged by size class,
  then consolidated once it is calm ([tiers and consolidation](#tiers-and-consolidation))
- purges of marks, and long vectors stored out of line
  ([folding marks](#4-folding-marks-where-no-merge-goes))
- a profile for a store under a retention lock ([stores that cannot
  delete](#5-stores-that-cannot-delete))

The document weighs the ways to drive the same work (inline in a writer's commit, on demand, or in
the background), and what changes on a store under a retention lock, one that refuses to delete or
overwrite an object before a date: S3 Object Lock, Azure immutable blob storage, a GCS bucket lock.
Nothing here changes the commit protocol. The format carries the tallies in a page's summary and a
pointer in each level of a header, a vector's reference in an entry, and the locked-store flag in a
header. A page without tallies still reads, and a reader that does not know about vectors out of line
refuses such an entry rather than reading it wrong.

## 1. What compaction buys, and what it costs

Compaction holds the read bounds of the dataset (see [where the N's
hide](13-dataset.md#1-where-the-ns-hide-and-the-bound-that-replaces-each)), under the organization the
layout declares ([the invariant](13-dataset.md#52-the-invariant)):

- Under tiers, the default with a partition, a partition holds at most `k − 1` objects of a size class
  while it receives rows, and one object a key once it is consolidated, its late rows apart.
- Under levels, the default for a sort key without a partition, level 0 holds at most eight objects,
  and the levels above 0 are key-disjoint, so a lookup touches one object in each of them.
- The marks a delete leaves are folded, so their rows are no longer stepped over and their bytes are
  freed (see [deleting rows](13-dataset.md#12-deleting-and-updating-rows)).

Under tiers it pays a rewrite of each row a size class it crosses, and one more at its partition's
consolidation: two to three times a row ([tiers and consolidation](#tiers-and-consolidation)). Under
levels it pays more. A row appended in a small commit moves up through every level,
and each merge rewrites the part of the next level it overlaps. That is about `F / 2` rewrites per
level at a fan-out `F`, about 5 at the default of 10. A load of gigabytes goes straight to the level
that holds it and is written once (see [the invariant](13-dataset.md#52-the-invariant)). The levels
grow by `F`. With level 1 at 1.25 MiB, a tebibyte of data needs seven levels, so a byte that came in
with a small commit is written about 35 times before it settles.

An object that overlaps nothing in the level above is not rewritten at all: the job that pushes it up
takes its entry out of one tree and puts it, byte for byte, into the other, as RocksDB's trivial move
does. A merge of that object alone would have written its rows again as one object of the same
bounds, so a move changes the bytes written and nothing of the levels' shape. It applies to an object
of a level above 0, sorted on the key as a merge leaves every object there, with no marks and no
fragments to fold, no larger than what an object written into the level above targets, and of the
current schema, which only a rewrite brings an older object to. Its commit makes the checks a merge's
makes: the entry is still the one planned, and nothing came into the part of the level above that its
bounds cover ([three drivers](#2-three-drivers-for-one-job)). With keys that arrive in order, as when
time leads the sort key, nothing ever overlaps, and every row is rewritten once, by the merge
that takes it out of level 0. Measured on 96 appends of 100 rows over five levels, compaction rewrote
9 600 rows and 204 408 bytes with the moves and 25 200 rows and 536 584 bytes without, and on the
churn bench, a thousand appends of 2 000 rows at the tail, 22.0 MiB against 42.8 MiB. With keys that
arrive at random, a move is rare and the bytes barely move: 770 620 against 796 300.

A move keeps the object's size, so with keys in order objects stayed the size they left level 0 at,
whatever their level's target. `CompactionTrigger.Neighbours` makes them grow. An object's size class
in its level is how many times the fan-out `F` fits between its bytes and the level's target, 0 within
`F` of the target. When the objects at a level's end hold a run of at least `F` adjacent objects of one
class above 0, those whose bytes stay within the target are merged into one, in their level. Its class is that of its bytes: the class above when rows make most of them, where it joins the run before it as a carry does, and its inputs' class when they were objects of a few rows, whose bytes are mostly a file's fixed cost. A byte is rewritten once a class,
`log_F(target / size)` times, as the leveled merges of random keys rewrite it once a level, never once
per append, and the objects end within `F` of their level's target. The plan reads the last `F²`
entries of each level for it, a page or two down the tree's right edge, and ranks it after the purges
of marks. On the 96 appends above, at `F = 3`, it halved the objects above level 0, from 32 to 16,
level 4's growing from 6 KB to 23 KB, for 9 600 more rows rewritten.

Such a merge takes only neighbours that nothing of another level sits between. Its output covers the
holes between its inputs, and an object of another level in one of them would overlap it: that
object's later move into this level, or this one's move into the object's level, would read and
rewrite it, and every move after that again. The plan cuts the run at the newest such object and merges
what is left past it when that still makes `F`. Merges of neighbours belong to the leveled style alone:
a tiered level grows its objects by concatenating them already, and a merge across another level's
object would change the order a dataset without a sort key answers in.

Keys in order keep the merges' runs whole as long as each level holds one band of the key space, the
oldest at the bottom, which the pick of the object a full level gives up decides
([the pointer](#the-pointer-and-the-tiered-plan)). On the lifecycle bench, 48 hours of requests in
order (`vortex-queries --micro lifecycle --partition off --late off --writers 1 --deletes off`), merges
across other levels' objects rewrote a row 5.9 times, every move into their range reading them again.
With the bands kept and the merges held to them, a row is rewritten 2.78 times, once out of level 0
and 1.78 times as its objects grow, every job out of a level above 0 is a move, and the dataset ends
with 51 objects, 47.6 in a window of a day, its store having taken 284 bytes a row. A job's objects
end at their target, the rows its writer holds counted: an exit of level 0, 168 KB for level 1's
128 KiB, writes an object of the target and one of the rest, which the merges grow. When an exit wrote
one object past the target, ten of them passed level 2's target before they made a run, and never
merged: 1.85 rewrites a row, 152 objects, 110 in a window of a day, 319 bytes a row. Partitioned by
hour (`--partition hour`), the dataset is tiered instead: 2.92 rewrites a row, 1.94 by the size
classes and 0.98 by the consolidations, and 26 objects in a window of a day. Without the merges a row
is written once.

Three costs remain, and this document is about them:

1. Who pays, and when. On demand, whoever calls `CompactAsync` pays, when they call it. Until then a
   dataset reports its lag, and lookups touch the extra objects.
2. Planning. If a level's bytes were only recorded in its leaves, a plan would walk every level's
   tree. That is nothing at the 40 objects of the churn bench, but at a tebibyte of 4 MiB objects it
   is 262 144 entries of a few hundred bytes each, about a thousand pages and a hundred megabytes, on
   every plan. A plan descends instead ([planning](#3-planning-in-the-depth-of-the-tree)).
3. Marks in levels no merge reaches. Level 4 of a ten-million-row dataset is only rewritten when
   level 3 overflows into it, and the top level never is. The marks there last until a delete would
   pass an object's share or its vector's cap, and that delete then rewrites the object itself.
   Meanwhile each commit that marks a row writes again the leaf page holding the object's entry, and
   that page grows with the vectors it holds ([folding marks](#4-folding-marks-where-no-merge-goes)).

## 2. Three drivers for one job

A job is always the same thing: one plan's inputs read in key order, the outputs written, and one
`ReplaceObjects` committed. That commit names the entries it read, and is abandoned when one of them
changed (see [rebase](13-dataset.md#82-rebase-by-re-applying-operations)). A leveled job also names
the part of the level it writes that its outputs fill, the range of the sort key its sources
span, and the commit looks at that part of the level again: an object there that the job did not read
abandons it. Another compaction moved that object in after the plan, from another level, and the
outputs would overlap it. Two jobs that start from the same level always share such an object, but a
job that empties level 0 into the first level that can hold it and one that pushes an object of
level 1 into the same level may share none, and only that look keeps the level key-disjoint.
A tiered job names its partition's objects alone, and the planner gives no two jobs one partition.
Correctness never depends on who runs the job, so every driver below is safe against every other one
and against every writer. What they differ in is who waits for the work, and how much of it is wasted.

| driver | who pays | the bounds | what goes wrong |
|---|---|---|---|
| inline, in the commit | the writer whose commit crosses a bound | held after every commit | the writer's latency jumps by a merge, a writer does work for everyone, and a crash leaves the outputs for vacuum |
| on demand, `CompactAsync` | the application's scheduler | held when it runs, and `Lag` says by how much they are not | a scheduler that stops leaves lookups touching every object since |
| in the background | a loop in some process | held within the loop's reaction time | two loops on one dataset race for the same inputs, and the loser's outputs are garbage |

### Inline

A commit that takes level 0 past its ceiling runs one job before it returns, so no version the writer
publishes leaves level 0 over its bound for long. The price is the merge's own latency, added to that
one commit. Merging level 0 into level 1 costs 0.3 to 1 ms per commit in the churn bench, spread over
the commits between merges, but a merge into a deep level takes seconds. So inline mode is limited to
level 0, and to inputs under a byte budget. Past the budget, the job is left to another driver and
the writer returns at once. Inline mode suits a dataset with one writer and no spare process, since
the writer is the one party that knows level 0 just grew.

`DatasetOptions.InlineCompactionBytes` (0 by default) turns it on. An append, an import, a replacement
or a change that adds to level 0 runs the level 0 job before it returns, as long as the job reads no
more than the budget. The commit stands whatever happens to the job: a job that fails, or that the
caller cancels, leaves level 0 to the next commit or to another driver, and the caller is told the
commit's outcome. On the churn bench the same bytes are written either way. Inline, the merges move
into the writers' commits, and an append takes 0.9 to 1.1 ms on average rather than 0.5 to 0.7, with a
99th percentile of 6 to 9 ms rather than 3.

### On demand

This is the default. `PlanCompactionAsync` says what is due and what it costs, and `CompactAsync`
runs one job. The application loops, from a scheduled task or after a batch of commits. It is the
right default for a library, because the library never starts a thread of its own, and it is the
primitive the other two drivers are built from.

### In the background

`RunCompactionAsync(schedule, cancellationToken)` is a long-running call that any host runs: a hosted
service, a worker, a console. It asks for the latest version, which costs one round trip for a
handle that knows its own is recent (see [the latest
version](13-dataset.md#83-the-latest-version-in-one-request)), plans, runs the job, and sleeps when
nothing is due. Two limits shape it: a budget of bytes written per second, so compaction never takes
the store's bandwidth away from the writers, and one job at a time per loop. A hosting integration,
such as `IHostedService`, belongs in a separate package, since the dataset package takes no
dependency.

Two loops on one dataset are safe but wasteful: both plan the same job, and one commit loses. A
dataset that several processes write is therefore compacted by a process of its own, which runs the
dataset's only loop, while the writers leave inline compaction off, as it is by default. Two
compactions then never run at once on the dataset, and nothing has to coordinate them. Loops that must share a dataset avoid the waste by a deterministic choice of job, which costs no request. Under tiers, each partition is dealt to one of the `n` loops by the hash of its value, and a loop plans and runs the jobs of its own partitions alone, so no two loops ever take one job. Taking the job ranked at a loop's index did not hold under tiers: the ranks move with every commit, a loop that finished took the job another was running, and four loops on 40 partitions did 20 to 32 % of their jobs twice, one of each pair for nothing. Dealt by partition, they did none twice, for a drain 8 % longer on long jobs, the hash dealing 40 partitions unevenly. Under levels, the planner ranks the due jobs, and a loop that knows it is one of `n` takes the job whose rank is its index.

`RunCompactionAsync` takes a `CompactionSchedule`: the options it plans against, `BytesPerSecond`,
the `Idle` sleep, and `Loops` and `Loop` for loops that know one another. The planner ranks the due
jobs so that no two read or write the same level (a job on levels that share nothing never takes
another's input), and a loop takes the one ranked at its index. A descent and a read of every leaf
rank the same jobs.

When nothing is due, the first loop also runs the vacuum, every `VacuumEvery`, half the retention window by default, a quarter either way, the first within its first four idle turns. A job deletes the data objects it replaced once no retained version reads them, but not the commit objects, which the trees of later versions share: only the vacuum's walk of the retained versions tells which one no version reads any more. Left to the application, the vacuum was forgotten: a pipeline's bucket held 11 664 commit objects for 925 MB. A vacuum is cheap since its walk reads each retained version once, 0.4 to 0.6 s for 800 versions at 20 ms a request. The first one runs soon after the loop starts, since a period of 3.5 days, half the default window, would never come for a maintainer restarted every day. No loop vacuums a store under retention locks, where the vacuum asks for a head per object to read its lock.

Leases were weighed and set aside. A loop would create `leases/<level>/<end of a span>` with
put-if-absent before a job, and hold its levels until the span ends. Once the commit of a job checks
the part of the level it writes ([three drivers](#2-three-drivers-for-one-job)), a lease protects
nothing: it saves the work of a job two loops that do not know each other would both run. For that
it costs a request per level a job touches, objects that vacuum has to list and delete, and on a
store under a retention lock a hundred bytes kept for the lock's term per lease. A process of its own
for compaction saves the same work for nothing.

## 3. Planning in the depth of the tree

Everything the planner needs comes from where a commit already writes:

| what the plan needs | by reading every leaf | by descending |
|---|---|---|
| a level's bytes, against its capacity | the sum of every leaf's bytes | the tallies on its top page, which the header carries. Every page's summary carries the tally of what lies under it, folded by a commit as it folds the bounds |
| the objects a level 0 job takes | level 0's leaves | the same, since level 0 holds at most eight |
| which object of a full level goes down | every leaf of the level, then the largest | one descent along the largest tally, which finds the same object |
| the object past where the level's last job stopped | not applicable | one descent along the keys, past the pointer the header records with the level |
| where a tiered run ends | every leaf of every level | one descent per other level, to its first object past the run's start |
| the objects of the next level a job meets | every leaf of that level | a walk that skips each subtree whose bounds lie outside the job's range |
| whether the job moves its object rather than merging it | the same walk, which finds none, and the object's own entry | the same |
| the objects with too many fragments | every leaf | a walk into the subtrees whose tally holds one |
| which objects hold the most marks | not applicable | the share of marked rows in every tally, for the [purges](#4-folding-marks-where-no-merge-goes) |

A plan reads the header, one path per full level, and the job's own objects. A cold plan over 125 000
objects asks the store for two pages, where reading every leaf would ask for 124. The tallies follow
the same rule as the row sums already carried up the tree: each commit rewrites the path above an
entry it changes, so a tally costs no page the commit does not already write. A level holding a page
without tallies is planned from its leaves until a commit rewrites that page.

### The pointer, and the tiered plan

Each level of a header carries a pointer, the tree key where the level's last job stopped, as LevelDB
keeps one (`CompactionOptions.Pick = RoundRobin`). A job records it with its replacement, which moves
it when the replacement applies and leaves it when the job is abandoned. Every later commit carries it,
and a level that empties forgets it. The level's next job takes the object past it, wrapping around at
the end, so that rewrites sweep every key in turn, where the largest object lies wherever it happens
to be. The object past a key sits at the end of one descent along the keys, just as the largest sits
at the end of one descent along the tallies, so the two picks cost a plan the same.

They do not cost the same in writes. The churn bench on ten million rows and 30 000 commits takes the
same jobs either way for roughly its first 15 000 commits. With two seeds out of three, the round
robin then writes 6 % more in compaction (2 149 and 2 146 MiB against 2 032 and 2 019), and 16 % more
in the objects the updates and deletes write themselves. The third seed takes the same jobs
throughout. So a leveled dataset keeps the largest-first pick by default (`CompactionPick.Auto`).

The default asks one thing first: whether the level's first object moves up whole, overlapping nothing
in the level above. Keys in order make every object do so, and any pick then moves rather than merges,
but the largest scatters the level's band, since objects of one size class differ by a few kilobytes,
where the first keeps the band whole for the merges of neighbours at the level's end. Keys at random
make the first object overlap the level above as much as any, and the largest goes, as before: the churn
bench at random keys takes the same jobs and writes the same bytes either way. The question costs a
walk of the level above that skips what lies outside the first object's range. Under late rows and
four writers, where levels interleave anyway, the lifecycle bench rewrites a row 6.9 times without a
partition, against 2.8 when no row comes late ([late rows under levels](#late-rows-under-levels)).

A tiered job concatenates a run of one level's objects with nothing else between them, and where the
runs are depends on every level's order. The longest run can only be found by reading every leaf. The
run that starts past the pointer needs much less: it ends before the first object another level holds
past its start, and one descent per level finds that object, rather than one per level for each object
of the run. The descents run side by side, and a walk of the source level between the two reads the
run itself. A plan over 100 000 objects by arrival, with levels interleaved object by object, asks the
store for one page, where reading every leaf would ask for 71. A dataset ordered by arrival keeps each
level's objects together, a run of level 0 moving up whole and the next landing past it, so the two
picks take the same runs there, and the round robin is a tiered dataset's default.

A tiered job rewrites nothing a concatenation would give back as it was. From the level whose target
first reaches the cap on an object up, each level aims at the size of the one below, and concatenating
its full objects writes them again unchanged: once a level, past the first gigabyte at the defaults,
three of a row's seven rewrites at a tebibyte. So the job takes of its run what `TieredPart` says. Its
leading objects of at least half the destination's target, holding no marks or fragments, move whole,
their entries changing level, as do an append's objects that large out of level 0. Half, because a
writer rolls an object on an estimate that misses by what a batch or a chunk weighs, so an object
rolled full can fall short of its target, and a concatenation of objects of half the target or more
pays every byte of them to save at most every other object. Otherwise its leading objects under that
are concatenated, a whole number of targets of them when they hold one, the rest waiting in the level,
so that the last object written is about full rather than a remainder every level above keeps; one
object alone, which a concatenation would write again as itself, moves. Below the cap it meets objects
that do not stand yet a level or two up, and goes with their concatenation. From the cap up, where the
objects about it stand for good, it would climb alone to the top, an object for every small append
between large ones: there it joins the object after it when the two fit the target, and moves alone
only before an object that leaves it no room. Appends alternating 32 768 rows and 1 024 at scale 1
ended in 35 objects rather than 49, for 33.4 MB rewritten rather than 19.5.

Over the life of a dataset without a key, appends each followed by the compactions they make due,
scaled down from the defaults to a fan-out of 4, 64 KiB at level 1 and a cap of 1 MiB from level 3 up
(`AuditTieredMovesBenchmarks`), and at four times those sizes:

| 8 M rows, 81 MB, scale 4 | rewritten | time | allocated | objects | top level, of its target |
|---|---|---|---|---|---|
| appends of 16 384 rows, concatenated | 379 MB | 1 456 ms | 509 MB | 30 | 1.02 |
| moved | 158 MB | 922 ms | 292 MB | 37 | 0.91 |
| appends of 131 072 rows, concatenated | 379 MB | 1 266 ms | 466 MB | 20 | 1.02 |
| moved | 76.5 MB | 468 ms | 162 MB | 24 | 0.91 |

The objects stay at the size their writer gave them rather than being repacked at every level: the
first level at the cap concatenates objects a quarter of its target here, a whole number of targets of
them, so its last object falls between three quarters of the target and the target. At the defaults'
fan-out of 10 they are a fifth of it, and the objects nearer their target.

### Tiers and consolidation

A dataset with a partition, or without a sort key, keeps each partition's objects in size classes
(`LayoutOrganization.Tiered`). Level 0 holds every partition's objects not yet consolidated; the
partition leads their tree keys, so a partition's objects lie together and a plan reads level 0's
leaves partition by partition. An object's size class is how many times the fan-out `k` (the layout's
`MergeFanout`, 8 by default) multiplies its bytes before they pass the layout's object size over `k`:
class 0 for an object of 128 MiB or more under objects of 1 GiB, one class more for each factor of `k` below. When a partition holds `k` objects of one class or more, a job merges them, the class of the smallest objects first, since that retires the most objects a byte, and no two jobs on one partition. A merge due as its class reaches `k` takes the `k`. One behind takes the whole class when its bytes stay within the object size and the merge within the jobs' budget ([the memory of a job](#the-memory-of-a-job)), and otherwise as many objects as those bounds let, `k` at least, neighbours in the order of the tree. Neighbours lie in the same leaves, so the commit that takes them away rewrites few: 512 objects taken as they arrived wrote a commit object of 3.1 MB, 512 neighbours one of 294 KB, and the whole class of 3 283 one of 72 KB. A merge of `k` at a time took 469 jobs and 510 MB of commit objects to empty that class, and its output fell back into its inputs' class. A partition so holds at most `k − 1` objects a class while it receives rows and its merges keep up, and a row is rewritten once a class it crosses: once, on appends a class below the object size over `k²`, in `DatasetTiersTests`' 96 000 rows of small appends merged four at a time.

When a partition has received no row for `CalmAfter`, an hour by default, measured by the dataset's
clock (`DatasetOptions.TimeProvider`) from the arrival of its newest row, its next job consolidates it:
its level 0 objects, with the consolidated objects of level 1 they meet, are rewritten into objects
disjoint by key, each up to the object size, a key never cut, at level 1. Objects already disjoint, as
a partition whose keys come in order leaves them, are not all rewritten: a run of neighbours each under
the object size over `k` is merged with what of level 1 lies in its span, so that the partition still
ends in objects of about the object size, and the others move to level 1 whole when it holds nothing
in their span. A move past an object of level 1 would leave that object inside its range, which the
commit refuses ([three drivers](#2-three-drivers-for-one-job)). On the lifecycle bench partitioned by
hour, keys in order, an hour's eight objects moved whole and a window of a day covered 147 objects;
merged, it covers 26, for 0.98 rewrites a row more. A consolidation of several objects' worth runs as
one stream of objects, its merge cut in slices of the key past a slice's rows (below), so that it writes
the same objects at any degree. It ran in ranges of the key side by side, as many as the session's
lanes, each writing objects of its own: a consolidation wrote 12 objects on one lane and 14 on four
(2026-11-17). A piece of a consolidation merges the ranges below its cut as the slices of one stream,
each reading of its inputs the granules of its range alone, the ranges cut from the inputs' sparse
indexes, and counting apart what it takes of each input, the counts added once the stream ends: its
ranges merged side by side before, each writing objects of its own on a worker of its own. The calm partitions are taken the earliest arrived first. A calm partition whose consolidation fits the jobs' budget is consolidated even when one of its classes is full, rather than merged by class first: on 40 calm partitions of 30 small objects, the merges by class then the consolidations took 194 jobs and 50 MB of commit objects, the consolidations alone 40 jobs and 10 MB. One whose consolidation does not fit is merged by class until it does. A consolidation of a dataset that declares identifiers writes its partition's index beside its objects ([the index of a consolidated partition](13-dataset.md#44-the-index-of-a-consolidated-partition)), when it rewrites objects: a partition left one object by its merges moves to level 1 whole, its own postings with it. `ConsolidateAsync` consolidates every partition whose level 0 holds an object, calm or not, as after a load of history, and `CalmAfter = null` leaves consolidation to it alone. A row is rewritten once more by its partition's consolidation: 96 000 rows of `DatasetTiersTests` once by their merges and once by the consolidation.

Rows that come late for a consolidated partition land in level 0 as small objects of that partition,
and wait in its size classes. A calm partition whose level 0 weighs less than an eighth of the
consolidated objects it meets (`LateShare`) is not consolidated again: its late rows wait for more, so
that a late row is rewritten some nine times at most there, rather than each late append rewriting
the consolidated objects it falls among.

A plan reports each partition its level 0 holds (`CompactionPlan.Partitions`): its objects and bytes
there, what its merges owe (`Lag`, the objects past `k − 1` a class), whether it is calm, whether it
never becomes calm (`NeverCalm`: more than `2k` objects of class 0 piled, the first of them 48 calm
periods old, a partition that keeps receiving rows), and whether it holds too little for its objects
(`TooFine`: calm with fewer bytes than the object size over `k²`, a partition finer than its rows fill).
A partition that never calms is reported rather than consolidated over and over; one too fine says the
partition is finer than the rate of its rows, which a coarser partition answers.

What the partition buys is reads that end where its values end. A filter on the partition's column
that keeps whole values reads whole objects but at its ends. An expiry by such a filter
(`ExpireAsync(filter)`) drops whole objects, opening none ([deleting
rows](13-dataset.md#12-deleting-and-updating-rows)). A range asked part by part (`AskByValuesAsync`)
asks each part of objects of its own ([parts of a range](12-index-reads.md#45-aggregations-of-a-dataset)).
An object's counts answer a question keyed by a function of the partition's column the object holds
constant ([the counts](10-indexes.md#49-vorticitycolumncountsv1-a-columns-counts)), and the rollups sum
the counts of runs of objects ([rollups](#rollups)).

A job over a schema with a variant column merges its inputs twice. The first pass reads the sort key
and the variant columns alone, through a projection of each input, and counts the paths of the
partition's rows, from which it chooses what the objects shred, each column in long chunks of its own,
with a path map that lets a question read one key alone ([shredded
variants](11-write-strategy.md#10-shredded-variants)). The second pass writes the objects, each counting
its paths again as its rows are written, at their granules, for its path directory. On a million rows
of six to eight attributes, counting and choosing take 256 ns a row, and the shredded write 310 ns a
row more than the whole one (`vortex-queries --micro shredwrite`). An append shreds nothing, unless the
dataset asks it to shred the paths it declares (`DatasetOptions.ShredOnAppend`), but counts its paths for its
own directory, on another lane than its writer's (`DatasetOptions.PathDirectoryOnAppend`).

The first pass reads an input's path directory rather than parse its rows when the directory tells every count a shredding is chosen from: the input read whole, written under the current schema, and its directory complete, with no list, whose elements' class it does not keep, and no number but 64-bit doubles: it counts doubles and 32-bit floats together, as a question reads them, and marks a path whose numbers are all doubles, which a shredding counts alone (`DirectoryEntry.WholeDoubles`, a flag a reader before it ignores). Unmarked, every path of log records' attributes that held a duration refused its directory, and a tier merge of two objects parsed every row of their attributes again in its first pass, 6.6 s of its 11.3 at degree 14; a directory written before the mark tells no double still. The directory is read from the job's own read of the input, with no request more, and the rows give the census their count and the bytes of their values alone, which size the variant chunks. A merge of 52 appends of metric records spent 15 % less processor (`vortex-queries --micro backlog`, 2026-11-16). A directory of version 3 keeps those two numbers too, as the writer counted them: an input every variant column of which has one is counted from its directories alone, and none of its rows is read, where they were put back together from their shredding only to be sized, a log's attributes from some four hundred keys a row. Its rows are then the object's as written, those a deletion vector removed since included, as its paths always were. A job's result counts these inputs (`InputsToldByDirectories`, internal). Any other input is counted by its rows. A census counts a value that repeats the row before's once, its counts taking the repeats at once and its spans the rows they lie at, as do the shredder and the writer's read of the shredded keys for their indexes: a metric's series holds the same attributes row after row. The rows that follow each other repeating a value are taken as one run: each node of the shredding appends the run's rows and its last value at once, and each key read for the indexes fills the run's slots with the first row's. A consolidation's series holds a run of a thousand rows, sixteen of each of its inputs, which a node took a row at a time.

The merge hands its writer runs of a few rows, one series of one input each. A job's writer stages them until they make a block exactly (`VortexWriteOptions.StagedRows`), the run that crosses the block cut there, then sends the block through: its paths counted, its columns ingested on the session's lanes, its chunks emitted. A block cut a few rows past the boundary left those rows in transit, and every block after it was copied there whole and concatenated again to go out. The writer does not copy a run as the merge chooses it. The merge notes where the run lies, the input's batch, the row and the count, in the map of the block it falls in (`BlockMap`), the run that crosses a block cut there, and sizes it at the bytes a row of that batch names, measured once a batch; it hands the writer a block's map at once (`VortexFileWriter.WriteBlockAsync`), short as an object or the merge ends. Whether an object has reached its size reads the writer's size with the map's rows counted as the writer counts the rows it stages (`SizeWith`), so that the objects end at the rows they ended at when the merge handed its writer one run at a time. The merge's thread spent 2.5 s of a consolidation copying runs before, then 0.9 s handing them over, one call of the writer a run, 1.5 million a consolidation of the metrics. Once the block is full, it is assembled by a pump of the session's core while the block before it still goes through: its runs copied out of the inputs' batches, then concatenated into one node. Two blocks may be assembled side by side. Assembled one after another, they made a chain of 6 s of a consolidation's 9.6.

A job's writer sends its blocks through several at once, each column's work on them in the order of the blocks ([the write](11-write-strategy.md#37-memory-allocation-and-threads)), when its object is aimed at 32 blocks or more at the bytes a row of the job's inputs holds (`VortexFileWriter.LineObjectBlocks`). Whether such an object has reached its size then reads the writer's size as the block four before the one the merge fills landed, with the rows staged since counted at that block's rate (`VortexFileWriter.FlightsAhead`). Read as the block before landed, the size would depend on how many blocks a degree lets go through at once; read four blocks back, it is the same at any degree, and the objects end at the same rows on one lane and on fourteen. Four blocks of lag in thirty-two at least keep an object near its target. An object aimed at fewer blocks reads the size one block back, as before, and its writer sends its blocks through one at a time.

An input's batch stays as long as a writer reads a run of it. The input's scan hands each batch over to the merge (`BatchHandover`), keeping the lane that decoded it, its arena and the segments it reads, until the merge gives it back; the batch's keys are encoded beside its decode, on that lane. The writers note each run they keep where it lies in a ledger the merge's writers share (`RunLedger`), a count a run, and a staging leaves the ledger once its block's assembly has copied its runs. When an input moves past a batch, the merge sets the batch aside with the number of runs noted then, and gives it back to its scan once every run below that number is copied. The merge's thread neither copies a run nor waits for a copy: before, it asked the writer to copy the runs of a batch about to change and waited for the blocks in flight to copy theirs, 7.0 s over two rounds of the metrics consolidation at degree 14 waiting for whole assemblies, 2.9 s for the copies alone (2026-11-17). A batch now lives until the writers copied its runs, a block or two past the input's move: a consolidation in ranges under a tight budget held 8.77 MB where it held 7.98. As the merge ends, its writers copy what they still keep of its inputs (`LetGoOfInputsAsync`), once. A run of a block or more goes into maps too, a block's at a time, cut at the blocks of the object: it went through the writer at once, where it lay, past its line, and a merge of objects whose keys run long, a level's, sent its writer 33 calls of a test's 744 blocks, none of them on the line, where its blocks now go through the line at four lanes, 730 of them. The copy of a run into the block keeps once a text whose view names the bytes the buffered view before it names (`CanonicalArena.CopyFrom` with `shareRepeats`): an input's reassembled rows view a series' repeated attributes once, and the comparisons that follow, the statistics of the views, the keys read for the indexes and the census of the block, end on two spans at the same address. The writer copies each row's bytes again as it gathers the block's columns, so that no encoder meets a shared view: an encoder sizes a text column by its buffers, and choosing on a shared heap would choose other encodings.

A merge of one piece is cut into slices of the key (`SlicedMerge`), so that it runs on more than one lane. The cuts come from the inputs' sparse indexes, as a consolidation's ranges do, and their number from the job's rows and its inputs' bytes alone, one slice for 2^18 rows (`DatasetOptions.SliceRows`, internal), 64 at most, and no more than half the chunks an input holds of a column, about, never from the degree: the same job cuts the same slices at any degree. A slice reads, at each of its cuts, the chunk each of its inputs holds there, which the slice beside it reads too: sixty appends of a chunk a column each, cut in six slices by their rows alone, were opened whole by every slice, and four slices at once held the whole jobs' budget before the writer took a row. The job's flow takes the slices in order and writes their runs through one output, while the slices after the one it takes are merged ahead, each on a lane of the session that is free, by a merge bounded by its two cuts, three ahead at most. Their runs wait in order. A slice no lane took, the job's flow merges itself. A batch an ahead slice moves past is dated when the job's flow takes the slice's last run (`KeyOrderedMerge.Retiring`), so that the ledger gives it back once the runs the flow noted until then are copied; a slice's merge is let go once its runs are copied. A job's batches are priced for three slices ahead whatever its degree, and are not priced again during the merge, which would depend on the order the lanes ran in. Each slice counts what its blocks hold in an account of its own (`CountedAccount`), inside the job's inputs' account. The slices ahead start once the slice the flow takes gave its first run: what that slice holds by input open, times the inputs a slice reads, prices a slice when it is more than the price its inputs' batches give. A slice ahead is admitted while the room holds its price and what the slices merging may still take, the one the flow takes included, each up to its price; it keeps runs waiting while it holds no more than its price, then merges a run as the flow takes one. Its runs hold its inputs' batches: kept waiting by their count alone, 16 384 of them, a slice of log records kept every batch of its range, and two slices of two inputs held 608 MiB of a jobs' budget of 672. A run also ends at a slice's bounds, so a merge of two slices or more writes other bytes than a merge of one piece, the same at any degree. A job's result counts the slices it cut and those a lane merged ahead (`MergeSlices`, `MergeSlicesAhead`, internal).

A shredded variant column's chunk takes the most of a job's writer to compress: its rows read and laid out into the shredding's columns, each compressed in turn. As one item of a block's columns, it held every block after it: the chunks that compressed one took 19.9 s of the 31.5 s the backlog bench's writers ran at degree 14. A job's writer takes the chunk from its column as it reaches its target, and compresses it on a lane of the session's of its own when one is free, on the writer's thread otherwise, while the blocks after it go through and the column gathers its next chunk; every column's chunk goes so since, shredded or not, each emitted by a task of its own ([the write](11-write-strategy.md#9-a-datasets-objects)). Two of a column's chunks are in emission at once: one at a time, a chunk of a log's attributes took 139 ms to compress while the column gathered the next in 80, and the writer waited for the one before 1.9 s of a merge's 8.4. The chunk's blob is written where the column's chunk after the next reaches its target, or at a flush, the end of a group of rows or the completion, before anything the columns hold then: the same place in the file whatever lane compressed it and whenever it ended, so that the file is the same at any degree. It is compressed against the file's encoding ids as they stand, and the ids it met past them become the file's when it is written, in the order it met them, its tree renumbered onto them, as a chunk compressed across the writer's threads is. A file that holds no id yet compresses its chunks in place: an id new to a chunk at index 0 is left out of its tree as the default, where no renumbering reaches it. What the chunk holds counts toward the writer's gathering ceiling until it is compressed, and what it will encode to toward the object's size until it is written: the ceiling bounds the chunk and the next one together, which a job's memory is priced by, and counted apart they took 650 MiB of a job priced at 490, whose next plans the handle narrowed to eight inputs. It sends it on a lane of its own when the session's `LaneBudget` has one free, and the merge goes on staging the next block meanwhile; the block before lands first, so blocks go through one at a time and in order, and the file is the same on whichever lane they go. Without a free lane the block goes through on the merge's thread, as at degree 1. The size the merge reads to cut its objects (`VortexFileWriter.Size`) is taken before a block leaves and counts the rows staged since at the rate then, so that the objects end at the same rows at any degree: read as it stood, it would depend on how far along the block in flight is. Anything else that changes the writer, a write of a larger batch, the declaration of the key index at the object's end, its completion or its abandon, lands the block in flight first. At degree 14, the backlog bench's tier merge of 66 inputs went from 17.5 to 15.1 s and its consolidation of 69 inputs from 33.8 to 26.3 s (`vortex-queries --micro backlog --degree 14`, 2026-11-16).

A block's columns go through as items of the writer's fan, a column's statistics one item and its index builders another. A variant column whose shredded keys a job's object indexes feeds every key in its builders' item: each key read from the rows in its class, then its sheet, its locates and its statistics over the granules, every one over the block's rows. A log's attributes shred hundreds of keys, and that one item held every block: in a merge of 12 million log records at degree 14, the blocks' ingest took 9.7 s of the job's 13.8, and the merge waited 8.3 s for it. Such a column's keys now go through in two turns of the fan (`IndexWriter.ReadKeys`, `IndexWriter.FeedKeys`). In the first, beside the columns' statistics, the columns' index builders and each column's gathering of the block's rows into its chunk, one item reads the block's rows once for every key: each row's dictionary, and where each field a key asks for lies (`VariantFieldExpr.KeyReads.Read`). In the second, which holds them alone, the keys are dealt into as many groups as the writer has lanes, a key to each in turn, and each group writes its keys' columns from that read into an arena of its own and feeds their builders. The longest items go first in a turn, since a turn ends with its last item. Only the columns whose chunk is due then go through a turn of their own, to be compressed (`EmitDueAcrossAsync`): before, every block ran that turn for every column, to gather its rows, and a block now runs it in a third of the cases. No builder of a key reads another's, and each is fed the same rows in the same order, so the file is the same at any degree, byte for byte. On the backlog bench's logs (`--logs`, 30 million rows, 2 GiB), the jobs merged 909 and 965 thousand rows a second instead of 637 and 735, and the consolidation of all 30 million rows took 27.0 s instead of 43.6; the metrics' jobs, whose two variant columns shred a few keys each, took the same time (2026-11-16).

### The memory of a job

A job's flows do some work ahead of what they must: an input decodes windows past its own two, a merge in slices merges the slices after the one it takes on lanes of their own, a writer sends up to four blocks through its line at once. Each such unit starts only while the room the jobs' budget leaves the job holds it (`JobMemory.AdmitsRead`, `AdmitsMerge`, `AdmitsWrite`), the writer first: a window or a slice ahead leaves the writer what it was priced at past what it holds, a block in flight leaves nothing. A unit not admitted waits for its turn on the path the job takes anyway, holding no lane: the window decoded as the input reaches it, the slice merged by the job's flow, the block sent through once the blocks before it landed. What is written does not change. A job's result counts the units it held back (`AdvancesHeldBack`, internal).

A job's flow holds one of the session's lanes from its start to its end, taken once its memory is reserved, and gives it back while an object it writes waits for the store: a pipe the store's put has not emptied, or the put itself at the object's commit (`Execution.HeldLane`). Another work computes on the lane meanwhile, and the flow takes it again after, waiting for it while it holds none, as a job waits for its first. A lane given back goes to a work that waits for one before any work takes a free one, so the flow is served by the next lane anyone gives back: a unit's end, a worker's, another job's flow waiting on its store or ending. A slice merged ahead that waits for the flow to take its runs holds its lane meanwhile, but the flow does not wait for it. A job's result counts these waits (`StoreWaitsWithoutLane`, internal). A range of a piece that a worker writes keeps the worker's lane.

A job's memory is its blocks, and they never pass the session's jobs' budget (`VortexSessionOptions.JobMemoryBudget`). By default that budget is half the process's limit, read again as the limit moves, and each block a job rents asks the process too, which weighs it against the rest of its memory as it weighs a question's: given the whole limit and never refused there, the jobs' blocks and those the pool parked for them, 1 222 MiB, filled a heap held to 1.5 GiB, and a writer's own array found no room. The blocks of a job's inputs, their reads, their batches and the merge's keys, rent under one hard budget, and those of its writer, each object's writer and its sink, under another, both under the job's (`JobMemory`). A block none of them can take is refused rather than counted past, as a query's lane growth would be: the job gives up its objects and its blocks, the handle takes half as many inputs in its next plan, and the caller plans again rather than run the same job twice. Only a job of the fewest inputs it may take that is refused fails, with a `VortexMemoryException` that says what it held. The stores of an append reserve in the same budget what they hold, past it when they must but never past the process's own ceiling by more than a sixteenth, and write their largest partition sooner when it is refused.

A job reserves what it holds at least (`LeastBytes`: its inputs open at the fewest rows a batch takes, and its writer) before it reads anything, in one step (`QueryMemoryBudget.ReserveAsync`). When another work holds the room, another table's job of the same process, the job waits its turn, the oldest first, and takes the room in the step that gives it back, so that none passes it; no thread waits meanwhile. It then runs at its width, where its first block refused gave it up and the next plan narrowed it, the objects it wrote meanwhile smaller for a tier more to rewrite. The grant funds the job's own budget (`HardChild(MemoryGrant)`): its first blocks take the grant's bytes, which the jobs' budget counts already, and ask the budget for the rest; what is left of it goes back when the job ends. A job that holds memory never waits for more: it writes a chunk early, or is refused and narrowed as before. A reservation past the budget's ceiling, or one that nothing under the budget holds the memory to make room for, which no release would wake, is not made, and the job starts as it did.

A job is priced by what the handle's last job measured, not by a model. Each job learns what an input held at most, its inputs' blocks over their count at the rows of their batches, and what its writer held at most, and the next plan sizes a job by them. Before any job, a handle prices an input at its open chunks, 128 KiB at least, and its batch at three times what a row weighs decoded, the first guess, and its writer at sixteen times the dataset's chunk bytes for each column, five times its chunk's target for a variant column it shreds, no more than the rows the job writes. The model it replaces was off by 1.4 to 3.1 times on three jobs of real metric records, whose writer, priced 192 MiB by its columns, held 691 MiB with its shredding.

A job is bounded by three quantities, none of them a model. Its width, the inputs it holds open, by the budget less its writer over what an input held in the handle's last job. Its bytes, by what the session's scratch leaves, its budget's room or nine tenths of its directory's free space, and by what it can commit in time: a quarter of the retention window at the bytes a second the handle's last job read, since a commit refuses objects begun half the window ago. The same bound sizes a piece of a consolidation, an object's size at most. A merge of a size class takes as many inputs as the three hold, the fan-out at least.

A job that reads 16 MiB or more reads its inputs through the local scratch (`JobInputs`): each object is copied once, as the first pass asks for it, to a directory of the job's own, its bytes as the store holds them, sealed when the dataset seals them, and decrypted segment by segment as the job reads them from the disk. The copy is one request to an HTTP store (`IObjectStore.CopyToAsync`), its body written to the file as it arrives, where reads of 4 MiB cost 256 requests an object of a gigabyte. A store on the local disk or in memory (`CostProfile.LocalDisk`, `CostProfile.Memory`) is read in place: a copy would only move the bytes to the disk they lie on, or that the process holds already. A compaction therefore needs a scratch on a disk, the session's `ScratchDirectory` or the default one, never a file system in memory, and refuses to start without one, or without room there for its inputs: under the session's `ScratchBudget`, or a tenth of the directory's free space kept. The directory goes with the job; one a dead process left is removed by the next job once a day old. Below 16 MiB the copy saves nothing: a maintainer's 49 jobs of a few small objects each made 2 443 requests in 35.3 s read from a store answering in 20 ms, and 2 450 in 36.4 to 36.7 s through the scratch.

A merge reads its inputs in batches of what the budget leaves once its writer and its open inputs are counted, by bytes alone, at what a row of a batch held in the handle's last job: a power of two between 1 024 rows and a window, each batch decoded on its own, so that what a merge holds of its inputs does not grow with them. The backlog bench's consolidation of 9 million rows merged 345 000 rows a second from 74 inputs under 2 GiB, at the narrowest batches its budget left, and 263 000 from 300 inputs under 8 GiB, at a block and more. A job's writer gathers its columns in an eighth of the jobs' budget at most, past which a column writes its chunk early: a quarter left the inputs less room, and the backlog compacted under 2 GiB in 17 jobs and 55 s instead of 11 and 49; a sixteenth took 8 jobs and 48 s, its object 2.3 % larger. Which column writes early is decided before a block's columns gather, from what they held then: the one that held the most, when together they held past the ceiling. Read as the columns gathered side by side, the total moved under the lanes, and an object's bytes changed by a few hundred from one run to the next at one degree. Before a job of the handle measured its writer, a writer that shreds variants is priced at five times its gathering ceiling: priced at the ceiling alone, the first job held 322 MiB in its writer over log records and 332 to 368 over metric records under a ceiling of 84 MiB, and was refused, then narrowed, twice; priced at four times, once every column's chunk was emitted by a task of its own, it held 344 to 384 MiB, its gathered chunks in emission 204 to 246 of it, and a consolidation of 74 appends was given up after 8 to 10 s of its 11 four times in ten. A floor of a block of 8 192 rows held 4.7 GB for a merge of 201 inputs of metric records; under a block, the decoders decode a range of a chunk all the same. Each input decodes its next batch on a lane of its scan while the merge takes the runs of the one before, so that the merge's thread chooses runs rather than decodes them: it decoded every input's columns before, their variants rebuilt, a tenth of a consolidation's processor on the chain every lane of the writer waited for. An input then holds two batches, which its measured price counts, and the job reprices the batches of the inputs still to open from what the open ones hold as they open. Past its two, an input the merge reads decodes further windows ahead, from a pool the merge's inputs share, as many as the session's lanes but the merge's own: it takes one once the merge reads it, from its second batch on, and gives it back as the merge passes the window, so that the input the merge drains decodes on several lanes while those it waits on hold their two. The merge takes its inputs in the order of their keys, so the windows start in the order of their first keys whatever input they belong to. A window is taken only while what the jobs' budget leaves the job, less the window, still holds what the writer was priced at, or held at most in the job when that is more, past what it holds now: the writer's room comes first, and a writer whose chunks come and go takes again what it held once. The batches are the inputs' splits either way, so the bytes written do not change. A consolidation's census counts its inputs on as many lanes as the room the jobs' budget leaves holds, each priced at an input and a slice of a wide shredding. An input whose count the room refused beside the others is counted again alone once they are done, rather than give the job up. A piece's ranges run side by side as many as that room holds, each priced at its inputs and its writer, one at least.

A merge puts the rows of one key in the order of the job's inputs, as a key-ordered read does, whatever the batches its inputs are read in. An input that held the smallest key used to run on through the keys equal to another input's, which kept its run longer, but whether it did depended on where its batch ended, and the batches follow what the job measured: the same rows compacted at one lane and at four wrote objects whose rows of one key came in another order. Compacted on copies of one store, an object is now the same bytes at any degree and on any run, segment for segment.

`ConsolidateAsync` consolidates a partition at once when its level 0, all open at once, and one object of level 1 at a time fit the budget at a block of rows each. Otherwise it merges the partition's size classes first, a pass at a time, each pass committed, until they do: the passes of an external sort, each one durable and visible to readers. A partition with no merge due is consolidated anyway.

A variant shredded into many columns is read a window and a slice at a time ([shredded variants](21-variant-columns.md#5-the-worst-cases)), and written a typed column at a time ([the write](11-write-strategy.md#10-shredded-variants)). What memory cannot hold goes to disk: the runs of an object's hashed key index and of a partition's postings past their scratch's memory. `CompactionResult.RangesAtOnce`, `MemoryReserved` and `ScratchBytes` say how many ranges a piece ran at once, 1 for any other job, what it reserved and what it spilled, and `vortex-queries --print-jobs` prints them beside the process's memory over each pass.

### Late rows under levels

Under levels, a row that comes after rows of later keys reaches level 0 with its append, and a job that
merges it into the level below rewrites what its key meets there: a row a few seconds late made almost
every exit of level 0 read the last object of level 1, and write it whole again. So an exit keeps such
rows apart. The rows at or before the last key of the objects it meets in its destination lead its
merge in key order; it writes them back to level 0, reads its sources alone, and those objects stand.
Level 0 holds overlapping objects anyway, which every question reads by their summaries. The rows stay
apart while they weigh an eighth of the rows of the objects they meet at most, and while level 0 holds
fewer such objects than half its ceiling: an exit then takes the objects that came in order alone, once
they pass the ceiling, and may leave late objects up to that half. An object is late when every key it
holds lies at or before the greatest key of the levels above 0, which the last object of each level
gives. Past either bound, an exit takes all of level 0 and merges it with what it meets, as before.
Level 0 keeps within its ceiling throughout. On 9 600 rows appended 100 at a time, 2 % of them a little
late, the exits read 7 785 rows of the level below and rewrote 17 100 rows, 1.78 a row, against 13 000,
1.35 a row, when they all come in order (`DatasetLateRowsTests`).

### Rollups

Once no job is due, the background loop sums the counts of the deepest level's objects into rollups
(`RollUpAsync`): runs of about `DatasetOptions.RollupFanout` consecutive objects, 64 by default, cut
where the partition changes, a rollup of rank 1 each, then runs of as many rollups a rank up,
each summed from what its children keep, with no row read. The tree of rollups lies beside the levels,
and the header carries its root (field 26). A commit that puts or takes away an object takes away
every rollup whose range holds a key it changes, which a later call sums again.

A question takes a whole rollup, from the highest rank down, when the union of its objects' summaries
proves the filter and the constant keys as one object's would, and its totals carry what the question
reads. Otherwise it may take the rollup bucket by bucket, each bucket's summaries the rollup's with the
key's first column bounded by the bucket's values, when that costs less than the children's counts, a
request weighed as a mebibyte read. The walk then skips the subtrees a rollup it took covers whole. A
rollup's counts and sketches give each chunk its first entry and its XXH3, and end their header with
its own, so that a question on a few values reads the header, then the chunks of those values. Over
thirty days of 2 000 routes, an object a day, a count of every route reads two rollups and the counts
of the three objects no rollup covers yet, 7 requests and 324 254 bytes, where the objects' counts take
a request each (`RollupBudgetTests`).

### Resuming a long job

A job that consolidated a day of the design's rate into one object would read and write some 170 GB;
its objects of 1 GiB each make that some 160 objects, each a job's part, and a failure
starts it again from nothing: its object is one upload, whose writer holds until the end what the end
writes, the chunk directories, the zone maps, the postings' pages, the census of the paths. Three ways
to lose less were weighed, none built:

| | how | what it takes | what it leaves |
|---|---|---|---|
| a checkpointed upload | at the end of a part, the writer saves its state beside the upload's parts, and a job that fails takes the upload back from there | every builder of the writer able to save and restore itself: its open chunks, its pages in the scratch, its counts, the granule being written | gigabytes of state saved at each checkpoint, and a writer twice as hard to change |
| the object built from parts | a partition's rows written as several objects first, each committed alone, then the partition's object assembled from theirs | parts that keep the shredding they chose; the object's tables merged from the parts' | the partition rewritten whole a second time, unless it is assembled by copying the parts' segments, a format change |
| a smaller object | the size the layout's objects aim at (`DatasetLayout.ObjectBytes`) cuts a partition's rows into objects of that size | nothing new | more objects over a window, a request each |

A fourth way is built: a consolidation that reads more than the layout's object size runs a piece at a
time (`DatasetCompactor.Pieces`). Each job takes the keys of the partition below a cut, the first key of
a granule of its inputs' sparse indexes past eight objects' size of what is left (`DatasetOptions.PieceObjects`,
internal), moved up so that no object of level 1 lies across it; it writes them to level 1 and commits.
The size is fixed: sized by the session's lanes, a piece wrote other objects at another degree. The inputs of level 0 it took rows of go
back to level 0 in the same commit with those rows marked, as a delete marks them, a run from row 0 since
an input's rows are in the key's order; one it took every row of goes. The next job's plan finds what is
left and consolidates the next piece, each input read from past its marked run, the objects of level 1 the
earlier pieces wrote left out. A failure loses one piece, and each piece commits within its own share of
the window. A consolidation runs in one piece when an input keeps no sparse index of its keys, or carries
the counts of rows a delete marked, which a piece's marks would leave behind. Each piece writes the
partition's index again, its objects' postings with those the index before covers still: a partition of
`P` pieces writes its index `P` times, a cost that grows as `P²` in the index's bytes, measured before it
is changed.

The third is there today: a dataset whose failed jobs cost too much declares a smaller size, and each
job at risk is that much smaller. The second is the shape of a job that resumes, at the price of a
format change, and waits for a measure of how often jobs fail. A delete that marks an input while the
job runs is the common reason a job starts again: its commit abandons the job, whose outputs no version
names and which the job deletes at once. Carrying the delete's marks over to the outputs would answer it
without either, and is not done.

### Cleaning up after itself

A job deletes what it replaced, after its commit and never before. The handle notes the objects the
commit took out, with the version that took them out and when it knew that version to be the latest. A
version stays retained while the commit that superseded it is younger than the retention window, or
while it is among the versions the dataset keeps whatever their age, as the vacuum judges it: an object
taken out by version `R` is read by version `R - 1` last, and goes once that version is no longer
retained. The handle deletes what is due at the end of each job, and when a step finds nothing to
compact, in batches of a thousand keys, without listing the store or walking a tree. Its clock reads the
time a little after the store dated the commit, so it deletes a little later than a vacuum would, never
sooner. A job whose commit applied nothing deletes its own outputs at once.

The notes live in the handle. What a handle closed or a process ended leaves noted, the objects of a job
that crashed before its commit, and every commit object, stay the vacuum's: a commit object is shared by
the trees of the versions after it, and only a walk of the retained versions tells which ones nothing
reads. A store under a retention lock deletes nothing before its lock lets go, which the vacuum waits
for: there, the handle notes nothing.

## 4. Folding marks where no merge goes

A purge is a job with a single input: an object rewritten alone, without its marked rows, in its own
level. Its keys stay inside the old range, as with a rewrite by a delete (see [deleting
rows](13-dataset.md#12-deleting-and-updating-rows)). It is the trigger the top level otherwise lacks.
That level is never compacted into itself, because its objects are key-disjoint, so its only dead
rows are those a delete marks.

An object is purged when its marks pass a threshold below the delete's own: half the share, or half
the vector cap. The object is then rewritten by maintenance, rather than by the delete of an
application that asked to remove ten rows. Purges are ranked by the bytes they free per byte they
write, which for one object is its marked share, and the descent of the
[planning](#3-planning-in-the-depth-of-the-tree) section finds it.

The reason is the leaf pages. A vector lives in its object's leaf entry, and every commit that marks
a row in any object of a page writes that page again, so a page's bytes grow with the vectors it
holds. The churn bench measures it on ten million rows. Each update or delete takes ten keys in a row,
one run in one of the 31 objects of level 4, and those objects' entries share one leaf page. A commit
writes 23 KiB while the vectors are empty. With vectors capped at 1 KiB, the default, the fullest
objects start being rewritten after 15 000 commits, and a commit writes 63 to 101 KiB from then on. At
a 4 KiB cap no object has been rewritten after 30 000 commits, and a commit writes 116 KiB and is still
growing. A purge that keeps the vectors under their cap keeps that page, and every header, smaller
than the cap would let them grow.

`CompactionTrigger.Marks` implements it, at half a delete's limits, ranked by how due an object is:
its marked share or its vector size, relative to half its limit, whichever is further along. The
vector's limit grows with its object, a thousandth of its bytes and never under 1 KiB. Under an
erasure delay (`DatasetOptions.ErasureDelay`), the age of the object's oldest mark relative to half
the delay takes the place of the vector's size, and a purge it makes due is
`CompactionTrigger.Erasure`: the delay is a bound the application owes, the vector's bytes no
longer trigger anything there, and the tallies then lead the plan to every object with marks. The
tallies carry the largest share and the largest vector under every page, so the plan only walks to
objects that may be due. On the churn bench the purges move the rewrites more than they remove them.
Once the vectors have filled, a commit writes about 56 KiB rather than 77, compaction about 83 KiB
rather than 65, and the two together the same 140, with the same reads and the same store size. What
the purges buy is where a rewrite happens: in compaction rather than inside a user's delete, which a
background driver takes off the write path altogether, plus the fold the top level otherwise lacks.

### Keeping marked pages small

Three measures keep the cost of marked pages down.

A commit writes the pages it changes once, in its pages region, with no second copy in the header.
The region follows the header, so a page that ends inside the read that opens the commit needs no
copy in the header: the reader keeps the part of the region that read brings back. Inlining those
pages in the header as well would make a commit object 39 % larger on the churn bench (26.6 KiB on
average over the first 15 000 commits, against 16.2) for nothing a reader needs. What a header carries
is the pages its own commit wrote that end past that read.

A header carries no page another commit wrote, marked or not, and says instead where the version that wrote it keeps its pages, so a page only costs the commits that change it. A reader that opens the version reads that page in one ranged read when it walks the level. A handle keeps, from one version to the next, the pages it read and the regions its last versions' open reads held (see [objects and keys](13-dataset.md#3-objects-and-keys)), so neither its commits nor its refreshes read the page again until a commit changes it. Leaving out the leaves with marks alone made a commit object 30 % smaller on the churn bench (11.3 KiB on average over the first 15 000 commits, against 16.2), with the same reads. Leaving out every page another commit wrote took 43 % off an append on a dataset of 40 consolidated partitions (122 KB against 212), for one request more to a cold question whose path the last commit did not write.

A vector past 256 bytes is stored out of line. The commit that writes the entry puts the vector's
bytes into its own commit object, as an index fragment is (see
[fragments](13-dataset.md#64-fragments-in-commit-objects-transient)), and the entry carries a
reference in their place. Its inline length of zero, which no vector in an entry can have, tells a
reader that only knows vectors in line to refuse the entry. The entry keeps its count of marked rows,
so a plan, a tally and a count without a filter still need no read. A read of the object fetches the
vector alongside the object's open, through the version's pages: the region the commit's opening read
holds, the handle's cache, where the writer leaves what it wrote, or one ranged read checked against
the reference's hash. Vacuum keeps the commit object a vector lies in, and verification reads every
vector. This is how Delta Lake stores a deletion vector past its inline size, and how Iceberg stores
every one, in a Puffin file. The price is one more ranged read per marked object a scan opens, in
parallel with the object's own open rather than dependent on it, and kept with the open object. On the
churn bench a commit object stays flat once the vectors have grown, at 7.5 to 10 KiB per commit where
vectors in line grow it to 15, with the same requests: the leaf page an object's marks rewrite carries
a 37-byte reference for each, where a vector in line takes up to a kilobyte.

## 5. Stores that cannot delete

A store under a retention lock accepts a new object, and refuses, until the object's retention date,
any overwrite and any delete that would destroy its bytes:

- S3 Object Lock: governance mode, which a privileged caller may bypass, compliance mode, which nobody
  may, and legal hold.
- Azure immutable blob storage: time-based retention and legal hold.
- GCS: a bucket's retention policy, and its lock.

On S3 the lock requires versioning. A delete without a version id then succeeds by adding a delete
marker: the key leaves every listing, and the bytes stay until their date passes and a lifecycle rule
expires the old version.

Much holds as it is. The dataset never overwrites anything: every object it writes is created by
put-if-absent under a fresh key, and read as it was created. The commit protocol, the rebase, every
read, verification and time travel within the retention window are unchanged, and are what such a
store is for. A lock is just the store keeping its promise for longer.

What changes:

1. Vacuum frees nothing before the date. On a versioned bucket its deletes become delete markers. The
   keys leave the listing, which is all vacuum needs, but the bytes are billed until the lock expires.
   On a store that refuses the delete, vacuum must know the date, so `HeadAsync` reports the
   retention date and the legal hold, and vacuum only deletes what is past both its own window and the
   lock, reporting the rest as locked until a date.
2. Every byte written is kept for the lock's term, so write amplification becomes storage
   amplification multiplied by a term of years. A copy-on-write of 4 MiB to delete ten rows (3.3 MiB
   per commit on the churn bench) is 32 GiB a day at ten thousand commits, and a seven-year lock keeps
   82 TiB of it. The same changes made by marks write 23 to 101 KiB per commit, mostly leaf pages,
   which is a few hundred megabytes to a gigabyte a day.
3. A rewrite frees nothing and only adds. A compaction's inputs stay as long as its outputs, so a
   merge bought for the read bounds is paid twice over the lock's term, and a purge frees no byte and
   only makes reads cheaper.
4. Commits are data too. Each commit object stays for the term. Ten thousand commits a day of leaf
   pages is the gigabyte above, so the group commit a writer can already make, one commit of many
   appends, pays off in bytes.
5. A put may have to carry a checksum, and must still be conditional. S3 refuses a put into a bucket
   under a lock without a Content-MD5 or a checksum header, which a streamed put of known length sends
   as a trailing checksum. The store library also has to confirm that its put-if-absent still holds
   on the versioned bucket a lock requires. Both are that library's work rather than this one's (see
   [the store abstraction](13-dataset.md#11-the-store-abstraction)), but they belong in the store's
   contract tests.

One option switches the whole profile for a locked store:

- deletes always by marks, with no rewrite below a size, and a vector cap raised to the page's
  bound, since a rewrite would add bytes and free none
- purges off
- compaction held to the read bounds only: level 0's ceiling, and key-disjoint levels at a larger
  fan-out, or tiers without a sort key, since each merge is paid for the whole term
- vacuum aware of the retention date

`ObjectHead` carries `RetainUntil` and `LegalHold`, which a store under a lock reports, and vacuum
only deletes what is past both its window and the lock. The rest is `Locked`, with `NextUnlock` the
date the first of it may go. `DatasetOptions.LockedStore`, fixed at creation and carried by every
header, is the profile. Every handle on the dataset then marks whatever an object's size or the share
of its rows, with vectors up to the page's bound, plans purge nothing, the levels merge at a fan-out
of 100 unless the dataset states one, and vacuum asks for the head of each object old enough to go
when the store's listing does not say its lock, as S3's does not. `MemoryObjectStore.RetainFor` and `Hold` simulate a
locked store, refusing a whole batch of deletes when one key in it is locked, as such a store does.
The checksum a locked bucket asks of every put, and the conditional put on a versioned bucket, remain
the store library's to prove in its contract tests.

The right to erasure deserves a word. A mark removes a row from every read at once, but its bytes
stay in the object, and in every object a rewrite replaced, until the lock expires: a store in
compliance mode refuses to destroy them before then, by design. Physical erasure under such a lock is
impossible for any layout, and so is not part of this design. The known answer is crypto-shredding,
an encryption key per data subject destroyed in place of the rows, which belongs to the store library
or to the application.

## 6. What the library implements

1. Planning from the top of the tree ([planning](#3-planning-in-the-depth-of-the-tree)): a tally in
   every page's summary, one descent to the largest object or to the one past a level's pointer, one
   per level to where a tiered run ends, and walks that skip whatever the bounds and tallies rule out.
2. Purge jobs ([folding marks](#4-folding-marks-where-no-merge-goes)): a `Marks` trigger after the
   levels' bounds and before the fragments, at half a delete's limits. The churn bench measures a
   commit's bytes with and without it: the purges move a fifth of them into compaction and leave the
   total the same.
3. The background driver ([three drivers](#2-three-drivers-for-one-job)): `RunCompactionAsync`, with
   a byte budget and one job at a time, and loops that know one another spread by the rank of
   independent jobs.
4. Inline level 0 compaction ([three drivers](#2-three-drivers-for-one-job)):
   `DatasetOptions.InlineCompactionBytes`, off by default, bounded by its byte budget.
5. The locked-store profile ([stores that cannot delete](#5-stores-that-cannot-delete)), and the
   retention date on the store interface: `DatasetOptions.LockedStore`, and `ObjectHead.RetainUntil`
   and `LegalHold`.
6. Marked pages left out of the header, and vectors out of line ([keeping marked pages
   small](#keeping-marked-pages-small)): over a handle that keeps its pages across versions, a header
   leaves out a marked leaf its commit did not write, and a vector past 256 bytes lies in the commit
   object that wrote it.
7. Tiers and consolidation ([tiers and consolidation](#tiers-and-consolidation)): a partition's size
   classes merged `k` at a time, the consolidation of a calm partition in key ranges side by side, the
   late rows held to a share, the census of the variants' paths by partition, and the report of each
   partition.
8. Rollups ([rollups](#rollups)): `RollUpAsync`, called by the background loop once no job is due,
   and a planner that takes whole rollups, or their buckets, where their objects' counts would answer.

Each one is measured the way the churn bench measures the dataset, from the first commit to the
ten-thousandth (see [how the dataset is tested](13-dataset.md#15-how-it-is-tested)), and keeps every
cost flat.
