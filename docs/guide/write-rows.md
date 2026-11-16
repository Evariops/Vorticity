# Write rows

Hand the writer records rather than columns: a span you already hold, or a stream that arrives over
time.

```csharp
await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
report = await writer.CompleteAsync(ct);
```

```csharp
await using VortexFileWriter streaming = session.CreateWriter<Reading>(path);
await streaming.WriteAsync(ReadingsAsync(Rows, ct), ct);
report = await streaming.CompleteAsync(ct);
```

`readings` is a `Reading[]` of a million rows. `ReadingsAsync` is an `async IAsyncEnumerable<Reading>`
that yields the same rows and awaits every ten thousand, as a source reading pages would.

## What happens

A span goes through the record's generated `WriteRows`, which copies it into the writer's builder
one column at a time: numbers through `GetSpan`, nullable members through `Append(T?)`, and strings
transcoded to UTF-8. The rows are read before `WriteAsync` returns, so you can reuse the array
immediately. From there the builder behaves as in [write-a-file.md](write-a-file.md).

A stream is gathered into groups of `BlockRows` rows, 8 192 by default, in a pooled array, and each
full group is written. The writer flushes on its own once 8 MiB are waiting, or four chunk targets
when that is more. The last partial group is written when the stream ends. You count nothing and call
nothing but `CompleteAsync`.

## What it costs

```
a span of 1000000 rows: 1508316 bytes, best of three 63 ms
  chunk rows: 65536 x15, 16384, 576
a stream of 1000000 rows: 1508316 bytes, best of three 97 ms
  chunk rows: 65536 x15, 16384, 576
```

Rows cost a field copy per row, and a `string` member is transcoded once per row. A list member is
copied element by element, and a nested record goes through a pooled array. The generated code works
on one column at a time, which is also why it can beat a hand-written loop that appends every column
of a row before moving on to the next ([write-lists-and-records.md](write-lists-and-records.md)
measures one).

The stream costs the enumeration and nothing else. Both calls cut the rows into the same chunks and
produce the same bytes, because the writer sizes a chunk by the width of the rows it holds, not by how
they arrived. [blocks-and-chunks.md](blocks-and-chunks.md) shows where that width comes from.

## Rows on several threads

Both calls fill the writer's builder on the caller's thread. The writer compresses and writes on
the session's lanes, but the copy of the rows into columns stays on that one thread, and on a wide
session it is what the write waits for: it cost a seventh of a write of ten million rows on one lane,
and kept that write under four and a half times its speed on one, whatever the lanes. Two forms move
it to several threads.

```csharp
await writer.WriteRecordsAsync<Reading>(readings, ct);
```

`WriteRecordsAsync` cuts an array already in memory into parts of whole blocks and fills them on the
session's lanes. The file holds the rows in the array's order, and is the one `WriteAsync` writes.

```csharp
WritePart<Reading>[] parts = new WritePart<Reading>[4];
for (int i = 0; i < parts.Length; i++)
{
    parts[i] = await writer.CreatePartAsync<Reading>(ct);
}

await Parallel.ForAsync(0, parts.Length, ct, async (i, token) =>
{
    Reading[] page = await ReadPageAsync(i, token);   // a source of your own, read on this task
    Reading.WriteRows(parts[i].Builder, page);
    await parts[i].WriteAsync(token);
});
```

A part is a builder of its own, filled on any thread, through the record's `WriteRows` or column by
column as in [write-a-file.md](write-a-file.md). The file takes a part's rows after those of
every part created before it, so the order of creation is the order of the rows, whatever order the
parts finish in. A part disposed without being written gives its place up, and `CompleteAsync`
refuses a file while a part is neither written nor disposed. Creating a part waits while twice the
session's degree of parts wait to be written, so a source faster than the writer waits there rather
than in memory.

## Watch out

* Rows are a convenience, not the fast path for wide numeric data. Filling a column with `GetSpan` or
  a bulk `Append` skips the copy per row, and [write-a-file.md](write-a-file.md) shows that path.
* Rows appended to the builder and not yet written go out with these, since the span form shares the
  writer's builder.
* A write that fails part way leaves nothing behind. If `WriteRows` throws, the builder is cut back to
  where it was before the call.
* The record must cover the file. `CreateWriter<Reading>` makes the record's schema the file's. When
  the schema came from elsewhere (an append, an untyped writer), the record's members are bound to
  columns by name, and a member with no column throws `VortexSchemaException`.
* Reading the rows back is covered in [read-rows.md](read-rows.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-rows
```

The figures above come from a run of every case, in which this one follows the others
([README.md](README.md)).
