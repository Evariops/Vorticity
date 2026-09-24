# The public surface

Why the surface a caller programs against has the shape it has, and where each part lives. The
complete listing of every public type and member is
[tests/Vorticity.Tests/Api/PublicSurface.txt](../../tests/Vorticity.Tests/Api/PublicSurface.txt),
which `PublicSurfaceTests` holds equal to the assemblies; the XML documentation is in the source; the
[guide](../guide/README.md) shows each use in a program that runs. This document does not repeat
them: it gives the rules they follow and the decisions behind them. The engine under the surface is
[03-architecture.md](03-architecture.md), the type mapping [07-dotnet-mapping.md](07-dotnet-mapping.md),
the meaning of a predicate [08-semantics.md](08-semantics.md).

Five rules decide every signature:

1. **A type is a schema, not a row.** A record declares which columns exist and how they are typed;
   reading it yields columns. Rows are a sink a caller asks for, and pays for.
2. **The compiler holds the lifetimes.** Everything borrowed from a scan is a `ref struct`: no
   `Dispose` on the hot path, nothing to forget.
3. **The plan lives under the API.** A scan is described by ordinary lambdas over a symbolic probe,
   run once. What cannot be pushed down does not compile. No expression tree, no string column name
   and no untyped value on the typed path.
4. **Generic, specialized, BCL first**: `INumber<T>`, static abstract members, intrinsics,
   `System.IO.Pipelines`, `System.Diagnostics.Metrics`; nothing virtual per value.
5. **A session, not a static.** Pools, caches, I/O concurrency, parallelism and extension registries
   live on a `VortexSession`.

What the surface is not: a DataFrame, a SQL engine, a join, a bridge to another columnar library. An
owned `RecordBatch` is where a bridge would attach, and the plan a scan builds is what a query
provider would target.

## 1. Packages and namespaces

| package | what it holds | depends on |
|---|---|---|
| `Vorticity` | the session, files, the writer, schema, the typed scan and its columns, the tool scan, the I/O seam, options, plans, exceptions | `System.IO.Hashing` |
| `Vorticity.Generators` | the `[VortexRecord]` generator and the analyzers | build time only |
| `Vorticity.Dataset` | the dataset over an object store, `[Experimental("VX0001")]` | `Vorticity` |
| `Vorticity.RowEncoding` | the row encoding, `[Experimental("VX0002")]` | `Vorticity` |

The core has two public namespaces: `Vorticity`, for everything a caller uses, and
`Vorticity.IO`, for the segment seam. The attributes the generator reads live in the core, so a
record can implement its interface by hand without referencing the generator.

## 2. The session

`VortexSession` (`Session/`) owns what must not be global:

| owned | why |
|---|---|
| the memory pool, 64-byte aligned by default (`AlignedMemoryPool`) | every batch, segment and builder buffer comes from it, and disposing the session returns everything |
| the segment cache | one budget for every file of the session, for sources that do I/O |
| the bound on reads in flight, `MaxConcurrentReads` | an object store sees at most that many requests from the process |
| the degree of parallelism, `MaxDegreeOfParallelism`, 1 by default | where every scan and writer of the session starts ([09-contracts.md](09-contracts.md) §2) |
| the index cache, `IndexCacheBytes` | what each open file keeps of decoded index runs |
| the extension registry | dtype ids this process knows beyond the editions, so a registered type binds to a record member or a `Column<T>`; binding checks the storage type the registration declares |

`VortexSession.Default` is immutable and is what `VortexFile.OpenAsync(path)` uses. A session is
thread-safe and meant to be shared by every request of a process, and its options are frozen when it
is created. It opens files from a path or from any `ISegmentSource`, creates writers over a path or a
`PipeWriter`, and appends; how a path is read is [03-architecture.md](03-architecture.md) §3.5.

## 3. Schema and types

The schema is a value: `VortexSchema`, a list of `VortexField(Name, VortexType)` that a collection
expression builds, and `VortexType`, immutable and interned by value, with a factory per dtype
(`Schema/`). `Map`, `Union` and `Variant` have no factory: a file may declare them, so the schema
shows them, but no .NET type maps to them. The arena the reader decodes into is built once at open
and never appears.

## 4. Records: the generated contract

A `[VortexRecord]` type — a `partial` record struct, record class, struct or class — declares
columns by its public members, in declaration order and under their names; `[VortexColumn]` renames
one and carries what a .NET type does not say (a decimal's precision and scale, a time unit, a time
zone), and `[VortexIgnore]` skips one. The generator (`src/Vorticity.Generators/RecordGenerator.cs`)
implements `IVortexRecord<TSelf>` — the schema, and a column-at-a-time read and write of rows — and
emits extension members on the probe, the columns and the builder, so that `r.Day`, `batch.Day` and
`builder.Day` are typed properties. Everything it emits can be written by hand
([records.md](../guide/records.md)).

A record binds to a file once, at the first sink of a scan or at `CreateWriter<TRecord>`: each member
matches a column by exact name, then by a unique case-insensitive match, and a missing column, an
ambiguous match or a type that does not fit throws `VortexSchemaException` naming both. A file with
more columns than the record is fine: **the record is the projection**. Where the mapping leaves room,
the generator decides: an `enum` is its underlying integer on the columns and an enum symbol on the
probe; a `DateTimeOffset` member without a declared zone is UTC; a list of records and a nullable
registered extension are refused with VX1005.

## 5. Reading

### 5.1 `Scan<TRecord>`

`file.Scan<TRecord>()` reads the columns the record names and nothing else. `Where` adds a predicate,
`Rows` a range or a set of row indices, `OrderBy` a key order, and `With(ScanOptions)` the scan's
batch cap, prefetch, degree, compaction and switches (`Scan/Scan.Typed.cs`, `Scan/ScanOptions.cs`). A
scan is consumed with `await foreach`, whatever the source: there is no synchronous enumeration. The
batch is **borrowed** — valid until the next `MoveNextAsync`, the contract of a `PipeReader`'s buffer —
and the loop body runs synchronously between two batches, so a `ref struct` in it is legal. Under
the loop, the decode of the next batch overlaps the caller's work on the current one, by `Prefetch`
batches, with buffers that alternate rather than accumulate.

### 5.2 The symbolic algebra

The lambda given to `Where` has the syntax of LINQ and none of its machinery (`Symbolic/`).
`Probe<TRecord>` has a `Sym<T>` per member, whose operators record a predicate instead of evaluating
one; the lambda runs once, when the scan is built, and its result is the plan. **What compiles is
exactly what is pushed down**:

| written | meaning |
|---|---|
| `r.Day >= 900`, `r.Day.In(1, 2, 3)` | comparison and membership, against a literal of the column's type |
| `r.Celsius > 45.0 && r.City == "Paris"` | conjunction; `\|\|` and `!` likewise |
| `r.Celsius == null`, `r.Celsius.IsNull` | nullity, never unknown ([08-semantics.md](08-semantics.md) §3) |
| `r.City.StartsWith("Par")`, `r.City.Like("P_r%")`, `r.Pages.Contains(42)` | text and list predicates ([12-index-reads.md](12-index-reads.md) §6) |
| `r.High > r.Low` | column against column of one type |
| `r.Day >= "900"`, `r.Day % 2 == 0`, `Math.Abs(r.Celsius) > 1` | **does not compile**: a literal of the wrong type, or no pushdown |

A captured local is read once, when the lambda runs, and an optional filter is two `Where` calls,
since the second joins with `AND`. A literal is compared **exactly**, never rounded into the column's
unit: a `DateTime` finer than a timestamp column's unit lies strictly between two stored values, so
`==` holds for no row and an ordering keeps or drops the boundary row correctly; a decimal literal
follows the same rule against the column's scale. A breakpoint inside the lambda sees symbols, not
values, and hits once.

### 5.3 `Columns<TRecord>` and `Column<T>`

A batch is a `Columns<TRecord>`, with a typed property per member and a deconstruction, and each
column a `Column<T>` (`Columns/`): `Values` as a span for fixed-width types, a span per value for text
and binary, a range per row for a list, and the accessors of [07-dotnet-mapping.md](07-dotnet-mapping.md)
§1. `Values` is **64-byte aligned** whatever the file's segment alignment: a buffer laid out on a
smaller boundary is copied once into an aligned block at its first read. On a nullable column `Values`
is the raw buffer, meaningless where the validity bit is 0, and `ValidityWords` gives the validity as
64-bit words; the indexer returns `T?` at the cost of a branch per value, and says so. `StartRow` is
the batch's first row in the file, and `Selection` the rows a filtered or taken block keeps when it is
delivered whole (§5.4).

### 5.4 Encoded views and the selection

A column says what its block holds before any decode (`Encoding`). A dictionary block has not been
decoded: `Values` forces the decode, while `AsDictionary()` gives its codes and values, so a consumer
that groups by it groups by code and never touches its strings; `AsRunEnd()` gives runs and their
lengths ([encoded-forms.md](../guide/encoded-forms.md)). With `ScanOptions.Compact = false`, a
filtered scan delivers each live block whole with its `Selection` instead of copying the surviving
rows: a filter that keeps half a block copies nothing ([selection.md](../guide/selection.md)).

### 5.5 Aggregates and group by

An aggregate is an operator of the scan, not a loop the caller writes (`Aggregation/`, `Scan/Scan.Agg*.cs`).
It runs block by block on the encoded form, never materializes a batch, keeps one state per chunk
when the session allows parallelism, and merges them at the end; its memory is the number of groups.
What each block lets it skip:

| block | count, minimum, maximum, sum | group by |
|---|---|---|
| settled by the file or zone statistics | read from them, nothing decoded | |
| constant | value × count | one group |
| run-end | per run, weighted by its length | by run |
| dictionary | over the distinct values, when every row is selected | by code, an array indexed by code, no hashing |
| frame of reference, bit-packing, delta | vectorized unpack, the base applied once | |
| a key the statistics say is sorted | | by run detection, no hashing |
| alp, zstd, fsst | the decode, then the plain kernel | |

A caller's aggregator implements `IAggregator` and receives the plain form, or
`IEncodedAggregator` to receive runs and dictionaries too. An integer sum runs in 128 bits, exact
whatever the order of rows or the cut of a parallel aggregation, and throws only when the result does
not fit; floats skip NaN, as the statistics do; a null key is a group of its own. Group-by results
come in key order when the key source is ordered, and in no promised order otherwise.

### 5.6 The sinks

| sink | returns | pays |
|---|---|---|
| `await foreach` | borrowed `Columns<TRecord>` | nothing per batch |
| `ToBatchesAsync()` | owned `RecordBatch`es | one copy per batch into pooled buffers |
| `ToRecordsAsync()` | `IAsyncEnumerable<TRecord>` | a copy per row; an allocation per row for text, lists and nested classes |
| `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, `AvgAsync`, `CountDistinctAsync` | one value | the statistics when they settle it, blocks otherwise ([12-index-reads.md](12-index-reads.md) §4) |
| `AggAsync(a => (…))` | several answers, one pass | the same |
| `GroupBy(…).AggAsync(…)` | one row per group | one state per group |
| `ExplainAsync()` | the `ScanPlan` | statistics and zone maps, never data |

**LINQ starts after a sink.** `ToRecordsAsync` yields an `IAsyncEnumerable<TRecord>`, so
`System.Linq.AsyncEnumerable` applies to it, on materialized rows, and the boundary is visible in the
code: everything before the sink is pushed, nothing after it is ([read-rows.md](../guide/read-rows.md)).

### 5.7 The key cursor

`Scan<TRecord>().Keys(r => r.Column)` builds a `KeyCursorBuilder<TKey>`, `TKey` inferred from the
member, and `OrderBy(r => r.Column)` delivers rows in key order: [12-index-reads.md](12-index-reads.md).

### 5.8 The tool path

For a file whose schema is not known when the program is compiled — `vxdump`, an ad hoc query, a
test — `file.Scan(columns)` reads columns by name, and `Where` takes a filter as text with typed holes
(`Scan/Scan.Tool.cs`, `Expressions/VortexExpr.Surface.cs`). It is the one place in the surface where
names are strings, because there is no other name. A batch is a `BatchView` holding the named columns
in the file's order; a number in a filter is the number the text wrote, scaled exactly against a
decimal column, and text compared with a date, time, timestamp, uuid or decimal column is parsed in
that type and refused when it does not parse ([untyped-files.md](../guide/untyped-files.md)).

## 6. Writing

The shape of a `PipeWriter`: the caller fills builders synchronously, and the writer encodes and hands
the bytes to its sink asynchronously (`Writing/`). A writer comes from the session, over a path or a
pipe, typed by a record or by a schema; `Builder()` hands out its `ColumnsBuilder`, whose
`ColumnBuilder<T>` gives spans to fill in place (`GetSpan`, `Advance`, as `IBufferWriter<T>`),
appends values, nulls, text and lists, and is reused after every `WriteAsync`. A primitive column is
encoded from the caller's bytes without a copy. A list appended in one call that fails part way
leaves nothing behind. A nullable column costs nothing until its first null.

`WriteAsync` takes a builder, a span or a stream of records, or a batch read from another file.
`FlushAsync` seals the whole blocks into a chunk and is the only I/O before `CompleteAsync`, which
writes the tail and returns the `WriteReport`. `Abandon()` gives the file up: a new file is deleted,
an append truncated back to its length, and a caller's pipe completed with an error. What the writer
does with the rows is [11-write-strategy.md](11-write-strategy.md); the guide's writing pages show each
form.

## 7. Options, plans, reports, diagnostics, exceptions

| type | what it says |
|---|---|
| `VortexOpenOptions` | the initial tail read, a known length or schema, the decompression cap, statistics verification, a torn tail's policy, index fragments to attach |
| `ScanOptions` | batch cap, prefetch, degree, `Compact`, and the `Pruning` and `UseIndexes` switches, which exist to check the structures, never to change a result |
| `VortexWriteOptions` | blocks and chunk targets, the compression profile and per-column hints, the target edition, statistics, string bounds, the index policy, the identity, user metadata, the degree |
| `IndexPolicy`, `EncodingHint`, `CompressionProfile` | [10-indexes.md](10-indexes.md) §6, [11-write-strategy.md](11-write-strategy.md) §3.4 |
| `ScanPlan`, `PruningStep`, `CountPlan`, `OrderPlan`, `KeyPlan` | the plan before a read: blocks, live blocks, segments and bytes to read, what each structure pruned and what consulting it cost, how a count and an order will be answered |
| `ScanStatistics` | the same quantities, measured after the scan |
| `WriteReport` | what the writer chose, per column and per chunk, and every index built or abandoned |
| `VortexDiagnostics` | the names of the meter and the activity source ([09-contracts.md](09-contracts.md) §5) |
| `VortexException` and its three kinds | a malformed file, an unsupported component with its id and `ComponentKind`, a schema that does not fit ([03-architecture.md](03-architecture.md) §5) |
| `VortexEditions` | the default, newest and floor editions, and the Rust version that reads each |

**The plan and the statistics count in the same units**: a plan's `Segments` and `BytesToRead` are what
the source will be asked for, each segment once, and `Requests` and `BytesRequested` are what it was
asked for, so on a file with statistics the two agree. `BlocksDecoded` counts the blocks that reached
the plain form: a block answered from a dictionary's codes or a run's lengths is not decoded.

## 8. The I/O seam

`Vorticity.IO` holds `ISegmentSource` and its three built-in implementations, whose contract is
[03-architecture.md](03-architecture.md) §3.5, and a writer's sink is any `PipeWriter`. What a scan
promises its source: **a segment is requested at most once per scan**, and the session's cache adds
reuse across scans. [object-store.md](../guide/object-store.md) implements the seam for a remote store.

## 9. The performance contract, and the gates that hold it

Each promise is a test (`tests/Vorticity.Tests/Api/*ContractTests.cs` and their neighbours), and
the build fails when one stops holding:

| promise | gate |
|---|---|
| no allocation per batch on every sink but rows | allocation counted across a full scan after one warm-up batch |
| `Values` contiguous and 64-byte aligned | the address of every `Values` span of the conformance corpus |
| a segment requested at most once per scan, and the plan is the execution | `Requests` equals the plan's distinct segments, `BytesRequested` equals `BytesToRead`, on every query of the corpus |
| a pruned block is not decoded | `BlocksDecoded` equals `LiveBlocks` |
| a question the statistics answer reads nothing | no request after the open for a count, a minimum or a maximum on a file with statistics |
| a dictionary or run-end aggregate does not decode | `BlocksDecoded` is 0 for a group by on a dictionary column and a sum over runs |
| a chunk is decoded once per scan, whatever runs it | the values decoded equal rows × columns at every prefetch and degree, and for a parallel aggregation |
| what cannot be pushed down does not compile | `tests/MustNotCompile` builds with exactly the expected diagnostics |
| the surface does not drift | `PublicSurfaceTests` against `PublicSurface.txt` |
| `vxdump` uses the public surface only | it compiles without access to internals |

The analyzers ship in `Vorticity.Generators` ([diagnostics.md](../guide/diagnostics.md)):

| id | flags |
|---|---|
| VX1001 | a span borrowed from a `Column<T>` stored in a variable declared outside the enumeration body |
| VX1002 | an owned `RecordBatch` that is never disposed |
| VX1003 | a hole of an interpolated filter whose type maps to no column type |
| VX1004 | `Rows` by range and by indices on the same scan |
| VX1005 | a record member whose type has no mapping |
| VX1006 | a record type, or a type containing it, that is not `partial` |
| VX1007 | a type that cannot be a record: generic, `ref struct`, static, abstract, file-local, or without a usable constructor |
| VX1008 | a member the reader cannot fill |

VX1001 to VX1004 are warnings; VX1005 to VX1008 are errors, and the generator emits nothing for a type
that has one.

## 10. The satellites

- **`Vorticity.Dataset`**: `VortexDataset.Scan<TRecord>()` returns the same `Scan<TRecord>` as a
  file, over every object of the version the handle read, which a later commit does not move; a
  batch's `StartRow` and a cursor's `Row` are positions in the dataset. The store seam, the
  maintenance calls and what they cost are [13-dataset.md](13-dataset.md).
- **`Vorticity.RowEncoding`**: `RowEncoder`, `RowSortField`, `RowKeys` and `RowKeyEncoder`
  ([06-row-encoding.md](06-row-encoding.md)). `[Experimental]` is set on the assembly of both
  experimental packages, so every type reports its diagnostic to a caller, and the packages' own code
  needs no suppression.
- **`vxdump`** is written against the tool path, the inspection members of `VortexFile` and the plans
  only, and published ahead of time: a section it cannot print from the public surface is a gap in the
  surface, not in the tool ([vxdump.md](../guide/vxdump.md)).
