# Compaction: when it runs, and what a store that cannot delete does to it

Compaction is the [levels and compaction](13-dataset.md#5-levels-and-compaction) section of the
dataset design, plus what this document describes:

- one planner, whose jobs run one per `CompactAsync` call, one per turn of a background loop, or in
  the commit that takes level 0 past its ceiling ([three drivers](#2-three-drivers-for-one-job))
- a plan that descends the trees rather than reading them
  ([planning](#3-planning-in-the-depth-of-the-tree))
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
hide](13-dataset.md#1-where-the-ns-hide-and-the-bound-that-replaces-each)):

- Level 0 holds at most eight objects, so a lookup touches at most eight objects there, rather than
  one per append since the last merge.
- The levels above 0 are key-disjoint, so a lookup touches one object in each of them.
- The marks a delete leaves are folded, so their rows are no longer stepped over and their bytes are
  freed (see [deleting rows](13-dataset.md#12-deleting-and-updating-rows)).

It pays for these in bytes written. A row appended in a small commit moves up through every level,
and each merge rewrites the part of the next level it overlaps. That is about `F / 2` rewrites per
level at a fan-out `F`, about 5 at the default of 10. A load of gigabytes goes straight to the level
that holds it and is written once (see [the invariant](13-dataset.md#52-the-invariant)). The levels
grow by `F`. With level 1 at 1.25 MiB, a tebibyte of data needs seven levels, so a byte that came in
with a small commit is written about 35 times before it settles.

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
changed (see [rebase](13-dataset.md#82-rebase-by-re-applying-operations)). Correctness never depends
on who runs the job, so every driver below is safe against every other one and against every writer.
What they differ in is who waits for the work, and how much of it is wasted.

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
service, a worker, a console. It asks for the latest version, which costs one head request for a
handle that knows its own is recent (see [the latest
version](13-dataset.md#83-the-latest-version-in-one-request)), plans, runs the job, and sleeps when
nothing is due. Two limits shape it: a budget of bytes written per second, so compaction never takes
the store's bandwidth away from the writers, and one job at a time per loop. A hosting integration,
such as `IHostedService`, belongs in a separate package, since the dataset package takes no
dependency.

Two loops on one dataset are safe but wasteful: both plan the same job, and one commit loses. The
waste can be avoided without any coordinating object, in one of two ways:

- Deterministic job choice. The planner ranks the due jobs, and a loop that knows it is one of `n`
  takes the job whose rank is its index modulo `n`, which spreads `n` loops over `n` jobs.
- Leases. Before a job, a loop creates `leases/<level>/<time bucket>` with put-if-absent. A bucket is,
  say, a minute, and the lease is held for as long as its bucket is current. Nothing releases a
  lease, since the next bucket replaces it. That matters under a retention lock ([stores that cannot
  delete](#5-stores-that-cannot-delete)), which would refuse a lease's delete.

The first costs nothing but needs the number of loops. The second costs one request per level a job
touches, and needs nothing else.

`RunCompactionAsync` takes a `CompactionSchedule`: the options it plans against, `BytesPerSecond`,
the `Idle` sleep, and `Loops` and `Loop` for loops that know one another. The planner ranks the due
jobs so that no two read or write the same level (a job on levels that share nothing never takes
another's input), and a loop takes the one ranked at its index. A descent and a read of every leaf
rank the same jobs. `UseLeases` turns on leases instead, one per level a job touches,
`leases/<level>/<end of the span>`, tried job by job in rank order. The key dates the lease, so vacuum
deletes the ones that ended a window ago without a head request each. A job still running when its
lease ends may collide with another loop's, which one commit then abandons, so a `LeaseSpan` of a
minute outlasts most jobs.

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

A tiered job concatenates a run of one level's objects with nothing else between them, and where the
runs are depends on every level's order. The longest run can only be found by reading every leaf. The
run that starts past the pointer needs much less: it ends before the first object another level holds
past its start, and one descent per level finds that object, rather than one per level for each object
of the run. The descents run side by side, and a walk of the source level between the two reads the
run itself. A plan over 100 000 objects by arrival, with levels interleaved object by object, asks the
store for one page, where reading every leaf would ask for 71. A dataset ordered by arrival keeps each
level's objects together, a run of level 0 moving up whole and the next landing past it, so the two
picks take the same runs there, and the round robin is a tiered dataset's default.

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
is pages other commits wrote, every level's that fits, except leaves with marks.

A header leaves out a leaf whose entries carry marks when its own commit did not write it, and says
instead where the version that wrote it keeps its pages, so a mark only costs the commits that change
its own level. A reader that opens the version reads that page in one ranged read when it walks the
level. A handle keeps, from one version to the next, the pages it read and the regions its last
versions' open reads held (see [objects and keys](13-dataset.md#3-objects-and-keys)), so neither its
commits nor its refreshes read the page again until a commit changes it. On the churn bench this makes
a commit object 30 % smaller (11.3 KiB on average over the first 15 000 commits, against 16.2), with
the same reads.

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
5. A lease cannot be released ([three drivers](#2-three-drivers-for-one-job)). Its key has to expire
   by its name, which the time bucket does, and each one is a hundred bytes kept for the term.
6. A put may have to carry a checksum, and must still be conditional. S3 refuses a put into a bucket
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
  fan-out, or tiered compaction without a clustering key, since each merge is paid for the whole term
- vacuum aware of the retention date

`ObjectHead` carries `RetainUntil` and `LegalHold`, which a store under a lock reports, and vacuum
only deletes what is past both its window and the lock. The rest is `Locked`, with `NextUnlock` the
date the first of it may go. `DatasetOptions.LockedStore`, fixed at creation and carried by every
header, is the profile. Every handle on the dataset then marks whatever an object's size or the share
of its rows, with vectors up to the page's bound, plans purge nothing, the levels merge at a fan-out
of 100 unless the dataset states one, and vacuum asks for the head of each lease it would delete,
since only there does it learn a lease's lock. `MemoryObjectStore.RetainFor` and `Hold` simulate a
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
   a byte budget and one job at a time, loops that know one another spread by the rank of independent
   jobs, and leases for those that cannot count one another.
4. Inline level 0 compaction ([three drivers](#2-three-drivers-for-one-job)):
   `DatasetOptions.InlineCompactionBytes`, off by default, bounded by its byte budget.
5. The locked-store profile ([stores that cannot delete](#5-stores-that-cannot-delete)), and the
   retention date on the store interface: `DatasetOptions.LockedStore`, and `ObjectHead.RetainUntil`
   and `LegalHold`.
6. Marked pages left out of the header, and vectors out of line ([keeping marked pages
   small](#keeping-marked-pages-small)): over a handle that keeps its pages across versions, a header
   leaves out a marked leaf its commit did not write, and a vector past 256 bytes lies in the commit
   object that wrote it.

Each one is measured the way the churn bench measures the dataset, from the first commit to the
ten-thousandth (see [how the dataset is tested](13-dataset.md#15-how-it-is-tested)), and keeps every
cost flat.
