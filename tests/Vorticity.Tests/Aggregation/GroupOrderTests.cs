using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The order of groups: by an aggregate or a key, ascending or descending, nulls last in both
/// directions, NaN after +∞, text bytewise, ties broken by ThenBy and then by the key; a top-k that
/// is the full sort's first k; the same order at every degree; and the order of a result's scan.
/// </summary>
public sealed partial class GroupOrderTests
{
    private const int Rows = 120_000;

    private static readonly string[] Cities = ["Arles", "Brest", "Caen", "Dax", "Évry", "Foix", "Gap", "Hyères", "Ivry"];

    private static readonly double?[] Readings = [double.NaN, -0.0, 0.0, double.PositiveInfinity, double.NegativeInfinity, null, 1.5, -2.5];

    /// <summary>Floats as a column orders them: NaN after +∞, the null last.</summary>
    private static readonly Comparer<double?> Floats = Comparer<double?>.Create((a, b) =>
        a is null || b is null ? (a is null).CompareTo(b is null)
        : double.IsNaN(a.Value) || double.IsNaN(b.Value) ? double.IsNaN(a.Value).CompareTo(double.IsNaN(b.Value))
        : a.Value.CompareTo(b.Value));

    [Fact]
    public async Task GroupsComeInTheOrderOfAnAggregateThenOfTheirKey()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityDayCount> expected = [.. rows.GroupBy(r => (r.City, r.Day))
                .Select(g => new CityDayCount(g.Key.City, g.Key.Day, g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.City, StringComparer.Ordinal).ThenBy(g => g.Day)];
            List<CityDayCount> ordered = await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                .OrderByDescending(g => g.Count()).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>());
            Assert.Equal(expected, ordered);

            // The query syntax, with a ThenBy, and a top-k that is the full sort's first ten.
            Vorticity.Aggregation query =
                from r in file.Scan<Visit>()
                group r by (r.City, r.Day) into g
                orderby g.Key.Day descending, g.Count(), g.Key.City
                select (g.Key.City, g.Key.Day, g.Count());
            List<CityDayCount> written = await ListAsync(query.As<CityDayCount>());
            List<CityDayCount> sorted = [.. expected.OrderByDescending(g => g.Day).ThenBy(g => g.Count).ThenBy(g => g.City, StringComparer.Ordinal)];
            Assert.Equal(sorted, written);
            Assert.Equal(
                sorted.Take(10),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                    .OrderByDescending(g => g.Key.Day).ThenBy(g => g.Count()).ThenBy(g => g.Key.City).Take(10)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>()));
            Assert.Equal(
                sorted.Skip(5).Take(3),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                    .OrderByDescending(g => g.Key.Day).ThenBy(g => g.Count()).ThenBy(g => g.Key.City)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).Skip(5).Take(3).As<CityDayCount>()));

            // A composite key orders by its components in turn; text bytewise.
            Assert.Equal(
                expected.OrderBy(g => g.City, StringComparer.Ordinal).ThenBy(g => g.Day),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day)).OrderBy(g => g.Key).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task NullsComeLastAndNaNAfterInfinityInBothDirections()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<double?> ascending = await file.Scan<Visit>().GroupBy(r => r.Reading).OrderBy(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal([double.NegativeInfinity, -2.5, 0.0, 1.5, double.PositiveInfinity, double.NaN, null], ascending.Select(Zero));
            List<double?> descending = await file.Scan<Visit>().GroupBy(r => r.Reading).OrderByDescending(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal([double.NaN, double.PositiveInfinity, 1.5, 0.0, -2.5, double.NegativeInfinity, null], descending.Select(Zero));

            // A null mean is last, ascending and descending.
            List<string> byMean = await file.Scan<Visit>().GroupBy(r => r.City).OrderByDescending(g => g.Average(x => x.Score)).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal("Ivry", byMean[^1]);
            List<string> byMeanUp = await file.Scan<Visit>().GroupBy(r => r.City).OrderBy(g => g.Average(x => x.Score)).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal("Ivry", byMeanUp[^1]);
            Assert.Equal(
                rows.Where(r => r.City != "Ivry").GroupBy(r => r.City).OrderBy(g => g.Average(r => r.Score)).Select(g => g.Key),
                byMeanUp.SkipLast(1));

            // An aggregate's floats, thousands of groups to each: the zeros as one, the nulls last in
            // both directions (a maximum skips NaN: of NaNs alone, it is null), ties by key; top-ks
            // that end inside a run of them.
            Comparer<double?> downward = Comparer<double?>.Create((a, b) => a is null || b is null ? Floats.Compare(a, b) : Floats.Compare(b, a));
            List<long> up = [.. rows.GroupBy(r => r.Id).OrderBy(g => Max(g), Floats).ThenBy(g => g.Key).Select(g => g.Key)];
            List<long> down = [.. rows.GroupBy(r => r.Id).OrderBy(g => Max(g), downward).ThenBy(g => g.Key).Select(g => g.Key)];
            foreach (int take in (int[])[100, 40_000, 80_000, 90_000])
            {
                Assert.Equal(up.Take(take), await file.Scan<Visit>().GroupBy(r => r.Id).OrderBy(g => g.Max(r => r.Reading)).Take(take).Select(g => g.Key).ToListAsync(Ct));
                Assert.Equal(down.Take(take), await file.Scan<Visit>().GroupBy(r => r.Id).OrderByDescending(g => g.Max(r => r.Reading)).Take(take).Select(g => g.Key).ToListAsync(Ct));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheOrderIsTheSameAtEveryDegreeAndPastOnePartOfTheSort()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            List<long>[] orders = new List<long>[2];
            int[] degrees = [1, 4];
            for (int d = 0; d < degrees.Length; d++)
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degrees[d]);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                orders[d] = await file.Scan<Visit>().GroupBy(r => r.Id).OrderBy(g => g.Count()).Select(g => g.Key).ToListAsync(Ct);
            }

            // More groups than one part of the sort holds: runs sorted apart, then merged.
            Assert.Equal(rows.Select(r => r.Id).Distinct().Count(), orders[0].Count);
            Assert.True(orders[0].Count > 65_536);
            Assert.Equal(rows.GroupBy(r => r.Id).OrderBy(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key), orders[0]);
            Assert.Equal(orders[0], orders[1]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATopKKeepsWhatTheFullSortPutsFirstTiesIncluded()
    {
        Draw[] rows = Draws();
        string path = await WriteAsync(rows);
        try
        {
            // Four counts shared by thousands of groups each: the k-th group ties with thousands of
            // others, which the key alone ranks, read from the index (text, integers) or built (a
            // float's, whose NaN and null the column orders).
            List<string> byName = [.. rows.GroupBy(r => r.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key)];
            List<long> byId = [.. rows.GroupBy(r => r.Id).OrderBy(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key)];
            List<double?> byScore = [.. rows.GroupBy(r => r.Score).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, Floats).Select(g => g.Key)];
            List<string> byMean = [.. rows.GroupBy(r => r.Name).OrderByDescending(g => g.Average(r => r.Value)).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key)];
            List<string> byCountThenMean = [.. rows.GroupBy(r => r.Name)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Average(r => r.Value), Floats).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key)];
            int boundary = rows.GroupBy(r => r.Name).Count(g => g.Count() == 4);
            foreach (int degree in (int[])[1, 4])
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                Assert.Equal(byName.Take(100), await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Take(100).Select(g => g.Key).ToListAsync(Ct));

                // Across two counts: those of the first sorted whole, the ties of the second ranked by key.
                Assert.Equal(
                    byName.Skip(boundary - 10).Take(20),
                    await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Select(g => g.Key).Skip(boundary - 10).Take(20).ToListAsync(Ct));
                Assert.Equal(byId.Take(100), await file.Scan<Draw>().GroupBy(r => r.Id).OrderBy(g => g.Count()).Take(100).Select(g => g.Key).ToListAsync(Ct));
                Assert.Equal(byScore.Take(50), await file.Scan<Draw>().GroupBy(r => r.Score).OrderByDescending(g => g.Count()).Take(50).Select(g => g.Key).ToListAsync(Ct));

                // A mean many groups share, null for some: the nulls last.
                Assert.Equal(byMean.Take(100), await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Average(r => r.Value)).Take(100).Select(g => g.Key).ToListAsync(Ct));
                Assert.Equal(byMean.TakeLast(30), await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Average(r => r.Value)).Select(g => g.Key).Skip(byMean.Count - 30).ToListAsync(Ct));
                Assert.Equal(
                    byCountThenMean.Take(100),
                    await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Average(r => r.Value)).Take(100).Select(g => g.Key).ToListAsync(Ct));

                // More than the groups: the full sort.
                Assert.Equal(byId, await file.Scan<Draw>().GroupBy(r => r.Id).OrderBy(g => g.Count()).Take(rows.Length).Select(g => g.Key).ToListAsync(Ct));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task ATopKOfManyGroupsRanksItsChunksAtOnceTiesIncluded(int degree)
    {
        // 300 000 names seen one to four times: past two chunks of 65 536 groups, each chunk's first k
        // ranked on a task of its own, then theirs. The k-th group ties
        // with tens of thousands, which the key ranks: the same groups, in the same order, as one
        // ranking of them all.
        Draw[] rows = Draws(300_000);
        string path = await WriteAsync(rows);
        try
        {
            List<string> byName = [.. rows.GroupBy(r => r.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key)];
            List<long> byId = [.. rows.GroupBy(r => r.Id).OrderBy(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key)];
            List<string> byCountThenMean = [.. rows.GroupBy(r => r.Name)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Average(r => r.Value), Floats).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key)];
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<string> names = file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Take(100).Select(g => g.Key);
            Assert.Equal(byName.Take(100), await names.ToListAsync(Ct));
            Assert.Equal(degree > 1, names.Plan.LastTopChunks > 1);
            Assert.Equal(byName.Skip(30).Take(70), await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Skip(30).Take(70).Select(g => g.Key).ToListAsync(Ct));
            Assert.Equal(byId.Take(1_000), await file.Scan<Draw>().GroupBy(r => r.Id).OrderBy(g => g.Count()).Take(1_000).Select(g => g.Key).ToListAsync(Ct));
            Assert.Equal(
                byCountThenMean.Take(100),
                await file.Scan<Draw>().GroupBy(r => r.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Average(r => r.Value)).Take(100).Select(g => g.Key).ToListAsync(Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AnAggregatesNumbersRankAsTheirColumnInBothDirections()
    {
        // Read into the pool for an order, integers and floats rank as keys of 64 bits: the
        // direction folded in, the extremes kept apart, a float's zeros as one and NaN after +∞,
        // a null last whichever the direction.
        Comparer<long?> integers = Comparer<long?>.Create((a, b) => a is null || b is null ? (a is null).CompareTo(b is null) : a.Value.CompareTo(b.Value));
        RanksAs<long>([5, -3, long.MaxValue, long.MinValue, 0, 5, -1, long.MaxValue - 1], Comparer<long>.Default);
        RanksAs<long?>([5, null, long.MaxValue, long.MinValue, 0, null, 5], integers);
        RanksAs<long?>([5, -3, long.MaxValue, long.MinValue, 0, 5], integers);
        double?[] floats =
            [double.NaN, -0.0, 0.0, double.PositiveInfinity, double.NegativeInfinity, null, 1.5, -2.5, double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon, null, double.NaN, 1.5];
        RanksAs(floats, Floats);
        RanksAs([.. floats.OfType<double>()], Comparer<double>.Create((a, b) => Floats.Compare(a, b)));
    }

    [Fact]
    public async Task AResultsScanIsSortedByItsColumn()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityDayCount> sorted = await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day)).Select(g => (g.Key.City, g.Key.Day, g.Count()))
                .As<CityDayCount>().OrderByDescending(g => g.Count));
            Assert.Equal(
                rows.GroupBy(r => (r.City, r.Day)).Select(g => (long)g.Count()).OrderByDescending(c => c),
                sorted.Select(g => g.Count));

            List<CityReading> readings = await ListAsync(file.Scan<Visit>().Where(r => r.Id < 40).Select(r => (r.City, r.Reading)).As<CityReading>().OrderBy(r => r.Reading));
            Assert.Equal(
                [.. rows.Where(r => r.Id < 40).Select(r => r.Reading).OrderBy(r => r is null ? 2 : double.IsNaN(r.Value) ? 1 : 0).ThenBy(r => r ?? 0).Select(Zero)],
                readings.Select(r => Zero(r.Reading)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AStateHasNoOrder()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Throws<ArgumentException>(() => file.Scan<Visit>().GroupBy(r => r.City).OrderBy(g => g.Aggregate<long, UnitsTotal, long>(x => x.Id)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A zero whatever its sign, which an order holds equal.</summary>
    private static double? Zero(double? value) => value == 0 ? 0.0 : value;

    /// <summary>
    /// Positions ranked by the order of <paramref name="values"/> read into the pool, as
    /// <paramref name="ascending"/> ranks them, and reversed with the nulls still last; and a top-k
    /// of each size split, as a full sort would, between what comes before its last and what ties
    /// with it.
    /// </summary>
    private static void RanksAs<T>(T[] values, Comparer<T> ascending)
    {
        foreach (bool descending in (bool[])[false, true])
        {
            Comparer<T> expected = descending ? Comparer<T>.Create((a, b) => a is null || b is null ? ascending.Compare(a, b) : ascending.Compare(b, a)) : ascending;
            int[] ranked = [.. Enumerable.Range(0, values.Length).OrderBy(i => values[i], expected)];
            long[] keys = ValuesOrder.Rent<T>(memory: null, values.Length);
            values.CopyTo(ValuesOrder.Values<T>(keys, values.Length));
            ColumnOrder order = ValuesOrder.Over<T>(keys, values.Length, descending, memory: null);
            try
            {
                int[] sorted = [.. Enumerable.Range(0, values.Length)];
                Array.Sort(sorted, (a, b) => order.Compare(a, b) is int c && c != 0 ? c : a.CompareTo(b));
                Assert.Equal(ranked, sorted);
                for (int keep = 1; keep < values.Length; keep++)
                {
                    int[] positions = [.. Enumerable.Range(0, values.Length)];
                    (int before, int tied) = order.TopTied(positions, values.Length, keep, memory: null, Ct);
                    T last = values[ranked[keep - 1]];
                    Assert.Equal(values.Count(v => expected.Compare(v, last) < 0), before);
                    Assert.Equal(values.Count(v => expected.Compare(v, last) == 0), tied);
                    Assert.All(positions[..before], p => Assert.True(expected.Compare(values[p], last) < 0));
                    Assert.All(positions[before..(before + tied)], p => Assert.Equal(0, expected.Compare(values[p], last)));
                }
            }
            finally
            {
                order.Release();
            }
        }
    }

    /// <summary>The maximum of a group's readings as the aggregate takes it: NaN skipped, null when nothing is left.</summary>
    private static double? Max(IEnumerable<Visit> group) =>
        group.Where(r => r.Reading is double value && !double.IsNaN(value)).Max(r => r.Reading);

    /// <summary>
    /// <paramref name="names"/> names seen one to four times each, scattered: a count each shares with
    /// thousands. A score per name, NaN or null for some, which 8 names share; a value whose mean many
    /// share, null for a seventh of the names.
    /// </summary>
    private static Draw[] Draws(int names = 40_000)
    {
        List<Draw> rows = [];
        for (int n = 0; n < names; n++)
        {
            int repeats = 1 + (int)((uint)(n * 2_654_435_761u) >> 30);
            int bucket = n % 5_000;
            double? score = bucket == 7 ? double.NaN : bucket == 11 ? null : bucket * 0.5;
            for (int r = 0; r < repeats; r++)
            {
                rows.Add(new Draw($"name-{(n * 7_919) % names:x5}-{n}", n, score, n % 7 == 0 ? null : (n % 13) + (r % 2)));
            }
        }

        Draw[] draws = [.. rows];
        new Random(42).Shuffle(draws);
        return draws;
    }

    private static async Task<string> WriteAsync(Draw[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-orders");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"draws-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Draw>(path))
        {
            await writer.WriteAsync<Draw>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<(Visit[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-orders");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"visits-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Visit[] rows = new Visit[Rows];
        for (int row = 0; row < Rows; row++)
        {
            string city = Cities[row / 5 % Cities.Length];

            // Ivry scores nothing: its mean is null.
            rows[row] = new Visit(city, row % 37, Readings[row % Readings.Length], city == "Ivry" ? null : row % 97, row % 90_000);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            await writer.WriteAsync<Visit>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Visit(string City, int Day, double? Reading, double? Score, long Id);

    [VortexRecord]
    public partial record struct CityDayCount(string City, int Day, long Count);

    [VortexRecord]
    public partial record struct CityReading(string City, double? Reading);

    [VortexRecord]
    public partial record struct Draw(string Name, long Id, double? Score, int? Value);
}
