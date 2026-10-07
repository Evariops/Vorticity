# The public surface

Why the surface a caller programs against has the shape it has, and where each part lives. The
complete listing of every public type and member is
[tests/Vorticity.Tests/Api/PublicSurface.txt](../../tests/Vorticity.Tests/Api/PublicSurface.txt),
which `PublicSurfaceTests` holds equal to the assemblies; the XML documentation is in the source; the
[guide](../guide/README.md) shows each use in a program that runs. This document does not repeat
them: it gives the rules they follow and the decisions behind them. The engine under the surface is
[03-architecture.md](03-architecture.md), the type mapping [07-dotnet-mapping.md](07-dotnet-mapping.md),
the meaning of a predicate [08-semantics.md](08-semantics.md).

Seven rules decide every signature:

1. **A type is a schema, not a row.** A record declares which columns exist and how they are typed;
   reading it yields columns. Rows are a sink a caller asks for, and pays for. A query's result of
   several values is a record's schema too: the record a scan reads from, the record a result is
   read as.
2. **The compiler holds the lifetimes.** Everything borrowed from a scan is a `ref struct`: no
   `Dispose` on the hot path, nothing to forget.
3. **The plan lives under the API.** A query is described by ordinary lambdas over a symbolic probe,
   run once, in the shape of LINQ: its method names, and its query syntax. What cannot be pushed
   down does not compile. No expression tree, no string column name and no untyped value on the
   typed path.
4. **Generic, specialized, BCL first**: `INumber<T>`, static abstract members, intrinsics,
   `System.IO.Pipelines`, `System.Diagnostics.Metrics`; nothing virtual per value.
5. **A session, not a static.** Pools, caches, I/O concurrency, parallelism and extension registries
   live on a `VortexSession`.
6. **`Async` names what runs.** A method that starts the work — an awaitable, or a stream that ends
   the query — ends in `Async`: `CountAsync`, `AverageAsync`, `ToRecordsAsync`. A method that only
   describes the query does not: `Where`, `GroupBy`, `Select`, `Take`, whose result is run by
   `await foreach`. A builder never reads: what it checks is in the schema the open read.
7. **Batches flow.** Every source, operator and sink exchanges batches, pulled by its consumer and
   decoded ahead within a bounded window. A query answers as soon as its first batch is final, holds
   what is still open rather than what it read, stops reading when its consumer stops, and
   allocates nothing per batch; a result is a stream of batches like a file's, and a scan of its
   own. Time to first batch, peak memory and allocations are promises with gates (§9).

What the surface is not: a DataFrame, a SQL engine, a join, a bridge to another columnar library. A
query over one table has the shape of LINQ ([16-queries.md](16-queries.md)); what is not one, a
`join` or a `let`, does not compile. An owned `RecordBatch` is where a bridge would attach, and the
plan a scan builds is what a query provider would target.

## 1. Packages and namespaces

| package | what it holds | depends on |
|---|---|---|
| `Vorticity` | the session, files, the writer, schema, the typed scan and its columns, the tool scan, the I/O seam, options, plans, exceptions | `System.IO.Hashing`, `Vorticity.Zstd` |
| `Vorticity.Generators` | the `[VortexRecord]` generator and the analyzers | build time only |
| `Vorticity.Dataset` | the dataset over an object store, `[Experimental("VX0001")]` | `Vorticity` |
| `Vorticity.RowEncoding` | the row encoding, `[Experimental("VX0002")]` | `Vorticity` |
| `Vorticity.Zstd` | Zstandard compression and decompression in managed C#: `ZstdCompressor`, `ZstdDecompressor` | `System.IO.Hashing` |

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
| the files kept mapped once closed, `MappedFileCacheCount` | opening a file again takes over its mapping and every page already mapped in it |
| whether a path is mapped at all, `MapFiles`, true by default | a service that reads files others may truncate reads them positionally, so that a cut file fails a read rather than the process |
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
more columns than the record is fine: **a scan reads the columns its query names** — the record's
members for batches and records, the elements of a `Select`, the keys and inputs of an aggregate —
so the record is the projection of a scan that delivers it. Where the mapping leaves room,
the generator decides: an `enum` is its underlying integer on the columns and an enum symbol on the
probe; a `DateTimeOffset` member without a declared zone is UTC; a list of records and a nullable
registered extension are refused with VX1005.

## 5. Reading

### 5.1 `Scan<TRecord>`

`file.Scan<TRecord>()` reads the columns the record names and nothing else. `Where` adds a predicate,
`Rows` a range or a set of row indices, `OrderBy` or `OrderByDescending` a key order, and
`With(ScanOptions)` the scan's batch cap, prefetch, degree, compaction and switches
(`Scan/Scan.Typed.cs`, `Scan/ScanOptions.cs`); `Select` and `GroupBy` turn it into a query of values
or of groups (§5.5). A
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
| `r.At.Truncate(CalendarUnit.Day) == day`, `r.Price.Bucket(10m) >= 100m` | a function of a column; non-decreasing, so the zone maps still prune ([16-queries.md](16-queries.md) §3) |
| `r.Day >= "900"`, `r.Day % 2 == 0`, `Math.Abs(r.Celsius) > 1` | **does not compile**: a literal of the wrong type, or no pushdown |

The same symbols are every lambda's: the keys of a `GroupBy`, the inputs of an aggregate, the
elements of a `Select`; and the results of a group compare in the `Where` that follows a `GroupBy`
as columns do in a filter ([16-queries.md](16-queries.md)).

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

### 5.5 Queries: select, group, aggregate

A projection, a group by and an aggregate are operators of the scan, not loops the caller writes
(`Aggregation/`, `Scan/Scan.Agg*.cs`, `Scan/Scan.Grouping.cs`), and they read as LINQ does, in
method or query syntax:

```csharp
Scan<CityHour> hourly =
    (from r in file.Scan<Reading>()
     where r.Day >= 900
     group r by (Hour: r.At.Truncate(CalendarUnit.Hour), r.City) into g
     where g.Count() > 10
     orderby g.Key.Hour, g.Average(x => x.Celsius) descending
     select (g.Key.Hour, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
    .As<CityHour>();
```

`GroupBy` takes one symbol or a tuple of them, whose names become the key's: `g.Key.Hour`. The
group's aggregates take a lambda over the rows, as LINQ's do, and the rows' probe exists only inside
it: a column that is neither a key nor aggregated does not compile. `Where`, `OrderBy`, `ThenBy`,
`Skip` and `Take` on the groups are a filter, an order and a top-k the engine applies. `Select` of
one value delivers an `Aggregation<T>`, one `T` per group; `Select` of several delivers an
`Aggregation` that a record reads, as many values as the record has members, through
`As<TRecord>()`. `Select` on a scan delivers a `Projection<T>` or a `Projection` of computed values
the same way. `As<TRecord>()` turns any of them into a `Scan<TRecord>` over the result, which the
result's columns fill, and on which every operator and sink of a scan runs again: a filter, a group
by of the groups, a write. `AggAsync<TResult>` answers a whole scan into a record the same way.

A query runs block by block on the encoded form — a dictionary key by code, a run-end key by run, a
sorted key by run detection, a constant one whole — and never hands a batch to the caller's code. It
**flows**: its result is a stream of batches, delivered as soon as each is final. A group by on a
key the statistics prove ordered, or a function of one that keeps the order, closes its groups as
the key moves on and delivers them then, holding the open ones only; on any other key it holds one
state per group, at any degree, and delivers at the end of its input. The whole semantics, from the
flow and the catalog of aggregates to the order of groups and the engine's strategies, is
[16-queries.md](16-queries.md).

A caller's aggregator implements `IAggregator` and receives the plain form, or
`IEncodedAggregator` to receive runs and dictionaries too. A sum is exact whatever the order of rows
or the cut of a parallel aggregation: an integer sum is delivered as a 64-bit integer, or as its own
type when it is one already, throwing only when the total does not fit it; a float sum is the same
bits at every degree and under every cut of the data into chunks, files or objects. Floats skip
NaN, as the statistics do; a null key is a group of its own. Groups come in the order a query asks
for, and in no promised order without one.

### 5.6 The sinks

| sink | returns | pays | first answer |
|---|---|---|---|
| `await foreach` | borrowed `Columns<TRecord>` | nothing per batch | the first split |
| `ToBatchesAsync()` | owned `RecordBatch`es | one copy per batch into pooled buffers | the first split |
| `ToRecordsAsync()` | `IAsyncEnumerable<TRecord>` | a copy per row; an allocation per row for text, lists and nested classes | the first split |
| `Select(r => e)` | `Projection<T>`, a value computed per row | the same as `ToRecordsAsync`, for the columns its element names | the first split |
| `Select(r => (…)).As<TRecord>()` | a `Scan<TRecord>` of values computed per row | nothing per batch, for the columns its elements name | the first split |
| `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, `AverageAsync`, `CountDistinctAsync`, `AggregateAsync`, `AggAsync(a => e)` | one value | the statistics when they settle it, blocks otherwise ([12-index-reads.md](12-index-reads.md) §4) | at once when the statistics settle it, the end of the pass otherwise |
| `AggAsync<TResult>(a => (…))` | several answers, one pass, into the record `TResult` | the same | the same |
| `GroupBy(…).Select(g => r)` | `Aggregation<T>`, one value per group | one state per group, the open groups only on a key that streams; `k` groups for a top-k | the first group closed on a key that streams, the end of the pass otherwise |
| `GroupBy(…).Select(g => (…)).As<TRecord>()` | a `Scan<TRecord>` over the groups, its batches borrowed | the same, and nothing per batch nor per group | the same |
| `writer.WriteAsync(scan)` | the batches of any scan, written as they come | the writer's own work; a rollup builds no record | — |
| `ExplainAsync()` | the `ScanPlan` | statistics and zone maps, never data | — |

**The plan ends at the sink.** A query has LINQ's names and syntax up to its last operator, and
everything up to there is pushed. Past it are values: `ToRecordsAsync()` and `ToValuesAsync()` hand
them out as an `IAsyncEnumerable<T>`, to which `System.Linq.AsyncEnumerable` applies, on
materialized values. A query is enumerated, like a scan, by the pattern of `await foreach` rather
than as an `IAsyncEnumerable<T>`, since what returns a stream to await carries `Async`; its own
`Skip`, `Take`, `ToListAsync`, `ToArrayAsync`, `As<TRecord>` and `Distinct` stay in the plan. A
query of several values is not enumerable: `As<TRecord>` hands it, like any result, to a scan, where
the plan goes on ([16-queries.md](16-queries.md) §1, [read-rows.md](../guide/read-rows.md)).

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
| `ScanPlan`, `PruningStep`, `CountPlan`, `OrderPlan`, `GroupPlan`, `GroupKeyPlan`, `GroupOrdering` | the plan before a read: blocks, live blocks, segments and bytes to read, what each structure pruned and what consulting it cost, how a count, an order and a group by will be answered ([16-queries.md](16-queries.md) §10) |
| `ScanStatistics`, `GroupStatistics` | the same quantities, measured after the scan; for a group by, its groups, the most held at once and the bytes, what its core did, how each key block was grouped, and the time to its first batch |
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
| a query's answers and order do not depend on its syntax, its degree or the cut of its data | method and query syntax build one plan; every answer, float sums included, the same bits at every degree, over two row orders, and over a dataset cut into several objects, before and after compaction ([16-queries.md](16-queries.md) §13) |
| the first batch waits for the first split | the time to first batch of a scan, a projection, a `Distinct` and a group by that streams, flat over two files sixteen times apart, locally and over the HTTP source with latency |
| memory is the window and the open state | `LiveMemoryTests`: a streaming group by's peak flat as its groups grow a hundredfold, a top-k's proportional to `k`, a high-cardinality group by's at most its groups' |
| a `Take` reads what it uses | `Requests` and `BlocksDecoded` bounded by the splits that hold its rows and the window |
| a group by is no slower than the loop by hand | a throughput axis per key form, the hand-written loops of the guide as floor |
| what cannot be pushed down does not compile | `tests/MustNotCompile` builds with exactly the expected diagnostics, a column outside an aggregate, `let` and `join` included |
| the surface does not drift | `PublicSurfaceTests` against `PublicSurface.txt`, and rule 6: a member that returns an awaitable or a terminal stream ends in `Async`, a builder does not |
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
| VX1009 | a component of a `GroupBy` key that is not a symbol of the scan: a literal, a captured value |
| VX1010 | an `As<TRecord>()` or an `AggAsync<TResult>` whose record members do not take the selected values, position by position |
| VX1011 | several selected values read without a record: enumerated, or awaited without `TResult`; it names `As<TRecord>()` and the record to declare |

VX1001 to VX1004 are warnings; VX1005 to VX1008 are errors, and the generator emits nothing for a type
that has one; VX1009 and VX1010 are errors, since what they flag throws when the query is built, and
VX1011 goes with the compiler's own error, to name the fix.

## 10. The satellites

- **`Vorticity.Dataset`**: `VortexDataset.Scan<TRecord>()` returns the same `Scan<TRecord>` as a
  file, over every object of the version the handle read, which a later commit does not move; a
  batch's `StartRow` and a cursor's `Row` are positions in the dataset, and a cursor walks both ways
  as a file's does. Its objects are opened ahead of the one being read and read side by side at a
  degree above one, so the first batch waits for the first object and no object boundary stalls the
  stream; every query of [16-queries.md](16-queries.md) runs over it, an object a range of a parallel
  aggregation. `DeleteAsync` and `UpdateAsync` take the same lambda as `Where`, and
  `EvolveSchemaAsync` a `VortexSchema` and the renamed columns. The store seam, the maintenance calls,
  what each costs and how a change of rows or of schema commits are [13-dataset.md](13-dataset.md).
- **`Vorticity.RowEncoding`**: `RowEncoder`, `RowSortField`, `RowKeys` and `RowKeyEncoder`
  ([06-row-encoding.md](06-row-encoding.md)). `[Experimental]` is set on the assembly of both
  experimental packages, so every type reports its diagnostic to a caller, and the packages' own code
  needs no suppression.
- **`vxdump`** is written against the tool path, the inspection members of `VortexFile` and the plans
  only, and published ahead of time: a section it cannot print from the public surface is a gap in the
  surface, not in the tool ([vxdump.md](../guide/vxdump.md)).
