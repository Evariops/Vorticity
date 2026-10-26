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
        DatasetOptions options = new DatasetOptions { Seed = (ulong)seed, ClusteringKey = ["Source", "Route", "Target"] };
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
