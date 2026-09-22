# Errors

The exceptions this library throws, what each one means, and which are your fault.

```csharp
await Show("512 zero bytes", async () =>
{
    string broken = Demo.Path("broken.vortex");
    await System.IO.File.WriteAllBytesAsync(broken, new byte[512]);
    await using VortexFile opened = await VortexFile.OpenAsync(broken);
});

await Show("a record member the file has no column for", async () =>
    await file.Scan<Humidity>().CountAsync());

await Show("a key cursor over a column nothing orders", async () =>
{
    await using KeyCursor<double?> cursor = await file.Scan<Reading>().Keys(r => r.Celsius).OpenAsync();
});
```

```
512 zero bytes -> VortexFormatException: Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.
a record member the file has no column for -> VortexSchemaException: Member 'Percent' of Humidity has no column; the columns are 'Day', 'Celsius', 'City'.
a key cursor over a column nothing orders -> VortexUnsupportedException (Index, vorticity.sorted.runs.v1): Unsupported Vortex component: index 'vorticity.sorted.runs.v1'. 'Celsius' has no key source (SortedColumn: the file statistics say the column is not sorted; SortedRuns: the file carries no index directory; …). A cursor over an unindexed column would have to hold the column to sort it, which this library refuses. Write the file with IndexPolicy.SortedRuns for that column, …
```

`Show` runs the call and prints the exception's type and message; the sample runs sixteen cases
this way.

## The hierarchy

Every exception the library throws on purpose derives from `VortexException`:

| | means | whose fault |
|---|---|---|
| `VortexFormatException` | the bytes are not what they claim to be, or a limit says no | the file's, or yours for pointing at the wrong bytes; never worth retrying |
| `VortexUnsupportedException` | the input is well formed and asks for something this build does not do | nobody's: a newer library, another edition, or an index the file lacks |
| `VortexSchemaException` | a record, a builder, a column request or a literal does not fit the schema | yours, and the message says what to change |

What stays outside it is plain .NET: an argument out of range is an `ArgumentException`, misuse is
an `InvalidOperationException`, a missing file is a `FileNotFoundException`. The object store of
[object-store.md](object-store.md) adds two of its own, `ObjectStoreException` and
`ObjectNotFoundException`, both `VortexException`s too.

## `VortexFormatException`

A file that does not parse: bad offsets, a truncation, a field out of range, a nesting deeper than
a cap allows, a block that would decode to more than `MaxDecompressedSize`. It is the half of the
promise about hostile input that says a malformed file produces this exception, or
`VortexUnsupportedException`, and never an out-of-bounds read, an unbounded allocation or a hang.
[limits.md](limits.md) lists the caps and shows a thousand damaged files producing nothing else.

A torn append is one too, when you ask for it: by default an open falls back to the last whole
version of the file, and `VortexTornTailPolicy.Refuse` makes it throw instead. See
[append-and-repair.md](append-and-repair.md).

## `VortexUnsupportedException`

The file, or the request, needs a component this build does not implement. The exception names
it, in two parts that together say which edition introduced it:

```csharp
catch (VortexUnsupportedException e)
{
    Console.WriteLine($"{what} -> {e.GetType().Name} ({e.Kind}, {e.ComponentId}): {First(e.Message)}");
}
```

```
a uuid column written to an edition older than uuid -> VortexUnsupportedException (DType, vortex.uuid): Unsupported Vortex component: dtype 'vortex.uuid'. The write targets edition core2025.05.0, which does not contain it; it was introduced in core2026.08.3.
```

`Kind` is a `ComponentKind`: `Array` or `Layout` for an encoding, `DType` for a column type,
`Aggregate` for a zone-map statistic, `Compression` or `Encryption` for a segment scheme, `Index`
for an index kind or an index a request needs, and `Feature` for something the library does not
offer on this input, such as an append over a layout it cannot resume. `ComponentId` is the id as
the file spells it. `VortexEditions.IntroducedIn(kind, id)` answers the edition question in code;
[editions.md](editions.md) says how to write for older readers.

An unknown encoding does not stop a file from opening: the refusal comes from the first block that
needs it, so a scan that does not read that column succeeds. [limits.md](limits.md) shows one.

## `VortexSchemaException`

The schema is a contract checked in one place, where a record or a builder meets a file, and the
message names the member, the column and both types:

```
a non-nullable member over a nullable column -> VortexSchemaException: Member 'Celsius' of RequiredCelsius is not nullable and its column 'Celsius' is (f64?); declare the member nullable.
a text literal against an integer column -> VortexSchemaException: Column 'Day' is a column of i32, which System.String does not map to: a string reads a utf8 column.
a column the file does not have, by name -> VortexSchemaException: The file has no column 'nope'; its schema is struct{Day: i32, Celsius: f64?, City: utf8}.
a nullable builder over a non-nullable column -> VortexSchemaException: Column 'Day' is i32, which is not nullable; ask for a builder of Int32 instead of Nullable`1.
```

On the typed path most of these cannot be written at all: a literal of the wrong type does not
compile, and a record binds once, at the first sink of a scan. On the tool path, where names and
literals are data, the check happens when `Where` is called or the column is asked for, before
anything is read. [records.md](records.md#binding-a-record-to-a-file) says how members find their
columns.

## The .NET ones

```
a filter that does not parse -> FormatException: The filter ends where a literal or a column after the operator was expected.
a missing file -> FileNotFoundException: Could not find file '…/no-such-file.vortex'.
a row index past the end -> ArgumentOutOfRangeException: The file has 1000000 rows. (Parameter 'rows')
a scan used twice -> InvalidOperationException: A scan is single-use: build another one for another sink.
an owned batch read after it is disposed -> ObjectDisposedException: This RecordBatch has been disposed; every span borrowed from it is invalid.
a session disposed while one of its files is open -> InvalidOperationException: The session still has '…/readings.vortex' open; dispose every file before the session.
```

| | when |
|---|---|
| `FormatException` | a filter string for the tool path, `VortexExpr.Parse`, that does not parse |
| `FileNotFoundException`, `IOException` | no file at the path, or one the operating system will not open |
| `ArgumentOutOfRangeException` | a row index below zero or past the last row; a negative option |
| `InvalidOperationException` | a scan run twice, `Rows` by range and by indices on one scan, a key cursor on a filtered scan, a session disposed before its files, an option set after `VortexSession.Create` returned |
| `ObjectDisposedException` | an owned batch used after `Dispose`, a disposed file asked for what it no longer holds |
| `OperationCanceledException` | a token you passed was cancelled: see [cancel-work.md](cancel-work.md) |

## Watch out

* **A row range past the end is clamped, a row index past the end throws.**
  `Rows(new RowRange(0, rows + 10))` counts 1 000 000 rows on the demonstration file, without an
  exception.
* **A disposed `VortexFile` answers what its open captured and refuses the rest.** `RowCount`,
  `Schema` and `Statistics` go on answering; `Identity` and `SegmentMap` throw
  `ObjectDisposedException`, because they read the tail buffer the file returned to the pool.
  `Scan<T>()` still builds a scan, which throws `ObjectDisposedException` at its first read.
  Treat a disposed file as gone.
* **Each scan is single-use.** Build a new one per question; building one costs nothing that is
  worth caching.
* VX1003 and VX1004 catch some of these at compile time: see [diagnostics.md](diagnostics.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- errors
```
