# The guide

These pages are named after what you are trying to do. Each one opens with a working example, then
explains what happens, what to watch out for, and what it costs.

Every example is a program in [samples/Vorticity.Samples](../../samples/Vorticity.Samples). The build
compiles it, and you can run it by the page's name:

```
dotnet run -c Release --project samples/Vorticity.Samples -- filter-rows
```

With no argument it runs every case. The figures quoted in the pages come from such a Release run,
where each case runs after the others on code they have already compiled. A case run alone also
pays for compiling what it runs first, so its first timings are higher. The run writes two
demonstration files, a million readings and a hundred thousand visits, and deletes them when it
ends. They use these three records:

```csharp
[VortexRecord] public partial record struct Reading(int Day, double? Celsius, string City);
[VortexRecord] public partial record struct Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin);
[VortexRecord] public partial record struct Address(string Country, string? City);
```

The [design documents](../design/README.md) live beside this directory. They explain why the format
and this implementation are shaped the way they are. A page here links to one when you want the
depth, and does not repeat it.

## Start here

| page | what you get |
|---|---|
| [getting-started.md](getting-started.md) | reference the packages, declare a record, write a file, read a column back |

## Reading

| page | what you get |
|---|---|
| [open-a-file.md](open-a-file.md) | open a file from a path or from a source you choose, and the session every open runs in |
| [scan-a-table.md](scan-a-table.md) | read every batch with `await foreach`, as borrowed columns that cost no allocation |
| [project-columns.md](project-columns.md) | read three columns out of fifty and pay for three: the record is the projection |
| [filter-rows.md](filter-rows.md) | a predicate written as a lambda and pushed down, so whole blocks are never read |
| [nullable-columns.md](nullable-columns.md) | the values and the validity of a column with nulls, read word by word or summed by the engine |
| [text-columns.md](text-columns.md) | text as UTF-8 spans without a `string`, and distinct counts without a loop |
| [encoded-forms.md](encoded-forms.md) | a dictionary or run-end column read as it is stored, grouped by code or by run |
| [selection.md](selection.md) | a filtered block delivered whole with the rows that passed, instead of a copy |
| [aggregates.md](aggregates.md) | several answers in one pass, group bys, and an aggregator of your own |
| [queries.md](queries.md) | group bys as queries, their groups filtered, ordered and cut, and groups delivered as soon as they are final |

## Going further on reading

| page | what you get |
|---|---|
| [keys-in-order.md](keys-in-order.md) | batches in key order, and a cursor that seeks, steps, ranks and counts |
| [read-rows.md](read-rows.md) | one record per row when rows are what you need, and where LINQ takes over |
| [read-rows-by-index.md](read-rows-by-index.md) | row 4 and row 900 000 without decoding what lies between |
| [threads.md](threads.md) | what a session shares, local and remote sources, and when parallelism pays |
| [owned-batches.md](owned-batches.md) | batches you keep, hand to another thread and dispose there |
| [nested-records.md](nested-records.md) | struct columns as nested records, and list columns as spans of their elements |
| [statistics-and-pruning.md](statistics-and-pruning.md) | the plan before a scan, its numbers after, and what makes pruning work |
| [cancel-work.md](cancel-work.md) | how cancellation reaches a scan, a write and a cursor |

## Writing

| page | what you get |
|---|---|
| [write-a-file.md](write-a-file.md) | fill the builder's buffers in place, write, complete |
| [write-rows.md](write-rows.md) | write an array or an asynchronous stream of records |
| [write-text.md](write-text.md) | text from UTF-8 bytes, from strings, formatted in place, or written straight into the buffer |
| [write-nulls.md](write-nulls.md) | nulls one at a time or as a bitmap, and why a column without any costs nothing |
| [write-lists-and-records.md](write-lists-and-records.md) | lists in one call or element by element, and nested records |
| [blocks-and-chunks.md](blocks-and-chunks.md) | what blocks and chunks are, and how to write a file that appends cheaply |
| [append-and-repair.md](append-and-repair.md) | add rows to a file, give a write up, and repair a file whose tail was torn |
| [writer-options.md](writer-options.md) | compression, encoding hints, text bounds, metadata, identity, and the report of what the writer chose |
| [choose-encodings.md](choose-encodings.md) | what each profile and hint costs, and the storage speeds at which the best choice changes |
| [indexes.md](indexes.md) | the indexes you can ask for, what each costs, and the index budget |
| [editions.md](editions.md) | target an edition so that older readers, Vortex Rust among them, can read your files |
| [stream-to-an-object.md](stream-to-an-object.md) | write to any `PipeWriter`, a socket or a multipart upload, with backpressure |
| [copy-a-file.md](copy-a-file.md) | copy, filter, project or re-encode a file, each column decoded and encoded once |

## Records and the tool path

| page | what you get |
|---|---|
| [records.md](records.md) | what `[VortexRecord]` generates, the attributes that shape a column, and records written by hand |
| [diagnostics.md](diagnostics.md) | the analyzer diagnostics VX1001 to VX1011 and how to fix the code each one points at |
| [untyped-files.md](untyped-files.md) | read a file whose schema you only learn at run time: columns by name, filters as text |
| [write-without-a-record.md](write-without-a-record.md) | write from a schema built at run time, with builders asked for by name |
| [vxdump.md](vxdump.md) | look inside a file: schema, layout tree, encodings, segments, statistics, indexes |

## Experimental packages

Both packages are marked `[Experimental]`. Using them reports VX0001 for the dataset and VX0002 for
the row encoding, which a project acknowledges once before it builds.

| page | what you get |
|---|---|
| [datasets.md](datasets.md) | a versioned dataset over an object store: append, import, scan, delete and update rows, change the schema |
| [dataset-maintenance.md](dataset-maintenance.md) | compaction, vacuum and verification, and what each costs in requests |
| [object-store.md](object-store.md) | implement the store interface for your service, and open a single file from a store |
| [row-keys.md](row-keys.md) | encode a tuple into bytes whose `memcmp` order is the tuple's order |

## Reference

| page | what you get |
|---|---|
| [errors.md](errors.md) | the exceptions the library raises, what each one means, and which ones are your fault |
| [limits.md](limits.md) | the caps that protect a reader from a hostile file, and the options for input you do not trust |
| [observability.md](observability.md) | metrics, activities, event counters and per-scan statistics, and what each number tells you |
| [native-aot.md](native-aot.md) | publish an application that uses the library ahead of time |
| [how-it-works.md](how-it-works.md) | the shape of the library in two pages, and where to read further |
| [benchmarks.md](benchmarks.md) | every figure the bench publishes: Vorticity against the Rust implementation, and each kernel against its baseline |
| [benchmarks-x64.md](benchmarks-x64.md) | the same measurements on an x64 machine, a Zen 4 processor under Windows, against a Rust built there |
| [benchmarks-parquet-x64.md](benchmarks-parquet-x64.md) | how fast Parquet files read and write, against the same rows as Vortex, on one thread and on 32, on the same machine |
| [benchmarks-duckdb.md](benchmarks-duckdb.md) | our group bys against DuckDB's on the same files, through its Vortex reader and from its own table |
