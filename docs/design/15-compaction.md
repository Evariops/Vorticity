# Compaction: when it runs, and what a store that cannot delete does to it

**Status: a proposal, whose first step is built.** What exists is [13-dataset.md](13-dataset.md) §5:
one planner, one job per `CompactAsync` call, run when the application calls it, and since the first
step (§3) a leveled plan that descends the trees rather than reading them. This document weighs the
other ways to drive the same work: inline in a writer's commit, on demand, or in the background. It
also weighs what a store under a retention lock changes, when it refuses to delete or overwrite an
object before a date: S3 Object Lock, Azure immutable blob storage, a GCS bucket lock. Nothing here
changes the commit protocol or a read; the tallies of §3 are the one change to a page's format, and a
page written before them still reads.

## 1. What compaction buys, and what it costs

Compaction holds the read bounds of [13-dataset.md](13-dataset.md) §1:

- **Level 0 holds at most eight objects.** A lookup touches at most eight objects there, rather than
  one per append since the last merge.
- **The levels above 0 are key-disjoint.** A lookup touches one object in each of them.
- **The marks a delete leaves are folded.** Their rows are no longer stepped over, and their bytes are
  freed (§12 of the same document).

It pays for these in bytes written. A row appended in a small commit moves up through every level,
and each merge rewrites the part of the next level it overlaps. That is about `F / 2` rewrites per
level at a fan-out `F`: about 5 at the default of 10. A load of gigabytes goes straight to the level
that holds it and is written once (§5.2). The levels grow by `F`. With level 1 at 1.25 MiB, a
tebibyte of data needs seven levels, so a byte that came in with a small commit is written about 35
times before it settles.

Three costs are left over, and this document is about them:

1. **Who pays, and when.** Today it is whoever calls `CompactAsync`, when they do. Until then a
   dataset reports its lag, and lookups touch the extra objects.
2. **Planning read every leaf.** `PlanCompactionAsync` walked every level's tree, since the bytes of
   a level were recorded nowhere else. At the 40 objects of the churn that is nothing. At a
   tebibyte of 4 MiB objects it is 262 144 entries of a few hundred bytes each: about a thousand
   pages and a hundred megabytes, on every plan. A leveled plan now descends instead (§3).
3. **Marks in levels no merge reaches.** Level 4 of a ten-million-row dataset is rewritten only when
   level 3 overflows into it, and the top level never is. The marks there last until a delete would
   pass an object's share or its vector's cap, and that delete then rewrites the object itself.
   Meanwhile each commit that marks a row writes again the leaf page holding the object's entry, and
   that page grows with the vectors it holds (§4).

## 2. Three drivers for one job

A **job** stays what it is: one plan's inputs read in key order, the outputs written, and one
`ReplaceObjects` committed. That commit names the entries it read, and is abandoned when one of
them changed (13-dataset.md §8.2). Correctness never depends on who runs the job, so every driver
below is safe against every other one and against every writer. What they differ in is who waits
for the work, and how much of it is wasted.

| driver | who pays | the bounds | what goes wrong |
|---|---|---|---|
| inline, in the commit | the writer whose commit crosses a bound | held after every commit | the writer's latency jumps by a merge; a writer does work for everyone; a crash leaves the outputs for vacuum |
| on demand, `CompactAsync` | the application's scheduler | held when it runs; `Lag` says by how much they are not | a scheduler that stops leaves lookups touching every object since |
| in the background | a loop in some process | held within the loop's reaction time | two loops on one dataset race for the same inputs, and the loser's outputs are garbage |

**Inline.** A commit that takes level 0 past its ceiling runs one job before it returns, so that no
version the writer publishes leaves level 0 over its bound for long. The price is the merge's own
latency, added to that one commit: level 0 into level 1 costs 0.3 to 1 ms a commit in the churn,
spread over the commits between merges. A merge into a deep level is seconds. So inline mode is
bounded to level 0, and to inputs under a byte budget: past it, the job is left to another driver
and the writer returns at once. Inline mode suits a dataset with one writer and no process to spare.
The writer is the one party that knows level 0 just grew.

**On demand.** The model today. `PlanCompactionAsync` says what is due and what it costs, and
`CompactAsync` runs one job. The application loops, from a scheduled task or after a batch of
commits. It is the right default for a library, because the library never starts a thread of its own.
It stays the primitive the other two drivers are made of.

**In the background.** A long-running call, `RunCompactionAsync(schedule, cancellationToken)`, that
any host runs: a hosted service, a worker, a console. It asks for the latest version, which is one
listing (13-dataset.md §8.3). It plans, runs the job, and sleeps when nothing is due. Two limits
shape it: a budget of bytes written per second, so that compaction never takes the store's
bandwidth from the writers, and one job at a time per dataset. A hosting integration, such as
`IHostedService`, belongs in a separate package, since the dataset package takes no dependency.

**Two loops on one dataset** are safe and wasteful: both plan the same job, and one commit loses.
The waste is avoided without any object that coordinates:

- **Deterministic job choice.** The planner ranks the due jobs, and a loop that knows it is one of
  `n` takes the job whose rank is its index modulo `n`, which spreads `n` loops over `n` jobs.
- **Or a lease.** Before a job, a loop creates `leases/<level>/<time bucket>` by put-if-absent. A
  bucket is a minute, say, and the lease is held for as long as its bucket is current. Nothing
  releases a lease: the next bucket replaces it. That matters under a retention lock (§5), which
  would refuse a lease's delete.

The first costs nothing and needs the number of loops. The second costs one request a job and needs
nothing.

## 3. Planning in the depth of the tree

**Built, for the leveled style.** Each thing the planner needs now comes from where a commit
already writes:

| what the plan needs | read before | read now |
|---|---|---|
| a level's bytes, against its capacity | the sum of every leaf's bytes | the tallies on its top page, which the header carries: every page's summary carries the tally of what lies under it, folded by a commit as it folds the bounds |
| the objects a level-0 job takes | level 0's leaves | the same, since level 0 holds at most eight |
| which object of a full level goes down | every leaf of the level, then the largest | one descent along the largest tally, which finds the same object |
| the objects of the next level a job meets | every leaf of that level | a walk that skips each subtree whose bounds lie outside the job's range |
| the objects over their fragments | every leaf | a walk into the subtrees whose tally holds one |
| which objects hold the most marks | nothing | the sum of marked rows in every tally, for the purge of §4 |

A plan now reads the header, one path per full level and the job's own objects. A cold plan over
125 000 objects asks the store for two pages, where reading every leaf asks for 124. The tallies
follow the rule the rows summed up the tree already follow: each commit rewrites the path above an
entry it changes, so a tally costs no page the commit does not already write. A page written before
tallies carries none, and a level holding one is planned by its leaves until a commit rewrites it.

**Not built.** First, a compaction pointer per level: the key the last job of a level stopped at, as
LevelDB keeps one. It would spread the rewrites evenly over the keys, where the largest object lies
wherever it happens to be. Second, a tiered plan by descent. A tiered job concatenates the longest
run of one level's objects that nothing else sits between, and only the order of every level says
where the runs are. A policy that took the run at a pointer instead would need one descent per level
for each object of the run.

## 4. Folding marks where no merge goes

A **purge** is a job with one input: an object rewritten alone, without its marked rows, in its own
level. Its keys lie inside the old range, as a rewrite by a delete does (13-dataset.md §12). It is the
missing trigger of the top level. That level is never compacted into itself, because its objects are
key-disjoint and, until marks, held no dead row. Now they do.

**When.** An object is purged when its marks pass a threshold below the delete's own: half the
share, or half the vector cap. The object is then rewritten by maintenance, not by the delete of an
application that asked to remove ten rows. Purges are ranked by the bytes they free per byte they
write. For one object that is its marked share, and the descent of §3 finds it.

**What it saves.** A vector lives in its object's leaf entry. Every commit that marks a row in any
object of a page writes that page again. While a level's pages fit the header's inline room, 192 KiB,
every commit carries them too, marks or not. So a page's bytes grow with the vectors it holds, and so
does every commit's.

The churn measures it on ten million rows. Each update or delete takes ten keys in a row, one run in
one of the 31 objects of level 4, and those objects' entries share one leaf page. A commit writes
23 KiB while the vectors are empty. With vectors capped at 1 KiB, the default, the fullest objects
start being rewritten after 15 000 commits, and a commit writes 63 to 101 KiB from there on. At a
4 KiB cap no object has been rewritten after 30 000 commits, and a commit writes 116 KiB and still
growing. A purge that keeps the vectors under their cap keeps that page, and every header, smaller
than the cap would let them grow.

**Built** (`CompactionTrigger.Marks`), at half a delete's bounds, and ranked by how due an object is:
its marked share or its vector, over half its bound, whichever is further. The tallies carry the
largest share and the largest vector under every page, so the plan walks only to objects that may be
due. On the churn the purges move the rewrites more than they remove them. Once the vectors have
filled, a commit writes about 56 KiB rather than 77, compaction about 83 KiB rather than 65, and the
two together the same 140; the reads and the store are the same. What they buy is where a rewrite
happens: in compaction rather than inside a user's delete, which a background driver (§2) takes off
the write path altogether, and the fold the top level lacked. A commit's bytes are still mostly the
leaf page the header carries, which only the two options below remove.

**A second copy, removed.** Every commit wrote the pages it changed twice: in its pages region, and
inlined in its header so that the read opening the commit would hold them. The region follows the
header, so a page that ends inside that read needs no copy: the reader keeps the part of the region
the read brought back. On the churn this took 39 % off a commit object, 26.6 KiB to 16.2 on average
over the first 15 000 commits, and costs a reader nothing. What a header still carries is pages
other commits wrote: every level's that fits, the leaf page with the vectors among them.

**Or out of the header.** The header could leave out a leaf page whose entries carry marks when this
commit did not write it. A mark would then cost only the commits that change its own level. A reader
that opens the version would read that page when it walks the level: one ranged read more, cached with
the version. A handle opens a fresh page source on each version, so it would pay that read after each
commit it does not write itself, unless it kept pages across versions: a page is immutable, and its
reference names it in every version that shares it.

**Out of line, the alternative.** A vector could be written once into the commit object that made
it, as an index fragment is (13-dataset.md §6.4), and the entry would name it by reference. Pages
would then stay small whatever the marks, and a commit would write only the vectors it changes.
The price is one more ranged read for each marked object a scan opens: in parallel with the object's
own open, not dependent on it, and kept with the open object. Vacuum would also have to keep every
commit object whose vector a retained version names. It is how Delta Lake stores a deletion vector
past its inline size, and how Iceberg stores every one, in a Puffin file. It is worth it once a delete
scatters single rows over many objects, each commit then changing many vectors. A delete on the
clustering key writes a run of a few bytes, which an inline vector already handles well.

## 5. Stores that cannot delete

A store under a **retention lock** accepts a new object and refuses, until the object's retention
date, any overwrite and any delete that would destroy its bytes:

- S3 Object Lock: governance mode, which a privileged caller may bypass; compliance mode, which
  nobody may; and legal hold.
- Azure immutable blob storage: time-based retention and legal hold.
- GCS: a bucket's retention policy, and its lock.

On S3 the lock requires versioning. A delete without a version id then succeeds by adding a delete
marker: the key leaves every listing, and the bytes stay until their date passes and a lifecycle
rule expires the old version.

**What holds as it is.** The dataset never overwrites anything. Every object it writes is created by
put-if-absent under a fresh key, and read as it was created. The commit protocol, the rebase, every
read, verify and time travel within the retention window are unchanged, and are what such a store
is for. A lock is the store keeping its promise longer.

**What changes.**

1. **Vacuum frees nothing before the date.** On a versioned bucket its deletes become delete
   markers. The keys leave the listing, which is all vacuum needs, and the bytes are billed until the
   lock expires. On a store that refuses the delete, vacuum must know the date, which the seam does
   not carry. `HeadAsync` gains the retention date and the legal hold, and vacuum deletes only what is
   past both its own window and the lock, reporting the rest as locked until a date.
2. **Every byte written is kept for the lock's term.** Write amplification becomes storage
   amplification, multiplied by a term of years. A copy on write of 4 MiB to delete ten rows, 3.3 MiB a
   commit in the churn, is 32 GiB a day at ten thousand commits. A seven-year lock keeps 82 TiB of it.
   The same changes made by marks write 23 to 101 KiB a commit, mostly leaf pages: a few hundred
   megabytes to a gigabyte a day.
3. **A rewrite frees nothing, only adds.** A compaction's inputs stay as long as its outputs. A merge
   bought for the read bounds is bought twice over the lock's term, and a purge (§4) frees no byte,
   it only makes reads cheaper.
4. **Commits are data too.** Each commit object stays for the term. Ten thousand commits a day of
   leaf pages is the gigabyte above, so the group commit a writer can already make, one commit of
   many appends, pays off in bytes.
5. **A lease cannot be released** (§2). Its key must expire by its name, which the time bucket does,
   and each one is a hundred bytes kept for the term.
6. **A put may have to carry a checksum, and must still be conditional.** S3 refuses a put into a
   bucket under a lock without a Content-MD5 or a checksum header. A streamed put of known length
   sends it as a trailing checksum. And the store library has to confirm that its put-if-absent
   still holds on the versioned bucket a lock requires. Both are that library's work and not this
   one's (13-dataset.md §11), but they belong in the store's contract tests.

**A profile for a locked store.** One option switches these at once:

- deletes by marks always, with no rewrite below a size, and a vector cap raised to the page's
  bound, since a rewrite would add bytes and free none;
- purges off;
- compaction held to the read bounds only: level 0's ceiling, and key-disjoint levels at a larger
  fan-out, or tiered compaction without a clustering key, since each merge is paid for the term;
- vacuum aware of the retention date.

**The right to erasure.** A mark takes a row out of every read at once. Its bytes stay in the object,
and in every object a rewrite replaced, until the lock expires: a store in compliance mode refuses
to destroy them before, by design. Physical erasure under such a lock is impossible for any layout,
and so is not in this design. The known answer is crypto-shredding, an encryption key per data
subject destroyed in place of the rows. It belongs to the store library or to the application.

## 6. What to build, in order

1. **Planning in the depth of the tree** (§3). Built for the leveled style: a tally in every page's
   summary, one descent to the largest object, and walks that skip what the bounds and tallies rule
   out. A pointer per level and a tiered plan by descent are left.
2. **Purge jobs** (§4). Built: a trigger `Marks` after the levels' bounds and before the fragments,
   at half a delete's bounds. The churn measures a commit's bytes with and without it: the purges
   move a fifth of them into compaction and leave the total as it was.
3. **The background driver** (§2), with a byte budget and one job at a time, coordinated by
   deterministic choice. A lease only if deployments run loops they cannot count.
4. **Inline level-0 compaction** (§2), bounded by a byte budget, as an option off by default.
5. **The locked-store profile** (§5), and the retention date on the seam.
6. **Marked pages out of the header, or vectors out of line** (§4), if the churn with purges still
   shows leaf pages dominating a commit's bytes.

Each step is measured the way the churn measures the dataset today, from the first commit to the
ten-thousandth ([13-dataset.md](13-dataset.md) §15): a step that does not keep every cost flat is
not done.
