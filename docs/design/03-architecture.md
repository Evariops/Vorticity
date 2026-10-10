# .NET architecture

The engine under the public API: the constraints it is built to, where things are, the objects a
scan and a write run on, the I/O interface, and the rules that keep it fast and safe. The public API
itself is described in [14-public-api.md](14-public-api.md), and what each column type becomes in
.NET in [07-dotnet-mapping.md](07-dotnet-mapping.md).

## 1. Founding constraints

| constraint | consequence |
|---|---|
| no third-party dependency | FlatBuffers and Protobuf are read and written by runtimes in this repository, hand-written against the five schemas. There is no `Google.FlatBuffers`, `protobuf-net` or `Apache.Arrow`. Two packages are referenced: `System.IO.Hashing` from `dotnet/runtime`, for XXH3, which the shared framework does not carry, and `Vorticity.Zstd`, this repository's managed Zstandard, rather than the libzstd the runtime ships |
| no allocation on the hot path | no LINQ, no `foreach` over interfaces, no capturing closures, no boxing, no `params` array (only `params ReadOnlySpan<T>`). Aligned native buffers, pools for transient memory, `ref struct` views |
| SIMD with a scalar twin | `System.Runtime.Intrinsics`: `Vector128` everywhere, `Vector256` and `Vector512` where the hardware has them, and a scalar path that the suite runs under `DOTNET_EnableHWIntrinsic=0` and compares bit for bit |
| async only where there is I/O | `ValueTask` and `ConfigureAwait(false)` throughout, with no blocking public call and no synchronous twin of an asynchronous one. A source whose reads complete at once (a mapped file, bytes in memory) pays for a completed `ValueTask`, not for a second code path. Pure CPU work stays synchronous: filling a builder, row encoding, decode kernels, a merge or a sort split into parts the caller awaits. A builder never reads |
| streaming, bounded | every source, operator and sink exchanges batches, pulled by its consumer. The async boundary is the batch, and every loop inside one is synchronous. Read-ahead is a bounded window, an operator holds its open state and never its input, a result is a stream of batches like a file's, and a consumer that stops also stops the reads. What a query costs before its first answer, and what it holds while it runs, is measured ([the flow](16-queries.md#2-the-flow)) |

The library has one target framework, `net11.0`. A single target means no `#if`, no API that differs
by target, no test matrix, and no kernel that could diverge between targets. The price is reach:
.NET 11 is not a long-term-support release, and a consumer pinned to .NET 10 cannot reference the
library. `netstandard2.1` is out of the question, because it lacks `Vector512`, static abstract
members, `INumber<T>`, `SearchValues` and aligned native allocation, which is to say the whole
performance model. `global.json` pins the SDK, so a build never silently changes compiler under a
library whose code generation is measured.

The library enables `AllowUnsafeBlocks` and `IsAotCompatible`. It does not set
`InvariantGlobalization`, an application property with no effect on a library, and avoids
culture-sensitive APIs instead. A module initializer asserts that the process is little-endian and
that the inline FlatBuffers structs have their wire sizes, since the zero-copy design reinterprets
file bytes in place.

Cancellation is accepted wherever it is actionable: an open, a segment read, a scan's enumeration,
every answer a scan computes, every write. It is not threaded through decode kernels, which are
short and CPU-bound. The granularity is the batch.

## 2. Where things are

| `src/Vorticity/` | |
|---|---|
| `Buffers`, `Serialization` | aligned buffers and leases, the FlatBuffers and Protobuf runtimes and the schema views |
| `Types`, `Schema`, `Editions` | the dtype model and its two serializations, the public schema, the edition registry |
| `File`, `IO`, `Layouts` | the open path, the footer and the file's statistics, the segment interface and its sources, the layout readers |
| `Arrays`, `Compute`, `Expressions` | the canonical arena and one decoder per encoding, kernels, masks and pruning, the filter expressions |
| `Scan`, `Columns`, `Symbolic`, `Records` | the scan and its plan, the borrowed and owned batches, the typed filter algebra, record binding |
| `Aggregation`, `Keys`, `Indexes` | aggregates and group by on encoded forms, the key cursor and its sources, the index directory, pruners and policies |
| `Writing`, `Advice` | the writer, its builders and chooser, the index builders, the encoding advisor |
| `Session`, `Diagnostics` | the session, its pool and cache, the exceptions, limits, meter, activity source and event source |

Alongside it sit `src/Vorticity.Generators`, `.Dataset`, `.RowEncoding` and `.Zstd`, then `tests/`
(unit and property tests, the conformance corpus, the fuzzer, and code that must not compile),
`bench/` ([bench/README.md](../../bench/README.md)), `tools/` (`vxdump`, the Rust corpus generator,
the Rust benchmark shim, the row-encoding vectors, the test-impact selector), and `samples/`, the
programs the guide's pages run.

## 3. Object model

None of this is public. The arenas are built once, at open and at `CreateWriter`, and never appear
in the API. [14-public-api.md](14-public-api.md) builds the public API on top of them.

### 3.1 Buffers

A segment is a view over aligned bytes, whatever their origin: a mapped file (zero-copy), pooled
native memory (a positional or remote read), or memory the caller already holds. A `SegmentLease`
owns the bytes and releases them when the batch that borrowed them is done.

### 3.2 Types

A dtype is a `readonly struct` with a tag and a payload, its children in a shared array, rather than
a class hierarchy, so a wide schema does not become thousands of objects. Equality and hashing are
structural and allocate nothing.

### 3.3 Arrays: an arena, not an object graph

A batch of fifty columns, each a tree of three to six encodings rebuilt per chunk, would allocate
hundreds of objects per batch as an object graph. Nodes are therefore structs in a pooled array owned
by the batch (`CanonicalArena`), addressed by index, and `CanonicalNode` is a `ref struct` view of
one. This mirrors the format, where `ArrayNode.children` and `buffers` are already indices. The
blocks a batch decodes into are kept for the scan's next batch, which writes into memory that is
still in the CPU cache without renting it again, and go back to the pool when the scan ends. The
parse of a flat layout's array blob is kept the same way, in the contexts the scans pool, for the
next batch that holds the same segment bytes.

A file's encoding ids are resolved once, at open, against the registry, into an array indexed by
the file's own `u16`, so on the hot path dispatch is an index, not a string lookup. Kernels are
generic over the physical type (`where T : unmanaged, INumberBase<T>`) and specialized per type by
the compiler. There is no `switch` on a type inside a loop and no virtual call per value.

A decoder canonicalizes a node, or decodes only a selection of its rows (`DecodeSelected`), which is
how a take avoids decoding a whole chunk to get one row of it ([90-registry.md](90-registry.md)).

### 3.4 What the public API sees of it

A scan's batch is borrowed. `Columns<TRecord>`, `Column<T>` and `BatchView` are `ref struct`s over
the scan's arenas, valid until the next `MoveNextAsync`, and the compiler, not a `Dispose`, enforces
that lifetime. A batch that must outlive the next one is an owned `RecordBatch`, one copy into pooled
buffers, which the caller disposes.

### 3.5 I/O

The public interface is `Vorticity.IO.ISegmentSource` (`IO/SegmentSeam.cs`): a length, a single
read, and a batch read of many ranges into many leases. The batch read is the real entry point. A
scan registers every segment a split needs before reading, which lets a source coalesce nearby
ranges (with a 1 MiB gap by default) and issue them together, and a scan requests each segment at
most once, every block in it decoding from the one lease. That is what makes or breaks performance
on an object store.

Since object storage lives outside the core, an implementer of the interface must honour four rules:

- All or nothing: a batch read that fails releases every lease it acquired and throws. Retry policy
  belongs to the source.
- Ownership: a lease holds its bytes (one block, or the pieces a response arrived in) until the
  reader disposes it, so a caching source has to reference-count.
- Thread safety: a source is shared by all the scans of a session.
- Cancellation leaves a source's cache consistent: a segment is either present whole or absent.

For sources that do I/O, the session adds one segment cache and one bound on reads in flight
(`MaxConcurrentReads`) shared by every file it opens. A mapped file and bytes in memory bypass both.
Over a source that does I/O, a file also serves every segment lying in the tail its open read, and
those segments are neither requested from the source nor cached. The open reads that tail from a
64-byte boundary, so a segment inside keeps the alignment it declares, and a file shorter than the
tail is read once. A test-only HTTP source with injectable latency
(`tests/Vorticity.Tests/IO/HttpRangeSegmentSource.cs`) proves the interface holds for a remote store.

Three sources ship. `MemoryMappedSegmentSource` is zero-copy. `FileSegmentSource` does positional
reads into aligned buffers. `MemorySegmentSource` serves bytes the caller holds, and uses them in
place when they start on a 64-byte boundary, otherwise copying them once, when the source is made,
into aligned memory it owns. A file opened from a path reads positionally until a scan's plan says it
will read data, and is mapped from then on:

- The open reads the last 8 KiB positionally, or the whole file up to 64 KiB, and a larger footer
  whole in a second read, because mapping a whole file just to read its tail costs more than one
  read. A file the open read whole is never mapped, and its scans are served from that read.
- The file is opened, locked the way `FileShare.Read` locks it, mapped and read with the system calls
  made directly where the platform has them (`NativeFile`). On macOS that is a third fewer calls than
  the framework makes for an open, a scan and a close.
- The first scan whose plan reads any data maps the file, once, and the mapping belongs to the file
  until it is disposed and the last lease on it is released. A held mapping reads two to three times
  faster than a positional read at every size, and a mapped scan allocates nothing per batch, so no
  threshold on volume is worth its complexity.
- Two scans that race to map publish a single mapping by compare-and-exchange, and a read already
  under way finishes positionally.
- The session keeps the mapping after the file is closed (`MappedFileCacheCount`, 64 files by
  default). The next open of the same file, recognized by its device and inode (on Windows by its
  volume and file id), takes it over with every page already mapped, so a file scanned again pays
  neither the mapping nor a page fault per page. A file whose length changed is mapped again, one
  replaced under its name leaves the cache at once, and the mapping holds neither the handle nor its
  lock, so a writer is never kept out. A file deleted after closing keeps its disk space as long as
  its mapping is kept, and `ReleaseMappedFiles` lets every kept mapping go. On Windows a kept file
  can be deleted and replaced under its name, but another writer cannot truncate or overwrite it
  from its start until the session lets it go, and this library's own writers release it themselves.
- A session created with `MapFiles = false` maps nothing. A path is then read by a
  `FileSegmentSource` under the session's read bound and segment cache, like any source that does
  I/O. On Linux and macOS, a mapped file truncated by another process faults on the pages past its
  new end, which kills the process, whereas a positional read of it throws
  `VortexFormatException`.

Coalescing has to respect alignment. A buffer for a coalesced range `[start, end)` places a segment
at file offset `o` at memory offset `o − start`, which is arbitrary for an arbitrary `start`. So the
start is rounded down to 64 and the buffer allocated 64-aligned. Every segment's offset is a
multiple of its own alignment, at most 64 ([resource caps](08-semantics.md#6-resource-caps)), and so
is its distance from the rounded start.

### 3.6 Two phases: materialize, then execute

Reading a split takes two calls: an asynchronous one that reads every segment the split needs,
coalesced, and a synchronous one that decodes. A decoder therefore never awaits. Threading
`ValueTask` through every decoder would put state machines on the hot path to serve a single case,
the lazily loaded values of a dictionary layout, and that case belongs at the layout level, where the
materialization decides what to fetch.

### 3.7 Async enumeration

A compiler-generated `async` enumerator allocates its state machine, and possibly more, per
`MoveNextAsync`. One allocation per scan is acceptable, one per batch is not. The scan's enumerator
is therefore written by hand over `ManualResetValueTaskSourceCore<bool>`, the pattern
`System.Threading.Channels` uses, and so is every enumerator of a query's flow: an operator's stream
of batches, a result's, the scan over a result, a dataset's walk of its objects. A view of rows over
those batches (values, records) steps synchronously within a batch and only awaits at the next one.
That means one `await` per batch, never per row, which is also what allows a batch to be borrowed,
since a span never crosses an `await`.

### 3.8 The write sink

The writer writes to a `PipeWriter`. `CreateWriter(path)` builds one over a file handle,
`PipeWriter.Create(stream)` covers a stream, and an object store provides its own. The writer writes
forward only, in one pass: segments, then the metadata FlatBuffers, the postscript and the end
record, the order the format is designed for. Padding is computed as each segment is written, so no
file is ever materialized whole. Bytes leave the pipe at `FlushAsync` and `CompleteAsync`, so a sink
that applies backpressure slows the producer instead of growing a buffer. A writer that gives up on a
file completes the caller's pipe with an error, so whatever the pipe feeds knows the bytes are not a
file. The postscript's 65 527-byte ceiling is checked when user metadata is given, not at the last
write.

## 4. Performance invariants

Each one is a test, not a guideline:

1. No managed allocation per batch in steady state on a full scan (`ScanAllocationTests`), and a
   fixed ceiling per operation and per written file ([allocations](05-benchmarks.md#5-allocations)).
2. Zero copy: a plain column's decode is a view of the segment's bytes
   (`LeafDecoderTests.PrimitiveReadsValuesZeroCopy`).
3. No per-row dispatch in decode and write loops: every kernel is generic and specialized
   (`PerRowDispatchTests`).
4. Scalar fallback: CI runs again, with hardware intrinsics disabled, the test classes that reach
   every scalar fallback the suite covers (`tests/scalar-pass.txt`), plus the conformance corpus.
5. Native AOT: CI publishes `vxdump` ahead of time and runs it over the corpus.
6. The first batch only waits for the first split. The time to first batch of every streaming query
   (a scan, a projection, a `Distinct`, a group by on a key that streams) does not grow with the file
   or the dataset, locally and over a source with latency ([how queries are
   tested](16-queries.md#13-how-it-is-tested)).
7. Memory is the window plus the open state. A streaming group by's peak stays flat as its groups
   grow, a top-k's follows `k`, and a blocking group by's follows its groups, not the degree times its
   groups (`LiveMemoryTests`).
8. Answers do not depend on how the data is split. Every aggregate whose definition does not depend
   on row order, float sums included, gives the same bits at every degree, in every row order, and
   under every split into chunks, files or objects.

## 5. Error handling

- On the hot path, `Try` forms and return codes, never exceptions for control flow.
- At the boundary, `VortexFormatException` for a malformed file, and `VortexUnsupportedException` for
  a component this build does not implement, which always names the id and its kind (`Array`,
  `Layout`, `DType`, `Aggregate`, `Compression`, `Encryption`, `Index` or `Feature`), since that is
  what upstream's troubleshooting asks for. `VortexSchemaException` when a record or a column does
  not fit the file, and `VortexMemoryException` when a query outgrows its memory or scratch budget.
- Unknown components fail lazily, only in the read that needs them, and there is no option to make
  them fail at open ([unknown
  components](08-semantics.md#4-unknown-components-resolve-lazily-fail-on-use)).
  `VortexFile.ArrayEncodings` and `LayoutEncodings` list what a footer declares, each with whether
  this build supports it.

## 6. Parser safety

A file is untrusted input.

- Every offset and length read from the file is checked against the real size before any access.
- FlatBuffers verification follows the format's actual rules, stated precisely here because the
  obvious phrasing both over- and under-rejects:
  - uoffsets (table and vector references) are unsigned and point forward: `uoffset > 0` and the
    target in bounds. Cycles are impossible by construction, so no visited set is kept.
  - Forward-only offsets exclude cycles but not sharing. Two slots may resolve to the same target,
    so the decoded graph is a DAG, and a reader that recurses once per edge is exponential in depth.
    A few hundred bytes of shared children can pass every bounds and depth rule while describing 2⁶³
    visits. Total work therefore has its own bound, separate from depth: every table visited is
    charged against `VortexLimits.MaxFlatBufferTables`, like the reference verifiers' `max_tables`.
  - soffsets (vtable references) are signed. A vtable may precede or follow its table, and both
    directions are bounded.
  - Vtable sharing between tables is legal and routine, so a verifier that refused to revisit a
    position would reject valid files.
  - Recursion depth is capped for layout trees, array trees and dtypes. A 10 000-deep nested struct
    would otherwise blow the stack while the schema is parsed, before any data is read.
- No allocation is sized by a file-supplied value without a cap, and the caps live in one table
  ([resource caps](08-semantics.md#6-resource-caps)), not in a choice per call site.
  `alignment_exponent` is a `u8`, so an unchecked file could demand `2²⁵⁵`.
- Semantic hints are classified ([untrusted
  hints](08-semantics.md#5-untrusted-hints-a-three-class-policy)). What memory safety depends on is
  validated unconditionally, and what only correctness depends on is covered by the threat model or
  by the opt-in `VortexOpenOptions.VerifyStatistics`.
- Children's row counts are checked against their parents, never assumed.
