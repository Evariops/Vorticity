// The decode saving of the read contract.
//
// Skipping a dead split was always the I/O saving; what did not exist was the DECODE saving. The
// first live split of a chunk decoded the chunk whole and retained it, so a chunk of sixteen
// blocks with one live block materialized sixteen. With the mask of live blocks on the scan
// context, a chunk with dead blocks in it goes through the selection path instead -- this batch's
// rows as a counted range -- and an encoding that selects without a full decode materializes the
// live block and nothing else.
//
// The quantity is `FlatLayoutReader.ValuesDecoded`, the counter `FlatLayoutDecodeCountTests` holds
// the scan and the take to, and it is EXACT here too: one block's rows, pruned; the whole chunk,
// unpruned. Two shapes, because two readers have the branch: a file whose single chunk IS the root
// (the flat reader sees the mask in file coordinates), and a file of many chunks (the chunked
// reader translates the mask into a chunk-local selection).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

[Collection(nameof(AllocationCollection))]
public sealed class BlockPruningDecodeTests
{
    /// <summary>Rows per block, and so per zone and per split.</summary>
    private const int Block = 1024;

    /// <summary>
    /// A band of ~100 values of a monotone bit-packed column falls in one block; pruned, the scan
    /// materializes that block alone, and returns exactly what the unpruned scan returns.
    /// </summary>
    /// <param name="batches">Batches written, one chunk each when more than one.</param>
    /// <param name="rowsPerBatch">Rows per batch; a multiple of the block for the many-chunk shape.</param>
    /// <param name="chunkBytes">The writer's byte target per chunk.</param>
    /// <param name="prunedDecodes">
    /// The values a pruned scan materializes, exactly. Two parts. The zone maps' own rows: the
    /// zones child of each column is a flat node the same reader decodes whole, one row per block
    /// -- 98 for 100 000 rows, 96 for 98 304. And the live splits: one split of 1 024 rows in the
    /// many-chunk shape, where a chunk is 8 blocks and divides evenly; TWO of 1 021 in the
    /// one-chunk shape, because <c>SplitCursor</c> sub-divides a 100 000-row span evenly rather
    /// than into blocks-plus-a-remainder, so its splits straddle blocks and the band's rows
    /// (49 998 to 50 099) fall in two of them. Rows of the dead half of a straddling split are
    /// decoded and filtered out, which is correct and is what the split geometry costs.
    /// </param>
    [Theory]
    [InlineData(1, 100_000, 1_048_576, (2 * 1_021) + 98)]
    [InlineData(12, 8_192, 65_536, 1_024 + 96)]
    public async Task APrunedScanMaterializesOnlyTheLiveBlocks(
        int batches, int rowsPerBatch, long chunkBytes, long prunedDecodes)
    {
        Decoders.EnsureRegistered();

        int rows = batches * rowsPerBatch;
        string path = Write(batches, rowsPerBatch, chunkBytes);
        try
        {
            // v = i + i % 3: monotone, not a progression, 17 bits packed under a frame of zero.
            // The band [50 000, 50 100) is rows 49 998 to 50 099 -- inside block 48 and no other.
            VortexExpr band = Expr.And(
                Expr.Ge(Expr.Field("v"), Expr.Literal(FilterLiteral.From(50_000L))),
                Expr.Lt(Expr.Field("v"), Expr.Literal(FilterLiteral.From(50_100L))));

            // The shape the rest of the test relies on: the band lives in one block of the mask.
            await using (VortexFile shape = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                LayoutTree tree = shape.LayoutTree;
                BlockMask? refined = await ZonePruningPlan.RefineAsync(shape, tree, band, CancellationToken.None);
                Assert.True(refined is not null, "the writer's zone maps should give the filter a mask; layout is " + Describe(tree.Root, 0));
                BlockMask mask = refined!;
                Assert.Equal((rows + Block - 1) / Block, mask.BlockCount);
                Assert.True(mask.LiveCount == 1, "the band should live in one block, not " + mask.LiveCount);
            }

            (List<long> unpruned, long unprunedDecoded) = await Read(path, band, prune: false);
            (List<long> pruned, long prunedDecoded) = await Read(path, band, prune: true);

            Console.Out.Write(
                "BLOCK PRUNING: " + rows.ToString(CultureInfo.InvariantCulture) + " rows in " +
                batches.ToString(CultureInfo.InvariantCulture) + " chunk(s) of " + Block.ToString(CultureInfo.InvariantCulture) +
                "-row blocks; a band of " + pruned.Count.ToString(CultureInfo.InvariantCulture) + " rows materialized " +
                prunedDecoded.ToString(CultureInfo.InvariantCulture) + " values pruned and " +
                unprunedDecoded.ToString(CultureInfo.InvariantCulture) + " unpruned.\n");

            Assert.NotEmpty(pruned);
            Assert.Equal(unpruned, pruned);

            // EXACT, like the decode-count tests: the live splits and the zone maps, against the
            // whole chunk(s) once -- 47x and 88x fewer values materialized for the same rows.
            Assert.Equal(prunedDecodes, prunedDecoded);
            Assert.Equal(rows, unprunedDecoded);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A column whose layout is a bare flat node, read in partial batches under a mask, answers as
    /// an unpruned scan does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only shape in the repository where the flat reader sees the scan's mask itself:
    /// <c>types/struct_field_names</c> is written elsewhere and its column sits directly under the
    /// root, where everything this library writes wraps a column in <c>vortex.chunked</c>, even for
    /// one chunk. A chunked ancestor clears the mask for its children and says what it means in the
    /// selection, so under one the flat reader never sees a mask at all.
    /// </para>
    /// <para>
    /// The batch cap is what puts the reader on a partial node: without it the file's 1 025 rows are
    /// one batch and the node is covered whole. The flat reader has no branch of its own for a
    /// partial batch of a masked flat node, because a bare flat column and a zone map do not occur
    /// together here: this file has no zone map, so its mask never holds a dead block. This test
    /// keeps that shape readable without such a branch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFlatColumnUnderNoChunkReadsTheSameRowsEitherWay()
    {
        Decoders.EnsureRegistered();
        string path = Corpus.Path("types/struct_field_names");

        int first = await FirstValue(path);
        VortexExpr band = Expr.And(
            Expr.Ge(Expr.Field(string.Empty), Expr.Literal(FilterLiteral.From(first))),
            Expr.Lt(Expr.Field(string.Empty), Expr.Literal(FilterLiteral.From(first + 1))));

        (List<int> unpruned, long unprunedDecoded) = await ReadField(path, band, prune: false, cap: 128);
        (List<int> pruned, long prunedDecoded) = await ReadField(path, band, prune: true, cap: 128);

        Console.Out.Write(
            "FLAT RANGE: a band of " + pruned.Count.ToString(CultureInfo.InvariantCulture) +
            " rows materialized " + prunedDecoded.ToString(CultureInfo.InvariantCulture) +
            " values pruned and " + unprunedDecoded.ToString(CultureInfo.InvariantCulture) + " unpruned.\n");

        Assert.NotEmpty(pruned);
        Assert.Equal(unpruned, pruned);
    }

    private static async Task<int> FirstValue(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            if (batch.RowCount > 0)
            {
                return batch.Column(System.Text.Encoding.UTF8.GetBytes(string.Empty))
                    .AsPrimitive<int>().Values[0];
            }
        }

        throw new InvalidOperationException("the fixture should hold rows");
    }

    private static async Task<(List<int> Values, long Decoded)> ReadField(
        string path, VortexExpr filter, bool prune, int cap)
    {
        FlatLayoutReader.ValuesDecoded = 0;
        List<int> values = [];
        byte[] name = System.Text.Encoding.UTF8.GetBytes(string.Empty);
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan()
            .Where(filter).WithPruning(prune).WithMaxBatchRows(cap).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            VortexColumn view = batch.Column(name);
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(view.AsPrimitive<int>().Values[row]);
            }
        }

        return (values, FlatLayoutReader.ValuesDecoded);
    }

    /// <summary>The layout tree in one line, with what each zone map answers, for a message.</summary>
    private static string Describe(LayoutNode node, int depth)
    {
        string zone = "";
        if (node.TryGetZoneMap(out ZoneMap map))
        {
            zone = " zonemap(len=" + map.ZoneLength.ToString(CultureInfo.InvariantCulture) +
                ", aggregates=" + map.AggregateCount.ToString(CultureInfo.InvariantCulture) +
                ", pruning=" + (map.IsPruningAvailable ? "yes" : "no") + ")";
        }

        string text = node.Encoding + "[" + node.RowCount.ToString(CultureInfo.InvariantCulture) + "]" + zone;
        if (depth < 4 && node.ChildCount > 0)
        {
            string[] children = new string[node.ChildCount];
            for (int i = 0; i < node.ChildCount; i++)
            {
                children[i] = Describe(node.GetChild(i), depth + 1);
            }

            text += "(" + string.Join(", ", children) + ")";
        }

        return text;
    }

    private static async Task<(List<long> Values, long Decoded)> Read(string path, VortexExpr filter, bool prune)
    {
        FlatLayoutReader.ValuesDecoded = 0;
        List<long> values = [];
        byte[] name = System.Text.Encoding.UTF8.GetBytes("v");
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().Where(filter).WithPruning(prune).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            VortexColumn view = batch.Column(name);
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(view.AsPrimitive<long>().Values[row]);
            }
        }

        return (values, FlatLayoutReader.ValuesDecoded);
    }

    /// <summary>One i64 column, blocks of <see cref="Block"/> rows, written in the batches asked for.</summary>
    private static string Write(int batches, int rowsPerBatch, long chunkBytes)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);

        int[] roots = new int[batches];
        long row = 0;
        for (int b = 0; b < batches; b++)
        {
            VortexBuffer values = arena.Allocate(rowsPerBatch * sizeof(long), sizeof(long), out Span<byte> destination);
            Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
            for (int i = 0; i < rowsPerBatch; i++, row++)
            {
                longs[i] = row + (row % 3);
            }

            int column = arena.AddPrimitive(i64, rowsPerBatch, Validity.NonNullable, PType.I64, values);
            roots[b] = arena.AddStruct(schema, rowsPerBatch, Validity.NonNullable, [column]);
        }

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-blockprune-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, roots, rowsPerBatch, chunkBytes).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(
        string path, DType schema, CanonicalArena arena, int[] roots, int rowsPerBatch, long chunkBytes)
    {
        // The DEFAULT edition, deliberately: below core2026.08.0 the writer has no `vortex.zoned`
        // layout to put a zone map in (VortexFileWriter.cs, "Omitted rather than approximated"),
        // and a file without zone maps has nothing to prune with.
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = chunkBytes,
        };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        for (int b = 0; b < roots.Length; b++)
        {
            using RecordBatch batch = new RecordBatch(arena, roots[b], (long)b * rowsPerBatch);
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }
}
