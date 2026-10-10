# Blocks and chunks

What `WriteAsync`, `FlushAsync` and `CompleteAsync` each do with your rows, and what makes a file
cheap to append to.

```csharp
int blockRows = writer.BlockRows;                          // 8 192 unless VortexWriteOptions.BlockRows says otherwise

await writer.WriteAsync<Reading>(rows.AsSpan(0, 5_000), ct);
await writer.FlushAsync(ct);

await writer.WriteAsync<Reading>(rows.AsSpan(5_000, 5_000), ct);
await writer.FlushAsync(ct);

WriteReport report = await writer.CompleteAsync(ct);
```

After each call the sample prints `RowCount`, `UnflushedBytes` and what the path holds:

```
wrote 5 000 rows: RowCount 5000, UnflushedBytes 140625, nothing at the path yet
flushed: RowCount 5000, UnflushedBytes 140625, nothing at the path yet
wrote 5 000 more: RowCount 10000, UnflushedBytes 281250, nothing at the path yet
flushed: RowCount 10000, UnflushedBytes 50850, nothing at the path yet
completed: chunk rows 8192, 1808, 22716 bytes
```

The first flush leaves `UnflushedBytes` unchanged. The second hands out a block of 8 192 rows, and
only the 1 808 rows after it stay pending. Flushed bytes go to a file written next to the path, which
`CompleteAsync` renames over it, so the path shows nothing, or the file it held before, until the new
file is complete ([append-and-repair.md](append-and-repair.md)).

## Two units

A block is `BlockRows` rows, 8 192 by default. It is the unit of pruning, of a take, and of the
batches of a filtered or ordered scan. A scan that only reads delivers up to sixteen blocks per batch,
never more than a chunk ([scan-a-table.md](scan-a-table.md)), and a filter does the same over the
blocks its zone maps prove whole.

A chunk is a run of whole blocks encoded together, and each column chooses its encoding once per
chunk. A chunk is also the seam of an append, which keeps the chunks before it and rewrites the last
one when the file does not end on a block boundary.

## What each call does

`WriteAsync` accepts any number of rows and keeps them in the builder's buffers. Once they fill a
chunk, the whole blocks among them are encoded and the rest stays pending. It writes nothing to the
file. By default the writer sizes a chunk by what its rows hold: as many whole blocks as keep the
widest column within a megabyte, between one block and 128, with all columns within 64 MiB. A chunk
is what a read fetches of a column, so this bounds what a selective read brings in, and at eight
bytes per value it makes sixteen blocks, enough for a scan to pay the fixed cost of a chunk rarely.

`ChunkTargetBytes` sets a fixed target in bytes for all the columns instead.
`ColumnChunkTargetBytes` gives a single column a target of its own: its rows wait across the file's
chunks until they reach that many bytes, then go out as one chunk of that column, so a dictionary is
paid for once. The other columns keep the file's chunks, and with them what a selective read fetches.
`WriteReport.ChunkRowsOf` gives each column's chunks.

`FlushAsync` seals every pending whole block into a chunk and hands the encoded chunks to the file. A
partial block stays pending, which is why the first flush above, with 5 000 rows, wrote nothing.

`CompleteAsync` seals the whole blocks, writes what remains as the tail (a chunk shorter than a
block), then the statistics, the zone maps and the footer: 8 192 rows and 1 808 above.

## What makes a file append-friendly

The sample writes the same rows in several ways, then opens an append on each file and prints where
it would resume:

| written as | chunks | bytes | an append resumes at |
|---|---|---|---|
| 98 304 rows, writes of 8 192 | 2 (65 536, 32 768) | 154 620 | 98 304 of 98 304 |
| 98 304 rows, writes of 8 192, a flush after each | 12 (8 192 x12) | 177 636 | 98 304 of 98 304 |
| 98 304 rows, one write | 2 (65 536, 32 768) | 154 620 | 98 304 of 98 304 |
| 100 000 rows, writes of 5 000 | 3 (65 536, 32 768, 1 696) | 160 044 | 98 304 of 100 000 |
| 100 000 rows, writes of 5 000, a flush after each | 13 (8 192 x12, 1 696) | 183 060 | 98 304 of 100 000 |
| 98 304 rows, `ChunkTargetBytes` 64 KiB | 12 (8 192 x12) | 177 636 | 98 304 of 98 304 |
| 98 304 rows, `ChunkTargetBytes` 16 MiB | 1 (98 304) | 152 460 | 98 304 of 98 304 |
| 98 304 rows, `BlockRows` 1 024 | 2 (65 536, 32 768) | 159 796 | 98 304 of 98 304 |

What decides where an append resumes is the file's row count, not how the rows arrived. A file of
98 304 rows ends on a block boundary, so an append continues after it without rewriting anything. A
file of 100 000 rows ends with a tail of 1 696 rows, and an append rewrites that tail chunk and
resumes at 98 304. Both are correct files. [append-and-repair.md](append-and-repair.md) measures what
repeated appends cost in each case.

## What it costs

Chunks are not free. Each one chooses its encodings again and carries its own framing, so flushing
after every block made the file 15 % larger than a single write (177 636 bytes against 154 620),
while a 16 MiB target, a single chunk, made it 1.4 % smaller. Blocks eight times smaller cost 3 %
here (159 796 bytes against 154 620 for the same writes) and buy pruning eight times finer, which is
what [filter-rows.md](filter-rows.md) is about. Chunks are not free for a reader either: a selective
read fetches the chunk of every column it reads for each block it keeps, which is why the writer
bounds chunks by the widest column rather than growing them as large as a scan would like.

Writing one block at a time gives the same result as one big write. The chunk is sized by the rows'
widest column, here the city's sixteen-byte views, a megabyte of which is eight blocks, and the rows
are cut there however they arrive.

## Watch out

* A flush is the only I/O before completion, and it writes nothing while less than a block is
  pending. `UnflushedBytes` counts the encoded chunks waiting for the file and the rows waiting for
  their chunk.
* Flush when you need the bytes out, to bound memory or to feed a slow sink
  ([stream-to-an-object.md](stream-to-an-object.md)), not after every write.
* An append keeps the file's block size, whatever its options say, and chunks its columns the same
  way the file does. `AppendAsync` refuses a file whose columns were given chunks of their own
  (`ColumnChunkTargetBytes`) with `VortexUnsupportedException`. Rewrite such a file instead
  ([copy-a-file.md](copy-a-file.md)).
* [11-write-strategy.md](../design/11-write-strategy.md) describes the design of the ingest, the
  chunking and the append.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- blocks-and-chunks
```

The figures above come from that run.
