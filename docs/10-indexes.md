# Skipping and locating indexes

A Vorticity specification, written 2026-09-15 and revised the same day with the second
iteration of [11-write-strategy.md](11-write-strategy.md), which owns the pass that builds these
structures (§3.2, §3.3), the scan contract that consults them (§6) and the surface that exposes
them (§7). This document owns what an index **is**: its kinds, its payload, where it lives in a
file, how it survives an append, and how it stays readable by everyone else. What a consumer can
**ask** of an index on the read path — cursors, probes, key-ordered delivery — is
[12-index-reads.md](12-index-reads.md), which also amends §4.1 and §6.2 (its §14).

It is deliberately **ahead of upstream**: Vortex 0.86.1 ships a Bloom filter whose wire format its
own authors call unstable, and two index epics whose status is *Proposed* (§1). It takes the best
of those designs, adds what the port can do that upstream has not decided yet, and fixes one rule
they have not: **every file we write with an index stays readable by a strict Rust 0.86.1 reader,
byte for byte, with the index simply invisible to it.** That rule decides where an index lives
(§3), and everything else follows from it.

Terms (§2): a **skipping index** proves that a region cannot match a predicate; a **locating
index** says where a value occurs. Both are optional accelerators: a reader that ignores them
returns the same rows as a reader that uses them, and [08-semantics.md](08-semantics.md) §1's
invariant — pruning may never eliminate a row a full scan would return — applies to both.

---

## 1. What upstream has, and what we take from it

Verified on 2026-09-15 against `vortex-data/vortex` `develop` and the 0.86.1 crates
([99-sources.md](99-sources.md) for the paths).

| upstream | state | what we take | what we do differently |
|---|---|---|---|
| Bloom aggregate `vortex.bloom_filter.sbbf` ([PR #9398](https://github.com/vortex-data/vortex/pull/9398), 0.86.0) | shipped, **marked unstable** ([PR #9753](https://github.com/vortex-data/vortex/pull/9753)): hash function, block layout and salt order may change; **no edition declares it**; must not be persisted | the split-block Bloom filter (SBBF) structure, one filter per block of rows, XxHash3-64, an equality probe as a scalar function | size chosen from the block's exact distinct count instead of a fixed 256 blocks; coarser resolutions as generations; stored **outside** the zone map (§3); Parquet's xxHash64 only as an explicit variant |
| Skip index write interface ([PR #9413](https://github.com/vortex-data/vortex/pull/9413)) | open, review required, interface under debate | `with_field_zoned_options` as the shape of a per-column declaration; a `SkipIndex` bundle = summary + probe + rewrite rule | a per-column `IndexPolicy` on `VortexWriteOptions`, `Auto` by default (§5.5); the rewrite rule is a pruner in the scan's block-mask chain, not a plugin |
| Epic #8900 Skipping Indexes, tracking #8901 | Proposed | the definition, the "safely ignored" guarantee, the granularity question | we answer the granularity question with block-aligned indexes at `k × zone_len` (§4.3) |
| Epic #8948 Locating Indexes, tracking #9024 (`vortex.indexed`) | Proposed, no code | the orientation argument (range-keyed vs value-keyed), `IndexExactness` Exact / Superset, `RowLocator` Rows / Blocks, "an index is an ordinary array tree" | not a wrapper layout: a wrapper layout is unreadable to old readers, and upstream says so themselves. Ours is out-of-tree (§3) and moves in-tree when `vortex.indexed` freezes (§9); and every payload is a **run**, so an append never rewrites what exists (§4.2) |
| Epic #7939 Multi-resolution zone maps | Phase 1 in progress | fine min/max at 8 192 rows, coarse Bloom at a multiple of it, cheap maps evaluated before expensive ones | same, as generations of `k` blocks (§4.3, §5.1) |
| Discussion #8682 trigram index | closed into #8900 | trigram summaries for `LIKE` | an n-gram Bloom (skipping, §5.2) and an n-gram postings index (locating, §6.4), once the expression model has `LIKE` (§6.6) |
| Roadmap H1 2026 ([#6456](https://github.com/vortex-data/vortex/discussions/6456)) | "Bloom filter" and "Text index" listed as future first-class layouts | the intent | the timing |

Nothing above is in the Vortex specification. `docs/specs/file-format.md` and `editions.md`
upstream contain neither "bloom" nor "index"; the only relevant rule is that an unknown zone-map
aggregate disables pruning **only under `allow_unknown`** — a strict session, which is the default
(`vortex-session-0.86.1/src/session.rs:234-242`), **rejects** the file
(`vortex-layout-0.86.1/src/layouts/zoned/schema.rs:56-60`). That single fact rules out the zone
map as a carrier for anything upstream has not frozen.

---

## 2. Terminology

| term | meaning | answers | probe cost |
|---|---|---|---|
| **skipping index** | a conservative summary per region: "no row in this region can match" | a block mask whose `false` bits are proven | O(regions) |
| **locating index** | a value-keyed structure: "the rows (or blocks) where this value occurs" | a row or block set; may answer the predicate outright | O(query terms) |
| **exactness** | `Exact`: `true` bits are exactly the matching rows. `Superset`: `false` bits are proven, `true` bits are "maybe" | whether the predicate is re-checked on survivors | — |
| **granularity** | `row`, `block` (a fixed `block_len` rows counted from row 0), `generation` (`k` consecutive blocks), `file` | how a probe result becomes a block mask or a row selection | — |
| **run** | an immutable payload covering a contiguous range of blocks; an index is a sequence of runs | how an append extends an index without touching it | — |

An index is defined by four things: a **kind id** (versioned string), a **column path**, a
**granularity**, and **runs** made of ordinary Vortex arrays (§4.2). Everything a reader needs to
decide whether it can use the index is in the directory entry; everything it needs to probe is in
the runs.

---

## 3. Where an index lives, and why

### 3.1 The rule

A file with an index must open and scan, without error and without configuration, in:

- Rust 0.86.1 with a **strict** session (the default; what DuckDB, DataFusion, Spark and the
  Python bindings use);
- any Vorticity that predates this document;
- the current Vorticity, which then uses the index.

### 3.2 The carrier: a postscript metadata entry

The postscript already carries user metadata: up to sixteen `(key, segment)` pairs, keys unique and
at most 64 UTF-8 bytes, values loaded **lazily** by every reader
([02-format.md](02-format.md) §2; `footer.fbs` `PostscriptMetadata`). Rust writes them with
`with_metadata_segment` and reads them with `metadata_segment(key)` as opaque bytes
(`vortex-file-0.86.1/src/writer.rs:167`, `file.rs:143`); our reader exposes them through
`VortexFile.MetadataCount` / `GetMetadataKey` and a lazy fetch; our postscript writer already has
`PostscriptWriter.WriteMetadata`.

So: **one** metadata entry, key `vorticity.index`, whose segment holds the **index directory**
(§4.1). The directory points at **run segments**: file regions written after the last data
segment and before the footer, 64-byte aligned, **not referenced by the layout tree** and not
listed in the footer's segment table. A reader that does not ask for the key never touches them.
Two nuances from the Rust reader, so that nobody relies on more than it does: metadata is loaded
only when the caller asks for it at open (`open.rs:265-270`, `file.rs:133-145`), and every
metadata segment's bounds are validated against the file size at open, whether loaded or not —
the directory segment is one of them, so it must lie inside the file, which it does by
construction. The run segments are not metadata entries and Rust does not know they exist.

What this costs: one metadata slot out of sixteen (the directory is one entry however many indexes
the file holds); and the run bytes are dead weight for a reader that ignores them, which is the
same cost upstream's own designs accept. *Since step 20 a second slot is this library's too:
`vorticity.identity`, which every file carries ([13-dataset.md](13-dataset.md) §7). Fourteen
remain for the caller.*

### 3.3 The carriers we do not use, and when we will

| carrier | what happens in a strict Rust 0.86.1 reader | verdict |
|---|---|---|
| a new aggregate in `vortex.zoned` | **open fails**: unknown aggregate id (`schema.rs:60`) | only once an edition declares the id, and then with **upstream's** id and wire format, not ours (§9) |
| a wrapper layout (`vortex.indexed`, upstream's direction) | **open fails**: unknown layout id, and the data beneath it is unreachable | our migration target when it freezes; the runs move as they are, only the pointer changes (§9) |
| a sidecar file (`file.vortex.idx`, the Iceberg Puffin pattern) | unaffected | legitimate for **post-hoc** indexing on stores that cannot append (§8); two objects to keep consistent |
| a new field in the `Postscript` FlatBuffer | ignored (FlatBuffers readers skip unknown vtable slots) | rejected: it would give our bytes a field id upstream may later assign; the metadata map exists for exactly this |

---

## 4. The directory and the runs

### 4.1 Directory

A hand-written protobuf message, like every other metadata this library writes
([02-format.md](02-format.md) §5.3), preceded by one version byte as `vortex.zoned` does.

```proto
message IndexDirectory {
  uint32 version = 1;                 // 1
  uint64 row_count = 2;               // the file's row count when this directory was written
  uint64 previous_eof = 3;            // file length before the append that wrote this directory; 0 for a first write
  bytes  policy = 4;                  // the serialized WritePolicy, so an append needs no options
  repeated IndexEntry entries = 5;
  uint32 budget_per_mille = 6;        // (step 17) absent means 100
  uint64 file_length = 7;             // (step 17) a sidecar's indexed file
  bytes  file_sha256 = 8;             // (step 17) a sidecar's indexed file
  repeated string array_encodings = 9; // (step 17) the encoding table a sidecar's payloads name
}
message IndexEntry {
  string  kind = 1;                   // "vorticity.bloom.sbbf.v1", ... (§5, §6)
  repeated uint32 column_path = 2;    // field indices from the root struct; empty = the root column
  uint64  block_len = 3;              // rows per block; the zone length
  bytes   options = 4;                // kind-defined, self-versioned
  repeated Run runs = 5;              // in block order, disjoint, covering [0, row_count)
}
message Run {
  uint64  first_block = 1;
  uint32  block_count = 2;            // a generation: this run's filters or keys cover these blocks
  repeated Segment payload = 3;       // file regions, in kind-defined order
  repeated bytes payload_dtype = 4;   // serialized DType (dtype.fbs) of each payload array
  uint64  entry_count = 5;            // entries of a locating run; 0 for a skipping kind (12 §14)
  bytes   options = 6;                // kind-defined, per run: the segment table of §4.2 (step 12c)
}
message Segment { uint64 offset = 1; uint32 length = 2; uint32 alignment_exponent = 3; }
```

Rules a reader enforces before using an entry (the three-class hint policy of
[08-semantics.md](08-semantics.md) §5 applies: an index is a *hint*):

- `row_count` equals the file's row count, or the whole directory is ignored (a stale directory
  after a failed append proves nothing).
- `column_path` resolves to a leaf column of the schema whose dtype the kind supports, or ignored.
- an unknown `kind` is ignored; a known kind with an unparseable `options` is ignored;
- runs are disjoint and in order, and every segment lies inside the file and before the footer,
  or the **entry** is ignored — never the file. An index can only ever cost pruning, never
  correctness. A block that no run covers is simply live.

**As delivered (step 12a).** `Indexes/IndexDirectory` is this message, with the policy of §5.5
serialized as a nested message of our own (one `ColumnPolicy` per override, sorted by path so that
one policy is one byte string). The reader rules are `IndexDirectory.TryParse`, which never
throws: a malformed or stale directory returns `false` with its reason, which
`VortexFile.IndexDirectoryRefusal` keeps for tooling. Beyond the list above it also drops an entry
whose run is empty, whose payload is missing for a kind that needs one, whose `payload_dtype` count
disagrees with its segments, or whose segment alignment exceeds 2¹⁶; and the bound "before the
footer" is the directory's own offset, since runs are written before the zone maps and the
directory after the statistics (§7.2). `VortexFile.ReadIndexDirectoryAsync` reads it once, lazily.
A directory is written whenever the policy asked for anything, even if every builder was
abandoned: it carries the policy an append will reuse, and "asked, nothing survived" must not
read as "never asked".

**As delivered (step 21, version 2 — [13-dataset.md](13-dataset.md) §7).** The writer writes
version 2: the version byte, the message whose `version` field says 2, then the XXH3-64 of both as
an 8-byte little-endian trailer. `Segment` gains `fixed64 checksum = 4`, the XXH3-64 of the
region's bytes, computed from the blob the writer has in hand. The reader checks the trailer before
it believes a byte: one flipped bit refuses the directory whole, as a stale one is. It then holds
every region it reads to its checksum (`IndexSegment.Holds`), and a region that fails costs what a
malformed payload already cost: the Bloom and key pruners make no claim for the blocks it covers,
and a key source refuses with a `VortexFormatException`. The read cost is one XXH3-64 per region
read, cached regions excepted; nothing reads a region to check it. Version 1 is still read, without
checksums: the forged fixture of §10 is one. The price on disk is 8 bytes per directory and 9 per
region. The test the format could not pass before now passes: a Bloom filter whose bits are zeroed
and whose framing is kept drops no row, and the same file with forged checksums does, which is what
the checksum is for (`LyingIndexTests.AZeroedFilterIsCaughtByItsChecksumAndCostsNoRow`).

### 4.2 Runs of ordinary arrays

Each payload segment is an array blob in the format of a `vortex.flat` segment
([02-format.md](02-format.md) §5.1), written by `ArrayBlobWriter` with the file's own encoding
dictionary and **compressed by the same compressor as data**, under the same target edition. Two
consequences that matter:

- an index inherits every encoding we have — a sorted key column bit-packs or FSST-compresses
  like any other column, a postings list delta-encodes — and no index defines a binary format of
  its own. This is upstream's central design decision in #9024 and it is the right one.
- the footer's `array_specs` may list an encoding used only by an index run. That is harmless to
  every reader because the ids are all in the target edition.

**A run is immutable and covers a contiguous block range.** A first write produces one run per
chunk for a locating index (its keys are the running table's entries, [11-write-strategy.md](11-write-strategy.md) §3.2.2) and one run
per generation for a Bloom; an append adds runs for the new blocks and, when it re-opened a
short last chunk ([11-write-strategy.md](11-write-strategy.md) §3.8), replaces the runs that
covered its blocks; a rewrite compacts runs into fewer, larger ones. A probe consults every run
overlapping the live blocks: for a locating index that is `k` lookups of `O(log keys)` each, the
log-structured price of never rewriting.

Runs are blocked like data: a key column longer than `payload_block_rows` (default 65 536) is
written as several segments with a per-segment min/max in the run's `options`, so a lookup reads
two segments, not the run. This is the "zones do the job of B-tree leaves and zone maps the job of
internal nodes" idea of #9024, applied through the directory rather than through a layout.

*Amended by [13-dataset.md](13-dataset.md) §6.3, delivered at step 23: the segment table is
bounded whatever the run's length.* A locating run of at most 64 segments keeps the version 1
options above, and the directory lists its `stride` regions per segment. A longer run writes its
table as **fence pages**: file regions of at most 64 KiB, each a protobuf message, arranged as a
tree whose root is inlined in the run's options. The directory then lists the root's child pages
as the run's payload, with no dtype per region.

```
message KeyRunOptions {              // version 2, a paged run
  uint32 version = 1;                 // 2
  FencePage root = 3;                 // level ≥ 1, at most 64 fences
  repeated bytes stride_dtypes = 4;   // the serialized dtype of each array of a segment
}
message FencePage {
  uint32 level = 1;                   // 0: its fences are segments; above: pages of level - 1
  repeated Fence fences = 2;          // in key order
}
message Fence {
  uint64 entries = 1; bytes min = 2; bytes max = 3;
  repeated Segment regions = 4;       // level 0: the segment's arrays; above: the child page
  uint64 segments = 5;                // above level 0: the segments under the child
}
```

A page carries the XXH3-64 of its bytes in its parent's `Segment`, like any region (§4.1). A reader
checks it, then the page's shape: the level its parent names, `stride` regions per segment, keys
that never go down, and a segment count equal to the parent's. A page that fails claims nothing
for its run in a pruner and makes a key source refuse.

### 4.3 Granularity and generations

`block_len` is the zone length. A skipping index at block granularity has one filter per block; a
**generation** of `k` blocks (`k ≤ 16` by default) has one filter over their union, built from the
retained hash buffers of those blocks when the `k`-th closes and sized from the sum of their
distinct counts; the **file-level** filter is the generation whose `k` is the whole file, when the
policy asks for one. A column may carry several resolutions; the scan evaluates the coarsest
first, and a finer filter is only probed for blocks still live. This is #7939's "multi-resolution
zone maps" without a change to `vortex.zoned`: the zone map stays what upstream reads, the coarse
maps are ours, out of tree.

*Amended by [13-dataset.md](13-dataset.md) §6.2: the generations are laid out as a filter tree of
fan-out 16, each node's children contiguous behind it, so that a probe descends and never reads
the per-block table whole. Delivered at step 24: the generations are the tree's level-1 nodes, and
every level above them is built the same way up to one root per run, which is the file-level
filter when the policy asks for three resolutions. One entry per column replaces one per
resolution; 13 §6.2's note has the layout.*

---

## 5. Skipping indexes

### 5.1 `vorticity.bloom.sbbf.v1` — a split-block Bloom filter per block or generation

**Structure.** `n_blocks` blocks of 256 bits (eight 32-bit words). Insert: `h = XxHash3-64(bytes,
seed = 0)`; block `= ((h >> 32) × n_blocks) >> 32`; in that block set one bit per word using the
eight Parquet salts on `(uint)h`. Probe: same, all eight bits set. The block layout and the salts
are Parquet's (`BloomFilter.md` in `parquet-format`) and the hash is upstream's: XxHash3-64 is two
to three times faster than xxHash64 on the short inputs a column holds, and it is what
`vortex.bloom_filter.sbbf` uses at 0.86.1, so our filter is bit-identical to the Rust
`BloomPartial` for as long as upstream keeps its layout — and can become theirs by a change of id
when they freeze it. **Parquet compatibility is a variant, not the default**: the `hash` option set
to xxHash64 produces a filter bit-identical to a Parquet SBBF, for the rare caller that exchanges
filters with Parquet tooling. Rebuilding an index is cheap, so a change of hash upstream costs a
`.v2` kind, not a migration.

**Hash input**, per dtype, so that equal values hash equal whatever the encoding they came from:

| dtype | bytes hashed |
|---|---|
| integers, floats, decimals | the value's little-endian bytes at the column's own width (floats by bit pattern; `-0.0` and `+0.0` are two values, as in [07-dotnet-mapping.md](07-dotnet-mapping.md)) |
| utf8, binary | the bytes |
| bool | not supported (a two-value domain has nothing to skip) |
| extension | the storage value's bytes |
| list elements | each element, into the **parent row's** block (§3.2.4 of the write spec): the filter answers "does any element of a row in this block equal v" |
| null | not inserted; `x = v` and `x IN (...)` never match a null |

*As delivered (step 28b).* A list or fixed-size list whose elements are one of the kinds above
takes a Bloom filter, named by its own path, top-level or nested. `BloomBuilder` hashes each
element of a valid row into the row's block; a null list names nothing and a null element is not
inserted. A list under a null struct names nothing either: the writer folds the parents' nulls
into the list's validity before the builder sees it. A list of lists and a map are refused with
their reason, and so is a trigram index on a list. The question is
`ListContains(list, v)` ([12-index-reads.md](12-index-reads.md) §7), and the pruner answers it
from the element filters; an equality on a list column claims nothing from them. `Auto` weighs a
list like any column. Its verdicts come at the block's close for a list view, whose block has no
element bound, and inside the block for a fixed-size list, whose block holds its rows times the
size. On the corpus and on the 1M-row list files `Auto` gives the filters up, so no byte moves;
the 1M-row `fixed_size_list` write stays at 1,019 of step 28a thanks to that bound.

**Size.** Built **when the block closes**, from the block's hash buffer, with `n_blocks` the
smallest power of two that gives the target false-positive rate (`fpp`, default 1 %) for the
block's **exact** distinct count by the Parquet sizing formula, clamped to `[1, max_blocks]`
(default 4 096 blocks = 128 KiB). A block whose distinct count is below `min_distinct` (default 8)
gets no filter: the zone map or the dictionary (§5.3) already answers equality there. A
generation is sized from the sum of its blocks' distinct counts, which over-sizes by the overlap
and never under-sizes. Upstream's fixed 256 blocks per zone (8 KiB) is both too much for a status
column and too little for a 100 000-distinct id column.

**Options.** `fpp` (as parts per million), `hash` (0 = XxHash3-64, the default; 1 = xxHash64, the
Parquet-compatible variant), `max_blocks`, and per block or generation a `u32 n_blocks` table so
that a probe can locate its filter without reading the others.

*Amended at step 24 (13 §6.2): the `n_blocks` table is gone. Options are version 2 — `fpp`,
`hash`, `max_blocks`, the fan-out of 16, `min_distinct`, `case_insensitive` and the root's
ceiling — and a filter is located through the tree, whose nodes name their children's regions.
`Indexes/BloomIndexOptions.cs` holds the message.*

**Probe rules** (a pruner in the scan's block-mask chain, [11-write-strategy.md](11-write-strategy.md) §6.1):

| predicate | proof |
|---|---|
| `x = v` | block pruned if `!contains(v)` |
| `x IN (v1..vn)` | pruned if no `vi` is contained; the literals are hashed once per query |
| `AND` | conjunction of proofs; `OR` | pruned only if every arm is pruned |
| `x != v`, `x < v`, `LIKE` | no claim (§5.2 for `LIKE`) |

**Cost.** One XxHash3-64 per value in the writer's statistics pass, shared with the distinct table
when it runs, plus eight bit sets per generation at block close (≈ 3 ns per value per
generation, vectorised). Budget to hold: under 5 ns per value on the write path with one
resolution, and at most `max_blocks × 32 B` per block on disk.

**As delivered (step 12b).** `Indexes/SplitBlockBloom` is the reference's `BloomPartial` line for
line, and `tools/conformance-gen/examples/gen_bloom_vectors.rs` proves it: 66 filters built by
vortex 0.86.1's own public `Accumulator` over typed arrays (every integer width, both floats with
both zeros, NaN and the infinities, nullable columns, strings on both sides of the inline limit,
binaries), which our filter reproduces byte for byte — and so does the writer's builder, fed the
same rows as canonical columns, wherever the clamp lets it size the same block count.
Four departures from the text above, each deliberate:

- **Exact distinct counts at every level.** A generation and the file are sized from the distinct
  count of the union of their blocks, kept in a hash set, rather than from the sum: the sum sizes
  a five-value status column's generation for eighty, the union for five — under the floor, so no
  filter, which is right for a column the zone map already prunes.
- **One entry per resolution**, `options` carrying the level (`0` block, `1` generation, `2`
  file), `fpp`, the hash, `max_blocks`, `k`, `min_distinct` and the packed `n_blocks` table (per
  block at block level, per run at the coarser ones): runs inside an entry are disjoint (§4.1), and
  a generation's filter covers the same blocks as the filters beneath it.
- **Runs are written when their generation closes**, between data chunks, not at `CompleteAsync`:
  §7.2 asks for both "nothing buffered across chunks" and "runs at CompleteAsync", and the first
  wins. The crosscheck writes 246 corpus files with payload regions between their chunks, and the
  reference reads all of them.
- **Payloads are written uncompressed** (a filter is uniform bits); still array blobs, `u32`,
  with their dtype in `payload_dtype`.

The probe is `Indexes/BloomPruner`, after the zone maps in the block-mask chain, one coalesced
read per level, coarsest first; `ScanBuilder.WithIndexes(false)` turns it off. It hashes a
literal only when it converts exactly into the column's type (an integer past 2⁵³ from a double,
a non-integral float, an out-of-range integer: no claim), asks for both zeros when the literal is
a zero, and asks for nothing on a NaN. A payload that is not the `u32` array its entry declares
makes no claim for the blocks it covers. What no reader can catch is a well-formed filter whose
bits are wrong: the format carries no checksum, and a zeroed filter would drop rows. *Since
step 21 the format carries one per region (§4.1's version 2), and a zeroed filter claims nothing;
only a liar who forges the checksums too is beyond a reader.* The
budget measured on the test fixture — 1,18 MiB of filters at 1 % against 0,50 MiB of ALP and
bit-packed data for 1 024 distinct keys per block — is the case the default budget refuses.

### 5.2 `vorticity.bloom.ngram3.v1` — a token Bloom for `LIKE` and `CONTAINS`

Same structure; the inserted keys are every byte **trigram** of every utf8 value in the block. A
`LIKE` pattern's literal runs (`'%foo-bar%'` → `foo-bar`) yield trigrams that must **all** be
present; a run shorter than three bytes yields no claim. Case-sensitive by construction; an
`ILIKE` variant inserts lower-cased trigrams and is a separate option bit. Sized from the estimated
distinct trigram count (bounded by 2²⁴). This is discussion #8682's trigram idea in its skipping
form. Superset only: the pattern is always re-evaluated on survivors. **Prerequisite**: `LIKE`,
`StartsWith` and `Contains` in `VortexExpr` and `FilterEvaluator` (§6.6).

**As delivered (step 12d).** The Bloom builder in trigram mode: every byte trigram of every valid
text value hashed into the block's set, sized like §5.1 from the exact distinct trigram count.
`IndexPolicy.NgramBloom(caseInsensitive)` folds **ASCII** bytes on both sides (`options` field 9);
a folded index still answers a case-*sensitive* predicate, since a value containing `Foo` contains
`foo` once folded, so the probe folds what it looks for. `ILIKE` itself does not exist in the
expression model, so the option buys a smaller filter and nothing else yet. `StartsWith`,
`Contains` and `LIKE` all probe it: a predicate requires the trigrams of each literal run between
its unescaped wildcards (`BytePattern.LiteralRuns`), and one absent trigram kills the block. Binary
columns are indexed as well as utf8.

### 5.3 `vorticity.dict.probe.v1` — the dictionary is already an exact index

When the writer dictionary-encoded a chunk, the chunk's values child *is* the set of distinct
values, small and exact. This kind has **no payload**: its entry says "the chunks of this column
are dictionary-encoded; probe the values child". A reader answers `x = v` by decoding the values
child alone (a few hundred bytes) and never the codes. It is the cheapest equality index there is,
it costs nothing to write, and the writer knows exactly when it applies. When a chunk is *not*
dictionary-encoded the reader gets no claim for that chunk and falls through to the next index.

**As delivered (step 33), and one sentence above is wrong.** The block pruner now answers `x = v`
and `x IN (…)` from the chunks' dictionaries, last in the chain of 11 §6 — after the zone maps and
the filters, before nothing, since it reads the column's own bytes. A chunk claims only the blocks
it covers **whole**, so a block two chunks share stays live, and the file's last block counts as
whole because no row lies past it. On `table_mixed` written with `Auto`, where `label` holds
sixteen values and the entry is 61 bytes:

| `label` | blocks live | bytes the plan reads |
| --- | --- | --- |
| a value no chunk holds | 0 of 123 | 539 696 (was 7 385 628) |
| a value every chunk holds | 123 of 123 | 7 924 232 (was 7 385 628) |

So the claim "a few hundred bytes" is not what a probe costs today: **a flat chunk is one segment**,
so decoding the values child means reading the chunk's bytes — 538 604 for `label`'s one chunk —
and only then are the codes skipped. The trade is that read against the whole scan: 13.7× less when
the value is absent, 7 % more when it is present. It pays on this file because the probed column is
one of six; it would break even on a file of one column, and it would become what §5.3 promised if
a dictionary's values were given a segment of their own.

### 5.4 The file-level filter

*As delivered (step 12b): `VortexFile.MayMatchAsync(expr)` answers from the statistics first and
then from the file-level entries alone; the resolution is opt-in (`resolutions: 3`) and gives up
past 2²² distinct values, which the report states. Since step 24 (13 §6.2) the file-level filter is
the root of each run's tree, and `MayMatchAsync` reads the roots alone. A policy of two
resolutions has a root too, under `max_blocks`, so it answers as well while its union fits.*

A generation with `k` = every block of the file, built at `CompleteAsync` from the sum of block
distinct counts, capped by `max_blocks` (default 1 Mi blocks = 32 MiB, rarely reached). It exists
for **multi-file** pruning: `VortexFile.MayMatch(expr)` reads it with the footer's file statistics
and skips a file before opening a scan. An append does not extend it (filters do not merge); it
adds a second file-level generation over the appended blocks, and a rewrite rebuilds one.

### 5.5 Policy: `Auto` by default

`IndexPolicy` per column: `None`, `Bloom(fpp, resolutions)`, `NgramBloom(...)`, `Postings`,
`SortedRuns`, or **`Auto`, the default**. `Auto` starts every cheap builder at block 0 and
**abandons** what the statistics disqualify or the budget refuses: a Bloom on a column the
statistics show sorted (min/max already prunes it), postings past the writer's `IndexBudgetBytes`
share for the column, a filter whose projected bytes exceed 2 % of the column's written bytes.
`dict.probe` is always recorded when it applies, because it is free. `WriteProfile.Fastest` sets
every column to `None`. The write-cost of `Auto` is measured before it becomes the default
([11-write-strategy.md](11-write-strategy.md) §5.3).

**As delivered (step 12e): `Auto` is the default, and it is `dict.probe` plus a Bloom filter.**
Measured by `bench/ab.sh` in one process on the `table_mixed` write, against the same write
without indexes: the directory and the dictionary probe alone cost nothing (0,992); a Bloom filter
per column costs **+6,6 %** [5,6 ; 6,9]; postings on top cost another +7 %, which is past the
+10 % of 11 §5.3. Postings are cheap in this section's argument because they would come "from the
tables we already build", and the writer's distinct table lives by plan memory and cannot feed
them; so here they cost an intern per row of their own and are not `Auto`'s. They stay one
`IndexPolicy.Postings` away. Sorted runs are never `Auto`'s either. With the verdicts below, the
same write costs **+0,6 %** [−0,4 ; 0,8], and every file of the write axis stays inside its
ceiling.

A Bloom filter born of `Auto` is given up, whole and with its reason, at the earliest fact that
condemns it, and always before its payloads are written, so an abandoned index leaves nothing in
the file: when the column's first block climbs (the zone map prunes it); when its filters would
exceed 2 % of the column's *raw* bytes, checked at every block before a filter is laid out (the
column compresses to no more than those) and, for fixed-width values, every 256 rows inside a
block — the distinct count so far only grows and a full block bounds the raw bytes, so the verdict
is the block close's, reached a block earlier; when a full first generation holds fewer distinct
values than the policy's floor (no filter of it was built, and the dictionary probe answers for
such a column); when its first four blocks hold one and the same set of values (a block filter
then answers "maybe" for every value the generation holds, and prunes nothing); and when they
exceed 2 % of the column's *written* bytes, checked at every chunk. The hash per row is the whole
cost of a live filter — +24 % on the corpus's `varbinview` write, +37 % on `zstd_buffers`, both
seventeen-odd values cycling — so these verdicts are what keeps `Auto` inside the throughput
ceilings; a memo of computed hashes was measured and saved nothing. A Bloom filter at 1 % costs about 1,2 bytes per distinct value
before the power-of-two rounding, so under this rule it pays on wide values that do not compress
and on medium-cardinality columns whose generations clear the floor, and nowhere near a column that
bit-packs to a byte a row. A directory is written only when it lists something or the policy is
the caller's own. The file budget counts the indexes still alive, never the dead weight of an
abandoned one.

*Amended at step 33, on the repeated-set verdict, which is right for a reason it did not give.* The
leaves of such a column do prune nothing — on `table_mixed`'s `label`, an explicit Bloom reads
5 840 bytes of filters for a present value and keeps all 123 blocks — but the **root** of that same
tree answers an absent value in 180 bytes and kills all 123, where the zone map alone reads
7 385 628. What is unaffordable is not the root's bytes (6 kB for the whole tree) but the pass that
fills it: one hash and one set insert per row, measured at ~9 ms per million rows. On a column the
chooser writes for almost nothing — `zstd_buffers`, 630 724 bytes for a million rows — that pass
takes the write from 5 ms to 14, far past the +10 % of 11 §5.3; on `table_mixed`, where five other
columns pay the bill, the same filter costs 1 ms in 162 and does not show. A writer cannot tell
those apart without timing itself, which would make its bytes depend on the machine, so `Auto`
keeps the pessimistic verdict — and §5.3's dictionary probe answers absence on exactly these
columns for no write at all. An explicit `IndexPolicy.Bloom()` buys the root back at that measured
price.

*Amended at step 24 (13 §6.5):* `Auto` also gives up a column whose first generation holds more
values than a node of `max_blocks` holds at the target rate. That generation's node would have no
filter, and a probe would read every block's filter; a sorted run is the structure for such a
column. The share now counts every level of the tree. A level costs about what the level below it
costs when values do not repeat, so a column of unique values needs wider values than before to
stay under 2 %.

---

## 6. Locating indexes

### 6.1 `vorticity.postings.blocks.v1` — value → blocks, from the tables we already build

The writer's running table already holds the distinct values of each chunk, with their bytes, and
restamps a key the first time it is seen in a block ([11-write-strategy.md](11-write-strategy.md)
§3.2.2): that restamp *is* the posting, appended to the key's block list in the fused pass, so the
run exists when the chunk closes without a further pass. A postings index keeps the table running
on every chunk of its column. At the end of each chunk the builder closes a **run**: `keys` (the chunk's distinct values, sorted and
deduplicated, compressed like any column) and `postings` (per key, the sorted list of **blocks**
of that chunk where it occurs, a `List<u32>` whose elements are delta-then-bit-packed). Probe:
for each run overlapping live blocks, locate `v` in `keys` (two reads with the blocked payload of
§4.2), take its postings, clear nothing, keep those blocks. Exactness: `Superset` at block
granularity — the block *does* contain the value, but not every row of it. Memory at write: the
running table, already paid for, and one block list per key. Nothing is buffered across chunks, so the builder never abandons
for size on its own; it abandons when the writer's budget for the *file* is exceeded by the sum of
runs. A rewrite compacts runs into one per generation.

### 6.2 `vorticity.sorted.runs.v1` — value → rows, exact, in log-structured runs

*Amended by [13-dataset.md](13-dataset.md) §6.1: the per-chunk runs are spilled and merged at
`CompleteAsync` into one run per entry, with hierarchical fences and a checksum per segment, and
at most K = 4 runs stay in flux after appends. A lookup no longer probes one run per chunk.
Delivered at step 22 for postings (§6.1 above), trigram postings (§6.4) and sorted runs, composite
keys included: one run per entry, plus the last chunk's own when the rows are not whole blocks; a
run's rows at `u64` when it spans 2³² rows or more. 13 §6.1's note says how, and what the K rule
costs over many appends.*

Per chunk, a run of `keys` sorted with their **row positions** (`u32` when the file has under
2³² rows): a sort of one chunk's rows, O(chunk log chunk) in time and one chunk in memory, in
flux. A probe locates `v` in each overlapping run (a per-run min/max in `options` skips most), and
the union of the row lists is the answer to `x = v` and to `x IN (...)`: `Exact`, so a scan serves
it through the row selection path `Take` already implements and never re-evaluates the predicate.
Range predicates (`x BETWEEN`) are a contiguous slice of each run's keys, also exact. This is a
log-structured merge tree without the merge: runs are compacted only at rewrite, when the whole
file is re-encoded anyway. A global sort at `CompleteAsync` — the first iteration of this
document — would have buffered the whole column and is rejected for the reason
[11-write-strategy.md](11-write-strategy.md) §9 gives.

**As delivered (step 12c), for §6.1 and §6.2 together.** `Writing/KeyIndexBuilder` keeps its own
per-chunk key table (`ChunkKeys`: interned bytes and a log of `(key, position)` pairs) rather than
the writer's running table, whose life is governed by plan memory; the second hash per row is paid
only by columns that ask. Ingest runs ahead of emission by the carried rows, so a chunk close
**cuts** the log at the chunk's end: without a carry the whole table becomes the run, with one only
the suffix is re-interned. The run's order is `Keys/KeyOrder.Total`'s, implemented on the key bytes
by `Indexes/KeyLayout` (a test holds the two equal over every primitive type); sorted runs are a
counting sort of the chunk's rows by the rank of their key, so one sort of the chunk's *distinct*
keys orders everything. Payloads, `stride` arrays per segment, compressed like data: postings are
`keys` (distinct, sorted), `offsets` (`u32`, one past each key's list) and `blocks` (`u32`, relative
to the run's first block) — two flat arrays in place of a `List<u32>`; sorted runs are `keys` (one per
entry) and `rows` (`u32`, relative to the run's first row, `first_block × block_len`). `Run` gains
**`bytes options = 6`**, which §4.2 already assumes ("a per-segment min/max in the run's
`options`") and §4.1's message lacked: the segment table, each segment's entry count and first and
last key. `IndexPolicy.WithSegmentEntries` sets `payload_block_rows`. Decimals are refused: the
kernels have no literal domain for one, so nothing could be looked up.

The probe, `Indexes/KeyIndexPruner`, runs after the Bloom filters and reads only the segments whose
range can hold a literal of the filter, in one coalesced read; a run's blocks start "absent" for
every encodable literal and are lifted where a lookup places it, or wholesale when a lookup fails.
Being exact at block granularity, it leaves live exactly the blocks that hold the value — the tests
assert the equality, not a bound. The row-selection half of §6.6 (an exact index delivering its rows
without re-evaluating the predicate) is the `SortedRuns` source's, step 13 — delivered as
[12-index-reads.md](12-index-reads.md) §9 describes: a cover of at most a batch of rows is read as
a take and the predicate is not evaluated; a larger one is pruned at block granularity as here.

### 6.3 `vorticity.hash.rows.v1` — hash → rows, superset

`keys` replaced by 64-bit hashes; smaller and cheaper to build than a sort, `Superset` because of
collisions. Kept as an option for very long keys; in practice §5.1 plus §6.1 cover the same
queries at a fraction of the size, and this kind is measured before it is implemented.

**Measured, and not implemented (step 31, 2026-09-17).** One column of 200 000 keys, 25 blocks,
one run per entry; the sorted runs' payload is what the file holds, the Bloom's is what the report
says, and the hash index is priced at its floor: eight bytes of hash and four of row, neither of
which compresses (a hash is noise by construction, and the rows follow the hash order).

| keys | `sorted.runs` (exact rows, ranges, cursors) | `hash.rows` (superset rows) | `bloom.sbbf` (maybe, per block) |
|---|---|---|---|
| `tenant/…/session/…`, 64 B | **3,69 B/entry** | 12 | 3,98 |
| hex of a 64-bit counter, 64 B | **11,77** | 12 | 3,98 |
| six bits of entropy a character, 64 B | 50,79 | 12 | **3,98** |
| the same, 128 B | 98,76 | 12 | **3,98** |
| `tenant/…` cut to 16 B (97 distinct) | **0,01** | 12 | — (under the policy's floor) |

The kind is never the best answer. Where keys carry structure — the shape a key column has when
anyone asks for a key index — the sorted keys compress **below** the hashes, and they are exact.
Where the keys are incompressible, the hashes do win over the sorted runs, four to eight times
over, but the Bloom is three times smaller again for the equality the hash index answers, and what
the hash index adds over it — the rows rather than the blocks — is what the sorted runs give
exactly, with the ranges and the cursors ([12-index-reads.md](12-index-reads.md) §4). So a third
kind would buy a band between two structures, at the price of a format id, a builder, a probe, a
source and the superset re-evaluation path.

What would reopen it: a measured workload of row-level lookups on incompressible long keys, where
block granularity is not enough and 50 to 100 bytes an entry is refused. The design above is what
it would be implemented from.

### 6.4 `vorticity.postings.ngram3.v1` — trigram → blocks

§6.1 with trigrams as keys: the locating form of §5.2, for `LIKE '%…%'` and `CONTAINS` at block
granularity over text columns. Bigger than the Bloom (one postings list per distinct trigram) and
strictly more selective; the usual choice is one or the other per column. Same prerequisite on
the expression model.

**As delivered (step 12d).** The postings builder in trigram mode, `IndexPolicy.NgramPostings`:
its keys are trigrams typed `binary` — a trigram may cut a UTF-8 code point — with the same
segments, the same probe, and the entry's `options` field 3 for the folding. Exact at block
granularity, it keeps live exactly the blocks whose rows together hold every required trigram; the
tests assert that count.

### 6.5 Composite keys through the row encoding

[06-row-encoding.md](06-row-encoding.md) already turns one or more columns into `memcmp`-ordered
byte strings. A locating index over `(a, b)` is §6.2 with the row encoding of `(a, b)` as its key
column: no new comparator, no new key format, and prefix queries on `a` alone are a range over the
encoded keys. `column_path` then lists several paths, in key order. The price is the key size the
row encoding produces, which the spec quantifies per type.

**As delivered (step 16, amending the above).** `column_path` is one list of field indices, so
`(a, b)` and `a.b` could not be told apart in it. A composite entry therefore keeps the kind
`vorticity.sorted.runs.v1`, leaves `column_path` **empty**, and carries its key columns in its
options: field 4, one message per column with its field indices (a nested column's whole path), and
field 5, the encoder's `Format` (`vortex-row 0.86.1 asc-nf`) — the row format is experimental, so
the bytes say what they follow. A reader that does not know these fields resolves the empty path to
the root struct and ignores the entry, which is the hint rule of §4.1. The encoder lives where
12 §13 proposed: `VortexWriteOptions.KeyEncoder`, an `IKeyEncoder` the core names and the
`Vorticity.RowEncoding` package implements (`RowKeyEncoder`); the policy is
`WritePolicy.ForKey(paths, IndexPolicy.SortedRuns)`, serialized in the directory's policy as field 3
(a column policy plus its repeated key paths, field 10). A key asked for without an encoder is
abandoned with that reason. The builder is the sorted-runs builder over binary keys: each fed range
of the key columns is sliced (records, not bytes), encoded together, and a row whose tuple holds a
null is not an entry, as a null is in no index.

### 6.6 Reading with a locating index

The scan contract is [11-write-strategy.md](11-write-strategy.md) §6: every structure refines a
block mask, cheapest first, and a partially live split becomes a row selection so that only its
live blocks are decoded; an `Exact` index covering the whole predicate delivers its rows as that
selection. `ScanBuilder.WithIndexes(false)` turns the chain off; the equivalence test of
[08-semantics.md](08-semantics.md) §1 — the same query with indexes on and off returns the same
rows — is the acceptance test for every kind, on written and appended files, and the fuzzer feeds
it forged payloads to prove that a lying index can only slow a scan down.

What the expression model must gain first: `LIKE`, `StartsWith`, `Contains` (for §5.2 and §6.4),
and nothing else — `Eq`, `In`, comparisons, `IsNull`, `And`, `Or`, `Not` exist.

---

## 7. Writing an index

### 7.1 API

`VortexWriteOptions.Indexes`: a `WritePolicy` — per column path an `IndexPolicy`, `Auto` where
unset — serialized into the directory so that `VortexFileWriter.Append(path)` reuses it without
being told. `WriteProfile.Fastest` disables everything. `CompleteAsync` returns the `WriteReport`
of [11-write-strategy.md](11-write-strategy.md) §7.3, which names every index built and every
one abandoned with its reason.

**As delivered (step 12a).** `Indexes/IndexPolicy` (`None`, `Auto`, `Bloom(...)`,
`NgramBloom(...)`, `Postings`, `SortedRuns`) and `WritePolicy` (a default and overrides by column
path, immutable, `For(path, policy)`); `VortexWriteOptions.Indexes`, `Profile` and
`IndexBudgetPerMille` — a share of the data bytes rather than a byte count, because the right
ceiling for a 10 MiB file and a 10 GiB one is not the same number. **The default is
`WritePolicy.None` until step 12's measurement of `Auto` lands**, as §5.5 requires; until then
every file written with the defaults is byte for byte what it was. `CompleteAsync` returns a
`WriteReport` (bytes by kind, summing to the file's length; the chunk sizes; per column the scheme
of every chunk and the plan-memory hit rate; per index built or abandoned with its reason). A kind
this writer does not build yet is reported abandoned with that reason, never silently skipped.
The first kind is `dict.probe` (§5.3), recorded under `Auto` as one payload-free run per maximal
range of consecutive dictionary-encoded chunks.

**As delivered (nested columns, 2026-09-17).** An override whose path is not a top-level column,
`For("person.address.city", …)`, is built on that leaf. Its entry's `column_path` is the whole
field-index path, and its builder is fed the leaf's node reached through the structs (and the
extensions around them). A row one of its parents nulls out is no entry: the leaf is re-published
over the range with the parents' nulls in its validity. The default policy and `Auto` choose among
the top-level columns only, so an `Auto` override on a nested path is reported abandoned with that
reason, and so is an override naming nothing in the schema; until then both were dropped without a
word. The reader already resolved nested paths. What it lacked is the reference's `get_item` rule,
`field.mask(struct.validity)`: a filter or an extreme on `person.name` read the buffer under a null
`person`. `FilterEvaluator.Resolve` now masks every field it descends into.

### 7.2 Build

Every index is a **streaming builder** owned by the column's `ColumnWriter` and fed by the fused
pass: it receives each block's hashes (from the hash buffer), the block's distinct count and the
restamps of the running table (a key seen in a block for the first time) and, at chunk end, the
table's entries in code order with their owned keys and the codes buffer. Bloom filters are built at block close; postings
and sorted runs at chunk close; generations when their `k`-th block closes; nothing is re-read
and nothing is buffered across chunks except the retained hash buffers of an open generation.
Runs are written at `CompleteAsync`, after the last data segment and before the zone maps; the
directory last. Memory is accounted per builder against the writer's `IndexBudgetBytes`, and every
kind abandons cleanly, whole, with a reason in the report.

### 7.3 What it must not cost

- A file written with `Profile = Fastest` is byte-identical to today's output. `WrittenSizeTests`
  holds. *Amended at step 20 ([13-dataset.md](13-dataset.md) §7): every file now carries its
  identity, so the promise is kept under one pinned `VortexWriteOptions.Identity` —
  `Fastest` is byte-identical to a write under `WritePolicy.None` — and `WrittenSizeTests` moved
  once, by the identity entry.*
- No builder adds a pass over the column: the hash comes from the buffer, the keys from the table.
- `WriteAllocationTests` gets one row per index kind, so a builder that allocates per row is caught.

---

## 8. Indexing a file that already exists, and appending to one

*Amended by [13-dataset.md](13-dataset.md) §6.4 and §7: the sidecar is retired. A post-hoc index
on a store that cannot append is a fragment inside a commit object, bound by identity, never by a
content hash on the read path.*

Three ways, none touching a data byte:

1. **Append** ([11-write-strategy.md](11-write-strategy.md) §3.8): new blocks, new runs, a new
   directory that lists old and new runs, a new footer and postscript. The directory's
   `previous_eof` and `vxdump --repair` handle a torn append.
2. **Post-hoc indexing by append.** Read the file, build the runs, append them with a new directory,
   footer and postscript that reference the same data segments. The cheapest way to index a file
   written without indexes, and the file format's own property.
3. **Sidecar.** On stores that cannot append (S3), write `file.vortex.idx`: the same directory and
   runs, with a `file_sha256` and `file_length` in the directory so a stale sidecar is refused.
   The reader looks for the sidecar only when the file carries no directory of its own.

A rewrite (read → write with `Indexes`) is always possible, always the slowest, and the only one
that compacts runs.

**As delivered (step 17).**
- **Append**: 11 §3.8's as-delivered note. Runs that end before the append's first block are kept
  and merged with the new ones; a run reaching into a re-opened chunk is dropped.
- **Post-hoc indexing**: `VortexFileIndexer.AppendIndexesAsync(path, policy, options?)` reads the file
  chunk by chunk, feeds every row to the builders block by block, and appends the runs, the
  statistics bytes again, a new directory (an old entry of another kind or column is kept), the old
  layout bytes, a footer rewritten only to extend the array-encoding table the payloads name, and a
  postscript. The memory-mapped reader cannot share the file with a writer, so the tail is laid out
  in a scratch file at its final offsets and copied behind the file once the reader is closed; the
  old file is a byte prefix of the new one, which the test asserts.
- **Sidecar**: `VortexFileIndexer.WriteSidecarAsync(path, policy)` writes `path + ".idx"`: `VXIX`, the
  payloads, the directory, and a 20-byte trailer (directory offset and length, version, `VXIX`). The
  directory gains three fields a file's own directory leaves empty: `file_length = 7`,
  `file_sha256 = 8`, and `array_encodings = 9`, the table the sidecar's payloads name, since the data
  file's footer is not the sidecar's to extend. The reader takes a sidecar only when
  `VortexReadOptions.IndexSidecarPath` names one — opt-in, so that a file without a directory is not
  probed for a second file on every scan — and only when the file has no directory of its own; a
  length or SHA-256 mismatch refuses it as stale, and the hash costs one read of the file at the
  first index read. Payloads are then read from the sidecar and decoded against its table
  (`VortexFile.IndexSource`, `CreateIndexContext`).
- **Sidecar, amended at step 26 (13 §7).** The binding no longer reads the file.
  - **Fields.** Field 8 is not written, and a sidecar that carries only it is refused with a
    reason: rebuild it. Three fields replace it:
    - `file_identity = 10`, the sixteen bytes of the file's `vorticity.identity`;
    - `file_token = 11`, the store's token, `fs:<length>:<modification ticks>` on a file system;
    - `file_hash = 12`, the XXH3-128 of the file's bytes, computed by the indexer, which reads the
      file whole anyway.
  - **Reader.** The reader checks the length, then the identity, both from the tail the open
    already holds. It checks the token only for a file without an identity, and that token is
    taken when the file is opened from a path with a sidecar asked for. It never computes the hash.
  - **`vxdump`.** `vxdump --sidecar P --verify` does: it checks every region the directory lists
    against its checksum, and the file's bytes against the recorded hash. It exits with 6 when one
    fails.

**A key source needs a complete index.** Found by the append tests: a pruner can use runs that cover
part of the file (a block no run covers is live), but a cursor over them misses the other blocks'
keys, and the exact cover counted the old rows only after an append whose builder the budget had
abandoned. `SortedRunsSource` now refuses an entry whose runs do not cover every block, with the
reason; the same rule refuses a dictionary probe over a column some of whose chunks are not
dictionaries, which step 15's source had accepted.

### 8.1 Proposal: immutable data, incremental index fragments, one manifest *(2026-09-17, proposed, not implemented)*

This second draft replaces the first one written the same day, which kept both mistakes named in
§8.1.1.

**Superseded the same day by [13-dataset.md](13-dataset.md)**, which keeps the diagnosis of §8.1.1
and challenges the rest point by point (its §13.I): this draft still pays, on the read path, for
the number of objects, of commits, of appends, of blocks and of fragments. It stays here as the
record of what was weighed.

**Priorities, in this order.**
1. **No wrong answer** under any interleaving of writers, indexers and readers.
2. **No read-path cost that grows with the data.**
3. **No coordination on the read path.**
4. **Every change incremental.**

The project is unpublished, so breaking changes to what §8 delivered are accepted: everything is
rebuilt.

#### 8.1.1 The design error

Vortex exists to read a few ranges out of a very large object. As delivered, the sidecar checks
its binding by hashing **the whole data file** at the first index read, so using the index of a
10 GB object on S3 first downloads the 10 GB. That is the opposite of the format's premise. A
faster hash (XXH3) would reduce the CPU, not the bytes read.

**The two causes.**
1. *The data file is treated as mutable.* An append writes into the same file under the same name,
   so an index outside the file has to prove which content it describes.
2. *An index outside the file is one object, bound by content.* Any change rewrites it whole, and
   proving the binding means reading the content.

**What follows from them.**

| # | problem | consequence |
|---|---|---|
| P1 | full-content hash on the read path | cost proportional to the data |
| P2 | an append does not maintain the external index | it goes stale silently: refused, so safe, but lost |
| P3 | the external index has a fixed name and is written in place | a torn or mismatched index is caught only by the hash |
| P4 | an append writes its postscript last, in the same file | a reader that opens during an append fails on the torn tail |
| P5 | no checksum on the index directory itself | corruption is caught only by the structural checks |
| P6 | a single writer by convention only | two appenders corrupt the file |
| P7 | an external index is all or nothing | no incremental update, no progressive build, no partial rebuild |

A stale index is not merely slower: a filter or a run built before new rows says "absent" where
the value now is, and the scan skips rows. The binding is a correctness matter.

#### 8.1.2 Principles

1. **A committed data object is immutable.** Changing the data means committing new objects;
   no byte of a committed object is ever rewritten.
2. **An index is derived data, made of immutable fragments.** Each fragment covers one block range
   of one data object. An index grows, shrinks and is rebuilt by adding and removing fragments.
3. **One small manifest per version is the only commit point.** Everything it references is
   immutable, so a reader holding a manifest has a snapshot: no locks, no waiting.
4. **The read path never reads bytes in proportion to the data.**
5. **Binding is by identity, obtained from bytes the reader reads anyway** (the store's metadata,
   the data object's tail). A content hash is an offline `verify` operation, never a read-path
   step.
6. **Everything derived can be rebuilt from the data objects alone.**

#### 8.1.3 The objects (the word "sidecar" is retired)

A **dataset** is a prefix (a directory, or an S3 key prefix). It holds three kinds of object:

| object | key | content | mutable |
|---|---|---|---|
| data object | `<prefix>/data/<uuid>.vortex` | a plain Vortex file, readable by Rust; may embed indexes as today (§3) | never |
| index fragment | `<prefix>/index/<uuid>.vxix` | the runs of one entry (kind, column path, options) over one block range of one data object, then a footer: the entry's description, its runs' segment tables, an XXH3-64 of the footer | never |
| manifest | `<prefix>/manifest/<version, 20 digits>.vxm` | the version, its parent, the data objects in row order with their identity (§8.1.4), and for each object the index entries with their fragment references (key, size, XXH3-64, block ranges), the write policy, an XXH3-64 of the manifest | never |

- A single existing Vortex file becomes a dataset of one data object: a manifest referencing it,
  with no copy.
- The in-file index of §3 stays. It is an **embedded fragment**, committed with its data by the
  file's postscript, and a manifest may reference it as such.
- What §8 called a sidecar was one fragment plus an embedded manifest, bound by content hash. It
  is removed.

#### 8.1.4 The identity of a data object

A manifest records, for every data object, its key, its size and a **version token**:
- **Written by Vorticity:** a 16-byte random `uid` in a postscript metadata entry
  (`vorticity.identity`) placed immediately before the postscript. The 65 535-byte tail read at
  open always covers it, so checking it costs **no request**.
- **Given by the store, when it gives one:** the S3 `VersionId` (versioned bucket) or `ETag`,
  returned by the `GET` of the tail at no cost; on a local file system, the file id with its size
  and modification time.

At open, the reader checks the size and whatever tokens the manifest recorded against what the
tail read returned.
- Data objects are committed under unique keys and never rewritten, so a mismatch means an
  out-of-band change. The data object is refused, and the scan of that version fails with that
  reason: the data is not what the manifest describes.
- A foreign Vortex file (no `uid`) is bound by the store token alone. On a local file system,
  where that token is only a heuristic, the limit is stated in the manifest and `verify` is the
  remedy.
- **`verify`**, offline: an XXH3-128 (`System.IO.Hashing.XxHash128`) of each object, recorded in
  the manifest when the dataset asks for it. XXH3 guards against accident, not forgery.

#### 8.1.5 Changing a dataset

- **Adding rows, on every store.** The rows go into a **new data object**, with its embedded
  indexes, and a new manifest lists the old objects plus the new one. The 10 GB object is not
  touched. Small appends make small objects, which compaction merges (§8.1.7).
- **Indexing incrementally.** An indexer builds fragments for (data object, block range): a new
  kind, a new column, or blocks not yet covered.
  - The work is resumable and parallel by range, and each batch of fragments is one commit.
  - Partial coverage is already correct (§8, "a key source needs a complete index"): a pruner uses
    the blocks a fragment covers and keeps the others live, and a key source waits until the
    coverage is complete. A 10 GB object can therefore be indexed progressively without ever
    answering wrong.
- **Rebuilding.** A commit removes an entry's fragments; the indexer builds them again, under a
  new policy if it changed.
- **Out of scope:** deleting or updating rows (deletion vectors, row-level updates). That is table
  format territory; see decision 5.

#### 8.1.6 The commit, and concurrency

**A commit is the creation of `manifest/<N+1>` if it does not exist.**
- S3: `PUT` with `If-None-Match: *`, which is atomic.
- Local file system: write a temporary file, flush it, then create the final name without
  overwriting (a hard link, or `File.Move` without overwrite), then flush the directory.

**A writer:**
1. reads manifest `N`;
2. writes its new objects under fresh keys: data objects, fragments;
3. tries to create `N+1` with parent `N`.

If `N+1` already exists, the writer reads it and **rebases**:
- Additions commute. Row numbers are per data object, so a fragment never depends on its object's
  place in the dataset. The writer re-applies its additions on top and tries `N+2`.
- An addition that references an object the new version removed (by compaction) is dropped. Its
  object becomes garbage.

No lease is needed: put-if-absent linearizes the commits, and a stale writer simply fails its
creation.

**A reader:**
1. finds the latest version: it reads the `manifest/_latest` hint (written after each commit,
   best effort, possibly stale), then probes `N+1`, `N+2`… with `HEAD` until one is missing;
2. reads that manifest;
3. opens data objects and fragments lazily.

Everything it reads is immutable: a snapshot, with no lock.

**Garbage collection (`vacuum`).** It deletes the objects no manifest within the retention window
references, then the manifests older than the window, keeping the latest. The window must exceed
the longest reader and writer lifetime. A reader that outlives it loses objects; that is reported,
never answered wrong.

**Interleavings.**

| interleaving | result |
|---|---|
| a reader opens during any commit | the previous version, whole |
| two writers commit | one creates `N+1`, the other rebases onto `N+2`; nothing is lost |
| a crash before the commit | orphan objects, collected; no version names them |
| an indexer and a compaction race | the fragment for a removed object is dropped at rebase |
| a torn or corrupt fragment or manifest | its XXH3-64 refuses it: the fragment's entry is ignored, the manifest's version is not readable |
| a data object changed out of band | its token mismatches; that version is refused with the reason |

#### 8.1.7 Compaction and rebuild

- **Data.** Small data objects are merged into larger ones, and the commit replaces their
  references. A large object is rewritten only on request.
- **Indexes.** The fragments of one entry are merged:
  - sorted runs by a streaming k-way merge;
  - postings by the same merge;
  - Bloom filters by OR when their sizes match, and from the data blocks otherwise.
- **Triggers:** thresholds on the number of fragments and of small objects. Compaction runs in the
  background and commits like any writer.
- **API:** `VortexDataset.CompactAsync`, `RebuildIndexesAsync(policy, scope)`, `VacuumAsync`,
  `VerifyAsync`, and `vxdump` verbs for each.

#### 8.1.8 The read-path invariant, and how it is held

Opening a version costs:
- the `_latest` hint and the probes, a few small requests;
- the manifest (kilobytes);
- per data object touched, the tail read of today;
- per index used, a fragment footer and the payload ranges the query needs.

None of these grows with the data. A **counting object store** in the tests asserts it: the bytes
and requests of "open, then a point query through an index" are identical over a 1 GB and a 10 GB
data object (a sparse or synthetic object, since only the bytes read matter).

#### 8.1.9 What stays and what goes

- **Stays:** in-file indexes at write time, as embedded fragments; the entry formats of §5 and §6;
  the partial-coverage rules; the three-class hint policy (08 §5).
- **Goes:**
  - the sidecar;
  - the directory's `file_length`, `file_sha256` and `array_encodings` fields, since a fragment
    carries its own encoding table;
  - post-hoc indexing by rewriting the file's tail, replaced by fragments;
  - in-place append (11 §3.8), see decision 1.
- **Added:** an XXH3-64 checksum to every directory, fragment and manifest.

#### 8.1.10 The store abstraction

`IObjectStore` with five operations:
- `GetRange`, which returns the version token with the bytes;
- `Head`;
- `PutIfAbsent`;
- `Delete`;
- `List(prefix)`, used by `vacuum` only, never on the read path.

It has three implementations:
- the local file system;
- an in-memory store, with injectable latency, failures and crashes, for the tests;
- S3, see decision 3.

#### 8.1.11 Tests before it is called done

- **An interleaving fuzzer** over the in-memory store: seeded schedules of readers, writers,
  indexers, compactions and vacuums, with crashes injected between any two store calls.
  - Every read answers what a scan with indexes off answers on the same version.
  - Every version inside the retention window references only existing objects.
- **Progressive indexing:** at every intermediate commit, answers equal those without indexes.
- **The rebase matrix:** every pair of concurrent operations.
- **Tampering:** a data object replaced out of band with the same size, a fragment or a manifest
  torn at every byte, a fragment of another object.
- **The counting store:** the invariant of §8.1.8.
- **Compaction equivalence:** the same answers before and after.
- **Rust:** every data object remains a plain Vortex file that Rust 0.86.1 reads (cross-check).

#### 8.1.12 Decisions left open

1. **In-place append (11 §3.8).**
   - Recommended: remove it. One model, the dataset, on every store, and one proof.
   - Or keep it as a single-file mode without a manifest, with its known limits (P4, P6).
2. **Manifests.**
   - Recommended: a full manifest per version, as long as it stays under about a megabyte.
   - Or a checkpoint plus deltas (Delta style) beyond that. Measure before choosing.
3. **The S3 client.** The rule of a single first-party dependency excludes the AWS SDK.
   - Recommended: a minimal client in the repository (SigV4, ranged `GET`, conditional `PUT`,
     `HEAD`, `DELETE`, `LIST`).
   - Or an adapter package outside the core.
4. **Retention:** a duration (recommended: 24 hours by default), a number of versions, or both.
5. **Scope.** This is a small table format, limited to appends, indexes and compaction.
   - Deletes and updates stay out.
   - Adopting Iceberg or Delta as the manifest layer, with Vortex files as their data files, is
     the alternative. It should be weighed before building our own, and upstream's plans checked.
6. **Identity in every file.** Recommended: always written. It costs about 40 bytes and no
   request, and §7.3's promise that `Profile = Fastest` is byte-identical to the pre-index writer
   is dropped with it.

---

## 9. Compatibility, editions and the road to upstream

| reader | file with an index directory |
|---|---|
| Rust 0.86.1, strict session (DuckDB, DataFusion, Spark, Python) | opens, scans, prunes with the zone map; the metadata entry is never fetched |
| Rust with `allow_unknown` | same |
| Vorticity before this document | same; the key is listed by `GetMetadataKey` and never fetched |
| Vorticity with this document | uses the index; on any inconsistency ignores the entry (§4.1) |

The file remains a pure core-edition file: every array in it, index runs included, carries an id
of the target edition. Our kinds are versioned strings **inside the directory**, so they need no
edition family and `EditionRegistry` does not change.

When upstream freezes a Bloom aggregate id in an edition, files targeting that edition may carry
the filter **in the zone map as well**, with upstream's id and wire format, and `Auto` prefers the
in-tree placement for readers that understand it. When `vortex.indexed` freezes, a locating index
becomes an auxiliary child of that layout: the runs are the same arrays, the directory entry
becomes the layout's metadata, and the out-of-tree carrier stays for older targets. Neither
migration changes a payload byte.

---

## 10. Tests and acceptance

- **Forged fixture read by Rust.** `tools/conformance-gen` forges a file with a directory, a Bloom,
  a postings index, a sorted run and a deliberately invalid entry; `verify_forged` reads it with a
  strict 0.86.1 session and compares every scalar. This is the test of §3.1 and it runs in
  `bench/crosscheck.sh`.
- **Equivalence property.** For each kind, on generated data: every supported predicate, indexes on
  and off, identical row sets ([08-semantics.md](08-semantics.md) §1's harness), on a file written
  once and on the same rows written then appended in two, three and eleven pieces.
- **Lying indexes.** Forged payloads that claim too little must never drop a row; the fuzzer owns
  this.
- **Runs.** A probe over `n` runs equals a probe over their compaction; a re-opened short chunk
  leaves no block covered twice or not at all.
- **False-positive rate.** `bloom.sbbf` at `fpp` 1 % measures under 1,5 % on uniform keys at the
  sized `n_blocks`; its bits match the Rust `BloomPartial` of 0.86.1 built from the same keys
  (generated by `tools/conformance-gen`, for as long as upstream keeps its layout), and the
  Parquet-compatible variant's bits match a Parquet SBBF built from the same keys.
- **Cost ratchets.** `WrittenSizeTests` unchanged with `Profile = Fastest`; per-kind rows in
  `WriteAllocationTests`; `Auto`'s cost on the write axis measured and bounded before it is the
  default.

---

## 11. Open questions

- The `Auto` thresholds (`min_distinct`, the 2 % payload budget, the column's share of
  `IndexBudgetBytes`) are guesses until the statistics pass exists; calibrate them on the corpus
  and on `table_mixed`.
- Whether `dict.probe` (§5.3) should be an index kind at all or a property the reader derives from
  the chunk's encoding tree with no directory entry. The entry costs nothing and makes the
  capability explicit; the derivation needs no writer support.
- Multi-column skipping (a Bloom over `(a, b)` pairs) via the row encoding, as §6.5 does for
  locating: cheap to add, unclear demand.
- ~~Whether to expose probes as a public API (`VortexFile.Probe(column, value)`) for engines that
  plan their own I/O, or keep them inside the scan.~~ Answered by
  [12-index-reads.md](12-index-reads.md) §5.1: the probe is `Scan().Where(x = v).AnyAsync()`, the
  same chain with one fewer type on the surface, and the cursor of its §4 is the surface for an
  engine that plans its own I/O.
- The default `k` of a generation (16) and whether `Auto` builds one at all without a policy.

**As delivered (step 33), answering the four above.** Every number below is one process against
`table_mixed` (1 M rows, six columns, 123 blocks, 7 445 145 bytes written with `Auto`) or the
throughput corpus, taking the best of three writes.

| Question | Answer | What the measurement said |
| --- | --- | --- |
| The 2 % payload budget (`AutoBloomShare`, 20 ‰ of the column's raw bytes) | **kept at 20 ‰** | It refuses every fixed-width column of the corpus, and that is right: an explicit Bloom on the six columns of `table_mixed` writes 10 672 264 bytes of filters against 7 445 204 bytes of data (143 %), takes the write from 164 ms to 315, and an absent probe then **reads 1 967 620 bytes of filters to skip 7 385 628 of data**. The rule bites at 31 ‰ for an 8-byte column at 10 000 ppm (2 048 bytes of filter per 65 536 raw), so 20 ‰ has margin on the only side that matters. |
| `min_distinct` (8) | **kept at 8** | It is a floor on a generation's distinct values, and the only corpus column it decides alone is `dict` (5 values): with §5.5's repeated-set rule taken out, the floor still refuses it, but only at the sixteenth block, and the file writes in 19 ms instead of 13. The floor is the right verdict late; the repeated-set rule is the same verdict at the fourth block. |
| The generation `k` (16) | **not a knob** | `k` is the tree's fanout, which every entry's options carry and a reader refuses when it is not its own. At `k` = 8 with the fanout left at 16, `table_mixed`'s filters grow to 20 019 048 bytes and **every** probe measured prunes nothing; moving both to 8 fails 28 tests of the index suite. Changing it is a format version, not a default. |
| `dict.probe` as a kind (§5.3) | **a kind, and now read by the pruner** | The entry is 61 bytes on `table_mixed` and 59 on the corpus's `dict`, and it is what lets a reader find the dictionary chunks without opening one layout — the `Dictionary` key source already did. Deriving it per chunk would cost a layout read to learn what one directory message says. §5.3 says what it now costs to use. |
| A Bloom over `(a, b)` pairs (§6.5) | **refused** | Three reasons, in order: the core has no encoder on the read side and cannot take one from `Vorticity.RowEncoding` without depending on it; a tuple's cardinality is the product of its columns', so the filter lands exactly in the regime the first row of this table rejects; and `WritePolicy.ForKey(paths, IndexPolicy.SortedRuns)` answers the same question exactly, per block, with fences. |

Decided (2026-09-15): XxHash3-64 is `System.IO.Hashing.XxHash3` taken as the **package** — the
first-party implementation in `dotnet/runtime` (MIT; `XxHash64` from the same package for the
Parquet variant). It ships as a NuGet package and is in no version of the shared framework
(checked from 8.0 to 11.0.0-rc.1), so it is the library's one first-party dependency:
[03-architecture.md](03-architecture.md) §1 admits it by name and keeps excluding third-party
packages.

---

## 12. Sources

Upstream issues and PRs read on 2026-09-15:
[#8900](https://github.com/vortex-data/vortex/issues/8900),
[#8901](https://github.com/vortex-data/vortex/issues/8901),
[#8948](https://github.com/vortex-data/vortex/issues/8948),
[#9024](https://github.com/vortex-data/vortex/issues/9024),
[#7939](https://github.com/vortex-data/vortex/issues/7939),
[#7707](https://github.com/vortex-data/vortex/issues/7707),
[PR #9398](https://github.com/vortex-data/vortex/pull/9398),
[PR #9413](https://github.com/vortex-data/vortex/pull/9413),
[PR #9753](https://github.com/vortex-data/vortex/pull/9753),
[PR #8933](https://github.com/vortex-data/vortex/pull/8933),
[discussion #8682](https://github.com/vortex-data/vortex/discussions/8682),
[discussion #6456](https://github.com/vortex-data/vortex/discussions/6456),
[editions.md](https://github.com/vortex-data/vortex/blob/develop/docs/specs/editions.md).
Crate sources at 0.86.1: `vortex-layout/src/layouts/zoned/{schema.rs,aggregates/bloom_filter/}`,
`vortex-file/src/{writer.rs,file.rs,open.rs}`, `vortex-session/src/session.rs`.
Parquet Bloom filter: `apache/parquet-format` `BloomFilter.md` (split-block, xxHash64, eight salts).
XXH3: `Cyan4973/xxHash` `doc/xxhash_spec.md`; the .NET implementation is
`System.IO.Hashing.XxHash3` in `dotnet/runtime` (MIT).
Log-structured runs: O'Neil, Cheng, Gawlick, O'Neil, *The log-structured merge-tree*, 1996.
