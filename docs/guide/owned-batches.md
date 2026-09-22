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
through a channel: 123 batches, 1000000 rows, mean 30.00 degrees
```

The columns of an ordinary `await foreach` are borrowed: they are valid until the loop moves on, and
the compiler keeps them inside the loop body ([scan-a-table.md](scan-a-table.md)). A pipeline needs
the opposite, a batch that outlives the loop and crosses threads. `ToBatchesAsync` gives one: each
`RecordBatch` is **owned** by whoever holds it, and whoever holds it last disposes it.

## What an owned batch is

A `RecordBatch` has its `Schema`, its `RowCount` and its `StartRow`, and two ways in:

* `batch.As<Reading>()` gives the same `Columns<Reading>` a typed scan gives, with the generated
  members (`cols.Celsius`), valid until the batch is disposed.
* `batch.View` gives a `BatchView`, columns by index or by name, for code that has no record type
  ([untyped-files.md](untyped-files.md)).

After `Dispose` both throw `ObjectDisposedException`, and so does every other member. A batch that
is never disposed keeps its buffers from the session's pool; analyzer VX1002 flags one
([diagnostics.md](diagnostics.md)).

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
kept past the loop: 8192 rows from row 499712, days 499 to 507, schema struct{Day: i32, Celsius: f64?, City: utf8}
```

`ToOwned()` on borrowed columns is the same copy as `ToBatchesAsync` makes, for the one batch you
want to keep. `BatchView.ToOwned()` does the same on the tool path.

## What the copy costs

The batch is copied once, into buffers from the session's pool. The decoded buffers are not handed
over instead, because they live beside the chunks the scan keeps across batches: a batch that took
them would take the chunk with it, and the next batch of that chunk would decode it again.

Reading every value of the demonstration file, warmed, the four variants run in turn, the best of
twenty rounds:

| | time | managed allocation |
|---|---|---|
| borrowed columns, typed scan | 12.4 ms | 111 KiB |
| borrowed columns, tool scan | 6.7 ms | 159 KiB |
| `ToBatchesAsync` | 8.3 ms | 1 350 KiB |
| `ToOwned()` on every borrowed batch | 13.3 ms | 1 370 KiB |

Two things differ between these rows, the copy and the form of the columns:

* A typed scan's borrowed columns arrive **encoded** where the file is: here `Day` and `City`
  run-end, `Celsius` dictionary. Reading `Values` or a text length decodes them in your loop
  ([encoded-forms.md](encoded-forms.md)). `ToBatchesAsync` and the tool scan decode them in the
  scan, and deliver them canonical.
* So the tool scan against `ToBatchesAsync` is the copy alone: **1.6 ms, 24 % of the canonical
  scan** in this run, and between a fifth and a third across runs.

`ToOwned()` copies what it is given as it is, the encoded form included, which is why it adds little
to the typed scan's own cost. An owned batch allocates about 10 KiB of managed memory beyond the
scan's own, the `RecordBatch` and its bookkeeping; its buffers come from the pool.

## Watch out

* **An owned batch has no selection.** With `ScanOptions.Compact = false`, or on a take by position,
  the scan delivers whole blocks with a `Selection`, and `ToBatchesAsync` and `ToOwned()` drop it:
  on this file `Celsius > 45` under `Compact = false` passes 122 500 rows and the owned batches
  select all 1 000 000, and `Rows(4, 900_000)` gives owned batches of 15 264 rows. Leave `Compact`
  on for an owned pipeline, and read a take through the borrowed columns or `ToRecordsAsync`.
* **A batch left in a channel is still owned.** A consumer that stops early drains the channel and
  disposes what it finds; a producer that fails to hand a batch over disposes it, as above.
* Holding batches holds pool memory: a bounded channel, as above, is what keeps a slow consumer from
  holding the whole file.
* A batch is not thread-safe. It may move to another thread, and be disposed there, but one
  consumer uses it at a time.

`RecordBatch` and the sinks are in §5.6 and §5.8 of [14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- owned-batches
```
