// The last version whose commit changed rows. Every operation of the dataset, one after the other,
// and what each one records: its own version when it changes rows or the schema, its parent's when
// it only moves them, as a compaction, an index fragment and the purge of deletion marks do. A
// commit that applies nothing creates no version, and a header written before the field reads as
// its own version.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Serialization.Protobuf;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetLastDataChangeTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public async Task EachCommitRecordsTheLastVersionThatChangedRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0x1A57_DA7A,
            ClusteringKey = ["Key"],
            MarkedObjectBytes = 0,
            MarkedShare = 1,
            Write = new VortexWriteOptions { RowBlockSize = 64 },
        };

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        Assert.Equal(1UL, dataset.LastDataChange);

        // Rows in: each records itself.
        await AppendAsync(dataset, 0, ct);
        Assert.Equal((2UL, 2UL), (dataset.Version, dataset.LastDataChange));
        await AppendAsync(dataset, 1, ct);
        Assert.Equal(3UL, dataset.LastDataChange);

        // Rows moved: a compaction, then the indexes and their fragments, record their parent's.
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, ct));
        Assert.Equal((4UL, 3UL), (dataset.Version, dataset.LastDataChange));
        await dataset.IndexAsync(IndexPolicy.None.Bloom("City"), cancellationToken: ct);
        Assert.True(dataset.Version > 4);
        Assert.Equal(3UL, dataset.LastDataChange);

        // A delete marks most of an object's rows: rows changed. The compaction that purges the marks
        // moves them.
        ulong deleted = (await dataset.DeleteAsync<ChangeRow>(r => r.Key < 1_030, ct)).Version;
        Assert.Equal(deleted, dataset.LastDataChange);
        CompactionPlan purge = await dataset.PlanCompactionAsync(new CompactionOptions { LevelZeroCeiling = 8 }, ct);
        Assert.Equal(CompactionTrigger.Marks, purge.Job?.Trigger);
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 8 }, ct));
        Assert.True(dataset.Version > deleted);
        Assert.Equal(deleted, dataset.LastDataChange);

        // A delete that matches nothing commits nothing, and an update records itself.
        ulong before = dataset.Version;
        await dataset.DeleteAsync<ChangeRow>(r => r.Key > 1_000_000, ct);
        Assert.Equal((before, deleted), (dataset.Version, dataset.LastDataChange));
        ulong updated = (await dataset.UpdateAsync<ChangeRow>(r => r.Key == 1_040, row => row with { City = "Brest" }, ct)).Version;
        Assert.Equal(updated, dataset.LastDataChange);

        // Objects removed by the caller, and a change of schema.
        DataObject first = await dataset.ListObjectsAsync(ct).FirstAsync(ct);
        ulong removed = (await dataset.RemoveObjectsAsync([first], ct)).Version;
        Assert.Equal(removed, dataset.LastDataChange);
        VortexSchema widened = VortexSchema.Create([.. ChangeRow.Schema.FieldArray, new VortexField("Note", VortexType.Utf8.Nullable)]);
        ulong evolved = await dataset.EvolveSchemaAsync(widened, cancellationToken: ct);
        Assert.Equal(evolved, dataset.LastDataChange);

        // Every handle reads what the version records.
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, options, ct);
        Assert.Equal(evolved, reader.LastDataChange);
    }

    [Fact]
    public void AHeaderWrittenBeforeTheFieldReadsAsItsOwnVersion()
    {
        CommitHeader old = new CommitHeader { Version = 42 };
        Assert.Equal(42UL, DatasetCommitter.LastDataChangeOf(old, 42));
        Assert.Equal(17UL, DatasetCommitter.LastDataChangeOf(old with { LastDataChange = 17 }, 42));

        // Written and read back.
        ProtoWriter writer = new ProtoWriter();
        try
        {
            (old with { LastDataChange = 17 }).Write(ref writer);
            Assert.Equal(17UL, CommitHeader.Read(writer.WrittenSpan).LastDataChange);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static async Task AppendAsync(VortexDataset dataset, int append, CancellationToken ct)
    {
        ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow((append * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        await dataset.AppendAsync(draft, ct);
    }
}
