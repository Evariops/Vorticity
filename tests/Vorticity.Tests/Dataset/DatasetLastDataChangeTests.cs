// The last version whose commit changed rows. Every operation of the dataset, one after the other,
// and what each one records: its own version when it changes rows or the schema, its parent's when
// it only moves them, as a compaction, an index fragment, the purge of deletion marks and a repack
// do (a rekey is checked with the encrypted dataset's tests). A commit that applies nothing creates
// no version, and a header written before the field reads as its own version.
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Protobuf;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
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
        // On the key: a Bloom on the four cities would be refused, below the distinct values it asks.
        IndexingResult indexed = await dataset.IndexAsync(IndexPolicy.None.Bloom("Key"), cancellationToken: ct);
        Assert.Equal((1L, 2), (indexed.ObjectsIndexed, indexed.Commits));
        Assert.Equal((6UL, 3UL), (dataset.Version, dataset.LastDataChange));

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
    public async Task ARepackMovesNoRowAndAReplacementAndAnImportChangeThem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions { Seed = 0x1A57_DA7A, ClusteringKey = ["Key"] };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        await AppendAsync(dataset, 0, ct);

        // The indexing leaves the first object's fragment in its commit object, which the next append
        // keeps referencing; the repack moves it into a commit of its own, and no row with it.
        IndexingResult indexed = await dataset.IndexAsync(IndexPolicy.None.Bloom("Key"), cancellationToken: ct);
        Assert.Equal(1, indexed.ObjectsIndexed);
        ulong fragments = dataset.Version;
        await AppendAsync(dataset, 1, ct);
        ulong appended = dataset.Version;
        (ulong repacked, OperationOutcome outcome) = await dataset.RepackAsync([fragments], ct);
        Assert.Equal(OperationOutcome.Applied, outcome);
        Assert.Equal((repacked, appended), (dataset.Version, dataset.LastDataChange));

        // A replacement the caller makes, and a file taken as an object, change rows.
        DataObject[] objects = [.. await dataset.ListObjectsAsync(ct).ToListAsync(ct)];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<ChangeRow>([new ChangeRow(5_000, 1.0, "Paris")], ct);
            ReplaceResult replaced = await dataset.ReplaceObjectsAsync(objects, [draft], ct);
            Assert.Equal((replaced.Version, replaced.Version), (dataset.Version, dataset.LastDataChange));
        }

        MemoryStream file = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(file), VortexTypes.ToDType(ChangeRow.Schema, new DTypeArena()), VortexWriteOptions.Default))
        {
            await writer.WriteAsync<ChangeRow>([new ChangeRow(6_000, 2.0, "Lyon")], ct);
            await writer.CompleteAsync(ct);
        }

        await store.PutIfAbsentAsync("imports/one.vortex", file.ToArray(), ct);
        ulong imported = await dataset.ImportAsync("imports/one.vortex", ct);
        Assert.Equal(imported, dataset.LastDataChange);
        Assert.Equal(2, (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count);
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
