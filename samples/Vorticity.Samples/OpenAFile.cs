using System;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class OpenAFile
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            Console.WriteLine($"from a path: {file.RowCount} rows, {file.FileLength} bytes, " +
                $"torn tail: {(file.TornTail is null ? "none" : file.TornTail.Reason)}");
        }

        await using (VortexFile file = await VortexFile.OpenAsync(
            MemoryMappedSegmentSource.Open(path), VortexOpenOptions.Default))
        {
            Console.WriteLine($"memory-mapped: {file.RowCount} rows");
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
        await using (VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(bytes), VortexOpenOptions.Default))
        {
            Console.WriteLine($"from bytes in hand: {file.RowCount} rows");
        }

        // A source the file is told not to close outlives it, and serves the next open.
        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
        await using (VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions { LeaveSourceOpen = true }))
        {
            Console.WriteLine($"lent a source: {file.RowCount} rows");
        }

        Console.WriteLine($"the source is still open: {await source.GetLengthAsync(default)} bytes");
        await source.DisposeAsync();

        await Cost("opening", VortexOpenOptions.Default);
        await Cost("opening with PreloadIndexes", new VortexOpenOptions { PreloadIndexes = true });

        async Task Cost(string what, VortexOpenOptions options)
        {
            CountingSegmentSource counting = new CountingSegmentSource(RandomAccessSegmentSource.Open(path));
            await using VortexFile file = await VortexFile.OpenAsync(counting, options);
            Console.WriteLine($"{what} cost {counting.Requests} read of {counting.Bytes} bytes " +
                $"for a file of {file.FileLength}, and knows of {file.Indexes?.Count.ToString() ?? "no"} indexes");
        }
    }
}
