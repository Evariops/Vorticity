// The tree led by the partition (spec A, 5; A5): every object's tree key leads with its partition, then its first row's
// key; each node tallies its objects by size class and the latest arrival; a walk filtered on the partition's column
// passes the other partitions' subtrees over unread; a layout without a key places its objects by partition and position.
//
// THE ORACLE IS THE ROWS AND THE LEAVES: each object's rows say its partition, its keys and its order (a full
// verification, I1 to I5); a level's root tallies what its leaves hold; reads return what LINQ keeps.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPartitionTreeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Forty small appends partitioned by day over thirty days, each its own objects at level 0, so that the tree's pages hold
    /// neighbouring partitions: a walk filtered on one day opens fewer objects than the level holds and passes subtrees over
    /// by their summaries, unread; the day's rows are LINQ's.
    /// </summary>
    [Fact]
    public async Task AWalkFilteredOnThePartitionPassesTheOtherPartitionsOverUnread()
    {
        Random random = new Random(9);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Row.Day)), SortKey = [LayoutValue.Column(nameof(Row.Service))] },
            AppendBufferBytes = 64 << 10,
        }, Ct);
        List<Row> all = [];
        for (int d = 0; d < 40; d++)
        {
            Row[] rows = [.. Enumerable.Range(0, 6_000).Select(i => new Row(random.Next(0, 30), $"svc-{random.Next(0, 9)}", (d * 6_000) + i, random.Next(0, 50)))];
            all.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        long objects = await dataset.ScanBuilder().ObjectsAsync(Ct).CountAsync(Ct);
        DatasetPlan plan = await dataset.ScanBuilder().Where(Expr.Eq(Expr.Field(nameof(Row.Day)), Expr.Literal(FilterLiteral.From(7)))).ExplainAsync(Ct);
        Console.WriteLine($"PARTITION TREE: {objects} objects at level 0 over 30 days; Day == 7 opens {plan.Objects}, {plan.ObjectsSkipped} objects and {plan.SubtreesSkipped} subtrees passed over unread");
        Assert.True(plan.SubtreesSkipped > 0 && plan.Objects < objects, $"Day == 7 opens {plan.Objects} of {objects} objects, {plan.SubtreesSkipped} subtrees passed over");
        Assert.Equal(all.Count(r => r.Day == 7), await dataset.Scan<Row>().Where(r => r.Day == 7).ToRecordsAsync(Ct).CountAsync(Ct));
    }

    /// <summary>
    /// Four appends of 20 000 rows over six days, keyed by service and status, partitioned by day or not, then merged: the
    /// layout's invariants hold (each object one partition's, sorted, keyed by its first and last rows, levels disjoint),
    /// each object states its arrival, each level's root tallies its leaves' objects by class and their latest arrival, and
    /// reads return LINQ's rows.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachLevelsRootTalliesItsObjectsAndTheirKeysLeadWithTheirPartition(bool partitioned)
    {
        Random random = new Random(partitioned ? 5 : 6);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = partitioned ? LayoutValue.Column(nameof(Row.Day)) : null,
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
            },
            AppendBufferBytes = 256 << 10,
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        for (int d = 0; d < 4; d++)
        {
            Row[] rows = Rows(random, 20_000, all.Count, days: 6);
            all.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        DatasetVerification appended = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(appended.Holds, string.Join('\n', appended.Problems));
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        DatasetVerification merged = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(merged.Holds, string.Join('\n', merged.Problems));
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(held.Entry.Arrival > 0, $"'{held.Entry.Key}' states no arrival");
        }

        DatasetSnapshot version = dataset.Snapshot;
        ObjectSummaryFold fold = ObjectSummaryFold.For(version.Header.Layout);
        foreach ((int level, DatasetTree tree) in version.Levels.Occupied())
        {
            ReadOnlyMemory<byte> page = await version.Pages.ReadPageAsync(tree.Root, Ct);
            ObjectTally? tally = null;
            if (tree.Depth == 1)
            {
                foreach (TreeEntry entry in TreePage.ReadLeaf(page))
                {
                    ObjectTally one = fold.TallyOf(entry.Value.Span);
                    tally = tally is { } sum ? sum.With(one) : one;
                }
            }
            else
            {
                foreach (InternalEntry child in TreePage.ReadInternal(page))
                {
                    ObjectSummaryFold.SummariesOf(child.Summary.Span, out ObjectTally? part);
                    tally = tally is { } sum && part is { } more ? sum.With(more) : part;
                }
            }

            long objects = 0;
            long latest = 0;
            await foreach (PositionedEntry held in tree.WalkAsync(version.Pages, 0, long.MaxValue, null, Ct))
            {
                objects++;
                latest = Math.Max(latest, ObjectEntry.FromBytes(held.Entry.Value).Arrival);
            }

            Assert.NotNull(tally);
            long counted = Enumerable.Range(0, SizeClasses.Count).Sum(c => tally.Value.Classes[c]);
            Assert.Equal((objects, latest), (counted, tally.Value.Latest));
        }

        Assert.Equal(all.Count(r => r.Day == 2 && r.Service == "svc-3"), await dataset.Scan<Row>().Where(r => r.Day == 2 && r.Service == "svc-3").ToRecordsAsync(Ct).CountAsync(Ct));
        Assert.Equal(all.Count, await dataset.Scan<Row>().ToRecordsAsync(Ct).CountAsync(Ct));
    }

    /// <summary>
    /// Four appends partitioned by day under no key, merged: each object holds one day's rows, its tree key that day and then a
    /// position no other object holds, and the objects hold every row appended.
    /// </summary>
    [Fact]
    public async Task AnObjectOfALayoutWithoutAKeyIsPlacedByItsPartitionAndPosition()
    {
        Random random = new Random(7);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Row.Day)) },
            AppendBufferBytes = 256 << 10,
        }, Ct);
        List<Row> all = [];
        for (int d = 0; d < 4; d++)
        {
            Row[] rows = Rows(random, 20_000, all.Count, days: 6);
            all.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        SortKey tree = dataset.Snapshot.Schema.Tree!;
        HashSet<long> positions = [];
        List<Row> read = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            List<Row> rows;
            await using (ObjectLease lease = await dataset.RentAsync(held.Entry, Ct))
            {
                rows = await lease.File.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
            }

            read.AddRange(rows);
            ReadOnlySpan<byte> order = VortexDataset.OrderOf(held.TreeKey).Span;
            int partition = tree.PrefixLength(order, 1);
            Assert.All(rows, row => Assert.Equal(rows[0].Day, row.Day));
            Assert.Equal(partition + sizeof(long), order.Length);
            Assert.NotEqual(FilterLiteralKind.Null, RowEncoder.ReadFirstValue(order, tree.TypeAt(0)).Kind);
            Assert.True(positions.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(order[partition..])), "two objects hold one position");
        }

        Assert.Equal(all, read.OrderBy(r => r.Sequence));
    }

    private static Row[] Rows(Random random, int count, long first, int days)
    {
        string[] services = [.. Enumerable.Range(0, 9).Select(i => $"svc-{i}")];
        return [.. Enumerable.Range(0, count).Select(i => new Row(random.Next(0, days), services[random.Next(services.Length)], first + i, random.Next(0, 50)))];
    }

    private static async Task AppendAsync(VortexDataset dataset, Row[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status);
}
