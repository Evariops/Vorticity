using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class OpenAFile
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            Console.WriteLine(file.Schema);
            Console.WriteLine($"{file.RowCount} rows, {file.Length} bytes, edition {file.Edition}");
            Console.WriteLine($"identity {file.Identity}, metadata keys [{string.Join(", ", file.Metadata.Keys)}]");
        }

        await using VortexSession session = VortexSession.Create(options => options.MaxConcurrentReads = 8);
        // Under a namespace nested in Vorticity, a bare File names the library's Vorticity.File namespace.
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);

        await TimeAsync("a path", await session.OpenAsync(path));
        await TimeAsync("a mapped source", await session.OpenAsync(new MemoryMappedSegmentSource(path)));
        await TimeAsync("positional reads", await session.OpenAsync(new FileSegmentSource(path)));
        await TimeAsync("bytes in memory", await session.OpenAsync(new MemorySegmentSource(bytes)));

        CountingSource counting = new CountingSource(new FileSegmentSource(path));
        await using (VortexFile file = await session.OpenAsync(counting))
        {
            Console.WriteLine($"the open: {counting.Requests} request, {counting.Bytes} bytes");
            long rows = await file.Scan<Reading>().CountAsync();
            double? hottest = await file.Scan<Reading>().MaxAsync(r => r.Celsius);
            Console.WriteLine($"CountAsync {rows}, MaxAsync {hottest}: still {counting.Requests} request");

            ScanPlan plan = await file.Scan<Reading>().ExplainAsync();
            (long requests, long read, _) = await PassAsync(file, counting);
            Console.WriteLine($"a full scan: the plan names {plan.Segments} segments, {plan.BytesToRead} bytes; the source served {requests} requests, {read} bytes");
        }

        CountingSource told = new CountingSource(new FileSegmentSource(path));
        VortexOpenOptions wide = new VortexOpenOptions { Length = bytes.Length, InitialReadSize = 256 * 1024 };
        await using (VortexFile file = await session.OpenAsync(told, wide))
        {
            Console.WriteLine($"InitialReadSize 256 KiB: {told.Requests} request, {told.Bytes} bytes");
        }

        await using VortexSession cached = VortexSession.Create(options => options.SegmentCache = new SegmentCache(64L * 1024 * 1024));
        CountingSource behindCache = new CountingSource(new FileSegmentSource(path));
        await using (VortexFile file = await cached.OpenAsync(behindCache))
        {
            for (int pass = 1; pass <= 2; pass++)
            {
                (long requests, long read, long hits) = await PassAsync(file, behindCache);
                Console.WriteLine($"with a segment cache, scan {pass}: the source served {requests} requests, {read} bytes; {hits} cache hits");
            }
        }
    }

    private static async Task TimeAsync(string how, VortexFile opened)
    {
        await using VortexFile file = opened;
        TimeSpan best = TimeSpan.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await PassAsync(file, null);
            best = clock.Elapsed < best ? clock.Elapsed : best;
        }

        Console.WriteLine($"{how,-17} a full scan in {best.TotalMilliseconds:F1} ms");
    }

    private static async Task<(long Requests, long Bytes, long CacheHits)> PassAsync(VortexFile file, CountingSource? source)
    {
        long requests = source?.Requests ?? 0;
        long bytes = source?.Bytes ?? 0;
        Scan<Reading> scan = file.Scan<Reading>();
        await foreach (Columns<Reading> columns in scan)
        {
            _ = columns.RowCount;
        }

        return ((source?.Requests ?? 0) - requests, (source?.Bytes ?? 0) - bytes, scan.Statistics.CacheHits);
    }

    private sealed class CountingSource(ISegmentSource inner) : ISegmentSource
    {
        private long _requests;
        private long _bytes;

        public long Requests => Interlocked.Read(ref _requests);

        public long Bytes => Interlocked.Read(ref _bytes);

        public long Length => inner.Length;

        public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _bytes, range.Length);
            return inner.ReadAsync(range, cancellationToken);
        }

        public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            foreach (SegmentRange range in ranges.Span)
            {
                Interlocked.Increment(ref _requests);
                Interlocked.Add(ref _bytes, range.Length);
            }

            return inner.ReadAsync(ranges, leases, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
