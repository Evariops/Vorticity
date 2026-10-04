using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class Queries
{
    internal static async Task RunAsync()
    {
        await using VortexFile readings = await VortexFile.OpenAsync(await Demo.ReadingsAsync());
        await using VortexFile visits = await VortexFile.OpenAsync(await Demo.VisitsAsync());

        // Query syntax: the compiler turns it into the method chain below, and both build one plan.
        Scan<DayCityHeat> hottest =
            (from r in readings.Scan<Reading>()
             where r.Day >= 900
             group r by (r.Day, r.City) into g
             where g.Count() > 100
             orderby g.Key.Day, g.Average(x => x.Celsius) descending
             select (g.Key.Day, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
            .Take(4)
            .As<DayCityHeat>();

        Scan<DayCityHeat> chained = readings.Scan<Reading>()
            .Where(r => r.Day >= 900)
            .GroupBy(r => (r.Day, r.City))
            .Where(g => g.Count() > 100)
            .OrderBy(g => g.Key.Day).ThenByDescending(g => g.Average(x => x.Celsius))
            .Select(g => (g.Key.Day, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
            .Take(4)
            .As<DayCityHeat>();

        List<DayCityHeat> fromQuery = await ToListAsync(hottest);
        List<DayCityHeat> fromChain = await ToListAsync(chained);
        foreach (DayCityHeat day in fromQuery)
        {
            Console.WriteLine($"day {day.Day} {day.City,-10} {day.Rows,4} rows, mean {day.Mean:F2}");
        }

        Console.WriteLine($"the query and the chain agree: {fromQuery.SequenceEqual(fromChain)}");

        // One value a group comes as itself; several need a record, which names and types them.
        Aggregation<long> perCity = from r in readings.Scan<Reading>() group r by r.City into g select g.Count();
        List<long> counts = await perCity.ToListAsync();
        Console.WriteLine($"rows per city: {string.Join(", ", counts)}");

        // A key the statistics say is sorted streams: the first group goes out before the file is read.
        double firstDay = double.MaxValue;
        double allDays = double.MaxValue;
        long dayGroups = 0;
        long batchesAtFirst = 0;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Scan<Reading> days = readings.Scan<Reading>();
            dayGroups = 0;
            await foreach (long rows in days.GroupBy(r => r.Day).Select(g => g.Count()))
            {
                if (dayGroups++ == 0)
                {
                    firstDay = Math.Min(firstDay, clock.Elapsed.TotalMilliseconds);
                    batchesAtFirst = days.Statistics.Batches;
                }
            }

            allDays = Math.Min(allDays, clock.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine(
            $"GroupBy(Day): the first of {dayGroups} groups after {firstDay:F2} ms and {batchesAtFirst} batch(es), " +
            $"all of them after {allDays:F2} ms");

        // Down the sorted key under a take: the chunks are read from the last one back, and the read
        // stops once the seventh day is out.
        Scan<Reading> lastDays = readings.Scan<Reading>();
        List<DayRows> seven = await ToListAsync(lastDays.GroupBy(r => r.Day).OrderByDescending(g => g.Key).Take(7)
            .Select(g => (g.Key, g.Count())).As<DayRows>());
        Console.WriteLine($"the last seven days, {seven[0].Day} down to {seven[^1].Day}: {lastDays.Statistics.Rows} rows read of a million");

        // An order on a key that does not stream, under a take: each lane keeps its best keys alone.
        List<DurationVisits> shortest = [];
        List<DurationVisits> every = [];
        double top = await BestOfThreeAsync(async () => shortest = await ToListAsync(visits.Scan<Visit>()
            .GroupBy(v => v.DurationMs).OrderBy(g => g.Key).Take(5).Select(g => (g.Key, g.Count())).As<DurationVisits>()));
        double whole = await BestOfThreeAsync(async () => every = await ToListAsync(visits.Scan<Visit>()
            .GroupBy(v => v.DurationMs).Select(g => (g.Key, g.Count())).As<DurationVisits>()));
        Console.WriteLine(
            $"the five shortest durations, {shortest[0].DurationMs} to {shortest[^1].DurationMs} ms, in {top:F2} ms; " +
            $"every one of the {every.Count} durations in {whole:F2} ms");
    }

    /// <summary>The best of three passes of <paramref name="run"/>, in milliseconds.</summary>
    private static async Task<double> BestOfThreeAsync(Func<Task> run)
    {
        double best = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await run();
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    private static async Task<List<T>> ToListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync())
        {
            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>A city's day: its readings and their mean.</summary>
[VortexRecord]
public partial record struct DayCityHeat(int Day, string City, long Rows, double? Mean);

/// <summary>A day and its readings.</summary>
[VortexRecord]
public partial record struct DayRows(int Day, long Rows);

/// <summary>A duration and the visits that lasted it.</summary>
[VortexRecord]
public partial record struct DurationVisits(int DurationMs, long Visits);
