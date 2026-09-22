using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class FilterRows
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        double? maxCelsius = 30.0;

        Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900 && r.City == "Paris");
        if (maxCelsius is double max)
        {
            scan.Where(r => r.Celsius <= max);
        }

        ScanPlan plan = await scan.ExplainAsync();
        long rows = 0;
        int earliest = int.MaxValue;
        await foreach (var (day, _, _) in scan)
        {
            rows += day.Length;
            earliest = Math.Min(earliest, day[0]);
        }

        ScanStatistics stats = scan.Statistics;

        Console.WriteLine($"plan: {plan.LiveBlocks} of {plan.Blocks} blocks, {plan.Segments} segments, {plan.BytesToRead} bytes");
        foreach (PruningStep step in plan.Pruning)
        {
            Console.WriteLine($"  {step.Structure} pruned {step.BlocksPruned} blocks, reading {step.BytesRead} bytes to decide");
        }

        Console.WriteLine($"{rows} rows, from day {earliest}");
        Console.WriteLine($"ran: {stats.BlocksDecoded} blocks decoded, {stats.BlocksPruned} pruned, {stats.Requests} requests, {stats.BytesRequested} bytes");

        int calls = 0;
        Scan<Reading> counted = file.Scan<Reading>().Where(r =>
        {
            calls++;
            return r.Day >= 900;
        });
        long kept = await counted.CountAsync();
        Console.WriteLine($"the lambda ran {calls} time for {kept} rows");

        await CountAsync("r.Day >= 900", file.Scan<Reading>().Where(r => r.Day >= 900));
        await CountAsync("r.Celsius > 45.0", file.Scan<Reading>().Where(r => r.Celsius > 45.0));
        await CountAsync("r.City == \"Paris\"", file.Scan<Reading>().Where(r => r.City == "Paris"));
        await CountAsync("r.Day.In(1, 2, 3)", file.Scan<Reading>().Where(r => r.Day.In(1, 2, 3)));
        await CountAsync("r.City.StartsWith(\"L\")", file.Scan<Reading>().Where(r => r.City.StartsWith("L")));
        await CountAsync("r.Celsius.Between(10.0, 20.0)", file.Scan<Reading>().Where(r => r.Celsius.Between(10.0, 20.0)));
        await CountAsync("r.Celsius == null", file.Scan<Reading>().Where(r => r.Celsius == null));
        await CountAsync("!(r.Celsius > 45.0)", file.Scan<Reading>().Where(r => !(r.Celsius > 45.0)));

        await using VortexFile visits = await VortexFile.OpenAsync(await Demo.VisitsAsync());
        await CountAsync("https referrer from FR or BE", visits.Scan<Visit>()
            .Where(v => v.Referrer != null && v.Referrer.StartsWith("https://") && v.Origin.Country.In("FR", "BE")));
        await CountAsync("v.Pages.Contains(7)", visits.Scan<Visit>().Where(v => v.Pages.Contains(7)));

        int? fromDay = 500;
        int? toDay = null;
        string? city = "Lyon";
        Scan<Reading> optional = file.Scan<Reading>().Where(r =>
        {
            Predicate filter = Predicate.All;
            if (fromDay is int from)
            {
                filter &= r.Day >= from;
            }

            if (toDay is int to)
            {
                filter &= r.Day <= to;
            }

            if (city is not null)
            {
                filter &= r.City == city;
            }

            Console.WriteLine($"built from optional parts: {filter}");
            return filter;
        });
        Console.WriteLine($"  {await optional.CountAsync()} rows");

        Console.WriteLine($"MayMatch day >= 900: {file.MayMatch<Reading>(r => r.Day >= 900)}, day >= 5000: {file.MayMatch<Reading>(r => r.Day >= 5000)}");
    }

    private static async Task CountAsync<TRecord>(string text, Scan<TRecord> scan)
        where TRecord : IVortexRecord<TRecord>
    {
        ScanPlan plan = await scan.ExplainAsync();
        long count = await scan.CountAsync();
        ScanStatistics stats = scan.Statistics;
        string how = plan.Count.Exact ? "exact from the structures" : $"{plan.Count.Pruned} pruned, {plan.Count.Proven} proven, {plan.Count.Decoded} to evaluate";
        Console.WriteLine($"{text,-32} {count,7} rows  {plan.LiveBlocks,3} of {plan.Blocks} blocks live  {stats.Requests,3} requests {stats.BytesRequested,8} bytes  ({how})");
    }
}
