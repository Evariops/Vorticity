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
/// The first groups of an order on the key, on a key that does not stream: each lane keeps the best
/// keys it has met alone, trimmed back once they pass one and a half times as many, and the answer is
/// the one the pass that holds every group gives, whatever the order, the degree, the key's type, a
/// NaN or a null among its values; a filter before the order, an order on an aggregate or no window
/// hold every group, as before.
/// </summary>
public sealed partial class KeyTopTests
{
    private const int Rows = 120_000;

    private const int Keys = 60_000;

    private const int BatchRows = 4_096;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheFirstGroupsOfAnOrderOnTheKeyAreKeptAloneOnEachLane(int degree)
    {
        (Row[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // Ascending, the first ten, with a row chosen by its place: the groups kept have every row.
            Vorticity.Aggregation first = Scan(file).GroupBy(r => r.Key).OrderBy(g => g.Key).Take(10)
                .Select(g => (g.Key, g.Count(), g.Sum(x => x.Value), g.First().Value));
            AggregationQuery query = (AggregationQuery)first.Query;
            Assert.NotNull(KeyTop.Of(query));
            Assert.Equal(
                rows.GroupBy(r => r.Key).OrderBy(g => g.Key).Take(10).Select(g => new KeyStats(g.Key, g.Count(), g.Sum(r => r.Value), g.First().Value)),
                await ListAsync(first.As<KeyStats>()));
            Assert.True(query.PeakGroups <= degree * (15 + BatchRows), $"{query.PeakGroups} groups held for ten of {Keys}");

            // Descending, past five, the windows folded into one reach.
            Vorticity.Aggregation last = Scan(file).GroupBy(r => r.Key).OrderByDescending(g => g.Key).Skip(5).Take(10)
                .Select(g => (g.Key, g.Count(), g.Sum(x => x.Value), g.First().Value));
            Assert.NotNull(KeyTop.Of((AggregationQuery)last.Query));
            Assert.Equal(
                rows.GroupBy(r => r.Key).OrderByDescending(g => g.Key).Skip(5).Take(10).Select(g => new KeyStats(g.Key, g.Count(), g.Sum(r => r.Value), g.First().Value)),
                await ListAsync(last.As<KeyStats>()));

            // A float key, a NaN first down and the nulls last both ways; text; two columns: the
            // same groups as the pass that holds them all, which a filter first forces.
            foreach (bool descending in (bool[])[false, true])
            {
                Assert.Equal(await LevelsAsync(file, descending, everyGroup: true), await LevelsAsync(file, descending, everyGroup: false));
            }

            List<string> names = await Scan(file).GroupBy(r => r.Name).OrderBy(g => g.Key).Take(12).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(rows.Select(r => r.Name).Distinct().Order(StringComparer.Ordinal).Take(12), names);
            Assert.Equal(
                await ListAsync(Scan(file).GroupBy(r => (r.Bucket, r.Name)).Where(g => g.Count() > 0).OrderBy(g => g.Key).Take(9).Select(g => (g.Key.Bucket, g.Key.Name, g.Count())).As<PairCount>()),
                await ListAsync(Scan(file).GroupBy(r => (r.Bucket, r.Name)).OrderBy(g => g.Key).Take(9).Select(g => (g.Key.Bucket, g.Key.Name, g.Count())).As<PairCount>()));

            // A filter first, an order on an aggregate, no window: every group, as before.
            Assert.Null(KeyTop.Of((AggregationQuery)Scan(file).GroupBy(r => r.Key).Where(g => g.Count() > 1).OrderBy(g => g.Key).Take(10).Select(g => g.Count()).Query));
            Assert.Null(KeyTop.Of((AggregationQuery)Scan(file).GroupBy(r => r.Key).OrderBy(g => g.Count()).Take(10).Select(g => g.Count()).Query));
            Assert.Null(KeyTop.Of((AggregationQuery)Scan(file).GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => g.Count()).Query));
            Assert.Null(KeyTop.Of((AggregationQuery)Scan(file).GroupBy(r => r.Key).OrderBy(g => g.Key).Where(g => g.Count() > 1).Take(10).Select(g => g.Count()).Query));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheFirstGroupsWithoutAnOrderAreExactAndOneLaneHoldsThemAlone(int degree)
    {
        (Row[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // Ten groups, which ones not promised, each with every one of its rows.
            Vorticity.Aggregation some = Scan(file).GroupBy(r => r.Key).Take(10).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value), g.First().Value));
            AggregationQuery query = (AggregationQuery)some.Query;
            Assert.True(KeyTop.FirstOf(query) is { FirstMet: true });
            List<KeyStats> read = await ListAsync(some.As<KeyStats>());
            Assert.Equal(10, read.Count);
            Dictionary<int, KeyStats> all = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => new KeyStats(g.Key, g.Count(), g.Sum(r => r.Value), g.First().Value));
            Assert.All(read, group => Assert.Equal(all[group.Key], group));
            Assert.Equal(10, read.Select(group => group.Key).Distinct().Count());

            // One lane holds the first met alone; several hold every group, which they merge.
            Assert.True(degree > 1 || query.PeakGroups <= 15 + BatchRows, $"{query.PeakGroups} groups held for ten of {Keys}");

            // A filter or an order first: not this top.
            Assert.Null(KeyTop.FirstOf((AggregationQuery)Scan(file).GroupBy(r => r.Key).Where(g => g.Count() > 1).Take(10).Select(g => g.Count()).Query));
            Assert.Null(KeyTop.FirstOf((AggregationQuery)Scan(file).GroupBy(r => r.Key).OrderBy(g => g.Count()).Take(10).Select(g => g.Count()).Query));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Scan<Row> Scan(VortexFile file) => file.Scan<Row>().With(new ScanOptions { BatchRows = BatchRows });

    /// <summary>The first five levels and their counts, as text; with <paramref name="everyGroup"/>, behind a filter that keeps every group and every group held.</summary>
    private static async Task<string> LevelsAsync(VortexFile file, bool descending, bool everyGroup)
    {
        GroupedScan<Row, Sym<double?>> levels = Scan(file).GroupBy(r => r.Level);
        GroupedScan<Row, Sym<double?>> kept = everyGroup ? levels.Where(g => g.Count() > 0) : levels;
        GroupedScan<Row, Sym<double?>> ordered = (descending ? kept.OrderByDescending(g => g.Key) : kept.OrderBy(g => g.Key)).Take(5);
        Vorticity.Aggregation top = ordered.Select(g => (g.Key, g.Count()));
        Assert.Equal(everyGroup, KeyTop.Of((AggregationQuery)top.Query) is null);
        return string.Join(";", (await ListAsync(top.As<LevelCount>())).Select(l => $"{(l.Level is double v ? BitConverter.DoubleToInt64Bits(v).ToString("X", System.Globalization.CultureInfo.InvariantCulture) : "null")}={l.Count}"));
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

    /// <summary>
    /// Keys in a scattered order, each about twice; levels with NaNs and nulls among them, and the
    /// greatest few rare, so that a top meets them late; names of a few thousand values.
    /// </summary>
    private static async Task<(Row[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "key-top");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int key = (int)((long)row * 7_919 % Keys);
            double? level = row % 101 == 0 ? null : row % 103 == 0 ? double.NaN : row % 5_000 == 4_999 ? 1e9 + row : (row % 977) / 7.0;
            rows[row] = new Row(key, level, $"n{(long)row * 31 % 3_001:D4}", row % 1_000, key % 97);
        }

        // In chunks of two thousand rows, so that several lanes take ranges of them in turn.
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 16 << 10 }))
        {
            for (int first = 0; first < rows.Length; first += 2_048)
            {
                await writer.WriteAsync<Row>(rows.AsSpan(first, Math.Min(2_048, rows.Length - first)), Ct);
            }

            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Row(int Key, double? Level, string Name, long Value, int Bucket);

    [VortexRecord]
    public partial record struct KeyStats(int Key, long Count, long Sum, long First);

    [VortexRecord]
    public partial record struct LevelCount(double? Level, long Count);

    [VortexRecord]
    public partial record struct PairCount(int Group, string Name, long Count);
}
