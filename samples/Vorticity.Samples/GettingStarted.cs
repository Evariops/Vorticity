using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class GettingStarted
{
    internal static async Task RunAsync()
    {
        string path = Demo.Path("getting-started.vortex");
        string[] cities = ["Paris", "Lyon", "Marseille", "Toulouse"];

        Reading[] readings = new Reading[100_000];
        for (int i = 0; i < readings.Length; i++)
        {
            double? celsius = i % 50 == 0 ? null : 10.0 + i % 400 / 10.0;
            readings[i] = new Reading(i / 1_000, celsius, cities[i / 7 % cities.Length]);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            await writer.WriteAsync<Reading>(readings);
            WriteReport report = await writer.CompleteAsync();
            Console.WriteLine($"wrote {report.RowCount} rows in {report.Bytes.Total} bytes");
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        Console.WriteLine($"{file.Schema}, {file.RowCount} rows");

        long batches = 0;
        long rows = 0;
        long nulls = 0;
        await foreach (var (day, celsius, _) in file.Scan<Reading>())
        {
            batches++;
            rows += day.Length;
            nulls += celsius.NullCount;
        }

        Console.WriteLine($"{batches} batches, {rows} rows, {nulls} without a temperature");

        double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);
        Console.WriteLine($"mean {mean:F2} degrees");

        long hot = await file.Scan<Reading>().Where(r => r.Celsius > 45.0 && r.City == "Paris").CountAsync();
        Console.WriteLine($"{hot} readings above 45 degrees in Paris");
    }
}
