# Stream to an object

Write a file to anything that accepts bytes, such as an upload, a socket or a stream, through a
`PipeWriter`, and let a slow destination slow the producer down.

```csharp
Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 << 10, resumeWriterThreshold: 128 << 10));
Task<Upload> upload = UploadAsync(pipe.Reader, partDelay: TimeSpan.FromMilliseconds(2));

await using (VortexFileWriter writer = session.CreateWriter(pipe.Writer, Reading.Schema))   // any PipeWriter
{
    for (int start = 0; start < Rows; start += writer.BlockRows)
    {
        await writer.WriteAsync<Reading>(readings.AsSpan(start, Math.Min(writer.BlockRows, Rows - start)), ct);
        if (writer.UnflushedBytes > 256 << 10)
        {
            long before = Stopwatch.GetTimestamp();
            await writer.FlushAsync(ct);                       // returns once the sink has taken the bytes
            waited += Stopwatch.GetTimestamp() - before;
            flushes++;
        }
    }

    report = await writer.CompleteAsync(ct);                   // completes the pipe
}
```

`UploadAsync` stands in for a multipart upload. It reads the pipe, takes 64 KiB at a time as a part,
and spends 2 ms on each. `Reading.Schema` is the record's schema, and `WriteAsync<Reading>` binds the
record to it as it would to a file.

```
through a Pipe: 1603260 bytes in 25 parts of up to 65536 bytes; 61 flushes waited 49 ms for the upload in all
read back from the uploaded bytes: 1000000 rows, mean 30.0000
through PipeWriter.Create(stream): 1508316 bytes, 1000000 rows
```

## What happens

The writer writes into the pipe's buffers and only flushes when asked. `WriteAsync` encodes chunks
into the `PipeWriter` without flushing it, and `FlushAsync` calls the pipe's own `FlushAsync`, which
returns once the reader has taken enough bytes to bring the pipe back under its pause threshold. A
slow destination therefore slows the producer instead of growing a buffer: here the 61 flushes
waited 49 ms in total, about what the 25 parts took to upload.

`CompleteAsync` completes the pipe once the footer is in it, which is how the reader knows the file
is whole. Do not complete the pipe yourself.

Any `PipeWriter` will do. `PipeWriter.Create(stream)` covers a `Stream`, a `FileStream` or a
`MemoryStream`, and the bytes form the same file as one written to a path. An object store's own pipe
gets the same backpressure, and a multipart upload is just a pipe that sends a part each time it
fills. Nothing in the writer needs to know.

## Giving up

A writer that is abandoned, or disposed without `CompleteAsync`, has no file to delete. It completes
the pipe with an error instead, so that whatever the pipe feeds knows the bytes are not a file:

```
a writer disposed without CompleteAsync: the upload sees OperationCanceledException: The Vortex file was abandoned; the bytes written so far are not a file.
```

The consumer's `ReadAsync` throws that exception. An upload should abort at that point and never
publish what it received.

## What it costs

The file written through the pipe is about 6 % larger than the one written through
`PipeWriter.Create(stream)` in a single write. Flushing every 256 KiB seals smaller chunks, and each
chunk carries its own framing and encoding choices ([blocks-and-chunks.md](blocks-and-chunks.md)).
Flush at the size your destination wants, and no more often.

## Watch out

* Bytes pile up in the pipe until you flush. A pause threshold only applies at a flush, so a producer
  that feeds the builder and never calls `FlushAsync` buffers the whole file in the pipe.
  `UnflushedBytes` says how much is waiting. `WriteAsync` over an `IAsyncEnumerable` flushes on its
  own once 8 MiB are waiting ([write-rows.md](write-rows.md)).
* The sink is written once, front to back. Nothing is sought or rewritten, which is what lets a socket
  or an upload be the destination. Appending, on the other hand, needs a path
  ([append-and-repair.md](append-and-repair.md)).
* To read the uploaded file back from memory, use `session.OpenAsync(new MemorySegmentSource(bytes))`.
  From an object store, use your own `ISegmentSource` ([object-store.md](object-store.md)).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- stream-to-an-object
```

The figures above come from a run of every case, in which this one follows the others
([README.md](README.md)).
