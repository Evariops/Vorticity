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
    /// Five services of 40 000 latencies each, spread over three orders of magnitude, sorted by the service, then the number
    /// each latency took as it came, and compacted into objects: the 90th percentile by service of those numbered 50 000 or
    /// more, at a 6 % error, lies within 6 % of LINQ's for each, a third of the blocks decoded at most, every object answered
    /// by its counts where the window holds it whole, else by its granules' histograms, each granule but those the window
    /// cuts proven or refuted; at 1 %, the error a quantile asks by default, within 1 %, the histograms' grid as fine. Asked
    /// of a store close by, which weighs bytes before requests: the objects, of 160 KB, read their rows in two requests.
    /// </summary>
    [Fact]
    public async Task AQuantileByAKeyConstantInEachGranuleIsAnsweredByItsHistograms()
    {
        Random random = new Random(19);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Latency.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Latency.Service)), LayoutValue.Column(nameof(Latency.Id))], ObjectBytes = 64 << 20, Organization = LayoutOrganization.Leveled },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
            CostProfile = Vorticity.IO.CostProfile.Memory,
        }, Ct);
        List<Latency> all = [];
        for (int a = 0; a < 2; a++)
        {
            Latency[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Latency($"svc-{random.Next(5)}", (a * 100_000) + i, Math.Round(Math.Exp(random.NextDouble() * 7), 3)))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Latency>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        ScanPlan explained = await dataset.Scan<Latency>().Where(r => r.Id >= 50_000).GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.06))).ExplainAsync(analyze: true, Ct);
        Assert.All(explained.Units, static unit => Assert.True(unit.Alternative is "granules" or "counts", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
        Assert.Contains(explained.Units, static unit => unit.Alternative == "granules");
        Dictionary<string, double> exact = all.Where(static r => r.Id >= 50_000).GroupBy(static r => r.Service).ToDictionary(static g => g.Key, static g => Percentile(g.Select(static r => r.Value), 0.9));
        Vorticity.Aggregation estimated = dataset.Scan<Latency>().Where(r => r.Id >= 50_000).GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.06)));
        List<ServiceQuantile> answers = await estimated.As<ServiceQuantile>().ToRecordsAsync(Ct).ToListAsync(Ct);
        long granules = (all.Count + 8191) / 8192;
        Console.WriteLine($"HISTOGRAMS: {estimated.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules; {string.Join(", ", answers.OrderBy(static a => a.Service).Select(a => $"{a.Service} {a.Value90:F1} for {exact[a.Service]:F1}"))}");
        Assert.Equal(5, answers.Count);
        Assert.All(answers, a => Assert.InRange(a.Value90!.Value, exact[a.Service] * 0.94, exact[a.Service] * 1.06));
        Assert.True(estimated.Metrics.BlocksDecoded <= granules / 3, $"{estimated.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules");

        Vorticity.Aggregation fine = dataset.Scan<Latency>().Where(r => r.Id >= 50_000).GroupBy(r => r.Service).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9, 0.01)));
        Assert.All(await fine.As<ServiceQuantile>().ToRecordsAsync(Ct).ToListAsync(Ct), a => Assert.InRange(a.Value90!.Value, exact[a.Service] * 0.99, exact[a.Service] * 1.01));
        Assert.True(fine.Metrics.BlocksDecoded <= granules / 3, $"at 1 %, {fine.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules");
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
    public partial record struct Latency(string Service, long Id, double Value);

    [VortexRecord]
    public partial record struct ServiceQuantile(string Service, double? Value90);
}
