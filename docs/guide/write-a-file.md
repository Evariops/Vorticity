# Write a file

Fill columns in place, hand them to the writer, and read what it decided on your behalf.

```csharp
await using (VortexFileWriter writer = session.CreateWriter<Reading>(path))
{
    ColumnsBuilder<Reading> b = writer.Builder<Reading>();
    double[] temperatures = new double[writer.BlockRows];
    ulong[] validity = new ulong[writer.BlockRows / 64];

    for (int start = 0; start < Rows; start += writer.BlockRows)
    {
        int count = Math.Min(writer.BlockRows, Rows - start);

        Span<int> day = b.Day.GetSpan(count);
        for (int i = 0; i < day.Length; i++) day[i] = (start + i) / 1_000;
        b.Day.Advance(day.Length);

        Temperatures(start, temperatures.AsSpan(0, count), validity);
        b.Celsius.Append(temperatures.AsSpan(0, count), validity);

        for (int i = 0; i < count; i++) b.City.Append(cities[(start + i) / 7 % cities.Length]);

        await writer.WriteAsync(b, ct);
        if (writer.UnflushedBytes > 8 << 20) await writer.FlushAsync(ct);
    }

    report = await writer.CompleteAsync(ct);
}
```

`Reading` is `[VortexRecord] public partial record struct Reading(int Day, double? Celsius, string City)`.
The record's schema becomes the file's, and the generator gives the builder one property per member:
`b.Day` is a `ColumnBuilder<int>`, `b.Celsius` a `ColumnBuilder<double?>`, `b.City` a
`ColumnBuilder<string>`. `cities` holds the city names as UTF-8 byte arrays, and `Temperatures` fills
a block of values and clears one bit in fifty in `validity`. [records.md](records.md) says what a
record may hold.

## What happens

* `CreateWriter<Reading>` creates the file, replacing whatever was at the path, and binds the record.
  `Builder<Reading>()` returns the writer's one builder, whose buffers come from the session's pool.
  Every call returns the same builder.
* `GetSpan(count)` is the `IBufferWriter` pattern. It hands you exactly `count` slots of the buffer
  the encoder will read; you fill them and `Advance` commits them. A primitive column is encoded
  from those bytes where they lie, without a copy.
* `Append(values, validity)` takes a block of values and its bitmap in one call: bit `i % 64` of
  word `i / 64` belongs to value `i`, and a set bit means the value is present. A bitmap with every
  bit set allocates nothing. [write-nulls.md](write-nulls.md) covers the other null appends.
* `Append` on a text column takes UTF-8 bytes and does not transcode them.
  [write-text.md](write-text.md) covers strings, formatted values and long values.
* `WriteAsync(b)` takes the builder's rows and clears it for the next block, keeping its buffers.
  Once the rows it holds reach a chunk's worth of bytes, it encodes the whole blocks among them.
* `FlushAsync` hands the encoded chunks to the file. It is the only I/O before completion, and
  `UnflushedBytes` says how much is waiting for it.
* `CompleteAsync` writes the pending rows as the last block, then the statistics, the zone maps
  and the footer, and returns the `WriteReport`.

## What the writer decided

The report is the only place that says what was chosen. The sample prints it, grouping the
per-chunk encodings:

```
1000000 rows in blocks of 8192, 17 chunks, 1508212 bytes, in 131 ms
  data 1495924, statistics 200, zone maps 8264, indexes 0, footer 3824
  chunk rows: 65536 x15, 16384, 576
  Day: RunEnd x16, Sequence
  Celsius: Dictionary x16, Alp
  City: RunEnd x17
read back: 1000000 rows, 1508212 bytes on disk, mean 30.0000 °C
```

`report.Columns` gives one encoding per chunk and per column. A day lasts a thousand rows, so `Day`
is written as runs; the last 576 rows all fall on day 999, a progression of step zero. Four hundred
distinct temperatures make a dictionary, except in the small tail chunk, which took ALP. The city
changes every seven rows, which is still a run. Nothing was sampled or guessed: the writer priced the
candidates on each chunk it held. [11-write-strategy.md](../design/11-write-strategy.md) describes
how.

`report.Bytes` sums to the file's length. `report.ChunkRows` says how the rows were cut into chunks,
and [blocks-and-chunks.md](blocks-and-chunks.md) explains why a write of one block at a time comes
out as chunks of 65 536 rows: the writer sizes a chunk by its widest column, here the city's
sixteen-byte views, a megabyte of them. `report.Indexes` lists every index the policy asked for:
none here, since the default is `IndexPolicy.None` ([indexes.md](indexes.md)).

## What it costs

The million rows took 131 ms in this run, first-use compilation included, and 1.51 MB on disk. The
same rows stored without encoding take 20.8 MB ([writer-options.md](writer-options.md)). The
columns are encoded on the thread that calls `WriteAsync`. The builder holds up to a chunk's worth
of rows, a megabyte of the widest column by default, before whole blocks are encoded and released.

## Watch out

* **A span does not cross an `await`.** Fill it and call `Advance` before the next `await`: the
  compiler refuses a `Span<T>` that lives across one.
* **Every column must hold the same number of rows** when `WriteAsync` is called, and no list may be
  left open. Otherwise it throws `VortexSchemaException`, for example: *The builder cannot be
  written: field 1 of struct{…} holds 7 rows and field 0 holds 8. Complete every row before writing
  it.*
* **A builder belongs to its writer.** `WriteAsync` refuses another writer's builder with
  `ArgumentException`. A builder is not thread-safe: one thread fills it and writes it.
* **`CompleteAsync` is what makes the file.** A writer disposed without it gives the file up, and a
  created file is deleted. [append-and-repair.md](append-and-repair.md) says what that means for an
  append.
* The options that change the file (compression, hints, block size, statistics, edition, metadata,
  identity) are in [writer-options.md](writer-options.md).

The other ways in: rows instead of columns ([write-rows.md](write-rows.md)), lists and nested
records ([write-lists-and-records.md](write-lists-and-records.md)), a schema known only at run
time ([write-without-a-record.md](write-without-a-record.md)), and a stream or an upload as the
destination ([stream-to-an-object.md](stream-to-an-object.md)).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-a-file
```

The figures above come from that run.
