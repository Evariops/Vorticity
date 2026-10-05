using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class ObjectStore
{
    internal static async Task RunAsync()
    {
        CancellationToken ct = CancellationToken.None;
        byte[] file = await System.IO.File.ReadAllBytesAsync(await Demo.ReadingsAsync(), ct);

        await using MemoryObjectStore memory = new MemoryObjectStore();
        PutOutcome first = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);
        PutOutcome again = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);
        Console.WriteLine($"put: {first}, then {again}; the store holds {memory.Count} object, {memory.Bytes} bytes");

        ObjectHead? head = await memory.HeadAsync("files/readings.vortex", ct);
        Console.WriteLine($"head: {head?.Length} bytes, token {head?.Token}; a missing key: {(await memory.HeadAsync("files/nothing", ct) is null ? "null" : "found")}");

        using (ObjectRange tail = await memory.GetRangeAsync("files/readings.vortex", file.Length - 8, 64, ct))
        {
            Console.WriteLine($"range: asked 64 bytes 8 before the end, got {tail.Length}, token {tail.Token}, contiguous {tail.IsContiguous}");
        }

        for (int i = 0; i < 5; i++)
        {
            await memory.PutIfAbsentAsync($"logs/{i:D2}", PipeReader.Create(new ReadOnlySequence<byte>([(byte)i])), 1, ct);
        }

        List<string> page = await memory.ListAsync("logs/", "logs/01", ct).Take(2).ToListAsync(ct);
        Console.WriteLine($"list under logs/ after logs/01, first two: {string.Join(", ", page)}");

        await memory.DeleteAsync(["logs/00", "logs/01", "logs/99"], ct);
        Console.WriteLine($"deleted a batch of three keys, one absent: {memory.Count} objects left");

        await using CountingObjectStore counting = new CountingObjectStore(memory);
        ObjectHead known = (await counting.HeadAsync("files/readings.vortex", ct))!.Value;
        await using (VortexFile opened = await VortexSession.Default.OpenAsync(new StoreSegmentSource(counting, "files/readings.vortex", known.Length)))
        {
            Console.WriteLine($"open from the store: {opened.RowCount} rows; {Cost(counting)}");
            counting.Reset();
            double? mean = await opened.Scan<Reading>().Where(r => r.Day >= 900).AverageAsync(r => r.Celsius);
            Console.WriteLine($"mean of Celsius for Day >= 900: {mean:F3}; {Cost(counting)}");
            counting.Reset();
            long rows = 0;
            await foreach (Columns<Reading> batch in opened.Scan<Reading>())
            {
                rows += batch.RowCount;
            }

            Console.WriteLine($"a full scan, {rows} rows: {Cost(counting)}");
        }

        await using (VortexSession cached = VortexSession.Create(o => o.SegmentCache = new SegmentCache(64L * 1024 * 1024)))
        {
            await using VortexFile opened = await cached.OpenAsync(new StoreSegmentSource(counting, "files/readings.vortex", known.Length));
            counting.Reset();
            long rows = 0;
            await foreach (Columns<Reading> batch in opened.Scan<Reading>())
            {
                rows += batch.RowCount;
            }

            Console.WriteLine($"a full scan through a session with a segment cache, {rows} rows: {Cost(counting)}");
        }

        await using MemoryObjectStore failing = new MemoryObjectStore
        {
            Fails = (operation, key) => operation == ObjectOperation.GetRange,
        };
        await failing.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);
        try
        {
            await failing.GetRangeAsync("files/readings.vortex", 0, 16, ct);
        }
        catch (ObjectStoreException e)
        {
            Console.WriteLine($"a store told to fail: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            await memory.GetRangeAsync("files/nothing", 0, 16, ct);
        }
        catch (ObjectNotFoundException e)
        {
            Console.WriteLine($"a key that is not there: {e.GetType().Name}, key '{e.Key}'");
        }

        counting.Reset();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(counting, Reading.Schema))
        {
            Console.WriteLine($"creating a dataset: {Cost(counting)}");
        }

        counting.Reset();
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(counting))
        {
            Console.WriteLine($"opening it: {Cost(counting)}");
        }
    }

    private static string Cost(CountingObjectStore store) =>
        $"{store.Requests} requests ({store.CountOf(ObjectOperation.GetRange)} get, {store.CountOf(ObjectOperation.Head)} head, " +
        $"{store.CountOf(ObjectOperation.PutIfAbsent)} put, {store.CountOf(ObjectOperation.List)} list), " +
        $"{store.DependentSteps} dependent steps, {store.BytesRead} bytes read, {store.BytesWritten} written";

    /// <summary>One object of a store read as a file: one ranged get per segment, the gets of a batch issued together.</summary>
    private sealed class StoreSegmentSource(IObjectStore store, string key, long length) : ISegmentSource
    {
        public long Length => length;

        public async ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            ObjectRange bytes = await store.GetRangeAsync(key, range.Offset, range.Length, cancellationToken);
            return new SegmentLease(bytes.Bytes, bytes);
        }

        public async ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            Task<SegmentLease>[] reads = new Task<SegmentLease>[ranges.Length];
            for (int i = 0; i < reads.Length; i++)
            {
                reads[i] = ReadAsync(ranges.Span[i], cancellationToken).AsTask();
            }

            try
            {
                await Task.WhenAll(reads);
            }
            catch
            {
                foreach (Task<SegmentLease> read in reads)
                {
                    if (read.IsCompletedSuccessfully)
                    {
                        read.Result.Dispose();
                    }
                }

                throw;
            }

            for (int i = 0; i < reads.Length; i++)
            {
                leases.Span[i] = reads[i].Result;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
