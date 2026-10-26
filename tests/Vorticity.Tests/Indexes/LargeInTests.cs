// An IN of many values against the indexes that answer it. A Bloom filter rules a node out only
// when it refuses every value, so the price of asking grows with the values while what it can rule
// out does not: the pruner asks while the blocks it rules out pay for the probes, and stops where they
// do not. A sorted run is walked with the sorted values, each segment asked about the values of its
// own range, and what the filter asks of the column is prepared once for every file it is asked of.
// Asking less only leaves blocks live, so the rows never change.
//
// THE FIXTURES DEFEAT THE ZONE MAPS. 256 blocks of 64 rows whose key holds a value of its own on
// every row, scattered over the whole range, under one index: Bloom filters of three resolutions at
// 1 %, or sorted runs whose segment table is inline, in pages, or in pages several levels deep.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Tests.Dataset;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class LargeInTests
{
    private const int Block = 64;
    private const int Blocks = 256;
    private const int Rows = Block * Blocks;

    /// <summary>The index of a fixture, and for sorted runs the shape of their segment table.</summary>
    public enum Index
    {
        /// <summary>Bloom filters of three resolutions.</summary>
        Bloom,

        /// <summary>Sorted runs of 512 entries a segment: 32 segments, inline.</summary>
        Inline,

        /// <summary>Sorted runs of 16 entries a segment: 1,024 segments, in pages.</summary>
        Paged,

        /// <summary>Sorted runs of 16 entries a segment, under pages of 512 bytes: several levels.</summary>
        Deep,
    }

    /// <summary>Row <paramref name="row"/>'s key: a multiplication by an odd number, one to one on 32 bits.</summary>
    private static long Key(long row) => (row * 2_654_435_761L) & 0xFFFF_FFFFL;

    /// <summary>The same key on 31 bits, for a column of Int32.</summary>
    private static int NarrowKey(long row) => (int)(Key(row) >> 1);

    [Fact]
    public async Task AnInThatEveryBlockHoldsStopsAskingTheBloomFiltersBeforeTheLeaves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile file = await VortexFile.OpenAsync(await PathAsync(Index.Bloom), ct);

        // Four values in every block, in row order: no filter can rule a block out, and asking every
        // one reads every leaf to learn nothing.
        long[] values = [.. Enumerable.Range(0, 4 * Blocks).Select(i => Key((long)i * (Block / 4)))];
        (int live, int segments) = await BloomAsync(file, values, askAll: false, ct);
        (int liveAll, int segmentsAll) = await BloomAsync(file, values, askAll: true, ct);

        Assert.Equal((Blocks, Blocks), (live, liveAll));
        Assert.True(segments < segmentsAll, $"{segments} segments read against {segmentsAll} asking every filter");
    }

    [Fact]
    public async Task AnInOfAFewValuesRulesOutWhatAskingEveryBloomFilterRulesOut()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile file = await VortexFile.OpenAsync(await PathAsync(Index.Bloom), ct);

        // Eight values, each in its own block: a filter that refuses them pays for itself many times.
        long[] values = [.. Enumerable.Range(0, 8).Select(i => Key(((long)i * 32 * Block) + 5))];
        (int live, int segments) = await BloomAsync(file, values, askAll: false, ct);
        (int liveAll, int segmentsAll) = await BloomAsync(file, values, askAll: true, ct);

        Assert.Equal((liveAll, segmentsAll), (live, segments));
        Assert.InRange(live, 8, 8 + 4);
    }

    [Theory]
    [InlineData(Index.Inline)]
    [InlineData(Index.Paged)]
    [InlineData(Index.Deep)]
    public async Task ASortedRunLeavesLiveExactlyTheBlocksThatHoldAValue(Index index)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile file = await VortexFile.OpenAsync(await PathAsync(index), ct);
        foreach (long[] values in Sets())
        {
            HashSet<long> wanted = [.. values];
            int holding = Enumerable.Range(0, Blocks).Count(block =>
                Enumerable.Range(block * Block, Block).Any(row => wanted.Contains(Key(row))));
            Assert.Equal(holding, await LocateAsync(file, Membership(values), ct));
        }
    }

    [Fact]
    public async Task OneFilterAskedOfTwoColumnLayoutsAnswersEachInItsOwn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile wide = await VortexFile.OpenAsync(await PathAsync(Index.Paged), ct);
        await using VortexFile narrow = await VortexFile.OpenAsync(await PathAsync(Index.Paged, PType.I32), ct);

        // One node, asked of an Int64 column and of an Int32 one: each column encodes the same values
        // to keys of its own width, which the other's runs could not be searched with.
        long[] values = [.. Enumerable.Range(0, 2_000).Select(i => i % 2 == 0 ? Key(i * 3L) >> 1 : NarrowKey(i * 5L))];
        VortexExpr filter = Membership(values);
        HashSet<long> wanted = [.. values];
        foreach ((VortexFile file, Func<long, long> key) in ((VortexFile, Func<long, long>)[])[(wide, Key), (narrow, row => NarrowKey(row)), (wide, Key)])
        {
            int holding = Enumerable.Range(0, Blocks).Count(block =>
                Enumerable.Range(block * Block, Block).Any(row => wanted.Contains(key(row))));
            Assert.Equal(holding, await LocateAsync(file, filter, ct));
        }
    }

    [Theory]
    [InlineData(Index.Bloom, 1)]
    [InlineData(Index.Bloom, 0)]
    [InlineData(Index.Paged, 1)]
    [InlineData(Index.Paged, 0)]
    [InlineData(Index.Deep, 0)]
    public async Task AnInOfAnySizeKeepsTheSameRowsWithTheIndexesOnAndOff(Index index, int lanes)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await PathAsync(index);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes > 0 ? lanes : Environment.ProcessorCount);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: ct);
        foreach (long[] values in Sets())
        {
            HashSet<long> wanted = [.. values];
            long expected = Enumerable.Range(0, Rows).Count(row => wanted.Contains(Key(row)));
            VortexExpr filter = VortexExpr.In("key", values);
            Assert.Equal(expected, await file.Scan("key").Where(filter).CountAsync(ct));
            Assert.Equal(expected, await file.Scan("key").Where(filter).With(new ScanOptions { UseIndexes = false }).CountAsync(ct));
            Assert.Equal(expected, await file.Scan("key").Where(filter).With(new ScanOptions { UseIndexes = false, UseStatistics = false }).CountAsync(ct));

            List<long> kept = [];
            await foreach (BatchView batch in file.Scan("key").Where(filter).WithCancellation(ct))
            {
                Column<long> keys = batch.Column<long>("key");
                for (int i = 0; i < keys.Length; i++)
                {
                    kept.Add(keys[i]);
                }
            }

            Assert.Equal(expected, kept.Count);
            Assert.All(kept, key => Assert.Contains(key, wanted));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task AnInAskedOfEveryObjectOfADatasetKeepsTheSameRowsWithTheIndexesOnAndOff(int lanes)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0x1A_26E1,
            Write = new VortexWriteOptions
            {
                RowBlockSize = Block,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.None.For("Key", IndexSpec.SortedRuns.WithSegmentEntries(16)),
            },
        };

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyOnly.Schema, options, ct);
        const int Objects = 8;
        for (int part = 0; part < Objects; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<KeyOnly>([.. Enumerable.Range(part * 2_048, 2_048).Select(row => new KeyOnly(Key(row)))], ct);
            await dataset.AppendAsync(draft, ct);
        }

        // The objects' scans run on the session's lanes and share what the filter asks of the column.
        await using VortexSession session = VortexSession.Create(o => o.MaxDegreeOfParallelism = lanes > 0 ? lanes : Environment.ProcessorCount);
        await using VortexDataset opened = await VortexDataset.OpenAsync(store, options with { Session = session }, ct);
        foreach (long[] values in Sets())
        {
            HashSet<long> wanted = [.. values];
            long expected = Enumerable.Range(0, Objects * 2_048).Count(row => wanted.Contains(Key(row)));
            VortexExpr filter = VortexExpr.In("Key", values);
            Assert.Equal(expected, await opened.Scan("Key").Where(filter).CountAsync(ct));
            Assert.Equal(expected, await opened.Scan("Key").Where(filter).With(new ScanOptions { UseIndexes = false }).CountAsync(ct));
        }
    }

    /// <summary>Sets of 1 to 20,000 values, half held by a row of the fixtures and half outside their keys.</summary>
    private static IEnumerable<long[]> Sets()
    {
        foreach (int count in (int[])[1, 64, 1_024, 20_000])
        {
            yield return [.. Enumerable.Range(0, count).Select(i => i % 2 == 0 ? Key((long)i * 7 % Rows) : (1L << 33) + i)];
        }
    }

    private static VortexExpr Membership(long[] values) =>
        Expr.In(Expr.Field("key"), [.. values.Select(FilterLiteral.From)]);

    /// <summary>The blocks the Bloom filters leave live under <paramref name="values"/>, and the index segments they read.</summary>
    private static async Task<(int Live, int Segments)> BloomAsync(VortexFile file, long[] values, bool askAll, CancellationToken ct)
    {
        BloomPruner pruner = Assert.IsType<BloomPruner>(await BloomPruner.BuildAsync(file, Membership(values), Block, ct));
        if (askAll)
        {
            pruner.BlockWorth = long.MaxValue;
        }

        BlockMask live = new BlockMask(file.RowCount, Block);
        await pruner.RefineAsync(file, live, ct);
        return (live.LiveCount, pruner.Segments);
    }

    /// <summary>The blocks the sorted runs leave live under <paramref name="filter"/>.</summary>
    private static async Task<int> LocateAsync(VortexFile file, VortexExpr filter, CancellationToken ct)
    {
        KeyIndexPruner pruner = Assert.IsType<KeyIndexPruner>(await KeyIndexPruner.BuildAsync(file, filter, Block, ct));
        BlockMask live = new BlockMask(file.RowCount, Block);
        await pruner.RefineAsync(file, live, ct);
        return live.LiveCount;
    }

    private static Task<string> PathAsync(Index index, PType width = PType.I64) => SharedFiles.GetAsync(
        $"{nameof(LargeInTests)}/{index}/{width}",
        async path =>
        {
            Decoders.EnsureRegistered();
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType keyType = types.Primitive(width, Nullability.NonNullable);
            DType schema = types.Struct(["key"], [keyType], Nullability.NonNullable);
            int column = width == PType.I64
                ? Fixed(arena, keyType, PType.I64, Key)
                : Fixed(arena, keyType, PType.I32, NarrowKey);
            int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.None.For("key", index switch
                {
                    Index.Bloom => IndexSpec.Bloom(resolutions: 3),
                    Index.Inline => IndexSpec.SortedRuns.WithSegmentEntries(512),
                    _ => IndexSpec.SortedRuns.WithSegmentEntries(16),
                }),
                Fences = index == Index.Deep ? new FenceShape(InlineFences: 4, PageBytes: 512) : FenceShape.Default,
            };

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        });

    private static int Fixed<T>(CanonicalArena arena, DType dtype, PType ptype, Func<long, T> value)
        where T : unmanaged
    {
        VortexBuffer buffer = arena.Allocate(Rows * System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), out Span<byte> bytes);
        Span<T> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = value(row);
        }

        return arena.AddPrimitive(dtype, Rows, Validity.NonNullable, ptype, buffer);
    }
}
