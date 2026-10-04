using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A result read through a record: a selection of any length, the answers of a whole scan, the
/// refusals of a record that does not take them, and the result as a scan, with a scan's operators
/// over it, each against LINQ to objects over the same rows.
/// </summary>
public sealed partial class ResultScanTests
{
    private const int Rows = 30_000;

    private static readonly string[] Cities = ["Arles", "Brest", "Caen", "Dax", "Évry", "Foix"];

    [Fact]
    public async Task ASelectionPastEightElementsIsReadThroughItsRecord()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<Wide> groups = await ListAsync(file.Scan<Visit>()
                .GroupBy(r => (r.City, r.Day))
                .Select(g => (
                    g.Key.City, g.Key.Day, g.Count(), g.Sum(x => x.Pages), g.Min(x => x.Pages), g.Max(x => x.Pages),
                    g.Average(x => x.Pages), g.CountDistinct(x => x.Pages), g.Sum(x => x.Seconds), g.Min(x => x.Seconds),
                    g.Max(x => x.Seconds), g.Average(x => x.Seconds)))
                .As<Wide>());

            Dictionary<(string, int), Visit[]> expected = rows.GroupBy(r => (r.City, r.Day)).ToDictionary(g => g.Key, g => g.ToArray());
            Assert.Equal(expected.Count, groups.Count);
            foreach (Wide group in groups)
            {
                Visit[] of = expected[(group.City, group.Day)];
                Assert.Equal(of.Length, group.Count);
                Assert.Equal(of.Sum(r => r.Pages), group.Pages);
                Assert.Equal(of.Min(r => r.Pages), group.MinPages);
                Assert.Equal(of.Max(r => r.Pages), group.MaxPages);
                Assert.Equal(of.Average(r => r.Pages), group.MeanPages!.Value, 9);
                Assert.Equal(of.Select(r => r.Pages).Distinct().Count(), group.DistinctPages);
                Assert.Equal(of.Sum(r => r.Seconds ?? 0), group.Seconds, 6);
                Assert.Equal(of.Min(r => r.Seconds), group.MinSeconds);
                Assert.Equal(of.Max(r => r.Seconds), group.MaxSeconds);
                Assert.Equal(of.Average(r => r.Seconds)!.Value, group.MeanSeconds!.Value, 9);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheAnswersOfAScanGoIntoTheRecordAggAsyncNames()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Totals totals = await file.Scan<Visit>()
                .Where(r => r.Day >= 3)
                .AggAsync<Totals>(a => (a.Count(), a.Sum(x => x.Pages), a.Max(x => x.City), a.Average(x => x.Seconds)), Ct);

            Visit[] kept = [.. rows.Where(r => r.Day >= 3)];
            Assert.Equal(kept.Length, totals.Count);
            Assert.Equal(kept.Sum(r => r.Pages), totals.Pages);
            // Text compares bytewise, as a column stores it: "Évry" after "Foix".
            Assert.Equal(kept.Select(r => r.City).Max(StringComparer.Ordinal), totals.LastCity);
            Assert.Equal(kept.Average(r => r.Seconds)!.Value, totals.MeanSeconds!.Value, 9);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ARecordThatDoesNotTakeTheSelectionIsRefusedBeforeAnyRead()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // Through a value the selection was kept in, which VX1010 does not see into: the refusal
            // at run time is what is tested.
            Vorticity.Aggregation three = file.Scan<Visit>().GroupBy(r => r.City).Select(g => (g.Key, g.Count(), g.Max(x => x.Day)));
            VortexSchemaException fewer = Assert.Throws<VortexSchemaException>(() => three.As<CityCount>());
            Assert.Contains("2 members and the selection 3 elements", fewer.Message, StringComparison.Ordinal);

            Vorticity.Aggregation counted = file.Scan<Visit>().GroupBy(r => r.City).Select(g => (g.Key, g.Count()));
            VortexSchemaException narrower = Assert.Throws<VortexSchemaException>(() => counted.As<CityNarrowCount>());
            Assert.Contains("Element 2", narrower.Message, StringComparison.Ordinal);
            Assert.Contains("'Count'", narrower.Message, StringComparison.Ordinal);

            Vorticity.Aggregation averaged = file.Scan<Visit>().GroupBy(r => r.City).Select(g => (g.Key, g.Average(x => x.Pages)));
            VortexSchemaException notNullable = Assert.Throws<VortexSchemaException>(() => averaged.As<CityPlainMean>());
            Assert.Contains("may be null", notNullable.Message, StringComparison.Ordinal);

            Func<Aggregates<Visit>, System.Runtime.CompilerServices.ITuple> twoCounts = a => (a.Count(), a.Count());
            await Assert.ThrowsAsync<VortexSchemaException>(async () => await file.Scan<Visit>().AggAsync<CityCount>(twoCounts, Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultsScanTakesTheOperatorsOfAScan()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            CityDayCount[] expected = [.. rows.GroupBy(r => (r.City, r.Day)).Select(g => new CityDayCount(g.Key.City, g.Key.Day, g.Count()))];

            // A filter evaluated on the result's batches.
            Assert.Equal(
                Sorted(expected.Where(g => g.Count > 600 && g.City != "Caen")),
                Sorted(await ListAsync(Daily(file).Where(g => g.Count > 600L & g.City != "Caen"))));

            // A group by of the result: the days of each city, and their rows.
            List<CityCount> cities = await ListAsync(Daily(file).GroupBy(g => g.City).Select(g => (g.Key, g.Sum(x => x.Count))).As<CityCount>());
            Assert.Equal(
                rows.GroupBy(r => r.City).Select(g => new CityCount(g.Key, g.Count())).OrderBy(c => c.City),
                cities.OrderBy(c => c.City));

            // The single answers, read over the stream.
            Assert.Equal(expected.Length, await Daily(file).CountAsync(Ct));
            Assert.Equal(expected.Count(g => g.Day == 2), await Daily(file).Where(g => g.Day == 2).CountAsync(Ct));
            Assert.True(await Daily(file).AnyAsync(Ct));
            Assert.False(await Daily(file).Where(g => g.Count < 0L).AnyAsync(Ct));
            Assert.Equal(expected.Max(g => g.Count), await Daily(file).MaxAsync(g => g.Count, Ct));
            Assert.Equal(expected.Min(g => g.City), await Daily(file).MinAsync(g => g.City, Ct));
            Assert.Equal(expected.Sum(g => g.Count), await Daily(file).SumAsync(g => g.Count, Ct));
            Assert.Equal(
                new GroupsAndRows(expected.Length, expected.Sum(g => g.Count)),
                await Daily(file).AggAsync<GroupsAndRows>(a => (a.Count(), a.Sum(x => x.Count)), Ct));

            // Rows by their position in the order the result is delivered.
            List<CityDayCount> all = await ListAsync(Daily(file));
            Assert.Equal(all.Skip(3).Take(4), await ListAsync(Daily(file).Rows(new RowRange(3, 7))));
            Assert.Equal([all[1], all[5], all[^1]], await ListAsync(Daily(file).Rows(5, all.Count - 1, 1)));

            // Batches the caller owns.
            long owned = 0;
            await foreach (RecordBatch batch in Daily(file).ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    owned += batch.RowCount;
                }
            }

            Assert.Equal(expected.Length, owned);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultIsWrittenAsItComes()
    {
        (Visit[] rows, string path) = await WriteAsync();
        string rollup = path + ".daily.vortex";
        string copy = path + ".copy.vortex";
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<CityDayCount>(rollup))
            {
                await writer.WriteAsync(Daily(file), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(copy))
            {
                await writer.WriteAsync(file.Scan<Visit>().Where(r => r.Day < 2), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile written = await VortexFile.OpenAsync(rollup, Ct);
            Assert.Equal(
                Sorted(rows.GroupBy(r => (r.City, r.Day)).Select(g => new CityDayCount(g.Key.City, g.Key.Day, g.Count()))),
                Sorted(await ListAsync(written.Scan<CityDayCount>())));

            await using VortexFile copied = await VortexFile.OpenAsync(copy, Ct);
            Assert.Equal(rows.Where(r => r.Day < 2), await ListAsync(copied.Scan<Visit>()));
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(rollup);
            System.IO.File.Delete(copy);
        }
    }

    [Fact]
    public async Task AResultsKeysAreWalkedInKeyOrderFromMemory()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityDayCount> delivered = await ListAsync(Daily(file));

            // The counts in order, a count's groups by their place in the result: the place Rows reads.
            (long Key, long Row)[] expected = [.. delivered.Select((g, i) => (g.Count, (long)i)).OrderBy(e => e.Item1).ThenBy(e => e.Item2)];
            await using (KeyCursor<long> counts = await Daily(file).Keys(g => g.Count).OpenAsync(Ct))
            {
                List<(long, long)> walked = [];
                for (bool on = await counts.SeekFirstAsync(Ct); on; on = await counts.NextAsync(Ct))
                {
                    walked.Add((counts.Key, counts.Row));
                }

                Assert.Equal(expected, walked);

                long middle = expected[expected.Length / 2].Key;
                Assert.True(await counts.SeekAsync(middle, SeekOp.Exact, Ct));
                Assert.Equal(expected.First(e => e.Key == middle).Row, counts.Row);
                Assert.Equal(expected.Count(e => e.Key == middle), await counts.KeyCountAsync(Ct));
                Assert.Equal(expected.Count(e => e.Key < middle), await counts.RankAsync(middle, Ct));
                Assert.True(await counts.SeekAsync(middle, SeekOp.Before, Ct));
                Assert.Equal(expected.Last(e => e.Key < middle), (counts.Key, counts.Row));
                Assert.True(await counts.SeekAsync(middle, SeekOp.After, Ct));
                Assert.Equal(expected.First(e => e.Key > middle), (counts.Key, counts.Row));
                Assert.False(await counts.SeekAsync(expected[^1].Key, SeekOp.After, Ct));
                Assert.False(await counts.SeekAsync(-1, SeekOp.Exact, Ct));

                // Backwards a key at a time, each on its last entry.
                List<(long, long)> lasts = [];
                for (bool on = await counts.SeekLastAsync(Ct); on; on = await counts.PrevKeyAsync(Ct))
                {
                    lasts.Add((counts.Key, counts.Row));
                }

                Assert.Equal(expected.GroupBy(e => e.Key).Select(g => g.Last()).Reverse(), lasts);
            }

            // Text, each key once: its entry's row is a row of the result that holds it.
            await using (KeyCursor<string> cities = await Daily(file).Keys(g => g.City).Distinct().OpenAsync(Ct))
            {
                List<string> walked = [];
                for (bool on = await cities.SeekFirstAsync(Ct); on; on = await cities.NextAsync(Ct))
                {
                    walked.Add(cities.Key);
                    Assert.Equal(cities.Key, delivered[(int)cities.Row].City);
                    Assert.Equal(delivered.FindIndex(g => g.City == cities.Key), cities.Row);
                }

                Assert.Equal(delivered.Select(g => g.City).Distinct().Order(StringComparer.Ordinal), walked);
                Assert.True(await cities.SeekAsync("Caen", SeekOp.Exact, Ct));
                Assert.Equal(delivered.Count(g => g.City == "Caen"), await cities.KeyCountAsync(Ct));
            }

            // Floats, from a result that arrives in no order of theirs.
            Scan<CityMean> means = file.Scan<Visit>().GroupBy(r => r.City).Select(g => (g.Key, g.Average(x => x.Pages))).As<CityMean>();
            await using (KeyCursor<double?> mean = await means.Keys(g => g.Mean).OpenAsync(Ct))
            {
                List<double> walked = [];
                for (bool on = await mean.SeekFirstAsync(Ct); on; on = await mean.NextAsync(Ct))
                {
                    walked.Add(mean.Key!.Value);
                }

                double[] expectedMeans = [.. rows.GroupBy(r => r.City).Select(g => g.Average(r => r.Pages)).Order()];
                Assert.Equal(expectedMeans.Length, walked.Count);
                for (int i = 0; i < walked.Count; i++)
                {
                    Assert.Equal(expectedMeans[i], walked[i], 9);
                }
            }

            KeyPlan plan = await Daily(file).Keys(g => g.Day).ExplainAsync(Ct);
            Assert.Equal((KeySourceKind.InMemory, 1, true), (plan.Source, plan.Runs, plan.HasRows));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultsScanIsSingleUse()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Scan<CityDayCount> daily = Daily(file);
            Assert.NotEmpty(await ListAsync(daily));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await daily.CountAsync(Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Scan<CityDayCount> Daily(VortexFile file) =>
        file.Scan<Visit>().GroupBy(r => (r.City, r.Day)).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>();

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

    private static List<CityDayCount> Sorted(IEnumerable<CityDayCount> groups) => [.. groups.OrderBy(g => g.City).ThenBy(g => g.Day)];

    private static async Task<(Visit[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "result-scans");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"visits-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Visit[] rows = new Visit[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Visit(Cities[row / 11 % Cities.Length], row / 4_000, row % 17, row % 13 == 0 ? null : row % 400 / 8.0);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            await writer.WriteAsync<Visit>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Visit(string City, int Day, int Pages, double? Seconds);

    [VortexRecord]
    public partial record struct CityDayCount(string City, int Day, long Count);

    [VortexRecord]
    public partial record struct CityNarrowCount(string City, int Count);

    [VortexRecord]
    public partial record struct GroupsAndRows(long Groups, long Rows);

    [VortexRecord]
    public partial record struct CityPlainMean(string City, double Mean);

    [VortexRecord]
    public partial record struct CityMean(string City, double? Mean);

    [VortexRecord]
    public partial record struct Totals(long Count, long Pages, string? LastCity, double? MeanSeconds);

    /// <summary>Twelve results of a group, past the eight the selections by arity stopped at.</summary>
    [VortexRecord]
    public partial record struct Wide(
        string City, int Day, long Count, long Pages, int MinPages, int MaxPages, double? MeanPages, long DistinctPages,
        double Seconds, double? MinSeconds, double? MaxSeconds, double? MeanSeconds);
}
