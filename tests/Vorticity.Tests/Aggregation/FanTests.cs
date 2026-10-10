using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key whose cardinality changes as the rows go: a quarter of the rows of few values, then many, or
/// the other way round. The lanes start across the source, lane i at i / N of its ranges, and the first
/// lane whose rows show many values turns the query to the core: an integer key on its first batch's
/// spread, a hashed one on the groups a sample of its first batch makes. Few values then many turns on the lanes that
/// start past the first quarter, which without the fan may all start in it; many then few turns on the
/// first lane, fan or not. The statistics say why and when.
/// </summary>
public sealed partial class FanTests
{
    // Rows enough that a lane judges its first rows while the queue still holds ranges: a lane that
    // judges once every range is taken has no batch left to turn at, and the query ends on its tables.
    private const int Rows = 3_200_000;

    [Theory]
    [InlineData("rising", true, GroupCoreReason.Spread)]
    [InlineData("rising", false, null)]
    [InlineData("falling", true, GroupCoreReason.Spread)]
    [InlineData("falling", false, GroupCoreReason.Spread)]
    [InlineData("hashed", true, GroupCoreReason.FirstRows)]
    [InlineData("hashed", false, null)]
    public async Task TheFirstLaneThatMeetsManyValuesTurnsTheQuery(string shape, bool fan, GroupCoreReason? expected)
    {
        // Without the fan, the lanes start on the queue's first ranges as the pool runs them: one that
        // starts late meets other rows first, and the outcome is the scheduler's. The answers are exact
        // whatever it is.
        Row[] rows = Rows_(shape);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            query.Plan.CoreLanes = 4;
            query.Plan.Fan = fan;
            Dictionary<long, KeyTotal> got = [];
            await foreach (KeyTotal total in query.As<KeyTotal>().ToRecordsAsync(Ct))
            {
                got.Add(total.Key, total);
            }

            Assert.Equal(rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => new KeyTotal(g.Key, g.Count(), g.Sum(r => r.Value))), got);
            GroupMetrics grouping = query.Metrics.Grouping!;
            if (expected is null)
            {
                return;
            }

            Assert.Equal(expected, grouping.CoreReason);

            // The first batch turns a lane before it folds a row: an integer key by its spread, a hashed one
            // by the groups a sample of its rows makes.
            Assert.True(grouping.TurnedAfterRows == 0, $"turned after {grouping.TurnedAfterRows} rows");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sessions", GroupCoreReason.Projection)]
    [InlineData("fixed", GroupCoreReason.None)]
    public async Task ALaneProjectsItsNewGroupsPastItsFirstRows(string shape, GroupCoreReason expected)
    {
        // Sessions of two rows, hashed: half of every batch new groups, which the first rows take for a key
        // of some 4·10⁴ values, and whose rate does not fall: the lanes turn on their projection. A key of
        // 10⁵ values hashed in no order brings new groups at a falling rate: nothing turns.
        Row[] rows = new Row[5_000_000];
        for (int row = 0; row < rows.Length; row++)
        {
            ulong mix = Mix((ulong)row);
            long key = shape == "sessions" ? (long)(Mix((ulong)row / 2) % (1UL << 40)) * 7 : (long)(mix % 100_000) * 7_919_000_003;
            rows[row] = new Row(key, (long)((mix >> 50) % 1_000));
        }

        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            query.Plan.CoreLanes = 4;
            long count = 0;
            long total = 0;
            await foreach (KeyTotal group in query.As<KeyTotal>().ToRecordsAsync(Ct))
            {
                count += group.Count;
                total += group.Total;
            }

            Assert.Equal(rows.Length, count);
            Assert.Equal(rows.Sum(r => r.Value), total);
            GroupMetrics grouping = query.Metrics.Grouping!;
            Assert.Equal(expected, grouping.CoreReason);
            Assert.Equal(rows.Select(r => r.Key).Distinct().Count(), grouping.Groups);
            if (expected == GroupCoreReason.Projection)
            {
                // Past its first rows and two windows more: three windows of a batch each.
                Assert.InRange(grouping.TurnedAfterRows, 3 * 65_536, rows.Length / 4);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A quarter of the rows of a thousand values, then 1.5 million values in no order; the other way
    /// round; or a thousand values, then keys hashed over 2⁴⁰ values, which nothing bounds.
    /// </summary>
    private static Row[] Rows_(string shape)
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = Mix((ulong)row);
            bool early = row < Rows / 4;
            long few = (long)(mix % 1_000);
            long many = shape == "hashed" ? (long)((mix >> 10) % (1UL << 40)) * 7 : (long)((mix >> 10) % 1_500_000);
            long key = shape == "falling" ? (early ? many : few) : (early ? few : many);
            rows[row] = new Row(key, (long)((mix >> 50) % 1_000));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fan");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(long Key, long Value);

    [VortexRecord]
    public partial record struct KeyTotal(long Key, long Count, long Total);
}
