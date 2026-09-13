# Functional scope

## 1. What Vortex is (and what that implies)

Vortex is not "yet another Parquet". It is a **columnar compression framework** whose file format
is merely a container. Three consequences shape Vorticity:

1. **The file is self-describing.** The split into row groups / pages / columns is not mandated by
   the spec: the *writer* decides, by composing a `Layout` tree that is serialized into the footer.
   A reader must therefore implement a **layout-tree interpreter**, not a fixed-structure parser.
2. **Data stays compressed until the last moment.** A Vortex array is a tree of encodings
   (`dict(runend(for(bitpacked)))`), and operations (filter, take, projection) can execute *on
   compressed data*. "Decompression" is a progressive canonicalization process.
3. **Compatibility is governed by "editions".** An edition is a frozen set of component IDs. The
   current Rust writer defaults to `core2026.08.3`; the read-forever guarantee starts at
   `core2025.05.0` (Vortex 0.36.0). Our *read* scope is therefore defined as a subset of editions,
   not as "version N of the format".

Target format: **`VTXF` version 1**, extension `.vortex`. The format version has not moved since
stabilization; it is the edition registry that evolves.

## 2. Goals for Vorticity 1.0

### In scope

| # | Capability | Detail |
|---|---|---|
| F1 | **Open a Vortex file** | EOF marker → postscript → footer → dtype → layout, in 1–2 I/O round trips (64 KiB tail read) |
| F2 | **Expose the schema** | Full `DType`: Null, Bool, Primitive (11 ptypes incl. f16), Decimal, Utf8, Binary, Struct, List, FixedSizeList, Extension |
| F3 | **Full scan** | `IAsyncEnumerable` of batches, decoded to canonical form |
| F4 | **Projection pushdown** | Read only the segments of requested columns (nested paths included) |
| F5 | **Row-range / random access** | Read rows `[a, b)` or an index list without touching the rest. Specialized `take` paths for `dict`, `runend`, `fastlanes.bitpacked`, `for`, `zigzag`, `alp`, `sequence` and `constant`, pushed down through the layout tree; documented zone-decode fallback for the variable-length encodings (`fsst`, `onpair`, `alprd`) — see [90-registry.md](90-registry.md) |
| F6 | **Zone-map pruning** | Use `vortex.zoned` / `vortex.stats` (min, max, null_count, nan_count) to skip zones |
| F7 | **Filter pushdown** | Simple predicates (comparisons, AND/OR/NOT, IS NULL, IN) evaluated before materialization |
| F8 | **Decode the `core` edition** | The 34 array encodings + 6 layouts of `core2026.08.3` — see [90-registry.md](90-registry.md) for the deliberate exceptions |
| F9 | **Write a file** | Conformant writer targeting an explicit edition, enforced per component kind. Default `core2026.08.3` — the newest frozen edition, and what the reference writer defaults to; `core2025.05.0` is selectable and honoured, at the cost of the zone map. The floor cannot be the *default* because the writer's own zone maps (`vortex.zoned`, `core2026.08.0`) and `vortex.uuid` (`core2026.08.3`) are outside it — see [90-registry.md](90-registry.md) |
| F10 | **Compress on write** | BtrBlocks-style cascade: FoR, ZigZag, BitPacking, Dict, RunEnd, Sparse, Constant, FSST, ALP |
| F11 | **Statistics** | Compute and write file-level statistics and zone maps |
| F12 | **Diagnostics** | Dump the layout/encoding tree (equivalent of `display_tree`) — indispensable for debugging and cross-testing |
| F13 | **Byte-sortable row encoding** | Columns → `ListView<u8>` such that `memcmp` of encoded rows equals tuple comparison. Ships as the separate `Vorticity.RowEncoding` **0.x** package, not in the 1.0 core contract — the upstream format is experimental and reserves the right to change ([09-contracts.md](09-contracts.md) §3). Spec: [06-row-encoding.md](06-row-encoding.md) |
| F14 | **Zstd** | `vortex.zstd` arrays and Zstd-compressed segments, via the in-box `System.IO.Compression.ZstandardDecoder` |

### Scope decision, 2026-09-13: full parity with what Vortex Rust supports

**The encodings below were deferred to 1.1 and are now in scope for 1.0.** The owner's instruction is
"on veut un support complet de ce que la version rust supporte", and that settles a question this
document had answered on a different basis — rarity and effort — rather than on capability.

| now in scope | was deferred because | still true, and now irrelevant |
|---|---|---|
| **`vortex.pco`** | a full integer/float codec, sizeable work; writer opt-in upstream so it never appears in default-written files | the effort is real; rarity is not a reason when parity is the target |
| **`vortex.zstd_buffers`** | draft `zstd2026.02.0` edition, no read-forever guarantee | still a draft edition; decode is trivial now that `vortex.zstd` is in place |
| **`vortex.variant`, `vortex.parquet.variant`, `vortex.map`, Union** | very recent (`core2026.08.2`/`.3`), rarely present | recency is not a reason either |
| **`fastlanes.delta`** | belonged to no core edition, so nothing required it | Rust writes it, so parity requires reading it |

**What this costs, measured rather than estimated.** `CorpusCoverageTests` records 45 corpus files
this build cannot read. 43 of them are exactly these components — `vortex.map` alone is 22 — so this
decision converts almost the whole of that number from *scope* into *work*. That test's header says
"it is not a work queue"; as of this decision, it is one.

**The effort estimates this table carried were unreliable, and here they are re-derived from the
reference implementation rather than from memory.** The parity decision does not depend on them — the
owner's instruction was unconditional — but the ORDER of the work does, and one estimate was simply
wrong.

| component | files | what it actually is | corrected estimate |
|---|---|---|---|
| ~~**`vortex.map`**~~ | 23 | **the estimate was wrong twice over.** The `Map` DTYPE was already implemented — parsing, arena, switch sites — and a map ARRAY is a `ListView<Struct{key,value}>` wearing the map dtype, so no new canonical kind and no new column type were needed | **DONE.** One decoder, one writer wrapper, and the harness work below |
| **`vortex.zstd_buffers`** | 4 | **NOT a value codec.** A META-ENCODING: it compresses each top-level buffer of *another* array independently and stores that array's encoding id and metadata so it can be rebuilt. Decoding means reconstructing an inner array with substituted buffers | **this table said "trivial once `vortex.zstd` is in place"; that is wrong.** Architectural, not trivial |
| **`vortex.variant`, `vortex.parquet.variant`** | 8 | a self-describing binary value format | moderate |
| **`vortex.pco`** | 4 | full pcodec | large |
| ~~**`fastlanes.delta`**~~ | 4 | ~~per-lane prefix sum over 1024-element FastLanes blocks~~ | **DONE.** The estimate held: one decoder, one metadata reader |

**Three of these are pinned by contract §2.8, not merely absent**, with reasons that stay true after
implementation. `EncodingRegistry.DescribeUnsupported` returns them verbatim:

* `fastlanes.delta` — "in no core edition; a default writer cannot emit it";
* `vortex.patched` — "in-memory only upstream; never produced by a conformant writer";
* the `vortex.list` **layout** — "experimental list layout; in no core edition".

Those statements describe *upstream*, and implementing a reader does not falsify any of them. What
changes is whether this library refuses the file. Both remaining single-file refusals sit here:
`containers/experimental_patched_array_editions_off` and `containers/experimental_list_layout`, each
produced only with an upstream environment switch. **The array `vortex.list` is already implemented
and read** — about fifty corpus files carry one; only the same-named layout is missing.

**What is NOT moved by this decision**, because "parity with Rust" is not the axis that excluded
them. Each is listed in the table below with its own reason, and each is a separate call:

* **IPC format** — upstream calls it under construction, with no shared-array support.
* **`tensor`, `spatial`, `json` editions** — plugins outside `core`, a product-surface question.
* **Apache.Arrow interop** — would break the zero-dependency rule, which is an architectural
  invariant of this library rather than a scope preference.
* **CUDA, DataFusion, DuckDB, Spark** — engine integrations, not format support.
* **Encryption**, **forward compatibility / WASM** — nothing to implement; upstream has neither.

If any of those was also meant by "support complet", it needs saying separately: three of the five
are not encodings at all, and one of them costs the zero-dependency guarantee.

### Out of scope (1.0)

| Exclusion | Reason |
|---|---|
| **IPC format** | Explicitly marked unstable and incomplete upstream ("under construction", no shared-array support) |
| **`tensor`, `spatial`, `json` editions** | Optional plugins outside `core` |
| **Encryption** | `EncryptionSpec` is an empty reserved table in the spec; nothing to implement |
| **Forward compatibility / WASM** | Not yet implemented upstream (planned before Vortex 1.0) |
| **CUDA, DataFusion, DuckDB, Spark** | Engine integrations, out of scope for a format library |
| **Apache.Arrow interop** | Would break the zero-dependency rule. Optional separate `Vorticity.Arrow` package |

### Note: segment compression

The footer's `CompressionSpec` allows `None`, `LZ4`, `ZLib`, `ZStd` at the **segment** level, and
`Buffer.compression` allows `None`/`LZ4` at the **buffer** level. Neither is implemented by any
Vortex release: the default Rust writer emits `None`, the reader never inspects either field, and
`footer.fbs` states that the segment spec's pointer into `compression_specs` is "reserved for
future use ... not used in the current version of the file format".

.NET 11 adds `ZstandardStream`, `ZstandardEncoder`, `ZstandardDecoder`, `ZstandardDictionary`
and their options types to `System.IO.Compression`, so Zstd costs us no external dependency — the
span-based one-shot `ZstandardDecoder.TryDecompress` is exactly the shape our buffer decompression
needs, with no stream allocation. `ZLib` and `Deflate` have always been in the BCL.

`LZ4` would have been the only scheme we hand-write, and **we do not**: buffer-level LZ4 is
declared by the schema and implemented by nothing. Vortex 0.86.1 never reads `Buffer.compression`,
never writes anything but `None`, and depends on no lz4 crate, while `footer.fbs` says in as many
words that `SegmentSpec._compression` is "reserved for future use ... not used in the current
version of the file format". With no framing and no decompressed length defined anywhere, a decoder
could only be written by inventing both. **Decision: 1.0 refuses a compressed buffer** with
`VortexUnsupportedException(lz4, compression)` — which, since the reference would read the
compressed bytes as data and return silent garbage, is also the safer behaviour. Reversed the day
upstream implements it. See [08-semantics.md](08-semantics.md) §7.

This removes the argument for deferring `vortex.zstd`. The library targets `net11.0` only, so the
support is unconditional — see [03-architecture.md](03-architecture.md) §1.

## 3. Phasing

Each phase is independently shippable and testable.

### Phase 0 — Foundations (zero dependency)
The entry price of "no dependencies": we write the serialization runtimes ourselves. Both are
small and precisely bounded.

* Aligned buffers, reference counting, zero-copy slices, `NativeMemory.AlignedAlloc`.
* **Minimal FlatBuffers runtime**: reader (vtable resolution, tables, vectors, inline structs,
  unions) plus a builder. No generated code: accessors hand-written against the 5 schemas.
  FlatBuffers reading is pure offset arithmetic — this is what makes zero-dependency realistic.
* **Minimal Protobuf runtime**: varint / zigzag / length-delimited, read and write, for ~30
  metadata messages plus `DType` and `Scalar`.
* `DType` and `Scalar` models with both serializations (the spec uses FlatBuffers *and* Protobuf
  depending on context).
* Error model with no exceptions on hot paths.
* Protobuf unknown-field skipping and the resource caps of
  [08-semantics.md](08-semantics.md) §6 — both are parser-level and must exist before any decoder
  is written.

### Phase 1 — Reading
* File-tail parsing: EOF (8 B) → postscript → footer → dtype → statistics.
* `ISegmentSource`: async range-read abstraction with request coalescing and caching. Two
  implementations: `mmap` (local, zero-copy) and async `RandomAccess` (network / object storage).
* Layout tree: `flat`, `chunked`, `struct`, `zoned`, `stats` (legacy), `dict`.
* Array deserialization: blob → `ArrayNode` tree + buffer table, zero-copy.
* Decoders: canonical first, then compressed (ordering in [90-registry.md](90-registry.md)).
* Scan with projection and row ranges.

### Phase 2 — Compute & pushdown
* SIMD kernels: FastLanes unpacking (1024-element transposed blocks), FoR, ZigZag, ALP, RunEnd, FSST.
* Row masks, `filter`, `take` directly over compressed encodings where it wins.
* Filter expressions and derivation of pruning predicates from zone maps.
* Zstd buffer and segment decompression.

### Phase 3 — Writing
* Canonical uncompressed writer (a valid file at an explicit target edition) — the milestone that
  unlocks cross-testing in the Vorticity → Rust direction.
* Layout strategies: struct split → row blocks (8192 by default) → zone maps → coalesce toward
  ~1 MiB → flat leaves.
* Sampling compressor (BtrBlocks style), with the **candidate scheme list derived from the target
  edition before sampling** and the allowlist at serialization reduced to a bug assertion
  ([90-registry.md](90-registry.md)).
* Write-side kernels in frequency order: FoR / BitPacking / Dict first, then FSST, then ALP.

Three cost sinks that the symmetry with reading hides, and that this phase should not discover
late: **FSST symbol-table construction** is an iterative sampling algorithm and is by far the
largest write kernel (decoding it is trivial by comparison); **ALP encoding** requires exponent
search and patch management, of which decoding exercises only a fraction; and the **FlatBuffers
builder** — back-to-front construction, alignment, and vtable deduplication — is the hidden half of
Phase 0. Vtable dedup is not optional: without it, wide-schema metadata inflates and the 105%
size target starts with a self-inflicted handicap.

All three are built. The measured corpus write ratio is **1.044×**, inside the ≤105% target; the
breakdown and what remains are in [90-registry.md](90-registry.md).

### Phase 3b — Row encoding (parallelizable)
Independent of the file format and of I/O: it touches neither, so it can be built alongside any
other phase and is gated only by the `DType`/array model from Phase 0. See
[06-row-encoding.md](06-row-encoding.md).

* Two-pass, column-major encoder (size pass, write pass), vectorized big-endian conversion and
  descending inversion.
* The full supported type set: Null, Bool, all primitives, Decimal up to i128, Utf8, Binary,
  Struct, FixedSizeList, with recursive nesting and canonicalized null bodies.

### Phase 4 — Hardening
* `vxdump` CLI (inspection, statistics, verification).
* Parser fuzzing: no malformed file may produce anything other than a clean exception — no
  out-of-bounds read, no OOM.
* Zero-allocation analyzers in CI.

## 4. 1.0 acceptance criteria

1. Any file written by Vortex Rust, from 0.36.0 up to **the version the corpus is pinned to**, in
   default configuration, is read correctly value by value (verified differentially, see
   [04-conformance.md](04-conformance.md)). The upper bound is explicit and moves when the
   scheduled corpus regeneration extends it — without it the criterion breaks itself the day
   upstream freezes a new core edition, with no regression on our side.
2. Any file written by Vorticity is read back correctly by Vortex Rust.
3. Zero managed allocations per batch in steady state on a full scan (excluding output buffers),
   measured with `MemoryDiagnoser`.
4. Scan throughput within a stated factor of the Rust reader on the same machine and dataset
   (target: ≤ 2×, see [05-benchmarks.md](05-benchmarks.md)). **Measured: 0.96×** on a full scan of
   `containers/zoned_many_zones_nulls`, in one process against `vortex = 0.86.1` through
   [`tools/vxbench-rs`](../tools/vxbench-rs) — one file on one arm64 machine, not a general claim;
   §1b states what it does and does not support.
5. AOT- and trimming-compatible, with no reflection and no `DynamicallyAccessedMembers`.
6. A file whose *unprojected* columns use unknown encodings still scans successfully; a projected
   one fails with the component ID and kind in the message ([08-semantics.md](08-semantics.md) §4).
7. Pruning never eliminates a row that full materialization would have returned, under randomized
   testing with deliberately inexact statistics ([08-semantics.md](08-semantics.md) §1).

Row encoding has its own criteria, in its own `0.x` package: byte-identical to `vortex-row` for
every supported type, and the order property holding under randomized testing
([04-conformance.md](04-conformance.md) §6).
