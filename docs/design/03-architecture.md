# .NET architecture

## 1. Founding constraints

| Constraint | Consequence |
|---|---|
| **Zero third-party dependency** | FlatBuffers and Protobuf hand-written. No `Google.FlatBuffers`, no `protobuf-net`, no `Apache.Arrow`. BCL, plus **one first-party package**: `System.IO.Hashing` (`dotnet/runtime`, MIT, not carried by the shared framework), for the XxHash3-64 of the write path and the Bloom filters — decided 2026-09-15, [10-indexes.md](10-indexes.md) §11. Nothing else. |
| **Zero allocation** | No LINQ, no `foreach` over interfaces, no capturing closures, no boxing, no `params` array: `params ReadOnlySpan<T>` only, which allocates nothing. Aligned native buffers, `ArrayPool` for transients, `ref struct` readers. |
| **SIMD** | `System.Runtime.Intrinsics` with `Vector512`/`Vector256`/`Vector128` paths and a scalar fallback. `Vector<T>` only where width is irrelevant. |
| **Async only** | `ValueTask<T>` throughout, `ConfigureAwait(false)` systematically. No blocking public API, and no synchronous twin of an asynchronous one: a scan is consumed with `await foreach` over every source, a writer is fed with `WriteAsync`, and the I/O seam reads only asynchronously. A source whose reads complete at once, a mapped file or bytes in memory, pays for a completed `ValueTask`, not for a second path. Pure in-memory CPU work with nothing to await stays synchronous rather than wrapping itself in a fake task: filling a builder, row encoding, decode kernels, and `CreateWriter`, which only opens a handle. |

**TFM: `net11.0`.** Single target. The SDK (11.0.100-rc.1) is installed locally, so this builds
and tests today.

A single TFM is a deliberate simplification: no `#if`, no conditional API surface, no test matrix.
It also makes Zstd unconditional — verified against the installed reference assemblies, `net11.0`
ships `ZstandardStream`, `ZstandardEncoder`, `ZstandardDecoder`, `ZstandardDictionary`,
`ZstandardCompressionOptions` and `ZstandardDecompressionOptions` in `System.IO.Compression`,
where `net10.0` had no Zstandard type at all. The span-based one-shot
`ZstandardDecoder.TryDecompress` is exactly the shape buffer decompression wants: no stream, no
allocation.

The accepted cost, stated plainly: .NET 11 is STS, not LTS, so consumers pinned to .NET 10 LTS
cannot reference the library until they move, and a stable 1.0 cannot ship before .NET 11 GA.
This is a deliberate trade of reach for simplicity, taken with the knowledge that Zstd alone would
not justify it — `vortex.zstd` appears only in opt-in files ([02-format.md](02-format.md) §7). What
justifies it is the single-TFM simplification itself: no conditional compilation, no API surface
that differs by target, no test matrix, and no risk of a kernel silently diverging between legs.

If LTS demand materializes, a `net10.0` leg is a contained addition — one `IBufferDecompressor`
implementation and one registry entry behind `#if`, with Zstd degrading to the
`VortexUnsupportedException` path that already exists for every out-of-scope component
([09-contracts.md](09-contracts.md) §3 treats adding or dropping a TFM as a major version).

`netstandard2.1` is rejected outright: it removes `Vector512`, static abstract members,
`INumber<T>`, `SearchValues` and `NativeMemory.AlignedAlloc` — that is, everything that makes the
performance story here.
`<AllowUnsafeBlocks>` and `<IsAotCompatible>` enabled. `<InvariantGlobalization>` is **not** set
here: it is an application property with no effect on a class library, so it belongs to `vxdump`
and the benchmark host. The core instead avoids culture-sensitive APIs by construction.

### Cancellation

`CancellationToken` is taken where cancellation is meaningful and actually actionable: file open,
segment I/O, scan enumeration (the scan's own `GetAsyncEnumerator(ct)`, and the token of
`ToBatchesAsync` and `ToRecordsAsync`, which `[EnumeratorCancellation]` joins to
`WithCancellation`), every answer a scan computes, and every write. It is **not** threaded through
pure decode/compute kernels — those are short, CPU-bound and allocation-free, and a token check per
kernel would cost more than it buys. Cancellation granularity is the batch, not the instruction.

## 2. Layout

```
Vorticity.sln
src/
  Vorticity/                    # net11.0, no third-party dependency (System.IO.Hashing only)
    Buffers/                      # VortexBuffer, alignment, ref-counting, pooling
    Serialization/
      FlatBuffers/                # reader + builder runtime, hand-written accessors
      Protobuf/                   # varint, zigzag, message readers/writers, unknown-field skip
    Types/                        # DType, PType, Nullability, Scalar, ScalarValue
    Arrays/                       # ArrayRef + one decoder per encoding
    Layouts/                      # Flat, Chunked, Struct, Zoned, Stats, Dict
    File/                         # Postscript, Footer, open path, VortexFile
    IO/                           # ISegmentSource and the file, mapped and memory sources
    Compute/                      # SIMD kernels, masks, filter, take, canonicalization
    Expressions/                  # filter expressions + pruning derivation
    Writing/                      # writer, builders, layout strategies, compressor
    Session/                      # VortexSession: pool, segment cache, read bound, parallelism
    Schema/                       # VortexSchema, VortexField, VortexType
    Records/                      # IVortexRecord binding, the .NET mapping of 07
    Symbolic/                     # Probe, Sym<T>, Predicate: the typed filter algebra
    Scan/                         # Scan<TRecord>, the tool Scan, plans, lanes
    Columns/                      # Columns<TRecord>, Column<T>, BatchView, RecordBatch
    Aggregation/                  # aggregates and group by on the encoded form
    Keys/                         # the key cursor and its sources
    Indexes/                      # index directory, builders, pruners
    Editions/                     # the edition registry
    Diagnostics/                  # exceptions, limits, the meter and activity source
  Vorticity.RowEncoding/        # SEPARATE 0.x package: byte-sortable row encoder, no I/O,
                                  # synchronous. Separate because the upstream format is
                                  # experimental (09-contracts.md §3), not because it is optional.
  Vorticity.Dataset/            # SEPARATE experimental package: many objects on a store, one table
  Vorticity.Generators/         # the [VortexRecord] generator and the VX analyzers
tests/
  Vorticity.Tests/              # xunit v3 — unit + property tests
  Vorticity.Conformance/        # xunit v3 — Rust cross-tests (golden + differential)
  Vorticity.Fuzz/               # parser fuzzing
bench/
  Vorticity.Benchmarks/         # BenchmarkDotNet
tools/
  vxdump/                         # inspection CLI
  conformance-gen/                # Rust crate: corpus generation + differential harness
```

## 3. Object model

This section is the engine's object model: the buffers, types, arenas and nodes a scan and a write
run on. None of it is public. Every type a caller can name, the shape of each call and what it
costs belong to [14-public-api.md](14-public-api.md), which supersedes the public API this section
used to sketch and builds its surface over this model without exposing it: the arenas are built
once at open and at `CreateWriter` and never appear.

### 3.1 Buffers

```csharp
internal readonly struct VortexBuffer   // non-owning view over an aligned segment
{
    public ReadOnlySpan<byte> Span { get; }
    public int Alignment { get; }      // 1 << alignment_exponent
    public VortexBuffer Slice(int offset, int length);
    public ReadOnlySpan<T> Cast<T>() where T : unmanaged;   // checks alignment
}
```

Three possible origins unified behind one view: memory-mapped (zero-copy, local case), aligned
native memory (network reads), pinned managed array (tests). Ownership is held by a
reference-counted `SegmentOwner`, released when the batch goes out of scope.

### 3.2 Types

`DType` as a `readonly struct` with a tag and a payload (children in a shared array) rather than a
class hierarchy: a wide schema must not produce thousands of objects. Structural equality and
hashing, allocation-free.

### 3.3 Arrays

```csharp
internal abstract class VortexArray        // tree node: light, immutable, shared
{
    public DType DType { get; }
    public int Length { get; }
    public ArrayStats Stats { get; }
}
```

An array is an encoding-tree node, not a value buffer.

**The tree is an arena, not an object graph.** A batch of 50 projected columns, each a 3–6 node
encoding tree (`dict(runend(for(bitpacked)))` is the format's own example), rebuilt per chunk,
would allocate hundreds of objects per batch and make performance invariant #1 unachievable before
a line of code is written. So arrays get exactly the treatment the DType model already gets in
§3.2: nodes are structs in a pooled array owned by the batch, addressed by index, returned to the
pool on `Dispose`. This mirrors the format itself, where `ArrayNode.children` and `buffers` are
already indices rather than pointers.

```csharp
internal readonly ref struct ArrayNode        // a view into the batch's node arena
{
    public DType DType { get; }
    public int Length { get; }
    public ArrayNode Child(int i);            // index into the same arena
}
```

Value access goes through **canonicalization**, which is synchronous (see §3.6): the segments a
batch needs are materialized before decoding starts, so the decode traversal has nothing to await.

Encoding dispatch: the file's `uint16` is resolved **once at open time** against the registry
(`FrozenDictionary<string, IEncoding>` → indexed array). On the hot path, dispatch is an array
index, not a string lookup.

PType dispatch: constrained generics (`where T : unmanaged, INumberBase<T>`) with per-type JIT
specialization — no enum `switch` inside inner loops.

### 3.4 Public API

The surface is [14-public-api.md](14-public-api.md)'s, and this is its shape over the model above:

```csharp
[VortexRecord] public partial record struct Reading(int Day, double? Celsius, string City);

await using VortexFile file = await VortexFile.OpenAsync(path, ct);      // or session.OpenAsync(ISegmentSource)
VortexSchema schema = file.Schema;
long rows = file.RowCount;

Scan<Reading> scan = file.Scan<Reading>()                                // the record is the projection
    .Where(r => r.Day >= 900 && r.City == "Paris")                      // a lambda over symbols, run once: the plan
    .Rows(RowRange.FromLength(0, 1_000_000))                             // long-based: the format counts rows in u64
    .With(new ScanOptions { BatchRows = 8_192 });

await foreach (var (day, celsius, city) in scan)
{
    ReadOnlySpan<int> days = day.Values;
    // ...
}
```

The batch of an `await foreach` is borrowed: `Columns<TRecord>` and `Column<T>` are `ref struct`s
over the scan's arenas, valid until the next `MoveNextAsync`, and the compiler rather than a
`Dispose` holds that lifetime, which is the price of zero-copy. A batch that must outlive its
successor is asked for by `ToBatchesAsync()` or `ToOwned()`, which copy it once into pooled buffers
of a `RecordBatch` the caller disposes. See [07-dotnet-mapping.md](07-dotnet-mapping.md) for what
each DType looks like at this boundary, including variable-width access, which is where the
lifetime rule bites hardest.

Three points the API must pin down rather than leave to discovery:

* `System.Range` is `int`-based and cannot address a file of 3 billion rows. Row ranges use a
  `RowRange(long start, long end)` of our own.
* **Order of application is `Where` then projection.** On the tool path the filter may name columns
  `Scan(columns)` does not; they are read for filtering and discarded before the batch is produced.
  On the typed path the filter speaks of the record's members, and the record is the projection.
* Batch size derives from the file's zones (8192 by default) but is capped by
  `ScanOptions.BatchRows` so a memory-constrained consumer is not at the writer's mercy. Exposing it
  costs a parameter now and a breaking change later.

### 3.5 I/O

The public seam, in `Vorticity.IO` ([14-public-api.md](14-public-api.md) §8):

```csharp
public interface ISegmentSource : IAsyncDisposable
{
    long Length { get; }
    ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken ct);
    ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken ct);
}
```

The batch overload is the real entry point: the reader registers every segment of a split before
reading, which allows **coalescing** nearby ranges (tunable threshold, ~1 MiB) and parallelizing.
This makes or breaks performance on object storage. A scan requests each segment at most once, and
every block of a segment decodes from its one lease. There is no synchronous read: a source whose
reads complete at once returns a completed `ValueTask`.

Three built-in implementations, **all local** — `RandomAccess` takes a `SafeFileHandle`, so it is
not an object-storage path:

* `MemoryMappedSegmentSource` — zero-copy, alignment guaranteed by the writer's padding; what a path
  opens with.
* `FileSegmentSource` — `RandomAccess.ReadAsync` with vectored reads into aligned native buffers.
* `MemorySegmentSource` — bytes the caller already holds, never copied.

Object storage is deliberately *not* in the core: an HTTP source would break zero-dependency and
belongs to the layer above. That makes `ISegmentSource` a seam an external implementer must be
able to satisfy without asking us questions, so its contract is specified, not implied:

* **Partial failure**: the batch `ReadAsync` is all-or-nothing. If any range fails, it throws and
  releases every lease it had already acquired, so none is left to dispose. Retry policy belongs to
  the source, not the reader.
* **Ownership**: a `SegmentLease` holds its bytes, one block or a `ReadOnlySequence<byte>` of the
  pieces a response arrived in, until the reader disposes it, which releases the owner the source
  named. A caching source therefore refcounts; the reader never frees what it did not allocate.
* **Cache**: the session's `SegmentCache` keeps the segments of a source that does I/O across
  scans, one budget for every file of the session; a mapped file or bytes in memory have nothing to
  cache and bypass it. A source may cache too. Eviction policy and memory budget are each cache's
  business; the reader assumes nothing about hit rates and never requires a segment to still be
  cached.
* **Concurrency**: the reads in flight to sources that do I/O are bounded per session by
  `MaxConcurrentReads`, a `SemaphoreSlim`; a source is shared by the scans of a session and must be
  thread-safe.
* **Cancellation**: a cancelled read leaves a source's cache in a consistent state — either a
  segment is fully present or absent, never partially populated.

A reference `HttpRangeSegmentSource` with injectable latency ships **in the test project**, not in
the core. It costs nothing in dependencies and it is the executable proof that the seam holds —
plus the harness for the open-latency metric, which is the object-storage number that matters.

#### Coalescing versus alignment

These two requirements collide unless the rule is fixed. A native buffer allocated for a coalesced
range `[start, end)` places the segment at file offset `o` at memory offset `o - start`, whose
alignment is arbitrary for an arbitrary `start`. Two lines fix it:

```
alignedStart = start & ~63L                 // round the coalesced start down to 64
buffer       = AlignedAlloc(length, 64)     // 64-byte aligned base
```

Proof: the writer guarantees each segment's file offset is aligned to its own exponent (≤ 6 after
the cap in [08-semantics.md](08-semantics.md) §6). If `alignedStart` is a multiple of 64 and `o` is
a multiple of `2^k` with `k ≤ 6`, then `o - alignedStart` is a multiple of `2^k`. Alignment
survives coalescing.

On the local path there is a better trick: vectored `RandomAccess.ReadAsync` can scatter a
coalesced range directly into per-segment aligned buffers (plus throwaway buffers for the gaps),
so no split-copy happens at all. It does not transpose to HTTP, where the response is a stream and
segmentation happens during the copy from the socket — one unavoidable copy, accepted.

### 3.6 Two phases: materialize, then execute

`MaterializeAsync` and `Execute` are deliberately separate:

```csharp
ValueTask<BatchSegments> MaterializeAsync(ScanContext ctx, CancellationToken ct);  // I/O, coalesced
Canonical               Execute(ScanContext ctx, in BatchSegments segments);      // CPU, sync
```

An `async` canonicalization would contradict the I/O design: coalescing only works because the
reader registers every segment of a split *before* reading, so by the time decoding runs there is
nothing left to await. Threading `ValueTask` through every decoder would pollute the hot path with
state machines to cover a single real case — lazily loading the `values` side of a `dict` layout —
and that case belongs at the layout level, where materialization decides what to fetch, not at the
array level.

The context type is named `ScanContext`, not `ExecutionContext`, to avoid colliding with
`System.Threading.ExecutionContext` and forcing qualified usings on every consumer.

### 3.7 Async enumeration

A compiler-generated `async IAsyncEnumerator` allocates its state machine, and potentially more per
`MoveNextAsync`. One allocation per *scan* is acceptable and documented; per *batch* is not. The
enumerator is therefore hand-written over `ManualResetValueTaskSourceCore<bool>` — the pattern
`System.Threading.Channels` uses. This is not exotic, but it is incompatible with writing
`yield return` and letting the compiler decide, so it is specified here rather than discovered by
`MemoryDiagnoser`.

### 3.8 Write sink

The reader has `ISegmentSource`; the writer's symmetric seam is a `PipeWriter`, and the format
makes it unusually simple. `CreateWriter(path)` builds one over `File.OpenHandle`,
`PipeWriter.Create(stream)` covers a `Stream`, and an object store provides its own. Inside, the
writer writes through a forward-only sink over that pipe:

```csharp
internal interface ISegmentSink
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);   // strictly sequential
    ValueTask FlushAsync(CancellationToken ct);                              // the pipe's flush: its backpressure
    long Position { get; }
}
```

**Single-pass, forward-only, no seeking.** Segments, then metadata FlatBuffers, then postscript,
then the EOF marker — the format is designed for exactly this order, which is what makes S3
multipart upload trivial in the layer above: a `PipeWriter` that flushes a part per threshold, and
nothing in the writer knows. Bytes reach the pipe's buffer as they are encoded and leave it only at
`FlushAsync` and `CompleteAsync`, so a sink that applies backpressure slows the producer rather than
growing a buffer. A writer that gives its file up completes the caller's pipe with an error, so that
whatever it feeds knows the bytes are not a file.

Two consequences to write down before someone implements them the slow way:

* Inter-segment padding is computed on the fly — each segment's required alignment is known when
  it is written, so **no full-file materialization is ever needed**. A giant buffer "for
  simplicity" would defeat the streaming property entirely.
* The postscript has a hard 65527-byte ceiling. Plenty of user metadata will breach it; the error
  must be raised early and clearly, not as a truncation at the last write.

## 4. Performance invariants

Enforced in CI, not merely documented:

1. **No managed allocation per batch** in steady state on a full scan (`MemoryDiagnoser`,
   threshold = 0 bytes excluding output buffers).
2. **Actual zero-copy**: on an `mmap` scan of an uncompressed file, no `memcpy` of data buffers
   (verified by pointer comparison in a dedicated test).
3. **No megamorphic dispatch** in decode loops: every kernel is generic and monomorphized.
4. **Scalar fallback tested**: every SIMD kernel has a test that forces the scalar path and
   compares results bit for bit (`DOTNET_EnableHWIntrinsic=0`).
5. **AOT**: a test publishes `vxdump` as Native AOT and runs it over the corpus.

## 5. Error handling

* Hot path: return codes / `TryXxx`, no exceptions.
* Public boundary: `VortexFormatException` (malformed file),
  `VortexUnsupportedException(string componentId, ComponentKind kind)` — the latter must **always**
  name the unknown component's ID and kind, because that is precisely the information upstream
  documentation requires to diagnose it ("which edition, which minimum version"). `ComponentKind`
  is `Array`, `Layout`, `DType`, `Aggregate`, `Compression`, `Encryption`, `Index` or `Feature`
  ([14-public-api.md](14-public-api.md) §7).
* No `AllowUnknownComponents` option: an unknown component fails only the read that needs it, and
  an unknown aggregate disables the corresponding pruning, both unconditionally
  ([08-semantics.md](08-semantics.md) §4). `VortexFile.ArrayEncodings` and `LayoutEncodings` list
  what a footer declares, each with `Supported`, which is the inspection an option would have
  offered.

## 6. Parser safety

A Vortex file is untrusted input. Non-negotiable rules:

* Every offset/length read from the file is validated against the real size **before** any access.
* FlatBuffers verification follows the format's actual rules — stated precisely, because the
  obvious phrasing both over- and under-rejects:
  * **uoffsets** (table and vector references) are unsigned and point forward: require
    `uoffset > 0` and the target in bounds. Progression is monotonic, so **cycles are impossible
    by construction** — no cycle detection, and no "already visited" check.
  * Forward-only uoffsets exclude cycles but **not sharing**: two slots at different positions may
    legally resolve to the same target, so the decoded object graph is a **DAG, not a tree**. A
    consumer that recurses once per edge is therefore exponential in depth — a 1.5 KB buffer of
    shared children passes every bounds, offset and depth rule while costing 2^63 visits. **Total
    visited work needs its own bound**, separate from depth: a recursive reader memoises revisited
    positions, and `FlatBufferTable.Root(buffer, ref tableBudget)` additionally charges every table
    against `VortexLimits.MaxFlatBufferTables` (the reference verifiers' `max_tables`).
  * **soffsets** (vtable references) are signed: a vtable may precede or follow its table. Bound
    them in both directions.
  * **vtable sharing between tables is legal and routine** (a standard builder optimization). A
    verifier that refuses to revisit a position would reject perfectly valid files.
  * Recursion depth is capped for layout trees, array trees **and DTypes** — a 10 000-deep nested
    struct blows the stack during schema parsing, before any data is read. Caps in
    [08-semantics.md](08-semantics.md) §6.
* No allocation sized directly by a file-supplied value without a cap. The caps are enumerated in
  [08-semantics.md](08-semantics.md) §6 rather than invented per call site — `alignment_exponent`
  is a `u8`, so an unchecked file can demand `2^255`.
* Semantic hints are classified and handled per [08-semantics.md](08-semantics.md) §5: fields that
  memory safety depends on are validated unconditionally; fields that only affect result
  correctness are covered by the threat model or by opt-in `VerifyStatistics`.
* Children `row_count`s must be consistent with their parents (the spec already requires this for
  `chunked`) — consistency is verified, never assumed.
