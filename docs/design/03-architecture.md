# .NET architecture

The engine under the public surface: the constraints it is built to, where things are, the objects a
scan and a write run on, the I/O seam, and the rules that keep it fast and safe. The surface itself
is [14-public-api.md](14-public-api.md); what each column type becomes in .NET is
[07-dotnet-mapping.md](07-dotnet-mapping.md).

## 1. Founding constraints

| constraint | consequence |
|---|---|
| **No third-party dependency** | FlatBuffers and Protobuf are read and written by runtimes of this repository, hand-written against the five schemas; no `Google.FlatBuffers`, `protobuf-net` or `Apache.Arrow`. Two packages are referenced: `System.IO.Hashing` from `dotnet/runtime`, for XXH3, which the shared framework does not carry, and `Vorticity.Zstd`, this repository's managed Zstandard, rather than the libzstd the runtime ships |
| **No allocation on the hot path** | no LINQ, no `foreach` over interfaces, no capturing closures, no boxing, no `params` array (only `params ReadOnlySpan<T>`); aligned native buffers, pools for transients, `ref struct` views |
| **SIMD with a scalar twin** | `System.Runtime.Intrinsics`: `Vector128` everywhere, `Vector256` and `Vector512` where the hardware has them, and a scalar path that the suite runs under `DOTNET_EnableHWIntrinsic=0` and compares bit for bit |
| **Async only, where there is I/O** | `ValueTask` and `ConfigureAwait(false)` throughout; no blocking public call and no synchronous twin of an asynchronous one. A source whose reads complete at once, a mapped file or bytes in memory, pays for a completed `ValueTask`, not for a second path. Pure CPU work stays synchronous: filling a builder, row encoding, decode kernels, a merge or a sort cut into parts the caller awaits. A builder never reads |
| **Streaming, bounded** | every source, operator and sink exchanges batches, pulled by its consumer: the async boundary is the batch, and every loop inside one is synchronous. Read-ahead is a bounded window, an operator holds its open state and never its input, a result is a stream of batches like a file's, and a consumer that stops stops the reads. What a query costs before its first answer, and holds while it runs, is measured ([16-queries.md](16-queries.md) §2) |

**One target framework, `net11.0`.** A single target means no `#if`, no API surface that differs by
target, no test matrix, and no kernel that could diverge between legs. The price is reach:
.NET 11 is not a long-term-support release, and a consumer pinned to .NET 10 cannot reference the
library. `netstandard2.1` is out of the question: it lacks `Vector512`, static abstract members,
`INumber<T>`, `SearchValues` and aligned native allocation, which is to say the performance model.
`global.json` pins the SDK, so that a build never changes compiler silently under a library whose
code generation is measured.

The library enables `AllowUnsafeBlocks` and `IsAotCompatible`. It does not set
`InvariantGlobalization`, an application property with no effect on a library; it avoids
culture-sensitive APIs instead. A module initializer asserts that the process is little-endian and
that the inline FlatBuffers structs have their wire sizes, since the zero-copy design reinterprets
file bytes in place.

**Cancellation** is taken where it is actionable: an open, a segment read, a scan's enumeration,
every answer a scan computes, every write. It is not threaded through decode kernels, which are
short and CPU-bound: the granularity is the batch.

## 2. Where things are

| `src/Vorticity/` | |
|---|---|
| `Buffers`, `Serialization` | aligned buffers and leases; the FlatBuffers and Protobuf runtimes and the schema views |
| `Types`, `Schema`, `Editions` | the dtype model and its two serializations; the public schema; the edition registry |
| `File`, `IO`, `Layouts` | the open path, the footer and the file's statistics; the segment seam and its sources; the layout readers |
| `Arrays`, `Compute`, `Expressions` | the canonical arena and one decoder per encoding; kernels, masks, pruning; the filter expressions |
| `Scan`, `Columns`, `Symbolic`, `Records` | the scan and its plan; the borrowed and owned batches; the typed filter algebra; record binding |
| `Aggregation`, `Keys`, `Indexes` | aggregates and group by on encoded forms; the key cursor and its sources; index directory, pruners, policies |
| `Writing`, `Advice` | the writer, its builders and chooser, the index builders; the encoding advisor |
| `Session`, `Diagnostics` | the session, its pool and cache; exceptions, limits, the meter and the event source |

Beside it: `src/Vorticity.Generators`, `.Dataset` and `.RowEncoding`; `tests/` (unit and property
tests, the conformance corpus, the fuzzer, and code that must not compile); `bench/`
([bench/README.md](../../bench/README.md)); `tools/` (`vxdump`, the Rust corpus generator, the
Rust benchmark shim, the row-encoding vectors, the test-impact selector); `samples/`, the programs
the guide's pages run.

## 3. Object model

None of this is public. The arenas are built once at open and at `CreateWriter` and never appear;
[14-public-api.md](14-public-api.md) builds the surface over them.

### 3.1 Buffers

A segment is a view over aligned bytes, whatever their origin: a mapped file (zero-copy), pooled
native memory (a positional or remote read), or managed memory the caller already holds. A
`SegmentLease` owns the bytes and releases them when the batch that borrowed them is done.

### 3.2 Types

A dtype is a `readonly struct` with a tag and a payload, its children in a shared array, rather than
a class hierarchy: a wide schema must not become thousands of objects. Equality and hashing are
structural and allocate nothing.

### 3.3 Arrays: an arena, not an object graph

A batch of fifty columns, each a tree of three to six encodings rebuilt per chunk, would allocate
hundreds of objects per batch as an object graph. So nodes are structs in a pooled array owned by
the batch (`CanonicalArena`), addressed by index; `CanonicalNode` is a `ref struct` view of one.
This mirrors the format, where `ArrayNode.children` and `buffers` are indices already. The blocks a
batch decodes into are kept for the next batch of the scan, which writes into memory still in the
cache without renting it again, and go back to the pool when the scan ends. The parse of a flat
layout's array blob is kept the same way, in the contexts the scans pool, for the next batch that
holds the same segment bytes.

A file's encoding ids are resolved **once, at open**, against the registry, into an array indexed
by the file's own `u16`: on the hot path, dispatch is an index, not a string lookup. Kernels are
generic over the physical type (`where T : unmanaged, INumberBase<T>`) and specialized per type by
the compiler: no `switch` on a type inside a loop, and no virtual call per value.

A decoder canonicalizes a node, and may instead decode only a selection of its rows
(`DecodeSelected`), which is how a take avoids decoding a chunk it needs one row of
([90-registry.md](90-registry.md)).

### 3.4 What the surface sees of it

A scan's batch is borrowed: `Columns<TRecord>`, `Column<T>` and `BatchView` are `ref struct`s over
the scan's arenas, valid until the next `MoveNextAsync`, and the compiler, not a `Dispose`, holds
that lifetime. A batch that must outlive its successor is an owned `RecordBatch`, one copy into
pooled buffers, which the caller disposes.

### 3.5 I/O

The public seam is `Vorticity.IO.ISegmentSource` (`IO/SegmentSeam.cs`): a length, a single read,
and a batch read of many ranges into many leases. The batch read is the real entry point: a scan
registers every segment a split needs before reading, which lets a source **coalesce** nearby
ranges (1 MiB gap by default) and issue them together, and a scan requests each segment at most
once, every block of it decoding from the one lease. That is what makes or breaks performance on
an object store.

What an implementer of the seam must honour, since object storage lives outside the core:

- **All or nothing**: a batch read that fails releases every lease it acquired and throws; retry
  policy is the source's.
- **Ownership**: a lease holds its bytes, one block or the pieces a response arrived in, until the
  reader disposes it; a caching source therefore reference-counts.
- **Thread safety**: a source is shared by the scans of a session.
- **Cancellation** leaves a source's cache consistent: a segment is present whole or absent.

The session adds one segment cache and one bound on reads in flight (`MaxConcurrentReads`) for every
file it opens, for sources that do I/O; a mapped file and bytes in memory bypass both. Over a source
that does I/O, a file also serves every segment lying in the tail its open read, which is then
neither asked of the source nor cached: the open reads that tail from a 64-byte boundary, so that a
segment inside keeps the alignment it declares, and a file shorter than the tail is read once. A test-only
HTTP source with injectable latency (`tests/Vorticity.Tests/IO/HttpRangeSegmentSource.cs`) is
the proof that the seam holds for a remote store.

Three sources ship: `MemoryMappedSegmentSource` (zero-copy), `FileSegmentSource` (positional reads
into aligned buffers) and `MemorySegmentSource` (bytes the caller holds, never copied). A file
opened **from a path** reads positionally until a scan's plan says it will read data, and is mapped
from then on:

- the open reads the last 8 KiB positionally, or the whole file up to 64 KiB, and a larger footer
  whole in a second read: mapping a whole file to read its tail costs more than one read. A file
  the open read whole is never mapped: its scans are served from that read;
- the file is opened, locked as `FileShare.Read` locks it, mapped and read with the system's calls
  made directly where the platform has them (`NativeFile`): on macOS a third fewer calls than the
  framework makes for an open, a scan and a close;
- the first scan whose plan reads any data maps the file, once, and the mapping is the file's until
  it is disposed and the last lease on it released. A held mapping reads two to three times faster
  than a positional read at every size, and a mapped scan allocates nothing per batch, so no
  threshold on the volume is worth its complexity;
- two scans that race to map publish one mapping by compare-and-exchange; a read already under way
  finishes positionally;
- the session keeps the mapping once the file is closed (`MappedFileCacheCount`, 64 files by
  default): the next open of the same file, known by its device and inode, takes it over with every
  page already mapped in it, so a file scanned again pays neither the mapping nor a page fault per
  page. A file whose length changed is mapped again, one replaced under its name leaves the cache at
  once, and the mapping holds neither the handle nor its lock, so a writer is never kept out. A
  file deleted once closed keeps its disk space while it is kept, and `ReleaseMappedFiles` lets
  every kept mapping go. On Windows, where a mapped file can be neither deleted nor replaced,
  nothing is kept;
- a session created with `MapFiles = false` maps nothing: a path is read by a `FileSegmentSource`
  under the session's bound on reads and segment cache, as any source that does I/O. On Linux and
  macOS a mapped file cut short by another process faults on the pages past its new end, which
  kills the process; a positional read of it throws `VortexFormatException` instead.

**Coalescing against alignment.** A buffer for a coalesced range `[start, end)` places a segment at
file offset `o` at memory offset `o − start`, arbitrary for an arbitrary `start`. So the start is
rounded down to 64 and the buffer allocated 64-aligned: every segment's offset is a multiple of its
own alignment, at most 64 ([08-semantics.md](08-semantics.md) §6), and so is its distance from the
rounded start.

### 3.6 Two phases: materialize, then execute

Reading a split is two calls: an asynchronous one that reads every segment the split needs,
coalesced, and a synchronous one that decodes. A decoder therefore never awaits: threading
`ValueTask` through every decoder would put state machines on the hot path to serve one case, the
lazily loaded values of a dictionary layout, which belongs to the layout level, where the
materialization decides what to fetch.

### 3.7 Async enumeration

A compiler-generated `async` enumerator allocates its state machine and possibly more per
`MoveNextAsync`. One allocation per scan is acceptable; one per batch is not. The scan's enumerator
is therefore written by hand over `ManualResetValueTaskSourceCore<bool>`, the pattern
`System.Threading.Channels` uses, and so is every enumerator of a query's flow: an operator's
stream of batches, a result's, the scan over a result, a dataset's walk of its objects. A view of
rows over those batches — values, records — steps synchronously within a batch and awaits only at
the next one: an `await` per batch, never per row, which is also what lets a batch be borrowed, a
span never crossing an `await`.

### 3.8 The write sink

The writer's seam is a `PipeWriter`: `CreateWriter(path)` builds one over a file handle,
`PipeWriter.Create(stream)` covers a stream, and an object store provides its own. The writer writes
**forward only, in one pass**: segments, then the metadata FlatBuffers, the postscript and the end
record, the order the format is designed for. Padding is computed as each segment is written, so
no file is ever materialized whole; bytes leave the pipe at `FlushAsync` and `CompleteAsync`, so a
sink that applies backpressure slows the producer instead of growing a buffer. A writer that gives
a file up completes the caller's pipe with an error, so that whatever it feeds knows the bytes are
not a file. The postscript's 65 527-byte ceiling is checked when user metadata is given, not at the
last write.

## 4. Performance invariants

Each is a test, not a guideline:

1. **No managed allocation per batch** in steady state on a full scan (`ScanAllocationTests`), and
   a fixed ceiling per operation and per written file ([05-benchmarks.md](05-benchmarks.md) §5).
2. **Zero copy**: a plain column's decode is a view of the segment's bytes
   (`LeafDecoderTests.PrimitiveReadsValuesZeroCopy`).
3. **No per-row dispatch** in decode and write loops: every kernel is generic and specialized
   (`PerRowDispatchTests`).
4. **Scalar fallback**: CI runs again with hardware intrinsics disabled the test classes that reach
   every scalar fallback the suite does (`tests/scalar-pass.txt`), and the conformance corpus.
5. **Native AOT**: CI publishes `vxdump` ahead of time and runs it over the corpus.
6. **The first batch waits for the first split**: the time to first batch of every streaming query
   — a scan, a projection, a `Distinct`, a group by on a key that streams — does not grow with the
   file or the dataset, locally and over a source with latency
   ([16-queries.md](16-queries.md) §13).
7. **Memory is the window and the open state**: a streaming group by's peak stays flat as its
   groups grow, a top-k's follows `k`, a blocking group by's follows its groups and not the degree
   times them (`LiveMemoryTests`).
8. **Answers do not depend on the cut**: every aggregate that does not depend on the order of rows
   by definition, float sums included, is the same bits at every degree, in every order of rows,
   and under every cut into chunks, files or objects.

## 5. Error handling

- On the hot path, `Try` forms and return codes; no exception for control flow.
- At the boundary: `VortexFormatException` for a malformed file; `VortexUnsupportedException` for
  a component this build does not implement, which always names the **id and its kind** (`Array`,
  `Layout`, `DType`, `Aggregate`, `Compression`, `Encryption`, `Index` or `Feature`), since that is
  what upstream's troubleshooting asks for; `VortexSchemaException` when a record or a column does
  not fit the file.
- Unknown components fail lazily, only the read that needs them, and there is no option to make
  them fail at open ([08-semantics.md](08-semantics.md) §4). `VortexFile.ArrayEncodings` and
  `LayoutEncodings` list what a footer declares, each with whether this build supports it.

## 6. Parser safety

A file is untrusted input.

- Every offset and length read from the file is checked against the real size before any access.
- FlatBuffers verification follows the format's actual rules, stated precisely because the obvious
  phrasing both over- and under-rejects:
  - **uoffsets** (table and vector references) are unsigned and point forward: `uoffset > 0` and the
    target in bounds. Cycles are therefore impossible by construction, and no visited set is kept.
  - Forward-only offsets exclude cycles but **not sharing**: two slots may resolve to one target,
    so the decoded graph is a DAG, and a reader that recurses once per edge is exponential in depth.
    A few hundred bytes of shared children pass every bounds and depth rule while describing 2⁶³
    visits. Total work therefore has its own bound, separate from depth: every table visited is
    charged against `VortexLimits.MaxFlatBufferTables`, as the reference verifiers' `max_tables`.
  - **soffsets** (vtable references) are signed: a vtable may precede or follow its table, and both
    directions are bounded.
  - **vtable sharing** between tables is legal and routine; a verifier that refused to revisit a
    position would reject valid files.
  - Recursion depth is capped for layout trees, array trees and dtypes: a 10 000-deep nested struct
    blows the stack while the schema is parsed, before any data is read.
- No allocation is sized by a file-supplied value without a cap; the caps are one table,
  [08-semantics.md](08-semantics.md) §6, not a choice per call site. `alignment_exponent` is a `u8`,
  so an unchecked file could demand `2²⁵⁵`.
- Semantic hints are classified ([08-semantics.md](08-semantics.md) §5): what memory safety depends
  on is validated unconditionally, what only correctness depends on is covered by the threat model
  or by the opt-in `VortexOpenOptions.VerifyStatistics`.
- Children's row counts are checked against their parents, never assumed.
