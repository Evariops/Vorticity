// An IN of many values against Bloom filters. A node is ruled out only when its filter refuses every
// value, so the price of asking grows with the values while what a filter can rule out does not:
// the pruner asks while the blocks it rules out pay for the probes, and stops where they do not.
// Asking less only leaves blocks live, so the rows never change.
//
// THE FIXTURE DEFEATS THE ZONE MAPS. 256 blocks of 64 rows whose Int64 key holds a value of its
// own on every row, scattered over the whole range, under a Bloom index of three resolutions at
// 1 %: a filter per block, one per sixteen blocks, and the file's.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class BloomLargeInTests
{
    private const int Block = 64;
    private const int Blocks = 256;
    private const int Rows = Block * Blocks;

    /// <summary>Row <paramref name="row"/>'s key: a multiplication by an odd number, one to one on 32 bits.</summary>
    private static long Key(long row) => (row * 2_654_435_761L) & 0xFFFF_FFFFL;

    [Fact]
    public async Task AnInThatEveryBlockHoldsStopsAskingBeforeTheLeaves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile file = await OpenAsync(ct);

        // Four values in every block, in row order: no filter can rule a block out, and asking every
        // one reads every leaf to learn nothing.
        long[] values = [.. Enumerable.Range(0, 4 * Blocks).Select(i => Key((long)i * (Block / 4)))];
        (int live, int segments) = await PruneAsync(file, values, askAll: false, ct);
        (int liveAll, int segmentsAll) = await PruneAsync(file, values, askAll: true, ct);

        Assert.Equal((Blocks, Blocks), (live, liveAll));
        Assert.True(segments < segmentsAll, $"{segments} segments read against {segmentsAll} asking every filter");
    }

    [Fact]
    public async Task AnInOfAFewValuesRulesOutWhatAskingEveryFilterRulesOut()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using VortexFile file = await OpenAsync(ct);

        // Eight values, each in its own block: a filter that refuses them pays for itself many times.
        long[] values = [.. Enumerable.Range(0, 8).Select(i => Key(((long)i * 32 * Block) + 5))];
        (int live, int segments) = await PruneAsync(file, values, askAll: false, ct);
        (int liveAll, int segmentsAll) = await PruneAsync(file, values, askAll: true, ct);

        Assert.Equal((liveAll, segmentsAll), (live, segments));
        Assert.InRange(live, 8, 8 + 4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task AnInOfAnySizeKeepsTheSameRowsWithTheIndexesOnAndOff(int lanes)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await PathAsync();
        int degree = lanes > 0 ? lanes : Environment.ProcessorCount;
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: ct);
        foreach (int count in (int[])[1, 64, 1_024, 20_000])
        {
            // Half the values held by a row of the file, half outside its keys.
            long[] values = [.. Enumerable.Range(0, count).Select(i => i % 2 == 0 ? Key((long)i * 7 % Rows) : (1L << 33) + i)];
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

    /// <summary>The blocks the Bloom filters leave live under <paramref name="values"/>, and the index segments they read.</summary>
    private static async Task<(int Live, int Segments)> PruneAsync(VortexFile file, long[] values, bool askAll, CancellationToken ct)
    {
        VortexExpr filter = Expr.In(Expr.Field("key"), [.. values.Select(FilterLiteral.From)]);
        BloomPruner pruner = Assert.IsType<BloomPruner>(await BloomPruner.BuildAsync(file, filter, Block, ct));
        if (askAll)
        {
            pruner.BlockWorth = long.MaxValue;
        }

        BlockMask live = new BlockMask(file.RowCount, Block);
        await pruner.RefineAsync(file, live, ct);
        return (live.LiveCount, pruner.Segments);
    }

    private static async Task<VortexFile> OpenAsync(CancellationToken ct) => await VortexFile.OpenAsync(await PathAsync(), ct);

    private static Task<string> PathAsync() => SharedFiles.GetAsync(
        $"{nameof(BloomLargeInTests)}/{Blocks}x{Block}",
        async path =>
        {
            Decoders.EnsureRegistered();
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType schema = types.Struct(["key"], [i64], Nullability.NonNullable);
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> keys = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                keys[row] = Key(row);
            }

            int column = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
            int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.None.For("key", IndexSpec.Bloom(resolutions: 3)),
            };

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        });
}
