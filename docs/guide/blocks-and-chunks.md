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

After each call the sample prints `RowCount`, `UnflushedBytes` and the file's length on disk:

```
wrote 5 000 rows: RowCount 5000, UnflushedBytes 140625, 0 bytes on disk
flushed: RowCount 5000, UnflushedBytes 140625, 0 bytes on disk
wrote 5 000 more: RowCount 10000, UnflushedBytes 281250, 0 bytes on disk
flushed: RowCount 10000, UnflushedBytes 50850, 14628 bytes on disk
completed: chunk rows 8192, 1808, 22708 bytes
```

## Two units

* **A block** is `BlockRows` rows, 8 192 by default. It is the unit of pruning, of a take, and of a
  batch a scan delivers.
* **A chunk** is a run of whole blocks encoded together: each column chooses its encoding once per
  chunk. A chunk is also the seam of an append, which keeps the chunks before it and rewrites the
  last one when the file does not end on a block.

## What each call does

* `WriteAsync` accepts any number of rows and keeps them in the builder's buffers. Once they hold
  about `ChunkTargetBytes` (1 MiB by default), the whole blocks among them are encoded as chunks;
  the rest stays pending. It writes nothing to the file.
* `FlushAsync` seals every whole block pending into a chunk and hands the encoded chunks to the file.
  A partial block stays pending: the first flush above, with 5 000 rows, wrote nothing.
* `CompleteAsync` seals the whole blocks, writes what remains as the tail, a chunk shorter than a
  block, and then the statistics, zone maps and footer: 8 192 rows and 1 808 above.

## What makes a file append-friendly

The sample writes the same rows in several ways, then opens an append on each file and prints where
it would resume:

| written as | chunks | bytes | an append resumes at |
|---|---|---|---|
| 98 304 rows, writes of 8 192 | 5 (32 768 x2, 8 192 x2, 16 384) | 160 756 | 98 304 of 98 304 |
| 98 304 rows, writes of 8 192, a flush after each | 12 (8 192 x12) | 177 628 | 98 304 of 98 304 |
| 98 304 rows, one write | 3 (32 768 x3) | 155 308 | 98 304 of 98 304 |
| 100 000 rows, writes of 5 000 | 5 (32 768 x2, 8 192, 24 576, 1 696) | 163 404 | 98 304 of 100 000 |
| 100 000 rows, writes of 5 000, a flush after each | 13 (8 192 x12, 1 696) | 183 052 | 98 304 of 100 000 |
| 98 304 rows, `ChunkTargetBytes` 64 KiB | 12 (8 192 x12) | 177 628 | 98 304 of 98 304 |
| 98 304 rows, `ChunkTargetBytes` 16 MiB | 1 (98 304) | 152 452 | 98 304 of 98 304 |
| 98 304 rows, `BlockRows` 1 024 | 5 (36 864 x2, 4 096 x2, 16 384) | 166 092 | 98 304 of 98 304 |

What decides where an append resumes is the file's row count, not how the rows arrived. A file of
98 304 rows ends on a block, and an append continues after it without rewriting anything. A file of
100 000 rows ends with a tail of 1 696 rows, and an append rewrites that tail chunk: it resumes at
98 304. Both are correct files. [append-and-repair.md](append-and-repair.md) measures what repeated
appends cost each way.

## What it costs

Chunks are not free. Each one chooses its encodings again and carries its own framing, so a flush
after every block made the file 14 % larger than one write (177 628 bytes against 155 308), and a
16 MiB target, one chunk, made it 2 % smaller. Blocks eight times smaller cost 3 % here (166 092
bytes against 160 756 for the same writes) and buy pruning eight times finer, which
[filter-rows.md](filter-rows.md) is about.

A write of one block at a time comes out in alternating chunks: the builder reaches the chunk
target at five blocks, and seals them as one chunk of four blocks and one of a single block. The
same rows in one call give chunks of four.

## Watch out

* **A flush is the only I/O before completion**, and it writes nothing while less than a block is
  pending. `UnflushedBytes` counts the encoded chunks waiting for the file and the rows waiting for
  their chunk.
* **Flush when you need the bytes out**, to bound memory or to feed a slow sink
  ([stream-to-an-object.md](stream-to-an-object.md)), not after every write.
* **An append keeps the file's block size**, whatever its options say.
* [11-write-strategy.md](../design/11-write-strategy.md) is the design of the ingest, the chunking
  and the append.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- blocks-and-chunks
```

The figures above come from that run.
