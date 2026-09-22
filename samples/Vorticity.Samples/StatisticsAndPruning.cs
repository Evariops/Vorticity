using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class StatisticsAndPruning
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        Console.WriteLine($"the file: {file.Length} bytes, {file.RowCount} rows");

        ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync();
        Print("Day >= 900", plan);

        Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
        await foreach (Columns<Reading> _ in scan)
        {
        }

        Print("Day >= 900", scan.Statistics);

        ScanPlan hot = await file.Scan<Reading>().Where(r => r.Celsius > 45.0).ExplainAsync();
        Print("Celsius > 45", hot);
        Scan<Reading> hotScan = file.Scan<Reading>().Where(r => r.Celsius > 45.0);
        await foreach (Columns<Reading> _ in hotScan)
        {
        }

        Print("Celsius > 45", hotScan.Statistics);

        Scan<Reading> everything = file.Scan<Reading>();
        await foreach (Columns<Reading> _ in everything)
        {
        }

        Print("no filter", everything.Statistics);

        ScanPlan none = await file.Scan<Reading>().Where(r => r.Day >= 5_000).ExplainAsync();
        Print("Day >= 5000", none);
        Scan<Reading> noneScan = file.Scan<Reading>().Where(r => r.Day >= 5_000);
        await foreach (Columns<Reading> _ in noneScan)
        {
        }

        Print("Day >= 5000", noneScan.Statistics);

        VortexFileStatistics statistics = file.Statistics;
        for (int i = 0; i < statistics.Count; i++)
        {
            FieldStatistics field = statistics[i];
            string name = file.Schema[i].Name;
            string bounds = name switch
            {
                "Day" => field.TryGetMin(out int low) && field.TryGetMax(out int high) ? $"{low} to {high}" : "none exact",
                "Celsius" => field.TryGetMin(out double? cold) && field.TryGetMax(out double? warm) ? $"{cold} to {warm}" : "none exact",
                _ => field.TryGetMin(out string? first) && field.TryGetMax(out string? last) ? $"{first} to {last}" : "none exact",
            };
            string nulls = field.TryGetNullCount(out long count) ? count.ToString() : "unknown";
            string sorted = field.TryGetIsSorted(out bool isSorted) ? isSorted.ToString() : "unknown";
            string constant = field.TryGetIsConstant(out bool isConstant) ? isConstant.ToString() : "unknown";
            Console.WriteLine($"  {name}: bounds {bounds}, nulls {nulls}, sorted {sorted}, constant {constant}");
        }

        string daySum = statistics[0].TryGetSum(out long days) ? days.ToString() : "not recorded";
        string celsiusSum = statistics[1].TryGetSum(out double degrees) ? degrees.ToString("F1") : "not recorded";
        Console.WriteLine($"  sums: Day {daySum}, Celsius {celsiusSum}");

        Console.WriteLine($"MayMatch(Day >= 900): {file.MayMatch<Reading>(r => r.Day >= 900)}");
        Console.WriteLine($"MayMatch(Day >= 5000): {file.MayMatch<Reading>(r => r.Day >= 5_000)}");
        Console.WriteLine($"MayMatch(Celsius > 100): {file.MayMatch<Reading>(r => r.Celsius > 100.0)}");
        Console.WriteLine($"MayMatch(City == \"Paris\"): {file.MayMatch<Reading>(r => r.City == "Paris")}");

        long count900 = await file.Scan<Reading>().Where(r => r.Day >= 900).CountAsync();
        Console.WriteLine($"CountAsync(Day >= 900): {count900}");

        await ClusteredAsync();
    }

    private static async Task ClusteredAsync()
    {
        foreach (bool clustered in new[] { false, true })
        {
            string path = Demo.Path(clustered ? "by-city.vortex" : "cycling-cities.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
            {
                Reading[] rows = new Reading[200_000];
                for (int i = 0; i < rows.Length; i++)
                {
                    string city = clustered ? Demo.Cities[i * Demo.Cities.Length / rows.Length] : Demo.Cities[i % Demo.Cities.Length];
                    rows[i] = new Reading(i / 1_000, 20.0, city);
                }

                await writer.WriteAsync<Reading>(rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            ScanPlan plan = await file.Scan<Reading>().Where(r => r.City == "Paris").ExplainAsync();
            long rows200 = await file.Scan<Reading>().Where(r => r.City == "Paris").CountAsync();
            Console.WriteLine($"City == \"Paris\" over 200000 rows, {(clustered ? "clustered by city" : "cities cycling row by row")}: {plan.LiveBlocks} of {plan.Blocks} blocks live, {rows200} rows");
        }
    }

    private static void Print(string what, ScanPlan plan)
    {
        Console.WriteLine($"plan, {what}: {plan.Rows} rows, {plan.LiveBlocks} of {plan.Blocks} blocks live, {plan.Segments} segments, {plan.BytesToRead} bytes to read, may match {plan.MayMatch}");
        foreach (PruningStep step in plan.Pruning)
        {
            Console.WriteLine($"  {step.Structure}: {step.BlocksPruned} blocks pruned, {step.SegmentsRead} segments and {step.BytesRead} bytes read to decide");
        }

        CountPlan count = plan.Count;
        Console.WriteLine($"  count: exact {count.Exact}, {count.Rows} rows, {count.Pruned} pruned, {count.Proven} proven, {count.Decoded} decoded");
    }

    private static void Print(string what, ScanStatistics stats) =>
        Console.WriteLine($"ran, {what}: {stats.Rows} rows in {stats.Batches} batches, {stats.Requests} requests, {stats.BytesRequested} bytes, {stats.BlocksDecoded} blocks decoded, {stats.BlocksPruned} pruned");
}
