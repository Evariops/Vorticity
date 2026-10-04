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
/// Groups many enough for their lanes to be merged in parts, each part of the key space by a task,
/// read as one: every kind of state merged from the part of each lane it holds, the null group, a
/// composite whose columns' indexes are merged once, and the answers .NET gives.
/// </summary>
public sealed partial class ParallelMergeTests
{
    private const int Rows = 240_000;

    [Fact]
    public async Task EveryStateMergesByParts()
    {
        Row[] rows = Rows_();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation byKey = file.Scan<Row>()
                .GroupBy(r => r.Key)
                .Select(g => (
                    g.Key, g.Count(), g.Sum(x => x.Value), g.Average(x => x.Value), g.Variance(x => x.Value), g.CountDistinct(x => x.Text),
                    g.Any(x => x.Value > 90), g.Min(x => x.Text), g.Max(x => x.Value), g.First().Value));
            List<KeyStats> stats = await ListAsync(byKey.As<KeyStats>());
            Assert.True(byKey.Plan.LastRun!.MergeParts > 1, $"{byKey.Plan.LastRun.MergeParts} parts");

            // The null key as a number no key holds.
            Dictionary<int, KeyStats> read = stats.ToDictionary(s => s.Key ?? int.MinValue);
            IGrouping<int?, Row>[] expected = [.. rows.GroupBy(r => r.Key)];
            Assert.Equal(expected.Length, read.Count);
            Assert.Contains(int.MinValue, read.Keys);
            foreach (IGrouping<int?, Row> group in expected)
            {
                KeyStats got = read[group.Key ?? int.MinValue];
                Row[] of = [.. group];
                Assert.Equal(of.Length, got.Count);
                Assert.Equal(of.Sum(r => r.Value), got.Sum);
                Assert.Equal(of.Average(r => r.Value), got.Mean);
                if (of.Length > 1)
                {
                    double mean = of.Average(r => r.Value);
                    Assert.Equal(of.Sum(r => (r.Value - mean) * (r.Value - mean)) / (of.Length - 1), got.Variance!.Value, 6);
                }

                Assert.Equal(of.Select(r => r.Text).Distinct().Count(), got.Texts);
                Assert.Equal(of.Any(r => r.Value > 90), got.High);
                Assert.Equal(of.Select(r => r.Text).Min(StringComparer.Ordinal), got.FirstText);
                Assert.Equal(of.Max(r => r.Value), got.Max);
                Assert.Equal(of[0].Value, got.FirstValue);
            }

            // A composite: each lane's indexes of its columns merged into one first, then its tuples by parts.
            Vorticity.Aggregation byPair = file.Scan<Row>().GroupBy(r => (r.Name, r.Small)).Select(g => (g.Key.Name, g.Key.Small, g.Count(), g.Sum(x => x.Value)));
            List<PairStats> pairs = await ListAsync(byPair.As<PairStats>());
            Assert.True(byPair.Plan.LastRun!.MergeParts > 1, $"{byPair.Plan.LastRun.MergeParts} parts");
            Assert.Equal(
                rows.GroupBy(r => (r.Name, r.Small)).Select(g => new PairStats(g.Key.Name, g.Key.Small, g.Count(), g.Sum(r => r.Value))).OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Small),
                pairs.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Small));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key of sixty thousand values in no order, null one row in ninety-seven; twenty thousand names; small values and texts.</summary>
    private static Row[] Rows_()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int? key = row % 97 == 0 ? null : (int)((mix >> 24) % 60_000);
            rows[row] = new Row(key, $"name-{(mix >> 40) % 20_000:D5}", row % 7, (long)((mix >> 8) % 100), $"t-{(mix >> 16) % 1_000:D3}");
        }

        return rows;
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

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "parallel-merge");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int? Key, string Name, long Small, long Value, string Text);

    [VortexRecord]
    public partial record struct KeyStats(
        int? Key, long Count, long Sum, double? Mean, double? Variance, long Texts, bool High, string? FirstText, long Max, long FirstValue);

    [VortexRecord]
    public partial record struct PairStats(string Name, long Small, long Count, long Sum);
}
