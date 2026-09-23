# Public API: the surface, the symbolic scan, and the read and write cases

A Vorticity specification, written 2026-09-21. [03-architecture.md](03-architecture.md) owns the
internal object model and the performance invariants; [07-dotnet-mapping.md](07-dotnet-mapping.md)
owns which .NET type each dtype becomes; [08-semantics.md](08-semantics.md) owns what a predicate
means; [12-index-reads.md](12-index-reads.md) owns the key cursor; [13-dataset.md](13-dataset.md)
owns the dataset. This document owns **every public type a caller can name**, the shape of the
calls, and the cost of each shape. Where it changes a decision written elsewhere, §13 lists the
amendment.

It replaces the surface that grew with the implementation: 253 public types in the core assembly,
95 of them decoders and encoding metadata, a write path that hands the caller an arena and node
indices, a read path whose literals carry no type, and a mutable static for parallelism. The new
surface is about sixty types, and every one of them is something a caller names on purpose.

Five rules decide every signature below:

1. **A type is a schema, not a row.** A record declares which columns exist and how they are
   typed. Reading it yields columns; rows are a sink you ask for, and pay for.
2. **The compiler holds the lifetimes.** Everything borrowed from a scan is a `ref struct`. There
   is no `Dispose` on the hot path and nothing to forget.
3. **The plan lives under the API.** A scan is described by ordinary C# lambdas over a symbolic
   probe. What cannot be pushed down does not compile. There is no expression tree, no string
   column name, and no untyped value on the typed path.
4. **Generic, monomorphised, BCL first.** `INumber<T>`, static abstract members,
   `System.Runtime.Intrinsics`, `System.IO.Pipelines`, `System.Diagnostics.Metrics`, and nothing
   the shared framework does not ship beyond the one package [03-architecture.md](03-architecture.md)
   allows. Nothing virtual per value, ever.
5. **A session, not a static.** Pools, caches, I/O concurrency, parallelism and extension
   registries live on a `VortexSession`. `VortexSession.Default` is immutable.

What this is not: a DataFrame, a SQL engine, a join, a window function, a bridge to another
columnar library. None of those has a consumer in this project's horizon; the owned `RecordBatch`
of §5.8 is where a bridge would attach if one ever did, without touching the core. The plan this
API builds is also what an Entity Framework provider would target one day; nothing here requires
building one now.

## 1. Packages and namespaces

| Package | Contents | Dependencies |
|---|---|---|
| `Vorticity` | session, file, writer, schema, the typed scan, columns, the tool scan, I/O seam, options, plans, exceptions | `System.IO.Hashing` only |
| `Vorticity.Generators` | the `[VortexRecord]` source generator and the analyzers | analyzer package, build-time only |
| `Vorticity.Dataset` | the versioned dataset over an object store, `[Experimental("VX0001")]` | `Vorticity` |
| `Vorticity.RowEncoding` | the byte-sortable row encoding, `[Experimental("VX0002")]` | `Vorticity` |
| `vxdump` | the inspection tool, published ahead of time, **written against the public surface only** | `Vorticity` |

Namespaces in the core: `Vorticity` for everything a caller uses, `Vorticity.IO` for the segment
seam. Nothing else is public.

The attributes the generator recognises live in the core (`VortexRecordAttribute`,
`VortexColumnAttribute`, `VortexIgnoreAttribute`) so that a project can implement
`IVortexRecord<TSelf>` by hand without referencing the generator.

## 2. The session

```csharp
VortexSession session = VortexSession.Create(o =>
{
    o.MemoryPool = AlignedMemoryPool.Shared;
    o.SegmentCache = new SegmentCache(256 * 1024 * 1024);
    o.MaxConcurrentReads = 32;
    o.MaxDegreeOfParallelism = Environment.ProcessorCount;
    o.Extensions.Register<MyExtensionType>();
});
```

```csharp
public sealed class VortexSession : IAsyncDisposable
{
    public static VortexSession Default { get; }                                   // immutable, process-wide
    public static VortexSession Create(Action<VortexSessionOptions> configure);
    public VortexSessionOptions Options { get; }                                   // frozen after Create

    public ValueTask<VortexFile> OpenAsync(string path, VortexOpenOptions? options = null, CancellationToken ct = default);
    public ValueTask<VortexFile> OpenAsync(ISegmentSource source, VortexOpenOptions? options = null, CancellationToken ct = default);
    public VortexFileWriter CreateWriter(string path, VortexSchema schema, VortexWriteOptions? options = null);
    public VortexFileWriter CreateWriter<TRecord>(string path, VortexWriteOptions? options = null) where TRecord : IVortexRecord<TRecord>;
    public VortexFileWriter CreateWriter(PipeWriter sink, VortexSchema schema, VortexWriteOptions? options = null);
    public ValueTask<VortexFileWriter> AppendAsync(string path, VortexWriteOptions? options = null, CancellationToken ct = default);
}

public sealed class VortexSessionOptions
{
    public MemoryPool<byte> MemoryPool { get; set; } = AlignedMemoryPool.Shared;   // 64-byte aligned, GC-accounted; another pool serves the builders and owned batches only
    public SegmentCache? SegmentCache { get; set; }                                // null: no cache across scans; sources that do I/O only
    public int MaxConcurrentReads { get; set; } = 16;                              // reads in flight across the session; a SemaphoreSlim, no package; sources that do I/O only
    public int MaxDegreeOfParallelism { get; set; } = 1;                           // decode and aggregate parallelism
    public long IndexCacheBytes { get; set; } = 64L * 1024 * 1024;                 // decoded index runs, per open file
    public VortexExtensionRegistry Extensions { get; }
}

public sealed class AlignedMemoryPool : MemoryPool<byte>
{
    public static new AlignedMemoryPool Shared { get; }
    public AlignedMemoryPool(int alignment = 64, long maxRetainedBytes = 256L * 1024 * 1024);
}

public sealed class SegmentCache
{
    public SegmentCache(long capacityBytes);
    public long Capacity { get; }  public long Size { get; }  public long Hits { get; }  public long Misses { get; }
    public void Clear();
}

public sealed class VortexExtensionRegistry
{
    public void Register<TExtension>() where TExtension : IVortexExtension<TExtension>;
}

public interface IVortexExtension<TSelf> where TSelf : IVortexExtension<TSelf>
{
    static abstract string Id { get; }                                             // "acme.money"
    static abstract VortexType StorageType { get; }
    static abstract TSelf FromStorage(ReadOnlySpan<byte> storage, ReadOnlySpan<byte> metadata);
    static abstract void ToStorage(in TSelf value, Span<byte> storage, ReadOnlySpan<byte> metadata);
}
```

What the session owns, and why it is not a static:

| Owned | Consequence |
|---|---|
| the memory pool | every batch, segment and builder buffer comes from it; disposing the session returns everything. The engine decodes into 64-byte aligned blocks, so a pool that is not an `AlignedMemoryPool` serves the builders and the owned batches, and the engine falls back to `AlignedMemoryPool.Shared` |
| the segment cache | one budget for all the files of the session, not one per file; a scan that re-reads a footer or an index region hits it |
| I/O concurrency | one bound on the reads in flight across every scan of every file; an object store sees at most `MaxConcurrentReads` requests from this process |
| the index cache | `IndexCacheBytes` bounds what each open file keeps of decoded index runs; it is per file, because the runs die with the file, and 0 keeps nothing |

The segment cache and the bound on reads apply to a source that does I/O: a `FileSegmentSource`, or
an `ISegmentSource` of the caller's. A `MemorySegmentSource` or a `MemoryMappedSegmentSource`, and a
path, whose few positional reads come before its mapping, have nothing to bound or to cache, and
are read directly.
| parallelism | the degree every scan of the session starts from; `ScanOptions.DegreeOfParallelism` overrides it for one scan |
| extensions | dtype ids this process knows beyond the frozen editions, so `Column<Money>` and a `Money` record member work |

`VortexSession.Default` is what `VortexFile.OpenAsync(path)` uses. Its degree of parallelism is 1: a
library does not take a host's cores without being asked, and the host asks once, on its session.
A path is opened by reading its tail positionally, and mapped into memory by the first scan whose
plan reads data ([15-anticipated-decisions.md](15-anticipated-decisions.md) part A), which is what
the benchmarks measure; a caller who wants every read through `RandomAccess` passes a
`FileSegmentSource`, and one who wants the mapping from the open a `MemoryMappedSegmentSource` (§8).

A registered extension is decoded as its storage, and the registered type reads the storage
bytes; binding a member or a `Column<T>` to it checks that the file stores the extension as the
registration's `StorageType` says, and refuses it with `VortexSchemaException` otherwise.

Thread-safety: a session is thread-safe and expected to be shared by every request of a process.
Disposing it while a file is open throws `InvalidOperationException` naming the file.

## 3. Schema and types

The schema is a value. The arena the reader uses internally is built once at `OpenAsync` and
`CreateWriter` and never appears.

```csharp
[CollectionBuilder(typeof(VortexSchema), nameof(Create))]
public sealed class VortexSchema : IReadOnlyList<VortexField>, IEquatable<VortexSchema>, ISpanFormattable, IUtf8SpanFormattable
{
    public static VortexSchema Create(ReadOnlySpan<VortexField> fields);
    public VortexField this[int index] { get; }
    public int Count { get; }
    public int IndexOf(string path);                                               // -1 when absent; dotted for a nested field
    public bool TryGetField(ReadOnlySpan<byte> pathUtf8, out int index);
}

public readonly record struct VortexField(string Name, VortexType Type)
{
    public static implicit operator VortexField((string Name, VortexType Type) field);
}

public sealed class VortexType : IEquatable<VortexType>, ISpanFormattable, IUtf8SpanFormattable   // immutable, interned by value
{
    public static VortexType Null, Bool, Int8, Int16, Int32, Int64, UInt8, UInt16, UInt32, UInt64, Float16, Float32, Float64, Utf8, Binary;
    public static VortexType Decimal(int precision, int scale);
    public static VortexType List(VortexType element);
    public static VortexType FixedSizeList(VortexType element, int size);
    public static VortexType Struct(ReadOnlySpan<VortexField> fields);
    public static VortexType Date;
    public static VortexType Time(TimeUnit unit);
    public static VortexType Timestamp(TimeUnit unit, string? timeZone = null);
    public static VortexType Uuid;
    public static VortexType Extension(string id, VortexType storage, ReadOnlyMemory<byte> metadata = default);

    public VortexTypeKind Kind { get; }
    public bool IsNullable { get; }
    public VortexType Nullable { get; }                                            // the same type, nullable
    public VortexType NonNullable { get; }
    public VortexType? ElementType { get; }                                        // list, fixed-size list
    public int FixedSize { get; }                                                  // fixed-size list
    public ReadOnlySpan<VortexField> Fields { get; }                               // struct
    public int Precision { get; }  public int Scale { get; }                       // decimal
    public string? ExtensionId { get; }  public VortexType? StorageType { get; }   // extension
    public ReadOnlyMemory<byte> ExtensionMetadata { get; }                         // extension: part of the type's identity
}

public enum VortexTypeKind : byte { Null, Bool, Primitive, Decimal, Utf8, Binary, Struct, List, FixedSizeList, Extension, Map, Union, Variant }
public enum TimeUnit : byte { Nanoseconds, Microseconds, Milliseconds, Seconds, Days }
```

```csharp
VortexSchema schema = [("day", VortexType.Int32), ("celsius", VortexType.Float64.Nullable), ("city", VortexType.Utf8)];
```

`Map`, `Union` and `Variant` have no factory: a file may declare them, so the schema shows them,
but no `T` maps to them and a record cannot name them.

### 3.1 The .NET mapping

The contract of [07-dotnet-mapping.md](07-dotnet-mapping.md), restated as what a record member and
a `Column<T>` may be. `T` carries nullability: `Column<double?>` is a nullable column,
`Column<double>` is not, and the binding at open refuses a non-nullable member over a nullable
column with `VortexSchemaException`.

| dtype | `T` | what `Column<T>` exposes | notes |
|---|---|---|---|
| bool | `bool` | `Bits` as `ReadOnlySpan<ulong>`, indexer | |
| i8 to i64, u8 to u64 | `sbyte` to `long`, `byte` to `ulong` | `Values`, indexer | exact |
| f16, f32, f64 | `Half`, `float`, `double` | `Values`, indexer | exact |
| utf8 | `string` | UTF-8 span indexer, `GetString(i)` on request (`null` at a null slot) | 07 §4: spans first |
| binary | `ReadOnlyMemory<byte>` | span indexer | |
| decimal(p ≤ 28, s ≤ 28) | `decimal` | indexer, `Storage<TStorage>()`, `Scale` | exact within 07 §2's bounds |
| decimal, wider | `VortexDecimal` | indexer, `Storage<TStorage>()` | 07 §2 |
| vortex.date | `DateOnly` | indexer, `Storage<int>()` or `Storage<long>()` | |
| vortex.time | `TimeOnly` | indexer, `Storage<TStorage>()`, `Unit` | |
| vortex.timestamp, naive or UTC | `DateTime` | indexer, `Storage<long>()`, `Unit` | `Kind` is `Unspecified` or `Utc` |
| vortex.timestamp with a zone | `DateTimeOffset` | indexer, `Storage<long>()`, `Unit`, `TimeZone` | the zone is resolved once at open by `TimeZoneInfo.FindSystemTimeZoneById`; a zone the host cannot resolve binds only as `long` |
| vortex.uuid | `Guid` | indexer, `Storage<Guid>()` is not offered: the conversion is explicit | 07 §1 |
| list<T> | `ReadOnlyMemory<T>` | `Range` indexer, `Elements` as `Column<T>` | rows allocate one array per list |
| struct | a nested `[VortexRecord]` type | `Columns<TNested>` | |
| a registered extension | the registered type | indexer, `Storage<TStorage>()` | §2; its members live on `ExtensionColumnExtensions`, apart from the built-in families, because a generic extension block constrained to `IVortexExtension<T>` would clash with them in the CLR signature |
| any nullable dtype | `T?`, or `string?` | `ValidityWords` | |

An `enum` member maps to its underlying integer dtype. A `char` is refused: it is not a dtype.

## 4. Records: the generated contract

```csharp
[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);
```

A `[VortexRecord]` type is a `partial` `record struct`, `record class`, `struct` or `class` whose
public properties, fields or primary-constructor parameters become columns, in declaration order,
under their own names. `[VortexColumn("session_id")]` renames one; `[VortexIgnore]` skips one.
A .NET type does not say everything a dtype does, so `[VortexColumn]` also carries what the
schema needs beyond the type: `Precision` and `Scale` for a `decimal` member (28 and 10 by
default), `Unit` for a time or timestamp (microseconds by default) and `TimeZone` for a
`DateTimeOffset`. They shape the schema the record writes; when reading, the file's dtype decides
and the binding checks that the member's type fits it.

```csharp
public interface IVortexRecord<TSelf> where TSelf : IVortexRecord<TSelf>
{
    static abstract VortexSchema Schema { get; }
    static abstract void ReadRows(Columns<TSelf> columns, Span<TSelf> rows);
    static abstract void WriteRows(ColumnsBuilder<TSelf> builder, ReadOnlySpan<TSelf> rows);
}
```

The generator emits, for `Reading`:

```csharp
public partial record struct Reading : IVortexRecord<Reading>
{
    public static VortexSchema Schema { get; } = [("Day", VortexType.Int32), ("Celsius", VortexType.Float64.Nullable), ("City", VortexType.Utf8)];
    public static void ReadRows(Columns<Reading> columns, Span<Reading> rows) { /* column to field, one column at a time */ }
    public static void WriteRows(ColumnsBuilder<Reading> builder, ReadOnlySpan<Reading> rows) { /* field to builder, one column at a time */ }
    public static class ColumnNames { public const string Day = "Day", Celsius = "Celsius", City = "City"; }
}

public static class ReadingVortexExtensions                                        // C# 14 extension members
{
    extension(Probe<Reading> r)
    {
        public Sym<int> Day => r.Column<int>(0);
        public Sym<double?> Celsius => r.Column<double?>(1);
        public Sym<string> City => r.Column<string>(2);
    }
    extension(Columns<Reading> c)
    {
        public Column<int> Day => c.Column<int>(0);
        public Column<double?> Celsius => c.Column<double?>(1);
        public Column<string> City => c.Column<string>(2);
        public void Deconstruct(out Column<int> day, out Column<double?> celsius, out Column<string> city);
    }
    extension(ColumnsBuilder<Reading> b)
    {
        public ColumnBuilder<int> Day => b.Column<int>(0);
        public ColumnBuilder<double?> Celsius => b.Column<double?>(1);
        public ColumnBuilder<string> City => b.Column<string>(2);
    }
}
```

Binding a record to a file happens once, at the first sink of a scan or at `CreateWriter<TRecord>`:
each member is matched to a column by exact name, then by a unique case-insensitive match; a member
with no column, an ambiguous match, or a type that does not fit the dtype under §3.1 throws
`VortexSchemaException` naming the member and the column. A file with more columns than the record
is fine: the record is the projection. A file with fewer is not.

Everything the generator emits can be written by hand: the interface is public and the extension
members are sugar. A hand-written record loses nothing but the sugar.

What the generator decides where §3.1 leaves room: an `enum` member is a `Sym<TEnum>` on the probe,
so an enum literal filters, and its underlying integer on the columns and the builder, which have
no enum accessors; a nullable list member is exposed as `ReadOnlyMemory<T>`, the form with
accessors and `AppendNull`; a `DateTimeOffset` member without `[VortexColumn(TimeZone = …)]` is
UTC. A list of records, and a nullable registered extension, are refused with VX1005: no typed
column reads the first, and no builder appends a null to the second. Positional parameters are
columns first, then public instance properties and fields, base types first; a member that would
collide with a generated one makes the interface implementation explicit.

## 5. Reading

### 5.1 `Scan<TRecord>`

```csharp
public sealed class VortexFile : IAsyncDisposable
{
    public static ValueTask<VortexFile> OpenAsync(string path, CancellationToken ct = default);   // VortexSession.Default
    public VortexSession Session { get; }
    public VortexSchema Schema { get; }
    public long RowCount { get; }
    public long Length { get; }
    public Guid Identity { get; }
    public VortexEdition Edition { get; }
    public VortexFileStatistics Statistics { get; }
    public VortexMetadata Metadata { get; }
    public VortexTornTail? TornTail { get; }

    public Scan<TRecord> Scan<TRecord>() where TRecord : IVortexRecord<TRecord>;
    public Scan Scan(params ReadOnlySpan<string> columns);                          // §5.8, the tool path
    public bool MayMatch<TRecord>(Func<Probe<TRecord>, Predicate> predicate) where TRecord : IVortexRecord<TRecord>;   // the file statistics the open read, no I/O: false is a proof, true is not

    public ValueTask<ImmutableArray<VortexIndexInfo>> GetIndexesAsync(CancellationToken ct = default);
    public ValueTask<VortexIndexVerification> VerifyIndexesAsync(CancellationToken ct = default);

    // inspection, for tools: what the footer declares and how the file is laid out
    public ImmutableArray<VortexComponent> ArrayEncodings { get; }
    public ImmutableArray<VortexComponent> LayoutEncodings { get; }
    public ImmutableArray<VortexSegment> SegmentMap { get; }
    public ValueTask<VortexLayout> GetLayoutAsync(CancellationToken ct = default);
}

public sealed record VortexComponent(string Id, bool Supported);                    // Supported: this build reads it
public sealed record VortexSegment(long Offset, int Length, int Alignment);
public sealed record VortexLayout(string Encoding, long RowCount, VortexType? Type, ImmutableArray<int> Segments,
    int ZoneCount, long ZoneLength, bool ZonesUsable, string? ArrayEncoding, ImmutableArray<VortexLayout> Children);
public sealed record VortexIndexInfo(string Kind, string Column, long BlockLength, int Runs, long Blocks, long Entries,
    long ListedBytes, VortexIndexLayout Layout);                                     // ListedBytes: what the directory lists; pages and trees hold more below
public enum VortexIndexLayout { None, Listed, FencePages, FilterTree }             // no payload, every region listed, fence pages, a tree of Bloom filters

public sealed class Scan<TRecord> where TRecord : IVortexRecord<TRecord>
{
    // composition; each call returns the same builder; a builder is single-use and not thread-safe
    public Scan<TRecord> Where(Func<Probe<TRecord>, Predicate> predicate);          // several calls are joined by &
    public Scan<TRecord> Rows(RowRange range);
    public Scan<TRecord> Rows(params ReadOnlySpan<long> indices);                   // sorted and deduplicated; refuses a range on the same scan
    public Scan<TRecord> OrderBy<TKey>(Func<Probe<TRecord>, Sym<TKey>> key, bool descending = false);   // key-ordered delivery, 12 §6
    public Scan<TRecord> With(ScanOptions options);
    public Scan<TRecord> WithCancellation(CancellationToken ct);                    // for await foreach, which passes no token; a token given to GetAsyncEnumerator wins

    // sinks: columns, borrowed
    public AsyncEnumerator GetAsyncEnumerator(CancellationToken ct = default);      // await foreach
    // sinks: batches, owned
    public IAsyncEnumerable<RecordBatch> ToBatchesAsync(CancellationToken ct = default);
    // sinks: rows
    public IAsyncEnumerable<TRecord> ToRecordsAsync(CancellationToken ct = default);
    // sinks: answers
    public ValueTask<long> CountAsync(CancellationToken ct = default);
    public ValueTask<bool> AnyAsync(CancellationToken ct = default);
    public ValueTask<T?> MinAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default);
    public ValueTask<T?> MaxAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default);
    public ValueTask<T> SumAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default) where T : INumber<T>;
    public ValueTask<double?> AvgAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default) where T : INumber<T>;
    public ValueTask<long> CountDistinctAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default);
    public ValueTask<TState> AggregateAsync<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken ct = default)
        where T : unmanaged where TAggregator : IAggregator<T, TState>;
    public ValueTask<T1> AggAsync<T1>(Func<Aggregates<TRecord>, Sym<T1>> aggregate, CancellationToken ct = default);
    public ValueTask<(T1, T2)> AggAsync<T1, T2>(Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>)> aggregates, CancellationToken ct = default);   // arities 2 to 8
    public GroupedScan<TRecord, TKey> GroupBy<TKey>(Func<Probe<TRecord>, Sym<TKey>> key);
    public GroupedScan<TRecord, (TKey1, TKey2)> GroupBy<TKey1, TKey2>(Func<Probe<TRecord>, (Sym<TKey1>, Sym<TKey2>)> keys);   // arities 1 to 4
    public KeyCursorBuilder<TKey> Keys<TKey>(Func<Probe<TRecord>, Sym<TKey>> column);   // §5.7; refuses a scan with Where or Rows

    // the plan, before; what happened, after
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken ct = default);
    public ScanStatistics Statistics { get; }                                       // valid once a sink has run

    public sealed class AsyncEnumerator : IAsyncDisposable { public ValueTask<bool> MoveNextAsync(); public Columns<TRecord> Current { get; } }
}
```

The borrow contract, which is the contract of `PipeReader` without the class: `Current` is valid
until the next `MoveNextAsync` or `DisposeAsync`. A `Columns<TRecord>` is a `ref struct`, so it
cannot be stored, captured or carried across an `await`; the one thing the compiler does not see,
a span copied into a variable declared outside the loop, analyzer VX1001 does.

There is no synchronous enumeration, and no synchronous path anywhere in the surface: a scan is
consumed with `await foreach`, whatever the source. The body of the loop runs synchronously
between two batches, so a `ref struct` in it is legal. The pipeline under it overlaps the decode
of the next batch with the caller's work on the current one, by `ScanOptions.Prefetch` batches,
with buffers that alternate rather than accumulate.

### 5.2 The symbolic algebra

The lambda given to `Where` has the syntax of LINQ and none of its machinery. `Probe<TRecord>` is a
sonde: each member is a `Sym<T>`, and the operators of `Sym<T>` record a predicate instead of
evaluating one. The lambda runs once, when the scan is built, and the result is the plan.

```csharp
public readonly struct Probe<TRecord>
{
    public Sym<T> Column<T>(int index);
    public Sym<T> Column<T>(string name);
    public Probe<TNested> Struct<TNested>(int index) where TNested : IVortexRecord<TNested>;
    public Predicate IsNull { get; }                                                // a nested record from Struct: the rows that hold none
    public Predicate IsNotNull { get; }                                             // the scan's own record is never null: no row, and every row
}

public readonly struct Sym<T>
{
    // the literal has the column's type; a column can be compared to another column of the same type
    public static Predicate operator ==(Sym<T> column, T value);   public static Predicate operator ==(Sym<T> left, Sym<T> right);
    public static Predicate operator !=(Sym<T> column, T value);   public static Predicate operator !=(Sym<T> left, Sym<T> right);
    public static Predicate operator <(Sym<T> column, T value);    public static Predicate operator <(Sym<T> left, Sym<T> right);
    public static Predicate operator <=(Sym<T> column, T value);   public static Predicate operator <=(Sym<T> left, Sym<T> right);
    public static Predicate operator >(Sym<T> column, T value);    public static Predicate operator >(Sym<T> left, Sym<T> right);
    public static Predicate operator >=(Sym<T> column, T value);   public static Predicate operator >=(Sym<T> left, Sym<T> right);
    public Predicate In(params ReadOnlySpan<T> values);
    public Predicate Between(T low, T high);                                        // inclusive
    public Predicate IsNull { get; }
    public Predicate IsNotNull { get; }

    // refused with our own message rather than the compiler's
    [Obsolete("Vortex does not push arithmetic down. Compute it on the columns after the scan.", error: true)]
    public static Sym<T> operator +(Sym<T> column, T value);                       // likewise -, *, /, %

    public override bool Equals(object? obj);  public override int GetHashCode();  // required by ==; not meaningful
}

public static class SymExtensions
{
    extension<TText>(Sym<TText> column) where TText : IComparable<string?>?        // Sym<string> and Sym<string?> alike
    {
        public Predicate StartsWith(string prefix);
        public Predicate Contains(string text);
        public Predicate Like(string pattern, char escape = '\\');
    }
    extension(Sym<bool> column) { public Predicate IsTrue { get; }  public Predicate IsFalse { get; } }
    extension<T>(Sym<ReadOnlyMemory<T>> list) { public Predicate Contains(T value); }
}

public readonly struct Predicate
{
    public static Predicate All { get; }                                            // every row; the identity of &
    public static Predicate None { get; }
    public static Predicate operator &(Predicate left, Predicate right);
    public static Predicate operator |(Predicate left, Predicate right);
    public static Predicate operator !(Predicate operand);
    public static bool operator true(Predicate p) => false;                        // so that && and || evaluate both sides
    public static bool operator false(Predicate p) => false;
}
```

What compiles is exactly what is pushed down:

| written | meaning | pushed |
|---|---|---|
| `r.Day >= 900` | comparison to a literal of the column's type | zone maps, indexes, then the kernel |
| `r.Day >= "900"` | | **does not compile**: `Sym<int>` has no operator with `string` |
| `r.Celsius > 45.0 && r.City == "Paris"` | conjunction | both, `&&` evaluates both sides through `operator false` |
| `r.Celsius == null`, `r.Celsius.IsNull` | nullity | never `unknown`, 08 §3 |
| `r.City.StartsWith("Par")`, `r.City.Like("P_r%")` | text | string-bound zones when written, n-gram indexes, then `SearchValues` |
| `r.Day.In(1, 2, 3)` | membership | zone maps per value, then the kernel |
| `r.High > r.Low` | column to column | zone maps when bounds settle it, then the kernel |
| `r.Pages.Contains(42)` | list membership | the kernel |
| `r.Day % 2 == 0` | arithmetic | **does not compile**, with the message above |
| `Helper(r.Day)` | a call | **does not compile** unless `Helper` takes a `Sym<int>`, in which case it composes |
| `r.Day >= min` where `min` is a local | a captured value | read once, when the lambda runs |

An optional filter is two `Where` calls, not a conditional inside one: the second call joins with
`&`. `Predicate.All` is the neutral element for a filter built in a loop.

A literal is compared exactly, never rounded into the column's unit. A `TimeOnly`, `DateTime` or
`DateTimeOffset` finer than a time or timestamp column's unit, which `DateTime.UtcNow` is for a
microsecond column, lies strictly between two stored values: `==` holds for no row, `!=` for every
row with a value, and an ordering compares against the neighbour on the right side of it, so
`r.At <= now` neither gains nor loses the row at the boundary. One past what the column's integers
reach, `DateTime.MaxValue` for a nanosecond column, holds for every row with a value or for none.
`In` drops such a value, and the key cursor seeks and ranks it the same way. A decimal literal
follows the same rule against the column's scale.

A breakpoint inside the lambda sees `Sym<int>`, not values, and hits once. That sentence belongs in
the guide's first page.

### 5.3 `Columns<TRecord>` and `Column<T>`

```csharp
public readonly ref struct Columns<TRecord>
{
    public int RowCount { get; }
    public long StartRow { get; }                                                   // the file row of the block's first row; row i is StartRow + i unless a filter compacted the batch
    public Selection Selection { get; }                                            // IsAll unless ScanOptions.Compact is false
    public bool IsAllValid { get; }  public ReadOnlySpan<ulong> ValidityWords { get; }  public bool IsValid(int index);   // a nullable nested record's own nulls
    public Column<T> Column<T>(int index);
    public Columns<TNested> Struct<TNested>(int index) where TNested : IVortexRecord<TNested>;
    public RecordBatch ToOwned();                                                   // one copy into pooled buffers the caller owns
}

public readonly ref struct Column<T>
{
    public int Length { get; }
    public int NullCount { get; }
    public bool IsAllValid { get; }
    public ReadOnlySpan<ulong> ValidityWords { get; }                               // empty when IsAllValid; bit i of word i / 64; LSB first; bits past Length are 0
    public bool IsValid(int index);
    public ColumnEncoding Encoding { get; }                                         // Canonical, Dictionary, RunEnd, Constant
    public DictionaryView<T> AsDictionary();                                        // throws unless Encoding is Dictionary
    public RunEndView<T> AsRunEnd();
    public T AsConstant();
    public Column<T> Canonical();                                                   // forces the decode
}

public static class ColumnExtensions
{
    extension<T>(Column<T> column) where T : unmanaged, IBinaryNumber<T>
    {
        public ReadOnlySpan<T> Values { get; }                                     // contiguous, 64-byte aligned
        public T this[int index] { get; }
        public T[] ToArray();
        public void CopyTo(Span<T> destination);
    }
    extension<T>(Column<T?> column) where T : unmanaged, IBinaryNumber<T>
    {
        public ReadOnlySpan<T> Values { get; }                                     // undefined at a null slot; read ValidityWords
        public T? this[int index] { get; }
    }
    extension(Column<bool> column)                     { public ReadOnlySpan<ulong> Bits { get; }  public bool this[int index] { get; } }
    extension(Column<ReadOnlyMemory<byte>> column)     { public ReadOnlySpan<byte> this[int index] { get; } }
    extension(Column<decimal> column)                  { public decimal this[int index] { get; }  public int Scale { get; }  public Column<TStorage> Storage<TStorage>() where TStorage : unmanaged; }
    extension(Column<VortexDecimal> column)            { public VortexDecimal this[int index] { get; }  public Column<TStorage> Storage<TStorage>() where TStorage : unmanaged; }
    extension(Column<DateOnly> column)                 { public DateOnly this[int index] { get; }  public Column<TStorage> Storage<TStorage>() where TStorage : unmanaged; }
    extension(Column<TimeOnly> column)                 { public TimeOnly this[int index] { get; }  public TimeUnit Unit { get; }  public Column<TStorage> Storage<TStorage>() where TStorage : unmanaged; }
    extension(Column<DateTime> column)                 { public DateTime this[int index] { get; }  public TimeUnit Unit { get; }  public Column<long> Storage(); }
    extension(Column<DateTimeOffset> column)           { public DateTimeOffset this[int index] { get; }  public TimeUnit Unit { get; }  public TimeZoneInfo TimeZone { get; }  public Column<long> Storage(); }
    extension(Column<Guid> column)                     { public Guid this[int index] { get; } }
    extension<T>(Column<ReadOnlyMemory<T>> column)     { public Range this[int index] { get; }  public Column<T> Elements { get; } }
}

public static class TextColumnExtensions                                           // Column<string> and Column<string?> alike, without a nullability warning
{
    extension<TText>(Column<TText> column) where TText : IComparable<string?>?    // only string meets it
    {
        public ReadOnlySpan<byte> this[int index] { get; }  public string? GetString(int index);  public int GetLength(int index);
    }
}

public static class ExtensionColumnExtensions                                      // apart: the same block in ColumnExtensions would clash in the CLR signature
{
    extension<T>(Column<T> column) where T : IVortexExtension<T> { public T this[int index] { get; }  public Column<TStorage> Storage<TStorage>() where TStorage : unmanaged; }
}
```

The nullable variants exist for every family above with `T?` or `string?`; they add nothing but
`ValidityWords` being non-empty and the indexer returning `null`.

`Values` is 64-byte aligned whatever the file's segment alignment: a buffer read in place from a
segment laid out on a smaller boundary is copied once into an aligned block of the batch, at its
first read, and every later read of that batch is free.

`Values` on a nullable column is the raw buffer: fast, and garbage where the validity bit is 0. The
two idioms are in §9.4. A consumer that wants `T?` per element pays a branch per element through
the indexer, and knows it.

### 5.4 Encoded views and the selection

```csharp
public readonly ref struct DictionaryView<T>
{
    public ReadOnlySpan<uint> Codes { get; }                                        // widened on demand when the file stores narrower codes
    public Column<T> Values { get; }                                                // the distinct values, in code order
    public int Cardinality { get; }
}

public readonly ref struct RunEndView<T>
{
    public ReadOnlySpan<uint> RunEnds { get; }                                      // exclusive end of each run, relative to the batch
    public Column<T> Values { get; }                                                // one per run
    public int RunCount { get; }
}

public readonly ref struct Selection
{
    public bool IsAll { get; }
    public int Count { get; }
    public ReadOnlySpan<ulong> Words { get; }                                       // empty when IsAll
    public bool Contains(int row);                                                  // one row, without walking the set
    public Enumerator GetEnumerator();                                              // the set indices, by TrailingZeroCount
}

public enum ColumnEncoding : byte { Canonical, Dictionary, RunEnd, Constant }
```

`Encoding` says what the block holds after the scan's own pruning and before any decode. A
`Column<T>` whose encoding is `Dictionary` has not been decoded: `Values` on it forces the decode,
`AsDictionary()` does not. A consumer that groups by a dictionary column groups by code and never
touches eight thousand strings per batch. A consumer that does not care calls `Values` and gets the
canonical form, as before.

With `ScanOptions.Compact = false` a filtered scan delivers the whole block plus its `Selection`
instead of copying the surviving rows into a compact batch. A filter that keeps half of a block
then costs no copy at all.

### 5.5 Aggregates and group by

```csharp
public readonly struct Aggregates<TRecord>                                          // the scalar form: one row of answers
{
    public Sym<long> Count();
    public Sym<long> CountDistinct<T>(Func<Probe<TRecord>, Sym<T>> column);
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T>> column) where T : INumber<T>;
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T?>> column) where T : struct, INumber<T>;      // nulls skipped
    public Sym<T?> Min<T>(Func<Probe<TRecord>, Sym<T>> column);
    public Sym<T?> Max<T>(Func<Probe<TRecord>, Sym<T>> column);
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T>> column) where T : INumber<T>;
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T?>> column) where T : struct, INumber<T>;
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column) where T : unmanaged where TAggregator : IAggregator<T, TState>;
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T?>> column) where T : unmanaged where TAggregator : IAggregator<T, TState>;
}

public readonly struct Group<TRecord, TKey>                                          // the grouped form: Aggregates plus the key
{
    public Sym<TKey> Key { get; }
    // the same members as Aggregates<TRecord>
}

public sealed class GroupedScan<TRecord, TKey>
{
    public Aggregation<T1> AggAsync<T1>(Func<Group<TRecord, TKey>, Sym<T1>> aggregate);
    public Aggregation<(T1, T2)> AggAsync<T1, T2>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>)> aggregates);   // arities 2 to 8; Async because the result is an IAsyncEnumerable
}

public static class SymExtensions                                                   // continued: the columns of a composite key
{
    extension<T1, T2>(Sym<(T1, T2)> key) { public Sym<T1> Item1 { get; }  public Sym<T2> Item2 { get; } }   // likewise for three and four
}

public sealed class Aggregation<TResult> : IAsyncEnumerable<TResult>
{
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken ct = default);
    public ScanStatistics Statistics { get; }
}

public interface IAggregator<T, TState> where T : unmanaged
{
    static abstract TState Seed();
    static abstract void Step(ref TState state, ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity, Selection rows);
    static abstract void Merge(ref TState into, in TState other);                   // what makes parallelism by chunk correct
}

public interface IEncodedAggregator<T, TState> : IAggregator<T, TState> where T : unmanaged
{
    static abstract void StepDictionary(ref TState state, ReadOnlySpan<uint> codes, ReadOnlySpan<T> dictionary, Selection rows);
    static abstract void StepRunEnd(ref TState state, ReadOnlySpan<uint> runEnds, ReadOnlySpan<T> values, Selection rows);
    static abstract void StepConstant(ref TState state, T value, int count);
}
```

An aggregate is an operator of the scan, not a loop the caller writes. It runs block by block on
the encoded form, never materialises a batch, keeps one state per chunk when the session allows
parallelism, and merges at the end. Its memory is the number of groups, not the number of batches.
What each encoding lets it skip:

| block | count, min, max, sum, null count | group by | cost |
|---|---|---|---|
| settled by file or zone statistics | read from the statistics | | 0 per value |
| constant | value times count | one group | 0 |
| run-end | per run, weighted by run length | runs | 1 per run |
| dictionary | over the distinct values, when the selection is all | by code, an array indexed by code, no hashing | 1 increment per value |
| for, bit-packed, delta | SIMD unpack, base applied once | | about 0.5 ns per value |
| a key the statistics say is sorted | | run detection, no hashing | 1 comparison per value |
| a composite key | | row-encoded bytes, one vectorised hash table | one hash per row |
| alp, zstd, fsst | decode, then the canonical kernel | | the decode |

Group by results arrive in key order when the key source is ordered, a sorted column or a
dictionary, and in an unspecified order otherwise. They are few; a caller that wants another order
sorts them.

The built-in aggregators implement `IEncodedAggregator`. A caller's `IAggregator` receives the
canonical form and forces the decode of the column it reads; implementing `IEncodedAggregator` is
how it avoids that. Its `T` is the column's storage primitive, so a decimal, uuid or boolean
column is refused; under Native AOT a caller's encoded steps are not reached and its `Step`
receives the canonical form. The encoded steps receive the rows that pass and hold a value: a
dictionary or run-end block whose distinct or run values hold a null reaches `Step` in the
canonical form instead. Where the rows of a block fall into several groups, rather than one range
per group, each row is one `StepConstant(value, 1)`.

What the answers are made of: an integer sum runs in 128 bits, exact whatever the order of the
rows or the cut of a parallel aggregation, and throws `OverflowException` only when the result
does not fit `T`; a float sum, mean, minimum and maximum skip NaN, as the statistics do, and a
distinct count takes NaN as one value; a uuid's minimum and maximum are byte order; decimals of
more than 38 digits are neither aggregated nor grouped by. `SumAsync`, `AvgAsync` and
`AggregateAsync` take a nullable column too, skipping its nulls. A null key is a group of its own,
last when the order is guaranteed. An aggregation ignores the scan's `OrderBy`.

### 5.6 The sinks

| sink | returns | pay for |
|---|---|---|
| `await foreach` | `Columns<TRecord>` per batch, borrowed | nothing beyond the decode |
| `ToBatchesAsync()` | `IAsyncEnumerable<RecordBatch>`, owned | one copy per batch into pooled buffers; the caller disposes |
| `ToRecordsAsync()` | `IAsyncEnumerable<TRecord>` | a field copy per row; an allocation per row for `string`, `ReadOnlyMemory<T>` and nested class members |
| `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, `AvgAsync`, `CountDistinctAsync` | one value | statistics when they settle it, blocks otherwise |
| `AggAsync(a => (…))` | a tuple, one pass | the same, for several at once |
| `GroupBy(…).AggAsync(g => (…))` | `Aggregation<(…)>` | one state per group |
| `AggregateAsync<T, TAggregator, TState>` | `TState` | the caller's aggregator, monomorphised |
| `ExplainAsync()` | `ScanPlan` | statistics and zone maps, never the data |

LINQ starts after a sink. `ToRecordsAsync` yields an `IAsyncEnumerable<TRecord>`, so
`System.Linq.AsyncEnumerable` applies to it, on the client, on materialised rows, and the boundary
is visible in the code: everything before the sink is pushed, nothing after it is.

### 5.7 The key cursor

The consumer surface of [12-index-reads.md](12-index-reads.md), typed by the key.

```csharp
public sealed class KeyCursorBuilder<TKey>
{
    public KeyCursorBuilder<TKey> Distinct();
    public ValueTask<KeyCursor<TKey>> OpenAsync(CancellationToken ct = default);
    public ValueTask<KeyPlan> ExplainAsync(CancellationToken ct = default);
}

public sealed class KeyCursor<TKey> : IAsyncDisposable
{
    public bool IsValid { get; }  public TKey Key { get; }  public long Row { get; }  public bool HasRows { get; }
    public ValueTask<bool> SeekFirstAsync(CancellationToken ct = default);
    public ValueTask<bool> SeekLastAsync(CancellationToken ct = default);
    public ValueTask<bool> SeekAsync(TKey key, SeekOp op, CancellationToken ct = default);   // Exact, AtOrAfter, After, AtOrBefore, Before
    public ValueTask<bool> SeekRankAsync(long rank, CancellationToken ct = default);
    public ValueTask<bool> NextAsync(CancellationToken ct = default);   public ValueTask<bool> PrevAsync(CancellationToken ct = default);
    public ValueTask<bool> NextKeyAsync(CancellationToken ct = default);  public ValueTask<bool> PrevKeyAsync(CancellationToken ct = default);
    public ValueTask<long> RankAsync(TKey key, CancellationToken ct = default);
    public ValueTask<long> KeyCountAsync(CancellationToken ct = default);           // the entries sharing the current key
}

public sealed record KeyPlan(string Path, KeySourceKind Source, int Runs, long? EntryCount, bool HasRows,
                             IReadOnlyList<KeySourceRejection> Rejected);
public enum KeySourceKind { None, SortedColumn, SortedRuns, Postings, Dictionary }
public sealed record KeySourceRejection(KeySourceKind Source, string Reason);      // why a source was not chosen
```

`file.Scan<Reading>().Keys(r => r.Day)` infers `TKey` from the member. The order of `TKey` is the
file's order, per dtype, as 12 §4.4 fixes it, and `Compare` is not offered: a caller that needs it
compares keys it obtained from the cursor, which are already in that order.

The cursor walks one column. A composite key's sorted runs serve the engine's own key-ordered
reads, but neither `Keys` nor `OrderBy` takes a tuple: a public cursor or order over a composite
key waits for a `TKey` that is a tuple of members.

### 5.8 The tool path

For a file whose schema is not known when the program is compiled: vxdump, an ad hoc query, a test.
Names are strings because there is no other name. It is the only place in the surface where they
are.

```csharp
public sealed class Scan
{
    public Scan Where(VortexExpr filter);
    public Scan Where([InterpolatedStringHandlerArgument("")] ref FilterHandler filter);   // $"day >= {min} and city = {city}"
    public Scan Rows(RowRange range);
    public Scan Rows(params ReadOnlySpan<long> indices);
    public Scan With(ScanOptions options);
    public Scan WithCancellation(CancellationToken ct);                             // as on Scan<TRecord>
    public AsyncEnumerator GetAsyncEnumerator(CancellationToken ct = default);      // BatchView
    public IAsyncEnumerable<RecordBatch> ToBatchesAsync(CancellationToken ct = default);
    public ValueTask<long> CountAsync(CancellationToken ct = default);
    public ValueTask<bool> AnyAsync(CancellationToken ct = default);
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken ct = default);
    public ScanStatistics Statistics { get; }
}

public readonly ref struct BatchView
{
    public VortexSchema Schema { get; }  public int RowCount { get; }  public long StartRow { get; }  public Selection Selection { get; }
    public Column<T> Column<T>(int index);                                          // VortexSchemaException when T does not fit
    public Column<T> Column<T>(string name);
    public RecordBatch ToOwned();
}

public abstract class VortexExpr
{
    public static VortexExpr Parse(ReadOnlySpan<char> text);                        // the grammar vxdump reads: comparisons, and, or, not, in, is null, like
    public static VortexExpr operator &(VortexExpr left, VortexExpr right);
    public static VortexExpr operator |(VortexExpr left, VortexExpr right);
    public static VortexExpr operator !(VortexExpr operand);
}

[InterpolatedStringHandler]
public ref struct FilterHandler
{
    public FilterHandler(int literalLength, int formattedCount, Scan scan);          // receives the scan: resolves names against its schema
    public void AppendLiteral(string literal);
    public void AppendFormatted<T>(T value);                                        // typed; a hole of the wrong type throws here, not at the scan
}
```

A batch holds the named columns in the file's order, not in the order `Scan(columns)` names them,
because that is the order the projection reads them in: a tool reads them by name. A number in a
filter is the number the text wrote: compared with a decimal column it is scaled exactly, from its
digits and not from the nearest double, and a value the column's scale cannot hold makes an
equality false rather than rounding into a match. Text compared with a date, time, timestamp, uuid
or decimal column is parsed in the column's type, and refused with `VortexSchemaException` when it
does not parse.

`RecordBatch` is the owned batch both paths share:

```csharp
public sealed class RecordBatch : IDisposable
{
    public VortexSchema Schema { get; }  public int RowCount { get; }  public long StartRow { get; }
    public BatchView View { get; }                                                  // throws after Dispose
    public Columns<TRecord> As<TRecord>() where TRecord : IVortexRecord<TRecord>;
}
```

## 6. Writing

The shape of `PipeWriter`: the caller fills builders synchronously, the writer encodes and hands
the bytes to its sink asynchronously, and there is no arena to see.

```csharp
public sealed class VortexFileWriter : IAsyncDisposable
{
    public VortexSchema Schema { get; }
    public long RowCount { get; }                                                   // rows accepted so far; after AppendAsync, where the append resumes
    public int BlockRows { get; }                                                   // VortexWriteOptions.BlockRows
    public long UnflushedBytes { get; }

    public ColumnsBuilder Builder();                                                // reusable; buffers from the session pool
    public ColumnsBuilder<TRecord> Builder<TRecord>() where TRecord : IVortexRecord<TRecord>;   // the same rows as Builder(), seen through the record
    public ValueTask WriteAsync(ColumnsBuilder builder, CancellationToken ct = default);          // takes the builder's rows; the builder is cleared and reusable
    public ValueTask WriteAsync<TRecord>(ReadOnlySpan<TRecord> rows, CancellationToken ct = default) where TRecord : IVortexRecord<TRecord>;   // copies the rows into the builder, then returns the task
    public ValueTask WriteAsync<TRecord>(IAsyncEnumerable<TRecord> rows, CancellationToken ct = default) where TRecord : IVortexRecord<TRecord>;
    public ValueTask WriteAsync<TRecord>(Columns<TRecord> columns, CancellationToken ct = default); // columnar pass-through, for a copy or a rewrite
    public ValueTask WriteAsync(BatchView batch, CancellationToken ct = default);
    public ValueTask WriteAsync(RecordBatch batch, CancellationToken ct = default);
    public ValueTask FlushAsync(CancellationToken ct = default);                    // seals whole blocks into a chunk and writes it
    public ValueTask<WriteReport> CompleteAsync(CancellationToken ct = default);   // the tail block, statistics, zone maps, indexes, footer
    public void Abandon();                                                          // Create: the file is deleted; Append: truncated back to what it was
}

public class ColumnsBuilder
{
    public VortexSchema Schema { get; }
    public int RowCount { get; }                                                    // WriteAsync refuses builders whose columns disagree
    public ColumnBuilder<T> Column<T>(int index);
    public ColumnBuilder<T> Column<T>(string name);
    public ColumnsBuilder Struct(int index);
    public void AppendNull();                                                       // a null row of a nullable nested record
    public void Clear();
}

public sealed class ColumnsBuilder<TRecord> : ColumnsBuilder where TRecord : IVortexRecord<TRecord>
{
    public new ColumnsBuilder<TNested> Struct<TNested>(int index) where TNested : IVortexRecord<TNested>;
}

public readonly struct ColumnBuilder<T> { public int Count { get; } }

public static class ColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b) where T : unmanaged, IBinaryNumber<T>
    {
        public Span<T> GetSpan(int sizeHint = 0);                                  // the IBufferWriter pattern: fill, then Advance
        public void Advance(int count);
        public void Append(T value);
        public void Append(ReadOnlySpan<T> values);
    }
    extension<T>(ColumnBuilder<T?> b) where T : unmanaged, IBinaryNumber<T>
    {
        public Span<T> GetSpan(int sizeHint = 0);  public void Advance(int count);
        public void Append(T value);  public void Append(T? value);  public void Append(ReadOnlySpan<T> values);
        public void Append(ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity);   // bulk, with the bitmap
        public void AppendNull();  public void AppendNulls(int count);
    }
    extension(ColumnBuilder<bool> b)   { public void Append(bool value);  public void Append(ReadOnlySpan<bool> values);  public void AppendBits(ReadOnlySpan<ulong> bits, int count); }
    extension(ColumnBuilder<ReadOnlyMemory<byte>> b) { public void Append(ReadOnlySpan<byte> bytes);  public Span<byte> GetSpan(int sizeHint = 0);  public void Commit(int length); }
    extension(ColumnBuilder<decimal> b)  { public void Append(decimal value);  public void Append(ReadOnlySpan<decimal> values); }
    extension(ColumnBuilder<DateOnly> b) { public void Append(DateOnly value);  public void Append(ReadOnlySpan<DateOnly> values); }
    // TimeOnly, DateTime, DateTimeOffset, Guid, VortexDecimal: the same pair
    extension<T>(ColumnBuilder<ReadOnlyMemory<T>> b)
    {
        public ColumnBuilder<T> Elements { get; }
        public void BeginList();  public void EndList();                            // elements appended between the two belong to one list
        public void Append(ReadOnlySpan<T> list);                                   // one list, in one call
    }
}

public static class TextColumnBuilderExtensions                                    // ColumnBuilder<string> and ColumnBuilder<string?> alike
{
    extension<TText>(ColumnBuilder<TText> b) where TText : IComparable<string?>?
    {
        public void Append(ReadOnlySpan<byte> utf8);                                // no transcoding
        public void Append(ReadOnlySpan<char> text);                                // Utf8.FromUtf16, vectorised
        public void Append<TValue>(TValue value) where TValue : IUtf8SpanFormattable;   // a number, a date, a Guid, formatted in place
        public Span<byte> GetSpan(int sizeHint = 0);  public void Commit(int length);    // one value written in place
        public void AppendNull();  public void AppendNulls(int count);             // to a nullable column
    }
}

public static class ExtensionColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b) where T : IVortexExtension<T> { public void Append(T value);  public void AppendNull(); }
}
```

A `ColumnBuilder<double>` may be asked of a nullable column: it writes it all valid. A
`ColumnBuilder<double?>` asked of a non-nullable column throws `VortexSchemaException`. `GetSpan(n)`
hands back exactly `n` values when `n` is positive, which the `IBufferWriter` contract allows, so
a block can be filled in one span. A list appended in one call that fails part way leaves nothing
behind.

What `WriteAsync` does with rows: they stay in the builder's own buffers until those hold a
chunk, as many whole blocks as keep the widest column within a megabyte of values, or
`ChunkTargetBytes` when it is set, and whole blocks are then encoded from where they lie, without
a copy.
`FlushAsync` seals the whole blocks into a chunk and writes it; it is the only I/O before
`CompleteAsync`, which writes the pending rows as the tail block. So a caller who wants an
append-friendly file writes in multiples of `BlockRows` and it is exact, and a caller who does not
still gets a correct file. The writer counts what it writes on the `vortex.write.bytes` meter and
carries an activity per file, ended when the file completes or is abandoned.

A schema may name an extension registered on the writer's session, outside every edition; a
reader needs the same registration to read it as more than its storage.

## 7. Options, plans, reports, diagnostics, exceptions

```csharp
public sealed record VortexOpenOptions
{
    public int InitialReadSize { get; init; } = 65_536;                             // the tail read at open
    public long? Length { get; init; }                                              // skips the length probe
    public VortexTornTailPolicy TornTail { get; init; } = VortexTornTailPolicy.ReadPrevious;
    public bool VerifyStatistics { get; init; }
    public VortexSchema? Schema { get; init; }                                      // for a file that embeds none
    public long MaxDecompressedSize { get; init; } = 256L * 1024 * 1024;            // per decode; 08 §resource caps
    public ImmutableArray<IndexFragment> IndexFragments { get; init; }
}

public sealed record VortexWriteOptions
{
    public int BlockRows { get; init; } = 8_192;                                    // the unit of pruning, of a take, of a batch
    public int ChunkTargetBytes { get; init; }                                      // 0: whole blocks while the widest column holds a megabyte, 1 to 128 of them; else the bytes of all columns gathered before a chunk is sealed
    public CompressionProfile Compression { get; init; } = CompressionProfile.Auto; // Auto, Fastest, Smallest, None
    public ImmutableDictionary<string, EncodingHint> Hints { get; init; }           // by column path; an unknown path throws at CreateWriter
    public VortexEdition TargetEdition { get; init; } = VortexEditions.Default;     // the edition the most deployed Rust reader accepts, not the newest
    public bool Statistics { get; init; } = true;
    public int StringBoundBytes { get; init; } = 16;                                // text columns prune by default
    public IndexPolicy Indexes { get; init; } = IndexPolicy.None;
    public Guid? Identity { get; init; }                                            // pin for a reproducible write
    public ImmutableDictionary<string, ReadOnlyMemory<byte>> Metadata { get; init; }
}

public sealed record ScanOptions
{
    public int BatchRows { get; init; }                                             // 0: sixteen blocks within a chunk, a block when it filters, orders or takes; never larger
    public bool Pruning { get; init; } = true;
    public bool UseIndexes { get; init; } = true;
    public bool Compact { get; init; } = true;                                      // false: whole blocks plus a Selection
    public int DegreeOfParallelism { get; init; }                                   // 0: the session's
    public int Prefetch { get; init; } = 1;                                         // batches decoded ahead of the consumer
}

public enum CompressionProfile : byte { Auto, Fastest, Smallest, None }
public enum EncodingHint : byte { Auto, Canonical, RunEnd, Dictionary, BitPacked, Fsst, Alp, Sequence, Zstd }
public enum VortexTornTailPolicy : byte { ReadPrevious, Refuse }

public sealed class IndexPolicy
{
    public static IndexPolicy None { get; }
    public static IndexPolicy Auto { get; }
    public IndexPolicy Bloom(string column, double falsePositiveRate = 0.01, bool required = false);
    public IndexPolicy NgramBloom(string column, int n = 3, bool required = false);
    public IndexPolicy Postings(string column, bool required = false);
    public IndexPolicy SortedRuns(string column, bool required = false);
    public IndexPolicy ForKey(ReadOnlySpan<string> columns, IndexKind kind, bool required = false);
    public IndexPolicy ForKey(ReadOnlySpan<string> columns, IndexKind kind, IKeyEncoder encoder, bool required = false);
    public IndexPolicy WithBudgetPerMille(int perMille);                            // 100 by default
}

public interface IKeyEncoder                                                        // what a composite key's tuples become; RowKeyEncoder is one (§12.3)
{
    string Format { get; }                                                          // recorded in the index directory: keys compare only within one format
    IEncodedKeys Encode(BatchView columns);                                         // the key columns, in key order
}

public interface IEncodedKeys : IDisposable { int RowCount { get; }  ReadOnlySpan<byte> Row(int index); }
```

The core ships no key encoder, and cannot find one without reflection: a composite key names its
encoder in `ForKey`. A key declared without one is abandoned, or fails the write when it is
required, and says why. The composite keys of one file share one encoder; `ForKey` refuses a
second one of another `Format` when the policy is built.

`required: true` means what it says: an index the writer cannot build within the budget makes
`CompleteAsync` throw `VortexException` naming the index and the reason, before a byte of the tail
is written, and the file is not completed. An index that is not required and is abandoned leaves
**no bytes** in the file: the budget and `Auto`'s share drop only an index that has written
nothing, so an index whose payloads are already in the file is kept even past the budget. A
required index is over budget when the required indexes alone exceed it once every other index
has given way, which is judged once the data passes 1 MiB.

```csharp
public sealed record ScanPlan(long Rows, int Blocks, int LiveBlocks, int Segments, long BytesToRead, bool MayMatch,
                              ImmutableArray<PruningStep> Pruning, CountPlan Count, OrderPlan? Order);
public sealed record PruningStep(string Structure, int BlocksPruned, int SegmentsRead, long BytesRead);
public sealed record CountPlan(bool Exact, long Rows, int Pruned, int Proven, int Decoded);
public sealed record OrderPlan(string Source, int Runs, long? Entries, bool Descending);
public readonly record struct ScanStatistics(long Rows, long Batches, long Requests, long BytesRequested, long BlocksDecoded, long BlocksPruned, long CacheHits);

public sealed record WriteReport(long RowCount, int BlockRows, ImmutableArray<int> ChunkRows, WriteBytes Bytes,
                                 ImmutableArray<ColumnWriteReport> Columns, ImmutableArray<IndexWriteReport> Indexes);
public readonly record struct WriteBytes(long Total, long Data, long Statistics, long ZoneMaps, long Indexes, long Footer);
public sealed record ColumnWriteReport(string Path, ImmutableArray<string> Encodings);
public sealed record IndexWriteReport(string Column, string Kind, IndexOutcome Outcome, string? Reason, long Bytes);

public sealed class VortexFileStatistics { public FieldStatistics this[int index] { get; }  public int Count { get; } }
public readonly struct FieldStatistics
{
    public bool TryGetMin<T>(out T value);  public bool TryGetMax<T>(out T value);  // exact bounds only: a truncated text bound answers false
    public bool TryGetSum<T>(out T value);                                          // at the widened type the file stores: long, ulong, double, decimal
    public bool TryGetNullCount(out long count);  public bool TryGetIsSorted(out bool sorted);  public bool TryGetIsConstant(out bool constant);
}
public sealed class VortexMetadata
{
    public ImmutableArray<string> Keys { get; }
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string key, CancellationToken ct = default);
}

public static class VortexDiagnostics
{
    public const string MeterName = "Vorticity";                                  // vortex.scan.rows, vortex.scan.requests, vortex.scan.bytes_requested,
                                                                                    // vortex.scan.blocks_decoded, vortex.scan.blocks_pruned, vortex.cache.hits,
                                                                                    // vortex.cache.misses, vortex.write.bytes; no tags by default
    public const string ActivitySourceName = "Vorticity";                         // vortex.scan.typed, vortex.scan.tool, vortex.write; tagged with what they did (09 §5)
}

public class VortexException : Exception;
public sealed class VortexFormatException : VortexException;                        // the bytes are not what they claim, or a limit says no
public sealed class VortexUnsupportedException : VortexException { public ComponentKind Kind { get; }  public string ComponentId { get; } }
public sealed class VortexSchemaException : VortexException;                        // a record, a builder or a literal does not fit the schema
public enum ComponentKind : byte { Array, Layout, DType, Aggregate, Compression, Encryption, Index, Feature }
```

`VortexUnsupportedException.Kind` says which table the id belongs to, and so which edition
introduced it: an array or layout encoding, a dtype, an aggregate, a compression or encryption
scheme, an index kind, or a feature of the library (an append the file refuses, a comparison no
kernel implements). `VortexEditions.Default` is the first edition that carries `vortex.uuid`, so a
`Guid` member writes by default; it equals `Newest` today and will not stay equal.

Metrics cost nothing without a listener; an `Activity` is created only when the source has one.
`ScanStatistics` is a per-scan snapshot, read after the fact, for the caller who wants the numbers
of one query rather than of the process.

The plan and the statistics count in the same units. `ScanPlan.Rows`, `Blocks` and `LiveBlocks`
count what the scan's rows touch: a range counts its blocks, and a take the rows it asks for and
the blocks that hold them. `Segments` and `BytesToRead` are what the source will be asked for,
each segment once; `ScanStatistics.Requests` and `BytesRequested` count what it was asked for,
where the request is made, so over a file with statistics the two agree. A filter the file
statistics refute reads nothing: the plan's first step, `file statistics`, prunes every block.
`BlocksPruned` counts the blocks the structures skipped. `BlocksDecoded` counts every block an enumeration delivered,
whatever encoding it keeps, and for an aggregate only the blocks one of whose columns reached the
canonical form: a block answered from a dictionary's codes or a run's lengths is not decoded.

Kept from today, narrowed: `RowRange`, `SeekOp`, `KeyPlan`, `VortexEdition`, `VortexEditions`
(`Default`, `Newest`, `ReadFloor`, `Contains`, `IntroducedIn`, `MinimumRustVersion`, and `Name`,
which spells an edition as the registry does, `core2026.08.3`), `VortexLimits`,
`VortexTornTail`, `VortexFileRepair` (`ValidLengthAsync`, `RepairAsync`), `VortexRepairResult`,
`VortexFileIndexer` (`AppendIndexesAsync`, `BuildFragmentAsync`), `IndexFragment`, `VortexIndexInfo`,
`VortexIndexVerification`, `IndexOutcome`, `IndexKind`, `ComponentKind`, `VortexDecimal`.

## 8. The I/O seam

```csharp
namespace Vorticity.IO;

public interface ISegmentSource : IAsyncDisposable
{
    long Length { get; }
    ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken ct);
    ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken ct);   // one preadv, or one coalesced request
}

public readonly record struct SegmentRange(long Offset, int Length);

public struct SegmentLease : IDisposable
{
    public SegmentLease(ReadOnlyMemory<byte> memory, IDisposable? owner = null);   // a source's own implementation builds its leases with these
    public SegmentLease(ReadOnlySequence<byte> bytes, IDisposable? owner = null);  // Dispose disposes the owner
    public ReadOnlySequence<byte> Bytes { get; }                                    // a single segment when the source can; several when a response arrives in pieces
    public bool IsContiguous { get; }
    public ReadOnlyMemory<byte> Memory { get; }                                     // when IsContiguous
}

public sealed class FileSegmentSource : ISegmentSource          // File.OpenHandle, RandomAccess; FileOptions from the plan
public sealed class MemoryMappedSegmentSource : ISegmentSource  // a view; leases are pointers into it
public sealed class MemorySegmentSource : ISegmentSource        // bytes the caller holds; never copied
```

The sink of a writer is a `PipeWriter`. `CreateWriter(string path)` builds one over
`File.OpenHandle`, without preallocation, since the size is known only when the file completes; `PipeWriter.Create(stream)` covers a
`Stream`; an object store provides its own, and gets backpressure from `FlushAsync` for free.

What the scan promises its source: **a segment is requested at most once per scan.** The plan
lists the segments the live blocks need, the source reads them in as few requests as the
coalescing allows, and every block of a segment decodes from the one lease. The session cache adds
reuse across scans; it is not what makes a single scan read each byte once.

## 9. Read cases

Every case is a program in `samples/`, and the guide's pages are these cases with their measured
figures. The record types used below:

```csharp
[VortexRecord] public partial record struct Reading(int Day, double? Celsius, string City);
[VortexRecord] public partial record struct Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin);
[VortexRecord] public partial record struct Address(string Country, string? City);
```

### 9.1 Open, and take the mean of one column

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);

double total = 0;
long seen = 0;
await foreach (var (_, celsius, _) in file.Scan<Reading>())
{
    foreach (double value in celsius.Values) total += value;   // wrong if the column has nulls: see 9.4
    seen += celsius.Length;
}
```

Better, because it never materialises a batch and answers from the statistics when they suffice:

```csharp
double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);
```

Cost: the open is one read of the tail. The loop reads each segment once, decodes each block once,
allocates nothing per batch. The `AvgAsync` form reads the `sum` and `null_count` statistics when
the file carries them, and nothing else.

### 9.2 Three columns of fifty

```csharp
[VortexRecord] public partial record struct DayAndCelsius(int Day, double? Celsius);

await foreach (var (day, celsius) in file.Scan<DayAndCelsius>()) { }
```

The record is the projection. Only the segments of the two columns are read; the other forty-eight
are branches not walked. A nested field is reached by a nested record, and the projection is over
leaves: `Address` with only `Country` reads that field's segments and not its sibling's.

### 9.3 Filters

```csharp
Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900 && r.City == "Paris");

if (maxCelsius is double max)
{
    scan.Where(r => r.Celsius <= max);                   // an optional filter is another Where, joined by &
}

await foreach (var (day, celsius, city) in scan) { }
```

What happens, in order, per [08-semantics.md](08-semantics.md): the file statistics may settle it
without a read; the zone maps of `Day` prune whole blocks; the string bounds of `City`, written by
default since `StringBoundBytes` is 16, prune more; an index, if the writer built one, prunes more;
the kernel decides the rest per value, on the encoded form where the encoding allows it, and the
batch is compacted to the survivors.

Text and nullity:

```csharp
file.Scan<Visit>().Where(v => v.Referrer != null && v.Referrer.StartsWith("https://") && v.Origin.Country.In("FR", "BE"))
```

Column to column, and the two forms of the same question:

```csharp
file.Scan<Trade>().Where(t => t.High > t.Low)
file.Scan<Reading>().Where(r => r.Celsius.Between(10.0, 20.0))
file.Scan<Reading>().Where(r => r.Celsius >= 10.0 && r.Celsius <= 20.0)
```

What does not compile, and is meant not to: `r.Day >= "900"`, `r.Day % 2 == 0`,
`Math.Abs(r.Celsius) > 1`, `r.City.Length > 3`. Each is either not a pushdown or a literal of the
wrong type, and the compiler says so with the message of §5.2.

### 9.4 Nullable columns

`Values` is the raw buffer. The bitmap says which slots mean anything. Two idioms, by selectivity
of nulls:

```csharp
await foreach (var (_, celsius, _) in file.Scan<Reading>())
{
    ReadOnlySpan<double> values = celsius.Values;
    ReadOnlySpan<ulong> valid = celsius.ValidityWords;
    if (valid.IsEmpty)                                    // all valid: the SIMD path, no mask
    {
        total += Sum(values);
        continue;
    }

    for (int w = 0; w < valid.Length; w++)                // word by word: 64 values per branch
    {
        ulong word = valid[w];
        int start = w << 6;
        int width = Math.Min(64, values.Length - start);
        if (word == ulong.MaxValue) { total += Sum(values.Slice(start, width)); continue; }
        while (word != 0)
        {
            int bit = BitOperations.TrailingZeroCount(word);
            total += values[start + bit];
            word &= word - 1;
        }
    }
}

static double Sum(ReadOnlySpan<double> values)                // System.Numerics.Vector<T>, in the shared framework
{
    Vector<double> acc = Vector<double>.Zero;
    int i = 0;
    for (; i <= values.Length - Vector<double>.Count; i += Vector<double>.Count) acc += new Vector<double>(values.Slice(i));
    double sum = Vector.Sum(acc);
    for (; i < values.Length; i++) sum += values[i];
    return sum;
}
```

Or nothing at all, because the engine does exactly this, on the encoded form, in parallel:

```csharp
double sum = await file.Scan<Reading>().SumAsync(r => r.Celsius);
```

The null count of a batch is `celsius.NullCount`, which the engine has from the validity words by
`BitOperations.PopCount`, sixty-four values per instruction, not from a loop over the bits.

### 9.5 Text columns without a string

```csharp
SearchValues<byte> vowels = SearchValues.Create("aeiouy"u8);
long withVowel = 0;

await foreach (var (_, _, city) in file.Scan<Reading>())
{
    for (int i = 0; i < city.Length; i++)
    {
        if (city[i].ContainsAny(vowels)) withVowel++;   // ReadOnlySpan<byte>, UTF-8, no allocation
    }
}
```

`city.GetString(i)` allocates and is there when a `string` is what the caller needs. A distinct
count of a text column is not a loop over its values:

```csharp
long cities = await file.Scan<Reading>().CountDistinctAsync(r => r.City);
```

On a dictionary-encoded column that is the dictionary's cardinality per chunk, merged by value; on
an FSST column it is a decode and a hash per value. The write report says which the column got.

### 9.6 Reading the encoded form

```csharp
double[] totalByCity = new double[64];                                  // a span would live across the awaits of the loop

await foreach (var (_, celsius, city) in file.Scan<Reading>())
{
    if (city.Encoding == ColumnEncoding.Dictionary)
    {
        DictionaryView<string> dict = city.AsDictionary();          // no decode of eight thousand strings
        ReadOnlySpan<uint> codes = dict.Codes;
        ReadOnlySpan<double> values = celsius.Values;
        for (int i = 0; i < codes.Length; i++)
        {
            totalByCity[(int)codes[i]] += values[i];                 // group by code: one add per row, no hash
        }
        // dict.Values[c] names group c for this chunk
    }
    else
    {
        // the canonical path: hash by city[i]
    }
}
```

A run-end column, the usual shape of `Day`:

```csharp
RunEndView<int> runs = day.AsRunEnd();
for (int r = 0, start = 0; r < runs.RunCount; r++)
{
    int end = (int)runs.RunEnds[r];
    // rows [start, end) all have day == runs.Values[r]
    start = end;
}
```

And the reason not to write any of it: `GroupBy(r => r.City).AggAsync(g => (g.Key, g.Sum(r => r.Celsius)))`
does the dictionary case by code, the run-end case by run, and the canonical case by hash, and merges
the per-chunk dictionaries by value so the answer is one row per city, not one per chunk.

### 9.7 A selection instead of a compaction

```csharp
Scan<Reading> scan = file.Scan<Reading>()
    .Where(r => r.Celsius > 20.0)
    .With(new ScanOptions { Compact = false });

await foreach (var cols in scan)
{
    ReadOnlySpan<double> values = cols.Celsius.Values;                // the whole block
    foreach (int i in cols.Selection)                                 // the rows that passed
    {
        total += values[i];
    }
}
```

A filter that keeps half of every block copies nothing this way, and a consumer that wants the
compact form leaves `Compact` alone and gets it.

### 9.8 Aggregates

Several answers in one pass:

```csharp
(double? min, double? max, long n, long cities) = await file.Scan<Reading>()
    .Where(r => r.Day >= 900)
    .AggAsync(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.Count(), a.CountDistinct(r => r.City)));
```

Group by, with a composite key and a custom aggregator:

```csharp
await foreach (var (city, day, total, variance) in file.Scan<Reading>()
    .GroupBy(r => (r.City, r.Day))
    .AggAsync(g => (g.Key.Item1, g.Key.Item2, g.Sum(r => r.Celsius), g.Aggregate<double, Welford, Welford.State>(r => r.Celsius))))
{
}

public readonly struct Welford : IEncodedAggregator<double, Welford.State>
{
    public struct State { public long Count; public double Mean; public double M2; public double Variance => Count > 1 ? M2 / (Count - 1) : double.NaN; }
    public static State Seed() => default;
    public static void Step(ref State s, ReadOnlySpan<double> values, ReadOnlySpan<ulong> validity, Selection rows) { /* the canonical loop */ }
    public static void StepDictionary(ref State s, ReadOnlySpan<uint> codes, ReadOnlySpan<double> dictionary, Selection rows) { /* count codes, then one step per distinct value with its weight */ }
    public static void StepRunEnd(ref State s, ReadOnlySpan<uint> runEnds, ReadOnlySpan<double> values, Selection rows) { /* one weighted step per run */ }
    public static void StepConstant(ref State s, double value, int count) { /* one weighted step */ }
    public static void Merge(ref State into, in State other) { /* Chan's formula */ }
}
```

Cost: one state per group per chunk, merged at the end; no batch materialised; with
`MaxDegreeOfParallelism` above 1, chunks aggregate in parallel and `Merge` joins them. The session
of §2, on ten million rows, is where the parallelism the guide could not demonstrate on a scan
becomes linear.

### 9.9 Key order and the cursor

```csharp
await foreach (var (day, celsius, _) in file.Scan<Reading>().OrderBy(r => r.Day, descending: true)) { }   // batches in key order, 12 §6

await using KeyCursor<int> cursor = await file.Scan<Reading>().Keys(r => r.Day).OpenAsync();
if (await cursor.SeekAsync(900, SeekOp.AtOrAfter))
{
    long row = cursor.Row;                                             // then Rows(row) on a scan, if the row is wanted
}
long rank = await cursor.RankAsync(900);
```

The cursor walks keys, not rows. On a column the statistics say is sorted it costs a binary search
over zone maps and one block decode per seek; on a `SortedRuns` index it costs the runs; on a column
with neither, `OpenAsync` throws `VortexUnsupportedException` naming what the writer would have to
build.

### 9.10 Rows, when rows are wanted

```csharp
await foreach (Reading r in file.Scan<Reading>().Where(r => r.Day == 900).ToRecordsAsync(ct))
{
    Console.WriteLine($"{r.City}: {r.Celsius}");
}

List<Reading> hottest = await file.Scan<Reading>()
    .ToRecordsAsync(ct)
    .OrderByDescending(r => r.Celsius)                                // System.Linq.AsyncEnumerable: client side, after the sink
    .Take(10)
    .ToListAsync(ct);
```

Cost: a field copy per row, an allocation per row for `City`. The second example reads the whole
file to sort ten rows on the client, and the code shows it: nothing after `ToRecordsAsync` is
pushed. The pushed form of the same question is `MaxAsync` or an `OrderBy` on a sorted column.

### 9.11 Rows by position

```csharp
await foreach (var cols in file.Scan<Reading>().Rows(4, 900_000)) { }   // two blocks, decoded; nothing between them read
await foreach (var cols in file.Scan<Reading>().Rows(RowRange.FromLength(1_000, 10))) { }
```

A take is priced in blocks. `StartRow` on the delivered columns is the block's first row; the rows
asked for are located by `StartRow` and the selection, which is what `Selection` reports on a take.

### 9.12 Local, remote, parallel

```csharp
await using VortexFile local = await session.OpenAsync(path);          // memory-mapped
await foreach (var cols in local.Scan<Reading>()) { }                  // a mapped segment completes synchronously: the await costs no suspension

await using VortexFile remote = await session.OpenAsync(new BlobSegmentSource(client, key));   // the caller's ISegmentSource
await foreach (var cols in remote.Scan<Reading>()) { }                 // the I/O awaits; the borrow stays synchronous inside the body
```

One shape for every source: there is no synchronous enumeration to choose, and a source whose
reads complete synchronously pays for a completed `ValueTask`, not for a state machine that
suspends. The rule inside the body is the one the compiler enforces: a `Column<T>` does not live
across an `await`. Do the columnar work, then await what you must, then `MoveNextAsync`.

Parallelism is the session's, and it pays on aggregates and on wide decodes, not on a scan whose
cost is walking splits. A split is the row range of one batch: a live block when the scan filters,
orders or takes, up to sixteen blocks within a chunk when it only reads, and smaller when
`BatchRows` cuts it; `ExplainAsync` names no splits, and its `LiveBlocks` is the count to read: a
scan with two live blocks has nothing to gain from eight threads.

### 9.13 Pipelines with owned batches

```csharp
Channel<RecordBatch> channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(4) { SingleWriter = true, SingleReader = true });

Task producer = Task.Run(async () =>
{
    await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(ct)) await channel.Writer.WriteAsync(batch, ct);
    channel.Writer.Complete();
});

await foreach (RecordBatch batch in channel.Reader.ReadAllAsync(ct))
{
    using (batch)
    {
        Columns<Reading> cols = batch.As<Reading>();
        // ...
    }
}
```

`ToBatchesAsync` copies each batch once into buffers from the session's pool, which the consumer
owns and disposes; `ToOwned()` on a borrowed `Columns<TRecord>` is the same copy, for the case
where a loop wants to keep one batch of a thousand. The decoded buffers are not handed over
instead, because they live in the lane's arenas beside the chunks retained across batches: a
batch that took them would take the chunk cache with it, and the next batch of the chunk would
decode it again. The copy costs about a third of a hot, in-memory scan that reads every value.

### 9.14 Nested records and lists

```csharp
await foreach (var v in file.Scan<Visit>())
{
    Columns<Address> origin = v.Origin;                                 // a struct column is a nested Columns<T>
    Column<string?> originCity = origin.City;

    Column<ReadOnlyMemory<int>> pages = v.Pages;
    ReadOnlySpan<int> allPages = pages.Elements.Values;                 // every list's elements, contiguous
    for (int i = 0; i < pages.Length; i++)
    {
        ReadOnlySpan<int> list = allPages[pages[i]];                   // Range into the elements
    }
}
```

### 9.15 A file whose schema is not known at compile time

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
Console.WriteLine($"{file.Schema}");                                   // struct{day: i32, celsius: f64?, city: utf8}

int threshold = 900;
await foreach (BatchView batch in file.Scan("day", "celsius").Where($"day >= {threshold}"))
{
    Column<int> day = batch.Column<int>("day");                        // by name: a batch holds its columns in the file's order
    Column<double?> celsius = batch.Column<double?>("celsius");
}
```

`{threshold}` reaches the handler as an `int`; a `string` in that hole throws at `Where`, naming the
column and both types. This is the surface vxdump is written on.

### 9.16 The plan before, the numbers after

```csharp
ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync();
Console.WriteLine($"{plan.LiveBlocks} of {plan.Blocks} blocks, {plan.Segments} segments, {plan.BytesToRead} bytes");

Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
await foreach (var cols in scan) { }
ScanStatistics stats = scan.Statistics;                                // Requests, BytesRequested, BlocksDecoded, BlocksPruned
```

`BytesToRead` and `BytesRequested` agree, to the coalescing gap: the plan is what the execution
does. That equality is a test (§11).

### 9.17 A torn tail

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
if (file.TornTail is { } torn)
{
    Console.WriteLine($"reading the version before the torn append: {file.RowCount} rows, valid to {torn.ValidLength} of {torn.FileLength}");
}

VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path);   // truncates to the last version that parses
```

`VortexTornTailPolicy.Refuse` at open throws `VortexFormatException` instead. A file with no
complete version throws on open either way.

### 9.18 Input you do not trust

```csharp
await using VortexFile file = await session.OpenAsync(path, new VortexOpenOptions
{
    MaxDecompressedSize = 64L * 1024 * 1024,                           // per decode, and a decode is a block
    VerifyStatistics = true,                                           // a statistic is a claim; check it against what is decoded
});
```

A component this build does not implement is refused where a block needs it, never at open: a
footer may list encodings no chunk uses, and which ones a chunk uses is known only once its bytes
are read.

The fixed caps of `VortexLimits` apply always. `MaxDecompressedSize` bounds one block's decode, so
`BlockRows` times the widest row of an honest file is the right ceiling, and it is small.

## 10. Write cases

### 10.1 Columns in, one file out

```csharp
await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
ColumnsBuilder<Reading> b = writer.Builder<Reading>();

for (int block = 0; block < blocks; block++)
{
    Span<int> day = b.Day.GetSpan(writer.BlockRows);                   // the IBufferWriter pattern: fill in place
    for (int i = 0; i < day.Length; i++) day[i] = (block * writer.BlockRows + i) / 1_000;
    b.Day.Advance(day.Length);

    b.Celsius.Append(temperatures.Slice(block * writer.BlockRows, writer.BlockRows), validity.Slice(block * 128, 128));
    for (int i = 0; i < writer.BlockRows; i++) b.City.Append(cities[i % cities.Length]);    // ReadOnlySpan<byte>, no transcoding

    await writer.WriteAsync(b, ct);                                    // takes the rows; b is cleared and reusable
    if (writer.UnflushedBytes > 8 << 20) await writer.FlushAsync(ct);
}

WriteReport report = await writer.CompleteAsync(ct);
```

Cost: `GetSpan` hands the caller the buffer the encoder will read; the primitive columns are encoded
from the caller's bytes without a copy. The span is filled and committed before the `await`, which
is what lets it live in an async method. Encoding prices each column on the thread that writes,
and its cost is the encode, not the API. `FlushAsync` is the only I/O before completion.

### 10.2 Rows in

```csharp
await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
await writer.WriteAsync<Reading>(readings.AsSpan(), ct);               // generated WriteRows: field to builder, one column at a time
await writer.CompleteAsync(ct);

await using VortexFileWriter streaming = session.CreateWriter<Reading>(path);
await streaming.WriteAsync(readingsAsync, ct);                         // batches of BlockRows, flushes as it goes
await streaming.CompleteAsync(ct);
```

Cost: a field copy per row; the `string` member is transcoded once per row. `WriteAsync` over an
`IAsyncEnumerable` groups rows by `BlockRows` so the file is append-friendly without the caller
counting.

### 10.3 Text: formatted in place, and long values

```csharp
b.City.Append("Paris"u8);                                              // the fast path: UTF-8 bytes
b.City.Append(cityString);                                             // Utf8.FromUtf16, vectorised
b.StartedAt.Append(timestamp);                                         // a DateTime member: its own column
b.Note.Append(DateOnly.FromDateTime(now));                             // an IUtf8SpanFormattable, formatted straight into a text column

Span<byte> dst = b.Note.GetSpan(4096);                                 // a value written in place
int written = Encoding.UTF8.GetBytes(longText, dst);
b.Note.Commit(written);
```

A value of twelve bytes or fewer lives in its view; a longer one goes to the data buffer with a
prefix in the view. The builder does both; the caller never sees a view.

### 10.4 Nulls

```csharp
b.Celsius.Append(21.5);
b.Celsius.AppendNull();
b.Celsius.Append(values, validity);                                    // bulk: the values and a bitmap, one bit per value
b.Referrer.AppendNull();
```

A nullable column with no null written costs nothing: the validity is `AllValid` until the first
`AppendNull`, and the bitmap is allocated then.

### 10.5 Lists and nested records

```csharp
ColumnsBuilder<Visit> v = writer.Builder<Visit>();
v.Pages.Append([1, 4, 9]);                                             // one list, one call
v.Pages.BeginList(); v.Pages.Elements.Append(pagesSpan); v.Pages.EndList();   // or element by element
ColumnsBuilder<Address> origin = v.Struct<Address>(5);
origin.Country.Append("FR"u8);
origin.City.AppendNull();
```

### 10.6 Blocks, chunks, and what makes a file append-friendly

```csharp
int rows = writer.BlockRows;                                           // 8 192 unless VortexWriteOptions.BlockRows says otherwise
```

`WriteAsync` accepts any number of rows and keeps a partial block pending. `FlushAsync` seals whole
blocks into a chunk and writes it; a chunk is the append seam. `CompleteAsync` writes the pending
rows as the tail block. Therefore: a file whose every `WriteAsync` is a multiple of `BlockRows` appends
by rewriting nothing; a file that is not appends by rewriting its tail chunk, which
`writer.RowCount` after `AppendAsync` reports as the row it resumes from. Both are correct files.

### 10.7 Append, abandon, torn

```csharp
await using VortexFileWriter appender = await session.AppendAsync(path);
long resumeAt = appender.RowCount;                                     // the file's row count when the tail chunk ended on a block; earlier otherwise
await appender.WriteAsync<Reading>(more.AsSpan(), ct);
await appender.CompleteAsync(ct);
```

An append never truncates while it writes: the old tail stays, which is what a reader falls back
to when the append is torn. `Abandon()` on an append truncates the file back to the length it had,
so it is as it was; on a `CreateWriter` it deletes the partial file. A writer disposed without
`CompleteAsync` behaves as `Abandon()`, and a caller's `PipeWriter` is then completed with an
error, so that whatever it feeds knows the bytes are not a file.

### 10.8 Choosing what the writer does

```csharp
VortexWriteOptions options = new()
{
    Compression = CompressionProfile.Smallest,                         // size over decode speed; Auto prices both
    Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(Reading.ColumnNames.Celsius, EncodingHint.Alp),
    TargetEdition = VortexEdition.Core20250500,                        // readable by every Rust reader since 0.36
    StringBoundBytes = 32,
    Indexes = IndexPolicy.None.Bloom(Visit.ColumnNames.Id, required: true).WithBudgetPerMille(300),
    Metadata = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("producer", "acme/1.4"u8.ToArray()),
    Identity = Guid.Parse("…"),                                        // a reproducible write: the same input gives the same bytes
};

WriteReport report = await writer.CompleteAsync(ct);
foreach (ColumnWriteReport column in report.Columns) Console.WriteLine($"{column.Path}: {string.Join(", ", column.Encodings)}");
foreach (IndexWriteReport index in report.Indexes) Console.WriteLine($"{index.Column} {index.Kind}: {index.Outcome} {index.Reason}");
```

An unknown path in `Hints` or in `Indexes` throws at `CreateWriter`. A `required` index that
cannot be built throws at `CompleteAsync`, and the file is not completed. The report is where the
writer says what it chose, per chunk, and why it declined what it declined.

### 10.9 Streaming to an object

```csharp
await using VortexFileWriter writer = session.CreateWriter(store.OpenPut(key), Reading.Schema);   // any PipeWriter
```

`FlushAsync` returns when the sink has accepted the bytes, so a sink that applies backpressure
slows the producer rather than growing a buffer. A multipart upload is a `PipeWriter` that flushes
a part per threshold; nothing in the writer knows.

### 10.10 Copy, rewrite, re-encode

```csharp
await using VortexFile source = await session.OpenAsync(input);
await using VortexFileWriter target = session.CreateWriter(output, source.Schema, new VortexWriteOptions { Compression = CompressionProfile.Smallest });

await foreach (BatchView batch in source.Scan()) await target.WriteAsync(batch, ct);   // columnar pass-through; the columns decode once and encode once
await target.CompleteAsync(ct);
```

The same loop with a `Where` is a filtered copy, and with a record type a projected one.

### 10.11 A writer without a record type

```csharp
VortexSchema schema = [("id", VortexType.Int64), ("payload", VortexType.Binary.Nullable)];
await using VortexFileWriter writer = session.CreateWriter(path, schema);
ColumnsBuilder b = writer.Builder();
b.Column<long>(0).Append(ids);
b.Column<ReadOnlyMemory<byte>?>(1).AppendNull();
await writer.WriteAsync(b, ct);
```

The tool path of the writer, with the same builders under string names.

## 11. The performance contract, and the gates that hold it

Each promise is a test in `Vorticity.Tests`, category `ApiContract`, and the build fails when
one stops holding.

| promise | gate |
|---|---|
| zero allocation per batch, on every sink but rows | `GC.GetAllocatedBytesForCurrentThread()` is unchanged across a full scan after one warm-up batch, on the built-in sources; a caller's `ISegmentSource` still costs one small object per segment read, in the adapter that reads it |
| nothing on the large object heap in steady state | the same measurement with `GC.CollectionCount(2)` unchanged |
| `Values` is contiguous and 64-byte aligned | an assertion on the address of every `Values` span of the conformance corpus |
| a segment is requested at most once per scan | `ScanStatistics.Requests` equals the plan's distinct segments, over a counting source |
| the plan is the execution | `BytesRequested` equals `BytesToRead`, on every query of the corpus; a source's coalescing lies below both |
| a pruned block is not decoded | `BlocksDecoded` equals `LiveBlocks` |
| a question the statistics answer reads nothing | `Requests` is 0 after open for `CountAsync`, `MinAsync`, `MaxAsync` on a file with statistics |
| a dictionary or run-end aggregate does not decode | `BlocksDecoded` is 0 for `GroupBy` on a dictionary column and `SumAsync` on a run-end column |
| a chunk is decoded once per scan, whatever runs it | `ScanMetrics.ValuesDecoded` equals rows × columns at a prefetch of 0 to 3 and a degree of 4, and for a parallel aggregation; the decoded chunks are one table per scan, shared by its lanes and by the key source of a key-ordered walk |
| memory per scan is bounded by the prefetch | peak pool size across a scan is at most `(Prefetch + 1)` batches of the widest block, plus one decoded chunk per column. Not yet a test: the pool counts no bytes in use. What it keeps in reserve, the only proxy, read 1.32, 2.07 and 2.73 widest splits at a prefetch of 0, 1 and 2 before the decoded chunks were shared between lanes, one chunk per lane; a retained chunk is now evicted once the delivered batch did not borrow it, which no later batch then does. A peak counter on the pool would make the gate testable |
| no virtual call per value | the typed loops of §9 within 5 % of hand-written loops over raw spans, measured in `bench/` |
| what cannot be pushed down does not compile | a project under `tests/MustNotCompile` builds with exactly the expected diagnostics, our `[Obsolete]` messages included |
| the surface is frozen | `PublicAPI.Shipped.txt` matches, `EnablePackageValidation` passes against the baseline |
| vxdump uses the public surface only | vxdump compiles without `InternalsVisibleTo` |

Analyzers shipped in `Vorticity.Generators`:

| id | flags |
|---|---|
| VX1001 | a span borrowed from a `Column<T>` assigned to a variable declared outside the enumeration body |
| VX1002 | a `RecordBatch` from `ToBatchesAsync` or `ToOwned` that is never disposed |
| VX1003 | a hole of an interpolated `Where` whose type maps to no column type; the column it meets is known only once the file is open, so a hole of a mappable type that does not fit its column throws when the filter is built |
| VX1004 | `Rows` by range and by indices on the same scan |
| VX1005 | a `[VortexRecord]` member whose type has no mapping under §3.1 |
| VX1006 | a `[VortexRecord]` type, or a type containing it, that is not `partial` |
| VX1007 | a `[VortexRecord]` type that cannot be a record: generic, `ref struct`, static, abstract, file-local, or without a usable constructor |
| VX1008 | a member the reader cannot fill: a get-only property with no constructor parameter, a `readonly` field, an inaccessible setter, a `required` member marked `[VortexIgnore]` |

VX1001 to VX1004 are warnings; VX1005 to VX1008 are errors, and the generator emits nothing for a
type that has one.

## 12. Satellites

### 12.1 `Vorticity.Generators`

The generator emits what §4 lists. It runs on `partial` types only, reports VX1005 on an
unmappable member, and emits nothing else. The analyzers of §11 ship in the same package.

### 12.2 `Vorticity.Dataset`, experimental

`VortexDataset.Scan<TRecord>()` returns the same `Scan<TRecord>` as a file, over every object of the
version, and `Scan(params ReadOnlySpan<string>)` the same tool scan. A scan is pinned to the
version its handle read when it was built, so a refresh or a commit on the handle does not move it.
A batch's `StartRow` is a position in the dataset, and so are `Rows(…)` and a key cursor's `Row`.
Counts and extremes answer from object summaries first, `ExplainAsync` sums the objects' plans with
object pruning as its first `PruningStep`, and a key cursor on the clustering key is bounded by the
tree; its backward steps are not offered.

```csharp
public sealed class VortexDataset : IAsyncDisposable
{
    public static ValueTask<VortexDataset> CreateAsync(IObjectStore store, VortexSchema schema, DatasetOptions? options = null, CancellationToken ct = default);
    public static ValueTask<VortexDataset> OpenAsync(IObjectStore store, DatasetOptions? options = null, CancellationToken ct = default);
    public ValueTask<ulong> RefreshAsync(CancellationToken ct = default);
    public ulong Version { get; }  public VortexSchema Schema { get; }  public long RowCount { get; }  public long ObjectCount { get; }  public long Lag { get; }

    public Scan<TRecord> Scan<TRecord>() where TRecord : IVortexRecord<TRecord>;
    public Scan Scan(params ReadOnlySpan<string> columns);
    public bool MayMatch<TRecord>(Func<Probe<TRecord>, Predicate> predicate) where TRecord : IVortexRecord<TRecord>;

    public ObjectDraft StartObject();                                               // a writer whose file becomes one object
    public ValueTask<ulong> AppendAsync(ObjectDraft draft, CancellationToken ct = default);   // completes it if the caller did not
    public ValueTask<ulong> AppendAsync(IAsyncEnumerable<RecordBatch> batches, CancellationToken ct = default);
    public ValueTask<ulong> ImportAsync(string objectKey, CancellationToken ct = default);
    public ValueTask<ReplaceResult> RemoveAsync(IReadOnlyList<DataObject> objects, CancellationToken ct = default);
    // ReplaceAsync(removed, added); ObjectsAsync; PlanCompactionAsync, CompactAsync, VacuumAsync, VerifyAsync
}

public interface IObjectStore : IAsyncDisposable
{
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken ct);   // a SegmentLease and the object's token
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken ct);
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken ct);
    ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken ct);
    IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken ct);
}
```

`IObjectStore` changes from [13-dataset.md](13-dataset.md) in the places listed as amendments in
§13: `ListAsync` pages by itself, `PutIfAbsentAsync` takes a `PipeReader` and a length so an object
streams and a multipart upload is possible, `DeleteAsync` takes a batch of keys and reports
nothing, since a store cannot say which keys existed, and a range read returns the object's token
with its bytes, because a token read in a separate call proves nothing about the bytes.
`FileObjectStore`, `MemoryObjectStore` and `CountingObjectStore` are public, the last so that a
store library can measure its read budget. The store exceptions derive from `VortexException`.
Maintenance keeps `PlanCompactionAsync`, `CompactAsync`, `VacuumAsync` and `VerifyAsync`, with a
`TimeProvider` on the options that read the clock.

### 12.3 `Vorticity.RowEncoding`, experimental

```csharp
public static class RowEncoder
{
    public const string VortexVersion = "0.86.1";                                  // the upstream release whose bytes these are
    public static RowKeys Encode<TRecord>(Columns<TRecord> columns, params ReadOnlySpan<RowSortField> fields) where TRecord : IVortexRecord<TRecord>;
    public static RowKeys Encode(BatchView batch, params ReadOnlySpan<RowSortField> fields);
    public static byte[] EncodeKey<TKey>(in TKey key, params ReadOnlySpan<RowSortField> fields) where TKey : IVortexRecord<TKey>;
}

public sealed class RowKeyEncoder : IKeyEncoder { public RowKeyEncoder(params ReadOnlySpan<RowSortField> fields);  public ReadOnlySpan<RowSortField> Fields { get; } }
public sealed class RowKeys : IEncodedKeys { public int TotalBytes { get; }  public ReadOnlySpan<byte> Elements { get; }  public ReadOnlySpan<int> Offsets { get; }  public ReadOnlySpan<int> Sizes { get; }  public int Compare(int left, int right);  public void SortIndices(Span<int> indices); }
public readonly struct RowSortField
{
    public RowSortField(bool descending, bool nullsFirst);
    public static RowSortField Ascending { get; }                                   // nulls first
    public bool Descending { get; }  public bool NullsFirst { get; }
    public RowSortField WithDescending(bool descending);  public RowSortField WithNullsFirst();  public RowSortField WithNullsLast();
}
```

`fields` holds one sort field per column, one for every column, or none for ascending with nulls
first. The keys cover every row of the batch, selected or not. A date, time, timestamp or uuid
column is ordered by its storage, which is its order. `EncodeKey` writes one tuple, given as a
record whose members are the key columns, so the dtypes the bytes depend on are the record's. The
bytes are the format of [06-row-encoding.md](06-row-encoding.md).

`[Experimental]` is set on the assembly, for both experimental packages: every type of the package
reports the diagnostic to a caller, and the package's own code, the dataset's use of the row
encoding included, runs in an experimental context that needs no suppression.

### 12.4 vxdump

Written against §5.8, §7 and the inspection members of `VortexFile` (§5.1) only, published ahead
of time, and the proof that the tool path is complete: a section vxdump cannot print from the
public surface is a gap in the surface, not in vxdump. The layout, encodings and segments sections
are why the inspection members exist; the row keys section it once had is gone, since vxdump
depends on the core alone.

## 13. What leaves the surface, and what this amends

### 13.1 Today's public types, and where they go

| today | tomorrow |
|---|---|
| `DTypeArena`, `DType`, `DTypeFormatter`, `PType`, `Nullability`, `DTypeKind` | `VortexSchema`, `VortexField`, `VortexType`, `VortexTypeKind`; the arena is internal |
| `CanonicalArena`, `CanonicalNode`, `CanonicalKind`, `VortexBuffer`, `Validity`, `ValidityKind`, `AlignedBufferPool` | `ColumnsBuilder`, `ColumnBuilder<T>`, `AlignedMemoryPool`; the arena is internal |
| `RecordBatch(arena, root, startRow)` | `RecordBatch` has no public constructor; `ToBatchesAsync` and `ToOwned` produce batches, and a `ColumnsBuilder`'s rows go to a writer, not into a batch |
| `VortexColumn` and the nine typed column views | `Column<T>` and its extension members, `Columns<TRecord>` |
| `ScanBuilder`, `VortexFileScanExtensions`, `Projection`, `ScanRequest`, `ScanContext`, `ScanMetrics`, `CountingSegmentSource` | `Scan<TRecord>`, `Scan`, `ScanOptions`, `ScanStatistics`, the meter |
| `ScanBuilder.DefaultDegreeOfParallelism` | `VortexSessionOptions.MaxDegreeOfParallelism` |
| `Expr`, `VortexExpr` node classes, `FilterLiteral`, `FilterLiteralKind`, `ComparisonOp`, `StringMatchOp`, `ExprKind` | `Sym<T>`, `Predicate`; `VortexExpr` stays as the parse result of the tool path, its nodes internal |
| `ColumnSummaries`, `SummaryPruner`, `ZoneMap`, `FileStatistics`, `FileStatisticsView`, `FieldStatistics` | `VortexFile.MayMatch<TRecord>`, `VortexFileStatistics`, `FieldStatistics` |
| `KeyCursor`, `KeyCursorBuilder`, `VortexFileKeyExtensions` | `KeyCursor<TKey>`, `KeyCursorBuilder<TKey>`, `Scan<TRecord>.Keys`; `KeyPlan`, `KeySourceKind` and `KeySourceRejection` stay as what `ExplainAsync` on the builder returns |
| `ISegmentSource`, `SegmentSpec`, `SegmentOwner`, `SegmentRequestSet`, `SegmentReadOptions`, `SegmentCoalescer`, `CoalescedRun`, `NativeSegmentOwner`, `PinnedArraySegmentOwner`, `RandomAccessSegmentSource` | `ISegmentSource`, `SegmentRange`, `SegmentLease`, `FileSegmentSource` |
| `ISegmentSink`, `StreamSegmentSink` | `PipeWriter` |
| `VortexReadOptions`, `WriteProfile`, `VortexEncodingHint`, `WritePolicy`, `CompositeKeyPolicy`, `IndexPolicyKind` | folded into `VortexOpenOptions`, `CompressionProfile`, `EncodingHint`, `IndexPolicy` |
| `EditionRegistry` | `VortexEditions` |
| `VortexFileFormat` | internal; `VortexFile.Edition` |
| `IndexDirectory`, `IndexEntry`, `IndexRun`, `IndexKinds` | internal |
| every decoder, every `*Metadata` struct, `LayoutTree`, `LayoutNode`, `LayoutView`, the `*LayoutReader`s, `ArrayNode*`, `ArrayView`, `ArrayStats*`, `Patches`, `Scalar*`, `TypedScalar`, `FieldMask*`, `FlatBuffer*`, `Proto*`, `DTypeFlatBuffers`, `DTypeProtobuf`, `EncodingRegistry`, `AggregateRegistry`, `ExtensionDTypeRegistry`, `ArrayBlobReader`, `Alignment`, `Int256`, `DecimalStorage`, `MessageScope`, `AggregateSpec*` | internal |

`VortexFile`, `VortexFileWriter`, `VortexFileRepair`, `VortexFileIndexer`, `VortexLimits`,
`VortexEdition`, `VortexDecimal`, `RowRange`, `SeekOp`, `KeyPlan`, `ScanPlan`, `PruningStep`,
`CountPlan`, `OrderPlan`, `WriteReport`, `ColumnWriteReport`, `IndexWriteReport`, `IndexOutcome`,
`VortexIndexInfo`, `VortexIndexVerification`, `IndexFragment`, `VortexTornTail`,
`VortexTornTailPolicy`, `VortexRepairResult`, `ComponentKind`, `VortexFormatException`,
`VortexUnsupportedException` keep their names and lose what §5 to §8 do not list.

### 13.2 Amendments to other documents

| document | what changes |
|---|---|
| [03-architecture.md](03-architecture.md) §1 | "Async only" stands, and reaches the writer: a scan is consumed with `await foreach` over every source and a writer is fed with `WriteAsync`; there is no synchronous enumeration and no synchronous read on the seam. "No `params`" becomes: `params ReadOnlySpan<T>` only, which allocates nothing. §3's public API is superseded by this document |
| [09-contracts.md](09-contracts.md) §1 | the thread-safety table gains `VortexSession` (thread-safe), `Scan<TRecord>` and `Scan` (single-use, one thread), `Columns<TRecord>` and `Column<T>` (affine to the enumeration body, enforced by the compiler), `ColumnsBuilder` (one thread) |
| [09-contracts.md](09-contracts.md) §2 | `ScanBuilder.DefaultDegreeOfParallelism` is removed; consent is given on the session, once, and `ScanOptions.DegreeOfParallelism` overrides it per scan. I/O concurrency is bounded by `VortexSessionOptions.MaxConcurrentReads`, a `SemaphoreSlim` and no package |
| [09-contracts.md](09-contracts.md) §3 | "changing the default write edition is major" stands; the default is `VortexEditions.Default`, the edition the most deployed Rust reader accepts, chosen per release and named in the release notes |
| [07-dotnet-mapping.md](07-dotnet-mapping.md) §2 | a `decimal` record member is accepted when the column's precision and scale fit `System.Decimal`, checked at binding; `VortexDecimal` remains the mapping beyond that, and the only one for a `Column<T>` over a wider column |
| [08-semantics.md](08-semantics.md) §3 | a literal of a type the column cannot compare to is no longer a silent non-match: on the typed path it does not compile, on the tool path `Where` throws `VortexSchemaException` |
| [12-index-reads.md](12-index-reads.md) §8 | `file.Keys(column)` becomes `Scan<TRecord>.Keys(r => r.Column)`; `FilterLiteral` keys become `TKey`; `Compare` is dropped from the cursor |
| [13-dataset.md](13-dataset.md) §3, §8 | `IObjectStore.ListAsync` pages by itself and returns `IAsyncEnumerable<string>`; `PutIfAbsentAsync` takes a `PipeReader` and a length; `DeleteAsync` takes a batch of keys |
| [10-indexes.md](10-indexes.md) §11 | an abandoned index leaves no bytes: the budget is decided before the filters are written; `required` fails the write |
| [11-write-strategy.md](11-write-strategy.md) §6 | the read contract gains: one lease per segment per scan, decode per block from that lease; `ScanOptions.Compact = false` delivers a selection instead of a compaction |
| [07-dotnet-mapping.md](07-dotnet-mapping.md) §1, §3–§5 | `T` carries nullability; a zoned timestamp's zone is resolved once at binding; the string and non-struct root examples are on this surface |
| [09-contracts.md](09-contracts.md) §5 | the meter and the activity source, their instruments, names and tags, beside the `EventSource` |
| [10-indexes.md](10-indexes.md) §5.5, §7.1, [11-write-strategy.md](11-write-strategy.md) §7.1 | the default index policy is `IndexPolicy.None`; `Auto` is asked for |
| [12-index-reads.md](12-index-reads.md) §1, [11-write-strategy.md](11-write-strategy.md) §7.2 | `ScanBuilder`, `InKeyOrder`, `Take` and `WithIndexes` are the internal engine's names, mapped to `Scan<TRecord>`, `OrderBy`, `Rows` and `ScanOptions` |
| [03-architecture.md](03-architecture.md) §2, §5, [08-semantics.md](08-semantics.md) §4 | no `Vorticity.Arrow`; `VortexUnsupportedException` takes a `ComponentKind`; no `AllowUnknownComponents` |

### 13.3 What the guide becomes

Every page of [the guide](../guide/README.md) is a case of §9 or §10 with its measured figures.
The pages that describe today's arena, `FilterLiteral`, `ScanMetrics` and the static degree of
parallelism are rewritten against this surface when it lands; until then they describe the code
that exists.
