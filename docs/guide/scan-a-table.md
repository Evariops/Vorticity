# Scan a table

Read every row, in batches, without copying, and know when not to write the loop at all.

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
the loop: mean 29.4001 over 1000000 rows, 2.9 ms
  17 batches, 123 blocks decoded, 51 requests, 1494044 bytes
```

That mean is wrong, and on purpose: `Celsius` is nullable, and `Values` is the raw buffer, which
holds something meaningless at each of the 20 000 null slots. The right mean is 30.0000.
[nullable-columns.md](nullable-columns.md) has the two ways to read around the nulls; the end of
this page has the way that does not need a loop.

## What comes back

`file.Scan<Reading>()` returns a `Scan<Reading>`, a builder: `Where`, `Rows`, `OrderBy` and `With`
compose it, and nothing is read until a sink runs. `await foreach` is the sink that hands you each
batch as a `Columns<Reading>`, which deconstructs into one `Column<T>` per member of the record, in
declaration order; `_` skips a member. `Columns<Reading>` also carries `RowCount`, `StartRow` (the
file row of its first row) and `Selection` ([selection.md](selection.md)).

`celsius.Values` is a `ReadOnlySpan<double>` over the decoded buffer itself, 64-byte aligned, so
reading a column copies nothing. A scan that only reads, like this one, delivers up to sixteen of
the file's 8 192-row blocks per batch, the rows it decodes at once, and never goes past the end of
a chunk: each chunk of this file is one batch, 17 for a million rows. A scan with a filter, an
order or a take holds one block per batch. `ScanOptions.BatchRows` asks for smaller ones and never
for more:

```csharp
await foreach (Columns<Reading> columns in file.Scan<Reading>().With(new ScanOptions { BatchRows = 4_096 }))
```

```
BatchRows 4096: 245 batches, 1000000 rows, the last starting at row 999424
```

## Who owns what

**A batch is borrowed.** `Current` is valid until the next `MoveNextAsync`: the buffers go back to
the scan and the next batch decodes into them. `Columns<T>` and `Column<T>` are `ref struct`s, so
the compiler refuses to let one, or a span taken from one, live across an `await`:

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

The body of the loop runs synchronously between two batches, which is what makes a `ref struct`
legal there. Do the columnar work, then await what you must, then let the loop move on. To keep a
batch past its iteration, copy it once with `ToOwned()`, or ask for owned batches with
`ToBatchesAsync()` ([owned-batches.md](owned-batches.md)).

**A scan is single-use.** One sink per builder: a second throws `InvalidOperationException`.
`ExplainAsync` may be asked before the sink, and `Statistics` read after it. The sinks, and what
each one costs, are tabled in §5.6 of [14-public-api.md](../design/14-public-api.md).

## Or let the scan do it

```csharp
double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);
```

```
AvgAsync: mean 30.0000, 1.7 ms
  1 blocks decoded, 17 requests, 1150428 bytes
```

The right answer, faster, and no batch ever reaches your code: the aggregate runs block by block
inside the scan, skips the nulls, and reads the one column it needs, 17 segments of the 51. The
loop above read three, because the record names three: [project-columns.md](project-columns.md) is
how to name fewer. For an aggregate, `BlocksDecoded` counts the blocks it had to bring to the
canonical form; it folded the other 122 in the form they are stored in
([aggregates.md](aggregates.md)).

A file this library writes carries, per column, a null count, order flags and, for a numeric
column, a minimum and a maximum; it carries no sum. `MinAsync`, `MaxAsync` and `CountAsync`
without a filter answer from those statistics and read nothing; `SumAsync` and `AvgAsync` read
the column. [aggregates.md](aggregates.md) has the rest of the operators.

## What it costs

* **Reads.** `Statistics.Requests` counts the segments the scan read: 51 for three columns over
  17 batches, 1.49 MB for a file of 1.51 MB, because a segment that spans several batches is read
  once and shared by them. A second scan reads them all again; over a source where a read is a
  request, [open-a-file.md](open-a-file.md) shows what a `SegmentCache` on the session saves.
* **Memory.** One batch is decoded ahead of the one you hold (`ScanOptions.Prefetch`, 1 by
  default), in buffers that alternate rather than accumulate.
* **Allocations.** A whole scan allocated 43 120 bytes over its 17 batches, and 43 584 bytes over
  123 batches of a block as over 245 of half a block: a scan pays for its start and then nothing
  per batch, a little less when each batch holds a whole chunk, which then need not stay decoded
  from one batch to the next. Counted process-wide with `GC.GetTotalAllocatedBytes`, so the thread
  that decodes ahead is included.

## Watch out

* Batches arrive in file order. [keys-in-order.md](keys-in-order.md) has the key-ordered scan.
* Breaking out of the loop ends the scan and returns its buffers.
* A scan does not see rows appended after the file was opened. Open it again for that.
* Parallelism is the session's, and pays on aggregates and wide decodes rather than on a loop like
  this one ([threads.md](threads.md)).

The figures come from one run of the sample on the demonstration file of a million rows; the
timings are the best of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- scan-a-table
```
