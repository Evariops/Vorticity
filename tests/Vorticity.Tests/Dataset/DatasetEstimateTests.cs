// An aggregation's estimates, tier after tier: over rows drawn at random, the margins each tier announces hold the exact
// answers in at least 95 % of its groups, the exact tier is the question's own answer to the bits, and the requests a tier
// makes follow the ranges it reads, never the blocks those hold.
//
// THE ORACLE IS THE QUESTION: every estimate is held to the answers the same question gives over every row.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetEstimateTests
{
    private const int Objects = 8;
    private const int PerObject = 50_000;

    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Regions = ["eu-west", "eu-north", "us-east", "us-west", "ap-south", "sa-east", "af-south"];

    /// <summary>Objects whose columns end their chunks together every 4 096 rows, so that each holds a dozen ranges of 1 KiB of its narrowest column.</summary>
    private static readonly DatasetOptions Chunked = new DatasetOptions
    {
        ClusteringKey = [nameof(Hit.At)],
        ChunkBytes = 4 << 10,
        GroupRows = 4_096,
        Write = new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 16 << 10 },
    };

    /// <summary>The draws the coverage is measured over: twenty, or as many as <c>VX_ESTIMATE_DRAWS</c> says for a longer measure.</summary>
    private static readonly int Draws = int.TryParse(Environment.GetEnvironmentVariable("VX_ESTIMATE_DRAWS"), out int draws) ? draws : 20;

    private static readonly EstimateOptions Tiers = new EstimateOptions { Shares = [0.05, 0.2], RangeBytes = 1 << 10 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 400 000 rows in eight objects, 140 groups of a region and a status spread over every row, drawn twenty times: the
    /// margins of the counts, the sums and the means of the tiers of 5 % and 20 %, 95 % intervals, hold the exact answers
    /// 95 % of the time, short of it by three standard errors at most, and the last tier is the question's answer, every
    /// margin 0. The groups of a draw share its ranges, so the standard error comes from the spread between the draws, not
    /// from the count of margins. Measured on 2026-10-25 over 200 draws: 94.80 % at 5 %, three ranges drawn a stratum,
    /// 95.09 % at 20 %, each with a standard error of 0.09 %; the shortfall of the smallest draws is the t interval's on
    /// four degrees of freedom of a finite population, and narrows as the draws grow.
    /// </summary>
    [Fact]
    public async Task TheMarginsOfEachTierHoldTheExactAnswers()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        List<Total> exact = await Question(dataset).As<Total>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Dictionary<(string, int), Total> answers = exact.ToDictionary(t => (t.Region, t.Status));
        List<string> whole = [.. exact.Select(Text).Order(StringComparer.Ordinal)];

        double[][] coverage = [.. Tiers.Shares.Select(_ => new double[Draws])];
        for (int draw = 0; draw < Draws; draw++)
        {
            int tier = 0;
            await foreach (AggregationEstimate estimate in Question(dataset).EstimateAsync(Tiers with { Seed = draw + 1 }, Ct))
            {
                List<Estimated> groups = await estimate.As<Estimated>().ToRecordsAsync(Ct).ToListAsync(Ct);
                if (estimate.IsExact)
                {
                    Assert.Equal(whole, [.. groups.Select(g => Text(new Total(g.Region, g.Status, g.Rows, g.Bytes, g.Mean))).Order(StringComparer.Ordinal)]);
                    Assert.All(groups, g => Assert.Equal((0.0, 0.0, 0.0), (g.RowsMargin, g.BytesMargin, g.MeanMargin)));
                    continue;
                }

                Assert.InRange(estimate.Share, Tiers.Shares[tier] * 0.9, Tiers.Shares[tier] * 1.6);
                int covered = 0;
                foreach (Estimated group in groups)
                {
                    Total truth = answers[(group.Region, group.Status)];
                    covered += (Math.Abs(group.Rows - truth.Rows) <= group.RowsMargin + 0.5 ? 1 : 0)
                        + (Math.Abs(group.Bytes - truth.Bytes) <= group.BytesMargin + 0.5 ? 1 : 0)
                        + (Math.Abs(group.Mean!.Value - truth.Mean!.Value) <= group.MeanMargin ? 1 : 0);
                }

                coverage[tier][draw] = covered / (3.0 * groups.Count);
                tier++;
            }

            Assert.Equal(Tiers.Shares.Count, tier);
        }

        for (int t = 0; t < coverage.Length; t++)
        {
            double mean = coverage[t].Average();
            double error = Math.Sqrt(coverage[t].Sum(c => (c - mean) * (c - mean)) / (Draws - 1) / Draws);
            Console.WriteLine($"ESTIMATES: tier {Tiers.Shares[t]:P0}, the margins hold the exact answers {mean:P2} of the time over {Draws} draws, a standard error of {error:P2}");
            Assert.True(mean >= 0.95 - (3 * error), $"tier {Tiers.Shares[t]:P0}: {mean:P2}, a standard error of {error:P2}");
        }
    }

    /// <summary>
    /// The requests of a tier, over a store that counts them, follow the ranges it reads: one a range and a column read at
    /// most, whose chunks the range holds whole, and the objects' opens at the first tier; drawn block by block, the same
    /// rows would cost a request a block and a column, twice as many at least.
    /// </summary>
    [Fact]
    public async Task ATiersRequestsFollowItsRangesNotItsBlocks()
    {
        const int Columns = 4;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await FilledAsync(store);
        long before = store.CountOf(ObjectOperation.Read);
        int ranges = 0;
        long blocks = 0;
        bool first = true;
        await foreach (AggregationEstimate estimate in Question(dataset).EstimateAsync(Tiers with { Seed = 7, Exact = false }, Ct))
        {
            long requests = store.CountOf(ObjectOperation.Read) - before;
            int read = estimate.Ranges - ranges;
            long decoded = estimate.Metrics.BlocksDecoded - blocks;
            Console.WriteLine($"ESTIMATE REQUESTS: tier {estimate.Share:P1}, {read} ranges read, {requests} requests, {decoded} blocks decoded");
            Assert.InRange(requests, read, (read * Columns) + (first ? Objects : 0));
            Assert.True(decoded * Columns >= 2 * requests, $"{decoded} blocks decoded in {requests} requests");
            before = store.CountOf(ObjectOperation.Read);
            ranges = estimate.Ranges;
            blocks = estimate.Metrics.BlocksDecoded;
            first = false;
        }
    }

    /// <summary>An estimate refuses what no state answers whole, and a question that filters, orders or windows its groups.</summary>
    [Fact]
    public async Task AnEstimateRefusesWhatItCannotScale()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        Assert.Throws<NotSupportedException>(() => dataset.Scan<Hit>().GroupBy(r => r.Region).Select(g => (g.Key, g.CountDistinct(r => r.Status))).EstimateAsync(cancellationToken: Ct));
        Assert.Throws<NotSupportedException>(() => dataset.Scan<Hit>().GroupBy(r => r.Region).OrderByDescending(g => g.Count()).Take(3).Select(g => (g.Key, g.Count())).EstimateAsync(cancellationToken: Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => Question(dataset).EstimateAsync(new EstimateOptions { Shares = [0.2, 0.1] }, Ct));
    }

    /// <summary>The count, the bytes and the mean latency of each region and status.</summary>
    private static Vorticity.Aggregation Question(VortexDataset dataset) =>
        dataset.Scan<Hit>().GroupBy(r => (r.Region, r.Status)).Select(g => (g.Key.Region, g.Key.Status, g.Count(), g.Sum(r => r.Bytes), g.Average(r => r.Latency)));

    /// <summary>Eight objects of 50 000 rows, a row every 0.216 s, its region and status at random, its bytes and latency uniform.</summary>
    private static async Task<VortexDataset> FilledAsync(IObjectStore store, DatasetOptions? options = null)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Hit.Schema, options ?? Chunked, Ct);
        Random random = new Random(0xE57);
        for (int o = 0; o < Objects; o++)
        {
            Hit[] rows = new Hit[PerObject];
            for (int i = 0; i < rows.Length; i++)
            {
                long at = ((long)o * PerObject) + i;
                rows[i] = new Hit(Regions[random.Next(Regions.Length)], 200 + random.Next(20), Start.AddTicks(at * 2_160_000L), random.NextInt64(0, 1_000_000), random.NextDouble() * 1_000);
            }

            // Written a group of rows at a time: the writer ends every column's chunks together where a write ends past a group.
            await using ObjectDraft draft = dataset.StartObject();
            for (int at = 0; at < rows.Length; at += 4_096)
            {
                await draft.Writer.WriteAsync<Hit>(rows.AsSpan(at, Math.Min(4_096, rows.Length - at)).ToArray(), Ct);
            }

            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
    }

    private static string Text(Total total) => total.ToString();

    [VortexRecord]
    public partial record struct Hit(string Region, int Status, DateTimeOffset At, long Bytes, double Latency);

    [VortexRecord]
    public partial record struct Total(string Region, int Status, long Rows, long Bytes, double? Mean);

    [VortexRecord]
    public partial record struct Estimated(string Region, int Status, long Rows, long Bytes, double? Mean, double RowsMargin, double BytesMargin, double MeanMargin);
}
