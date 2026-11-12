using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// What a warm growth allocates follows its groups, not its points: the same two thousand series at 24 points and at 240,
/// a dataset's layout, by series then instant, read on one lane and on four. Alone in its collection, which measures what
/// every thread allocates.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class GrowthAllocationTests
{
    private const int Series = 2_000;

    private const int WarmUp = 4;

    private const int Runs = 5;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TenTimesThePointsAGroupAllocateNoMore()
    {
        ReleaseOnlyCeilings.Require();
        string few = await WriteAsync(24);
        string many = await WriteAsync(240);
        bool shuffled = AggregationPlan.ShuffledOrder;
        AggregationPlan.ShuffledOrder = false;
        try
        {
            StringBuilder report = new StringBuilder("GROWTH ALLOCATIONS: a warm query on every thread, floor of 5 after 4\n");
            foreach (int lanes in (int[])[1, 4])
            {
                long small = await FloorAsync(few, lanes);
                long large = await FloorAsync(many, lanes);
                report.Append(CultureInfo.InvariantCulture, $"    {lanes} lane(s): 24 points {small,12:N0} B, 240 points {large,12:N0} B\n");

                // The rows ten times more, the groups' states the same: measured on 2026-11-12, 97 216 B and 97 272 B at one
                // lane; 112 576 B and 159 864 B at four, whose lanes take more ranges of the larger file, about a tenth of a
                // byte a row more, where a point kept would be 24.
                Assert.True(large <= (small * 5 / 4) + (64 << 10), report.ToString());
            }
        }
        finally
        {
            AggregationPlan.ShuffledOrder = shuffled;
            System.IO.File.Delete(few);
            System.IO.File.Delete(many);
        }
    }

    private static async Task<long> FloorAsync(string path, int lanes)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        for (int i = 0; i < WarmUp; i++)
        {
            await QueryAsync(file);
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            int groups = await QueryAsync(file);
            floor = Math.Min(floor, GC.GetTotalAllocatedBytes(precise: true) - before);
            Assert.Equal(Series, groups);
        }

        return floor;
    }

    private static async Task<int> QueryAsync(VortexFile file)
    {
        int groups = 0;
        await foreach (Grown _ in file.Scan<Point>()
            .GroupBy(r => r.Series)
            .Select(g => (g.Key, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)))
            .As<Grown>()
            .ToRecordsAsync(Ct))
        {
            groups++;
        }

        return groups;
    }

    /// <summary>Two thousand counters of <paramref name="points"/> points each, by series then instant, falling now and then.</summary>
    private static async Task<string> WriteAsync(int points)
    {
        Random random = new Random(47);
        Point[] rows = [.. Enumerable.Range(0, Series).SelectMany(s =>
        {
            double value = 0;
            return Enumerable.Range(0, points).Select(i =>
            {
                value = random.Next(200) == 0 ? random.Next(10) : value + random.Next(100);
                return new Point($"s{s:D4}", Start.AddSeconds(15 * i), value);
            }).ToArray();
        })];
        string directory = Path.Combine(AppContext.BaseDirectory, "growth");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"allocations-{points}-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Point>(path))
        {
            await writer.WriteAsync<Point>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Point(string Series, DateTimeOffset At, double Value);

    [VortexRecord]
    public partial record struct Grown(string Series, double? Increase, double? First, double? Last);
}
