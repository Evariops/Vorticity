// Quantiles from each granule's histogram (spec D, 2.2; D1): a measure's values in a granule kept in a log-linear sketch of
// eight buckets an octave, within 1/17 of any value of a bucket; a quantile whose error allows that grid, by a key constant
// in each granule, is answered granule by granule, the granules where the key changes read as rows.
//
// THE ORACLE IS LINQ: each estimate lies within its error of the quantile LINQ computes over the group's rows, and most
// granules are answered without decoding a block.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleHistogramsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Five services of 40 000 latencies each, spread over three orders of magnitude, sorted by the service and compacted
    /// into objects: the 90th percentile by service at a 6 % error, which the granules' histograms answer for an object its
    /// counts do not, lies within 6 % of LINQ's for each, a third of the object's blocks decoded at most; at 1 %, within 1 %.
    /// </summary>
    [Fact]
    public async Task AQuantileByAKeyConstantInEachGranuleIsAnsweredByItsHistograms()
    {
        Random random = new Random(19);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Latency.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Latency.Service))], ObjectBytes = 64 << 20, Organization = LayoutOrganization.Leveled },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        List<Latency> all = [];
        for (int a = 0; a < 2; a++)
        {
            Latency[] rows = [.. Enumerable.Range(0, 100_000).Select(_ => new Latency($"svc-{random.Next(5)}", Math.Round(Math.Exp(random.NextDouble() * 7), 3)))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Latency>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        ScanPlan explained = await dataset.Scan<Latency>().GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.06))).ExplainAsync(analyze: true, Ct);
        Assert.Contains(explained.Units, static unit => unit.Alternative == "granules");
        Dictionary<string, double> exact = all.GroupBy(static r => r.Service).ToDictionary(static g => g.Key, static g => Percentile(g.Select(static r => r.Value), 0.9));
        Vorticity.Aggregation estimated = dataset.Scan<Latency>().GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.06)));
        List<ServiceQuantile> answers = await estimated.As<ServiceQuantile>().ToRecordsAsync(Ct).ToListAsync(Ct);
        long granules = (all.Count + 8191) / 8192;
        Console.WriteLine($"HISTOGRAMS: {estimated.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules; {string.Join(", ", answers.OrderBy(static a => a.Service).Select(a => $"{a.Service} {a.Value90:F1} for {exact[a.Service]:F1}"))}");
        Assert.Equal(5, answers.Count);
        Assert.All(answers, a => Assert.InRange(a.Value90!.Value, exact[a.Service] * 0.94, exact[a.Service] * 1.06));
        Assert.True(estimated.Metrics.BlocksDecoded <= granules / 3, $"{estimated.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules");

        Vorticity.Aggregation fine = dataset.Scan<Latency>().GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.01)));
        Assert.All(await fine.As<ServiceQuantile>().ToRecordsAsync(Ct).ToListAsync(Ct), a => Assert.InRange(a.Value90!.Value, exact[a.Service] * 0.99, exact[a.Service] * 1.01));
    }

    /// <summary>The value below which a share <paramref name="q"/> of <paramref name="values"/> lie, interpolated between ranks.</summary>
    private static double Percentile(IEnumerable<double> values, double q)
    {
        double[] sorted = [.. values.Order()];
        double rank = q * (sorted.Length - 1);
        int below = (int)Math.Floor(rank);
        return below + 1 < sorted.Length ? sorted[below] + ((rank - below) * (sorted[below + 1] - sorted[below])) : sorted[below];
    }

    [VortexRecord]
    public partial record struct Latency(string Service, double Value);

    [VortexRecord]
    public partial record struct ServiceQuantile(string Service, double? Value90);
}
