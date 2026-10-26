// The bounds of an object's keys in its entry: its first key, which its tree key leads with, and its last, row-encoded,
// for a key of any type. A text key, which a file's statistics do not bound, then has the level ranges and the proof of
// one clustering value a number has.
//
// THE ORACLE IS THE OBJECT'S OWN ROWS: an entry's last key is the largest key its rows hold, encoded as the dataset's key
// encodes it; and every dataset here reads back the rows appended.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ObjectKeyBoundsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryObjectStatesItsLastKeyAppendedOrWrittenByAJob()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Tenanted.Schema, new DatasetOptions { ClusteringKey = [nameof(Tenanted.Tenant), nameof(Tenanted.Id)] }, Ct);
        Random random = new Random(0xB0);
        List<Tenanted> appended = [];
        for (int append = 0; append < 6; append++)
        {
            Tenanted[] rows = [.. Enumerable.Range(0, 200).Select(i => new Tenanted($"tenant-{random.Next(7)}", random.NextInt64(10_000), i))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
            await HoldTheirLastKeysAsync(dataset);
        }

        while (await dataset.CompactAsync(Compaction(), Ct) is not null)
        {
        }

        await HoldTheirLastKeysAsync(dataset);
        Assert.Equal(appended.Count, (int)dataset.RowCount);

        // A round trip through the entry's bytes keeps it.
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            byte[] bytes = held.Entry.ToBytes();
            Assert.Equal(held.Entry.LastKey.ToArray(), ObjectEntry.FromBytes(bytes).LastKey.ToArray());
            Assert.Equal(held.Entry.LastKey.ToArray(), ObjectEntry.LastKeyOf(bytes).ToArray());
        }
    }

    [Fact]
    public async Task AKeyOfOneNumberStatesNoLastKeyWhichItsStatisticsBound()
    {
        // A number's statistics bound a key of it alone, and its entries keep the format they had; a composite key of
        // numbers states its last key, since its leading column's bounds would take every object of a value of it.
        foreach ((string[] key, bool states) in new[] { ((string[])[nameof(Numbered.Id)], false), ([nameof(Numbered.Group), nameof(Numbered.Id)], true) })
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Numbered.Schema, new DatasetOptions { ClusteringKey = key }, Ct);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Numbered>([.. Enumerable.Range(0, 100).Select(i => new Numbered(i % 3, 100 - i))], Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
            {
                Assert.Equal(states, !held.Entry.LastKey.IsEmpty);
                Assert.Equal(states, held.Entry.HasFields);
            }
        }
    }

    [Fact]
    public async Task ALevelRangeOfATextKeyTakesTheObjectsItsKeysMeetAlone()
    {
        // Each tenant's rows in objects of their own, then rows of one tenant: the job out of level 0 takes that tenant's
        // objects, where a range of the text's summaries, which bound nothing, took every object of the level.
        string[] tenants = ["acme", "globex", "initech", "umbrella"];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Tenanted.Schema, Options(ClusteringValue.Column), Ct);
        long id = 0;
        foreach (string tenant in tenants)
        {
            await AppendAsync(dataset, [.. Enumerable.Range(0, 300).Select(i => new Tenanted(tenant, id++, i))]);
        }

        while (await dataset.CompactAsync(Compaction() with { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        int objects = 0;
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            objects++;
        }

        await AppendAsync(dataset, [.. Enumerable.Range(0, 50).Select(i => new Tenanted("globex", id++, i))]);
        CompactionPlan plan = await dataset.PlanCompactionAsync(Compaction() with { LevelZeroCeiling = 0 }, Ct);
        CompactionJob job = plan.Job!;
        Assert.Equal(0, job.FromLevel);
        Console.Out.Write(FormattableString.Invariant(
            $"TEXT KEY RANGE: {objects} objects above level 0 for {tenants.Length} tenants; the job out of level 0 reads {job.Inputs.Count} objects for one tenant's rows.\n"));
        Assert.True(job.Inputs.Count < objects, $"the job reads {job.Inputs.Count} objects of {objects}");
        foreach (CompactionInput input in job.Inputs)
        {
            Assert.True(input.Level == 0 || await TenantsAsync(dataset, input.Entry) is ["globex"], $"an input of level {input.Level} holds another tenant");
        }

        while (await dataset.CompactAsync(Compaction() with { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        Assert.Equal(1_250, dataset.RowCount);
        Assert.Equal(350, await dataset.Scan<Tenanted>().Where(r => r.Tenant == "globex").CountAsync(Ct));
    }

    [Fact]
    public async Task TheNeighboursOfATextValueMergeAsANumbersDo()
    {
        // Rows of one tenant at a time, in key order, a few at an append: the small objects each tenant leaves merge.
        string[] tenants = ["acme", "globex", "initech"];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Tenanted.Schema, Options(ClusteringValue.Column), Ct);
        long id = 0;
        long appended = 0;
        foreach (string tenant in tenants)
        {
            for (int append = 0; append < 24; append++)
            {
                await AppendAsync(dataset, [.. Enumerable.Range(0, 60).Select(i => new Tenanted(tenant, id++, i))]);
                appended += 60;
                for (int step = 0; await dataset.CompactAsync(Compaction(), Ct) is not null; step++)
                {
                    Assert.True(step < 1_000, "compaction never runs out of jobs");
                }
            }
        }

        Dictionary<string, int> objects = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            if (held.Level > 0)
            {
                string tenant = Assert.Single(await TenantsAsync(dataset, held.Entry));
                objects[tenant] = objects.GetValueOrDefault(tenant) + 1;
            }
        }

        Console.Out.Write(FormattableString.Invariant(
            $"TEXT NEIGHBOURS: {appended} rows of {tenants.Length} tenants in appends of 60; objects above level 0 by tenant: {string.Join(", ", objects.Select(pair => $"{pair.Key} {pair.Value}"))}.\n"));
        Assert.Equal(appended, dataset.RowCount);
        Assert.All(objects, pair => Assert.True(pair.Value <= 6, $"{pair.Value} objects hold {pair.Key}'s 1440 rows"));
    }

    /// <summary>Holds every object's entry to its rows: its last key the largest key they hold, encoded as the key encodes it.</summary>
    private static async Task HoldTheirLastKeysAsync(VortexDataset dataset)
    {
        ClusteringKey key = dataset.Snapshot.Schema.Key!;
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
            List<Tenanted> rows = await lease.File.Scan<Tenanted>().ToRecordsAsync(Ct).ToListAsync(Ct);
            byte[] last = rows.Select(row => key.Encode([FilterLiteral.From(row.Tenant), FilterLiteral.From(row.Id)])).Max(ByteOrder.Instance)!;
            Assert.Equal(last, held.Entry.LastKey.ToArray());
        }
    }

    private static async Task<List<string>> TenantsAsync(VortexDataset dataset, ObjectEntry entry)
    {
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        return [.. (await lease.File.Scan<Tenanted>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(row => row.Tenant).Distinct()];
    }

    private static async Task AppendAsync(VortexDataset dataset, Tenanted[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Tenanted>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    private static DatasetOptions Options(ClusteringValue value) => new DatasetOptions
    {
        Seed = 0xB0_0D5,
        ClusteringKey = [nameof(Tenanted.Tenant), nameof(Tenanted.Id)],
        Clustering = new ClusteringOptions { Value = value },
        ChunkBytes = 0,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    private static CompactionOptions Compaction() => new CompactionOptions
    {
        LevelZeroCeiling = 2,
        TargetBytesAtLevelOne = 2 << 10,
        Fanout = 3,
        MaxObjectBytes = 1L << 30,
    };

    /// <summary>Byte strings in their bytewise order.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new ByteOrder();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long Id, double Amount);

    [VortexRecord]
    public partial record struct Numbered(long Group, long Id);
}
