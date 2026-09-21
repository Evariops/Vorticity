using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class GettingStarted
{
    internal static async Task RunAsync()
    {
        string path = Demo.Path("getting-started.vortex");
        await Demo.WriteReadingsAsync(path, VortexWriteOptions.Default);

        await using VortexFile file = await VortexFile.OpenAsync(path);
        double total = 0;
        long seen = 0;
        await foreach (RecordBatch batch in file.Scan().Project(["celsius"]).ExecuteAsync())
        {
            using (batch)
            {
                total += Sum(batch);
                seen += batch.RowCount;
            }
        }

        Console.WriteLine($"{seen} rows of {file.RowCount}, mean {total / seen:F2} degrees");
        Console.WriteLine($"{Demo.ReadingRows * 12} bytes of values became a file of {file.FileLength}");
    }

    private static double Sum(RecordBatch batch)
    {
        // The values are the decoded column itself, not a copy of it.
        ReadOnlySpan<double> values = batch.Column("celsius"u8).AsPrimitive<double>().Values;
        double sum = 0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum;
    }
}
