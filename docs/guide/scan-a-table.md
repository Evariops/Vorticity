# Scan a table

This page reads every row in batches, without copying, and shows when you should not write the loop
at all.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);

Scan<Reading> scan = file.Scan<Reading>();
double total = 0;
long seen = 0;
await foreach (var (_, celsius, _) in scan)
{
    foreach (double value in celsius.Values)
    {
        total += value;
    }

    seen += celsius.Length;
}
```

```
the loop: mean 29.4001 over 1000000 rows, 4.3 ms
  17 batches, 123 blocks decoded, 51 requests, 1494068 bytes
```

That mean is wrong on purpose. `Celsius` is nullable, and `Values` is the raw buffer, which holds
meaningless values at the 20 000 null slots. The right mean is 30.0000.
[nullable-columns.md](nullable-columns.md) shows two ways to read around the nulls, and the end of
this page shows the way that needs no loop.

## What comes back

`file.Scan<Reading>()` returns a `Scan<Reading>`, which is a builder. `Where`, `Rows`, `OrderBy` and
`With` shape it, and nothing is read until a sink runs. `await foreach` is the sink that hands you each
batch as a `Columns<Reading>`. It deconstructs into one `Column<T>` per member of the record, in
declaration order, and `_` skips a member. `Columns<Reading>` also carries `RowCount`, `StartRow` (the
file row of its first row) and `Selection` ([selection.md](selection.md)).

`celsius.Values` is a `ReadOnlySpan<double>` over the decoded buffer itself, aligned on 64 bytes, so
reading a column copies nothing. A scan that only reads delivers up to sixteen of the file's
8 192-row blocks per batch and never crosses the end of a chunk. In this file each chunk is one
batch, 17 for a million rows. A scan with a filter, an order or a take delivers one block per batch.
`ScanOptions.BatchRows` asks for smaller batches, never for larger ones:

```csharp
await foreach (Columns<Reading> columns in file.Scan<Reading>().With(new ScanOptions { BatchRows = 4_096 }))
```

```
BatchRows 4096: 245 batches, 1000000 rows, the last starting at row 999424
```

## Who owns what

A batch is borrowed. `Current` stays valid until the next `MoveNextAsync`, after which its buffers go
back to the scan and the next batch is decoded into them. `Columns<T>` and `Column<T>` are
`ref struct`s, so the compiler refuses to let one of them, or a span taken from one, live across an
`await`:

```csharp
await foreach (var (day, _, _) in file.Scan<Reading>())
{
    await Task.Yield();
    Console.WriteLine(day.Length);
}
```

```
error CS4007: Instance of type 'Vorticity.Column<int>' cannot be preserved across 'await' or 'yield' boundary.
```

The loop body runs synchronously between two batches, which is what makes a `ref struct` legal there.
Do the columnar work first, then await what you need to, then let the loop move on. To keep a batch
beyond its iteration, copy it once with `ToOwned()`, or ask for owned batches with `ToBatchesAsync()`
([owned-batches.md](owned-batches.md)).

A scan is single-use. Each builder accepts one sink, and a second one throws
`InvalidOperationException`. You may call `ExplainAsync` before the sink and read `Metrics` after
it. The sinks and what each one costs are listed in
[the public API design](../design/14-public-api.md#56-the-sinks).

## Or let the scan do it

```csharp
double? mean = await file.Scan<Reading>().AverageAsync(r => r.Celsius);
```

```
AverageAsync: mean 30.0000, 2.1 ms
  1 blocks decoded, 17 requests, 1150428 bytes
```

You get the right answer, faster, and no batch ever reaches your code. The aggregate runs block by
block inside the scan, skips the nulls, and reads only the column it needs, 17 segments out of 51.
The loop above read three columns because the record names three, and
[project-columns.md](project-columns.md) shows how to name fewer. For an aggregate,
`BlocksDecoded` counts the blocks it had to bring to their plain form. It folded the other 122 in
the form they are stored in ([aggregates.md](aggregates.md)).

A file written by this library carries, per column, a null count, order flags and, for a numeric
column, a minimum and a maximum. It does not carry a sum. So `MinAsync`, `MaxAsync` and `CountAsync`
without a filter are answered from those statistics and read nothing, while `SumAsync` and
`AverageAsync` read the column. [aggregates.md](aggregates.md) covers the other operators.

## What it costs

* Reads: `Statistics.Requests` counts the segments the scan read, 51 for three columns over 17
  batches, which is 1.49 MB for a file of 1.51 MB. A segment that spans several batches is read once
  and shared. A second scan reads everything again. Over a source where each read is a request,
  [open-a-file.md](open-a-file.md) shows what a `SegmentCache` on the session saves.
* Memory: one batch is decoded ahead of the one you hold (`ScanOptions.Prefetch`, 1 by default), in
  buffers that alternate rather than pile up.
* Allocations: a whole scan allocated 6 944 bytes over its 17 batches, and 7 032 bytes over 123
  batches of one block or 245 batches of half a block. A scan pays for its start and then nothing per
  batch, slightly less when each batch holds a whole chunk because the chunk then does not need to
  stay decoded from one batch to the next. These numbers come from `GC.GetTotalAllocatedBytes` for
  the whole process, so they include the thread that decodes ahead.

## Watch out

* Batches arrive in file order. [keys-in-order.md](keys-in-order.md) covers scans in key order.
* Breaking out of the loop ends the scan and returns its buffers.
* A scan does not see rows appended after the file was opened. Open the file again for that.
* Parallelism comes from the session. It pays on aggregates and wide decodes much more than on a
  loop like this one ([threads.md](threads.md)).

The figures come from one run of the sample on the demonstration file of a million rows. The timings
are the third of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- scan-a-table
```
