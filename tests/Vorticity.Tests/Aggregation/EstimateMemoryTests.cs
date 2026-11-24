using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>What an estimate holds of its session's memory.</summary>
public sealed partial class EstimateMemoryTests
{
    [VortexRecord]
    public partial record struct Hit(long User, long Bytes);

    [VortexRecord]
    public partial record struct UserBytes(long User, long Rows, long Bytes, double RowsMargin, double BytesMargin);

    /// <summary>
    /// An estimate's sample and its sums by stratum and group are held to its session's budget: under a budget each range's
    /// part fits and the sample of every user does not, the estimate fails with a memory error, where it held the sample
    /// past the budget, counted nowhere. Under a budget that holds it, the same estimate answers, and gives back all it held.
    /// </summary>
    [Fact]
    public async Task AnEstimatesSampleIsHeldToItsSessionsBudget()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-estimate-{Guid.NewGuid():N}.vortex");
        Hit[] hits = new Hit[400_000];
        for (int i = 0; i < hits.Length; i++)
        {
            hits[i] = new Hit((long)((ulong)i * 0x9E3779B97F4A7C15UL >> 44), i % 1_000);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Hit>(path))
        {
            await writer.WriteAsync<Hit>(hits, ct);
            await writer.CompleteAsync(ct);
        }

        EstimateOptions tiers = new EstimateOptions { Shares = [0.5], RangeBytes = 1 << 14, ExactUnder = TimeSpan.Zero, Exact = false, Seed = 5 };
        try
        {
            QueryMemoryBudget tight = new QueryMemoryBudget(4L << 20);
            await using (VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 2;
                options.MemoryBudget = tight;
            }))
            await using (VortexFile file = await session.OpenAsync(path, cancellationToken: ct))
            {
                await Assert.ThrowsAsync<VortexMemoryException>(async () =>
                {
                    await foreach (AggregationEstimate tier in Users(file).EstimateAsync(tiers, ct))
                    {
                        await foreach (UserBytes _ in tier.As<UserBytes>().ToRecordsAsync(ct))
                        {
                        }
                    }
                });
            }

            Assert.Equal(0, tight.ReservedBytes);
            QueryMemoryBudget roomy = new QueryMemoryBudget(1L << 30);
            await using (VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 2;
                options.MemoryBudget = roomy;
            }))
            await using (VortexFile file = await session.OpenAsync(path, cancellationToken: ct))
            {
                long groups = 0;
                await foreach (AggregationEstimate tier in Users(file).EstimateAsync(tiers, ct))
                {
                    await foreach (UserBytes _ in tier.As<UserBytes>().ToRecordsAsync(ct))
                    {
                        groups++;
                    }
                }

                Assert.True(groups > 0);
            }

            Assert.True(roomy.PeakBytes > 4L << 20, $"{roomy.PeakBytes:N0} bytes held at most");
            Assert.Equal(0, roomy.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Vorticity.Aggregation Users(VortexFile file) =>
        file.Scan<Hit>().GroupBy(h => h.User).Select(g => (g.Key, g.Count(), g.Sum(h => h.Bytes)));
}
