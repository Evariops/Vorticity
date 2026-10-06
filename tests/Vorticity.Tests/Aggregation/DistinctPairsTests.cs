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
/// The pairs of a distinct count chained by group (PLAN-HIGH-CARDINALITY, H9): a merge of some groups
/// takes their pairs alone, keeping some groups keeps theirs alone, and a group by counts each value
/// once a group whether its lanes merge in series, in many parts, or as it streams.
/// </summary>
public sealed partial class DistinctPairsTests
{
    private const int Rows = 240_000;

    [Fact]
    public void AMergeOfSomeGroupsTakesTheirPairsAlone()
    {
        DistinctPairs<long> source = Filled(groups: 10, perGroup: 50);
        DistinctPairs<long> target = new DistinctPairs<long>();
        target.EnsureGroups(2);

        // Groups 3 and 7 of the source into groups 0 and 1, group 7 twice: its pairs are there already.
        Assert.Equal(50, target.MergeGroup(source, from: 3, into: 0));
        Assert.Equal(50, target.MergeGroup(source, from: 7, into: 1));
        Assert.Equal(0, target.MergeGroup(source, from: 7, into: 1));
        Assert.Equal(100, target.Count);
        Assert.False(target.Add(0, Value(3, 0)));
        Assert.True(target.Add(0, Value(7, 0)));
    }

    [Fact]
    public void AMergeOfEveryGroupMapsEachAndCountsTheNewPairs()
    {
        DistinctPairs<long> source = Filled(groups: 4, perGroup: 30);
        DistinctPairs<long> target = Filled(groups: 2, perGroup: 30);
        long[] counts = [30, 30];

        // Groups 0 and 1 meet the target's own, which hold the same values; 2 and 3 fold into them.
        target.MergeAll(source, [0, 1, 0, 1], counts);
        Assert.Equal([60, 60], counts);
        Assert.Equal(120, target.Count);
    }

    [Fact]
    public void KeepingGroupsKeepsTheirPairsUnderTheirNewNumbers()
    {
        DistinctPairs<long> pairs = Filled(groups: 10, perGroup: 20);
        pairs.Keep([2, 9]);
        Assert.Equal(40, pairs.Count);
        Assert.False(pairs.Add(0, Value(2, 5)));
        Assert.False(pairs.Add(1, Value(9, 19)));
        Assert.True(pairs.Add(0, Value(9, 19)));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(4, false)]
    [InlineData(14, true)]
    public async Task AGroupByCountsEachValueOnceAGroupWhateverItsMerge(int degree, bool inParts)
    {
        Row[] rows = Rows_(sorted: false);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Row>()
                .GroupBy(r => r.Key)
                .Select(g => (g.Key, g.CountDistinct(x => x.Value), g.CountDistinct(x => x.Text)));
            query.Plan.MergeInParts = inParts;
            query.Plan.MergeParts = inParts ? 64 : null;
            Dictionary<int, KeyCounts> got = (await ListAsync(query.As<KeyCounts>())).ToDictionary(c => c.Key);
            Assert.Equal(Expected(rows), got);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AStreamingGroupByKeepsItsOpenGroupsPairs(int degree)
    {
        // The key sorted: the groups go out as they close, and each batch keeps the open ones' pairs.
        Row[] rows = Rows_(sorted: true);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Row>()
                .GroupBy(r => r.Key)
                .Select(g => (g.Key, g.CountDistinct(x => x.Value), g.CountDistinct(x => x.Text)));
            List<KeyCounts> got = await ListAsync(query.As<KeyCounts>());
            Assert.Equal(Expected(rows), got.ToDictionary(c => c.Key));
            Assert.Equal(got.Select(c => c.Key).Order(), got.Select(c => c.Key));

            // It streamed: it held about a batch's groups at once, kept from one batch to the next.
            long peak = ((AggregationQuery)query.Query).PeakGroups;
            Assert.True(peak * 3 < got.Count, $"the group by held {peak} of {got.Count} groups at once");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Dictionary<int, KeyCounts> Expected(Row[] rows) =>
        rows.GroupBy(r => r.Key).ToDictionary(
            g => g.Key,
            g => new KeyCounts(g.Key, g.Select(r => r.Value).Distinct().Count(), g.Select(r => r.Text).Distinct(StringComparer.Ordinal).Count()));

    /// <summary>Pairs of <paramref name="groups"/> groups, <paramref name="perGroup"/> values each, the values of one group the same in every set.</summary>
    private static DistinctPairs<long> Filled(int groups, int perGroup)
    {
        DistinctPairs<long> pairs = new DistinctPairs<long>();
        pairs.EnsureGroups(groups);
        for (int group = 0; group < groups; group++)
        {
            for (int i = 0; i < perGroup; i++)
            {
                Assert.True(pairs.Add(group, Value(group, i)));
                Assert.False(pairs.Add(group, Value(group, i)));
            }
        }

        return pairs;
    }

    /// <summary>The <paramref name="i"/>-th value a group was filled with: its own, apart from any other group's.</summary>
    private static long Value(int group, int i) => ((long)group << 32) | (uint)i;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Sixty thousand keys, a value of a few hundred and a text of a few dozen a row, the keys in no order or sorted.</summary>
    private static Row[] Rows_(bool sorted)
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            rows[row] = new Row((int)((mix >> 24) % 60_000), (long)((mix >> 8) % 300), $"t-{(mix >> 16) % 40:D2}");
        }

        if (sorted)
        {
            Array.Sort(rows, (a, b) => a.Key.CompareTo(b.Key));
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
        string directory = Path.Combine(AppContext.BaseDirectory, "distinct-pairs");
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
    public partial record struct Row(int Key, long Value, string Text);

    [VortexRecord]
    public partial record struct KeyCounts(int Key, long Values, long Texts);
}
