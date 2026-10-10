# Owned batches

Hand batches to another thread, a channel or a later step, and know what the copy costs.

```csharp
Channel<RecordBatch> channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(4) { SingleWriter = true, SingleReader = true });

Task producer = Task.Run(async () =>
{
    try
    {
        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(ct))
        {
            try
            {
                await channel.Writer.WriteAsync(batch, ct);
            }
            catch
            {
                batch.Dispose();
                throw;
            }
        }

        channel.Writer.Complete();
    }
    catch (Exception e)
    {
        channel.Writer.Complete(e);
    }
});

await foreach (RecordBatch batch in channel.Reader.ReadAllAsync(ct))
{
    using (batch)
    {
        Columns<Reading> cols = batch.As<Reading>();
        total += Sum(cols.Celsius);
        valid += cols.Celsius.Length - cols.Celsius.NullCount;
        rows += cols.RowCount;
        batches++;
    }
}

await producer;
```

```
through a channel: 17 batches, 1000000 rows, mean 30.00 degrees
```

The columns of an ordinary `await foreach` are borrowed. They are valid until the loop moves on, and
the compiler keeps them inside the loop body ([scan-a-table.md](scan-a-table.md)). A pipeline needs
the opposite: a batch that outlives the loop and crosses threads. `ToBatchesAsync` gives you that.
Each `RecordBatch` is owned by whoever holds it, and whoever holds it last disposes it.

## What an owned batch is

A `RecordBatch` has a `Schema`, a `RowCount` and a `StartRow`, and two ways in:

* `batch.As<Reading>()` gives the same `Columns<Reading>` a typed scan gives, with the generated
  members (`cols.Celsius`), valid until the batch is disposed.
* `batch.View` gives a `BatchView`, with columns by index or by name, for code that has no record
  type ([untyped-files.md](untyped-files.md)).

After `Dispose`, both throw `ObjectDisposedException`, as does every other member. A batch that is
never disposed keeps its buffers until a collection finalizes it, and they then go back to the system
rather than to the pool. To keep such batches from piling up, the library requests a background
collection once the batches held by callers reach 256 MiB, or an eighth of the memory the GC may use
when that is less, and again each time that amount doubles. Analyzer VX1002 flags a batch that
nothing disposes ([diagnostics.md](diagnostics.md)).

## Keeping one batch of many

```csharp
RecordBatch? kept = null;
await foreach (Columns<Reading> cols in file.Scan<Reading>())
{
    if (cols.StartRow <= 500_000 && 500_000 < cols.StartRow + cols.RowCount)
    {
        kept = cols.ToOwned();
    }
}
```

```
kept past the loop: 65536 rows from row 458752, days 458 to 524, schema struct{Day: i32, Celsius: f64?, City: utf8}
```

`ToOwned()` on borrowed columns makes the same copy `ToBatchesAsync` makes, for the one batch you want
to keep. `BatchView.ToOwned()` does the same on the tool path.

## What the copy costs

The batch is copied once, into buffers from the session's pool. The decoded buffers are not handed
over directly because they live alongside the chunks the scan keeps across batches: a batch that took
them would take the chunk with it, and the next batch of that chunk would have to decode it again.

Reading every value of the demonstration file, warmed up, the four variants run in turn, the best of
twenty rounds:

| | time | managed allocation |
|---|---|---|
| borrowed columns, typed scan | 6.0 ms | 6 KiB |
| borrowed columns, tool scan | 4.5 ms | 4 KiB |
| `ToBatchesAsync` | 5.2 ms | 122 KiB |
| `ToOwned()` on every borrowed batch | 6.0 ms | 121 KiB |

Two things differ between these rows: the copy, and the form of the columns. A typed scan's borrowed
columns arrive encoded the way the file stores them, here `Day` and `City` as runs and `Celsius` as a
dictionary, and reading `Values` or a text length decodes them in your loop
([encoded-forms.md](encoded-forms.md)). `ToBatchesAsync` and the tool scan decode them inside the
scan and deliver them in their plain form. So the difference between the tool scan and
`ToBatchesAsync` is the copy alone, 0.8 ms or 17 % of the plain scan in this run. Timings this short
move from run to run.

`ToOwned()` copies what it is given as it is, encoded form included, which is why it adds little to
the typed scan's own cost. An owned batch allocates a few KiB of managed memory on top of the scan's
own, for the `RecordBatch` and its bookkeeping, and its buffers come from the pool. Its `Schema` is
the scan's, one instance for every batch, so `As<T>()` binds the record once for the whole scan
rather than once per batch. A borrowed batch allocates nothing.

## Watch out

* An owned batch keeps its selection. With `ScanOptions.Compact = false`, or on a take by position,
  the scan delivers whole blocks with a `Selection`, and the copy carries it. On this file,
  `Celsius > 45` under `Compact = false` passes 122 500 rows and the owned batches select the same
  122 500, and `Rows(4, 900_000)` gives owned batches that select 2 rows. Read `Selection` on the
  batch's `View` or on `As<T>()` as you would on a borrowed batch. The whole block is still copied.
* A batch left in a channel is still owned. A consumer that stops early drains the channel and
  disposes what it finds, and a producer that fails to hand a batch over disposes it, as above.
* Holding batches holds pool memory. A bounded channel, as above, keeps a slow consumer from holding
  the whole file.
* A batch is not thread-safe. It may move to another thread and be disposed there, but only one
  consumer uses it at a time.

`RecordBatch` and the sinks are described in
[the public API design](../design/14-public-api.md#56-the-sinks), along with
[the tool path](../design/14-public-api.md#58-the-tool-path).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- owned-batches
```
