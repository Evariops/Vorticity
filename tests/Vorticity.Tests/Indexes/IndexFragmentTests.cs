// Index fragments attached to a file: an index built over a block range after the write.
//
// WHAT IS HELD. A fragment over a block range prunes those blocks and no other, as a pruner uses
// the blocks it covers, and a key source refuses it until fragments cover the file, as an exact
// source waits for full coverage. A fragment is read BESIDE the file's own index, from its own
// bytes and its own encoding table. Two fragments of one entry join when their blocks are disjoint;
// an entry the file already has, a fragment of another file, and a fragment overlapping another's
// blocks are each left out with the reason, and the scan answers the same.
//
// THE DATA MAKES THE INDEX THE ONLY THING THAT PRUNES. The keys of a block are spread over the whole
// domain -- row r holds (r * 7919) mod 100003 -- so a zone map proves nothing, and every block a plan
// reports pruned was pruned by the index under test. And the answers are always compared with the
// same scan over the same file with no index at all.
//
// AND IT CATCHES THE TWO TRAPS NOBODY WOULD SEE. A range fed to builders that were not told where it
// starts writes runs whose blocks contradict their `FirstBlock`: the values of the range are then
// looked up in the wrong blocks, which the lookups of present keys below would lose. And two
// fragments' first segments sit at the same offset -- each counts from its own magic -- so a run
// cache keyed by the offset alone would hand the second fragment's run the first one's keys, which
// the walk across both would show.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class IndexFragmentTests
{
    private const int Rows = 20_000;
    private const int BlockRows = 1_024;
    private const int Blocks = (Rows + BlockRows - 1) / BlockRows;

    /// <summary>Blocks 4 to 11: a range inside the file, touching neither end.</summary>
    private static readonly RowRange Middle = new RowRange(4 * BlockRows, 12 * BlockRows);

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["id", "tag"],
        [Types.Primitive(PType.I64, Nullability.NonNullable), Types.Primitive(PType.I64, Nullability.NonNullable)],
        Nullability.NonNullable);

    /// <summary>A budget no index of these tests can exceed: the budget is not what is under test.</summary>
    private static readonly VortexWriteOptions Build = new VortexWriteOptions { IndexBudgetPerMille = 1_000 };

    private static long Id(int row) => (row * 7_919L) % 100_003;

    private static long Tag(int row) => (row % 1_000) * 3;

    [Fact]
    public async Task AFragmentOverABlockRangePrunesItsBlocksAndNoOther()
    {
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        IndexFragment fragment = await FragmentAsync(data, Runs("id"), Middle);
        Assert.Equal(IndexOutcome.Built, Assert.Single(fragment.Reports).Outcome);

        await using VortexFile file = await OpenAsync(data, fragment.Bytes);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync();
        Assert.True(directory is not null, string.Join("; ", file.IndexFragmentRefusals));
        Assert.Null(Assert.Single(file.IndexFragmentRefusals));
        IndexRun run = Assert.Single(Assert.Single(directory!.Entries).Runs);
        Assert.Equal((4UL, 8U, 1), (run.FirstBlock, run.BlockCount, run.Origin));

        // A key no row holds: the eight blocks of the range are proven empty, and only them.
        VortexExpr absent = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(AbsentId())));
        ScanPlan plan = await file.Scan().Where(absent).ExplainAsync();
        Assert.Equal(0, Pruned(plan, "zone map"));
        Assert.Equal(8, Pruned(plan, "locating index"));
        Assert.Equal(Blocks - 8, plan.LiveBlocks);
        Assert.Equal(0, await file.Scan().Where(absent).CountAsync());

        // Keys the range holds, found where they are: builders fed a range without its first block
        // would have filed them under the wrong blocks, and these lookups would come back empty.
        await using VortexFile plain = await OpenAsync(data);
        for (int row = (int)Middle.Start; row < Middle.End; row += 509)
        {
            VortexExpr present = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(row))));
            Assert.Equal(1, await file.Scan().Where(present).CountAsync());
            Assert.Equal(
                await CountAsync(plain.Scan().Where(present).WithIndexes(false)),
                await CountAsync(file.Scan().Where(present)));
        }
    }

    [Fact]
    public async Task ABloomFragmentOverABlockRangeFindsEveryKeyOfItsRange()
    {
        // The filters of a range are numbered from the range's first block. A Bloom tree has no
        // check that would notice otherwise: every key of the range, looked up, must be found.
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        IndexFragment fragment = await FragmentAsync(
            data, WritePolicy.None.For("id", IndexPolicy.Bloom(falsePositivePpm: 100)), Middle);

        await using VortexFile file = await OpenAsync(data, fragment.Bytes);
        IndexRun run = Assert.Single(Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries).Runs);
        Assert.Equal((4UL, 8U), (run.FirstBlock, run.BlockCount));

        for (int row = (int)Middle.Start; row < Middle.End; row += 97)
        {
            VortexExpr present = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(row))));
            Assert.Equal(1, await file.Scan().Where(present).CountAsync());
        }

        VortexExpr absent = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(AbsentId())));
        Assert.Equal(8, Pruned(await file.Scan().Where(absent).ExplainAsync(), "bloom filter"));
    }

    [Fact]
    public async Task AFragmentIsReadBesideTheFilesOwnIndex()
    {
        // The file indexes `id` itself; a fragment adds a Bloom filter on `tag`. Both answer, each
        // from its own bytes and its own encoding table.
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), Runs("id"));
        IndexFragment fragment = await FragmentAsync(
            data, WritePolicy.None.For("tag", IndexPolicy.Bloom(falsePositivePpm: 100)), new RowRange(0, Rows));

        await using VortexFile file = await OpenAsync(data, fragment.Bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
        Assert.Null(Assert.Single(file.IndexFragmentRefusals));
        Assert.Equal(2, directory.Entries.Count);
        Assert.Equal(0, directory.Entries[0].Runs[0].Origin);
        Assert.Equal(1, directory.Entries[1].Runs[0].Origin);

        // The fragment's filter proves a tag no row holds absent from every block the zone map could
        // not: the last block's tags start at 1 368, so the zone map takes it first, and the filter
        // takes the nineteen others.
        VortexExpr noTag = Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(1L)));
        ScanPlan plan = await file.Scan().Where(noTag).ExplainAsync();
        Assert.Equal(1, Pruned(plan, "zone map"));
        Assert.Equal(Blocks - 1, Pruned(plan, "bloom filter"));
        Assert.Equal(0, plan.LiveBlocks);
        Assert.Equal(0, await file.Scan().Where(noTag).CountAsync());

        // The file's own run still walks every key, in order.
        List<long> expected = [];
        for (int row = 0; row < Rows; row++)
        {
            expected.Add(Id(row));
        }

        expected.Sort();
        Assert.Equal(expected, await KeysAsync(file, "id"));

        // And every answer is the one the file gives without any index.
        await using VortexFile plain = await OpenAsync(data);
        VortexExpr someTags = Expr.Or(
            Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(Tag(17)))),
            Expr.Eq(Expr.Field("tag"), Expr.Literal(FilterLiteral.From(1L))));
        Assert.Equal(
            await CountAsync(plain.Scan().Where(someTags).WithIndexes(false)),
            await CountAsync(file.Scan().Where(someTags)));
    }

    [Fact]
    public async Task AKeySourceWaitsUntilFragmentsCoverTheFile()
    {
        // An exact source waits for full coverage. One fragment over the first half is a
        // pruner's and not a cursor's; with the second half the two join into one entry, and the
        // walk crosses from one fragment's run into the other's.
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        RowRange first = new RowRange(0, 10 * BlockRows);
        RowRange second = new RowRange(10 * BlockRows, Rows);
        IndexFragment head = await FragmentAsync(data, Runs("id"), first);
        IndexFragment tail = await FragmentAsync(data, Runs("id"), second);

        await using (VortexFile half = await OpenAsync(data, head.Bytes))
        {
            VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
                async () => await half.Keys("id").OpenAsync());
            Assert.Contains("cover 10 of the file's 20 blocks", refused.Message, StringComparison.Ordinal);
        }

        await using VortexFile file = await OpenAsync(data, head.Bytes, tail.Bytes);
        IndexEntry entry = Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries);
        Assert.Equal(2, entry.Runs.Count);
        Assert.Equal((0UL, 1), (entry.Runs[0].FirstBlock, entry.Runs[0].Origin));
        Assert.Equal((10UL, 2), (entry.Runs[1].FirstBlock, entry.Runs[1].Origin));

        // The two fragments' first key segments lie at the same offset of their own bytes: a cache
        // keyed by the offset alone would answer the second run with the first one's keys.
        Assert.Equal(entry.Runs[0].Payload[0].Offset, entry.Runs[1].Payload[0].Offset);

        List<long> expected = [];
        for (int row = 0; row < Rows; row++)
        {
            expected.Add(Id(row));
        }

        expected.Sort();
        Assert.Equal(expected, await KeysAsync(file, "id"));

        List<long> ordered = [];
        await foreach (RecordBatch batch in file.Scan().InKeyOrder("id").ExecuteAsync())
        {
            ordered.AddRange(batch.Column("id"u8).AsPrimitive<long>().Values.ToArray());
        }

        Assert.Equal(expected, ordered);
    }

    [Fact]
    public async Task AFragmentOfAnotherVersionOfTheFileIsRefusedWithItsReason()
    {
        // The same rows written twice are two versions: a fragment of one is not the other's.
        Decoders.EnsureRegistered();
        byte[] indexed = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        byte[] other = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        Assert.Equal(indexed.Length, other.Length);
        IndexFragment fragment = await FragmentAsync(indexed, Runs("id"), new RowRange(0, Rows));

        await using VortexFile file = await OpenAsync(other, fragment.Bytes);
        Assert.Null(await file.ReadIndexDirectoryAsync());
        string refusal = Assert.Single(file.IndexFragmentRefusals)!;
        Assert.Contains("the fragment indexes the version", refusal, StringComparison.Ordinal);
        Assert.Contains("stale", refusal, StringComparison.Ordinal);

        // A hint refused is a scan without the hint.
        VortexExpr present = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(7))));
        Assert.Equal(1, await file.Scan().Where(present).CountAsync());
    }

    [Fact]
    public async Task AnEntryTheFileAlreadyHasStaysTheFiles()
    {
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), Runs("id"));
        IndexFragment fragment = await FragmentAsync(data, Runs("id"), Middle);

        await using VortexFile file = await OpenAsync(data, fragment.Bytes);
        IndexEntry entry = Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries);
        Assert.All(entry.Runs, run => Assert.Equal(0, run.Origin));
        Assert.Contains("is the file's own already", Assert.Single(file.IndexFragmentRefusals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlappingFragmentsKeepTheFirstAndSayWhy()
    {
        // Two opinions on the same blocks: a key walk would meet their keys twice.
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        IndexFragment early = await FragmentAsync(data, Runs("id"), new RowRange(0, 12 * BlockRows));
        IndexFragment late = await FragmentAsync(data, Runs("id"), new RowRange(8 * BlockRows, Rows));

        await using VortexFile file = await OpenAsync(data, early.Bytes, late.Bytes);
        IndexEntry entry = Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries);
        IndexRun run = Assert.Single(entry.Runs);
        Assert.Equal((0UL, 12U, 1), (run.FirstBlock, run.BlockCount, run.Origin));
        Assert.Null(file.IndexFragmentRefusals[0]);
        Assert.Contains("covers blocks another fragment covers", file.IndexFragmentRefusals[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFragmentIsWholeBlocksBoundToItsFile()
    {
        Decoders.EnsureRegistered();
        byte[] data = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        await using (VortexFile file = await OpenAsync(data))
        {
            // A block cut in two would be claimed by two fragments or by none.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await VortexFileIndexer.BuildFragmentAsync(file, Runs("id"), new RowRange(100, 5_000)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await VortexFileIndexer.BuildFragmentAsync(file, Runs("id"), new RowRange(0, Rows + BlockRows)));

            // The end of the file ends a block, whatever its length.
            IndexFragment last = await VortexFileIndexer.BuildFragmentAsync(
                file, Runs("id"), new RowRange(19 * BlockRows, Rows), options: Build);
            Assert.Equal(IndexOutcome.Built, Assert.Single(last.Reports).Outcome);
        }

        // A file without an identity -- this writer's shape with its identity entry renamed, which
        // to a reader is a file from another writer -- has nothing to bind a fragment to unless the
        // store names it.
        byte[] anonymous = await WriteAsync(Guid.NewGuid(), WritePolicy.None);
        int key = anonymous.AsSpan().LastIndexOf(FileIdentity.MetadataKeyUtf8);
        Assert.True(key > 0);
        anonymous[key + FileIdentity.MetadataKeyUtf8.Length - 1] = (byte)'Y';
        await using VortexFile unbound = await OpenAsync(anonymous);
        Assert.Null(unbound.Identity);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await VortexFileIndexer.BuildFragmentAsync(unbound, Runs("id"), new RowRange(0, Rows)));

        // With a token it is built; a reader that has no token of its own to compare -- one given
        // bytes rather than a path -- refuses it and says so.
        IndexFragment tokened = await VortexFileIndexer.BuildFragmentAsync(
            unbound, Runs("id"), new RowRange(0, Rows), storeToken: "store:v1", options: Build);
        await using VortexFile reader = await OpenAsync(anonymous, tokened.Bytes);
        Assert.Null(await reader.ReadIndexDirectoryAsync());
        Assert.Contains("store token", Assert.Single(reader.IndexFragmentRefusals), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sorted run on a column, required: a run weighs about what its column weighs, and the
    /// budget would otherwise abandon it at any size.
    /// </summary>
    private static WritePolicy Runs(string column) =>
        WritePolicy.None.For(column, IndexPolicy.SortedRuns.AsRequired());

    /// <summary>
    /// A key no row holds that every block's bounds still admit, so a zone map proves nothing and
    /// whatever prunes it is the index.
    /// </summary>
    private static long AbsentId()
    {
        HashSet<long> held = [];
        for (int row = 0; row < Rows; row++)
        {
            held.Add(Id(row));
        }

        long id = 50_000;
        while (held.Contains(id))
        {
            id++;
        }

        return id;
    }

    private static int Pruned(ScanPlan plan, string structure)
    {
        foreach (PruningStep step in plan.Pruning)
        {
            if (step.Structure == structure)
            {
                return step.BlocksPruned;
            }
        }

        return 0;
    }

    private static async Task<long> CountAsync(ScanBuilder scan)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<List<long>> KeysAsync(VortexFile file, string column)
    {
        List<long> keys = [];
        await using KeyCursor cursor = await file.Keys(column).OpenAsync();
        bool any = await cursor.SeekFirstAsync();
        while (any)
        {
            keys.Add(cursor.Key.SignedValue);
            any = await cursor.NextAsync();
        }

        return keys;
    }

    /// <summary>A fragment of the file in <paramref name="data"/>, built from a copy opened on its own.</summary>
    private static async Task<IndexFragment> FragmentAsync(byte[] data, WritePolicy policy, RowRange rows)
    {
        await using VortexFile file = await OpenAsync(data);
        return await VortexFileIndexer.BuildFragmentAsync(file, policy, rows, options: Build);
    }

    private static ValueTask<VortexFile> OpenAsync(byte[] data, params ReadOnlyMemory<byte>[] fragments) =>
        VortexFile.OpenAsync(
            new MemorySegmentSource(data),
            new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = fragments } },
            default);

    private static async Task<byte[]> WriteAsync(Guid? identity, WritePolicy indexes)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = BlockRows,
            Indexes = indexes,
            Identity = identity,
            IndexBudgetPerMille = 1_000,
        };
        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, options))
        {
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer ids = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> idBytes);
                VortexBuffer tags = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> tagBytes);
                Span<long> idValues = MemoryMarshal.Cast<byte, long>(idBytes);
                Span<long> tagValues = MemoryMarshal.Cast<byte, long>(tagBytes);
                for (int i = 0; i < Rows; i++)
                {
                    idValues[i] = Id(i);
                    tagValues[i] = Tag(i);
                }

                int idNode = arena.AddPrimitive(Schema.GetField(0), Rows, Validity.NonNullable, PType.I64, ids);
                int tagNode = arena.AddPrimitive(Schema.GetField(1), Rows, Validity.NonNullable, PType.I64, tags);
                using RecordBatch batch = new RecordBatch(
                    arena, arena.AddStruct(Schema, Rows, Validity.NonNullable, [idNode, tagNode]), 0);
                await writer.WriteAsync(batch);
                await writer.CompleteAsync();
            }
            finally
            {
                arena.Reset();
            }
        }

        return stream.ToArray();
    }
}
