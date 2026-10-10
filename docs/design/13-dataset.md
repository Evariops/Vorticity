# Dataset: a log-structured tree of Vortex files

`Vorticity.Dataset`, experimental under `VX0001`, keeps a versioned table over an object store: rows
appended over time into plain Vortex files, compacted in the background, indexed progressively, and
read as one table at any version. [datasets.md](../guide/datasets.md) and
[dataset-maintenance.md](../guide/dataset-maintenance.md) show it in use.

The requirement is that no read-path cost grows with any N: not the bytes of data, not the number of
data objects, commits, appends since the last compaction, blocks of an object or index fragments.
Every bound is a constant of the design (a fan-out, a page size, a level count) times a logarithm,
plus the size of the answer. The write path pays instead, once and amortized, in compaction, and the
price is stated (see [the price](#54-the-price)).

Some things do not bend. Every data object is a plain Vortex file that a strict Rust 0.86.1 reader
opens, with this library's structures invisible to it. The core takes no third-party dependency. An
index is a hint unless it is proven complete (see [indexing after the
fact](10-indexes.md#7-building-appending-and-indexing-after-the-fact)). No interleaving of readers,
writers, indexers, compactions and vacuums produces a wrong answer. And the S3 client is not this
library's: another library implements the store interface ([the store
abstraction](#11-the-store-abstraction)), and this one never names S3 in code.

## 1. Where the N's hide, and the bound that replaces each

| N | the bound | mechanism |
|---|---|---|
| bytes of data | nothing on the read path | identity from bytes the open already reads. The content hash is the writer's, checked only offline ([identity](#7-identity-and-integrity)) |
| data objects | O(log N) pages read, at most 8 + L objects touched by a key lookup | a copy-on-write tree of objects with summaries per node ([the tree](#4-the-tree)), leveled compaction on a clustering key ([levels](#5-levels-and-compaction)) |
| commits | one `List` finds the latest, and a commit writes O(depth) pages in one object | newest-first keys ([the latest version](#83-the-latest-version-in-one-request)), path copying into one commit object ([commits](#43-commits-are-paths-written-as-one-object)) |
| appends since compaction | at most four runs per index entry per object, at most 8 objects in level 0 | one rule inside a file and across files ([the invariant](#52-the-invariant), [runs](#61-one-run-per-entry-per-object)) |
| blocks of an object | `1 + 16 · log₁₆ blocks` filter reads, output-sensitive | the filter tree ([the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)) |
| entries of a run | two fence pages up to 10¹² entries | fence pages ([the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)) |
| fragments | one ranged read each, inside the commit object that wrote it | [fragments](#64-fragments-in-commit-objects-transient) |
| distinct keys, on a Bloom filter | the structure is chosen by cardinality | [which structure](#65-which-structure-for-which-column) |
| rows deleted since compaction | a binary search over at most 1 KiB of runs per batch or step, and at most an eighth of an object's rows stepped over | a deletion vector per leaf entry, folded by compaction ([deleting rows](#12-deleting-and-updating-rows)) |

## 2. Principles

1. Objects are immutable, with one commit object per version. Nothing committed is rewritten, and a
   reader holding a version's header holds a snapshot.
2. Metadata is a tree, not a list. The set of data objects is a copy-on-write tree whose nodes carry
   summaries. Reading descends and committing copies a path, and neither is O(objects).
3. Fan-in is bounded everywhere: four runs per index entry per object, 8 objects in level 0, L levels,
   and one object per level for a clustering-key lookup. Compaction holds the bounds, and a plan
   reports when it lags.
4. Cost is output-sensitive or logarithmic, never proportional to the dataset. A predicate that
   matches everything reads everything, which is the answer's own size.
5. The write path pays: merges when a file completes, filter trees, fences and compaction, each
   stated as a write amplification.
6. Identity comes from bytes already read, and integrity from checksums of what is read. Pages are
   referenced by their XXH3-128, commit headers and index regions carry an XXH3-64, and nothing on
   the read path hashes data.
7. Derived data folds back into the data. A compaction that rewrites an object embeds its indexes, so
   external fragments are a transient layer between "written without an index" and "next rewritten".

## 3. Objects and keys

A dataset is a store prefix holding two kinds of immutable object:

| object | key | content |
|---|---|---|
| data object | `data/<uid>.vortex` | a plain Vortex file, its indexes embedded, its identity in its postscript ([identity](#7-identity-and-integrity)) |
| commit object | `commit/<inverted version>.vxc` | everything else one commit produced: a header, the tree pages it wrote, the index fragments it wrote, a table of both, and a trailer |

The commit object opens by its head, where a Vortex file opens by its tail: magic `VXCOMMIT`, a format
version, the header's length, the header (Protobuf), then pages and fragments in the order they were
added, the table, and a 32-byte trailer. The trailer holds the table's offset and length, the
object's own length, an XXH3-64 over the header and the table, and magic `VXCT`. Recording the length
turns any truncation into a stated error rather than a shorter object.

The header holds the version, its parent, the schema and the names its columns gave up (see [schema
evolution](#13-schema-evolution)), the clustering key, the write policy, the tree's chunking
parameters and seed, the compaction and retention settings, and, per level, where its last
round-robin compaction stopped and its top page inlined, with the pages below it as long as the
header stays within 192 KiB. One ranged read of the first 256 KiB opens a commit. The pages region
follows the header, so a page the commit wrote itself that ends inside those 256 KiB is not inlined,
since the read that opens the commit brings it back once, where inlined it would be written twice.
Nor is a leaf holding an object with marked rows (see [deleting rows](#12-deleting-and-updating-rows))
inlined, unless this commit wrote it: its bytes grow with every mark, and carried by every header they
would cost each commit what only the commits that change its level should pay. For every page it
names without carrying it (a level's top, or a child of a page it carries), the header records where
that page's version keeps its pages, as far as the writer knows, so a reader fetches such a page in
one request rather than two.

A page reference is a fixed 36 bytes: the version that wrote the page, its offset relative to that
object's pages region, its length, and its XXH3-128. A commit references every page it did not change
where it already lies, in an older commit object, and checks the hash on read. Fixed-width, relative
references are what let a header name offsets that depend on its own length, and what make two
identical trees identical page for page. There is no separate page object, no orphan page after a
crash, and one conditional creation per commit.

A reference names its page in every version that shares it, so a handle keeps what it has from one
version to the next: the pages it read from the store, evicted least recently used first within
`DatasetOptions.PageCacheBytes` (32 MiB by default), what the reads that opened its last sixteen
versions brought back of their pages regions, and where each version's pages start. A refresh or a
commit only asks the store again for the pages that changed. Pages are kept page by page only when
read from the store, and kept by version when a read brought them back with their commit, since most
of what a version wrote the next one rewrites, and a budget in bytes would fill up with those.

`<inverted version>` is `10²⁰ − 1 − version` in twenty digits, so the newest commit sorts first and
the first key of a listing of `commit/` is the latest version (see [the latest
version](#83-the-latest-version-in-one-request)). There is no mutable object anywhere.

An existing Vortex file becomes a leaf by import, without a copy: the store only grows by the commit
object. A process that lists `data/` and reads every file sees uncommitted and superseded objects, as
in every table format. The latest commit object is the only definition of the content.

## 4. The tree

### 4.1 A prolly tree per level

Each level ([levels](#5-levels-and-compaction)) is a copy-on-write search tree over data objects,
ordered by the clustering key when the dataset declares one and by first row position otherwise. It
is a prolly tree, the probabilistic B-tree of Noms and Dolt. Once a commit is a sorted batch of
changes per level, a prolly tree and a B+tree with path copying are the same algorithm (load the
touched leaves, merge the changes, emit them again, rebuild the nodes above up to the top), and only
the rule that places page boundaries differs. The prolly rule alone gives history independence,
deduplication across writers and the strongest test oracle available ([how it is
tested](#15-how-it-is-tested)).

A boundary is decided per entry from the XXH3-64 of the entry's key alone, seeded with sixteen random
bytes the dataset draws once, so values never move a boundary and an indexer that updates an object's
descriptor rewrites exactly `depth` pages. It is normalized by size: no boundary before 64 KiB of
entries, then a probability proportional to the room left, `k · entryBytes / (max − bytes)` with
`k = 2`, which puts the mean page at 128 KiB by construction, and a forced boundary at 256 KiB, the
page cap. After an edit, chunking continues until a new boundary meets an old one, so the tree is a
pure function of the key set and the parameters: the same objects committed in any order give
byte-identical pages and one root hash. The rule sits behind an interface whose other
implementation, a B+tree's fill factor, is measured on the same bench.

With entries of about 180 bytes the fan-out is about 680. The header inlines a level's top page and,
while they fit, the pages below it, so a lookup reads 0 dependent pages up to about 650 objects, 1 up
to about 400 000, and 2 up to about 280 million. A leaf with marked rows that another commit wrote
costs one more read, once (see [objects and keys](#3-objects-and-keys)).

### 4.2 What a node carries

A leaf entry describes one data object:

- its key: the row encoding of the object's smallest clustering key followed by its first row
  position, or the position alone without a clustering key, then the object's uid, which makes the key
  a function of the object rather than of the tree it was committed into (see
  [rebase](#82-rebase-by-re-applying-operations))
- its size, row count and identity: the uid in its postscript, the store's token when the store
  provides one, and the XXH3-128 the writer computed ([identity](#7-identity-and-integrity))
- summaries: per summarized column, its minimum, maximum and null count, taken from the object's own
  file statistics before its put, so they cost the append no request. The first 32 top-level columns
  are summarized by default (`DatasetOptions.SummaryColumns`), and the clustering key's always are
- its index descriptor: indexes embedded in the object, or per entry the fragments that cover it (see
  [fragments](#64-fragments-in-commit-objects-transient))
- its deletion vector, when a delete marked rows in it: their positions as sorted runs, placed after
  everything else the entry holds, so an entry without one keeps exactly the bytes it always had. Its
  row count is then the live rows, and its summaries become bounds only ([deleting
  rows](#12-deleting-and-updating-rows)). A vector takes at most 1 KiB, which reduces the fan-out of
  the page holding it until the object is next rewritten: a page of entries all at the cap holds
  about a hundred

An internal entry holds a page reference to a child, the sum of its rows, the union of its children's
summaries, and the tally of the objects under it: their bytes, the largest of them, the rows marked
deleted in them, the most index fragments any one of them carries, and the most any one of them has
marked, both as a share of its rows and as the bytes of its vector, which a compaction plans by (see
[what a compaction does](#53-what-a-compaction-does)). The union behaves like an intersection, which
is the one thing about it a reader must get right. A column only travels up when every child carries
it, and a bound only when every child has one, since keeping a bound because another child was silent
would prune a subtree that holds the answer. One inexact child makes the union inexact.

The walk skips a subtree whose union refutes the predicate without reading it. An object whose own
summaries refute it is not opened, and an object outside a row range is neither read nor counted,
thanks to the row sums at every level. The question is asked by the core's own pruner, the same one
a file's statistics use, so there is one implementation of [statistic
precision](08-semantics.md#1-statistic-precision-what-inexact-licenses) and two callers. Measured: a
filter inside one of eight objects opens one object with the summaries, and eight without, for the
same rows.

### 4.3 Commits are paths, written as one object

A commit is a sorted batch of changes per level: entries added, removed or updated. Adding an object
chunks its leaf again along with the pages above it, O(depth) pages, laid out inside the commit object
with the header and the fragments. Every page the commit did not change is referenced where it lies,
and a page whose new content equals one the writer has already read is referenced rather than
rewritten, which the prolly rule makes common after a rebase. One conditional creation commits it
([the commit protocol](#8-the-commit-protocol)), its cost does not depend on the number of objects,
and a crash leaves nothing to clean up except data objects.

## 5. Levels and compaction

### 5.1 Why levels are part of the read bound

Without a merge policy, the objects a lookup touches are the appends themselves. The bound of the
[principles](#2-principles) only holds if compaction maintains an invariant, so the invariant is
specified, and a plan (`PlanCompactionAsync`) reports every violation of it as a lag without opening
a data object. A lag is reported, never refused: compaction is the caller's background job, and a
library never stalls a writer.

### 5.2 The invariant

- Level 0 holds appended objects as they came, overlapping in key, and at most 8 of them.
- Levels 1 to L, when the dataset declares a clustering key, hold objects with disjoint key ranges
  within a level, so a lookup by key touches at most one object per level, 8 + L in all. This is
  leveled compaction as RocksDB does it, with Vortex files as the sorted tables. Level `i` holds
  `128 KiB × F^i`, with `F` = 10, and an object written into it targets `128 KiB × F^(i−1)`, capped
  at 4 MiB. The cap is what a delete rewrites ([deleting rows](#12-deleting-and-updating-rows)), and
  the capacities keep growing past it, so a level holds `F` times the one below whatever the size of
  its objects, and a dataset of `N` bytes spans about `log_F(N / 128 KiB)` levels. A level holding a
  single object is never over its size.
- Without a clustering key, levels are size tiers. A lookup touches every object the summaries do not
  refute, which is output-sensitive rather than bounded, and the plan says so.
- Inside every object, an index entry has at most four runs, the same rule as for a single file
  appended in place (see [the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)).

`CompactionOptions` carries the fan-out, the level 0 ceiling, the level 1 target, the object cap and a
cap on the number of levels. Under a level cap, the top level has no size bound and only grows, since
merging it into itself would rewrite key-disjoint objects into the same objects.

### 5.3 What a compaction does

A compaction reads `k` objects and writes one or more at the destination level's target: a k-way
merge of the inputs on the clustering key when there is one, and a concatenation otherwise. The
writer builds the outputs' embedded indexes once, dropping the inputs' fragments, so compaction is
also the index compaction of everything it touches.

Level 0 is not sorted, and nothing ever sorts it. An append arrives in any order, and the writer
streams with bounded memory ([11-write-strategy.md](11-write-strategy.md)). So the write policy
requires a sorted run on the clustering key in every object whose rows may be out of order (see [the
policy](10-indexes.md#6-the-policy)), and the compaction reads each input in key order through that
run (see [rows in key order](12-index-reads.md#5-rows-in-key-order)). The merge's inputs are ordered,
its outputs are sorted by construction, and the writer never has to learn to sort. The run is the
object's structure rather than a hint, so the index budget spares it. On a narrow table it is as large
as the column it orders, and a budget of a tenth of the data would refuse it on every object past a
mebibyte, the merge's outputs included.

An object whose rows come in key order carries no run when the key is a single integer column with
no nulls, as is the case for a merge's output or its rewrite ([deleting
rows](#12-deleting-and-updating-rows)). Its statistics say the column is sorted, and a sorted column
is the source a key cursor and a key-ordered read take before any run, with its zone map as the index,
so a run next to it would be bytes nobody reads. Sealing the object checks that a key cursor opens on
the column alone, so an object whose order was lost is refused rather than read as one without the
key.

Level 0 empties into the first level whose capacity can hold it, not always into level 1. A few
small commits of a few kilobytes merge into level 1, rewriting a level a few times their size rather
than the levels that hold the dataset. A load of gigabytes skips the levels it would only overflow,
and is written once rather than once per level on its way up. Nothing orders the levels among
themselves except their sizes: a row in any level is a row of the dataset, and each level above 0
only has to stay key-disjoint.

The merge emits windows, not rows. At each step the input holding the smallest key emits every row at
or below the smallest key the others hold, which is a contiguous window of its current batch, so the
cost is `k` comparisons per window. Rows are compared by their row encoding, the order the tree, the
runs and the seeks already use. An output is sealed at the first key change past its target, never
inside a key, since two outputs sharing a boundary value would overlap, and the destination inputs
are chosen against the union of the source range. Rows with a null key come last. A composite key
merges on its tuple, and a composite key whose columns hold nulls is refused, since its run holds no
such tuple. The rows written are checked against the inputs' count, because a rewrite that loses rows
is the one failure it must not have. Measured on four interleaved level 0 objects: 42 808 bytes in,
and one sorted object of 16 596 out.

The triggers, in order, are level 0 above its ceiling, a level above its size, an object whose marks
are due ([deleting rows](#12-deleting-and-updating-rows)), and then an entry with more than four
fragments, which is a compaction of index bytes only (see
[fragments](#64-fragments-in-commit-objects-transient)).

A plan descends the trees rather than reading them. A level's bytes are the sum of the tallies its top
page carries, and the header carries that page, or the handle keeps it when it is a leaf with marks.
The object a job pushes down from a level over its size sits at the end of one descent: the largest,
the default for a leveled dataset, found by following the largest tally at each page, or, under a
round robin (`CompactionOptions.Pick`), the first one past the key where the level's previous job
stopped, which the header records with the level, found by following the keys. The objects of the
level below that the job's key range meets, the objects with too many fragments and the one whose
marks are most due are found by walks that skip every subtree whose summary or tally rules it out. A
cold plan over 125 000 objects asks the store for two pages, where reading every leaf would ask for
124.

A tiered job concatenates a run of one level's objects with nothing else between them. Under a round
robin, which is a tiered dataset's default, it takes the run that starts past the level's pointer and
ends before the first object another level holds past that start, one descent per level: a plan over
100 000 objects by arrival asks for one page, where reading every leaf would ask for 71. The longest
run (`CompactionPick.Largest`) depends on every level's order in full, so a plan that takes it still
reads every leaf, as does a plan over a level whose pages were written before tallies existed, until a
commit rewrites them. Both choose the job the other would. When many processes append, one
coordinator batching their rows into one commit solves the contention. Without it, the [commit
protocol](#8-the-commit-protocol) stays correct, only slower.

### 5.4 The price

Leveled compaction with `F` = 10 rewrites each row about `F/2` times per level it crosses, about 20 to
25× the appended bytes over four levels. A small commit crosses every level, and a bulk load only
those above the one it lands in. Tiered compaction rewrites each row about once per level, about L×.
The default is leveled when a clustering key is declared and tiered otherwise. A dataset that appends
1 TiB a day, leveled, writes about 25 TiB a day in compaction. That is the honest cost of the
one-object-per-level read bound, and every LSM store pays it.

Who pays it, and when, is the caller's decision. Compaction runs when `CompactAsync` is called, in a
loop any host runs (`RunCompactionAsync`, which plans, runs the job that is due at the rate it is
given, and sleeps when nothing is due), or, for level 0 under a byte budget, in the writer's own
commit (`DatasetOptions.InlineCompactionBytes`). [15-compaction.md](15-compaction.md) weighs those
drivers (inline in a commit, on demand, in the background), planning from the top of the tree, and
what a store that cannot delete does to the price.

## 6. Indexes at scale

### 6.1 One run per entry per object

An index entry has one run per object, merged when the object completes, and an append in place keeps
it at no more than four runs by merging past that (see [the directory and the
runs](10-indexes.md#3-the-directory-and-the-runs)). A lookup on a key uncorrelated with row order
therefore reads at most four fence pages per object, never one per chunk.

### 6.2 The filter tree and the fences

Bloom filters form a tree with a fan-out of 16, and long runs page their segment tables into fences
(see [the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)). A point probe reads at
most `1 + 16 · log₁₆ blocks` filters, and a lookup reads two fence pages before its payload, whatever
the object's size.

### 6.3 Checksums

Every page, fragment and index region carries its checksum ([identity](#7-identity-and-integrity)),
and a region that fails claims nothing. A reader still answers exactly without it, and `VerifyAsync`
names it.

### 6.4 Fragments, in commit objects, transient

A fragment is one entry's runs over a block range of one object, in the container the core builds for
a file it cannot append to (see [indexing after the
fact](10-indexes.md#7-building-appending-and-indexing-after-the-fact)), with its own encoding table
and dtypes so that it decodes without the object's footer. An indexer works by object and block
range, and each batch is one commit whose commit object carries the fragment's bytes. The leaf entry
references it by `(version, offset, length, XXH3-64)`, one ranged read. Coverage may be partial: a
pruner uses the covered blocks, and a key source waits for complete coverage, which the entry states,
so an object can be indexed progressively without ever answering wrong.

A fragment is identified by its content. Two indexers of the same range write one fragment, and
dropping a fragment removes the fragment with those bytes wherever it lies. Past four fragments, an
entry's fragments are bundled (the containers copied as they are, one after another, behind a table)
rather than merged, since a Bloom tree's upper filters cannot be rebuilt from its leaves and a run's
payloads hold offsets into their own container. A data compaction that rewrites the object embeds the
index and drops every fragment, so at steady state the external layer only holds the indexes of
objects nobody has rewritten yet.

### 6.5 Which structure for which column

| distinct keys | structure | point lookup |
|---|---|---|
| few, or dictionary-encoded | zone map and dictionary probe | one values child per chunk the zone map keeps |
| up to about 10⁵ per 16 blocks | Bloom filter tree | `1 + 16 · log₁₆ blocks` filters, output-sensitive |
| many, or a range or an order | one sorted run per object, with fences | two fence pages and two segments per object touched |
| the dataset's lookup key | the clustering key | one object per level, then its run: the only structure that bounds the objects |

`Auto` gives up a Bloom filter on a column whose first node exceeds a filter's capacity, where a
sorted run is the right structure (see [the policy](10-indexes.md#6-the-policy)).

### 6.6 Order beyond the lookup

The trees are ordered by the clustering key, so the order of that one key is bounded everywhere:

| need | mechanism | cost |
|---|---|---|
| walk the objects by key | an in-order walk of a level's tree, with the next pages prefetched eight ahead | O(log N + objects touched), about one dependent round trip per window of eight pages |
| rows in key order, `OrderBy` | a merge of each object's key-ordered read. Inside a level above 0 the objects are key-disjoint | at most 8 + L objects open at once |
| rows by position, `Rows(a, b)` | the row sums of every node | O(log N) plus the objects the range touches |
| count and rank on the clustering key | objects whose summaries lie wholly inside the range are counted unopened, and the core's exact cover handles the boundary objects | at most level 0's objects plus two per level opened |
| an order on another column | no tree helps | one cursor per object the summaries cannot refute |

An object opens when it could hold the next row, and not before. Objects are offered in the order of
a lower bound on their keys (on the clustering key, ascending, a leaf key is the object's exact
minimum), and one is only opened once that bound is at or below the smallest key an open input holds.
So a consumer that stops after `k` rows opens no object whose minimum lies past the k-th key, and
`LIMIT 10` opens one object of four. Measured on the same rows: four cursors over four interleaved
level 0 objects, and one over the key-disjoint level a compaction made of them. A float has no such
bound, since a zone's minimum and maximum exclude NaN while the key order places NaNs at the ends, so
float objects open before the first row, and an object whose first row lies below the bound it was
ordered by raises `VortexFormatException` rather than deliver a wrong order. Ties between objects
follow the dataset's order, so a descending read is the exact reverse of an ascending one.

A key cursor walks both ways. Walking down is the same merge with a max-heap. The objects of a level
above 0 are key-disjoint, so the minimum of the next object in the level is a strict upper bound on an
object's keys, and the object only opens once the walk passes below it. Level 0's objects, which
nothing but their summaries bounds from above, open at the seek, at most eight plus the lag. A step
against the walk's direction seeks every object again just past the current entry, whose place in the
order is its key and then its row in the dataset: an object before it in the dataset's order lands at
or before the key, one after it strictly before, and its own object steps. That is the change of
direction every merging iterator pays for (see [stepping and
direction](12-index-reads.md#32-stepping-and-direction)), and the walk down is the walk up reversed,
ties included.

A key cursor only reads the trees where it walks. In each level the walk stands between two objects,
a place found by one path down the level's tree and moved one object at a time, with a page read only
when the walk crosses into it. The pages read, and the objects' entries in them, are parsed once per
version and shared by its cursors. An object's first row is counted when the walk first takes one of
its entries, from the rows of every level's objects before its tree key, along one path of each. A
seek on a handle that holds nothing yet therefore reads the depth of each level, whatever the number
of objects: over 20 000 objects in one level it reads one page, where a listing of the objects would
read every leaf, 23 pages.

Any other order costs, at best, one cursor per object the summaries cannot refute. `ORDER BY x LIMIT
k` prunes by the summaries, which is good in practice and output-sensitive, but never bounded. A
second bounded order requires a second copy of the data clustered by that key, a dataset of its own.
A global secondary index is excluded, since every compaction would rewrite it.

## 7. Identity and integrity

- Every postscript this library writes mints a uid: sixteen random bytes in the metadata entry
  `vorticity.identity`, written right after the footer, so the open's tail read covers it and reading
  it costs no request. It identifies a version of the bytes, not a file: an append in place mints a
  new one, and a fragment bound to the old uid is refused. It costs 72 to 96 bytes per file, and
  `VortexWriteOptions.Identity` pins it for a reproducible write.
- The store's token (a version id or an entity tag, or, on a file system, the length and the
  modification time) is recorded when the store provides one and compared as an opaque string. It is
  the only binding for a foreign file without a uid. On a file system it is a heuristic, and the entry
  says so. `VortexOpenOptions.IndexFragmentsNeedIdentity` refuses a fragment bound that way.
- The content hash is the writer's. The writer sees every byte it emits, so an XXH3-128 of the object
  costs nothing at that point and is recorded in the leaf entry. No reader computes it, and only
  verification does, offline.
- Pages are addressed by their XXH3-128, and a commit object carries an XXH3-64 over its header and
  table, which gives integrity, deduplication and idempotent retries in one mechanism. A torn page or
  header makes its version unreadable, with the reason. A corrupt fragment or index region claims
  nothing. A corrupt data segment raises `VortexFormatException`. XXH3 guards against accidents, not
  forgery.

## 8. The commit protocol

### 8.1 A commit is one conditional creation

A writer reads the latest commit `N`, uploads its data objects under fresh uid keys, in parallel, then
creates `commit/<inverted N+1>` (header, pages and fragments in one object) with the store's
put-if-absent, streamed from a pipe whose length is given up front, so the store can choose between
one request and a multipart upload. Put-if-absent linearizes commits with no lease, no lock and no
external service.

Uncontended, a commit is three dependent requests: the listing, the read of `N`'s header, and the
creation. The data objects add nothing to the commit itself. A handle that knows its version was the
latest less than half a retention window ago (see [the latest
version](#83-the-latest-version-in-one-request)) builds on that version directly, so its commit is a
single request, the creation. A refusal tells it another writer went first, and it then asks for the
versions after the number it lost and rebases. Past that half window it lists first. Either way, it
reads the version it created as it wrote it, with nothing read back. A handle whose commit found
nothing to change reads the version that decision was made on, which is fetched first when the batch
was decided on the version it held, since a no-op names the latest version.

### 8.2 Rebase by re-applying operations

If `N+1` already exists, the writer reads it and applies its logical operations again on that
version's trees (add objects, add fragments, drop fragments, replace objects by a compaction's
outputs), and never merges two trees. Each operation carries its own answer to a moved ground:

| concurrent operations | result |
|---|---|
| append, append | both objects land in level 0 |
| append, compaction of level 0 | the compaction took a snapshot, so the new object is not among its inputs and stays |
| indexer, indexer on the same range | the second finds the same fragment already there and writes nothing |
| indexer, compaction of that object | the object is gone, so the fragment is dropped and never written |
| compaction, compaction on overlapping inputs | the second finds an input missing and abandons, and its outputs are garbage |
| vacuum, anything | vacuum only deletes what no retained version references and what is older than the window ([lifecycle](#10-lifecycle)) |
| delete or update, append | the appended object is not among those the change read, so it stays and its rows are not touched |
| delete or update, anything that rewrites or marks an object it read | an input is gone or its entry changed, so the replacement is abandoned and the change is worked out again on the version that won ([deleting rows](#12-deleting-and-updating-rows)) |
| compaction, delete or update that marks one of its inputs | the entry the compaction read changed, so it abandons, since its outputs would bring the marked rows back, and they are garbage |
| schema change, schema change | the second finds a different schema from the one it was checked against, and is dropped ([schema evolution](#13-schema-evolution)) |
| append, schema change | the object lands as it was written, and reads as the new schema like any earlier one ([schema evolution](#13-schema-evolution)) |
| reader, anything | the reader holds a version, and everything it references is immutable |

An object's key ends with its uid, which makes it unique and a function of the object. Without it,
two appenders that read the same version would compute the same key, and the second would replace the
first's entry, a case the interleaving fuzzer found. Because a commit header carries the pages it
wrote and the pages above them it read, one rebase iteration is also three dependent requests while
the tree fits in the header. A writer that loses too many times (`DatasetOptions.MaxAttempts`) is told
to use a coordinator, in the exception's own words.

### 8.3 The latest version in one request

Commit keys sort newest first, so the first key a listing of `commit/` yields is the whole discovery,
and the listing stops after one page. That needs a strongly consistent listing, which S3 has offered
since 2020 and which [the store abstraction](#11-the-store-abstraction) requires of any store. A
reader that wants to read its own writes is handed the version by its writer. There is no hint
object.

A handle that knows its version was the latest less than half a retention window ago asks for the
versions after it instead, with a head request each. Every one of them was created after that moment,
and vacuum only takes a commit a full window after the next one superseded it, so they are all still
there and the first missing one ends the run. Past that half window, the handle lists. Below the
window the commits are not contiguous: vacuum keeps an old commit object whose pages a retained
version still names and removes the unnamed ones after it, so a handle that took the gap above its
own version for the end would read it as the latest and commit on it. The handle's clock therefore
has to agree with the store's to within the other half window, as a vacuum's already must.

## 9. The read-path budget

### 9.1 Dependent round trips, not bytes

On an object store a request costs 10 to 100 ms whatever its size below a megabyte, so the budget is
counted in dependent round trips. Cold, a point lookup on the clustering key through a sorted run
costs:

| step | dependent requests | cached by |
|---|---|---|
| list the latest commit, read its header | 2 | version |
| descend a level's tree | 0 up to ~650 objects, 1 up to ~400 000, 2 up to ~280 million | page, across versions, since pages are immutable |
| open the object's tail | 1, in parallel across the ≤ 8 + L objects touched | object |
| its fences, then the two payload segments | 1 + 1 | run |
| the data segments | 1, coalesced | none |

That is about 6 to 8 requests cold and 2 to 3 warm, for any number of rows and objects. Latency does
not choose the structure. It sets the page cap, the inlining, the one-object commit, the in-process
cache of immutable headers, pages, tails and fences, and the parallel requests. Hedged requests belong
to the store library.

A scan in the tree's order opens the next object ahead once its consumer reads past the first batch
(see [the first batch of each source](16-queries.md#26-the-first-batch-of-each-source)), so an
object's tail and its first data request wait on each other rather than on the object before it, and
a scan that stops at its first batch opens a single object.

### 9.2 Held by a counting store

- For one file of any size, opening it and answering a count through a sorted run in fence pages, a
  scan through a filter tree, a key seek and the roots costs the same 13 requests and 415 811 bytes on
  a sparse 1.31 GiB file and on a sparse 13.1 GiB one (`ReadBudgetTests`). A content hash on the read
  path would turn that into 1 157 against 11 525 requests, and the test would fail.
- For any number of objects, a cold clustering-key lookup costs 2, 2 and 3 requests over datasets of
  1, 1 000 and 100 000 objects, and adding one object costs 3, 3 and 4 dependent steps
  (`DatasetBudgetTests`).
- On the critical path, with 20 ms of injected latency, the lookup waits for its round trips one after
  another, which no count of requests can show.

## 10. Lifecycle

- Vacuum (`VacuumAsync`, explicit, never automatic) marks everything the retained versions reference
  (a subtree already seen is not walked again, so the versions cost their distinct pages), then
  deletes the unmarked commit objects and data objects older than the retention window, seven days by
  default (`DatasetOptions.RetentionWindow`), always keeping the latest version. What ages is the
  moment a version was superseded, by the store's own clock. An unreferenced object younger than the
  window may belong to a writer in flight and is kept, which means a writer must commit within the
  window. Only `commit/`, `data/` and the compaction loops' `leases/` are swept, and an imported file
  belongs to its owner. On a store with retention locks, whatever the lock or a legal hold still keeps
  is left and reported with the date the first of it may go (`DatasetOptions.LockedStore`, see
  [stores that cannot delete](15-compaction.md#5-stores-that-cannot-delete)). A reader that outlives
  the window gets an `ObjectNotFoundException` naming the version it was reading, never a partial
  answer. A dry run reports without deleting.
- Repacking is internal for now. A commit object kept alive by a few live pages among dead ones is
  detected as sparse during vacuum, and a metadata-only commit can copy those pages and fragments
  into itself so the old object can go at the next vacuum, but neither the detection nor the repack
  is exposed in the public API yet.
- Verification (`VerifyAsync`, offline) checks every object's length, content hash, rows and
  identity, every fragment against its reference and its object, and every commit object whole. It
  reads the pages from the store, not from a header's inlined copies, which is the only way to see a
  torn stored page that a reader never reads. It reports every problem by name and throws none. Given
  an older version, it only walks the difference between the two trees, O(changed pages).
- Rebuilding an index drops an object's fragments and attaches one over the whole object in the same
  commit, so no version holds the object with neither. Rewriting the object, as a compaction does,
  embeds the index instead.

## 11. The store abstraction

`IObjectStore`, in the dataset package, is what an S3 library implements:

| operation | used by |
|---|---|
| `GetRangeAsync(key, offset, length)` returns the bytes and the object's token in one answer | every read. A data object's segment source plans its reads with the core's coalescer and issues them together |
| `HeadAsync(key)` returns the size, the token, the store's creation time, and the retention date and legal hold on a locked store | opening, vacuum's ages, and what a lock still keeps |
| `PutIfAbsentAsync(key, pipe, length)` returns created or exists | every write, streamed |
| `DeleteAsync(keys)` | vacuum, a batch at a time, where an absent key is not an error |
| `ListAsync(prefix, startAfter)` | the latest version, in one page ([the latest version](#83-the-latest-version-in-one-request)), and vacuum |

A store must document an atomic put-if-absent, a strongly consistent listing, a token that changes
whenever the bytes under a key change, and ranged reads on objects of any size. Three behaviours are
part of the interface: a put streams and creates nothing when its content ends early or runs long, a
delete takes a batch and reports nothing, and a listing pages by itself as it is enumerated. Retries,
hedging, connection pools and credentials belong to the store library, never to this one.

Three stores ship. `FileObjectStore` uses the kernel's create-new for put-if-absent, and a crash may
leave its content short, which the format detects. `MemoryObjectStore` has injectable latency,
failures and crashes after a put. `CountingObjectStore` is a decorator that counts requests, bytes and
the dependent steps of the critical path. The contract is a test suite, `ObjectStoreContractTests`,
which an S3 library runs against its own store.

## 12. Deleting and updating rows

A delete marks rows, and does not rewrite any object it can mark. It takes the rows its filter is true
for (a row it is false or unknown for stays, as a scan would not return it) and records them in the
leaf entry of each object that holds one, as a deletion vector: the sorted runs of the object's own
row positions it takes, two varints per run, placed after everything else the entry holds (see [what a
node carries](#42-what-a-node-carries)). The object's bytes do not change, so the commit writes the
pages on the path to the entries it changed and no data object. An object whose rows all go is removed
from the tree without being read, and one whose summaries refute the filter is not opened.

An update is a delete and an insert applied together. The rows it takes are marked where they were,
read as records, passed through the caller's function, and written into new level 0 objects, where
appended rows go, because a changed row may carry a different key. One `ReplaceObjects` commits the
whole change, so a reader sees all of it or none of it.

### When a delete rewrites instead

An object is rewritten without the rows, rather than marked, when the marks would cost its reads more
than they save the write. That happens under `MarkedObjectBytes` (a mebibyte), where a rewrite is a
few requests and leaves the object exact, when the rows marked in it would pass an eighth of its rows
(`MarkedShare`), since every read of it steps over them, and when its vector would pass 1 KiB
(`MarkedVectorBytes`), a few hundred runs. The vector is counted as the runs it will form: one run per
row when that already fits, and otherwise from the rows' positions, read before anything is written,
since the rows a range takes form one run whatever their number.

The vector lives in a leaf page, so a commit that marks a row in any object of a page writes that page
again, and the bounds weigh the rewrites the marks save against the bytes they add to each of those
commits. A commit that does not change the page leaves it out of its header (see [objects and
keys](#3-objects-and-keys)). A vector past 256 bytes lies out of line instead: it is written once into
the commit object that made it, like a fragment ([fragments](#64-fragments-in-commit-objects-transient)),
and its entry names it by reference, so the page carries 37 bytes for it whatever the marks. A read of
the object fetches it alongside the object's open, from where the version's pages lie or from what the
handle kept. Vacuum keeps the commit object it lies in.

The rows a rewritten object keeps are a subset of its rows, so its keys stay inside its old range: a
level above 0 stays key-disjoint and the tree's order holds, and without a clustering key the
rewritten object keeps the old one's position. Either way, rows are judged by the scan's own
evaluator, so a delete and a scan with the same filter agree on every row, nulls included, and a
rewrite checks that it kept exactly the rows the count left.

### What a read does with a vector

An entry's row count is its live rows, so a count without a filter and every position stay
arithmetic: a position maps to the object's own through the runs before it. The rest is the object's
own read with its marked rows left out, at the cost of a binary search over the runs per batch or per
step:

| read | with marked rows |
|---|---|
| a filtered count, an any | the object's count with the rows left out, tier by tier, in one pass: arithmetic on a sorted column's slices, a test of each row on an index's rows, the zone's rows minus the marked ones on a zone the maps decide whole, and the marked rows cleared before counting on a decode |
| rows in file order | a batch without a marked row is delivered as it came, renumbered, and one with some is gathered without them. An aggregation's batches stay whole, with the marked rows deselected and their columns still encoded |
| rows in key order, a key cursor | the key source steps over the marked rows' entries as it walks, and a rank subtracts the entries marked below it. That is arithmetic when the ranks are rows (a sorted column's, or the clustering run of a level above 0), and otherwise uses the marked rows' keys, read by one take when the cursor opens |
| a minimum, a maximum | on a column the object's statistics say is sorted and that holds no null, its first or last live row: the statistic while that row is the column's end, and one row read once a delete took it. Otherwise the object's extreme when a count finds a live row holding it, and its live rows read when none does |
| the summaries | an entry with marks keeps its bounds, which its live rows still lie inside, and drops what they no longer prove: exactness, a null count other than zero, a column left with no bound. They prune, but no longer answer |

### Compaction folds the marks

Compaction reads each input's live rows and writes objects without marks, so a vector only lasts until
its object is next compacted. A level no merge reaches, the top one above all, is purged instead. An
object whose marks reach half a delete's limits (a sixteenth of its rows or half a kilobyte of vector)
is rewritten alone, in its level and inside its range, by a job of its own
(`CompactionTrigger.Marks`), the most marked first, and the plan finds it by descending the shares and
vectors the tallies carry (see [what a compaction does](#53-what-a-compaction-does)). A delete that
would pass the limits only rewrites the object itself when compaction lags. A compaction that read an
object before a delete marked rows in it would bring those rows back, so its `ReplaceObjects` names
the entries it read (uid, key and vector) and is abandoned when one of them changed (see
[rebase](#82-rebase-by-re-applying-operations)).

### What it costs

The churn bench ([how it is tested](#15-how-it-is-tested)) measures it on ten million rows and thirty
thousand commits of ten rows at random keys. An update takes 1.9 to 2.9 ms and a delete 0.9 to 1.6 ms,
where rewriting the 4 MiB objects that hold the rows takes 19 and 14 ms. A commit writes 12 KiB at
first and 16 to 55 KiB once the vectors have filled, where a rewrite writes 3.3 MiB (the rows' own
objects), with a commit object of 8 to 10 KiB throughout, whose leaf page carries a reference for each
vector past 256 bytes. Compaction, merges and purges together, writes 61 to 111 KiB per commit. After
a thousand commits the store holds 137 MiB rather than 449 MiB, since the retention window keeps every
object a rewrite replaced. Reads cost what they cost on the rewritten dataset: a filter over every
object makes the same 220 requests, and a point lookup, a seek, a count under a key range and the
largest key stay where they were. What the marks keep is space, up to an eighth of an object's rows,
until its next compaction.

### Concurrent writers

The change is worked out on the version the handle holds. An append that lands first adds objects the
change never read, and their rows are not touched. A commit that removes, rewrites or marks an object
the change read (a compaction, another delete) makes the replacement abandon (see
[rebase](#82-rebase-by-re-applying-operations)), and the change is worked out again on the version
that won, up to `MaxAttempts` times. Whatever a lost attempt wrote is left for vacuum.

## 13. Schema evolution

A change of schema is a commit that changes the header's schema and rewrites no object. Per top-level
column, a change is one of the following: a nullable column added, a column dropped (unless the
clustering key uses it), a column renamed, a number widened within its kind (a signed integer to a
wider signed one, an unsigned to a wider unsigned, a float to a wider float), or a column made
nullable. Each keeps every value already written readable, and anything else is refused before the
commit. The clustering key's columns keep their names and types in every object, since they order the
objects and the trees.

Names are identities. An object's field belongs to the column with its name, or to the column a
rename moved that name to. The header records every name a column gave up, along with the column
that carries it now, or none when it was dropped, and no column ever takes one of those names again.
That is what makes names enough: an object written while a name was in use never lends its values to
another column, and the summaries its entry carries, keyed by the names it had, never describe
another column. A summary under a former name prunes nothing for the new one, which is conservative
and ends at the next rewrite.

When an object's dtype is the version's, as for every object written since the last change, its read
is its own scan, after one comparison of dtypes whose hashes are cached. Otherwise its columns are
mapped when it opens, and each batch its own scan delivers is reshaped in the batch's arena: a struct
over its fields in the dataset's order, a missing column filled with nulls, a column written
non-nullable declared nullable over the same buffers, and narrower numbers widened into a buffer of
the arena. The filter reaches the object in its own terms. It is renamed, and each predicate over a
column the object lacks is folded to what a null makes it (a comparison unknown, `IS NULL` true), so a
filter over an added column refutes every earlier object without a read. Under a negation an unknown
is not false, so a predicate that cannot be stated over the object's columns there is evaluated on the
reshaped batches instead. Key cursors, key-ordered reads, compaction, deletes and updates read through
the same mapping, and an object that lacks a cursor's column holds no entry.

Compaction folds the change into the data, as it folds indexes ([principles](#2-principles)): every
object it writes is in the current schema. Until then, reading an earlier object costs the reshaping
(records per batch, and a copy for a widened column), and reading any other object costs nothing
more.

The operation carries the schema it was checked against. A rebase onto a version with a different
schema drops it, since what it checked no longer holds, and the caller is told to refresh, while one
that finds its own schema already there is done. An append prepared under the earlier schema lands
after the change, and its object reads as the new schema like any other.

## 14. Alternatives weighed

| question | chosen | rejected, and why |
|---|---|---|
| metadata structure | a prolly tree per level | a flat manifest per version (O(N) per open and per commit), a checkpoint plus deltas (the checkpoint is still the list), a manifest list of manifests as in Iceberg (fine up to 10⁶ files, but not a bound), an external catalog (a service dependency) |
| prolly or B+tree | prolly | the same lookup and commit costs, but the prolly tree adds history independence, deduplication across writers, a one-hash equality of datasets, an incremental diff across lineages, and a rebuild-from-scratch oracle, for a chunker of about a hundred lines |
| the latest version | newest-first keys and one listing | a hint object and probes (O(lag), and a mutable object), a pointer updated by compare-and-swap (one more mutable thing) |
| concurrency | put-if-absent, rebase by operations | leases and lock files, three-way merges of trees |
| data across objects | leveled on a clustering key, tiered without | no compaction (O(appends)), a global secondary index (rewritten at every compaction) |
| index placement | in the file, with transient external fragments | in-file only (adding an index rewrites data), external only (the index no longer travels with the file) |
| deleting rows | a bounded deletion vector in the leaf entry, folded by compaction | copy-on-write of every object a row is taken from (an object per delete, 3 MiB per commit of ten rows, each kept through the retention window), positional delete files as in Iceberg (a read joins them, and they pile up until a rewrite), equality deletes (a read evaluates each of them against every row) |
| binding | uid and store token, with the writer's content hash for verification | a content hash on the read path (it reads the data) |
| metadata objects | one commit object per version | an object per page or per pack (sequential reads, orphans after a crash, two creations per commit) |
| Iceberg or Delta as the table layer | neither | their data formats are Parquet, ORC and Avro, and a Vortex data file would make the table unreadable to every engine that reads them, which is the only reason to adopt them |

Not in this design: a clustering key picked automatically, since it decides the write amplification
and is the user's to state, and evolving the fields of a nested struct, or changing a column's type
other than by widening a number or making it nullable ([schema evolution](#13-schema-evolution)).

## 15. How it is tested

- The counting matrix of [the read-path budget](#92-held-by-a-counting-store).
- An interleaving fuzzer over the in-memory store: readers, writers, indexers, compactions and vacuums
  in seeded schedules, with commits prepared on a stale version and crashes after a put. Every read
  must equal a scan with indexes and summaries off on the same version, and every version a vacuum
  retains must verify whole.
- The rebase matrix, covering every row of the [rebase table](#82-rebase-by-re-applying-operations).
- [The invariant](#52-the-invariant), checked after every step of a randomized append stream drained
  at random moments.
- Progressive indexing: at every intermediate commit, the answers equal those without indexes.
- The tree's oracles: incremental edits equal a rebuild from scratch, byte for byte, and the same
  operations in two orders give one root hash.
- Order: key-ordered reads across levels equal a sort of the rows, in both directions, with and
  without summaries. A key cursor's walk down is its walk up reversed, a step against its direction
  lands next to the entry before it, and its seeks below a key land where one sorted file's would.
- Deletes and updates: the rows left equal the rows written, filtered in C# under three-valued logic,
  across levels and without a clustering key, and a delete that loses its objects to a compaction is
  worked out again.
- The plan: a descent chooses the job a read of every leaf chooses, on random shapes and options that
  reach every trigger, every page's tally is the one its leaves add up to, and a cold plan asks for
  the same one or two pages over 3 000, 31 000 and 125 000 objects.
- Marks: a dataset that marks reads like a second one taking the same changes by rewrites, through
  every read (rows by position, rows in key order both ways with null keys around the marks, key
  cursors with their ranks and seeks, counts, extremes, aggregates), under a seeded fuzzer of deletes
  and updates. A compaction that read an object before rows were marked in it is abandoned, a count
  told to leave rows out counts none of them on any tier, each switched off in turn, an object whose
  marks are due by share or by vector is purged alone in its level, and a range too long for a
  vector's worst case is marked as the single run it is, with an update's rows written once.
- Schema evolution: objects of each earlier schema read as the current one through scans, counts,
  extremes, key order, key cursors, deletes, updates and compaction, filters over added columns
  included, and every change that would lose a value is refused.
- Tampering: an object replaced at equal size, and a page, a root and a fragment torn at every byte, or
  a fragment of another object. Readers answer exactly, and verification names each one.
- Rust: compaction outputs are among the files the cross-check hands to Rust 0.86.1.
- The churn bench (`bench/Vorticity.Benchmarks.Churn`): ten million rows, then thirty thousand commits
  of ten rows (appends, updates and deletes at random keys), with compaction drained and vacuum run
  between them. Every cost stays flat, or levels off once the marks have filled: an append takes 0.5
  to 0.7 ms, an update 1.9 to 2.9 ms and a delete 0.9 to 1.6 ms, each making five to six requests, one
  of them its commit. Compaction costs about a millisecond per commit, and a commit writes 12 KiB, then
  16 to 55 KiB, of which its commit object is 8 to 10 KiB. A point lookup takes 0.7 to 1.0 ms in three
  to four requests, a seek and ten steps either way 0.3 to 0.7 ms, a hundred rows by position 0.2 to
  0.4 ms, and a scan of every row 30 ms, with the heap between 60 and 92 MiB. Rewriting instead of
  marking, an update takes 19 ms and a commit writes 3.3 MiB.
