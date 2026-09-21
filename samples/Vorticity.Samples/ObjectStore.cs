using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class ObjectStore
{
    internal static async Task RunAsync()
    {
        // The seam is five methods. Two implementations ship: a directory, and memory.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(await Demo.ReadingsAsync());
        PutOutcome first = await memory.PutIfAbsentAsync("readings.vortex", bytes, CancellationToken.None);
        PutOutcome again = await memory.PutIfAbsentAsync("readings.vortex", bytes, CancellationToken.None);
        Console.WriteLine($"put: {first}, and putting it again: {again}");
        Console.WriteLine($"the store holds {memory.Count} objects, {memory.Bytes} bytes");

        ObjectHead? head = await memory.HeadAsync("readings.vortex", CancellationToken.None);
        Console.WriteLine($"head: {head?.Length} bytes, missing object: " +
            $"{await memory.HeadAsync("nothing", CancellationToken.None) is null}");

        IReadOnlyList<string> listed = await memory.ListAsync(string.Empty, string.Empty, 10, CancellationToken.None);
        Console.WriteLine($"list: {string.Join(", ", listed)}");

        // Opening one object of a store as a file: the source adapts the seam.
        await using ObjectSegmentSource source = new ObjectSegmentSource(memory, "readings.vortex");
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default);
        Console.WriteLine($"opened from the store: {file.RowCount} rows, {file.FileLength} bytes");

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project(["celsius"]).ExecuteAsync())
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        Console.WriteLine($"scanned {rows} rows out of the store");

        // What an operation costs, counted at the seam.
        await using CountingObjectStore counting = new CountingObjectStore(new MemoryObjectStore());
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(
            counting, (await VortexFile.OpenAsync(await Demo.ReadingsAsync())).DType))
        {
            Console.WriteLine($"creating a dataset: {counting.Requests} requests -- " +
                $"{counting.CountOf(ObjectOperation.GetRange)} get, " +
                $"{counting.CountOf(ObjectOperation.PutIfAbsent)} put, " +
                $"{counting.CountOf(ObjectOperation.List)} list, {counting.CountOf(ObjectOperation.Head)} head, " +
                $"{counting.CountOf(ObjectOperation.Delete)} delete; {counting.BytesRead} bytes read, " +
                $"{counting.BytesWritten} written, {counting.DependentSteps} dependent steps");
        }

        // A store that fails, to see what the caller sees.
        await using MemoryObjectStore failing = new MemoryObjectStore
        {
            Fails = (operation, key) => operation == ObjectOperation.GetRange,
        };
        await failing.PutIfAbsentAsync("readings.vortex", bytes, CancellationToken.None);
        try
        {
            await failing.GetRangeAsync("readings.vortex", 0, 16, CancellationToken.None);
        }
        catch (ObjectStoreException e)
        {
            Console.WriteLine($"a store that refuses a read: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            await memory.GetRangeAsync("nothing", 0, 16, CancellationToken.None);
        }
        catch (ObjectNotFoundException)
        {
            Console.WriteLine("a key that is not there: ObjectNotFoundException");
        }
    }
}
