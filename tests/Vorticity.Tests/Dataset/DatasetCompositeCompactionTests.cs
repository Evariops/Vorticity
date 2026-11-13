// A compaction under a composite key of wide integers, over objects of rows in no order. The merge
// hands the writer runs of a few rows, one input after another, and a run that small waits in the
// writer's arena: copied there behind the offsets its list column was just rebased to, it held the
// key's 64-bit values four bytes off their boundary, and the composite index refused them. The rows
// are shuffled and cut into twelve to forty-one objects of random sizes, over few enough keys that
// runs of one row alternate with runs of ten, and every seed is compacted three times.
//
// THE ORACLE IS THE ROWS APPENDED: the dataset reads back the same rows after the compactions.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetCompositeCompactionTests
{
    private const int Rows = 2_000;

    /// <summary>A row: a key of three 64-bit integers, 32-bit counters, a label and a histogram.</summary>
    [VortexRecord]
    public partial record struct Hop(ulong Source, ulong Route, ulong Target, uint Calls, uint Errors, string Label, long[] Buckets);

    /// <summary>A row: the same key, a list of counts and a list of texts, each long enough to weigh more than the rest of the row.</summary>
    [VortexRecord]
    public partial record struct Histogram(ulong Source, ulong Route, ulong Target, long Sequence, long[] Buckets, string[] Tags);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ShuffledRowsUnderACompositeKeyOfWideIntegersCompactFromManySmallObjects(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Random random = new Random(seed);
        Hop[] rows = Hops(random);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions { Seed = (ulong)seed, Layout = new DatasetLayout { SortKey = [LayoutValue.Column("Source"), LayoutValue.Column("Route"), LayoutValue.Column("Target")] } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Hop.Schema, options, ct);
        int objects = 12 + random.Next(30);
        int at = 0;
        for (int o = 0; o < objects; o++)
        {
            int left = rows.Length - at;
            int size = o == objects - 1
                ? left
                : Math.Min(Math.Max(1, random.Next(1, Math.Max(2, 2 * left / (objects - o)))), left - (objects - o - 1));
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hop>(rows.AsSpan(at, size), ct);
            await dataset.AppendAsync(draft, ct);
            at += size;
        }

        Assert.Equal(objects, dataset.Levels[0].Entries);
        for (int round = 0; round < 3; round++)
        {
            await dataset.CompactAsync(cancellationToken: ct);
        }

        Assert.True(dataset.Levels[0].Entries < objects, $"{dataset.Levels[0].Entries} objects left at level 0 of {objects}");
        Assert.Equal(Describe(rows), Describe(await dataset.Scan<Hop>().ToRecordsAsync(ct).ToListAsync(ct)));
    }

    /// <summary>
    /// A merge writes of a list column the elements its rows name, once. An append sorts its rows by the key and leaves the
    /// elements of their lists where they came, so the rows a merge takes from an object, a few at a time between another's,
    /// lie apart in that object's elements: written as the extent from the first of them to the last, each run of rows
    /// carried nearly every element of its object, and objects of histograms came out of their merge many times as large
    /// as they went in. The oracle is the bytes the job read against those it wrote, and the rows read back.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AMergeWritesTheElementsOfAListColumnOnce(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Random random = new Random(seed);
        const int Objects = 12;
        Histogram[] rows = Histograms(random, Objects * 400);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions { Seed = (ulong)seed, Layout = new DatasetLayout { SortKey = [LayoutValue.Column("Source"), LayoutValue.Column("Route"), LayoutValue.Column("Target")] } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Histogram.Schema, options, ct);
        for (int at = 0; at < rows.Length; at += rows.Length / Objects)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Histogram>(rows.AsSpan(at, rows.Length / Objects), ct);
            await dataset.AppendAsync(draft, ct);
        }

        // Level 0, past its ceiling, leaves for level 1 in one merge of all its objects.
        CompactionResult? merged = await dataset.CompactAsync(cancellationToken: ct);
        Assert.NotNull(merged);
        Assert.Equal(Objects, merged.ObjectsIn);
        Assert.Equal(rows.Length, merged.Rows);
        Assert.True(
            merged.BytesOut <= merged.BytesIn,
            $"the merge read {merged.BytesIn:N0} bytes in {merged.ObjectsIn} objects and wrote {merged.BytesOut:N0}");
        List<Histogram> read = await dataset.Scan<Histogram>().ToRecordsAsync(ct).ToListAsync(ct);
        Assert.Equal(Describe(rows), Describe(read));
    }

    /// <summary>
    /// An append of more rows than one batch of its writer holds writes of a list column the elements its rows name, once.
    /// The append sorts its rows by the key and hands them to the writer a batch at a time, each batch a part of the rows
    /// that names elements all over what the append held: written as the extent they span, every batch carried the
    /// append's elements whole. The oracle is the same rows appended in the order of their key, whose batches lie one
    /// after the other in their elements: the object of the rows in no order weighs what that one does.
    /// </summary>
    [Fact]
    public async Task AnAppendOfRowsInNoOrderWritesItsListsOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Random random = new Random(11);
        Histogram[] rows = new Histogram[150_000];
        for (int i = 0; i < rows.Length; i++)
        {
            long[] buckets = new long[4 + random.Next(5)];
            for (int b = 0; b < buckets.Length; b++)
            {
                buckets[b] = random.NextInt64(1L << 40);
            }

            rows[i] = new Histogram((ulong)random.Next(1 << 20), 0, (ulong)random.Next(64), i, buckets, []);
        }

        Histogram[] inOrder = [.. rows.OrderBy(r => r.Source).ThenBy(r => r.Route).ThenBy(r => r.Target)];
        long asTheyCame = await AppendedBytesAsync(rows, ct);
        long sorted = await AppendedBytesAsync(inOrder, ct);

        // The two objects hold the same rows in the same order: a hundredth apart at most, where batches that each
        // carried every element made the first fifteen times the second.
        Assert.InRange(asTheyCame, sorted - (sorted / 100), sorted + (sorted / 100));
    }

    /// <summary>The bytes of the one object an append of <paramref name="rows"/> writes, under the key of three integers.</summary>
    private static async Task<long> AppendedBytesAsync(Histogram[] rows, CancellationToken ct)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions { Seed = 11, Layout = new DatasetLayout { SortKey = [LayoutValue.Column("Source"), LayoutValue.Column("Route"), LayoutValue.Column("Target")] } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Histogram.Schema, options, ct);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Histogram>(rows, ct);
        await dataset.AppendAsync(draft, ct);
        CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: ct);
        Assert.Equal(1, plan.ObjectsByLevel[0]);
        Assert.Equal(rows.Length, dataset.RowCount);
        return plan.BytesByLevel[0];
    }

    /// <summary>
    /// Rows in no order of their key, each with 16 to 48 counts and 4 to 12 texts, some of the texts too long for a view:
    /// lists long enough that their elements are most of an object, over keys few enough that the rows of every object
    /// interleave with every other's. An eighth of the rows hold an empty list.
    /// </summary>
    private static Histogram[] Histograms(Random random, int count)
    {
        Histogram[] rows = new Histogram[count];
        for (int i = 0; i < rows.Length; i++)
        {
            bool empty = random.Next(8) == 0;
            long[] buckets = new long[empty ? 0 : 16 + random.Next(33)];
            for (int b = 0; b < buckets.Length; b++)
            {
                buckets[b] = random.NextInt64(1L << 40);
            }

            string[] tags = new string[empty ? 0 : 4 + random.Next(9)];
            for (int t = 0; t < tags.Length; t++)
            {
                tags[t] = random.Next(4) == 0 ? $"a tag long enough to leave its view, {random.Next(1 << 20)}" : $"t{random.Next(1 << 20)}";
            }

            rows[i] = new Histogram((ulong)random.Next(64), (ulong)random.Next(16) << 40, (ulong)random.Next(64), i, buckets, tags);
        }

        return rows;
    }

    /// <summary>The rows as sorted text, so that two sets of them compare whatever their order.</summary>
    private static List<string> Describe(IEnumerable<Histogram> rows) =>
        [.. rows.Select(r => $"{r.Source}|{r.Route}|{r.Target}|{r.Sequence}|{string.Join(',', r.Buckets)}|{string.Join(',', r.Tags)}").Order(StringComparer.Ordinal)];

    /// <summary>The rows, shuffled: 48 keys, so that one object holds several rows of a key and another holds none.</summary>
    private static Hop[] Hops(Random random)
    {
        Hop[] rows = new Hop[Rows];
        for (int i = 0; i < rows.Length; i++)
        {
            long[] buckets = new long[random.Next(6)];
            for (int b = 0; b < buckets.Length; b++)
            {
                buckets[b] = random.Next(1_000);
            }

            string label = random.Next(3) == 0 ? $"a label long enough to leave its view, {i}" : $"l{i % 30}";
            rows[i] = new Hop(
                (ulong)random.Next(4), (ulong)random.Next(3) << 40, (ulong)random.Next(4), (uint)random.Next(10_000), (uint)random.Next(10), label, buckets);
        }

        return rows;
    }

    /// <summary>The rows as sorted text, so that two sets of them compare whatever their order.</summary>
    private static List<string> Describe(IEnumerable<Hop> rows) =>
        [.. rows.Select(r => $"{r.Source}|{r.Route}|{r.Target}|{r.Calls}|{r.Errors}|{r.Label}|{string.Join(',', r.Buckets)}").Order(StringComparer.Ordinal)];
}
