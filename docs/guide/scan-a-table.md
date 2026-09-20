# Scan a table

Read every row, in batches, without copying.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
long rows = 0;
await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
{
    using (batch)
    {
        rows += batch.RowCount;
    }
}
```

`Scan()` returns a builder. Nothing is read until `ExecuteAsync()` is enumerated, and the builder is
where projection, filter, row selection, batch size and metrics are set — each returns the same
builder, so they chain.

## What comes back

A `RecordBatch` is a window of rows over the decoded columns. It carries its own schema, its
`RowCount`, and `StartRow`, the file row its first row is. Columns come out of it by name or by
index:

```csharp
ReadOnlySpan<double> values = batch.Column("celsius"u8).AsPrimitive<double>().Values;
```

`Values` is the decoded buffer, not a copy of it: reading a column allocates nothing.

On the million-row file, the default scan gives 123 batches of at most 8 192 rows. That ceiling is
the file's block size, not a constant; `WithMaxBatchRows(4_096)` halves it to 245 batches. Asking
for more than a block holds does not merge blocks.

## Who owns what

**Dispose every batch.** Its buffers go back to the pool, and the next batch takes them. A scan
whose batches are not disposed holds every buffer it ever decoded.

**Nothing a batch gave you survives it.** A column, a span, a string span: all of them point into
the batch's memory, and the batch is recycled the moment it is disposed. Copy what you need to keep.

**A column may not cross an `await`.** `VortexColumn` and its typed forms are `ref struct`s, so the
compiler refuses:

```
error CS4007: Instance of type 'Vorticity.Columns.VortexColumn' cannot be preserved
              across 'await' or 'yield' boundary.
```

Holding one inside the loop body is fine. Awaiting while holding one is not, and the compiler says
so rather than letting you read recycled memory.

## What it costs the file

```csharp
ScanMetrics metrics = new ScanMetrics();
await foreach (RecordBatch batch in file.Scan().WithMetrics(metrics).ExecuteAsync())
{
    batch.Dispose();
}

Console.WriteLine($"{metrics.Batches} batches, {metrics.ValuesDecoded} values decoded");
```

`ScanMetrics` is a counter you hand in and read afterwards; it costs an interlocked add per event.

The reads underneath are worth knowing about. Scanning the million-row 1 523 369-byte file asked
its source for **124 rounds totalling 184 725 367 bytes** — 121 times the file. A segment holds many
blocks, and it is fetched again for every block that needs it; nothing caches it between batches.
Over a memory map or over bytes in memory that repetition is free, which is why
[open-a-file.md](open-a-file.md) recommends a mapping for a file you read more than once. Over a
source where a read is a request, put a cache in front of it.

## Counting without decoding

```csharp
long rows = await file.Scan().CountAsync();
```

With no filter this is the footer's row count and reads nothing. With a filter it is what
[filter-rows.md](filter-rows.md) describes: block statistics first, rows only where they cannot
decide.

## Watch out

* The batches arrive in file order. There is no parallel enumeration to opt into here;
  `WithDegreeOfParallelism` parallelises the decoding under one enumerator, and
  `threads.md` says what that changes.
* Breaking out of the loop stops the scan. Any batch already yielded is still yours to dispose.
* A scan does not see a write that happened after the file was opened. Open again for that.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- scan-a-table
```
