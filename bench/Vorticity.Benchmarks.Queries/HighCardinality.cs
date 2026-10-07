using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The high-cardinality bench (PLAN-HIGH-CARDINALITY.md, H0b): keys in no order, in the order of the
/// rows, and at a regular stride, from 10³ to 10⁷ groups; ten rows a key, a key a row, a Zipf law,
/// hot keys that drift; text, UUID and composite keys; distinct counts and the operators after a
/// group by. Two matrices name them (<c>--matrix small</c>, <c>--matrix full</c>), so that an A/B
/// runs with one command.
/// </summary>
internal static class HighCardinality
{
    /// <summary>The rows of the small matrix's files.</summary>
    internal const int SmallRows = 4_000_000;

    /// <summary>The rows of the full matrix's files: ten million keys come back twice.</summary>
    internal const int FullRows = 20_000_000;

    /// <summary>The distributions of the matrix, each a file.</summary>
    internal static readonly string[] Distributions = ["random", "ordered", "strided"];

    internal static IEnumerable<(string File, Scenario Scenario)> All()
    {
        // The small matrix: every distribution at a thousand and a million groups.
        foreach (string distribution in Distributions)
        {
            foreach (string groups in (string[])["1e3", "1e6"])
            {
                yield return Spread(distribution, SmallRows, groups, "count sum", Matrix.Small);
            }
        }

        // The full matrix: every cardinality of every distribution; every set of aggregates in no order.
        foreach (string distribution in Distributions)
        {
            foreach (string groups in (string[])["1e3", "1e4", "1e5", "1e6", "1e7"])
            {
                yield return Spread(distribution, FullRows, groups, "count sum", Matrix.Full);
                if (distribution == "random")
                {
                    yield return Spread(distribution, FullRows, groups, "count", Matrix.Full);
                    yield return Spread(distribution, FullRows, groups, "count mean", Matrix.Full);
                    yield return Spread(distribution, FullRows, groups, "four", Matrix.Full);
                    yield return Spread(distribution, FullRows, groups, "count range", Matrix.Full);
                    yield return Spread(distribution, FullRows, groups, "count deviation", Matrix.Full);
                }
            }
        }

        // Ten rows a key, where the lanes' batches outnumber the groups; a key a row, where nothing folds.
        foreach (string key in (string[])["tenfold", "unique"])
        {
            yield return Spread("random", FullRows, key, "count sum", Matrix.Full);
            yield return Spread("random", FullRows, key, "four", Matrix.Full);
        }

        // A distinct count by a million keys, twenty values a key: the pairs a merge in parts hands
        // each part (PLAN-HIGH-CARDINALITY, H9).
        yield return Spread("random", FullRows, "1e6", "count distinct", Matrix.Full);

        // A popularity law and hot keys that change as the rows go.
        yield return ("skews", new Scenario("hc zipf 1e6, count sum", (file, run) => SkewsAsync(file, run, zipf: true), 16, Matrix.Full));
        yield return ("skews", new Scenario("hc drift, count sum", (file, run) => SkewsAsync(file, run, zipf: false), 16, Matrix.Full));

        // Text, UUID and composite keys, and the extremes of a text.
        yield return ("pages", new Scenario("hc text key 1e6 (urls), count", UrlsAsync, 16, Matrix.Full));
        yield return ("few-names", new Scenario("hc text key 1e3 (names), count", NamesAsync, 1, Matrix.Full));
        yield return ("pages", new Scenario("hc uuid key 1e6, count", IdsAsync, 16, Matrix.Full));
        yield return ("pages", new Scenario("hc (text, int) key, count", UrlSmallsAsync, 16, Matrix.Full));
        yield return ("pages", new Scenario("hc int key 1e6, min and max of a text", TextExtremesAsync, 16, Matrix.Full));

        // Distinct counts, by group and over the whole scan.
        yield return ("visits", new Scenario("hc distinct users by day (365 groups, 1e7 users)", DistinctByDayAsync, 1, Matrix.Full));
        yield return ("visits", new Scenario("hc distinct users over the scan (1e7 users)", DistinctUsersAsync, 1, Matrix.Full));

        // What follows a group by of a million groups; on a text key, the ranking against the same group
        // by delivered in no order (PLAN-HIGH-CARDINALITY, H7).
        yield return ($"spread-random-{FullRows}", new Scenario("hc random 1e6, order by count take 100", TopCountsAsync, 1, Matrix.Full));
        yield return ($"spread-random-{FullRows}", new Scenario("hc random tenfold, where count > 10", HavingAsync, 1, Matrix.Full));
        yield return ("pages", new Scenario("hc text key 1e6 (urls), order by count take 100", TopUrlsAsync, 16, Matrix.Full));
        yield return ($"spread-random-{FullRows}", new Scenario("hc random 1e6, order by max take 100", TopMostAsync, 1, Matrix.Full));

        // The distinct values of a column, a million and ten million of them (PLAN-HIGH-CARDINALITY, H13).
        yield return ($"spread-random-{FullRows}", new Scenario("hc random 1e6, distinct", (file, run) => DistinctAsync(file, run, s => s.K6), 16, Matrix.Full));
        yield return ($"spread-random-{FullRows}", new Scenario("hc random 1e7, distinct", (file, run) => DistinctAsync(file, run, s => s.K7), 16, Matrix.Full));
    }

    private static async Task<long> DistinctAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long values = 0;
        await foreach (int value in run.TrackDistinct(file.Scan<Spread>().Select(key).Distinct()))
        {
            if (values++ == 0)
            {
                run.Answer();
            }
        }

        return values;
    }

    /// <summary>A group by of the matrix: its distribution's file, a key column, a set of aggregates.</summary>
    private static (string File, Scenario Scenario) Spread(string distribution, int rows, string groups, string aggregates, Matrix matrix)
    {
        string name = $"hc {distribution} {groups}{(rows == SmallRows ? " small" : string.Empty)}, {aggregates}";
        int probe = groups is "1e7" or "unique" or "tenfold" ? 16 : 1;
        Func<VortexFile, Run, Task<long>> query = distribution == "strided"
            ? (file, run) => StridedAsync(file, run, StridedKey(groups))
            : aggregates switch
            {
                "count" => (file, run) => CountAsync(file, run, Key(groups)),
                "count mean" => (file, run) => MeanAsync(file, run, Key(groups)),
                "four" => (file, run) => FourAsync(file, run, Key(groups)),
                "count range" => (file, run) => RangeAsync(file, run, Key(groups)),
                "count deviation" => (file, run) => DeviationAsync(file, run, Key(groups)),
                "count distinct" => (file, run) => CountDistinctAsync(file, run, Key(groups)),
                _ => (file, run) => TotalAsync(file, run, Key(groups)),
            };
        return ($"spread-{distribution}-{rows}", new Scenario(name, query, probe, matrix));
    }

    private static Func<Probe<Spread>, Sym<int>> Key(string groups) => groups switch
    {
        "1e3" => s => s.K3,
        "1e4" => s => s.K4,
        "1e5" => s => s.K5,
        "1e6" => s => s.K6,
        "1e7" => s => s.K7,
        "tenfold" => s => s.Tenfold,
        _ => s => s.Unique,
    };

    private static Func<Probe<Strided>, Sym<long>> StridedKey(string groups) => groups switch
    {
        "1e3" => s => s.K3,
        "1e4" => s => s.K4,
        "1e5" => s => s.K5,
        "1e6" => s => s.K6,
        _ => s => s.K7,
    };

    private static async Task<long> CountAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyCount> groups in run.Track(file.Scan<Spread>().GroupBy(key).Select(g => (g.Key, g.Count()))).As<KeyCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> TotalAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Spread>().GroupBy(key).Select(g => (g.Key, g.Count(), g.Sum(s => s.Value)))).As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> MeanAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyMean> groups in run.Track(file.Scan<Spread>().GroupBy(key).Select(g => (g.Key, g.Count(), g.Average(s => s.Real)))).As<KeyMean>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> RangeAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyRange> groups in run.Track(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Min(s => s.Value), g.Max(s => s.Value))))
            .As<KeyRange>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> DeviationAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyDeviation> groups in run.Track(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.StandardDeviation(s => s.Real))))
            .As<KeyDeviation>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> CountDistinctAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long values = 0;
        await foreach (Columns<KeyCount> groups in run.Track(file.Scan<Spread>().GroupBy(key).Select(g => (g.Key, g.CountDistinct(s => s.Value)))).As<KeyCount>())
        {
            run.Answer();
            values += Sum(groups.Column<long>(1).Values);
        }

        return values;
    }

    private static async Task<long> FourAsync(VortexFile file, Run run, Func<Probe<Spread>, Sym<int>> key)
    {
        long rows = 0;
        await foreach (Columns<KeyFour> groups in run.Track(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Value), g.Average(s => s.Real), g.Max(s => s.Value))))
            .As<KeyFour>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> StridedAsync(VortexFile file, Run run, Func<Probe<Strided>, Sym<long>> key)
    {
        long rows = 0;
        await foreach (Columns<LongKeyTotal> groups in run.Track(file.Scan<Strided>().GroupBy(key).Select(g => (g.Key, g.Count(), g.Sum(s => s.Value)))).As<LongKeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> SkewsAsync(VortexFile file, Run run, bool zipf)
    {
        long rows = 0;
        Func<Probe<Skews>, Sym<int>> key = zipf ? s => s.Zipf : s => s.Drift;
        await foreach (Columns<KeyTotal> groups in run.Track(file.Scan<Skews>().GroupBy(key).Select(g => (g.Key, g.Count(), g.Sum(s => s.Value)))).As<KeyTotal>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> UrlsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<NameCount> groups in run.Track(file.Scan<Page>().GroupBy(p => p.Url).Select(g => (g.Key, g.Count()))).As<NameCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> NamesAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<NameCount> groups in run.Track(file.Scan<Named>().GroupBy(n => n.Name).Select(g => (g.Key, g.Count()))).As<NameCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> IdsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<IdCount> groups in run.Track(file.Scan<Page>().GroupBy(p => p.Id).Select(g => (g.Key, g.Count()))).As<IdCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> UrlSmallsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<UrlSmallCount> groups in run.Track(file.Scan<Page>()
            .GroupBy(p => (p.Url, p.Small))
            .Select(g => (g.Key.Url, g.Key.Small, g.Count())))
            .As<UrlSmallCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    private static async Task<long> TextExtremesAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyTexts> groups in run.Track(file.Scan<Page>()
            .GroupBy(p => p.Key)
            .Select(g => (g.Key, g.Min(p => p.Url), g.Max(p => p.Url))))
            .As<KeyTexts>())
        {
            run.Answer();
            rows += groups.RowCount;
        }

        return rows;
    }

    private static async Task<long> DistinctByDayAsync(VortexFile file, Run run)
    {
        long users = 0;
        await foreach (Columns<DayUsers> days in run.Track(file.Scan<Visit>().GroupBy(v => v.Day).Select(g => (g.Key, g.CountDistinct(v => v.User)))).As<DayUsers>())
        {
            run.Answer();
            users += Sum(days.Column<long>(1).Values);
        }

        return users;
    }

    private static async Task<long> DistinctUsersAsync(VortexFile file, Run run)
    {
        long users = await file.Scan<Visit>().CountDistinctAsync(v => v.User).ConfigureAwait(false);
        run.Answer();
        return users;
    }

    private static async Task<long> TopCountsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyCount> groups in run.Track(file.Scan<Spread>()
            .GroupBy(s => s.K6)
            .OrderByDescending(g => g.Count())
            .Take(100)
            .Select(g => (g.Key, g.Count())))
            .As<KeyCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> TopUrlsAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<NameCount> groups in run.Track(file.Scan<Page>()
            .GroupBy(p => p.Url)
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

    private static async Task<long> TopMostAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyMost> groups in run.Track(file.Scan<Spread>()
            .GroupBy(s => s.K6)
            .OrderByDescending(g => g.Max(s => s.Value))
            .Take(100)
            .Select(g => (g.Key, g.Max(s => s.Value))))
            .As<KeyMost>())
        {
            run.Answer();
            rows += groups.RowCount;
        }

        return rows;
    }

    private static async Task<long> HavingAsync(VortexFile file, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyCount> groups in run.Track(file.Scan<Spread>()
            .GroupBy(s => s.Tenfold)
            .Where(g => g.Count() > 10L)
            .Select(g => (g.Key, g.Count())))
            .As<KeyCount>())
        {
            run.Answer();
            rows += Sum(groups.Column<long>(1).Values);
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
}
