using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key of one fixed-width column finds its groups in two passes (PLAN-HIGH-CARDINALITY.md, H15):
/// each row's home slot with no branch on the keys, then the rows left in their order. Every distance
/// read ahead gives the groups of the row-at-a-time path, numbered as they first come, with nulls in
/// the key, keys spread or crowded into one chain, under a filter, on one lane and four.
/// </summary>
public sealed partial class TwoPassProbeTests
{
    private const int Rows = 150_000;

    public static TheoryData<string, int, bool> Cases => new TheoryData<string, int, bool>
    {
        { "spread", 1, false },
        { "spread", 1, true },
        { "spread", 4, false },
        { "crowded", 1, false },
        { "crowded", 4, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryDistanceGivesTheGroupsOfARowAtATime(string keys, int degree, bool filtered)
    {
        string path = await WriteAsync(keys);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Total> oneAtATime = await RunAsync(file, filtered, probe: -1, ordered: degree > 1);
            Assert.True(oneAtATime.Count > 1_000, $"{oneAtATime.Count} groups");
            Assert.Contains(oneAtATime, t => t.Key is null);
            foreach (int probe in (int[])[0, 1, 7, 64])
            {
                Assert.Equal(oneAtATime, await RunAsync(file, filtered, probe, ordered: degree > 1));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // The first pass: a key in its home slot gives its group, any other -1, and a read ahead changes
    // nothing of it.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1_000)]
    public void TheFirstPassFindsTheKeysInTheirHomeSlots(int ahead)
    {
        Vorticity.Aggregating.KeyTable<long> table = new Vorticity.Aggregating.KeyTable<long>();
        long[] keys = [.. Enumerable.Range(0, 40).Select(a => a * ((1L << 32) + 1)).Concat(Enumerable.Range(1, 500).Select(i => (long)i * 7))];
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i, table.GetOrAdd(keys[i], i));
        }

        long[] probes = [.. keys, -5, 1L << 40, 3_501];
        int[] groups = new int[probes.Length];
        table.FindAtHome(probes, groups, new uint[probes.Length], ahead, out bool missed);
        Assert.True(missed, "the absent keys found no group");
        int found = 0;
        for (int i = 0; i < probes.Length; i++)
        {
            // Found means right: the key's own group. The crowded keys share one home, which one of
            // them holds; the keys absent find nothing.
            if (groups[i] >= 0)
            {
                Assert.Equal(i, groups[i]);
                found++;
            }
            else
            {
                Assert.True(i >= keys.Length || i < 40 || table.GetOrAdd(probes[i], -1) == i);
            }
        }

        Assert.InRange(found, 400, 501);
    }

    // Keys at a stride of 2^22 past 2^32 folded into runs of ten homes in a row, which the prime of
    // 17 929 slots laid over each other: 12 % of 10^4 keys away from home, each row of them through the
    // second pass. Their shared low zeros left out, they are numbers in a row, each at home.
    [Theory]
    [InlineData(22, 10_000)]
    [InlineData(20, 10_000)]
    [InlineData(22, 100_000)]
    public void KeysAtAStrideOfAPowerOfTwoAllSitInTheirHomes(int stride, int count)
    {
        long[] keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = (long)i << stride;
        }

        new Random(7).Shuffle(keys);
        Vorticity.Aggregating.KeyTable<long> table = new Vorticity.Aggregating.KeyTable<long>();
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i, table.GetOrAdd(keys[i], i));
        }

        int[] groups = new int[keys.Length];
        table.FindAtHome(keys, groups, new uint[keys.Length], 0, out bool missed);
        Assert.False(missed, $"{groups.Count(g => g < 0)} of {count} keys away from home");
        Assert.Equal(Enumerable.Range(0, count), groups);
    }

    /// <summary>The groups as they come, in the order of their first rows on one lane; by key on more, whose merge orders them its own way.</summary>
    private static async Task<List<Total>> RunAsync(VortexFile file, bool filtered, int probe, bool ordered)
    {
        Scan<Row> scan = filtered ? file.Scan<Row>().Where(r => r.Value > 30L) : file.Scan<Row>();
        Vorticity.Aggregation grouped = scan.GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
        grouped.Plan.ProbeAhead = probe;
        // The lanes' keys, taken before the pass: the merge releases them.
        Vorticity.Aggregating.GroupKeys?[] lanes = [];
        grouped.Plan.Watch = partitions => lanes = [.. partitions.Select(partition => partition.Keys)];
        List<Total> totals = [];
        await foreach (Total total in grouped.As<Total>().ToRecordsAsync(Ct))
        {
            totals.Add(total);
        }

        // The key's blocks went through the hashed path, not all through their dictionaries' codes.
        Assert.Contains(lanes, keys => keys is { OnlyDictionaries: false });
        return ordered ? [.. totals.OrderBy(t => t.Key ?? long.MinValue)] : totals;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A key with a null every eleventh row, each of its 75 000 values twice, the rows 75 000 apart: a
    /// block of rows sees each value once, which no dictionary encodes, and the second half of the file
    /// finds its keys in the first pass. Spread, the values over the longs in no order; or crowded,
    /// a * (2^32 + 1), which all fold to one home and fill its line, then its chain, until the table
    /// takes a seed.
    /// </summary>
    private static async Task<string> WriteAsync(string keys)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "two-pass-probes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong value = (ulong)(row % (Rows / 2));
            long key = keys == "spread" ? (long)(value * 0xD6E8_FEB8_6659_FD93UL) : (long)value * ((1L << 32) + 1);
            rows[row] = new Row(row % 11 == 0 ? null : key, (long)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 8) % 100));
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(long? Key, long Value);

    [VortexRecord]
    public partial record struct Total(long? Key, long Count, long Sum);
}
