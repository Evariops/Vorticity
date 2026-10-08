using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The queries of the engine's stage (PLAN-QUERIES-STREAMING.md, 6j): each line of the stage is judged
/// on one of them, and a query that tracks its aggregation reports its lanes and its merge.
/// </summary>
internal static class EngineScenarios
{
    internal static IEnumerable<(string File, Scenario Scenario)> All(int large)
    {
        // 6k: the group by that streams, against the same query forced to block.
        yield return ("readings", new Scenario("group by city day (composite), count avg, blocking", (file, run) => CityDayAsync(file, run, blocking: true), 1_000));
        yield return ("readings", new Scenario("group by day (sorted), welford", (file, run) => DayWelfordAsync(file, run, blocking: false), 100));
        yield return ("readings", new Scenario("group by day (sorted), welford, blocking", (file, run) => DayWelfordAsync(file, run, blocking: true), 100));

        // 6l: a filter that keeps 7 % of the rows, contiguous, on a sorted key.
        yield return ($"readings-{large}", new Scenario($"group by city under a 7 % day filter, {large / 1_000_000}M", CitiesOfSomeDaysAsync));

        // 6m, 6n, 6o, 6p: what follows the pass.
        yield return ("names", new Scenario("order by count take 100, text key (1M groups)", TopNamesAsync));
        yield return ("requests", new Scenario("order by max take 10 with its row, 1M groups", SlowestUsersAsync));
        yield return ("requests", new Scenario("ohlc by day (first, max, min, last)", OhlcAsync));

        // 6n: every group's chosen row, read after the pass, against the same query without it.
        yield return ("requests", new Scenario("max by user with its row, every group (1M groups)", (file, run) => SlowestOfEveryUserAsync(file, run, row: true)));
        yield return ("requests", new Scenario("max by user, every group (1M groups)", (file, run) => SlowestOfEveryUserAsync(file, run, row: false)));
        yield return ("draws", new Scenario("order by key take 10, random int (1M groups)", FirstKeysAsync));
        yield return ("readings", new Scenario("order by key descending take 7, 1000 days", LastDaysAsync));
        yield return ("draws", new Scenario("take 10 without order, random int (1M groups)", AnyKeysAsync));

        // 6c, 6q, 6d1: a table of keys and a merge, by cardinality.
        yield return ("draws", new Scenario("group by random int (4 groups), count sum", (file, run) => DrawsAsync(file, run, d => d.K4)));
        yield return ("draws", new Scenario("group by random int (100 groups), count sum", (file, run) => DrawsAsync(file, run, d => d.K100)));
        yield return ("draws", new Scenario("group by random int (1000 groups), count sum", (file, run) => DrawsAsync(file, run, d => d.K1000)));
        yield return ("draws", new Scenario("group by random int (100k groups), count sum", (file, run) => DrawsAsync(file, run, d => d.K100k), 10_000));
        yield return ("draws", new Scenario("group by random int (1M groups), count sum", (file, run) => DrawsAsync(file, run, d => d.K1M), 100_000));

        // 6b: a key of two fixed-width columns, at a few thousand groups and at nearly a group a row.
        yield return ("draws", new Scenario("group by (random int, random int) (4 000 groups), count sum", (file, run) => PairsAsync(file, run, d => d.K4, d => d.K1000)));
        yield return ("draws", new Scenario("group by (random int, random int) (1.8M groups), count sum", (file, run) => PairsAsync(file, run, d => d.K100k, d => d.K100), 100_000));

        // 6d2: a hot key and a long tail, and keys each seen twenty times.
        yield return ("skewed", new Scenario("group by skewed key (one key 30 %, 1M rare), count sum", KeyedAsync, 100_000));

        // The same key on a file of many chunks a lane: what the last ranges a queue hands out weigh.
        yield return ($"skewed-{large}", new Scenario($"group by skewed key (one key 30 %, 1M rare), count sum, {large / 1_000_000}M", KeyedAsync, 100_000));
        yield return ("medium", new Scenario("group by medium key (200k keys x 20), count sum", KeyedAsync, 10_000));
        yield return ("late", new Scenario("group by nearly sorted key (1M keys, late by 2 500), count sum", (file, run) => LateKeysAsync(file, run, blocking: false), 100_000));
        yield return ("late", new Scenario("group by nearly sorted key (1M keys, late by 2 500), count sum, blocking", (file, run) => LateKeysAsync(file, run, blocking: true), 100_000));
        yield return ("late", new Scenario("group by nearly sorted key (1M keys, late by 2 500), count sum, zones", (file, run) => LateKeysAsync(file, run, blocking: false, zones: true), 100_000));

        // The floors at equal work: a count and an integer sum, which a hand loop does exactly.
        yield return ("readings", new Scenario("group by city (run-end), count sum of an int", CityDaysAsync));
        yield return ("readings", new Scenario("hand loop city (run-end), count sum of an int", HandCityDaysAsync));
        yield return ("requests", new Scenario("group by endpoint (dictionary), count sum of an int", EndpointDurationsAsync));
        yield return ("requests", new Scenario("hand loop endpoint (dictionary), count sum of an int", HandEndpointDurationsAsync));
    }

    private static async Task<long> CityDayAsync(VortexFile file, Run run, bool blocking)
    {
        long rows = 0;
        Aggregation days = run.Track(file.Scan<Reading>()
            .GroupBy(r => (r.City, r.Day))
            .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(r => r.Celsius))));
        ((AggregationQuery)days.Query).Plan.Blocking = blocking;
        await foreach (Columns<CityDayStats> groups in days.As<CityDayStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    private static async Task<long> DayWelfordAsync(VortexFile file, Run run, bool blocking)
    {
        long rows = 0;
        Aggregation<WelfordState> days = run.Track(file.Scan<Reading>()
            .GroupBy(r => r.Day)
            .Select(g => g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)));
        ((AggregationQuery)days.Query).Plan.Blocking = blocking;
        await foreach (WelfordState state in days)
        {
            run.Answer();
            rows += state.Count;
        }

        return rows;
    }

    private static async Task<long> CitiesOfSomeDaysAsync(VortexFile file, Run run)
    {
        int days = (int)(file.RowCount / 1_000);
        int from = days / 2;
        int until = from + (days * 7 / 100);
        long rows = 0;
        await foreach (Columns<CityStats> groups in run.Track(file.Scan<Reading>()
            .Where(r => r.Day >= from & r.Day < until)
            .GroupBy(r => r.City)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Celsius))))
            .As<CityStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> TopNamesAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<NameCount> groups in run.Track(file.Scan<Named>()
            .GroupBy(n => n.Name)
            .OrderByDescending(g => g.Count())
            .Take(100)
            .Select(g => (g.Key, g.Count())))
            .As<NameCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> SlowestUsersAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (UserTop user in run.Track(file.Scan<Request>()
            .GroupBy(r => r.UserId)
            .OrderByDescending(g => g.Max(r => r.Latency))
            .Take(10)
            .Select(g => (g.Key, g.Max(r => r.Latency), g.MaxBy(r => r.Latency).At)))
            .As<UserTop>()
            .ToRecordsAsync())
        {
            run.Answer();
            rows += user.User + (user.At is null ? 1 : 0);
        }

        return rows;
    }

    private static async Task<long> SlowestOfEveryUserAsync(VortexFile file, Run run, bool row)
    {
        long rows = 0;
        if (row)
        {
            await foreach (Columns<UserTop> users in run.Track(file.Scan<Request>()
                .GroupBy(r => r.UserId)
                .Select(g => (g.Key, g.Max(r => r.Latency), g.MaxBy(r => r.Latency).At)))
                .As<UserTop>())
            {
                run.Answer();
                rows += users.RowCount;
            }

            return rows;
        }

        await foreach (Columns<UserSlowest> users in run.Track(file.Scan<Request>()
            .GroupBy(r => r.UserId)
            .Select(g => (g.Key, g.Max(r => r.Latency))))
            .As<UserSlowest>())
        {
            run.Answer();
            rows += users.RowCount;
        }

        return rows;
    }

    private static async Task<long> OhlcAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (DayBar day in run.Track(file.Scan<Request>()
            .GroupBy(r => r.At.Truncate(CalendarUnit.Day))
            .Select(g => (g.Key, g.First().Latency, g.Max(r => r.Latency), g.Min(r => r.Latency), g.Last().Latency)))
            .As<DayBar>()
            .ToRecordsAsync())
        {
            run.Answer();
            rows += day.Open is null || day.Close is null ? 1 : 2;
        }

        return rows;
    }

    private static async Task<long> FirstKeysAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Draw>()
            .GroupBy(d => d.K1M)
            .OrderBy(g => g.Key)
            .Take(10)
            .Select(g => (g.Key, g.Count(), g.Sum(d => d.Value))))
            .As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> LastDaysAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<DayCount> groups in run.Track(file.Scan<Reading>()
            .GroupBy(r => r.Day)
            .OrderByDescending(g => g.Key)
            .Take(7)
            .Select(g => (g.Key, g.Count())))
            .As<DayCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> AnyKeysAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Draw>()
            .GroupBy(d => d.K1M)
            .Take(10)
            .Select(g => (g.Key, g.Count(), g.Sum(d => d.Value))))
            .As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> DrawsAsync(VortexFile file, Run run, Func<Probe<Draw>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Draw>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Sum(d => d.Value))))
            .As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> PairsAsync(VortexFile file, Run run, Func<Probe<Draw>, Sym<int>> first, Func<Probe<Draw>, Sym<int>> second)
    {
        long rows = 0;
        await foreach (Columns<PairTotal> groups in run.Track(file.Scan<Draw>()
            .GroupBy(d => (first(d), second(d)))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Count(), g.Sum(d => d.Value))))
            .As<PairTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    /// <summary>A key its zones prove final as the read goes (6g), or the same query forced to block.</summary>
    private static async Task<long> LateKeysAsync(VortexFile file, Run run, bool blocking, bool zones = false)
    {
        long rows = 0;
        Aggregation keys = run.Track(file.Scan<Keyed>()
            .GroupBy(k => k.Key)
            .Select(g => (g.Key, g.Count(), g.Sum(k => k.Value))));
        ((AggregationQuery)keys.Query).Plan.Blocking = blocking;
        ((AggregationQuery)keys.Query).Plan.ZonesAtEveryDegree = zones;
        await foreach (Columns<KeyTotal> groups in keys.As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> KeyedAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Keyed>()
            .GroupBy(k => k.Key)
            .Select(g => (g.Key, g.Count(), g.Sum(k => k.Value))))
            .As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> CityDaysAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<CityDays> groups in run.Track(file.Scan<Reading>()
            .GroupBy(r => r.City)
            .Select(g => (g.Key, g.Count(), g.Sum(r => r.Day))))
            .As<CityDays>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (Sum(groups.Column<long>(2).Values) > 0 ? 0 : 1);
        }

        return rows;
    }

    /// <summary>The same work by hand: a count and an integer sum per run of a city.</summary>
    private static async Task<long> HandCityDaysAsync(VortexFile file, Run run)
    {
        long[] counts = new long[Fixtures.Cities.Length];
        long[] totals = new long[Fixtures.Cities.Length];
        await foreach (var (day, _, city) in file.Scan<Reading>())
        {
            ReadOnlySpan<int> values = day.Values;
            if (city.Encoding == ColumnEncoding.RunEnd)
            {
                RunEndView<string> runs = city.AsRunEnd();
                ReadOnlySpan<uint> ends = runs.RunEnds;
                for (int r = 0, start = 0; r < runs.RunCount; r++)
                {
                    int end = (int)ends[r];
                    long sum = 0;
                    for (int i = start; i < end; i++)
                    {
                        sum += values[i];
                    }

                    int index = IndexOf(runs.Values[r]);
                    totals[index] += sum;
                    counts[index] += end - start;
                    start = end;
                }
            }
            else
            {
                for (int i = 0; i < city.Length; i++)
                {
                    int index = IndexOf(city[i]);
                    totals[index] += values[i];
                    counts[index]++;
                }
            }
        }

        long rows = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            run.Answer();
            rows += counts[i] + (totals[i] > 0 ? 0 : 1);
        }

        return rows;
    }

    private static async Task<long> EndpointDurationsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<EndpointDurations> groups in run.Track(file.Scan<Request>()
            .GroupBy(r => r.Endpoint)
            .Select(g => (g.Key, g.Count(), g.Sum(r => r.DurationMs))))
            .As<EndpointDurations>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (Sum(groups.Column<long>(2).Values) > 0 ? 0 : 1);
        }

        return rows;
    }

    /// <summary>The same work by hand: a count and an integer sum per code of the dictionary, folded into the names once per block.</summary>
    private static async Task<long> HandEndpointDurationsAsync(VortexFile file, Run run)
    {
        Dictionary<string, (long Count, long Sum)> totals = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        long[] countByCode = [];
        long[] sumByCode = [];
        await foreach (Columns<Request> batch in file.Scan<Request>())
        {
            Column<string> endpoint = batch.Endpoint;
            ReadOnlySpan<int> durations = batch.DurationMs.Values;
            if (endpoint.Encoding != ColumnEncoding.Dictionary)
            {
                for (int i = 0; i < endpoint.Length; i++)
                {
                    string name = endpoint.GetString(i)!;
                    (long count, long sum) = totals.GetValueOrDefault(name);
                    totals[name] = (count + 1, sum + durations[i]);
                }

                continue;
            }

            DictionaryView<string> dict = endpoint.AsDictionary();
            ReadOnlySpan<uint> codes = dict.Codes;
            if (countByCode.Length < dict.Cardinality)
            {
                countByCode = new long[dict.Cardinality];
                sumByCode = new long[dict.Cardinality];
            }

            Array.Clear(countByCode);
            Array.Clear(sumByCode);
            for (int i = 0; i < codes.Length; i++)
            {
                countByCode[codes[i]]++;
                sumByCode[codes[i]] += durations[i];
            }

            for (int code = 0; code < dict.Cardinality; code++)
            {
                if (countByCode[code] > 0)
                {
                    string name = dict.Values.GetString(code)!;
                    (long count, long sum) = totals.GetValueOrDefault(name);
                    totals[name] = (count + countByCode[code], sum + sumByCode[code]);
                }
            }
        }

        long rows = 0;
        foreach ((string name, (long count, long sum)) in totals)
        {
            run.Answer();
            rows += count + (name.Length > 0 && sum > 0 ? 0 : 1);
        }

        return rows;
    }

    private static long Sum(ReadOnlySpan<long> values)
    {
        long sum = 0;
        foreach (long value in values)
        {
            sum += value;
        }

        return sum;
    }

    private static readonly byte[][] CityNames = [.. Array.ConvertAll(Fixtures.Cities, Encoding.UTF8.GetBytes)];

    private static int IndexOf(ReadOnlySpan<byte> city)
    {
        for (int c = 0; c < CityNames.Length; c++)
        {
            if (city.SequenceEqual(CityNames[c]))
            {
                return c;
            }
        }

        throw new InvalidOperationException("a city the fixture does not write");
    }
}
