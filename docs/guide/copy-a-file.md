# Copy a file

Copy a file batch by batch, keep only the rows or the columns you want, or write it again under
other options.

```csharp
await using VortexFile source = await session.OpenAsync(input);

await using (VortexFileWriter target = session.CreateWriter(output, source.Schema))
{
    await foreach (BatchView batch in source.Scan()) await target.WriteAsync(batch, ct);   // the columns decode once and encode once
    report = await target.CompleteAsync(ct);
}
```

The source is the demonstration file of a million readings. `source.Scan()` with no column names
reads every column, and `WriteAsync(BatchView)` takes each batch as it comes: a columnar
pass-through, with no rows and no builder in between.

## The variants

The same loop with a filter is a filtered copy, with a record a projected one, and under other
options a rewrite:

```csharp
await using (VortexFileWriter target = session.CreateWriter(output, source.Schema, new VortexWriteOptions { Compression = CompressionProfile.Smallest }))
{
    await foreach (BatchView batch in source.Scan()) await target.WriteAsync(batch, ct);
    report = await target.CompleteAsync(ct);
}

await foreach (BatchView batch in source.Scan().Where($"Day >= {from}")) await target.WriteAsync(batch, ct);

await using (VortexFileWriter target = session.CreateWriter<Reading>(output))
{
    await foreach (Columns<Reading> columns in source.Scan<Reading>().Where(r => r.Day >= from && r.City == "Paris")) await target.WriteAsync(columns, ct);
    report = await target.CompleteAsync(ct);
}

await using (VortexFileWriter target = session.CreateWriter<DayAndCelsius>(output))
{
    await foreach (Columns<DayAndCelsius> columns in source.Scan<DayAndCelsius>()) await target.WriteAsync(columns, ct);
    report = await target.CompleteAsync(ct);
}
```

`DayAndCelsius` is `[VortexRecord] public partial record struct DayAndCelsius(int Day, double? Celsius)`:
the record is the projection, so the scan reads two columns and the target has two.

## What it costs

```
source: 1000000 rows, 1564708 bytes
a copy: 1000000 rows, 1506964 bytes, 25 chunks, in 25 ms
re-encoded under Smallest: 1285228 bytes, in 48 ms; Day Zstd x25, Celsius Alp x25, City Zstd x25
a filtered copy, Day >= 900: 100000 rows, 158148 bytes, in 2 ms
a typed filtered copy, Day >= 900 and City == Paris: 12502 rows, 19932 bytes, in 1 ms
a projected copy, Day and Celsius: 1000000 rows, 1154876 bytes, in 12 ms
the projected copy reads back: struct{Day: i32, Celsius: f64?}, 1000000 rows, mean 30.0000
```

The times are from a run in which earlier samples had already compiled the code paths. Each column
is decoded once and encoded once; the target chooses its encodings afresh and cuts its own chunks.
The copy came out 57 KB smaller than its source, in 25 chunks against 50, because the source was
written one block at a time ([blocks-and-chunks.md](blocks-and-chunks.md)); a copy is also how a file
that has taken many appends gets its space back ([append-and-repair.md](append-and-repair.md)).
`Smallest` made it 18 % smaller than the source, for more time spent writing. A filtered copy reads
only the blocks the filter keeps, by the same pruning as any scan ([filter-rows.md](filter-rows.md)).

## Watch out

* **The target must not be the source.** The source is open, and a writer over the same path throws
  `IOException`: *The process cannot access the file … because it is being used by another process.*
  Write elsewhere and move the file once the copy completes.
* **A record written as columns covers every column of the target.** Three columns into a writer of
  two throws `VortexSchemaException`: *Member 'City' of Reading has no column to write; the struct is
  struct{Day: i32, Celsius: f64?}.*
* **The types must be the target's.** A batch is refused when a column's type differs from the
  target's; a non-nullable column may go into a nullable one.
* **A batch is borrowed** until the task `WriteAsync` returns completes: await it before the next
  batch, as the loop does.
* The copy takes the target's options, not the source's: indexes, statistics, edition and metadata
  are what you pass to `CreateWriter` ([writer-options.md](writer-options.md), [indexes.md](indexes.md)).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- copy-a-file
```

The figures above come from that run.
