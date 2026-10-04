using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class StreamToAnObject
{
    private const int Rows = 1_000_000;
    private const int PartBytes = 64 << 10;

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        Reading[] readings = Readings(Rows);

        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 << 10, resumeWriterThreshold: 128 << 10));
        Task<Upload> upload = UploadAsync(pipe.Reader, partDelay: TimeSpan.FromMilliseconds(2));

        long waited = 0;
        int flushes = 0;
        WriteReport report;
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

        Upload uploaded = await upload;
        Console.WriteLine($"through a Pipe: {report.Bytes.Total} bytes in {uploaded.Parts} parts of up to {PartBytes} bytes; " +
            $"{flushes} flushes waited {Stopwatch.GetElapsedTime(0, waited).TotalMilliseconds:F0} ms for the upload in all");

        await using (VortexFile file = await session.OpenAsync(new MemorySegmentSource(uploaded.Bytes)))
        {
            Console.WriteLine($"read back from the uploaded bytes: {file.RowCount} rows, mean {await file.Scan<Reading>().AverageAsync(r => r.Celsius):F4}");
        }

        using (MemoryStream stream = new MemoryStream())
        {
            PipeWriter sink = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            await using (VortexFileWriter writer = session.CreateWriter(sink, Reading.Schema))
            {
                await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await session.OpenAsync(new MemorySegmentSource(stream.ToArray()));
            Console.WriteLine($"through PipeWriter.Create(stream): {stream.Length} bytes, {file.RowCount} rows");
        }

        Pipe abandoned = new Pipe();
        Task<Upload> refused = UploadAsync(abandoned.Reader, TimeSpan.Zero);
        await using (VortexFileWriter writer = session.CreateWriter(abandoned.Writer, Reading.Schema))
        {
            await writer.WriteAsync<Reading>(readings.AsSpan(0, 100_000), ct);
            await writer.FlushAsync(ct);
        }

        try
        {
            await refused;
        }
        catch (OperationCanceledException e)
        {
            Console.WriteLine($"a writer disposed without CompleteAsync: the upload sees {e.GetType().Name}: {e.Message}");
        }
    }

    private sealed record Upload(int Parts, byte[] Bytes);

    /// <summary>A stand-in for a multipart upload: takes the bytes a part at a time, each part taking <paramref name="partDelay"/>.</summary>
    private static async Task<Upload> UploadAsync(PipeReader reader, TimeSpan partDelay)
    {
        using MemoryStream received = new MemoryStream();
        int parts = 0;
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            while (buffer.Length >= PartBytes || (result.IsCompleted && !buffer.IsEmpty))
            {
                ReadOnlySequence<byte> part = buffer.Slice(0, Math.Min(PartBytes, buffer.Length));
                foreach (ReadOnlyMemory<byte> segment in part) received.Write(segment.Span);
                parts++;
                await Task.Delay(partDelay);
                buffer = buffer.Slice(part.End);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
        return new Upload(parts, received.ToArray());
    }

    private static Reading[] Readings(int count)
    {
        Reading[] readings = new Reading[count];
        for (int row = 0; row < count; row++)
        {
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return readings;
    }
}
