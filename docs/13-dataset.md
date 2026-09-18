# Dataset: a log-structured tree of Vortex files

*Proposal, 2026-09-17, revised the same day after the review of the tree, of the order and of
the latency (§4.1, §6.6, §9, §13.J to §13.M). Not implemented. It supersedes the second draft of
[10-indexes.md](10-indexes.md) §8.1, which it challenges point by point in §13.I, and it amends
10 §4 (directory), §4.3 (generations), §6.2 (runs) and §8 (sidecar), 11 §3.8 (append) and
12 §6 (key-ordered delivery, now across objects).*

## 0. The requirement

10 §8.1 named the sidecar's design error, a content hash of the data on the read path, and replaced
it with a small table format: immutable data objects, index fragments, one flat manifest per
version, a commit by conditional creation. That draft still pays, on the read path, for quantities a
user cannot bound: the number of data objects (the manifest lists them all and a point query opens
each), the number of commits (the latest version is found by probing), the number of appends (one
run per chunk, one object per append, each probed), the number of blocks (the `n_blocks` table),
and the number of fragments (one object each, one request each).

**The requirement here: no read-path cost proportional to any N.** Not the bytes of data, not the
data objects, not the commits, not the appends since the last compaction, not the blocks of an
object, not the fragments. Every bound is a constant of the design (a fan-out, a page size, a level
count) times a logarithm, plus the size of the answer. The write path pays once, amortised, in
compaction, and the price is stated (§5.4).

**Kept from the whole spec.** Every data object is a plain Vortex file that a strict Rust 0.86.1
reader opens and scans, with our structures invisible to it. No third-party dependency in the core.
An index is a hint unless it is proven complete (10 §4.1, §8). No wrong answer under any
interleaving of readers, writers, indexers, compactors and vacuum. **The S3 client is not this
library's:** another library implements the store abstraction of §11 and this one never names S3
in code.

## 1. Where the N's hide, and the bound that replaces each

| N | where it costs, in 10 §8.1 or today | the bound here | mechanism |
|---|---|---|---|
| bytes of data | the sidecar hashes the file at the first index read | nothing on the read path | identity from bytes the open already reads; the content hash is computed by the writer, who sees every byte anyway, and checked only by `verify` (§7) |
| data objects | the manifest lists them all, O(N) bytes per open, and a point query opens each | O(log N) pages read, at most 8 + L objects touched | a copy-on-write tree of objects with a summary per node (§4), leveled compaction by a clustering key (§5) |
| commits | `_latest` hint then `HEAD` probes, O(lag); a flat manifest rewritten whole, O(N) per commit | one `List` finds the latest; a commit is one object holding O(depth) pages | version keys that sort newest first (§8.3); path copying into one commit object (§4.3) |
| appends since compaction | one run per chunk, one object per append, every one probed | at most K runs per entry per object, at most 8 objects in level 0 | one LSM rule, inside a file and across files (§5.2, §6.1) |
| blocks of an object | the `n_blocks` table grows with the blocks and a probe reads it whole | O(F · log_F blocks) filter reads, output-sensitive | the filter tree (§6.2) |
| entries of a run | one fence per segment, read whole | ≤ 64 KiB of fences per level, two levels to 10¹² entries | hierarchical fences (§6.3) |
| fragments | one object per fragment, one `GET` each | one ranged read per fragment, inside the commit object that wrote it | §3, §6.4 |
| distinct keys, on a Bloom | a filter over N keys costs 1,2 B × N per level; the coarse levels are unreadable | the structure is chosen by cardinality, never a Bloom above ~10⁵ keys per node | §6.5 |

## 2. Principles

1. **Immutable objects, one commit object per version.** A committed data object or commit
   object is never rewritten. A version is one commit object; a reader holding its header has a
   snapshot.
2. **Metadata is a tree, not a list.** The set of data objects is a copy-on-write tree whose
   nodes carry summaries. Reading descends; committing copies a path. Neither is O(objects).
3. **Bounded fan-in everywhere.** At most K runs per index entry per object, at most 8 objects in
   level 0, at most L levels, at most 1 object per level for a clustering-key lookup. Compaction
   holds the bounds; `Explain` reports when it lags.
4. **Cost is output-sensitive or logarithmic, never proportional to the dataset.** A predicate
   that matches everything reads everything, and that is the answer's own size.
5. **The write path pays.** Merges at `CompleteAsync`, filter trees, fences, compaction. Every
   such cost is stated as a write amplification and measured.
6. **Identity from bytes already read; integrity by checksum of what is read.** Every page is
   referenced by its XXH3-128, every fragment segment and every commit header carries an XXH3-64.
   Nothing on the read path hashes data.
7. **Derived data is rebuildable from the data objects alone**, and folds back into them: a
   compaction that rewrites an object embeds its indexes, so external fragments are a transient
   layer between "written without an index" and "next rewritten".

## 3. Objects and keys

A **dataset** is a store prefix. Two kinds of object, both immutable:

| object | key | content |
|---|---|---|
| data object | `data/<uid>.vortex` | a plain Vortex file, indexes embedded as 10 §3 describes, identity as §7; an append is whatever was appended, a compaction output is the level's target size (§5.3) |
| commit object | `commit/<inverted version>.vxc` | **everything else one commit produced**, in one object: a header, then the tree pages the commit wrote, then the index fragments it wrote (§6.4), then a table of both and an XXH3-64 |

The **header** holds the version, its parent, the schema, the clustering key, the write policy, the
chunker parameters and the dataset's seed (§4.1), the compaction and retention settings, and the
level table: per level, its top page **inlined**, and the pages below it too while the header stays
under 256 KiB. A reader opens a commit with one ranged read of its first 256 KiB, which covers the
header by construction, exactly as a Vortex file is opened by its tail (02 §1).

A **page reference** is `(version, offset, length, XXH3-128)`: the commit object that wrote the
page, where it lies in it, and its content hash. A commit references the pages it did not change
where they already are, in older commit objects; the hash is checked on read, and it lets a writer
reuse any page whose content it knows (§4.3, §8.2). There is therefore **no separate page or pack
object**, no orphan page after a crash, and one `PutIfAbsent` per commit. §13.M weighs the layouts
this replaces.

`<inverted version>` is `10²⁰ − 1 − version`, twenty digits: the **newest commit sorts first**, so
one `List(prefix: "commit/", max: 1)` returns it with no hint and no probe (§8.3). There is no
mutable object anywhere in the layout.

A single existing Vortex file becomes a dataset of one leaf: one commit object, no copy. A file
this writer produces alone, outside any dataset, stays what it is today.

**Naive listers.** A process that lists `data/` and reads every file sees uncommitted and
superseded objects. That is true of every table format and it is stated, not solved: the latest
commit object is the only definition of the dataset's content.

**As delivered (step 36), the bytes.** A commit object is opened by its **head**, where a Vortex
file is opened by its tail, and for the reason this section gives: the header must be inside the
first ranged read, so it is at offset zero.

| where | what | bytes |
| --- | --- | --- |
| 0 | magic `VXCOMMIT` | 8 |
| 8 | format version, little-endian | 4 |
| 12 | header length, little-endian | 4 |
| 16 | header, proto3 | header length |
| … | pages, back to back | |
| … | fragments, back to back (§6.4) | |
| … | table, proto3 | |
| length − 32 | table offset, table length, **object length**, XXH3-64 over header ++ table, magic `VXCT` | 32 |

Three decisions the prose left open, and what settled each:

- **Every page reference is a fixed 36 bytes** (version, offset, length, XXH3-128). The header lies
  before the pages and names their offsets, so the header's length depends on offsets that depend
  on the header's length. Fixed-width references break that circle: the header is serialized once
  to measure it, then again with the offsets rebased, and the builder **checks** that the second
  pass is the same length rather than assuming it.
- **A reference this commit hands out is relative until `Build`**, and a reference to another
  version passes through untouched. That is what makes "every page the commit did not change is
  referenced where it already lies" (§4.3) one line rather than a bookkeeping problem.
- **The object's own length is in the trailer.** A store hands back what it has, so a truncated
  object is a shorter object and not an error; recording the intended length turns every truncation
  into a sentence. `CommitObjectTests` truncates a real object at **every byte** and requires a
  `CommitFormatException` with a reason at each one.

Opening is one request, measured by the counting store of §11. A commit smaller than the 256 KiB
read comes back whole, so that one read verifies the header, the table and the checksum; a larger
one opens on its header alone, and its pages are then read by the references that name them and
checked against them on arrival. The checksum covers the header and the table only, as §7 says:
re-hashing every page at open would make opening cost the whole object.

## 4. The dataset tree

### 4.1 What it is

One tree per level (§5), each a **copy-on-write search tree over data objects**, ordered by the
clustering key when the dataset declares one and by first row position otherwise. Both candidate
trees are ordered, so lookup, range, first, last and ordered walk cost O(log N + output) in
either; §6.6 says what that order buys and where it stops.

**Decided: a prolly tree**, the probabilistic B-tree of Noms and Dolt; §13.J weighs it against a
B+tree with path copying. Once a commit is a sorted batch of changes per level, the two are one
algorithm: load the touched leaves, merge the changes, re-emit the leaves, rebuild the internal
nodes over the changed range up to the top. Only the rule that places page boundaries differs, a
fill factor for the B+tree, a hash of the content for the prolly tree, and from that rule alone
follow history independence, deduplication across writers and the test oracle.

**The boundary rule.**
- A boundary is decided **per entry, from the XXH3-64 of the entry's key alone**, seeded with a
  16-byte random seed the dataset draws once and records in every header. Values never move a
  boundary, so an indexer that updates an object's descriptor rewrites exactly `depth` pages.
- It is **normalised by size**, as Dolt's chunker is: no boundary before 64 KiB of entries since
  the previous one, a probability that rises with the bytes accumulated so that the mean page is
  about 128 KiB, and a forced boundary at **256 KiB**, the page cap. The cap makes the byte bound
  of §9.2 deterministic, and the latency budget of §9.1 sets it, not the CPU.
- After an edit, re-chunking continues past the change until a new boundary coincides with an old
  one, so the tree stays a **pure function of the key set and the parameters**: the same objects
  committed in any order give byte-identical pages and one root hash.
- The rule is one function behind a seam. The B+tree's fill rule is its other implementation,
  measured on the same bench (§14); changing rules rebuilds the trees and touches nothing else.

With entries of about 200 bytes and pages of about 128 KiB, the fan-out is about 650. The header
inlines a level's top page and, while it fits, the pages below it: a lookup then reads **0
dependent pages** up to about 650 objects, **1** up to about 400 000, **2** up to about 280
million. The B+tree at the same cap would move each threshold by about 2× in objects, one step
on a logarithmic scale, which is why the cap is the latency's to set and not the structure's.

**As delivered (step 37), and the curve the bullets above do not fix.** "A probability that rises
with the bytes accumulated so that the mean page is about 128 KiB" admits many curves and most of
them are wrong. A first implementation raised the per-entry probability linearly from the floor to
the cap; `bench -- --tree` measured a mean page of **71 KiB** against the 128 asked for, because a
page holds hundreds of entries and a probability that looks small per entry is a near-certainty per
kilobyte. The family that lands the mean where it is asked is a hazard proportional to the room
left: `p = k · entryBytes / (max − bytes)`, whose mean extent past the floor is `(max − min)/(k+1)`,
so `k = (max − min)/(target − min) − 1` — which is **2** at 64 / 128 / 256 KiB. The mean is then the
target by construction, and the bench checks it rather than the comment claiming it:

| rule | objects | fan-out | mean page | min | max | pages written per commit | pages read |
| --- | --- | --- | --- | --- | --- | --- | --- |
| prolly | 10⁶ | 684 | 130 751 | 65 707 | 254 019 | 3 | 4 |
| B+tree fill | 10⁶ | 686 | 131 109 | 64 800 | 131 250 | 3 | 4 |

Both rules are behind `IBoundaryRule`, as this section requires, and the numbers sit beside each
other. §4.1's prediction of "about 650" at 200-byte entries is 684 at the ~180-byte entries the
bench uses, and the floor and the cap are respected to the entry that crosses them.

**What a commit costs here, measured.** Pages are **written** only where something changed, at every
level: an untouched page is a reference, and a point commit writes `depth` pages at a million
objects. Pages are **read** in the levels above the leaves, because a level's pages are described by
the entries of the level above and this implementation materialises them: 4 reads at a million
objects, which is `depth + 1`; at a billion it would be the ~2 400 pages of level 1. The descent
that would make the read cost `O(depth)` at every size is named here so that the number above is
what it has to beat.

### 4.2 What a node carries

A **leaf entry** describes one data object:

- key range: the row-encoded `(min, max)` of the clustering key over the object, or its row
  position range; the object's key, size, row count;
- identity: the `uid` of its postscript entry, the store's version token if the store gave one,
  the XXH3-128 the writer computed while writing (§7);
- summaries, bounded: per summarised column, `min`, `max`, `null_count`, and when the policy asks
  a pointer to the object's file-level filter (10 §5.4). Summarised columns are the first 32 leaf
  columns by default or the declared list, so an entry has a bounded size whatever the schema;
- its index descriptor: `embedded` (the object's own directory covers the entries the policy
  names) or, per entry, its fragments: `(version, offset, length, XXH3-64, block range)`, each
  inside the commit object that wrote it, at most K per entry after fragment compaction (§6.4).

An **internal entry** holds a page reference (§3) to a child and carries the union of its
children's key ranges and summaries and the sum of their rows. Pruning happens at every level:
a predicate that the node's summaries refute skips the whole subtree.

**As delivered (step 39a).** `ObjectEntry` carries what the commit protocol of §8 reasons about —
the object's key, the `uid` its postscript holds, its rows and bytes, the writer's XXH3-128, and the
fragments attached to it.

`VortexDataset` is the surface: `CreateAsync`, `OpenAsync`, `RefreshAsync`, `AppendAsync` (one data
object and one commit), `ImportAsync` (a file already in the store becomes a leaf **without a
copy**, measured: the store grows by the commit objects **to the byte**), `ObjectsAsync` and
`Scan()`. A data object is a plain Vortex file, so the scan over one object is the core's own scan,
filter and indexes included; the dataset adds the order and the pruning below. Until a clustering
key is declared, a leaf's key is §4.1's other option — **the object's first row position**, eight
big-endian bytes, whose `memcmp` order is its numeric order — and a dataset that declares none needs
no row encoding at all.

**As delivered (step 39b): the summaries, and what they let the walk skip.** A leaf entry now carries
`min`, `max` and `null_count` per summarised column — the first 32 top-level columns by default, or
`DatasetOptions.SummaryColumns`, and **always** the clustering key's own columns whatever the limit
says — and an internal entry carries their union. The bounds are the
object's own file statistics (02 §3, the segment 11 §6.3 prunes a whole file with), read out of the
buffer the sink still holds, **before** the put: they cost the append no request, which is the only
currency §9.1 counts.

The union is **intersection-shaped**, and that is the one thing in this section a reader must get
right. A part that says nothing about a column leaves the parent with nothing to say about it, so a
column travels up only when every child carries it, and a bound only when every child has that
bound. Keeping a bound because the other child was silent would prune a subtree that holds the
answer. Precision travels the same way: `min(min A, min B)` is the true minimum of `A ∪ B` only
while both were true minima, so one `vortex.bounded_min` turns the union inexact and takes the
`min == max ⇒ constant` shortcut off with it.

Asking the question is the **core's** job, not a second implementation of it:
`Vorticity.Scan.ColumnSummary` and `SummaryPruner` are a public seam onto the zone pruner, and
`VortexFile.MayMatch` is now the same call with the file's own statistics — one implementation of
08 §1, two callers. A dataset's node is the caller that has no file.

| what the walk skips | on what | shown by |
|---|---|---|
| a whole subtree, unread | the node's union refutes the predicate | `DatasetScanMetrics.SubtreesSkipped` |
| an object, unopened | its own summaries refute it | `ObjectsSkipped` |
| an object outside `Rows(a, b)` | the row sums, at every level | `ObjectsConsidered` |
| an open, repeated | the object is immutable (§3), so it stays open | `CacheHits` |

Measured, not asserted: a filter inside one of eight objects opens **1** object with the summaries
and **8** with `WithSummaries(false)`, for the same 100 rows; a tree of depth 3 over sixteen objects
answers an impossible filter by skipping **2 subtrees** and considering **0 objects**.

`Rows(a, b)` (§6.6) is the same walk with the row sums as its test, so an object outside the range
is neither read nor counted.

**A tension between §6.6 and §4.1, resolved here.** §4.1 says "one tree per level", ordered by the
clustering key *or* by first row position; §6.6 says `Rows(a, b)` goes through "the insertion-order
tree". Both cannot hold once a clustering key is declared, because there is only one tree. Resolved
in favour of §4.1: **`Rows(a, b)` addresses rows in the dataset's own order**, which is the key order
when one is declared and insertion order otherwise — and with the first-row-position key this tree
*is* the insertion-order tree, the two orders being the same one. A second tree, keyed by position
beside the one keyed by value, is what §6.6's wording would need, and it would double what a commit
writes; nothing has asked for it yet.

**As delivered (step 39c): the clustering key.** `DatasetOptions.ClusteringKey` declares it, the
header carries it, and a leaf's key becomes the **row encoding of the object's smallest key**
followed by the object's first row position: the encoding puts the objects in key order whatever
order they arrived in, and the suffix makes level-0 keys unique where two objects share a minimum.
The row encoding of 06 is self-delimiting, so the suffix decides only a tie and never an order.

The minimum costs one seek on the object's own key cursor, which the mandatory run of §6.1 serves.
An imported object with no run and no sorted column falls back to the per-column minima its
summaries carry — for one column that *is* the minimum; for a tuple it is a key at or below the true
one whose leading component is right, so such objects may tie wrongly below the first column. An
order that is approximate, never an answer that is wrong: every scan still reads every object the
summaries do not refute.

**What step 39 still leaves:** `InKeyOrder` *batches* across objects. The merged cursor below
already walks the keys in order; turning that into record batches needs two things the core does not
expose — a batch that is a **window** over another batch's buffers, and a per-row read of a column as
a filter literal. Both exist inside the scan; neither is public, and inventing a second copy of
either in this package is the wrong place for them.

*Step 41b closed the first of the two: `RecordBatch.Window(start, length)` is public, because the
compaction's k-way merge needed exactly it — "these rows of that batch". It gathers rather than
aliasing the buffers, and says so: a true window is a second traversal of every canonical kind and
nothing measured yet asks for it. The second seam is still missing, and with it step 39d.*

*Step 39d did not need the second one. The merge compares rows by their row encoding (06), as the
compaction's does, and never reads a value as a literal. What it needed instead is a seam nobody had
named: a `Select` without the key leaves a merge nothing to compare, so the key is read on top of
the selection and dropped before a batch is handed on — the trim the scan already performs on a
filter's columns, now public as `RecordBatch.Project(Projection)`, which copies no value. §6.6 says
what the read costs.*

The acceptance is §14's and it is a comparison, never a chosen number: the same rows written into a
dataset of several objects and into one file answer the same, unfiltered and filtered, with the
index chain on and off — and now with the summaries consulted and ignored.

### 4.3 Commits are paths, written as one object

A commit is a **sorted batch of changes per level**: entries added, entries removed, descriptors
updated. Adding an object appends a leaf entry: the leaf, its ancestors and the level's top page
are re-chunked, O(depth) pages, and those pages are laid out **inside the commit object** with the
header and the fragments. Every page the commit did not change is referenced where it already
lies. A page whose new content equals one the writer has already read, which the prolly rule makes
common after a rebase, is referenced rather than rewritten. Removing objects (compaction) is the
same over the range of leaves it touches. One `PutIfAbsent` creates the commit (§8). The commit
cost does not depend on the number of objects, and a crash leaves nothing to clean up but data
objects.

## 5. Levels and compaction

### 5.1 Why levels are part of the read bound

Without a merge policy, the number of objects a lookup touches is the number of appends. The
bound of §2.3 is only true if compaction holds an invariant, so the invariant is specified here
and `Explain` reports every violation of it as a lag, with the count.

*As delivered (step 41a): `DatasetScanBuilder.ExplainAsync` returns a `DatasetPlan` — the version,
the objects per level, the **lag**, whether a clustering key is declared, and what the walk would
read and skip — without opening a data object. A level 0 above its ceiling of eight is reported and
**never refused**: compaction is the user's background job and "a library never stalls a writer"
(§5.3). A test appends eleven objects on purpose, reads a lag of 3, and gets all 1 100 rows.*

### 5.2 The invariant

- **Level 0** holds the appended objects as they came, overlapping in key. At most **8**.
- **Levels 1..L** hold, when the dataset declares a clustering key, objects with **disjoint key
  ranges** inside a level. A lookup by key touches at most one object per level: at most
  **8 + L** objects in all, and L ≤ 5 up to 10⁵ × the level-1 size. This is RocksDB's leveled
  compaction, with Vortex files as the sorted string tables. Level i holds up to `F^i` times the
  size of level 1, `F` = 10 by default.
- Without a clustering key, levels are **size tiers**: level i holds objects of about `F^i` times
  the level-1 size, at most `F` of them. A lookup touches all of them unless summaries refute
  them; this is output-sensitive, not bounded, and the dataset says so in `Explain`.
- **Inside every object, at most K = 4 runs per index entry**, and in a single file appended in
  place the same rule holds (§6.1).

**As delivered (step 41a): the levels, without the policy that fills them.** A version names **one
tree per level**, not one tree: `DatasetLevels`, read from the header's list, which already carried
several. A commit applies a sorted batch of changes **per level** (§4.3) and writes a `CommitLevel`
for each occupied one; a level nobody filled stays an empty tree at its own number, because a
level's number *is* its meaning — its target size, its place in the lookup bound, what a compaction
of the level below writes into.

An operation says which level it acts on. A compaction says it **per input and per output**, because
a leveled compaction reads two levels at once — the objects of level `i` and the objects of level
`i + 1` whose key ranges they overlap — and splitting that into one operation per level would split
§8.2's row 5 with it: "an input is missing: the outputs are garbage" has to abandon the whole
compaction, and two operations abandon separately.

A scan merges the levels into one key order. With a single level the walk keeps §6.6's O(log N)
`Rows(a, b)`, testing a subtree's row sum before descending into it; across levels the row offsets
are not known until the merge has produced them — an object of level 1 may sit between two objects
of level 0 — so the range is applied per entry and the cost is the objects rather than the rows.
Bounded either way, and the difference is stated rather than hidden.

The one inline budget of §3 is shared and spent from the top down: level 0 is the level every lookup
descends and the one an append touches, so it gets the room first.

**As delivered (step 41b): the invariant is now held rather than described.** `CompactionPolicy`
reads the leaves of every occupied level — the objects, never the rows — and returns a
`CompactionPlan`: the objects and bytes per level, the lag, the style, and the one job that is due.
`DatasetCompactor` runs it. A randomised append stream of 24 rounds, drained at random moments, ran
16 compactions across 4 levels and the two halves of the invariant were asserted **after every
single step**: level 0 inside its ceiling, and every level above key-disjoint object by object.

The numbers of §5.2 and §5.3 are reconciled by one formula rather than by two readings. An object
written into level `i` targets `level1 × F^(i-1)` — "256 MiB at level 1, growing with the level" —
and a level holds `F` of them, which is at once §5.2's "F^i times the size of level 1" and the
tiered bullet's "at most F of them". The fan-out and level 1's size are read from the header when it
states them (§4.1), so two writers of one dataset compact it to the same shape.

One guard is not in the specification and belongs here: **a level holding a single object is never
over its size**. The object target saturates at 4 GiB, so a level's capacity stops growing at the
top; without the guard a dataset past that point would move its last object up a new level on every
call, for ever, and every move would look like progress.

### 5.3 What a compaction does

A compaction reads `k` objects and writes one or more, at the target size of the destination
level (256 MiB at level 1, growing with the level, capped at 4 GiB so that an object stays a
reasonable unit of rewrite). It is a Vortex rewrite: a k-way merge of the inputs on the clustering
key when there is one (the row encoding compares by `memcmp`, 06), a concatenation otherwise, and
**the writer builds the embedded indexes of the outputs once**, with one run per entry per object
(§6.1). The inputs' external fragments are dropped: the outputs need none. Compaction is therefore
also the index compaction of everything it touches, and the external layer shrinks at every pass.

**Level 0 is not sorted, and nobody sorts it.** An append arrives in any order and the writer is a
stream with bounded memory (11 §3), so a leveled compaction cannot ask it for an object sorted by
the clustering key. Instead the write policy **forces a `SortedRuns` entry on the clustering key**
for every level-0 object, never abandoned, and the compaction reads each input **in key order
through that run**, the permuted read `InKeyOrder` already performs (12 §6). The merge's inputs
are then ordered, its outputs are sorted by construction, and the writer never learns to sort. An
append whose run the budget would refuse is refused as an append, with the reason.

Triggers: level 0 above 8 objects; a level above its size; an entry above K fragments (that one
is a fragment compaction, §6.4, and reads index bytes only — since step 42c planned last, after the
two that move data, which drop an object's fragments with it). Compaction runs as a client of the
same commit protocol as any writer (§8) and is the user's background job: **a library never stalls
a writer**; a level-0 count above 8 degrades the read bound and is reported, never refused.
Contention between writers is not the tree's problem either: when many processes append, one
coordinator batches their rows into one commit; without it the protocol of §8 stays correct and
only slower.

The zero-decode rewrite of 11 §9 (pass encoded arrays through when the encoding tree is kept) is
the optimisation that makes compaction cheap on CPU; it is separate work and it is not assumed.

**As delivered (step 41b).** The merge emits **runs, not rows**. At every step one input holds the
smallest key and may emit every row at or below the smallest key the others hold — a contiguous
window of its current batch — so the cost is `k` comparisons per window rather than per row, and the
writer keeps receiving batches of a useful size. `RecordBatch.Window` is the seam that says "these
rows of that batch"; §4.2 had already named it as one of the two things the core did not expose, and
it is now public rather than copied into this package. The comparator is the row encoding of 06,
because it is the order the tree, the runs and the seeks already use: a comparison written here
would be a second order, and the first time the two disagreed the dataset would hold an object whose
own run says it is sorted and whose rows are not.

A roll **never splits a key**. Two outputs sharing a boundary value would have overlapping ranges,
and §5.2 asks a level for disjoint ones, so the object is sealed at the first key change past the
target rather than at the target. The overlap that selects the destination inputs is computed
against the **union** of the source range, not object by object: sources at `[1,5]` and `[90,100]`
leave a hole, the outputs span `[1,100]` because a merge writes one sorted sequence, and a
destination object at `[20,30]` that was not read would end up overlapping an output.

**Two refusals, named rather than worked around.** A **composite** clustering key has no permuted
read — `InKeyOrder` drives one column and the composite key source serves cursors only (12 §4.6) —
and ordering on the first column alone would write objects whose ranges overlap below it, so such a
compaction is refused with the sentence that would fix it. A key column **holding nulls** is refused
too, because a key-ordered read delivers no row whose key is null (12 §6) and the merge would drop
them in silence. The rows written are compared against the inputs' count at the end regardless: a
rewrite that loses rows is the one failure it must not have.

And the price, measured rather than assumed: four interleaved objects of level 0, 42 808 bytes, came
out as one sorted object of **16 596**. §5.4's write amplification counts the bytes a row is
rewritten through; it does not say each pass writes as much as it read, and sorting a key column is
exactly the case where a pass writes much less.

### 5.4 The price, stated

Leveled compaction with `F` = 10 rewrites each row about `F/2` times per level it crosses: about
**20 to 25×** the appended bytes over four levels. Tiered rewrites each row about once per level,
about **L×**. `F` and the policy are dataset settings; the default is leveled when a clustering key
is declared and tiered otherwise. A dataset that appends 1 TiB a day and is leveled writes about
25 TiB a day in compaction, spread over the day; that number is the honest cost of the "one object
per level" read bound, and it is what every LSM store pays.

## 6. Indexes at scale

### 6.1 One run per entry per object, K in flux

Today a locating index has one run per chunk (10 §6.2), so a lookup on a key uncorrelated with
row order probes every chunk: the per-run bounds span the whole domain and prune nothing. That is
O(chunks), and a 10 GiB object has thousands.

**The writer merges at `CompleteAsync`.** Chunk runs are still built in flux, in one pass, from
the chunk's own table (11 §3.2.2); they are **spilled** to a scratch (memory under
`IndexBudgetBytes`, then `VortexWriteOptions.ScratchDirectory`) instead of being written to the
sink, and at `CompleteAsync` a streaming k-way merge writes **one run per entry** in segments of
65 536 entries, with hierarchical fences (§6.3) and one XXH3-64 per segment. The sink stays
forward-only. Memory is the merge's buffers. The merge reads index bytes, at most a few per cent of
the data (10 §5.5's budget), never data. Postings merge by key with their block lists
concatenated; sorted runs merge by `(key, row)`; the dictionary probe has nothing to merge.

**In-place append (11 §3.8) keeps the same bound.** An append writes its new run and, when the
entry then has more than K = 4 runs, merges them all into one, reading the old runs (index bytes)
and leaving them dead. Amortised, each index byte is rewritten about `log_K` times over the life of
a file. A lookup reads at most K fence pages. The rule is the same as §5.2's and it is why the
single-file mode stays: it is the format's own operation, it yields one Rust-readable file, it is
tested, and it now scales.

**As delivered (step 22).**
- **The scratch** is `Writing/RunScratch`: rented one-mebibyte pages up to 64 MiB, then a temporary
  file in `VortexWriteOptions.ScratchDirectory`, opened `DeleteOnClose`. A write without a locating
  index makes none. A chunk run is laid raw, in windows of 4 096 entries: its key offsets and keys,
  then its file rows (`long`) or its block-list offsets and file blocks (`uint`). No compression is
  paid twice.
- **The merge** is `Writing/RunMerge`: a heap of cursors, one window each, ordered by the key's
  ordered integer or its bytes, then by row, or by the run's place for a postings key, whose lists
  are then concatenated in block order. Past 64 runs it merges in passes, each laying its runs back
  into the scratch. The output is cut at `segment_entries` into the same arrays as before, with rows
  relative to the run's first row, at `u32` unless the run spans 2³² rows or more, at `u64` then; the
  row array's dtype says which, and both readers take both.
- **The last chunk keeps a run of its own** when the file's rows are not whole blocks, since an
  append re-opens that chunk and drops its run; the merged run ends before it and stays.
- **K = 4 at the append** (`Writing/RunAbsorb`). When the kept runs and the append's two would pass
  K, the kept runs that end at the append's first block, with no gap between them, are read back —
  index bytes, checksums held — laid raw in a scratch the writer takes over, and merged in front of
  the new chunk runs; the builder that continues the entry is the one whose kind, column and options
  match, which is the test the directory merge applies. Without such a builder, or when it is
  abandoned, the old runs stay listed. A run before a gap is never merged: a merged run claims every
  block of its range.
- **What it costs, measured and stated.** "Merges them all into one" rewrites the entry's index
  every K − 1 appends, so over n appends of one size the index bytes rewritten grow as n² / K, not as
  n · log_K n. The logarithmic figure above holds only for appends that grow geometrically. A
  workload of many appends belongs to the dataset (§5), whose compaction is tiered across objects.

### 6.2 The filter tree

Bloom filters keep 10 §5.1's structure and bits. What changes is the **layout**: instead of one
table of `n_blocks` per block that a probe reads whole, filters form a tree of fan-out **16**. A
node is `[16 × u32 child sizes][the node's filter]`, and its 16 children are contiguous behind it,
so a probe reads the node, and, if it says maybe, reads the children in **one ranged read**, and
descends. Leaves are the per-block filters. A point probe reads at most `1 + 16 · depth` filters
with `depth = log₁₆ blocks`: 5 for a 100 M-row object. The 10 §4.3 generations *are* the internal
nodes; nothing about how they are built changes, only where they are written.

A node above `max_blocks` (128 KiB) is not built, exactly as today, and the descent starts at the
highest built level. For a column whose distinct count per 16 blocks exceeds what 128 KiB holds
at the target `fpp` (about 100 000 keys), that level is the leaves, and the probe would read
every block's filter: O(blocks). **That is not a layout problem, it is the wrong structure for
the column**, and §6.5 says which one is right.

**As delivered (step 24).**
- **One entry per column, one run per tree, one root per run.** The entry's options are version 2
  and hold nothing that grows with the file: no `n_blocks` table. The run's payload is the root,
  and the run's options give the root's word count. Version 1 entries are no longer read; a reader
  ignores them, so a file written before this step loses that pruning and no row.
- **A node** is u32 words in an array blob: a header of nine words (level, leaves, filter size,
  and the offset, length, XXH3-64 and alignment of its children's region), one size per child, then
  its filter. The children are written before the node, as one region, and the node carries their
  checksum, so a probe checks every region it reads. The format is in `Indexes/BloomTree.cs`. A
  level-1 node none of whose blocks has a filter is **bare**: it lists no child and names no region,
  which saves sixteen words where they are most of the cost.
- **The writer** is `Writing/BloomTreeWriter`. A generation closes into a level-1 node; sixteen
  closed nodes of a level go out as one region under a new node; at the end, the partial levels
  close from the bottom and the first level left with one node is the root. A level holds at most
  sixteen nodes in memory. A node's filter comes from the union of its children, one hash set per
  open level. A closed node's set moves up: handed over when it is the first child, folded in and
  recycled otherwise. A set that passes what a node of `max_blocks` holds is dropped, and that node
  and every node above it get no filter. At 1 % under the default ceiling a node holds about
  108 000 values.
- **The leaves keep their bits and their clamp**, and the reference's vectors still hold. A node
  above the leaves is either built at the size its union asks for or not built. It is never clamped
  into a saturated filter, as a version 1 generation filter was.
- **The root is the file-level filter** when the policy asks for three resolutions. It then uses
  the file-level ceiling and set. Under two resolutions it is built like any node.
  `VortexFile.MayMatchAsync` reads the roots alone, so it now answers from a file of two
  resolutions too.
- **The probe** (`Indexes/BloomPruner`) reads every run's root in one read. Then, level by level,
  it reads the children of the nodes that still cover a live block: one region per node, one
  coalesced read per level. A node that proves the predicate false kills its blocks, so its children
  are never read. A region or a node that does not check out claims nothing, and nothing beneath it
  is read.
- **What it reads, counted** (`BloomTreeTests`). Over 4 096 blocks, a value present in one block
  reads 4 regions: the root, then one group per level. An absent value reads the root alone. Under
  a ceiling of 64 blocks, the two upper levels have no filter, and the same probe reads 19 regions.
- **An append cuts a tree, it does not drop it.** The nodes over the blocks the append writes again
  hold values those blocks no longer have, so they only answer "maybe" more often. The shorter run
  leaves those blocks to the append's own tree. Trees are not merged, since filters of different
  sizes do not merge, so a probe reads one root per append.
- **What a level costs.** When values do not repeat, a level costs about what the level below
  costs. On `AutoIndexTests`' 48-block fixture, the root took a unique 256-byte column from 1.6 % to
  2.1 % of its bytes, past `Auto`'s 2 %. The fixture's values are now 384 bytes wide.
- **§6.5's symmetric rule** is in `BloomBuilder`. Under `Auto`, a column whose first generation
  passes a node's capacity is given up, with that reason. Asked for by name, it keeps its leaves,
  and its generations have no filter.

### 6.3 Hierarchical fences

A run's segment table (first and last key, entry count, offset, length, XXH3-64 per segment) is
the fence table. One fence is about 48 bytes for integer keys and up to a few hundred for strings;
a 64 KiB page holds 300 to 1 300 fences, so one level covers 20 to 85 M entries and two levels
cover every object this format can hold. The run's `options` holds the root fence page inline when
it fits and otherwise a pointer to it; a lookup reads at most two fence pages before the two
payload segments. Bounded, whatever the run's length.

**As delivered (step 23).**
- **The wire form** is 10 §4.2's amendment. A run of at most 64 segments keeps its table inline, as
  before. A longer one has its root inline in its options, the root's child pages as its payload,
  and pages of at most 64 KiB below.
- **The writer** is `Writing/FenceTreeWriter`. It cuts each level into pages, and each page it
  writes becomes one fence of the level above. The first level with at most 64 fences is the root.
  A parent names its children by region and checksum, so the pages go out level by level after the
  index writer closes, before the zone maps, and count as index bytes. A page always takes two
  fences, so every level at least halves: a key larger than a page deepens the tree and never stops
  it. The reader takes 64 levels, which a 64-bit count cannot pass.
- **The reader** is `Indexes/FenceTable`, one per run, in memory for an inline table and paged
  otherwise. It descends by key, by segment or by entry position and keeps the last 64 pages it
  read. The key source and the pruner both use it, and so does the append that reads a run back
  (§6.1). The pruner stops at a segment whose last key passes the key, so it reads no further
  fence.
- **What a fence costs, measured.** For `u64` keys at offsets near 10¹², a fence takes 74 bytes in
  a sorted run and 98 in postings, not the 48 estimated above: each region carries an offset, a
  length and a checksum. Past 2⁴⁴ it takes 76 and 101 bytes, and a 64 KiB page still holds 862 or
  648 of them. Under a root of 64, two levels then hold 4.5 × 10¹² sorted entries and 3.4 × 10¹²
  postings entries (`FenceTreeTests`).
- **What a lookup reads, counted.** A synthetic run of 10⁸ entries reads one page, and one of 10¹⁰
  entries reads two, whichever key is sought. A key past the run reads none.

### 6.4 External fragments, in commit objects, transient

A **fragment** is one entry's runs over one block range of one object, in the format of the
in-file runs: array blobs, then a footer that is the `IndexEntry` message of 10 §4.1 with its
fences, its own encoding table (the sidecar's field 9 of today) and the column's dtype, so a
fragment decodes with no access to the data object's footer. A commit's fragments are written
into its **commit object** (§3), and a leaf entry references a fragment as `(version, offset,
length, XXH3-64)`: one ranged read.

An indexer works by `(object, block range)`; each batch is one commit; coverage may be partial and
the rules of 10 §8 apply unchanged: a pruner uses the covered blocks, an exact source waits for
complete coverage, which the leaf entry states. An entry with more than K fragments is compacted
by merging them (index bytes only) into one fragment in a new commit; a data compaction that rewrites
the object embeds the index and drops every fragment. So the external layer holds, at steady state,
only the indexes of objects nobody has rewritten, at most K fragments per entry, and usually one.

The sidecar of 10 §8 is one fragment in one commit object: it is not a separate thing any more,
and the word is retired.

**As delivered (step 42a): the core's half — a fragment is built over a range, and read beside the
file's own index.** The dataset's half (commit objects that carry fragments, the indexer, the cache,
fragment compaction) and the sidecar's retirement are the steps after it.

- **The container is the sidecar's**, which step 42 found already position-independent: `VXIX`, the
  payloads, a directory that carries the binding and its own encoding table, a trailer — every offset
  counting from its first byte. A fragment may hold several entries, one per index its policy names;
  the reader treats each entry on its own, which is §6.4's "one entry's runs" read at the entry.
- **Built over a range.** `VortexFileIndexer.BuildFragmentAsync(file, policy, rows, storeToken?,
  contentHash?)` indexes whole blocks of an open file into bytes, bound by the file's identity, or by
  the store's token for a file written without one; the content hash is recorded when the caller
  knows it — a dataset's entry does — and computed by nobody. The builders are numbered from the
  range's first block. Fed a range without that, a sorted run fails loudly ("row 286 is before the
  run's first row 4096"), but a Bloom tree claims blocks 0 to 7 for the filters of blocks 4 to 11 —
  the silent case, which a test keeps. A dictionary probe describes the file's chunks, not the rows
  read, so a range writes it whole.
- **Read beside the file's own index.** `VortexReadOptions.IndexFragments` attaches fragments at
  the open. Each is bound like a sidecar, then added to the directory the file names, entry by
  entry, by one rule — an entry's identity is its kind, column, block length and options, byte for
  byte. The file's own entry of an identity stays the file's. The same identity across fragments
  joins its runs when their blocks are disjoint, which is how two halves become one source. An
  overlap is left out, since a key walk would meet the keys twice. `VortexFile.IndexFragmentRefusals`
  says, per fragment, what was left out and why. Nothing fails the open.
- **Every run knows its origin.** Offsets inside a payload — fence pages, filter-tree children —
  cannot be rebased without rewriting it, so no run is rebased: `IndexRun.Origin` names the source
  its offsets count in and the encoding table its payloads name, and the dozen read sites of the
  index path read and decode per origin, one batch per origin where they batched per file.
- **The run cache is keyed by origin.** Two fragments' first segments lie at the same offset of
  their own bytes, and a cache keyed by the offset alone answered the second fragment's run with
  the first one's keys — measured by the test that walks across both: `[0, 0, 15, 15, …]`.
- **What 10 §8 asks, held.** A fragment over blocks 4 to 11 prunes those eight blocks and no other,
  counted by `Explain`; a key source refuses one fragment over half the file, naming the blocks it
  covers, and walks the file once the second half is attached.

**As delivered (step 42b): the dataset's half — fragments written in commit objects, by an indexer
that commits once per block range.**

- **`DatasetIndexer.IndexAsync(dataset, object, policy, rows?)`** reads the object's rows through its
  plain view, builds the fragment, and commits one `AddFragment` — one batch, one commit, as the
  paragraph above asks. The object comes from the dataset's own walk, which knows its level and its
  leaf key. An object without an identity, one imported from another writer, is refused: its
  fragment would bind by the store's token, which its entry does not carry yet.
- **The operation carries the bytes, and the commit that lands writes them.** `AddFragment` held a
  reference, whose version is the version that writes it — which a rebase decides, not the indexer.
  The committer now opens its commit object before re-applying the operations, writes each applied
  fragment into it, and names it in the entry. An attempt that loses throws both away.
- **The commit object's body is laid out in the order things are added.** §3's table says pages,
  then fragments. But an indexing commit writes the fragment BEFORE the leaf page that names it, and
  the step-36 builder took a fragment's offset at its addition while laying every fragment after
  every page: right only until a page followed a fragment, which every indexing commit does, and a
  reference hashed into a page cannot be moved. The body is now one sequence and every offset is
  final when handed out; an object built pages-first is byte for byte what it was.
- **A fragment is its content.** "Already there" (§8.2, row 3) compares bytes — a length and a hash —
  since a fragment not yet committed has no reference to compare; and a drop removes the fragment
  of those bytes wherever it lies, which a rebuild and a fragment compaction both mean. Two indexers
  of one range write one fragment: the fragment is a function of the object, the policy and the
  range, and the test holds the second at `AlreadyThere`.
- **An object is opened with its fragments.** The object cache keys a handle by the object and its
  fragments' hashes: the object is immutable, its fragments are not, and a file reads its index
  directory once. A new fragment is a new view, never a stale handle. The fragments are read from
  their commit objects by reference and checked against it; one whose bytes do not match fails the
  read with the reason, as a torn page does — step 43's tamper tests decide whether a scan should
  rather proceed without it.

**As delivered (step 42c): "compacted by merging them (index bytes only) into one fragment" — a
bundle of the containers, not a rebuilt index.**

- **Why not merge the runs.** Two kinds cannot be merged from their own bytes into one run. A Bloom
  tree's upper filters are sized from the exact union of their blocks' hashes, which the leaf
  filters do not give back. And a run's payload — fence pages, filter-tree children — holds offsets
  into its own container, which a copy into another would have to find and rewrite kind by kind.
  What this paragraph asks for is a cost: one fragment, one ranged read, index bytes only.
- **So the fragment is a bundle.** `FragmentBundle` copies the K containers as they are, one after
  another, each on the 64-byte alignment its regions assume, behind a table of offsets and lengths:
  `VXFB`, the parts, the table, a trailer. The object cache splits it back and hands each part to the
  reader as the fragment it was, where 42a's rules apply unchanged. A bundle of bundles is flat.
  Nothing is decoded and no offset is rewritten.
- **The trigger.** `CompactionPolicy` plans a `Fragments` job once neither trigger that moves data
  is due, since those drop an object's fragments with the object. `DatasetCompactor` swaps each
  object's fragments for their bundle in one commit, by content, which keeps it right under a
  rebase: an indexer's fragment attached meanwhile stays, and a second identical compaction finds
  the same bundle there.
- **Measured on one object indexed in eight one-block commits**, K = 4: one fragment of the eight
  containers' bytes plus alignment and a table; every entry read back with its eight runs; the
  key-ordered read and five questions answered as before; nothing due afterwards. Four more
  fragments on top, and the next bundle holds twelve flat parts.
- **What it leaves.** An entry indexed by n ranges keeps n runs where a rebuild would write one: a
  pruner reads them in the bundle's one read, and a cursor merges them as it merges any runs.
  Rebuilding them into one run is a data read, which is §10's rebuild, not this.

**As delivered (step 42d): the word "sidecar" is retired.**
- **What is removed.** The `.idx` file, `VortexFileIndexer.WriteSidecarAsync`,
  `VortexReadOptions.IndexSidecarPath`, `IndexSidecar` and `vxdump --sidecar`.
- **What replaces them.** A single file's post-hoc index is a fragment in the caller's hands:
  `BuildFragmentAsync` over the whole file, attached with `IndexFragments`. The container and its
  binding live on as `IndexContainer`.
- **The tests.** The five binding tests of step 26 are now fragment tests, each over the same
  scenario:
  - bound without reading the file;
  - refused once the file changes;
  - bound by the token when there is no identity;
  - the reasons;
  - a byte changed in place passes the reader and fails the recorded hash.
- **A hole the last test found.** A fragment the budget emptied still binds, and binds to
  nothing. The runs of a small, well-compressed file outweigh its data. The test now asserts
  every report `Built`, since a bound fragment with no entry proves nothing.

### 6.5 Which structure for which column

The read bound depends on choosing by cardinality, which the writer knows exactly (11 §3.2.2).

| distinct keys per block | structure | point lookup cost | notes |
|---|---|---|---|
| ≤ 8, or dictionary-encoded | zone map, `dict.probe` (10 §5.3) | one values child per chunk, bounded by the zone map | free |
| medium, up to ~10⁵ per 16 blocks | filter tree (§6.2) | `1 + 16 · log₁₆ blocks` filters, output-sensitive | 1,2 B per distinct key per level |
| high, or a range or order predicate | one sorted run per object (§6.1), fences (§6.3) | 2 fence pages + 2 segments per object touched | the index costs the key column's compressed size plus a `u32` per row |
| the dataset's lookup key | the **clustering key** (§5) | `depth − 1` pages per level, ≤ 1 object per level, then the run above | the only structure that bounds the number of *objects* |

`Auto` already abandons a Bloom whose generation overflows the floor; it gains the symmetric rule:
a column whose first generation exceeds the node cap is not a Bloom candidate, and is reported so.
A locating index on it is an explicit `IndexPolicy.SortedRuns`, as 12 §13 decided.

### 6.6 Order beyond the lookup

Both trees of §4.1 are ordered by their key, so the order of **one** key, the clustering key, is
bounded everywhere in the dataset:

| need | mechanism | cost |
|---|---|---|
| walk the objects by key | an in-order walk of a level's tree, children prefetched in parallel | O(log N + objects touched) |
| rows in clustering-key order, `InKeyOrder` (12 §6) | inside a level ≥ 1 the objects are key-disjoint, so a sequential walk; a k-way merge of the level-0 objects, through their runs, and of the levels | ≤ 8 + L cursors, bounded |
| access by position, `Rows(a, b)` | the insertion-order tree, nodes carrying row sums | O(log N) |
| rank and count on the clustering key | the row counts of the objects wholly inside the range, the exact cover (12 §9) in the two boundary objects per level | O((8 + L) log n), bounded |
| composite keys and prefixes | the row encoding orders by `memcmp` (06); a prefix is a range | the same |
| an order on any other column | no tree helps | below |

**The bound holds for one key.** An order on another column costs, at best, one cursor per object
the summaries cannot refute, with a sorted run per object: proportional to the output, not
bounded. Three answers, stated so that nobody expects a fourth:
- `ORDER BY x LIMIT k` prunes by the summaries: an object whose `min` exceeds the current k-th
  best is skipped, and a node's summaries skip its whole subtree. Output-sensitive, good in
  practice.
- A second order is a **second copy** of the data, clustered by that key: a projection in the
  sense of ClickHouse or Lance, at the price of the storage. It is a dataset of its own and needs
  nothing new here.
- A global secondary index over the dataset is excluded, as §13.D says: every compaction would
  rewrite it.

**As delivered (step 39b), two rows of the table.** `Rows(a, b)` is `DatasetScanBuilder.Rows(from,
to)`: one walk of the tree that tests a node's row sum before descending into it, so a subtree
outside the range costs no read and an object outside it is never opened — O(log N) plus the objects
the range touches, and the per-object range is handed to the core's own `ScanBuilder.Rows`. The
first row of the table, the in-order walk of a level, is the same walk with no range; children are
**not** prefetched in parallel yet, so its cost is one dependent read per page rather than one per
level.

**As delivered (step 39c), the second row and the fourth.** `DatasetKeyCursor` is the k-way merge:
one `KeyCursor` per level-0 object, over the mandatory run of §6.1, chosen between by
`KeyCursor.Compare` so a merge cannot disagree with the runs it merges. `Cursors` reports how many
are open, because **that number is the claim** — what bounds it is not this code but §5's invariant,
and until compaction exists (step 41) it grows with every append. A linear scan picks the smallest
of *k*, not a heap: *k* is bounded by a small constant and a heap would cost bookkeeping to save
comparisons that do not exist.

An object with no source for the key is a **refusal**, not a skip: a walk that quietly left an
imported object's rows out would answer a question nobody asked. The scan is unaffected — it reads
objects, not keys.

The terminals came with it. `AnyAsync`, `MinAsync` and `MaxAsync` run across the objects, and an
object whose own summary cannot beat the best so far is skipped unopened — §6.6's third answer,
"`ORDER BY x LIMIT k` prunes by the summaries", at *k* = 1. An inexact bound is still a bound, which
is what makes the skip legal: a summary `min` at or below the true minimum that already loses to the
best cannot be hiding a winner.

What is **not** delivered is `InKeyOrder` as batches across objects; §4.2's note says what it needs.

**As delivered (step 39d): the second row, whole, and the last.** `DatasetScanBuilder.InKeyOrder(path,
descending)` reads each object through the core's own `InKeyOrder` — its run or its sorted column,
with the builder's filter and projection — and merges them with `KeyOrderedMerge`, the merge §5.3's
compaction writes with: one merge, whether it writes objects or reads rows. `ORDER BY x LIMIT k` is
that read and a consumer that stops.

**An object is opened when it could hold the next row, not before.** The objects are offered in the
order of a lower bound on their keys, and one is opened only once its bound is at or below the
smallest key an open input holds. The bound of this table follows without a line of code of its
own: a level's objects are key-disjoint (§5.2), so the next one's minimum lies above everything the
current one still holds, and a level above 0 costs one cursor at a time. Measured on the same rows
three ways: **4** cursors over four interleaved objects of level 0; **1** over the six key-disjoint
objects of level 1 a compaction made of them; **3** once two objects of level 0 sit on top — 2 + L.
And a consumer that stops after `k` rows has opened no object whose minimum lies past the k-th key:
`LIMIT 10` opens **1** object of 4, in either direction, and `LIMIT 300`, which crosses one boundary,
opens **2**. `DatasetPlan.Cursors` states the bound before the read; `DatasetScanMetrics.Cursors`
measures it after.

**Where the bound comes from.** On the clustering key, upward, the tree already is that order: a
leaf key is the object's encoded minimum, exact (§4.1), and the walk is lazy, so a consumer that
stops early stops the walk too and a subtree past the k-th key is never read. Anywhere else — another
column, or downward — the kept objects are collected and sorted by their summaries' bound: the leaf
pages are read, the objects are not opened. On another column every kept object may be open at
once, output-sensitive as the last row of the table says, and `Explain` counts them.

**A float has no bound.** A zone's min and max exclude NaN (08 §2), while the key order puts a
negative NaN first and a positive one last (12 §4.4): a float's summary is not a bound in the key
order, and a descending merge that trusted one would deliver the positive NaN after the largest
number. Float objects are therefore opened before the first row; upward on the clustering key the
tree's minimum — the run's first key, NaN included — is the bound. A bound that lies anyway is
refused: an object whose first row lies below the bound it was ordered by raises
`VortexFormatException` rather than deliver a wrong order.

**Ties follow the dataset's order when a reader asks.** Each object carries a rank, its place in
the walk, negated downward, and of two rows with one key the lower rank goes first — which makes a
descending read the exact reverse of an ascending one, as in a file (12 §6). The compaction does
not ask, and was measured not to want it: ranked ties cut a run at every key two interleaved inputs
share, and on the randomised stream of the compaction tests the first compaction then wrote its 493
rows as 3 objects and 21 641 bytes instead of 2 and 16 609. With its ties unranked and its inputs
ranked by the job's order, the compaction reproduces the previous one step for step — the same
objects in and out, the same bytes read and written, all sixteen compactions of that stream — while
now opening its inputs on demand.

Without summaries (`WithSummaries(false)`) no object has a bound and every one is opened before a
row goes out: the eager merge, which every test reads beside the lazy one and requires to answer the
same.

**What this row does not cover yet.** `DatasetKeyCursor` still opens one cursor per object of every
level: the bound holds for `InKeyOrder`, not for the cursor, whose seek would have to reach, in each
level above 0, the one object that can hold the sought key. And the fourth row of the table — rank
and count on the clustering key in O((8 + L) log n) — is not delivered: the dataset has no rank, and
`CountAsync` opens every object the summaries keep. The note of step 39c above says "the fourth" and
describes the terminals, which are the §6.6 third answer at k = 1, not that row.

## 7. Identity and integrity

- **Every postscript this writer writes mints a `uid`**: 16 random bytes in the metadata entry
  `vorticity.identity`, written last before the postscript so that the 64 KiB tail read of the
  open covers it. It identifies a **version of the bytes**, not a file: an in-place append mints a
  new one, and a fragment bound to the old `uid` is refused before any length is compared. Two of
  sixteen metadata slots are now this library's (the directory and the identity); the count is
  stated in 10 §3.2. 10 §7.3's promise that `Fastest` is byte-identical to the pre-index writer is
  dropped; the project is unpublished.
- **The store's token** (`VersionId`, `ETag`, or on a file system the file id with size and
  modification time) is recorded when the store gives one and compared as an opaque string. It is
  the only binding for a foreign file without a `uid`; on a file system it is a heuristic and the
  leaf entry says so.
- **The content hash is the writer's.** The writer sees every byte it emits, so an XXH3-128
  (`System.IO.Hashing.XxHash128`) of the object costs nothing at write time and is recorded in the
  leaf entry. **No reader ever computes it.** `verify` does, offline, object by object. A foreign
  object is hashed once, by the indexer that first reads it whole anyway.
- **Pages are addressed by their XXH3-128** in every reference, and a commit object carries an
  XXH3-64 over its header and its table: integrity, deduplication and idempotent retries in one
  mechanism. Fragment segments and run segments carry an XXH3-64 each. A torn or corrupt page or
  header makes its version unreadable with the reason; a corrupt fragment segment makes its entry
  claim nothing for the blocks it covers; a corrupt data segment is a `VortexFormatException` as
  today. XXH3 guards against accident, not forgery.

**As delivered (step 20, the identity).** `File/FileIdentity`: the entry's value is a `Guid`'s
sixteen bytes, minted by `Guid.NewGuid()` unless `VortexWriteOptions.Identity` pins one, and written
right after the footer, so the postscript is the only thing between it and the EOF record. The
writer, the append and `VortexFileIndexer.AppendIndexesAsync` all end through one method,
`VortexFileWriter.WriteEndAsync`, which also writes the postscript from the builder's rented buffer
(`FlatBufferBuilder.FinishMemory`) instead of a copy: that saving pays for the entry, and a write
allocates 144 bytes less than before it. `VortexFile.Identity` reads the value from the retained
tail at every call, with no field and no request; an entry that is not sixteen bytes, or that
lies outside the tail, is no identity. `vxdump` prints it. What it costs on disk is 72 to 96 bytes
a file, not the 40 estimated: the value, the entry's key and segment tables in the postscript, and
the padding they move; the corpus rewrite goes from 0,6375 to 0,6425 of the reference's bytes. A
pinned identity makes two writes of the same batches byte-identical again, which is the form
11 §2's determinism and 10 §7.3's `Fastest` promise now take. A strict Rust 0.86.1 reader opens
every such file: the cross-check reads 854 of them.

**As delivered (step 26, the sidecar bound by identity).**
- **What the sidecar records.** Its directory records the file's length, its identity, the
  store's token (`fs:<length>:<modification ticks>` on a file system) and the XXH3-128 of its
  bytes, computed by the indexer. The SHA-256 is gone.
- **How a reader binds it.** It compares the length and the identity, both from the tail the open
  already read, so binding reads no byte of the file; a test counts the requests. A file without
  an identity is bound by the token, taken at an open by path when a sidecar is asked for. A touch
  breaks that binding, and so does an open that has no path; both are refused with the reason.
- **What no reader sees.** A byte changed in place keeps the length and the identity, so every
  reader check passes. `vxdump --sidecar P --verify` compares the recorded hash, reports the
  mismatch and exits with 6.
- **Scope.** The sidecar itself is retired by §6.4 when the dataset's fragments exist (step 42).
  This step moves its binding to the one this document keeps.
- **Since step 42d**, that binding is a fragment's (`IndexContainer`), and the check no reader
  makes is public: `VortexFile.VerifyIndexesAsync()`, which `vxdump --fragment P --verify` calls.

**As delivered (step 21, the checksums of the file's own indexes).** The in-file index directory is
version 2 ([10-indexes.md](10-indexes.md) §4.1): an XXH3-64 trailer over the directory, and an
XXH3-64 per payload region, computed by the writer from the blob it holds. A reader verifies what it
reads and nothing else; a region that fails claims nothing, and a key source that needs it refuses.
The fragments and commit objects of §3 will carry the same per-region checksum when they exist.

## 8. The commit protocol

### 8.1 A commit is one conditional creation

A writer reads the latest commit `N`, writes its data objects under fresh `uid` keys, then creates
`commit/<inverted N+1>`, header, pages and fragments in one object, with `PutIfAbsent`. On a file
system: temporary file, flush, create the final name without overwriting, flush the directory.
Put-if-absent linearises commits; no lease, no lock, no external service. Under latency a commit
is **three dependent requests**: the `List`, the read of `N`'s header, the creation; the data
objects were uploaded before, in parallel, and cost the commit nothing.

### 8.2 Rebase by re-applying operations

If `N+1` exists, the writer reads it and **re-applies its logical operations** to that version's
trees: add objects, add fragments, replace objects by compaction outputs, drop fragments. It never
merges two trees. Every page of `N+1` the operations leave unchanged is referenced where it lies,
and every page the writer re-chunks to a content it has already seen is referenced too, which the
prolly rule makes the common case in the regions the winner did not touch; the rest is O(depth)
pages in a new commit object. An iteration costs `depth + 2` dependent requests: the header of
`N+1`, the touched leaves, the creation. Contention is answered by a coordinator that batches
commits (§5.3), not by the tree.

| concurrent operations | result |
|---|---|
| append, append | both objects land in level 0; order by commit |
| append, compaction of level 0 | the compaction took a snapshot of ≤ 8 objects; the new append is not among them and stays in level 0 |
| indexer, indexer on the same `(object, range, entry)` | the second finds the fragment present in the winner's leaf, drops its own and writes nothing for it |
| indexer, compaction of that object | the fragment's object is gone; the fragment is dropped and never written |
| compaction, compaction on overlapping inputs | the second finds an input missing and abandons; its outputs are garbage |
| vacuum, anything | vacuum deletes only what no retained root references and what is older than the window (§10) |
| reader, anything | the reader holds a root; everything it references is immutable |

Every case is a row of the rebase matrix test (§14).

*Rows 3 and 4 with real fragments since step 42b: the second indexer's commit object holds no
fragment bytes, which the test reads back; and a fragment is its content, so "present in the
winner's leaf" means the same bytes, wherever the winner wrote them (§6.4).*

**As delivered (step 40): what the fuzzer found in row 1.** "Both objects land in level 0" was true
of the intention and false of the bytes. A tree's keys are unique, and an object's key was what
orders it — its first row position, or its smallest clustering key — computed by the writer from the
version *it* had read. Two handles on one dataset read the same version, computed the same position
and produced the same key, and the second commit did not add an object: it **replaced** the first
one's entry. Ten appends left six objects, and nothing said so.

The key now ends with the object's `uid`. That makes it unique, and — the half that is easy to miss
— it makes it a function of the **object** rather than of the tree, which §8.2 requires of anything
it re-applies: a store that crashed after its put holds the object under the key the first attempt
chose, so a suffix recomputed against the winner would add the object a second time. What it costs
is the order between two objects whose prefixes are equal, which is then their uids' order rather
than the commit order this table names. Under a clustering key that is a tie between equal minima.
Without one it is two concurrent appends that believed they started at the same row, and they did.

**As delivered (step 38).** `DatasetCommitter.CommitAsync` is the loop above: `List`, open the
winner, re-apply, `PutIfAbsent`, repeat. The operations are the four this section names
(`AddObject`, `AddFragment`, `DropFragment`, `ReplaceObjects`) and each carries its own answer to a
moved ground, so the matrix's rows are branches you can point at: an add whose `uid` is already
there is `AlreadyThere`, a fragment whose object is gone or whose `uid` changed is `Dropped`, a
replacement whose input is missing is `Abandoned`. A writer that loses `MaxAttempts` times is told
to use a coordinator rather than to try harder, in the exception's own words.

Two measurements, on the counting store:

- **An uncontended commit is three dependent requests**, as §8.1 says: the `List`, the header, the
  creation.
- **A rebase iteration is three as well, not `depth + 2`**, because §3's inlining pays for itself: a
  commit's header carries the pages it wrote **and the ones it had read on the way**, which are the
  levels above the leaves, so the next commit's descent finds them in a header it has already read.
  On a tree of 400 objects and depth 3, a commit after another makes **one** ranged read.

Two decisions the prose did not settle:

- **A page reference's offset is relative to its object's pages region**, not absolute. An internal
  page holds its children's references, so an absolute offset would have to exist before the page's
  bytes do, and the page's bytes decide the header's length, which decides where the pages begin.
  Relative offsets cut that circle and buy history independence in the byte sense: two identical
  trees written into two commit objects are identical page for page. A reader adds the region's
  start, which is in the object's first sixteen bytes and is remembered per version.
- **A commit inlines what it already holds and never reads a page to inline it.** Reading one to
  make the next commit cheaper would trade the thing §8.1 counts for the thing it does not.

### 8.3 Finding the latest version in one request

Commit keys sort newest first (§3), so `List("commit/", max: 1)` is the whole discovery. It needs a
strongly consistent listing, which S3 has had since 2020 and which §11 requires of any store. A
reader that wants read-your-writes is handed the version by the writer. There is no hint object and
there is no probe loop; there is no mutable object at all.

## 9. The read-path budget

### 9.1 Dependent round trips, not bytes

On an object store a request costs 10 to 100 ms whatever its size below a megabyte, so the budget
is **dependent round trips**. Cold, a point lookup on the clustering key through a sorted run:

| step | dependent requests | cacheable |
|---|---|---|
| list the latest commit, read its header | 2 | the header, by version |
| descend one level's tree | 0 up to ~650 objects, 1 up to ~400 000, 2 up to ~280 million (§4.1) | pages, forever: they are immutable |
| open the object: the tail | 1 per object touched, ≤ 8 + L, in parallel | the tail, by `(key, token)` |
| fences, then the two payload segments | 1 + 1 | fences, by run |
| the data segments | 1, coalesced | no |

About **6 to 8 cold, 2 to 3 warm**, for any number of rows and objects. Latency does not choose
the structure, it sets the parameters and the layout, and every lever below applies to either
tree of §13.J:

- **fat pages**: 256 KiB cost 2 to 3 ms of transfer on a 30 ms request, and fan-out is what
  latency turns into depth;
- **the header inlines the top of every level**, so the first descent is free;
- **one object per commit**, so the path to a recently changed region is one ranged read;
- **an in-process cache** of headers, pages, tails and fence pages, keyed by object key and
  offset, with no invalidation to get wrong since everything is immutable;
  `VortexOpenOptions.PreloadIndexes` fills it with the level pages at open;
- **parallel requests** wherever they are independent: the objects a lookup touches, the children
  of a node during an ordered walk;
- **hedged requests**, a duplicate sent at the p95 with the first answer kept, which belong to
  the store library and not here: the tail of S3's latency distribution must not be multiplied by
  the depth.

### 9.2 Two invariants, asserted by a counting store

With the fan-outs and page sizes fixed:

1. **A clustering-key point lookup costs at most R requests and B bytes**, constants of the design,
   asserted **equal** across data objects of 1 GiB, 10 GiB and 100 GiB (sparse, only the bytes read
   matter) and across datasets of 1, 10³ and 10⁶ objects (synthetic leaves).
2. **An equality lookup through a Bloom or a sorted run on a non-clustering column costs at most
   R′ requests plus a term proportional to the objects the summaries could not refute**, and the
   test asserts the constant part across the same matrix with a selective key.
3. **The dependent requests are the critical path.** The in-memory store injects a latency λ and
   no CPU cost; a cold clustering-key lookup completes within `D × λ`, `D` the count of §9.1,
   which no total of requests can prove, since parallel requests hide in a total.

**As delivered (step 27), invariant 1 on one file.**
- **The counter.** `Vorticity.IO.CountingSegmentSource` counts what a reader asks: one request
  per single read, one per set of ranges read together, and the ranges and bytes.
- **The two files.** `ReadBudgetTests` writes the same 200 000 rows twice, with a hole after
  every write of 32 KiB or more: 48 MiB for a 1.31 GiB file, 480 MiB for a 13.1 GiB one, both
  sparse. The holes follow the data and the filter leaves, never the index sections. Those sections
  end up between 2²⁸ and 2³⁵ in both files, so a fence page's varint offsets take the same bytes.
- **The lookup.** It opens the file, counts a key through a sorted run in fence pages, scans a
  tenant through a filter tree, seeks a key cursor, and asks the roots. It costs the same **13
  requests and 415 811 bytes** on both files. With the indexes in an identity-bound sidecar, the
  data file sees 3 requests and 114 807 bytes on both.
- **What it catches.** A whole-file hash put back into the sidecar's binding (the SHA-256 before
  step 26) turns that into 1 157 against 11 525 requests, and the test fails.
- **What it does not prove.** The files differ in length, not in rows. A file of ten times the
  rows reads a larger footer and zone map: those are O(chunks) by the Vortex format, and bounding
  them is the dataset's job.

**As delivered (step 40), invariants 1 and 3 over a dataset.** The counting store measures a cold
clustering-key lookup — the `List`, the header, the descent — across datasets of 1, 10³ and 10⁵
objects, at the chunker's own defaults and at §4.1's own worked assumption of ~200-byte entries, so
that its "fan-out is about 650" is the thing under test rather than a smaller cap chosen for the
test's convenience:

| objects | depth | requests | dependent steps | pages read below the header |
|---|---|---|---|---|
| 1 | 1 | 2 | 2 | 0 |
| 1 000 | 2 | 2 | 2 | 0 |
| 100 000 | 2 | 3 | 3 | 1 |

A second lookup **on the same key** costs nothing: the pages are immutable and the source keeps
them. A second lookup on a *different* key is not warm, and the test says so — the cache holds what
was read, not what could have been.

Invariant 3 is the one no count can prove, so it reads a clock: with the in-memory store's latency
at 20 ms, the same lookup waits for its round trips one after another. The assertion is a **lower**
bound only. An upper bound would be a wall-clock ceiling in the test suite, which this repository
keeps out of CI; what it would buy is proof that parallel requests do not add up, and a descent of
three sequential requests has none to hide.

**And the write side, which §4.3 states and §8.1 counts.** One object added to a dataset of 1, 10³
and 10⁵ objects costs **3, 3 and 4** dependent steps. Both of the spec's numbers are right and §3's
inlining is what decides which: while the header carries the pages the commit will touch, the
touched leaves cost nothing and §8.1's three holds; once the tree outgrows the 192 KiB the header
inlines, the touched leaf is a read of its own and §8.2's `depth + 2` shows through. Neither is a
function of the object count, which is the claim.

10 §8.1.8's "identical bytes on 1 GiB and 10 GiB" was right in intent and too strong in form: a
tree reads a page more at each order of magnitude. The constants hold at a fixed page cap, which
makes the pages' share of B exactly `depth × 256 KiB` thanks to the forced boundary; the axis it
lacked, the number of objects, is the one that matters most.

## 10. Lifecycle

- **Vacuum** walks the trees of every commit inside the retention window and marks the data
  objects and the commit objects their page and fragment references point into, then deletes the
  unmarked data objects whose store timestamp is older than the window, and the unmarked commit
  objects older than the window, keeping the latest. Marking reads O(pages), a factor of the
  fan-out below the objects, and it is the one linear operation in the design, offline, by choice.
  An orphan data object younger than the window is a writer in flight and is kept: **a writer must
  commit within the window**, which is the rule Delta states.
- **Repack**, optional: when vacuum finds a commit object kept alive by a few live pages among
  many dead ones, a metadata-only commit rewrites those pages into itself, so the old object can
  go at the next pass. A threshold on the dead ratio triggers it.
- **Retention**: 7 days by default, a dataset setting, and vacuum is explicit, never automatic. A
  reader that outlives the window may lose an object; it reports that, never a wrong answer.
- **Verify**: hashes every object against the leaf entries, walks the page hashes, checks every
  fragment segment; offline; the only reader of the content hash. Between two versions it is
  incremental, by the diff of their trees (§13.J), whatever their lineage.
- **Rebuild** an index: drop the entry's fragments in a commit and index again; or rewrite the
  object, which embeds it.

## 11. The store abstraction

`IObjectStore`, in the `Vorticity.Dataset` package so that the S3 library can reference it:

| operation | used by |
|---|---|
| `GetRange(key, offset, length) → bytes + token` | every read; the reader's `ISegmentSource` over a data object is an adapter on it, with 03 §3.5's coalescing |
| `Head(key) → size + token` | open |
| `PutIfAbsent(key, bytes) → created \| exists` | every write: data objects, commit objects |
| `Delete(key)` | vacuum |
| `List(prefix, startAfter, max) → keys` | one call at open (§8.3); vacuum |

Requirements a store must document: atomic `PutIfAbsent`; a strongly consistent `List`; a token
that changes whenever the bytes under a key change; ranged `GetRange` on objects of any size. What
a store library should do and this library never will: retries, hedged requests, connection
pooling, credentials. Three implementations ship here: the file system, an in-memory store with
injectable latency, failures and crashes for the tests, and a **counting** decorator for §9.2 that
also records the critical path. Data objects are written through the store by a forward-only
`ISegmentSink` adapter (03 §3.8), which is what lets the S3 library use a multipart upload.

**As delivered (step 35).** `Vorticity.Dataset`, a 0.x package that references the core and that
the core knows nothing about. `IObjectStore` is the five operations above; `ObjectRange` carries a
read's bytes **and** the object's token in one answer, because §7 binds what a reader believes to
the object it read and two calls prove nothing. Keys are checked in one place (`ObjectKey`), by the
file store's rules — no leading or trailing slash, no `..`, no empty segment, printable ASCII
without a backslash — so what the memory store accepts is what the file store can represent.

| what | how, and what it is for |
| --- | --- |
| `MemoryObjectStore` | a sorted map under one lock, plus the three injections §11 asks for: `Latency` (per operation, so requests issued together overlap and requests issued in a row do not), `Fails` (throws, changes nothing) and `CrashesAfterPut` (**stores the object, then throws** — the state §8.2's rebase exists for, where the key is taken by bytes the writer never learned it wrote) |
| `FileObjectStore` | `FileMode.CreateNew` is the atomic `PutIfAbsent`: the kernel creates the file or says someone else did. The content is not atomic — a process that dies mid-write leaves a short object — and the format detects that (§3's XXH3-64, the postscript), so the store says what it does rather than claiming more. Its token is the length and the last-write time |
| `CountingObjectStore` | counts per operation, bytes read and written (a refused put's bytes counted: they crossed the seam), keys listed, and **`DependentSteps`** — an operation that starts while none is in flight begins a new step. That is §9.2's critical path, and it is the number a total of requests cannot give |
| `ObjectSegmentSource` | an `ISegmentSource` over one object, planning its reads with the core's own `SegmentCoalescer` and issuing the runs **together**, so a split of forty segments is one step and not forty. It remembers the object's token and refuses a read whose token differs: the object changed under the reader |
| `ObjectSegmentSink` | buffers and creates the object in `CommitAsync`, whose answer (`Created` or `Exists`) **is** the outcome of a commit (§8.1). It outlives its writer on purpose: `VortexFileWriter` disposes the sink it was handed, and the file is not complete until that dispose returns, so a dispose here closes the sink and keeps the bytes |

The contract is a test suite, not prose: `ObjectStoreContractTests` takes a factory and runs the
same ten cases against both stores — atomicity under sixteen concurrent writers, a token that
changes when a key is deleted and created again, ordinal listing and its paging, a range clamped to
the object and refused past its end, an empty object, a 4 MiB object read in pieces, and every key
shape that could escape the store. An S3 library runs it against its own store.

## 12. What stays, what goes

| | today | 10 §8.1 | here |
|---|---|---|---|
| in-file indexes at write time | yes | yes, as embedded fragments | yes; **one run per entry** (§6.1), filter tree (§6.2), fences (§6.3), a checksum per segment |
| in-place append | yes | removed | **kept**, single-file mode, K runs per entry; the reader falls back to `previous_eof` on a torn tail instead of failing |
| post-hoc indexing by tail rewrite | yes | removed | kept on file systems: the index travels with the file |
| sidecar | one file, bound by SHA-256 | removed | one fragment in one commit object; the word goes |
| directory fields `file_length`, `file_sha256`, `array_encodings` | in the sidecar's directory | removed | `file_sha256` goes; the encoding table and the dtype move into the fragment footer |
| manifest | none | a flat list per version, O(N) | a prolly tree per level, O(log N); one commit object per version, its header ≤ 256 KiB |
| latest version | none | hint + probes | one `List`, newest-first keys |
| compaction | rewrite the file | "background, thresholds" | the invariant of §5.2, part of the read bound, priced in §5.4 |
| identity | none | `uid` + store token | `uid` per postscript write, store token, writer-computed XXH3-128 |
| S3 | none | a client in the repository | **not this library**; `IObjectStore` only |

**As delivered (step 25): the torn-tail fallback.**
- **When it applies.** `VortexFile.OpenAsync` falls back when the tail does not parse (a
  `VortexFormatException`) and the file begins with the Vortex magic. It then walks back from the
  end for an end-of-file record whose prefix opens, which is `VortexFileRepair`'s walk, and opens
  that prefix as the file.
- **What it says.** `VortexFile.TornTail` gives the file's length, the version's length and the
  tail's error. The version reads as itself: rows, statistics, indexes, `FileLength`. `vxdump`
  prints the tear. The field lives in a weak table beside the file, so a whole file pays nothing
  for it.
- **What it costs.** A whole file opens with no read more. A file that is not Vortex is refused
  after one more read of four bytes. The walk reads in proportion to the torn bytes; it is a
  recovery path, not a read path.
- **What it refuses.** `VortexOpenOptions.TornTail = Refuse` keeps the failure, and so does a file
  with no whole version before its tail. An in-place append, an in-place indexing pass and a
  sidecar all refuse a torn file and name `VortexFileRepair.RepairAsync`: nothing is written
  behind garbage. Since step 42d the sidecar's place is taken by `BuildFragmentAsync`, which
  refuses a file opened at its previous version the same way.
- **What it does not use.** The new directory's `previous_eof` is never read: it is in the torn
  bytes. The walk finds the old end-of-file record, which the append never overwrote.

## 13. The algorithms considered

### A. Metadata structure

| option | read | commit | verdict |
|---|---|---|---|
| flat manifest per version (Delta, 10 §8.1) | O(N) | O(N) | no |
| checkpoint + deltas (Delta) | O(N) at checkpoint, O(commits) replay | O(1) | no: the checkpoint is still the list |
| two levels, manifest list → manifests (Iceberg) | O(N / page) | O(N / page) | no, asymptotically; fine to ~10⁶ files, not a bound |
| B+tree with path copying | O(log N) | O(log N) | yes, fallback |
| **prolly tree** (Noms, Dolt) | O(log N) | O(log N) | **yes**: history-independent, so two writers converge on identical pages, dedup is free and rebase never merges trees |
| external catalog (DynamoDB, a database) | O(1) | O(1) | no: a service dependency, and the hard part moves, it does not vanish |

### B. Latest version

| option | cost | verdict |
|---|---|---|
| hint object + `HEAD` probes (10 §8.1) | O(lag), a mutable object | no |
| mutable pointer updated by compare-and-swap | O(1) | acceptable as a hint, needs `If-Match`; not chosen because a mutable object is one more thing to reason about |
| **newest-first keys + `List(max: 1)`** | O(1), no mutable object | **yes**; requires a consistent listing, which §11 requires anyway |

### C. Concurrency

| option | verdict |
|---|---|
| **put-if-absent on the commit object, rebase by re-applying operations** | **yes**; linearisable commits, no lease |
| leases or lock files | no on object stores; a lock file only for the single-file local mode's writer |
| three-way merge of manifests | no: operations commute or one loses, a tree merge is never needed |

### D. Data organisation across objects

| option | lookup bound | write amplification | verdict |
|---|---|---|---|
| no compaction (10 §8.1 in practice) | O(appends) | 1 | no |
| size-tiered (Cassandra STCS, Lucene tiered) | O(L) objects, all probed, summaries prune | ~L | **default without a clustering key** |
| **leveled by clustering key** (RocksDB) | ≤ 1 object per level, 8 + L in all | ~F/2 · L | **default with a clustering key**; the only policy that bounds the objects a lookup touches |
| partitioning by a partition key (Hive) | a prefix of the tree's key | none | subsumed: a partition key is a prefix of the clustering key |
| a global secondary index over the dataset | O(log N) | rewritten at every compaction | no: the per-object index moves with the object, the tree over objects does the rest |

### E. Skipping indexes

| option | verdict |
|---|---|
| flat per-block filter table (today) | no: O(blocks) per probe |
| **filter tree, fan-out 16, children contiguous** | **yes** (§6.2) |
| XOR or ribbon filters instead of SBBF | 15 to 25 % smaller, static, fine for immutable fragments; not now: SBBF is bit-identical to upstream's and the bytes are not the bound |
| node-level Bloom pointers in tree summaries | yes, when the policy asks for a file-level filter (10 §5.4) |

### F. Locating indexes

| option | verdict |
|---|---|
| one run per chunk (today) | no: O(chunks) on uncorrelated keys |
| **merge at `CompleteAsync` from spilled chunk runs; K in flux** | **yes** (§6.1); memory bounded, sink forward-only |
| hash → rows (10 §6.3) | O(1) probe, no ranges; not now |
| learned or interpolation indexes | no: no worst-case bound |
| a global key → (object, row) index | no, see D |

### G. Binding and integrity

| option | verdict |
|---|---|
| content hash on the read path | no |
| **`uid` per postscript write + store token; writer-computed XXH3-128 for `verify`; content-addressed pages; a checksum per segment** | **yes** (§7) |
| a checksum of the fragment footer only (10 §8.1) | no: the payload is what a lookup reads and what a zeroed filter corrupts |

### H. Index placement

| option | verdict |
|---|---|
| in-file only | no: rebuilding or adding an index rewrites the data |
| external only | no: the index no longer travels with the file, and the single-file mode dies |
| **both; external is transient and folds back at compaction** | **yes** (§6.4) |

### I. Point-by-point on 10 §8.1

| §8.1 said | here |
|---|---|
| a manifest lists the objects in row order with their fragments | a tree per level with summaries (§4); a flat list is O(N) on both paths |
| `manifest/_latest` hint then `HEAD` probes | newest-first keys, one `List` (§8.3) |
| one object per fragment, `index/<uuid>.vxix` | fragments inside the commit object, one ranged read each (§6.4) |
| an XXH3-64 of the fragment's footer | one per payload segment and per page (§7) |
| compaction "in the background, on thresholds"; a large object rewritten only on request | an invariant that the read bound depends on, with its price (§5) |
| "identical bytes at 1 GiB and 10 GiB" | constants R and B at fixed fan-out, across sizes **and object counts** (§9.2) |
| remove in-place append | keep it as the single-file mode, under the same K-runs rule, torn-tail tolerant (§6.1) |
| a minimal S3 client in the repository | not this library; `IObjectStore` in the dataset package (§11) |
| full manifest under a megabyte, or checkpoint plus deltas | moot: pages are at most 256 KiB and the header is bounded |
| retention 24 h | 7 days, explicit vacuum, orphans younger than the window kept (§10) |
| Iceberg or Delta as the manifest layer | not available: Iceberg's data formats are Parquet, ORC and Avro, Delta's is Parquet; a Vortex data file would make the table unreadable to every other engine, which is the only reason to adopt them. Lance is the closest prior art and its manifest is a flat fragment list, O(N) as well |
| identity written in every file | yes, as a version token (§7) |

### J. Prolly tree against B+tree with path copying

Once the commit is a sorted batch per level, the two share one algorithm and differ by the
boundary rule alone (§4.1). What follows from that rule:

| characteristic | B+tree, path copying | prolly tree |
|---|---|---|
| lookup | O(log_B N) pages, B deterministic | O(log_B N) pages, B expected |
| point write | `depth` pages, +1 on a split | `depth` pages, plus one or two leaves when a boundary moves, rarely |
| range replacement, the compaction case | the range's leaves plus the path | the same, re-chunked until the old boundaries are met again |
| shape | depends on the history of operations | a pure function of the key set and the parameters |
| page size | between B/2 and B; ~69 % full after random inserts, ~100 % after appends | around the target, between the min and the forced max |
| worst case | deterministic | probabilistic, clamped by min and max; adversarial keys only saturate the clamps, and the seed is the dataset's |
| deduplication across writers and lineages | no | yes: the same set gives the same pages |
| diff between two versions | O(Δ log N) inside one lineage, by shared pages | O(Δ log N) always, across datasets too |
| equality of two datasets | a full walk | one root hash |
| test oracle | invariants and equality of entry sets | a rebuild from scratch, byte for byte |
| coupling to the format | the page layout | the layout plus the hash, the seed, the min, the target and the max, all in the header |
| prior art | every database; LMDB and btrfs for copy-on-write | Noms, Dolt; the Merkle Search Tree of Bluesky's repositories is a cousin |

At this design's parameters, entries of ~200 bytes and a 256 KiB cap:

| | B+tree | prolly, 64 / 128 / 256 KiB |
|---|---|---|
| mean fan-out | ~900 after random inserts, ~1 300 after appends | ~650 |
| dependent page reads after the header, for 10³ / 10⁶ / 10⁹ objects | 0 / 1 / 2 | 0 / 1 / 2 |
| pages written per point commit | `depth`, +1 every ~1 000 | `depth`, +1 in a minority of cases |
| bytes read per descent, bound | `depth` × 256 KiB | `depth` × 256 KiB |
| bytes read per descent, typical | `depth` × 180 KiB | `depth` × 130 KiB |

**Impacts on this project.** The same code volume, 500 to 800 lines, once the batch form is
chosen; the prolly adds a chunker of about a hundred lines and removes every rebalancing path. Its
oracle is the strongest a test can have: incremental edits equal a rebuild, byte for byte, and two
orders of the same operations give one root hash, so the interleaving fuzzer compares one hash for
the metadata layer instead of a scan. The rebase converges, and the pages it reuses are garbage it
does not make. `verify`, diff and replication between stores become incremental across lineages.
The costs: a less familiar structure; a canonical serialisation, which content addressing demands
of both anyway; the header carries the chunker's parameters, and changing them is a new tree
format, migrated by a rebuild. **Decided: prolly**, with the rule behind a seam so that the B+tree
fill rule is measured on the same bench, and reversing the decision costs a rebuild and nothing
else.

### K. Order

Both trees are ordered; §6.6 lists what the order of the clustering key buys, bounded, and states
that an order on any other column is output-sensitive. The alternatives for a second order, a
projection or a global secondary index, are weighed there and in D. What the question of sorting
decided beyond that is in §5.3: level 0 is sorted by nobody, and its mandatory run on the
clustering key is the order the compaction reads it in.

### L. Latency

A request to an object store costs 20 to 100 ms at the first byte whatever its size below a
megabyte, with a heavy tail, so the cost model is the number of **dependent** requests. Under it
the two trees make exactly as many: one per page level. Latency therefore does not choose the
structure; it sets the page cap, the inlining, the commit layout, the cache and the parallelism
of §9.1. Where the two still differ under latency:

- **fan-out**: at an equal cap the prolly holds ~30 % fewer entries per page, so it crosses a
  depth threshold about 2× earlier in objects, one step on a logarithmic scale and nothing at
  realistic sizes; for a full walk of the metadata layer, vacuum or verify, the B+tree makes ~30 %
  fewer parallel requests, in the background;
- **diff, replication and verify across lineages**: the one latency-bound job where they diverge.
  The prolly compares two trees hash by hash from the top and reads only the subtrees that differ,
  O(Δ log N) requests to synchronise a dataset to another bucket or region; the B+tree walks
  everything outside its own lineage. At 50 ms a request over millions of objects, seconds against
  hours;
- **rebase under contention**: `depth + 2` dependent requests per iteration for both; the prolly
  reuses more pages, which is less garbage, not fewer requests. Contention is answered by a
  coordinator that batches commits, not by either tree.

### M. The layout of the metadata objects

| option | verdict |
|---|---|
| one object per page (`tree/<hash>`), one per pack of fragments, one root | no: `depth` writes made parallel at best, `depth` reads that stay sequential, orphan pages after a crash, and a pack saves a request only when the page it holds is on the path |
| pages in a pack per commit, a separate root | the path of a recent region is one ranged read, but two creations per commit and a pack orphaned by a failed root |
| **one commit object: header with the inlined top levels, pages, fragments, table** | **yes**: one `PutIfAbsent`, no orphan but data objects, the header read like a file's tail, the recent path in one ranged read, older pages by reference into older commits; the cost is a commit object kept alive by a few live pages, which repack answers (§10) |

## 14. Tests before it is called done

- **The counting matrix** of §9.2: 3 object sizes × 3 object counts, the first two invariants;
  the third under the injected latency of the in-memory store, cold and warm.
  *As delivered (step 40): the object-count axis and invariant 3, in `DatasetBudgetTests`, with the
  table in §9.2. The object-SIZE axis is `ReadBudgetTests` (step 27) and stays there: it is a claim
  about one file's read path, which a dataset adds a constant to and cannot change.*
- **The interleaving fuzzer** over the in-memory store: seeded schedules of readers, writers,
  indexers, compactors and vacuum, crashes between any two store calls; every read equals a scan
  with indexes off on the same version; every retained root references only existing objects.
  *As delivered (step 40): `DatasetFuzzTests`. Each writer PREPARES its operations against the
  version it read and COMMITS them after a seeded number of other writers have moved the ground, so
  the staleness is the point rather than an accident; one commit in five crashes after its put and
  is retried. The oracle is a model of §8.2's rules over a dictionary, and the tree must equal it
  entry for entry — which is what caught the key collision recorded in §8.2. Vacuum and the
  compactor's policy are not scheduled because they do not exist yet (steps 41 and 43); their
  operations are, so the schedule drives those directly.*
- **The rebase matrix** of §8.2, every row.
- **The invariant of §5.2** after every compaction step of a randomised append stream; `Explain`
  reports the lag when it is violated on purpose.
  *As delivered (steps 41a and 41b): the lag in `DatasetLevelTests` — eleven objects against a
  ceiling of eight, reported as 3 and refused nowhere — and the invariant in
  `DatasetCompactionTests`. The stream appends a random count of shuffled keys at a random offset
  and drains at random moments, so level 0 is sometimes over its ceiling and sometimes empty; both
  halves of §5.2 are asserted after every step, not at the end. The seeded run does 16 compactions
  across 4 levels.*
- **Progressive indexing**: at every intermediate commit, answers equal those without indexes.
  *As delivered (step 42b): `DatasetIndexingTests` indexes four clustered objects in two halves
  each, one commit per half, and after every commit asks five questions with the indexes and the
  summaries on and off. The key-ordered read on the fragments' column is refused before the eighth
  commit and exact after it. The interleaving fuzzer schedules the real indexer too: two handles
  appending and indexing block ranges from stale views, every read after every step equal to the
  scan without indexes.*
- **Merge equivalence**: a run merged at `CompleteAsync` answers as the chunk runs did; a file
  appended in place `n` times answers as one written once, and holds ≤ K runs per entry.
- **The two tree oracles**: incremental edits against a rebuild from scratch, byte for byte, on
  randomised batches of adds, removes and descriptor updates; the same operations in two orders
  against one root hash. Both run for the prolly rule and, as invariants and entry-set equality
  only, for the B+tree rule behind the same seam, whose fan-out and pages per commit the bench
  records beside the prolly's.
- **Order**: `InKeyOrder` across levels equals the sorted scan; `ORDER BY x LIMIT k` through the
  summaries equals the first `k` of the full sort; a compaction reading level 0 through its runs
  produces the same object as one reading inputs sorted beforehand.
  *As delivered (step 41b): the third one. Four objects whose key ranges interleave modulo four,
  appended out of order, shuffled inside each object — so a compactor that concatenated instead of
  merging, or read file order instead of its inputs' runs, is caught on the first key. The oracle is
  the same rows sorted before a byte of them was written, into one file: the compaction's output is
  compared against it row by row, key and measure. The first two await `InKeyOrder` across objects
  (step 39d).*
  *Step 39d: the first two, in `DatasetKeyOrderTests`. `InKeyOrder` is read over the interleaved
  objects of level 0, over the key-disjoint level 1 a compaction made of them, and over both levels
  once level 0 is filled again; both directions, with the summaries and without, against the same
  rows sorted before they were written. `LIMIT k` is a consumer that stops, and the test counts the
  objects it opened.*
- **Tampering**: an object replaced out of band at equal size, a page, a root and a fragment torn
  at every byte, a fragment of another object.
- **Rust**: every data object, including compaction outputs, remains a plain file that 0.86.1
  reads; the cross-check gains compacted files.

## 15. Decisions left open

1. ~~**Prolly tree or B+tree.**~~ Decided the same day: prolly, §4.1 and §13.J; the boundary
   rule stays behind a seam so that the alternative is measured, not argued.
2. **The constants**: `F`, the level-0 cap, K, the fan-out of the filter tree, the summarised
   column count, the page min, target and max (64 / 128 / 256 KiB), the repack threshold. The
   values above are the defaults to measure against, not guesses to keep; the page cap in
   particular is measured under the injected latency of §9.2, not chosen for CPU.
3. **A clustering key without a declared one**: whether `Auto` may pick one from the statistics
   (a sorted column with a wide range). Recommended no: a clustering key is a contract the user
   states, since it decides the write amplification.
4. **Schema evolution**: the header records one schema; an object with another is refused.
   Adding a nullable column is the first case worth allowing, later.
5. **Deletes and updates**: an LSM by key makes an upsert natural (the newest level wins) and a
   delete a tombstone. Out of scope, and the design does not close the door.
6. **A commit coordinator** for contended writers, a process that batches rows from many producers
   into one commit. Outside this library; the protocol needs none to be correct (§8.2).

## 16. Staging

1. **In the core, now, small**: the `uid` per postscript write; the checksum per segment;
   the merge at `CompleteAsync` with K runs in flux, on files and on appends; the filter tree
   layout; hierarchical fences; the reader's `previous_eof` fallback. Each holds today's tests
   and adds its own; the directory moves to version 2. This alone makes a 10 GiB single object
   answer a point query in bounded requests, with the sidecar's hash gone.
2. **The `Vorticity.Dataset` package**: `IObjectStore`, the file-system and in-memory stores, the
   counting decorator with its critical path; the commit object and the prolly tree, the boundary
   rule behind its seam and the B+tree rule measured on the same bench before the package is
   called done; the commit protocol; a dataset of one object, then of many, in level 0 only, with
   the mandatory run on the clustering key; the fuzzer and the two oracles from day one.
3. **Compaction**: tiered, then leveled with a clustering key; the invariant test; the price
   measured against §5.4.
4. **Fragments in commit objects, the indexer by range, fragment compaction, vacuum, repack,
   verify.**
5. **The S3 library, elsewhere**, against `IObjectStore` and the counting matrix run for real.

## 17. Sources

RocksDB leveled and universal compaction (`rocksdb/wiki`, "Leveled Compaction", "Universal
Compaction"); O'Neil et al., *The log-structured merge-tree*, 1996; Noms and Dolt on prolly trees
(`attic-labs/noms` `doc/intro.md`, `dolthub` "Prolly trees"); Delta Lake protocol (`_last_checkpoint`,
`VACUUM` retention of 168 hours, the S3 commit story before conditional writes); Apache Iceberg
spec (`file_format` values, manifest column bounds; Puffin for packed blobs); Lance format
(`lance-format` manifests, fragments, indices bound to fragments); Amazon S3 conditional writes
(`If-None-Match` on `PutObject`, August 2024; `If-Match`, November 2024) and strong read-after-write
consistency (December 2020); Parquet `BloomFilter.md`; Graf and Lemire, *Xor filters*, 2020;
Dillinger and Walzer, *Ribbon filter*, 2021. Prolly trees: Noms `doc/intro.md` and the Dolt
articles on prolly trees and its `prolly` package; Auvolat and Taïani, *Merkle Search Trees*, 2019;
the AT Protocol repository specification, for the MST in production. Normalised content-defined
chunking: Xia et al., *FastCDC*, 2016. Hedged requests: Dean and Barroso, *The Tail at Scale*,
2013. Secondary orders as copies: ClickHouse projections, Lance.
