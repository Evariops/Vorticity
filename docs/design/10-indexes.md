# Skipping and locating indexes

What an index is in a Vorticity file: its kinds, its bytes, where it lives, how it survives an
append, and how it stays invisible to every other reader. What a caller can ask of an index (a
cursor, a count, key-ordered rows) is covered in [12-index-reads.md](12-index-reads.md), and how the
writer builds one in its single pass in [11-write-strategy.md](11-write-strategy.md).

An index is an optional accelerator. A reader that ignores it returns the same rows as a reader that
uses it, and the pruning invariant (pruning never removes a row a full scan returns, see [statistic
precision](08-semantics.md#1-statistic-precision-what-inexact-licenses)) holds for every kind. It is
also ahead of upstream. Vortex 0.86.1 ships a Bloom filter aggregate its authors call unstable and no
edition declares, and its index designs are still proposals. This design takes their structure and
adds one rule they have not fixed: a file with an index stays readable by a strict Rust 0.86.1
reader, with the index simply invisible to it. That rule decides [where an index
lives](#2-where-an-index-lives), and the rest follows.

## 1. Terms

| term | meaning |
|---|---|
| skipping index | a conservative summary per region, "no row here can match", which clears bits of a block mask |
| locating index | a value-keyed structure, "the rows or blocks where this value occurs", which may answer a predicate outright |
| exactness | `Exact` means the rows it names are exactly the matching ones. `Superset` means what it clears is proven and what it keeps is "maybe" |
| granularity | a row, a block (the zone length, 8 192 rows by default, counted from row 0), a node of 16 blocks, or the file |
| run | an immutable payload over a contiguous range of blocks. An index is a sequence of runs |

An index is a kind (a versioned string), a column path, a block length and its runs, which are
ordinary Vortex arrays. Everything a reader needs to decide whether it can use an index is in its
directory entry, and everything it needs to probe it is in the runs.

## 2. Where an index lives

The rule is that a file with an index opens and scans, with no configuration, in Rust 0.86.1 with a
strict session (the default, and what DuckDB, DataFusion, Spark and the Python bindings use), in any
earlier Vorticity, and in this one, which then uses the index.

The carrier is a postscript metadata entry. The postscript already carries up to sixteen
`(key, segment)` pairs that every reader only loads on request (see [the
postscript](02-format.md#2-postscript-footerfbs)). One entry, `vorticity.index`, holds the index
directory ([below](#3-the-directory-and-the-runs)). The directory points at run segments: regions
written after the data and before the footer, 64-byte aligned, referenced by neither the layout tree
nor the footer's segment table. A reader that does not ask for the key never touches them. Rust
checks that the directory's own segment lies inside the file, which it does, and does not know the
runs exist. A second entry, `vorticity.identity`, is the file's identity (see [identity and
integrity](13-dataset.md#7-identity-and-integrity)), which leaves fourteen entries for the caller.

The carriers not used:

| carrier | in a strict Rust 0.86.1 reader | verdict |
|---|---|---|
| a new aggregate in `vortex.zoned` | the open fails on an unknown aggregate | only once an edition declares one, with upstream's id and bytes ([the road upstream](#8-compatibility-and-the-road-upstream)) |
| a wrapper layout, upstream's `vortex.indexed` | the open fails, and the data beneath is unreachable | the migration target once it freezes ([the road upstream](#8-compatibility-and-the-road-upstream)) |
| a new field in the postscript table | ignored | refused, since it would take a field id upstream may assign |
| a second object next to the file | unaffected | used, for stores that cannot append, as a fragment ([indexing after the fact](#7-building-appending-and-indexing-after-the-fact)) |

## 3. The directory and the runs

The directory is a Protobuf message of this library's own, preceded by a version byte the way
`vortex.zoned`'s metadata is, and followed by an XXH3-64 of both (`Indexes/IndexDirectory.cs`). It
holds the file's row count when it was written, the write policy (so an append needs no options),
the index budget, and one entry per index: its kind, column path, block length, kind-defined options
and runs. A run names its first block and block count, its payload regions (each with its offset,
length, alignment, XXH3-64 checksum and array dtype), its entry count for a locating kind, and
kind-defined options.

A reader checks the following before it believes an entry, under the policy for [untrusted
hints](08-semantics.md#5-untrusted-hints-a-three-class-policy):

- the directory's checksum, and that its row count is the file's. Otherwise the whole directory is
  ignored, and the file reads without it.
- that the column path resolves to a column whose dtype the kind supports, that the kind and its
  options are known, and that the runs are disjoint, in order and inside the file. Otherwise the
  entry is ignored, never the file.
- every region it reads, against its checksum. A region that fails claims nothing in a pruner, and
  makes a key source refuse.

An index can therefore only ever cost pruning, never correctness. A block no run covers stays live,
and a zeroed filter is caught by its checksum rather than dropping rows.

Runs are ordinary arrays. Each payload is an array blob, the same form as a `vortex.flat` segment,
written with the file's encoding table and compressed like data under the same target edition. An
index therefore inherits every encoding: a sorted key column bit-packs or FSST-compresses like any
column, and no index defines a binary format of its own, which matches upstream's own design decision
for locating indexes. The footer may list an encoding used only by an index run, which is harmless
since every id is in the target edition.

A run is immutable and covers a contiguous range of blocks. A write produces one run per entry. Chunk
runs are spilled as they close and merged when the file completes, plus a separate run for the last
chunk when the rows do not fill whole blocks, since an append reopens that chunk. An append adds runs,
and merges an entry's runs once it holds more than four, so a lookup reads at most four runs. A
rewrite writes a single run.

Long runs are paged. A locating run of at most 64 segments of 65 536 entries lists its segments
inline. A longer one writes its segment table as fence pages of at most 64 KiB, forming a tree whose
root is inline in the run's options and whose pages carry their children's checksums. Two levels
cover more than 10¹² entries, so a lookup reads at most two fence pages before its payload
(`Indexes/FenceTable.cs`).

Bloom filters form a tree. A run's filters are laid out with a fan-out of 16: the leaves are the
per-block filters, an internal node filters the union of its 16 children and lists their sizes, and
the children of a node sit contiguously behind it. A probe reads the root and then one region per
node that still says "maybe", level by level, in one coalesced read per level
(`Indexes/BloomTree.cs`). A node whose union exceeds what `max_blocks` can hold at the target rate is
built without a filter, and the descent starts below it.

## 4. Skipping indexes

### 4.1 `vorticity.bloom.sbbf.v1`: a split-block Bloom filter per block

A filter is `n` blocks of 256 bits (eight 32-bit words). To insert, compute `h = XXH3-64(bytes, seed
0)`, pick block `((h >> 32) × n) >> 32`, and in it set one bit per word using the eight Parquet salts
applied to `(uint)h`. A probe checks that the same eight bits are all set. The layout and salts are
Parquet's and the hash is upstream's, so a filter is bit-identical to Rust's `BloomPartial` of
0.86.1, tested against vectors it built, and becomes upstream's by a change of id the day they freeze
it. A variant using Parquet's xxHash64 is bit-identical to a Parquet SBBF, tested against the
`parquet` crate's own filters, for a caller who exchanges filters with Parquet tooling.

What is hashed is chosen so that equal values hash equal whatever encoding they came from:

| dtype | bytes hashed |
|---|---|
| integers, floats, decimals | the value's little-endian bytes at the column's width. Floats hash by bit pattern, so `-0.0` and `+0.0` are two values |
| text, binary | the bytes |
| extension | its storage value's bytes |
| the elements of a list | each element, into the row's block, which answers "does any element of a row in this block equal v", the question `ListContains` asks |
| bool | not indexed, since a two-value domain has nothing to skip |
| null | not inserted, since `x = v` and `x IN (…)` never match a null |

A filter is built when a block closes, sized from its exact distinct count: the smallest power of two
of blocks that gives the target false-positive rate (1 % by default) by Parquet's sizing formula,
clamped to `max_blocks` (4 096 blocks, 128 KiB). A block with fewer than `min_distinct` (8) distinct
values gets no filter, since the zone map or the dictionary answers equality there. A tree node is
sized from the exact union of its blocks' values, never clamped into a saturated filter. The
file-level filter is the root of the tree when the policy asks for three resolutions (see [the
file-level filter](#44-the-file-level-filter)).

The filters act as a pruner after the zone maps in the scan's block mask (see [reading what the
writer produced](11-write-strategy.md#6-reading-what-the-writer-produced)), with these rules:

| predicate | proof |
|---|---|
| `x = v` | a block whose filter does not contain `v` is pruned |
| `x IN (v1 … vn)` | pruned if the filter contains none of them. The literals are hashed once per query |
| `AND`, `OR` | a conjunction of proofs, and under `OR` a block is pruned only if every arm prunes it |
| `ListContains(x, v)` | as `x = v`, over the element filters |
| `!=`, `<`, `LIKE` | no claim (see [trigrams](#42-vorticitybloomngram3v1-trigrams-for-text-predicates) for `LIKE`) |

A literal is only hashed when it converts exactly into the column's type. An integer beyond 2⁵³ from
a double, a non-integral float or an out-of-range integer claims nothing, a zero asks for both zeros,
and a NaN asks for nothing.

### 4.2 `vorticity.bloom.ngram3.v1`: trigrams for text predicates

This is the same structure over every byte trigram of every value of a text or binary column.
`StartsWith`, `Contains` and `LIKE` require the trigrams of each literal run of three bytes or more
between their wildcards, and one absent trigram prunes the block. An option folds ASCII case on both
sides. A folded index still answers a case-sensitive predicate, since a value containing `Foo`
contains `foo` once folded. It is a `Superset` index, so the predicate is always evaluated on the
survivors.

### 4.3 `vorticity.dict.probe.v1`: the dictionary is already an index

When the writer dictionary-encoded a chunk, the chunk's values child already is the exact set of its
distinct values. This kind therefore has no payload. Its entry says which chunks of the column are
dictionaries, and the pruner answers `x = v` and `x IN (…)` from those values without decoding the
codes, last in the chain since it reads the column's own bytes. A chunk only claims the blocks it
covers whole. It costs nothing to write. Reading it costs the chunk's segment, since a flat chunk is
one segment, so it pays off when the probed column is one of several (on a 1M-row table of six
columns, an absent value reads 0.073× the bytes of the scan) and breaks even on a file with a single
column.

### 4.4 The file-level filter

The root of each run's tree filters the whole run. A scan's plan reads the roots along with the file
statistics, and `ScanPlan.MayMatch` is false when either proves the scan empty. `VortexFile.MayMatch`
answers from the statistics alone, without a read, for an engine that prunes many files before
opening any. An append does not extend a tree, since filters of different sizes do not merge.
Instead it adds a tree for its own blocks.

## 5. Locating indexes

### 5.1 `vorticity.postings.blocks.v1`: value to blocks

A run holds the distinct values of its range, sorted, and for each one the blocks it occurs in, as
three arrays (`keys`, `offsets` and `blocks`) compressed like data. A lookup locates `v` in the keys,
where the run's segment table skips straight to the one segment that can hold it, and keeps exactly
the blocks listed. It is `Exact` at block granularity: a kept block holds the value, though not in
every row. The keys alone serve a walk of distinct values (see [sources of
order](12-index-reads.md#2-sources-of-order)).

### 5.2 `vorticity.sorted.runs.v1`: value to rows, exact

A run holds its range's keys sorted, with a row position per entry (a `u32` relative to the run's
first row, or a `u64` when a run spans 2³² rows or more). It answers `x = v`, `x IN (…)` and ranges
exactly, so a scan takes its rows as a selection and does not evaluate the predicate again. It is
also the key source of a cursor and of key-ordered delivery ([12-index-reads.md](12-index-reads.md)).
Keys follow the total order described in [key order per
dtype](12-index-reads.md#34-key-order-per-dtype). The run is built by sorting each chunk's distinct
keys, counting-sorting its rows by rank, and k-way merging the chunk runs, so memory is one chunk plus
the merge's buffers, never the whole column.

### 5.3 `vorticity.postings.ngram3.v1`: trigrams to blocks

This is the [postings index](#51-vorticitypostingsblocksv1-value-to-blocks) with the trigrams of a
text column as keys, typed `binary` since a trigram may cut a UTF-8 character. It is the locating
form of the [trigram filter](#42-vorticitybloomngram3v1-trigrams-for-text-predicates), larger than
the filter and exact at block granularity for the conjunction of a pattern's trigrams.

### 5.4 Composite keys, through the row encoding

A sorted run over `(a, b, …)` keys each row by the row encoding of the tuple
([06-row-encoding.md](06-row-encoding.md)): bytes whose `memcmp` order is the tuple order, so a
prefix on the leading columns is a range. The entry keeps the kind, leaves its column path empty, and
lists its key columns in its options together with the name of the encoding its bytes follow
(`vortex-row 0.86.1 …`), since that format is experimental upstream. The core ships no encoder.
`IndexPolicy.ForKey(columns, kind, encoder)` takes an `IKeyEncoder`, and the row-encoding package's
`RowKeyEncoder` is one. A tuple holding a null produces no entry.

### 5.5 Considered and not built

- Hash to rows: sorted runs whose keys are 64-bit hashes. They are smaller than sorted keys only on
  incompressible keys, where a Bloom filter is three times smaller again for the equality they would
  answer. Where keys have structure, the sorted keys compress below the hashes and stay exact.
  Measured on four key shapes, it was never the best answer. A workload of row-level lookups on long
  incompressible keys would reopen the question.
- A Bloom filter over column pairs. A tuple's cardinality is the product of its columns', which is
  exactly the regime where filters cost more than they save, and a composite sorted run answers the
  same question exactly.

## 6. The policy

`VortexWriteOptions.Indexes` is an `IndexPolicy`, `None` by default, so a file written with the
defaults carries no index. A caller names indexes by column path, nested paths included
(`IndexPolicy.None.Bloom("id").SortedRuns("ts")`), or starts from `IndexPolicy.Auto`
(`Indexes/IndexPolicy.cs`, [indexes.md](../guide/indexes.md)).

`Auto` records the dictionary probe wherever it applies, since it is free, and starts a Bloom filter
on every top-level column. It gives a filter up, whole and with its reason, at the earliest fact that
condemns it, and always before its payload is written, so an abandoned index leaves no byte behind:

- the column's first block climbs, so the zone map already prunes it
- its filters would pass 2 % of the column's raw bytes, checked at every block, or 2 % of its written
  bytes, checked at every chunk
- a full first node holds fewer distinct values than the floor, or its first four blocks hold one and
  the same set, so every filter would say "maybe" for every value
- its first node holds more values than a filter of `max_blocks` can, which is where a sorted run is
  the right structure

The hash per row is the whole cost of a live filter, which is why the verdicts come early. With them,
`Auto` adds 0.6 % to the write of a million-row table of six columns. Postings and sorted runs cost a
key table per row and are never chosen by `Auto`.

The file budget, `IndexPolicy.WithBudgetPerMille`, is 100 by default, meaning indexes may add a tenth
of the data's bytes. It is judged before payloads are written, once the data passes 1 MiB, and drops
optional indexes first. An index whose payloads are already written is kept rather than left as dead
bytes.

A required index (`required: true`) is never dropped by the budget to make room. If it cannot be
built, because its builder gave it up or the required indexes alone exceed the budget,
`CompleteAsync` throws a `VortexException` naming it and the reason before the file's tail is
written. The flag is stored in the directory, so an append that reuses the policy keeps it. A
dataset's clustering key is such an index (see [what a compaction
does](13-dataset.md#53-what-a-compaction-does)), and the dataset writes it exempt from the budget: it
is kept even when it alone is over the budget, and reported with what it cost, since a run on a
narrow table is as large as the column it orders and no object is big enough to bring it under a
share of the data.

The `WriteReport` names every index built, with its bytes, and every index abandoned, with its
reason.

## 7. Building, appending, and indexing after the fact

The builders are fed by the writer's pass over each block. A Bloom builder hashes its rows into an
exact set per block and per open node, and a key builder keeps its own table of the chunk's keys and
their positions. Filter runs are written between chunks as their nodes close, then the merged key
runs, fence pages and upper tree levels after the last chunk, before the zone maps, and the directory
last. Nothing is buffered across chunks except the open nodes and the spilled chunk runs, which go to
pooled memory and then to a scratch file.

When appending (see [appending to a file](11-write-strategy.md#38-appending-to-a-file)), runs that end
before the append's first block are kept and listed again next to the new ones, a run reaching into a
reopened chunk is dropped and rebuilt, and once an entry has more than four runs they are merged.

A file that already exists can be indexed without touching a data byte:

- In place, on a file system: `VortexFileIndexer.AppendIndexesAsync(path, policy)` reads the file,
  builds the runs, and appends them with a new directory, footer and postscript over the same data.
  The old file is a byte prefix of the new one.
- Outside the file, on a store that cannot append: `VortexFileIndexer.BuildFragmentAsync(file,
  policy, rows?)` returns an `IndexFragment`, a container of runs and a directory over a range of
  blocks, which a caller keeps where it likes and passes back at open through
  `VortexOpenOptions.IndexFragments`. A fragment is bound to the file's identity, or to the store's
  token for a file written without one, using bytes the open reads anyway. A content hash is also
  recorded, and only checked offline by `VortexFile.VerifyIndexesAsync` (`vxdump --fragment P
  --verify`). Fragments add entries next to the file's own, and an entry with the same identity joins
  its runs when their blocks are disjoint. In a dataset, fragments live in commit objects (see
  [fragments](13-dataset.md#64-fragments-in-commit-objects-transient)).

A key source needs complete coverage. A pruner uses whatever blocks the runs cover and leaves the
others live, but a cursor over partial runs would miss keys, so a key source refuses an entry whose
runs do not cover every block, and names the blocks it has.

## 8. Compatibility, and the road upstream

| reader | a file with an index directory |
|---|---|
| Rust 0.86.1, strict or permissive session | opens, scans and prunes with the zone maps, and never fetches the entry |
| an earlier Vorticity | the same: the key is listed but never fetched |
| this Vorticity | uses the index, and ignores an entry that fails a check ([the directory and the runs](#3-the-directory-and-the-runs)) |

The file stays a pure core-edition file. Every array in it, index runs included, carries an id of the
target edition, and the kinds are versioned strings inside the directory, which no edition needs to
know. When upstream freezes a Bloom aggregate in an edition, a file targeting it may also carry
filters in the zone map, under upstream's id and bytes. When `vortex.indexed` freezes, a locating
index becomes its auxiliary child, with the runs as the same arrays and the directory entry as its
metadata. Neither migration changes a payload byte.

## 9. How it is tested

- Rust reads it. The cross-check writes the whole corpus under rotating policies (every kind, filters
  at three resolutions, runs cut into small segments, appends, indexing after the fact, a dataset's
  compacted objects), and a strict Rust reader compares every value.
- Equivalence: every supported predicate, with indexes on and off, on a file written once and on the
  same rows appended in two, three and eleven pieces, returns the same rows.
- Lying indexes: a zeroed filter with its framing kept is caught by its checksum and drops no row,
  and torn fence pages, tree nodes and regions claim nothing (`LyingIndexTests`, `BloomTreeTests`,
  `FenceTreeTests`).
- Bit-exactness: 66 filters against Rust's `BloomPartial`, and 26 against Parquet's `Sbbf`.
- Allocation: a ceiling per kind on the write (`IndexAllocationTests`).

The upstream designs this follows are the Bloom aggregate (vortex PR #9398, marked unstable in
#9753), the skip-index interface (#9413), the skipping and locating index epics (#8900, #8948,
#9024), and multi-resolution zone maps (#7939). Parquet's `BloomFilter.md` describes the filter, and
O'Neil et al., *The log-structured merge-tree*, 1996, the runs.
