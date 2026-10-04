using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>A query of the matrix: its name, the file it reads, and what one run of it does.</summary>
/// <param name="Name">The name the tables and the references use.</param>
/// <param name="Query">One run over the open file.</param>
/// <param name="ProbeEvery">The answers between two memory probes in the memory pass.</param>
internal sealed record Scenario(string Name, Func<VortexFile, Run, Task<long>> Query, int ProbeEvery = 1);

/// <summary>
/// The queries, against the surface as it stands. Each returns a number its work depends on, so that
/// nothing is optimized away and a hand-written loop can be checked against the operator it floors.
/// </summary>
internal static class Scenarios
{
    internal static IEnumerable<(string File, Scenario Scenario)> All(int large)
    {
        yield return ("readings", new Scenario("group by city (run-end), count avg", GroupByCityAsync));
        yield return ("readings", new Scenario("hand loop city (run-end), count sum", HandCityAsync));
        yield return ("readings", new Scenario("group by city (run-end), welford", GroupByCityWelfordAsync));
        yield return ("readings", new Scenario("group by day (sorted), count avg", GroupByDayAsync, 100));
        yield return ("readings", new Scenario("group by city day (composite), count avg", GroupByCityDayAsync, 1_000));
        yield return ("requests", new Scenario("group by endpoint (dictionary), count avg", GroupByEndpointAsync));
        yield return ("requests", new Scenario("hand loop endpoint (dictionary), count sum", HandEndpointAsync));
        yield return ("requests", new Scenario("group by endpoint (dictionary), errors in the group", ErrorsInTheGroupAsync));
        yield return ("requests", new Scenario("group by endpoint (dictionary), errors filtered before", ErrorsFilteredBeforeAsync));
        yield return ("requests", new Scenario("group by user (1M groups), count avg", GroupByUserAsync, 100_000));
        yield return ("requests", new Scenario("group by user (1M groups), as records", GroupByUserRecordsAsync, 100_000));
        yield return ("readings", new Scenario("first batch, full scan", FirstBatchAsync));
        yield return ($"readings-{large}", new Scenario($"first batch, full scan, {large / 1_000_000}M", FirstBatchAsync));
        yield return ("readings", new Scenario("first batch, scan filtered everywhere", FirstFilteredBatchAsync));
        yield return ($"readings-{large}", new Scenario($"first batch, scan filtered everywhere, {large / 1_000_000}M", FirstFilteredBatchAsync));
        yield return ("readings", new Scenario("first group, group by day (sorted)", FirstDayGroupAsync));
        yield return ($"readings-{large}", new Scenario($"first group, group by day (sorted), {large / 1_000_000}M", FirstDayGroupAsync));
    }

    private static async Task<long> GroupByCityAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<CityStats> groups in file.Scan<Reading>()
            .GroupBy(r => r.City)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Celsius)))
            .As<CityStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (groups.Column<double?>(2).NullCount == 0 ? 0 : 1);
        }

        return rows;
    }

    private static async Task<long> GroupByCityWelfordAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (WelfordState state in file.Scan<Reading>()
            .GroupBy(r => r.City)
            .Select(g => g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)))
        {
            run.Answer();
            rows += state.Count;
        }

        return rows;
    }

    /// <summary>What the guide's encoded-forms page writes by hand: runs of a city, a sum per run.</summary>
    private static async Task<long> HandCityAsync(VortexFile file, Run run)
    {
        long[] counts = new long[Fixtures.Cities.Length];
        double[] totals = new double[Fixtures.Cities.Length];
        await foreach (var (_, celsius, city) in file.Scan<Reading>())
        {
            ReadOnlySpan<double> values = celsius.Values;
            if (city.Encoding == ColumnEncoding.RunEnd)
            {
                RunEndView<string> runs = city.AsRunEnd();
                ReadOnlySpan<uint> ends = runs.RunEnds;
                for (int r = 0, start = 0; r < runs.RunCount; r++)
                {
                    int end = (int)ends[r];
                    double sum = 0;
                    long valid = 0;
                    for (int i = start; i < end; i++)
                    {
                        if (celsius.IsValid(i))
                        {
                            sum += values[i];
                            valid++;
                        }
                    }

                    int index = IndexOf(runs.Values[r]);
                    totals[index] += sum;
                    counts[index] += end - start + (valid < 0 ? 1 : 0);
                    start = end;
                }
            }
            else
            {
                for (int i = 0; i < city.Length; i++)
                {
                    int index = IndexOf(city[i]);
                    totals[index] += celsius.IsValid(i) ? values[i] : 0;
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

    private static async Task<long> GroupByDayAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<DayStats> groups in file.Scan<Reading>()
            .GroupBy(r => r.Day)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Celsius)))
            .As<DayStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (groups.Column<double?>(2).NullCount == 0 ? 0 : 1);
        }

        return rows;
    }

    private static async Task<long> GroupByCityDayAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<CityDayStats> groups in file.Scan<Reading>()
            .GroupBy(r => (r.City, r.Day))
            .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(r => r.Celsius)))
            .As<CityDayStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(2).Values) + (groups.Column<double?>(3).NullCount == 0 ? 0 : 1);
        }

        return rows;
    }

    private static async Task<long> GroupByEndpointAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<EndpointStats> groups in file.Scan<Request>()
            .GroupBy(r => r.Endpoint)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Latency)))
            .As<EndpointStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (groups.Column<double?>(2).NullCount == 0 ? 0 : 1);
        }

        return rows;
    }

    /// <summary>What the guide's encoded-forms page writes by hand: a dictionary's codes, a sum per code.</summary>
    private static async Task<long> HandEndpointAsync(VortexFile file, Run run)
    {
        Dictionary<string, (long Count, double Sum)> totals = new Dictionary<string, (long, double)>(StringComparer.Ordinal);
        long[] countByCode = [];
        double[] sumByCode = [];
        await foreach (Columns<Request> batch in file.Scan<Request>())
        {
            Column<string> endpoint = batch.Endpoint;
            ReadOnlySpan<double> latency = batch.Latency.Values;
            if (endpoint.Encoding != ColumnEncoding.Dictionary)
            {
                for (int i = 0; i < endpoint.Length; i++)
                {
                    string name = endpoint.GetString(i)!;
                    (long count, double sum) = totals.GetValueOrDefault(name);
                    totals[name] = (count + 1, sum + latency[i]);
                }

                continue;
            }

            DictionaryView<string> dict = endpoint.AsDictionary();
            ReadOnlySpan<uint> codes = dict.Codes;
            if (countByCode.Length < dict.Cardinality)
            {
                countByCode = new long[dict.Cardinality];
                sumByCode = new double[dict.Cardinality];
            }

            Array.Clear(countByCode);
            Array.Clear(sumByCode);
            for (int i = 0; i < codes.Length; i++)
            {
                countByCode[codes[i]]++;
                sumByCode[codes[i]] += latency[i];
            }

            for (int code = 0; code < dict.Cardinality; code++)
            {
                if (countByCode[code] > 0)
                {
                    string name = dict.Values.GetString(code)!;
                    (long count, double sum) = totals.GetValueOrDefault(name);
                    totals[name] = (count + countByCode[code], sum + sumByCode[code]);
                }
            }
        }

        long rows = 0;
        foreach ((string name, (long count, double sum)) in totals)
        {
            run.Answer();
            rows += count + (name.Length > 0 && sum > 0 ? 0 : 1);
        }

        return rows;
    }

    /// <summary>A filtered group: every request and the failures of each endpoint, in one pass.</summary>
    private static async Task<long> ErrorsInTheGroupAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<EndpointErrors> groups in file.Scan<Request>()
            .GroupBy(r => r.Endpoint)
            .Select(g => (g.Key, g.Count(), g.Count(r => r.Status >= 500), g.Where(r => r.Status >= 500).Average(r => r.Latency)))
            .As<EndpointErrors>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    /// <summary>The failures alone, the filter before the group by: the floor of the filtered group's predicate.</summary>
    private static async Task<long> ErrorsFilteredBeforeAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<EndpointStats> groups in file.Scan<Request>()
            .Where(r => r.Status >= 500)
            .GroupBy(r => r.Endpoint)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Latency)))
            .As<EndpointStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> GroupByUserAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<UserStats> groups in file.Scan<Request>()
            .GroupBy(r => r.UserId)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Latency)))
            .As<UserStats>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values) + (groups.Column<double?>(2).NullCount == 0 ? 0 : 1);
        }

        return rows;
    }

    /// <summary>The same groups as records: a row per group, the path a caller pays a record and its fields for.</summary>
    private static async Task<long> GroupByUserRecordsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (UserStats group in file.Scan<Request>()
            .GroupBy(r => r.UserId)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Latency)))
            .As<UserStats>()
            .ToRecordsAsync())
        {
            run.Answer();
            rows += group.Count + (group.User >= 0 && group.Mean >= 0 ? 0 : 1);
        }

        return rows;
    }

    private static async Task<long> FirstBatchAsync(VortexFile file, Run run)
    {
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            run.Answer();
            return batch.RowCount;
        }

        return 0;
    }

    /// <summary>A filter every block may match, so that the structures prune nothing and the first batch waits only for their read.</summary>
    private static async Task<long> FirstFilteredBatchAsync(VortexFile file, Run run)
    {
        await foreach (Columns<Reading> batch in file.Scan<Reading>().Where(r => r.Celsius > 49.0))
        {
            run.Answer();
            return batch.RowCount;
        }

        return 0;
    }

    private static async Task<long> FirstDayGroupAsync(VortexFile file, Run run)
    {
        await foreach (long count in file.Scan<Reading>()
            .GroupBy(r => r.Day)
            .Select(g => g.Count()))
        {
            run.Answer();
            return count;
        }

        return 0;
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
