# The guide

Pages named by what you are trying to do. Each one is short, opens with a working example, and says
what happens, what to watch out for, and what it costs.

Every example is a program in [samples/Vorticity.Samples](../../samples/Vorticity.Samples),
compiled by the build and run by the page's name, and the figures a page quotes are what its program
measured:

```
dotnet run -c Release --project samples/Vorticity.Samples -- filter-rows
```

With no argument it runs every case, and the numbers a page quotes come from such a Release run: a
page's case runs after the others, on code they have already compiled, where a case run alone also
pays for compiling what it runs first. The run writes two demonstration files and deletes them when
it ends: a million readings and a hundred thousand visits, of these three records.

```csharp
[VortexRecord] public partial record struct Reading(int Day, double? Celsius, string City);
[VortexRecord] public partial record struct Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin);
[VortexRecord] public partial record struct Address(string Country, string? City);
```

The design documents are a different thing and live beside this directory: they say why the format
and this implementation are shaped as they are. A page here points at one when you want the depth,
and never repeats it.

## Start here

| page | what you get |
|---|---|
| [getting-started.md](getting-started.md) | reference the library and its generator, declare a record, write a file, read a column back |

## Reading

| page | what you get |
|---|---|
| [open-a-file.md](open-a-file.md) | open from a path, which maps the file, or from a source you choose, and the session every open runs in |
| [scan-a-table.md](scan-a-table.md) | every batch of a file with `await foreach`, as borrowed columns that cost no allocation |
| [project-columns.md](project-columns.md) | three columns of fifty, paying for three: the record is the projection |
| [filter-rows.md](filter-rows.md) | a predicate written as a lambda and pushed down, so that whole blocks are never read; what does not push down does not compile |
| [nullable-columns.md](nullable-columns.md) | the values and the validity of a column with nulls, read word by word, or summed by the engine |
| [text-columns.md](text-columns.md) | text as UTF-8 spans without a `string`, and a distinct count that is not a loop |
| [encoded-forms.md](encoded-forms.md) | a dictionary or run-end column read as it is stored, grouped by code or by run with nothing decoded |
| [selection.md](selection.md) | a filtered block delivered whole with the rows that passed, instead of a copy of them |
| [aggregates.md](aggregates.md) | several answers in one pass, a group by, and an aggregator of your own |

## Going further on reading

| page | what you get |
|---|---|
| [keys-in-order.md](keys-in-order.md) | batches in the order of a key, and a cursor that seeks, steps, ranks and counts keys |
| [read-rows.md](read-rows.md) | one record per row when rows are what you need, and where LINQ starts |
| [read-rows-by-index.md](read-rows-by-index.md) | row 4 and row 900 000 without decoding what lies between |
| [threads.md](threads.md) | what a session shares, a local file against a remote one, and when parallelism pays |
| [owned-batches.md](owned-batches.md) | batches you keep, hand to another thread and dispose there, and what the copy costs |
| [nested-records.md](nested-records.md) | a struct column as a nested record, and a list column as spans of its elements |
| [statistics-and-pruning.md](statistics-and-pruning.md) | the plan before a scan and its numbers after: which blocks were skipped, and by what |
| [cancel-work.md](cancel-work.md) | how cancellation reaches a scan, a write and a cursor, and where it takes effect |

## Writing

| page | what you get |
|---|---|
| [write-a-file.md](write-a-file.md) | columns in, one file out: fill the builder's buffers in place, write, complete |
| [write-rows.md](write-rows.md) | write an array or an asynchronous stream of records |
| [write-text.md](write-text.md) | text from UTF-8 bytes, from strings, formatted in place, or written straight into the buffer |
| [write-nulls.md](write-nulls.md) | nulls one at a time or as a bitmap, and why a column with none costs nothing |
| [write-lists-and-records.md](write-lists-and-records.md) | a list in one call or element by element, and a nested record |
| [blocks-and-chunks.md](blocks-and-chunks.md) | what a block and a chunk are, and the write sizes that make a file append-friendly |
| [append-and-repair.md](append-and-repair.md) | add rows to a file, give a write up, and read or repair a file whose tail was torn |
| [writer-options.md](writer-options.md) | compression, encoding hints, text bounds, metadata, a pinned identity, and the report that says what the writer chose |
| [choose-encodings.md](choose-encodings.md) | what each profile and hint costs on ten million rows of twenty column shapes, and the storage speeds at which the choice turns |
| [indexes.md](indexes.md) | the indexes you can ask for, what each costs, the budget, and an index the write must not lose |
| [editions.md](editions.md) | target an edition so that older readers, and Vortex Rust, can read what you write |
| [stream-to-an-object.md](stream-to-an-object.md) | write to any `PipeWriter`, a socket or a multipart upload, with its backpressure |
| [copy-a-file.md](copy-a-file.md) | copy, filter, project or re-encode a file, each column decoded once and encoded once |

## Records and the tool path

| page | what you get |
|---|---|
| [records.md](records.md) | what `[VortexRecord]` generates, the attributes that shape a column, and a record written by hand |
| [diagnostics.md](diagnostics.md) | what the analyzers flag, VX1001 to VX1008, and how to fix the code each one points at |
| [untyped-files.md](untyped-files.md) | read a file whose schema you learn at run time: columns by name, filters as text |
| [write-without-a-record.md](write-without-a-record.md) | write from a schema built at run time, through the same builders under string names |
| [vxdump.md](vxdump.md) | look inside a file: schema, layout tree, encodings, segments, statistics, indexes |

## Experimental packages

Both are marked `[Experimental]` on the assembly: every use reports VX0001 for the dataset or
VX0002 for the row encoding, which a project acknowledges before it builds.

| page | what you get |
|---|---|
| [datasets.md](datasets.md) | a versioned dataset over an object store: create, append, import, scan, commit |
| [dataset-maintenance.md](dataset-maintenance.md) | compact, vacuum and verify, and what each costs in requests |
| [object-store.md](object-store.md) | implement the store seam for the service you use, and open a single file out of one |
| [row-keys.md](row-keys.md) | encode a tuple into bytes whose `memcmp` order is the tuple's order |

## Reference

| page | what you get |
|---|---|
| [errors.md](errors.md) | the exceptions this library raises, what each one means, and which ones are your fault |
| [limits.md](limits.md) | the caps that protect a reader from a hostile file, and the options for input you do not trust |
| [observability.md](observability.md) | the meter, the activities and the per-scan statistics, and what each number diagnoses |
| [native-aot.md](native-aot.md) | publish an application that uses this library ahead of time |
| [how-it-works.md](how-it-works.md) | the shape of the library in two pages, and where to read further |
| [benchmarks.md](benchmarks.md) | every figure the bench publishes: what this costs against the Rust implementation, and each kernel against the loop it replaced |
