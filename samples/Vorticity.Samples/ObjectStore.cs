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

        byte[] buffer = new byte[64];
        ObjectRead near = await memory.ReadAsync("files/readings.vortex", file.Length - 8, buffer, null, ct);
        Console.WriteLine($"read: asked 64 bytes 8 before the end, got {near.BytesRead} of an object of {near.ObjectLength}, token {near.Token}");
        ObjectRead tail = await memory.ReadTailAsync("files/readings.vortex", buffer, ct);
        Console.WriteLine($"tail: the last {tail.BytesRead} bytes, and the length with them, {tail.ObjectLength}");

        PutOutcome stale = await memory.PutAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, PutCondition.IfMatch("0"), ct);
        PutOutcome replaced = await memory.PutAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, PutCondition.IfMatch(head!.Value.Token), ct);
        Console.WriteLine($"replace while the token is 0: {stale}; while it is {head.Value.Token}: {replaced}, the token now {(await memory.HeadAsync("files/readings.vortex", ct))?.Token}");

        for (int i = 0; i < 5; i++)
        {
            await memory.PutIfAbsentAsync($"logs/{i:D2}", PipeReader.Create(new ReadOnlySequence<byte>([(byte)i])), 1, ct);
        }

        List<ObjectListing> page = await memory.ListAsync("logs/", "logs/01", ct).Take(2).ToListAsync(ct);
        Console.WriteLine($"list under logs/ after logs/01, first two: {string.Join(", ", page.Select(o => $"{o.Key} ({o.Head.Length} byte)"))}");

        await memory.DeleteAsync(["logs/00", "logs/01", "logs/99"], ct);
        Console.WriteLine($"deleted a batch of three keys, one absent: {memory.Count} objects left");

        await using CountingObjectStore counting = new CountingObjectStore(memory);

        // A session that holds a store opens a location in it as it opens a path. The session
        // disposes the stores it holds; this one does not own the memory store under it.
        await using (VortexSession session = VortexSession.Create(o =>
        {
            o.SegmentCache = new SegmentCache(64L * 1024 * 1024);
            o.ObjectStores.Add("mem://lake", counting);
        }))
        {
            await using VortexFile opened = await session.OpenAsync("mem://lake/files/readings.vortex");
            Console.WriteLine($"open from the store: {opened.RowCount} rows; {Cost(counting)}");
            counting.Reset();
            double? mean = await opened.Scan<Reading>().Where(r => r.Day >= 900).AverageAsync(r => r.Celsius);
            Console.WriteLine($"mean of Celsius for Day >= 900: {mean:F3}; {Cost(counting)}");
            counting.Reset();
            Console.WriteLine($"a full scan, {await CountAsync(opened)} rows: {Cost(counting)}");
            counting.Reset();
            Console.WriteLine($"the same scan again, its segments in the session's cache, {await CountAsync(opened)} rows: {Cost(counting)}");

            // An append replaces the object with its old bytes and the new ones, only while nobody else
            // replaced it. This store copies nothing on its service, so the old bytes are read back.
            counting.Reset();
            await using (VortexFileWriter appending = await session.OpenWriterAsync("mem://lake/files/readings.vortex"))
            {
                await appending.WriteAsync<Reading>([new Reading(1_000, 21.5, "Lyon"), new Reading(1_001, 22.0, "Lyon")]);
                await appending.CompleteAsync();
            }

            await using VortexFile appended = await session.OpenAsync("mem://lake/files/readings.vortex");
            Console.WriteLine($"two rows appended at the location: {appended.RowCount} rows now; {Cost(counting)}");
        }

        await using MemoryObjectStore failing = new MemoryObjectStore
        {
            Fails = (operation, key) => operation == ObjectOperation.Read,
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
        $"{store.Requests} requests ({store.CountOf(ObjectOperation.Read)} read, {store.CountOf(ObjectOperation.Head)} head, " +
        $"{store.CountOf(ObjectOperation.Put)} put, {store.CountOf(ObjectOperation.List)} list), " +
        $"{store.DependentSteps} dependent steps, {store.BytesRead} bytes read, {store.BytesWritten} written";

    private static async Task<long> CountAsync(VortexFile file)
    {
        long rows = 0;
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
