# Skipping and locating indexes

A Vorticity specification, written 2026-09-15 and revised the same day with the second
iteration of [11-write-strategy.md](11-write-strategy.md), which owns the pass that builds these
structures (§3.2, §3.3), the scan contract that consults them (§6) and the surface that exposes
them (§7). This document owns what an index **is**: its kinds, its payload, where it lives in a
file, how it survives an append, and how it stays readable by everyone else.

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
same cost upstream's own designs accept.

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

### 4.3 Granularity and generations

`block_len` is the zone length. A skipping index at block granularity has one filter per block; a
**generation** of `k` blocks (`k ≤ 16` by default) has one filter over their union, built from the
retained hash buffers of those blocks when the `k`-th closes and sized from the sum of their
distinct counts; the **file-level** filter is the generation whose `k` is the whole file, when the
policy asks for one. A column may carry several resolutions; the scan evaluates the coarsest
first, and a finer filter is only probed for blocks still live. This is #7939's "multi-resolution
zone maps" without a change to `vortex.zoned`: the zone map stays what upstream reads, the coarse
maps are ours, out of tree.

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

### 5.2 `vorticity.bloom.ngram3.v1` — a token Bloom for `LIKE` and `CONTAINS`

Same structure; the inserted keys are every byte **trigram** of every utf8 value in the block. A
`LIKE` pattern's literal runs (`'%foo-bar%'` → `foo-bar`) yield trigrams that must **all** be
present; a run shorter than three bytes yields no claim. Case-sensitive by construction; an
`ILIKE` variant inserts lower-cased trigrams and is a separate option bit. Sized from the estimated
distinct trigram count (bounded by 2²⁴). This is discussion #8682's trigram idea in its skipping
form. Superset only: the pattern is always re-evaluated on survivors. **Prerequisite**: `LIKE`,
`StartsWith` and `Contains` in `VortexExpr` and `FilterEvaluator` (§6.6).

### 5.3 `vorticity.dict.probe.v1` — the dictionary is already an exact index

When the writer dictionary-encoded a chunk, the chunk's values child *is* the set of distinct
values, small and exact. This kind has **no payload**: its entry says "the chunks of this column
are dictionary-encoded; probe the values child". A reader answers `x = v` by decoding the values
child alone (a few hundred bytes) and never the codes. It is the cheapest equality index there is,
it costs nothing to write, and the writer knows exactly when it applies. When a chunk is *not*
dictionary-encoded the reader gets no claim for that chunk and falls through to the next index.

### 5.4 The file-level filter

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

### 6.3 `vorticity.hash.rows.v1` — hash → rows, superset

`keys` replaced by 64-bit hashes; smaller and cheaper to build than a sort, `Superset` because of
collisions. Kept as an option for very long keys; in practice §5.1 plus §6.1 cover the same
queries at a fraction of the size, and this kind is measured before it is implemented.

### 6.4 `vorticity.postings.ngram3.v1` — trigram → blocks

§6.1 with trigrams as keys: the locating form of §5.2, for `LIKE '%…%'` and `CONTAINS` at block
granularity over text columns. Bigger than the Bloom (one postings list per distinct trigram) and
strictly more selective; the usual choice is one or the other per column. Same prerequisite on
the expression model.

### 6.5 Composite keys through the row encoding

[06-row-encoding.md](06-row-encoding.md) already turns one or more columns into `memcmp`-ordered
byte strings. A locating index over `(a, b)` is §6.2 with the row encoding of `(a, b)` as its key
column: no new comparator, no new key format, and prefix queries on `a` alone are a range over the
encoded keys. `column_path` then lists several paths, in key order. The price is the key size the
row encoding produces, which the spec quantifies per type.

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
  holds.
- No builder adds a pass over the column: the hash comes from the buffer, the keys from the table.
- `WriteAllocationTests` gets one row per index kind, so a builder that allocates per row is caught.

---

## 8. Indexing a file that already exists, and appending to one

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
- Whether to expose probes as a public API (`VortexFile.Probe(column, value)`) for engines that
  plan their own I/O, or keep them inside the scan.
- The default `k` of a generation (16) and whether `Auto` builds one at all without a policy.

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
