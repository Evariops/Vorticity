// A lookup of a few keys in a sorted run of many entries reads every 1,024th key, then the one stretch of entries the
// samples leave each key, and the rows of the entries it finds: so every key here is held against the blocks a row at a
// time finds, keys whose rows straddle the samples, keys at a segment's ends, keys between two entries, and keys of
// many rows marked a block at a time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class SampledLookupTests
{
    private const int Block = 64;
    private const int Rows = 65_536;

    public static TheoryData<string> Layouts => new TheoryData<string> { "runs", "unique" };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task AFewKeysLeaveLiveTheBlocksTheirRowsLieIn(string layout)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        long[] keys = new long[Rows];
        for (int row = 0; row < Rows; row++)
        {
            // Runs of 700 rows straddle the samples a run of every 1,024th entry holds; unique keys are scattered.
            keys[row] = layout == "runs" ? row / 700 : (row * 2_654_435_761L) & 0xFFFF_FFFFL;
        }

        await using VortexFile file = await MemoryFiles.OpenAsync(await WriteAsync(keys));
        long[] sorted = [.. keys.Distinct().Order()];
        Random random = new Random(layout.Length);
        List<long[]> asks =
        [
            [sorted[0]],
            [sorted[^1]],
            [sorted[sorted.Length / 2]],
            [sorted[sorted.Length / 3], sorted[(sorted.Length / 3) + 1]],
            [sorted[random.Next(sorted.Length)], sorted[random.Next(sorted.Length)], sorted[random.Next(sorted.Length)]],
            [sorted[0] - 1],
            [sorted[^1] + 1],
            [sorted[5] + (layout == "runs" ? 0 : 1)],
        ];

        foreach (long[] asked in asks)
        {
            VortexExpr filter = asked.Length == 1
                ? Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(asked[0])))
                : Expr.In(Expr.Field("key"), [.. asked.Select(FilterLiteral.From)]);
            KeyIndexPruner pruner = Assert.IsType<KeyIndexPruner>(await KeyIndexPruner.BuildAsync(file, filter, Block, ct));
            BlockMask live = new BlockMask(file.RowCount, Block);
            await pruner.RefineAsync(file, live, ct);

            HashSet<long> wanted = [.. asked];
            for (int block = 0; block < Rows / Block; block++)
            {
                bool holds = false;
                for (int row = block * Block; row < (block + 1) * Block && !holds; row++)
                {
                    holds = wanted.Contains(keys[row]);
                }

                Assert.True(!holds || live.IsLive(block), $"{layout}: block {block} holds one of [{string.Join(", ", asked)}] and was ruled out.");
            }

            int holding = Enumerable.Range(0, Rows / Block).Count(block => Enumerable.Range(block * Block, Block).Any(row => wanted.Contains(keys[row])));
            Assert.Equal(holding, live.LiveCount);
        }
    }

    private static async Task<byte[]> WriteAsync(long[] keys)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [i64], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(keys.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        keys.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        int column = arena.AddPrimitive(i64, keys.Length, Validity.NonNullable, PType.I64, buffer);
        int root = arena.AddStruct(schema, keys.Length, Validity.NonNullable, [column]);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None.For("key", IndexSpec.SortedRuns),
        };

        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, options))
        {
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch);
            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }
}
