// The dataset's indexer and its acceptance, progressive indexing: at every intermediate commit,
// answers equal those without indexes.
//
// THE DATASET IS CLUSTERED, SO EVERY OBJECT ALREADY HAS AN INDEX: the mandatory run on its key.
// The fragments add two more beside it -- a Bloom filter on `tag`, a sorted run on `id` -- which
// is why every run carries an origin: a fragment is read next to the file's own directory, never
// instead of it.
//
// EVERY OBJECT IS INDEXED IN TWO HALVES, ONE COMMIT EACH, and after every commit the same questions
// are asked with the indexes and without them, with the summaries and without them. The answers
// never move; what moves is what the objects can answer by an index, and the key-ordered read on
// `id` shows the coverage rule end to end: refused while any object's fragments leave a block
// uncovered, and exact once they all cover their objects.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetIndexingTests
{
    private const int Objects = 4;
    private const int PerObject = 2_048;
    private const int BlockRows = 256;
    private const int Rows = Objects * PerObject;

    private static readonly WritePolicy Policy = WritePolicy.None
        .For("tag", IndexSpec.Bloom(falsePositivePpm: 100))
        .For("id", IndexSpec.SortedRuns.AsRequired());

    // A budget that cannot refuse anything here, because none of these tests is about the budget:
    // the objects are a few kilobytes and their filters outweigh them, so at 1000 per mille the
    // verdict would fall on the indexes these tests need built. The verdict is reached before any
    // payload is written, so it judges even an object that flushes nothing, and the fixture has to
    // say what it means.
    private static readonly VortexWriteOptions Build = new VortexWriteOptions { IndexBudgetPerMille = 100_000 };

    private static long Id(long row) => (row * 7_919L) % 100_003;

    private static long Tag(long row) => (row % 500) * 3;

    [Fact]
    public async Task EveryIntermediateCommitAnswersAsWithoutIndexes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int i in (int[])[2, 0, 3, 1])
        {
            await dataset.AppendAsync(ObjectRows(types, schema,i), ct);
        }

        List<PositionedObject> objects = await ObjectsAsync(dataset);
        Assert.Equal(Objects, objects.Count);
        await AssertAnswersAsync(dataset);

        RowRange[] halves = [new RowRange(0, PerObject / 2), new RowRange(PerObject / 2, PerObject)];
        int commits = 0;
        foreach (PositionedObject target in objects)
        {
            foreach (RowRange half in halves)
            {
                // Before the last fragment lands, some object still has a block no run on `id`
                // covers: a key-ordered read on it is refused, never served short.
                await Assert.ThrowsAsync<VortexUnsupportedException>(
                    async () => await CountRowsAsync(dataset.ScanBuilder().InKeyOrder("id")));

                IndexingResult result = await DatasetIndexer.IndexAsync(dataset, target, Policy, half, Build, ct);
                Assert.Equal(OperationOutcome.Applied, result.Outcome);
                Assert.All(result.Reports, report => Assert.Equal(IndexOutcome.Built, report.Outcome));
                commits++;
                await AssertAnswersAsync(dataset);
            }
        }

        // Every object: its own run, and the two fragments' entries beside it, each joined across
        // its two halves, from origins 1 and 2.
        foreach (PositionedObject held in await ObjectsAsync(dataset))
        {
            Assert.Equal(2, held.Entry.Fragments.Count);
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, ct);
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(ct))!;
            Assert.All(lease.File.IndexFragmentRefusals, Assert.Null);
            Assert.Equal(3, directory.Entries.Count);
            foreach (IndexEntry entry in directory.Entries)
            {
                int origins = entry.Runs.Count;
                Assert.Equal(entry.Runs[0].Origin == 0 ? 1 : 2, origins);
            }

            // And the fragment's filter prunes inside the object: a tag no row holds.
            VortexExpr noTag = Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(1L)));
            ScanExplanation plan = await lease.File.ScanBuilder().Where(noTag).ExplainAsync(ct);
            Assert.Equal(0, plan.LiveBlocks);
        }

        // Covered everywhere: the key-ordered read on `id` is served, and it is the sorted column.
        List<long> ids = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("id").ExecuteAsync(ct))
        {
            ids.AddRange(batch.Column("id"u8).AsPrimitive<long>().Values.ToArray());
        }

        List<long> expected = [];
        for (long row = 0; row < Rows; row++)
        {
            expected.Add(Id(row));
        }

        expected.Sort();
        Assert.Equal(expected, ids);
        Assert.Equal(Objects * halves.Length, commits);
    }

    [Fact]
    public async Task ARebuildSwapsAnObjectsFragmentsForOneInOneCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A rebuild drops the entry's fragments and indexes again, in one commit. Three
        // partial fragments become one over the whole object, and no version in between holds the
        // object with neither.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        await dataset.AppendAsync(ObjectRows(types, schema, 0), ct);
        PositionedObject target = Assert.Single(await ObjectsAsync(dataset));
        for (int block = 0; block < 3; block++)
        {
            RowRange one = new RowRange(block * BlockRows, (block + 1) * BlockRows);
            Assert.Equal(OperationOutcome.Applied, (await DatasetIndexer.IndexAsync(dataset, target, Policy, one, Build, ct)).Outcome);
        }

        PositionedObject partial = Assert.Single(await ObjectsAsync(dataset));
        Assert.Equal(3, partial.Entry.Fragments.Count);
        ulong before = dataset.Version;

        IndexingResult rebuilt = await DatasetIndexer.RebuildAsync(dataset, partial, Policy, Build, ct);
        Assert.Equal(OperationOutcome.Applied, rebuilt.Outcome);
        Assert.Equal(before + 1, dataset.Version);
        PositionedObject whole = Assert.Single(await ObjectsAsync(dataset));
        PageReference fragment = Assert.Single(whole.Entry.Fragments);
        Assert.Equal(dataset.Version, fragment.Version);

        // The fragment covers every block now, so a key-ordered read on `id` is served, and the
        // answers are the same as without any index.
        Assert.Equal(PerObject, await CountRowsAsync(dataset.ScanBuilder().InKeyOrder("id")));
        await AssertAnswersAsync(dataset);
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task MoreThanKFragmentsAreBundledIntoOneAndAnswerTheSame()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // An entry with more than K fragments is compacted by merging them (index bytes only)
        // into one fragment in a new commit. Eight one-block fragments on one object, K = 4.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        await dataset.AppendAsync(ObjectRows(types, schema, 0), ct);
        PositionedObject target = Assert.Single(await ObjectsAsync(dataset));
        for (int block = 0; block < PerObject / BlockRows; block++)
        {
            RowRange one = new RowRange(block * BlockRows, (block + 1) * BlockRows);
            Assert.Equal(OperationOutcome.Applied, (await DatasetIndexer.IndexAsync(dataset, target, Policy, one, Build, ct)).Outcome);
        }

        List<long> ordered = await IdsInOrderAsync(dataset);
        await AssertAnswersAsync(dataset);

        CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: ct);
        Assert.Equal(1, plan.FragmentedObjects);
        Assert.Equal(CompactionTrigger.Fragments, plan.Job!.Trigger);

        CompactionResult bundled = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: ct));
        Assert.Equal(CompactionTrigger.Fragments, bundled.Trigger);
        Assert.Equal(OperationOutcome.Applied, bundled.Outcome);
        Assert.Equal(0, bundled.Rows);

        // One fragment, which carries the eight containers byte for byte: its size is theirs, plus
        // an alignment and a table.
        PositionedObject held = Assert.Single(await ObjectsAsync(dataset));
        PageReference bundle = Assert.Single(held.Entry.Fragments);
        ReadOnlyMemory<byte> bytes = await dataset.ReadFragmentAsync(bundle, ct);
        Assert.True(FragmentBundle.IsBundle(bytes.Span));
        Assert.Equal(8, FragmentBundle.Unpack(bytes).Count);
        Assert.InRange(bundled.BytesOut, bundled.BytesIn, bundled.BytesIn + (9 * 64) + (8 * 16) + 12);

        // Read as it was: every entry's eight runs, from the bundle's eight parts.
        await using (ObjectLease lease = await dataset.RentAsync(held.Entry, ct))
        {
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(ct))!;
            Assert.All(lease.File.IndexFragmentRefusals, Assert.Null);
            Assert.Equal(3, directory.Entries.Count);
            Assert.Equal((1, 8, 8), (directory.Entries[0].Runs.Count, directory.Entries[1].Runs.Count, directory.Entries[2].Runs.Count));
        }

        Assert.Equal(ordered, await IdsInOrderAsync(dataset));
        await AssertAnswersAsync(dataset);
        Assert.Null((await dataset.PlanCompactionAsync(cancellationToken: ct)).Job);

        // More fragments on top of the bundle, past K again: the next bundle flattens it.
        foreach (RowRange again in (RowRange[])[
            new RowRange(0, 2 * BlockRows), new RowRange(2 * BlockRows, 4 * BlockRows),
            new RowRange(4 * BlockRows, 6 * BlockRows), new RowRange(6 * BlockRows, PerObject)])
        {
            await DatasetIndexer.IndexAsync(dataset, target, WritePolicy.None.For("tag", IndexSpec.Bloom(falsePositivePpm: 1_000)), again, Build, ct);
        }

        Assert.NotNull(await dataset.CompactAsync(cancellationToken: ct));
        PageReference flat = Assert.Single(Assert.Single(await ObjectsAsync(dataset)).Entry.Fragments);
        Assert.Equal(12, FragmentBundle.Unpack(await dataset.ReadFragmentAsync(flat, ct)).Count);
        Assert.Equal(ordered, await IdsInOrderAsync(dataset));
        await AssertAnswersAsync(dataset);
    }

    [Fact]
    public async Task TwoIndexersOfOneRangeWriteOneFragment()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The rebase of a duplicate fragment, with the real indexer: the same rows under the same
        // policy are the same bytes, and the second commit finds them in the winner's leaf.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        await dataset.AppendAsync(ObjectRows(types, schema,0), ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, Clustered(), ct);

        PositionedObject first = Assert.Single(await ObjectsAsync(dataset));
        PositionedObject stale = Assert.Single(await ObjectsAsync(second));
        Assert.Equal(OperationOutcome.Applied, (await DatasetIndexer.IndexAsync(dataset, first, Policy, options: Build, cancellationToken: ct)).Outcome);
        Assert.Equal(OperationOutcome.AlreadyThere, (await DatasetIndexer.IndexAsync(second, stale, Policy, options: Build, cancellationToken: ct)).Outcome);

        await dataset.RefreshAsync(ct);
        Assert.Single(Assert.Single(await ObjectsAsync(dataset)).Entry.Fragments);
    }

    [Fact]
    public async Task AFragmentOfAnObjectACompactionReplacedIsDropped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A data compaction rewrites the object, embeds its index and drops every fragment; an
        // indexer that built against the old object loses.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int i in (int[])[1, 0])
        {
            await dataset.AppendAsync(ObjectRows(types, schema,i), ct);
        }

        PositionedObject indexed = (await ObjectsAsync(dataset))[0];
        Assert.Equal(OperationOutcome.Applied, (await DatasetIndexer.IndexAsync(dataset, indexed, Policy, options: Build, cancellationToken: ct)).Outcome);

        await using VortexDataset stale = await VortexDataset.OpenAsync(store, Clustered(), ct);
        PositionedObject late = (await ObjectsAsync(stale))[1];

        CompactionOptions compaction = new CompactionOptions
        {
            LevelZeroCeiling = 1,
            TargetBytesAtLevelOne = 1L << 30,
            MaxObjectBytes = 1L << 30,
        };
        Assert.NotNull(await dataset.CompactAsync(compaction, ct));
        PositionedObject output = Assert.Single(await ObjectsAsync(dataset));
        Assert.Empty(output.Entry.Fragments);

        IndexingResult dropped = await DatasetIndexer.IndexAsync(stale, late, Policy, options: Build, cancellationToken: ct);
        Assert.Equal(OperationOutcome.Dropped, dropped.Outcome);
        await dataset.RefreshAsync(ct);
        Assert.Empty(Assert.Single(await ObjectsAsync(dataset)).Entry.Fragments);
        await AssertAnswersAsync(dataset);
    }

    [Fact]
    public async Task AnObjectWithoutAnIdentityIsRefusedWithTheReason()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();

        // A file of another writer: this writer's shape with its identity entry renamed.
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, new VortexWriteOptions { RowBlockSize = BlockRows }))
        {
            await foreach (RecordBatch batch in ObjectRows(types, schema,0))
            {
                await writer.WriteAsync(batch, ct);
            }

            await writer.CompleteAsync(ct);
        }

        byte[] foreign = stream.ToArray();
        int key = foreign.AsSpan().LastIndexOf(FileIdentity.MetadataKeyUtf8);
        foreign[key + FileIdentity.MetadataKeyUtf8.Length - 1] = (byte)'Y';
        string objectKey = CommitKey.ForData("foreign");
        await store.PutIfAbsentAsync(objectKey, foreign, ct);

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Unclustered(), ct);
        await dataset.ImportAsync(objectKey, ct);
        PositionedObject imported = Assert.Single(await ObjectsAsync(dataset));
        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await DatasetIndexer.IndexAsync(dataset, imported, Policy, options: Build, cancellationToken: ct));
        Assert.Contains("store's token", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The same questions, with the indexes and the summaries and without either.</summary>
    private static async Task AssertAnswersAsync(VortexDataset dataset)
    {
        VortexExpr[] questions =
        [
            Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(Tag(17)))),
            Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(1L))),
            Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(4_321)))),
            Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(50_001L))),
            Expr.And(
                Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(10_000L))),
                Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(11_000L)))),
        ];

        foreach (VortexExpr question in questions)
        {
            List<long> plain = await KeysAsync(dataset.ScanBuilder().Where(question).WithIndexes(false).WithSummaries(false));
            Assert.Equal(plain, await KeysAsync(dataset.ScanBuilder().Where(question)));
            Assert.Equal(plain, await KeysAsync(dataset.ScanBuilder().Where(question).WithSummaries(false)));
            Assert.Equal(plain.Count, await dataset.ScanBuilder().Where(question).CountAsync());
        }
    }

    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            keys.AddRange(batch.Column("key"u8).AsPrimitive<long>().Values.ToArray());
        }

        return keys;
    }

    private static async Task<List<long>> IdsInOrderAsync(VortexDataset dataset)
    {
        List<long> ids = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("id").ExecuteAsync())
        {
            ids.AddRange(batch.Column("id"u8).AsPrimitive<long>().Values.ToArray());
        }

        return ids;
    }

    private static async Task<long> CountRowsAsync(DatasetScanBuilder scan)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<List<PositionedObject>> ObjectsAsync(VortexDataset dataset)
    {
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync())
        {
            objects.Add(held);
        }

        return objects;
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "tag", "id"],
        [
            types.Primitive(PType.I64, Nullability.NonNullable),
            types.Primitive(PType.I64, Nullability.NonNullable),
            types.Primitive(PType.I64, Nullability.NonNullable),
        ],
        Nullability.NonNullable);

    private static DatasetOptions Clustered() => Unclustered() with { ClusteringKey = ["key"] };

    private static DatasetOptions Unclustered() => new DatasetOptions
    {
        Seed = 0x1D_E7E5,
        Write = new VortexWriteOptions { RowBlockSize = BlockRows, WritePolicy = WritePolicy.None },
    };

    /// <summary>Object <paramref name="i"/>: keys <c>[i · 2048, (i + 1) · 2048)</c>, in one batch.</summary>
    private static async IAsyncEnumerable<RecordBatch> ObjectRows(DTypeArena types, DType schema, int i)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(PerObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer tags = arena.Allocate(PerObject * sizeof(long), sizeof(long), out Span<byte> tagBytes);
        VortexBuffer ids = arena.Allocate(PerObject * sizeof(long), sizeof(long), out Span<byte> idBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<long> tagValues = MemoryMarshal.Cast<byte, long>(tagBytes);
        Span<long> idValues = MemoryMarshal.Cast<byte, long>(idBytes);
        for (int row = 0; row < PerObject; row++)
        {
            long at = ((long)i * PerObject) + row;
            keyValues[row] = at;
            tagValues[row] = Tag(at);
            idValues[row] = Id(at);
        }

        int keyNode = arena.AddPrimitive(i64, PerObject, Validity.NonNullable, PType.I64, keys);
        int tagNode = arena.AddPrimitive(i64, PerObject, Validity.NonNullable, PType.I64, tags);
        int idNode = arena.AddPrimitive(i64, PerObject, Validity.NonNullable, PType.I64, ids);
        int root = arena.AddStruct(schema, PerObject, Validity.NonNullable, [keyNode, tagNode, idNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        yield return batch;
        await Task.CompletedTask;
    }
}
