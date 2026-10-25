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
`vortex.zoned`'s metadata is, and followed by an XXH3-64 of both (`Vortex/Indexes/IndexDirectory.cs`). It
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

A lookup by a value needs the entries of the indexes that locate it, not the whole directory. The
table of contents a dataset's entry carries for an object ([what a node
carries](13-dataset.md#42-what-a-node-carries)) names each entry of a locating kind (sorted runs,
block postings, hashed postings, trigram postings and the dictionary probe): its column, its kind,
and where its message lies within the directory, with an XXH3-64 of those bytes. A lookup reads the
messages of its filter's columns alone and checks each against its checksum, so an entry that fails
costs only itself. A day's directory held 11.5 MB, of which the identifier's hashed postings, what a
lookup reads, took 558 bytes. An object whose table of contents names none is looked up through its
whole directory.

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
(`Vortex/Indexes/FenceTable.cs`). A page read is checked whole in one pass, then held as its bytes and
where each fence's keys lie in them: a descent compares keys where the page holds them, and parses
the regions of the fence it takes alone. Parsed into a key array and a region array a fence, the
pages of a lookup that leaves one identifier out over three objects of 180 M rows were half of what
it allocated.

Bloom filters form a tree. A run's filters are laid out with a fan-out of 16: the leaves are the
per-block filters, an internal node filters the union of its 16 children and lists their sizes, and
the children of a node sit contiguously behind it. A probe reads the root and then one region per
node that still says "maybe", level by level, in one coalesced read per level
(`Vortex/Indexes/BloomTree.cs`). A node whose union exceeds what `max_blocks` can hold at the target rate is
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
file-level filter](#411-the-file-level-filter)).

The filters act as a pruner after the zone maps in the scan's block mask (see [reading what the
writer produced](11-write-strategy.md#6-reading-what-the-writer-produced)), with these rules:

| predicate | proof |
|---|---|
| `x = v` | a block whose filter does not contain `v` is pruned |
| `x IN (v1 … vn)` | pruned if the filter contains none of them, asked while the filters pay for themselves (below) |
| `AND`, `OR` | a conjunction of proofs, and under `OR` a block is pruned only if every arm prunes it |
| `ListContains(x, v)` | as `x = v`, over the element filters |
| `!=`, `<`, `LIKE` | no claim (see [trigrams](#42-vorticitybloomngram3v1-trigrams-for-text-predicates) for `LIKE`) |

A literal is only hashed when it converts exactly into the column's type. An integer beyond 2⁵³ from
a double, a non-integral float or an out-of-range integer claims nothing, a zero asks for both zeros,
and a NaN asks for nothing.

An `IN` of many values is where a filter can cost more than it saves. A block is pruned only when its
filter refuses every value, and each value passes a filter that does not hold it with the filter's
false-positive rate p, so a block holding none of n values is pruned with probability (1 − p)ⁿ. That
probability falls fast: at p = 1 %, it is 37 % for 100 values and almost nothing for 1 000. The rate p
is not the one the policy asked for either. Rounding each filter up to a power of two leaves it 50 to
100 % full, so the real rate of most filters lies 10 to 60 times below the nominal one, and a block
of few distinct values has a nearly empty filter that refuses almost anything. And when every block
holds one of the values, nothing can be pruned at all, whatever p.

No fixed threshold on n can follow all of that, so the pruner counts what the filters cost against
what they save, in probes, as it asks them. A probe and a value hashed cost one, and a block pruned
earns two per row, since a scan that reads a block decodes and tests each of its rows. On an M4 Pro
a probe takes 3 ns (3.5 ns in a filter of a megabyte), a value's hash 4 ns, and a row 5 to 7 ns when a
scan tests an `IN` on an Int64 column. Each level of the tree gives the question the worth of one of
its nodes. The question stops at the level where its credit runs out and asks nothing below, and a
level no question still asks is not read. A filter answers maybe at the first value it may hold, so
the values are hashed as the probes reach them, and only a node proved absent needs them all.

The waste is then bounded by one node's worth per level, whatever n, the filters' rates and where the
values lie, and a question whose filters pay never runs out. Over 4 096 blocks of 64 rows, the rule
asks every filter up to 64 values and prunes the same blocks as asking them all. At 1 024 values and
beyond it stops within 0.3 to 0.4 ms, where asking every filter took 8.5 to 400 ms to prune nothing or
next to nothing. Counting the scan of the blocks left live, it stays within 1 % of the better of
asking always and never up to 64 values, and within 8 to 17 % of never asking beyond, which is the
price of reading the roots and one level before it gives up. Not asking a filter only leaves blocks
live, so the rows a scan returns never change.

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
column. A scan probes only the chunks that hold a block the earlier structures left live among its
own rows, a lane's part of an object its part's, and reads them together, 32 MiB a round trip; each
chunk's values are looked up among the literals, sorted once, where they lie, nothing sorted per
chunk, and a chunk that is not a dictionary claims nothing.

### 4.4 `vorticity.slices.v1`: the slices of a file

A file a dataset's job writes in slices carries its slice table: per slice, its first row and its row
count, in order, covering every row once, and its bounds. Like the dictionary probe it has no payload.
The entry is on the column the bounds are on, one run over every block, and its options hold the table
behind a version byte. A key table (version 6), which a job writes into each object of a mebibyte or
more, holds the slice count, then per slice its rows and its first key, row-encoded as the key orders
it and kept as the bytes it shares with the first key before it and the rest, then the last key of the
last slice the same way; its entry is on the key's first column, and a job cuts a slice only at a
change of key, so that a key lies in one slice. A time table (version 5) holds the instant's unit, then
per slice the gap from the previous slice's end, its width and its rows, each a varint, a few bytes a
slice: the datasets an earlier release compacted by periods wrote their objects in time slices, which
a reader still walks. Version 4 followed the time slices with the columns each slice's rows were sorted
by and the paths the file's synopsis counted, which a reader skips; the three before it, which only the
first writers of the format made and no store keeps, are not read. A table that does not decode, or
whose rows are not the file's, is not read. The writer declares the slices before it completes the
file, which checks that they cover its rows ([key
slices](11-write-strategy.md#9-key-slices-and-shredded-variants)), and writes an index directory for
them even when its policy asks for no index. A time table is also the instant's key source for those
objects of periods ([sources of order](12-index-reads.md#2-sources-of-order)). A scan of an object's
rows under a range of the key's first column, or under a window on a time table's instant, covers the
rows of the slices the range reaches, which the table of the object's entry names before any read: its
plan, its zone maps and its walk then count those slices' blocks alone.

A sliced file also says where its chunks lie, under the kind `vorticity.slices.chunks.v1`, on the
root. Its options hold the file's table of array encodings, and its one run has a payload region per
top-level column, in column order. A column's region holds, behind a version byte, its chunks, each
its offset, length, alignment and rows as varints. That is about ten bytes a chunk, 250 KB a column
for a day of 10⁹ rows, so the regions are read when needed rather than kept in the options every open
reads, and a read of one column, the instant at the edges of a window, fetches that column's region
alone. With it a reader decodes the chunks that hold a slice from their own bytes, since a chunk's
segment ends with its encoding tree, and never reads the footer ([the rows of a
slice](12-index-reads.md#51-the-rows-of-a-slice-without-the-footer)). An append drops both slice
kinds, since they describe the file as its writer cut it.

A column of more chunks than a page lays its region out in pages instead (version 2,
`Vortex/Indexes/SliceChunks.cs`): a page table, then pages of 128 entries, each 24 bytes, a chunk's
first row, offset, length and rows, little-endian. The table holds, behind the version byte, the
chunks' alignment, the entries a page and the chunks, then the XXH3-64 of each page. The columns paged
are those whose chunks start at the rows of the first one's and share one alignment, so the first row
of each page is the same for all of them, and the object's table of contents holds it once ([what a
node carries](13-dataset.md#42-what-a-node-carries)). A column cut otherwise, a variant column's
shapes, keeps version 1, as does a column of a page or fewer. A day's column held 43 946 chunks in a
region of about half a megabyte, which every read of a slice and every lookup read and parsed whole:
5.9 MB of directories an object. Paged, a read of a slice reads the column's page table and the pages
that hold its rows, in the round trips the whole region took, checks each page against its checksum,
and reads the entries where they lie. A lookup learns its rows from the postings, so it reads the
tables first and the pages then, one round trip more for far fewer bytes. A reader of the file alone
reads the region whole, table and pages, which its run's payload names.

### 4.5 `vorticity.variant.paths.v1`: where each shredded key of a variant lies

A writer that shreds a variant column ([90-registry.md](90-registry.md#1-array-encodings))
notes, for each key of the column's tree and in each chunk, the range of the key's buffers in the
chunk's segment, the dtype the key wears there (its typed column's, or a variant's where the key kept
values of other classes) and the key's encoding tree over those buffers alone. The writer lays a key's
buffers out together, in its tree's depth-first order, so the range is one read. The entry is on the
variant column. Its options hold the file's table of array encodings and the keys, and its one run has a
payload region per key, behind a version byte and the body's length, the body one zstd frame: the
trees of one key differ little from chunk to chunk, so a key costs 32 to 82 bytes a chunk where its
trees take 219 to 929. A reader of one key reads its region, then each chunk's range, and decodes it as
a flat layout decodes a segment whose tree is inlined, never fetching another key's bytes. A key whose
buffers do not lie together in a chunk is left out of that chunk, which is then read whole. An append
drops the entry.

### 4.6 `vorticity.variant.directory.v1`: every path a variant holds

A job that writes an object in key slices counts the paths of each variant column as the object's rows
are written ([shredded variants](11-write-strategy.md#9-key-slices-and-shredded-variants)), and the
object carries what it counted: for every path and class of value the rows hold there (a boolean, an
integer, a number, a text, a timestamp, an object, a list, a null, anything else), how many values, where
they lie (the typed column a shredded path keeps, the values of other classes beside it, or the
residual), and their bounds. An integer or a timestamp keeps its least and greatest, a number its least
and greatest and whether one was NaN, a boolean which of the two it saw, and a text its least and
greatest cut to the bytes a text column's zones keep, the greatest one past its cut when a longer text
shares it. Each path also names, a bit each, the slices of the object it may hold a value in, from the
spans of rows its values lie in as the rows are written: two values closer than a slice is long share a
span, so a path keeps about a span per slice at most, and the bits are a superset of the slices that hold
it. An object of an earlier release's period counted the spans by time, its rows in time order. The entry
is on the variant column. Its one run holds one region, behind a version byte and the body's length, the
body one zstd frame.

The directory fits the file's index budget. Over it, the paths of the residual held least are left
out first, and the directory says it is partial, after which a path's absence proves nothing.

A scan whose filter asks a path for a value reads the directories of the columns it names, one region
each, before the Bloom filters, and makes of each the entries of the filter's paths alone: the region is
decompressed into a rented buffer and its entries walked as they lie, their keys compared as bytes, where
parsing every entry made 4 MB of an hour of log records at every question. It refutes the file when a
fenced predicate asks for a path the file never holds, for a class the path never holds there, or for
values outside its bounds, under the filter's conjunctions and disjunctions. A presence asked absent, a
conversion of any class, and a negation refute nothing. A refuted file reads no block (`ScanPlan.MayMatch` is false, and `Explain`
names the step `path directory`). Otherwise it asks the same of each slice, and kills the blocks that
lie wholly inside the runs of slices refuted. An append drops the entry, since it parses none of its
rows.

### 4.7 `vorticity.variant.blocks.v1`: a shredded key's zone map

A writer that shreds a variant column counts, as it routes each value, the values of the top-level keys
shredded as an integer, a text or a boolean, block by block: for each block that holds a value of the
key's class, how many, and their bounds, as the path directory keeps them for the object. Those keys'
typed columns hold every value of their class, so a block with no entry holds none, and a question of
that class matches nothing there. A key shredded as a double is not counted, since a float lies beside
its typed column and `AsDouble` reads both.

The writer keeps a key's statistics where they can prune, measured on the file's own values: how much
the blocks narrow the key's range, a block without the key counting whole. A key whose every block
holds it over its whole range, as a scattered duration does, scores nothing and is dropped. Under the
index budget, which the path directories share, the keys that narrow least go first. The entry is on
the variant column, the keys and classes in its options, its run a region per key and class, behind a
version byte and the body's length, the body one zstd frame.

A scan whose filter reads one of those keys in its class (`AsInt64`, `AsString`, `AsBool`, a presence of
the class, a membership) reads its region after the zone maps and kills each live block the
statistics refute, under the filter's conjunctions and disjunctions, as a zone map kills a declared
column's (`Explain` names the step `path statistics`). An append drops the entry.

An index of the policy may also name a path. An entry then carries, beside the variant column's field,
the keys of the path and the class of its values (two fields of the entry's message that an older
reader skips, which then sees an entry on a variant column, a dtype no kind supports, and uses none).
The writer reads the path in its class at ingest, as a question reads it, and hands those values to
the entry's builder as a column of that class's dtype: `IndexPolicy.Auto` or an explicit Bloom policy
on the variant column gives each top-level key it shreds, in the class its typed column holds, a Bloom
filter judged against the bytes of that key's ranges. A filter's question of the same class probes it,
and a question of another class never does. A policy may also name one key in one class as a column
of its own, `Attributes['user'].AsString()`, which a dataset does for an identifier among the
attributes: that key's entry is then built under that policy, a Bloom filter or [hashed
postings](#57-vorticitypostingshashedv1-value-to-blocks-through-the-hash), the only locating kind a
path takes for now. An object that does not shred the key has no slot for it, and no entry.

### 4.8 `vorticity.column.sheet.v1`: what a column holds

A dataset's object in slices, of a mebibyte or more, keeps a sheet of each top-level column an index
keys, an integer, a float, a string or a binary through any extension: its rows and nulls, its distinct
values, a sketch of them, its most frequent values, and a filter of its values when they repeat
([22-on-the-fly-and-synopses.md](22-on-the-fly-and-synopses.md#51-the-column-sheet)); a smaller object,
which a question reads in the request its sheets would take, keeps none. Each value is hashed
as a Bloom filter hashes it, XXH3 of its bytes as the column stores them. The distinct values are counted
exactly while there are at most 2^20 of them and estimated past that by a HyperLogLog of 2^11 registers
of six bits, 1 536 bytes, about 2.3 % of relative error, by Ertl's estimator. A column whose valid rows
hold at least four times its distinct values keeps its 128 most frequent values with their exact rows,
the most first, and a binary fuse filter of 8-bit fingerprints over the distinct hashes: about 1.125
bytes a value and a false positive in 256. A column whose values hardly repeat keeps neither, and one
that meets a new value nine rows in ten drops its counts early, its registers going on alone.

The entry is on the column, its one run a region: a version byte, the flags, the rows, the nulls and the
distinct values as varints, the registers four in three bytes, the frequent values, each its rows, its
error and its bytes, then the filter, its seed, its segments and its fingerprints. The object's table of
contents names each column's region, so that a reader fetches the sheets of the columns it asks with no
open of the object, in one request. A dataset's scan whose filter asks a column for values with an
equality or a set reads the sheets of each object of a megabyte or more before it, sixteen objects side by
side, and passes over an object whose filter lacks every value asked, under the filter's conjunctions and
disjunctions (`DatasetScanCounters.ObjectsRefutedBySheets`). Writing the sheets costs 5.6 to 5.9 ns a
column a row on the design's requests, eleven columns, one thread (`vortex-queries --micro sheets`).

### 4.9 `vorticity.column.counts.v1`: a column's counts

The same objects keep, for each top-level column whose sheet counts its values and whose values repeat,
its counts: for each value, for its nulls, and for the values its budget left out together, an entry over
the whole object that holds the rows of that value and, for each measure column, their values, their
exact sum, their least and their greatest, in the words the engine's states take
([22-on-the-fly-and-synopses.md](22-on-the-fly-and-synopses.md#52-the-counts-of-a-column-and-their-rollups)).
The measures are the top-level integer and float columns of more than 64 values; a number column of 64
values or fewer, a status or a code, is counted as a column and carried by no other. A column whose
distinct values pass a quarter of its valid rows keeps no counts, nor one whose sheet gave its counts up.

The budget is absolute: 2^20 entries a column, fewer for a table of many number columns, whose states
take at most 128 MiB a column while the object is written. Past it, the values whose rows are fewest so
far are left out, their entries merged into the entry of the values left out, exact, and a value
left out stays out, so that a value kept is counted from its first row.

The entry is on the column, its one run a region the object's table of contents names: a header, the
measures, whether the entries have sketches and the wide columns whose registers they keep (section
4.10), a directory of chunks of about 16 KB, each naming its first value and its first entry, with the
XXH3 of its bytes, then the header's own XXH3, then the chunks (version 2). The nulls come first, then
the values left out, then the values in the order of their bytes, each one's entry after it, or, in a
rollup's counts kept by bucket, its entry for each bucket its rows lie in: an entry's rows and each
measure's states as varints, a float sum as its three bins, the values it skipped, its top and its high
part, and a float's extremes as their eight bytes. A reader refuses groups out of that order, which it
searches a value in, and one that wants a few values reads the header, then their chunks, each checked
against its sum. Entries of two objects of the same value merge word for word, which is how a rollup's
are summed ([rollups](15-compaction.md#rollups)). The counts of version 1, by the hours of a period's
instant, which an earlier writer of this branch kept, are read as no counts. The sheets and the counts of
an object lie right after its data, where no read of the structures a scan reads together bridges them.
On the design's requests, two measures one of them a float, an entry takes 52 to 61 bytes, and the counts
cost the writer 9.4 to 12.0 ns a counted column a row, one thread (`vortex-queries --micro counts`).

### 4.10 `vorticity.column.sketches.v1`: the sketches of a column's counts

The sketches of a column's counts lie in a region of their own, right after the counts, which the table
of contents names beside them: a question of counts, sums and extremes reads the counts alone, and one of
quantiles both, side by side in one request. The region holds, for each entry of the counts in their
order, a quantile sketch of each measure, then the registers of each wide column, each its length first,
0 for none: a version byte, the entries and what each keeps, a directory of chunks of about 16 KB, each
naming its first entry, then the chunks. On the design's requests, Service's counts take 136 KB and their
sketches 2.4 MB, which a count by service no longer reads.

A column of at most 1 000 values keeps, for each entry, a quantile sketch of each measure: the engine's
log-linear grid at 1 % of relative error, which a quantile asked with no error takes, its buckets as the
engine merges them. Sketches of two objects merge bucket by bucket into the sketch of their rows together.
A sketch of a measure spanning 3.7 decades fills some 790 buckets: an entry's two sketched measures take 1
to 3.4 KB on the design's requests, and the sketches cost the writer some 12 to 18 ns more a sketched
column a row.

A column of at most 100 values keeps, for each entry, the distinct values of every wide column as well:
a text column of more than 1 000 values that repeat. They are a HyperLogLog of 2^11 registers of six bits
over the hashes of the wide column's values in the entry's rows, the sheet's, written as the set registers
while few are, packed past them, after the entry's quantile sketches. An identifier, whose values hardly
repeat, is no wide column: its distinct values in an entry are the entry's rows. Registers of two objects
merge register by register. The sheet of a wide column hashes each row of a batch for them, and the
columns of few values raise their entries' registers once the whole batch is in.

### 4.11 The file-level filter

The root of each run's tree filters the whole run. A scan's plan reads the roots along with the file
statistics and the path directories, and `ScanPlan.MayMatch` is false when any of them proves the scan empty. `VortexFile.MayMatch`
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

An `IN` is answered by walking the run and the filter's sorted values together, as two sorted lists
are merged, in this run and in a postings run alike. A value past the segment at hand is reached by
steps to the next segments while they cost less than a descent of the segment table, which compares
about log₂ of the segments, and by a descent past that, so values thick in the run cost a step each
and values far apart a descent each. A table in pages is only descended, since a step could read a
page no value needs. Each segment read is then matched against the values of its own range, by a
search per value or a walk of its entries, whichever compares less. What the filter asks of the
column, its values encoded in the column's layout and sorted in the run's order, is prepared once per
filter and kept with it, so the objects of a dataset share it. Over a run of 4.2 million entries,
4 096 values take 3.3 ms instead of 17.1 ms, and 65 536 values 12.4 ms instead of 36.5 ms, with
128 KiB allocated instead of 17 MiB.

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
  Measured on four key shapes, it was never the best answer. Lookups on long incompressible keys
  reopened the question, and [hashed postings](#57-vorticitypostingshashedv1-value-to-blocks-through-the-hash)
  answer it with hashes cut to what the pairs need, kept as gaps, and blocks rather than rows.
- A Bloom filter over column pairs. A tuple's cardinality is the product of its columns', which is
  exactly the regime where filters cost more than they save, and a composite sorted run answers the
  same question exactly.

### 5.6 The kinds of persisted states

The aggregates a dataset keeps past a scan, for runs and for its tables, are kinds of their
own, each a version in its name ([states](16-queries.md#57-states-kept-past-a-scan)). A state is
integers on every machine, stored as columns of 64-bit integers that hold its words as the engine
keeps them, so a state read back is the same bits and merges as the engine merges, in any order:

| kind | the state | its columns |
|---|---|---|
| `vorticity.state.count.v1` | a count | `count` |
| `vorticity.state.sum.int128.v1` | an exact sum of integers | `low`, `high` |
| `vorticity.state.sum.int320.v1` | an exact sum of unscaled decimals | `l0` to `l4`, least significant first |
| `vorticity.state.sum.indexed.v1` | an exact float sum and its count | `m0`, `m1`, `m2` (the three bins), `meta` (count, top bin, infinities met), `high` (the bins' high parts, the epochs settled) |
| `vorticity.state.min.v1`, `vorticity.state.max.v1` | an extreme | a column of the value's own type, null where there is none |

A kind's layout never changes under its name. A reader meeting a version it does not know ignores the
state and works from the rows, as with any index. Fixed vectors pin the words of three float sums, so
that the CI's x64 runs hold the bits arm64 computed.

### 5.7 `vorticity.postings.hashed.v1`: value to blocks, through the hash

This kind answers `x = v` and `x IN (…)` on a column of long, rare values, an identifier above all, at
a fraction of a sorted run's bytes. A sorted run of request ids of 32 hexadecimal digits takes about
19.5 bytes a row: the key, which no encoding shrinks much, and the row. Hashed postings take about 2.8.

Each value is hashed with XXH3-64 over its bytes in the column's key layout, the same bytes a Bloom
filter hashes, and the run holds every distinct (hash, block) pair once, sorted by hash. A run keeps of
each hash only its top bits, as many as number its pairs plus ten: 32 bits for 4 million pairs, 43
for 2³³. The run's entry count, in the directory, says how many, so no option carries it. A segment
holds about 16 384 pairs, cut at the end of a hash, as two arrays compressed like data. The first
holds the gaps between consecutive cut hashes, the first gap 0 since the segment's first hash is its
lower bound. The gaps average about 2¹⁰ whatever the run's size, so they pack into about 13 bits. The
second array holds each pair's block, counted from the run's first. The segments' bounds are full
hashes, so the run's table, inline or in fence pages, finds a probe's segment as it finds a key's in
any locating run.

A probe hashes its literal, reads the one segment whose bounds hold the hash, sums the gaps from the
lower bound until it reaches the literal's cut hash, and keeps the blocks of the pairs found there. A
value the run lacks meets another value's cut hash about once in a thousand probes, which keeps a block
live for nothing. The kind is therefore a superset at block granularity, as a Bloom filter is, and
never exact.

Measured on four million identifiers in blocks of 8 192 rows (`--micro needles`), each index's bytes
from the write report and a probe's from one segment decoded and searched:

| identifier | sorted run | hashed postings | read by a probe | a probe |
|---|---|---|---|---|
| 32 hex digits, one a row | 19.46 B/row | 2.78 B/row | 44 KiB instead of 1 246 KiB | 4.7 µs instead of 1 246 µs |
| 16 hex digits, one a row | 11.50 B/row | 2.78 B/row | 44 KiB instead of 736 KiB | 4.9 µs instead of 717 µs |
| 32 hex digits, ten rows each | 4.96 B/row | 2.00 B/row | 34 KiB instead of 317 KiB | 7.4 µs instead of 63 µs |

At the design's scale an identifier costs more, its block numbers wider: a day of 4.32 × 10⁹ requests
writes 4.17 bytes a row of hashed postings for its request id, 18.0 GB, whether the ids follow the
rows' order or not, a segment of about 16 384 pairs taking 68.5 KB ([what an identifier
costs](../guide/indexes.md)).

The block numbers take what the run's blocks need, 9 bits here and 19 in an object of 2³² rows, where
a pair weighs about 4 bytes. Two probes in a thousand for values the column lacks answered a block.

An append adds the kind's runs and never reads old ones back to merge them. A run keeps only the bits
of its hashes its own pairs need, which a larger run cannot extend, so the runs are listed as they
are, as a Bloom filter's are, and a probe reads one segment per run.

## 6. The policy

`VortexWriteOptions.Indexes` is an `IndexPolicy`, `None` by default, so a file written with the
defaults carries no index. A caller names indexes by column path, nested paths included
(`IndexPolicy.None.Bloom("id").SortedRuns("ts")`), or starts from `IndexPolicy.Auto`
(`Vortex/Indexes/IndexPolicy.cs`, [indexes.md](../guide/indexes.md)).

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
share of the data. A dataset's declared identifiers (`DatasetOptions.Identifiers`) get the same:
required [hashed postings](#57-vorticitypostingshashedv1-value-to-blocks-through-the-hash) on each, in
every object it writes, appends and jobs alike, exempt from the budget. A Bloom filter is never tried
on one, since a tree of filters loses its upper nodes past what one filter holds, which the values of
an identifier always pass. An identifier may be a key of a variant column read as a string or an
integer, `Attributes['user'].AsString()`: a job then shreds that key wherever a row holds one of that
class, whatever its bytes say, and indexes it as a column. An append shreds nothing, so its objects
carry no index on such a key.

The `WriteReport` names every index built, with its bytes, and every index abandoned, with its
reason.

## 7. Building, appending, and indexing after the fact

The builders are fed by the writer's pass over each block. A Bloom builder hashes its rows into an
exact set per block and per open node, and a key builder keeps its own table of the chunk's keys and
their positions. Filter runs are written between chunks as their nodes close, then the merged key
runs, fence pages and upper tree levels after the last chunk, before the zone maps, and the directory
last. Nothing is buffered across chunks except the open nodes and the spilled chunk runs, which go to
pooled memory and then to a scratch file.

Hashed postings keep no run a chunk. Each value's hash is noted once a block, told apart by a table of
the block's hashes, and appended to the bucket of its top bits, a bucket for about four million pairs
of the file's row bound, up to 1 024; a bucket's full pages go to the scratch at each chunk's close.
After the last chunk the buckets are sorted one at a time, in the hashes' order, stably on the bits
under the bucket's, so a hash's blocks keep their order, and fed to the runs the merge of chunk runs
would have made: the same pairs, the same segments. An identifier of a day of the design's requests,
4.3 × 10⁹ pairs, then takes two passes over its pairs, where its runs of 33 000 pairs a chunk, 130 000
of them, took a k-way merge in passes of 64. A bucket past twice its aim, as every bucket of a file
written with no bound on its rows is, is first cut again by the next bits of its hashes, up to ten, its
pages read back in their order, and each part sorted alone: a file of 10⁷ random integers and no
bound ends allocating 225 MB where sorting its one bucket whole took 404, and holding 199 MB of
working set more where it held 370, measured by a probe; `HashedBucketsTests` holds the parts to a
stable sort of every pair.

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

A dataset is indexed after the fact by `VortexDataset.IndexAsync(policy)`, which gives the indexes to
the dataset before it gives them to the objects. Its first commit adds them to the policy the
dataset's commits carry, in the bytes a directory stores a policy in, and from that version on every
write and every compaction builds them into the objects it writes, whatever its own options say. Its
budget spares them as it spares a dataset's clustering run, since they were asked for by name, but
they are not required: a block of too few distinct values still leaves its filter out. The order
matters: a compaction rewrites the objects it merges, and one that did not know the indexes
would write its output without them and drop the fragments of its inputs, so an indexing that ran
before the dataset knew the indexes could be undone by any compaction. The indexing then gives each
object that lacks one of the indexes a fragment, a batch of objects per commit, and walks the objects
again until a walk finds nothing to index, which is how it finds an object written by a compaction
planned before its first commit.

`IndexAsync(IndexPolicy.Auto)` commits `Auto` as the default of the dataset's policy, which takes the
place of none when two policies join. Every column a writer meets that no override names then takes
`Auto`'s indexes, those schema evolution adds after the policy included, and every key a job shreds
from a variant column takes `Auto`'s Bloom filter in its class. The budget weighs them, since no
column asked for them by name. The objects already written take them as compaction rewrites them:
the walk gives fragments only to the columns a policy names.

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
