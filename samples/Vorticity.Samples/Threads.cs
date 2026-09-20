using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class Threads
{
    internal static async Task RunAsync()
    {
        string path = await Demo.WideAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        // One open file, four scans at once. The file is thread-safe; each scan is its own.
        Task<long>[] concurrent = new Task<long>[4];
        for (int i = 0; i < concurrent.Length; i++)
        {
            concurrent[i] = Count(file);
        }

        Console.WriteLine($"four concurrent scans of one open file: {string.Join(", ", await Task.WhenAll(concurrent))}");
        Console.WriteLine($"the process default is {ScanBuilder.DefaultDegreeOfParallelism} thread");

        // Parallelism spreads a scan's chunks, so the shape of the file is what decides.
        string smaller = Demo.Path("smaller-blocks.vortex");
        int chunks = await Demo.WriteWideAsync(
            smaller, new VortexWriteOptions { DataBlockTargetBytes = 65_536 });
        int wideChunks = await Demo.WriteWideAsync(Demo.Path("wide-again.vortex"), VortexWriteOptions.Default);
        Console.WriteLine($"as written: {wideChunks} chunks; with a 64 KiB data block target: {chunks}");

        await Degrees("as written", path);
        await Degrees("64 KiB data blocks", smaller);

        static async Task Degrees(string what, string path)
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            ScanPlan plan = await file.Scan().ExplainAsync();
            Console.WriteLine($"  {what}: {plan.Splits} splits, {plan.Blocks} blocks, " +
                $"{plan.SegmentsToRead} segments to read");
            foreach (int degree in new[] { 1, 2, 4, 8 })
            {
                long best = long.MaxValue;
                long rows = 0;

                // Three passes, the fastest kept: one machine, one moment, and the shape is what to
                // read off it rather than the value.
                for (int pass = 0; pass < 3; pass++)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    rows = 0;
                    await foreach (RecordBatch batch in file.Scan().WithDegreeOfParallelism(degree).ExecuteAsync())
                    {
                        using (batch)
                        {
                            rows += batch.RowCount;
                        }
                    }

                    best = Math.Min(best, watch.ElapsedMilliseconds);
                }

                Console.WriteLine($"  {what}, degree {degree}: {rows} rows in {best} ms");
            }
        }

        // Consent given once, for the process, rather than at every call site.
        ScanBuilder.DefaultDegreeOfParallelism = 4;
        Console.WriteLine($"the process default is now {ScanBuilder.DefaultDegreeOfParallelism}: " +
            "every builder made from here starts from it, and a builder already made is untouched");
        ScanBuilder.DefaultDegreeOfParallelism = 1;

        static async Task<long> Count(VortexFile file)
        {
            long rows = 0;
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
            {
                using (batch)
                {
                    rows += batch.RowCount;
                }
            }

            return rows;
        }
    }
}
