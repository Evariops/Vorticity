using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class EncodedForms
{
    private static readonly byte[][] CityNames = Array.ConvertAll(Demo.Cities, Encoding.UTF8.GetBytes);

    internal static async Task RunAsync()
    {
        string readings = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(readings);
        string visits = await Demo.VisitsAsync();
        await using VortexFile visitFile = await VortexFile.OpenAsync(visits);

        await CensusAsync(file, visitFile);

        double[] byRun = [];
        await BestOfThreeAsync("City by run, by hand", async () => (byRun = await CelsiusByCityAsync(file)).Length);

        Dictionary<string, double> grouped = [];
        ScanStatistics cityStats = default;
        await BestOfThreeAsync("GroupBy(r => r.City)", async () =>
        {
            grouped.Clear();
            Aggregation<(string, double)> byCity = file.Scan<Reading>()
                .GroupBy(r => r.City)
                .Select(g => (g.Key, g.Sum(r => r.Celsius)));
            await foreach ((string city, double total) in byCity)
            {
                grouped[city] = total;
            }

            cityStats = byCity.Statistics;
            return grouped.Count;
        });

        int agree = 0;
        for (int c = 0; c < Demo.Cities.Length; c++)
        {
            agree += Math.Abs(grouped[Demo.Cities[c]] - byRun[c]) < 1e-6 ? 1 : 0;
        }

        Console.WriteLine($"  {agree} of {Demo.Cities.Length} cities agree, Paris {grouped["Paris"]:F1}; the GroupBy decoded {cityStats.BlocksDecoded} blocks");

        Dictionary<string, (long Visits, long Duration)> byCode = [];
        await BestOfThreeAsync("Referrer by code, by hand", async () => (byCode = await VisitsByReferrerAsync(visitFile)).Count);

        Dictionary<string, (long Visits, long Duration)> byGroup = [];
        ScanStatistics referrerStats = default;
        await BestOfThreeAsync("GroupBy(v => v.Referrer)", async () =>
        {
            byGroup.Clear();
            Aggregation<(string?, long, int)> byReferrer = visitFile.Scan<Visit>()
                .GroupBy(v => v.Referrer)
                .Select(g => (g.Key, g.Count(), g.Sum(v => v.DurationMs)));
            await foreach ((string? referrer, long count, int duration) in byReferrer)
            {
                byGroup[referrer ?? "(null)"] = (count, duration);
            }

            referrerStats = byReferrer.Statistics;
            return byGroup.Count;
        });

        int same = 0;
        foreach ((string referrer, (long Visits, long Duration) totals) in byGroup)
        {
            same += byCode.TryGetValue(referrer, out (long Visits, long Duration) mine) && mine == totals ? 1 : 0;
        }

        Console.WriteLine($"  {same} of {byGroup.Count} groups agree, null referrers {byGroup["(null)"].Visits}; the GroupBy decoded {referrerStats.BlocksDecoded} blocks");
    }

    private static async Task<double[]> CelsiusByCityAsync(VortexFile file)
    {
        double[] totalByCity = new double[Demo.Cities.Length];
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
                    for (int i = start; i < end; i++)
                    {
                        sum += celsius.IsValid(i) ? values[i] : 0;
                    }

                    totalByCity[IndexOf(runs.Values[r])] += sum;
                    start = end;
                }
            }
            else
            {
                for (int i = 0; i < city.Length; i++)
                {
                    totalByCity[IndexOf(city[i])] += celsius.IsValid(i) ? values[i] : 0;
                }
            }
        }

        return totalByCity;
    }

    private static async Task<Dictionary<string, (long Visits, long Duration)>> VisitsByReferrerAsync(VortexFile file)
    {
        Dictionary<string, (long Visits, long Duration)> totals = [];
        long[] visitsByCode = [];
        long[] durationByCode = [];
        (long Visits, long Duration) nulls = default;
        await foreach (Columns<Visit> v in file.Scan<Visit>())
        {
            Column<string?> referrer = v.Referrer;
            ReadOnlySpan<int> duration = v.DurationMs.Values;
            if (referrer.Encoding != ColumnEncoding.Dictionary)
            {
                throw new InvalidOperationException("this sample reads the referrer as the writer stored it: a dictionary");
            }

            DictionaryView<string?> dict = referrer.AsDictionary();
            ReadOnlySpan<uint> codes = dict.Codes;
            if (visitsByCode.Length < dict.Cardinality)
            {
                visitsByCode = new long[dict.Cardinality];
                durationByCode = new long[dict.Cardinality];
            }

            Array.Clear(visitsByCode);
            Array.Clear(durationByCode);
            for (int i = 0; i < codes.Length; i++)
            {
                if (!referrer.IsValid(i))
                {
                    nulls = (nulls.Visits + 1, nulls.Duration + duration[i]);
                    continue;
                }

                visitsByCode[codes[i]]++;
                durationByCode[codes[i]] += duration[i];
            }

            for (int code = 0; code < dict.Cardinality; code++)
            {
                if (visitsByCode[code] == 0)
                {
                    continue;
                }

                string name = dict.Values.GetString(code)!;
                (long Visits, long Duration) sofar = totals.GetValueOrDefault(name);
                totals[name] = (sofar.Visits + visitsByCode[code], sofar.Duration + durationByCode[code]);
            }
        }

        totals["(null)"] = nulls;
        return totals;
    }

    private static async Task CensusAsync(VortexFile file, VortexFile visitFile)
    {
        Census census = new Census();
        await foreach (var (day, celsius, city) in file.Scan<Reading>())
        {
            census.Add("readings", "Day", day.Encoding);
            census.Add("readings", "Celsius", celsius.Encoding);
            census.Add("readings", "City", city.Encoding);
        }

        await foreach (Columns<Visit> v in visitFile.Scan<Visit>())
        {
            census.Add("visits", "Id", v.Id.Encoding);
            census.Add("visits", "StartedAt", v.StartedAt.Encoding);
            census.Add("visits", "DurationMs", v.DurationMs.Encoding);
            census.Add("visits", "Referrer", v.Referrer.Encoding);
            census.Add("visits", "Pages", v.Pages.Encoding);
            census.Add("visits", "Pages.Elements", v.Pages.Elements.Encoding);
            census.Add("visits", "Origin.Country", v.Origin.Country.Encoding);
            census.Add("visits", "Origin.City", v.Origin.City.Encoding);
        }

        await foreach (var (day, celsius, city) in file.Scan<Reading>().Where(r => r.Day == 500))
        {
            census.Add("Day == 500", "Day", day.Encoding);
            census.Add("Day == 500", "Celsius", celsius.Encoding);
            census.Add("Day == 500", "City", city.Encoding);
        }

        census.Print();

        await foreach (var (day, celsius, city) in file.Scan<Reading>())
        {
            DictionaryView<double?> temperatures = celsius.AsDictionary();
            RunEndView<int> days = day.AsRunEnd();
            RunEndView<string> cities = city.AsRunEnd();
            Column<string> decoded = city.Canonical();
            Console.WriteLine($"first batch: {day.Length} rows; Celsius {temperatures.Cardinality} distinct values, {temperatures.Values.NullCount} of them null; Day {days.RunCount} runs, City {cities.RunCount} runs; City.Canonical() is {decoded.Encoding}");
            break;
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> name)
    {
        for (int c = 0; c < CityNames.Length; c++)
        {
            if (name.SequenceEqual(CityNames[c]))
            {
                return c;
            }
        }

        throw new InvalidOperationException($"no city named {Encoding.UTF8.GetString(name)}");
    }

    private static async Task BestOfThreeAsync(string name, Func<Task<int>> run)
    {
        int groups = 0;
        TimeSpan best = TimeSpan.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            groups = await run();
            best = clock.Elapsed < best ? clock.Elapsed : best;
        }

        Console.WriteLine($"{name,-26} {groups,2} groups, {best.TotalMilliseconds,6:F1} ms");
    }

    private sealed class Census
    {
        private readonly Dictionary<(string Scan, string Column), int[]> _counts = [];
        private readonly List<(string Scan, string Column)> _order = [];

        public void Add(string scan, string column, ColumnEncoding encoding)
        {
            if (!_counts.TryGetValue((scan, column), out int[]? counts))
            {
                counts = new int[4];
                _counts.Add((scan, column), counts);
                _order.Add((scan, column));
            }

            counts[(int)encoding]++;
        }

        public void Print()
        {
            Console.WriteLine($"{"batches of",-11} {"column",-15} {"canonical",9} {"dictionary",10} {"run-end",7} {"constant",8}");
            foreach ((string scan, string column) in _order)
            {
                int[] c = _counts[(scan, column)];
                Console.WriteLine($"{scan,-11} {column,-15} {c[0],9} {c[1],10} {c[2],7} {c[3],8}");
            }
        }
    }
}
