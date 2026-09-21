using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class Options
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        VortexOpenOptions open = new VortexOpenOptions
        {
            Read = new VortexReadOptions
            {
                MaxDecompressedSize = 64L * 1024 * 1024,
                IndexCacheBytes = 16L * 1024 * 1024,
                VerifyStatistics = true,
            },
            PreloadIndexes = true,
            TornTail = VortexTornTailPolicy.Refuse,
        };

        await using VortexFile file = await VortexFile.OpenAsync(path, open);
        Console.WriteLine($"{file.RowCount} rows, {file.Indexes?.Count} indexes, " +
            $"ceiling {file.ReadOptions.MaxDecompressedSize} bytes");

        Console.WriteLine($"read:  ceiling {VortexReadOptions.Default.MaxDecompressedSize}, " +
            $"index cache {VortexReadOptions.Default.IndexCacheBytes}, " +
            $"verify statistics {VortexReadOptions.Default.VerifyStatistics}");
        Console.WriteLine($"open:  first read {VortexOpenOptions.Default.InitialReadSize}, " +
            $"preload indexes {VortexOpenOptions.Default.PreloadIndexes}, " +
            $"torn tail {VortexOpenOptions.Default.TornTail}");
        Console.WriteLine($"write: block {VortexWriteOptions.Default.RowBlockSize} rows, " +
            $"data block {VortexWriteOptions.Default.DataBlockTargetBytes} bytes, " +
            $"compress {VortexWriteOptions.Default.Compress}, " +
            $"profile {VortexWriteOptions.Default.Profile}, " +
            $"edition {VortexWriteOptions.Default.TargetEdition}, " +
            $"index budget {VortexWriteOptions.Default.IndexBudgetPerMille} per mille");
        Console.WriteLine($"scan:  {ScanBuilder.DefaultDegreeOfParallelism} thread");

        try
        {
            await using VortexFile tight = await VortexFile.OpenAsync(path, new VortexOpenOptions
            {
                Read = new VortexReadOptions { MaxDecompressedSize = 4_096 },
            });
            await foreach (RecordBatch batch in tight.ScanBuilder().ExecuteAsync())
            {
                batch.Dispose();
            }
        }
        catch (VortexFormatException e)
        {
            Console.WriteLine(e.Message);
        }

        int n = 0;
        foreach ((string name, VortexWriteOptions options) in new (string, VortexWriteOptions)[]
        {
            ("default", VortexWriteOptions.Default),
            ("Compress = false", new VortexWriteOptions { Compress = false }),
            ("Profile = Fastest", new VortexWriteOptions { Profile = WriteProfile.Fastest }),
            ("FileStatistics = false", new VortexWriteOptions { FileStatistics = false }),
            ("RowBlockSize = 1024", new VortexWriteOptions { RowBlockSize = 1_024 }),
        })
        {
            string written = Demo.Path($"options-{n++}.vortex");
            await Demo.WriteReadingsAsync(written, options);
            Console.WriteLine($"  {name}: {new FileInfo(written).Length} bytes");
            System.IO.File.Delete(written);
        }
    }
}
