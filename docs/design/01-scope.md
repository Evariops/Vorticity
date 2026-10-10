# Scope

What Vorticity covers, what it deliberately leaves out, and the guarantees it holds.

## 1. What Vortex is, and what that implies

Vortex is not another Parquet. It is a columnar compression framework whose file format is a
container, and three of its properties shape this implementation:

1. The file describes itself. How rows are split into chunks, zones and columns is not fixed by the
   format. The writer decides, by composing a layout tree that the footer serializes, so a reader is
   an interpreter of that tree rather than a parser of a fixed structure.
2. Data stays compressed until the last moment. An array is a tree of encodings, such as
   `dict(runend(for(bitpacked)))`, and a filter, a take or a projection can run on the compressed
   form. Decoding is a progressive canonicalization.
3. Compatibility is governed by editions. An edition is a frozen set of component ids. Files written
   by Vortex 0.36.0 and later, edition `core2025.05.0`, are promised to stay readable. A reader's
   scope is therefore a set of editions, not a version of the format, which has stayed at `VTXF`
   version 1.

[02-format.md](02-format.md) condenses the format, and [90-registry.md](90-registry.md) lists every
component and its state.

## 2. What Vorticity is

Vorticity is a reader and a writer of Vortex files for .NET 11, with no third-party dependency. The
FlatBuffers and Protobuf runtimes are written here, and so is the Zstandard codec,
`Vorticity.Zstd`. The only other package the core references is Microsoft's `System.IO.Hashing`
(see [the founding constraints](03-architecture.md#1-founding-constraints)). It is compatible with
Native AOT and trimming.

| package | what it holds |
|---|---|
| `Vorticity` | the session, files, the typed scan and its columns, aggregates, the key cursor, the tool path for schemas known only at run time, the writer and its builders, the I/O interface |
| `Vorticity.Generators` | the `[VortexRecord]` source generator and the analyzers VX1001 to VX1011, build time only |
| `Vorticity.Dataset` | a versioned dataset over an object store ([13-dataset.md](13-dataset.md)), experimental under `VX0001` |
| `Vorticity.RowEncoding` | the byte-sortable row encoding ([06-row-encoding.md](06-row-encoding.md)), experimental under `VX0002` because upstream reserves the right to change it |
| `Vorticity.Zstd` | Zstandard compression and decompression in managed C#, which the core's `vortex.zstd` and `vortex.zstd_buffers` go through ([its README](../../src/Vorticity.Zstd/README.md)) |
| `vxdump` | the inspection tool, written against the public API only |

## 3. What it reads

- Every component a Rust writer emits: 37 array encodings and 7 layouts. That covers every component
  of the core editions up to `core2026.08.3`, `vortex.zstd_buffers` from the draft `zstd2026.02.0`
  edition, and the three components Rust writes outside every edition when an upstream flag asks for
  them: `fastlanes.delta`, `vortex.patched` and the experimental `vortex.list` layout.
- Every file from Rust 0.36.0 up to the version the conformance corpus is pinned to, 0.86.1, value
  for value ([04-conformance.md](04-conformance.md)).
- Lazily. An id this library does not know only fails the read that needs it. An unprojected column
  may use one, and a zone-map aggregate nobody knows only disables its own pruning (see [unknown
  components](08-semantics.md#4-unknown-components-resolve-lazily-fail-on-use)).
- A few things are refused by name, with the id and its kind: a legacy two-buffer `vortex.fsst`, a
  shredded variant child, and a compressed buffer or segment, which the schema declares but no
  Vortex release implements (see [buffer-level LZ4](08-semantics.md#7-buffer-level-lz4)).

## 4. What it writes

- Files every Rust reader of the target edition opens. The default target is
  `VortexEditions.Default`, `core2026.08.3`, read by Vortex Rust 0.85.0 and later. Lower targets are
  honoured by dropping what they cannot carry, and refused where a column needs a component the
  target lacks ([90-registry.md](90-registry.md)). In CI, the reference reads every file this writer
  produces, value for value.
- Encodings chosen per chunk, by exact formulas over statistics computed in one pass, with a trial
  only for zstd, pco, FSST, OnPair, ALP and ALP-RD ([11-write-strategy.md](11-write-strategy.md)).
- Pruning structures: a zone map per block of 8 192 rows, file statistics, bounded string bounds,
  and on request the skipping and locating indexes of [10-indexes.md](10-indexes.md), which a Rust
  reader ignores.
- Appends that continue a file in place, and a repair for a torn one.

## 5. What it answers

A scan reads the columns a record names and nothing else, pushes a filter written as a lambda down
into the file statistics, the zone maps and the indexes, and decodes only the blocks that survive. On
top of that:

| capability | where |
|---|---|
| rows by index, decoding only the rows asked for where the encoding allows it | [90-registry.md](90-registry.md) |
| queries in the shape of LINQ (projections, group by, aggregates) on the encoded form, a dictionary by its codes and a run by its length, flowing as batches as soon as they are final, with sums exact whatever the split | [16-queries.md](16-queries.md) |
| counts, existence, minimum and maximum answered from the structures before any decode | [answers without rows](12-index-reads.md#4-answers-without-rows) |
| a key cursor that seeks, steps, ranks and counts, and batches in key order | [12-index-reads.md](12-index-reads.md) |
| parallel decoding on a session's threads, with batches still in file order | [parallelism](09-contracts.md#2-parallelism) |

## 6. What it leaves out

| | why |
|---|---|
| the IPC format | unstable and incomplete upstream |
| the `tensor`, `spatial` and `json` editions | plugins outside `core` |
| encryption | the format's slot is an empty reserved table |
| engine integrations (DataFusion, DuckDB, Spark, CUDA) | not a format library's job |
| Apache Arrow interoperability | it would break the zero-dependency rule. An owned `RecordBatch` is where a bridge package would attach |
| a DataFrame, SQL, joins | the plan a scan builds is what such a layer would target |
| deleting or updating rows inside a file | a file is immutable and an append only adds blocks. Row deletes and updates live one level up, in the dataset ([deleting and updating rows](13-dataset.md#12-deleting-and-updating-rows)) |
| an S3 client | the dataset's store is an interface another library implements ([the store abstraction](13-dataset.md#11-the-store-abstraction)) |
| preserving unknown nodes byte for byte through a rewrite | a separate feature, with its own invariants |

## 7. Guarantees, and what holds each

| guarantee | held by |
|---|---|
| every corpus file Rust wrote is read value for value | the conformance corpus, 876 files over seven editions ([the golden corpus](04-conformance.md#3-the-golden-corpus)) |
| Rust reads every file this library writes | the cross-check in CI ([the cross-check](04-conformance.md#4-the-cross-check-rust-reads-what-we-write)) |
| a scan allocates nothing per batch in steady state | allocation ratchets in the test suite ([allocations](05-benchmarks.md#5-allocations)) |
| a full scan within 2× of Rust's time, a decoder within 1.5×, files no larger than 105 % of Rust's | the benchmark gates, and `WrittenSizeTests` for the bytes ([the comparison](05-benchmarks.md#1-the-comparison)). Where each stands is on [the benchmark page](../guide/benchmarks.md) |
| pruning never removes a row a full scan returns | every filter test runs with pruning and indexes on and off ([statistic precision](08-semantics.md#1-statistic-precision-what-inexact-licenses)) |
| malformed input fails with a clean exception, never a crash, a hang or an unbounded allocation | parser fuzzing in CI and the resource caps ([resource caps](08-semantics.md#6-resource-caps)) |
| Native AOT and trimming, no reflection | `vxdump` published ahead of time and run over the corpus in CI |
| the row encoding is byte-identical to Rust's `vortex-row`, and byte order is tuple order | golden vectors and a randomized order property ([the row encoding](04-conformance.md#7-the-row-encoding)) |
