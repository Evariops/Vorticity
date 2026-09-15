// That the chooser is actually handed the statistics the ingest pass computed - the one thing
// `WrittenSizeTests` cannot see.
//
// Stage 2 of docs/11-write-strategy.md §8 replaces the passes `ColumnCompressor` ran over the rows
// with a read of the chunk's `BlockStats`. The replacement is deliberately FAIL-SOFT: `Choose`
// compares the summary's row count against the node it is given and measures the column itself when
// they disagree, so a wrong block range can only ever cost a pass. That is the right failure mode
// and it is also an invisible one -- every byte stays identical, every test stays green, and the
// whole stage quietly does nothing.
//
// So the writer counts the chunks that fell back, and this holds the count at zero across the batch
// shapes that make the arithmetic non-trivial: batches smaller than a block, larger than a block,
// straddling one, and exactly one.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ChunkStatisticsTests
{
    /// <summary>65 536 rows over five columns, in 64 batches of 1024: eight blocks, three chunks.</summary>
    private const string Wide = "containers/zoned_many_zones_nulls";

    /// <summary>8 193 rows: one row past a block, which is where an off-by-one hides.</summary>
    private const string Straddle = "types/utf8_nullable_r8193";

    public static TheoryData<string, int, int?> Shapes()
    {
        TheoryData<string, int, int?> data = [];
        foreach (string id in new[] { Wide, Straddle })
        {
            foreach (int batchRows in new[] { 1, 97, 1024, 8192, 8193, 65536 })
            {
                // The default block, a block smaller than most batches, and the caller's own
                // chunking -- the three regimes `EmitChunkAsync` computes a block range for.
                data.Add(id, batchRows, 8192);
                data.Add(id, batchRows, 512);
                data.Add(id, batchRows, null);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task EveryChunkIsHandedTheStatisticsOfItsOwnRows(string id, int batchRows, int? rowBlock)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-stats-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = rowBlock };

        try
        {
            long missing;
            long noTable;
            long rows;
            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(id), CancellationToken.None))
            {
                await using VortexFileWriter writer =
                    VortexFileWriter.Create(path, source.Schema, options);

                await foreach (RecordBatch batch in source.Scan()
                    .WithMaxBatchRows(batchRows).ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
                missing = writer.ChunksWithoutStatistics;
                noTable = writer.ChunksWithoutTable;
                rows = writer.RowCount;
            }

            Assert.True(rows > 0, "the fixture must have rows for the count to mean anything");
            Assert.True(
                missing == 0,
                $"{id} at batch {batchRows}, block {rowBlock?.ToString() ?? "null"}: {missing} " +
                "column chunk(s) fell back to measuring themselves, so stage 2 did nothing for them");

            // THE SAME RATCHET FOR THE DISTINCT TABLE (docs/11 §3.2.2, stage R2): the walk it
            // replaces is byte-identical to it by construction, so a table that quietly stopped
            // serving -- a chunk whose last block never recorded its count, a tail re-probed in the
            // wrong order -- would keep every byte-exact test green while the chooser walked every
            // chunk again. Held at zero over the same batch shapes, straddling and carried tails
            // included, because those are exactly the shapes that decide the table's lifetime.
            Assert.True(
                noTable == 0,
                $"{id} at batch {batchRows}, block {rowBlock?.ToString() ?? "null"}: {noTable} " +
                "column chunk(s) of a comparable kind were walked for their dictionary because the " +
                "distinct table could not serve them");
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
    /// The merge of a chunk's blocks equals a summary taken over the chunk's rows in one go.
    /// </summary>
    /// <remarks>
    /// The test above proves the range reaches the chooser; this proves the range is the RIGHT one.
    /// Both are needed: a range that covered the wrong blocks would still have the right row count
    /// whenever the blocks are uniform, which is the usual case.
    /// </remarks>
    [Theory]
    [InlineData(8192)]
    [InlineData(512)]
    [InlineData(100)]
    public void MergingABlockRangeEqualsSummarizingItsRowsAtOnce(int blockRows)
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();

        const int rows = 5_000;
        VortexBuffers.Primitive(arena, types, rows, out int node);

        ColumnWriter column = new ColumnWriter();
        int filled = 0;
        for (int offset = 0; offset < rows;)
        {
            int take = Math.Min(blockRows - filled, rows - offset);
            column.Accumulate(arena, node, offset, take);
            offset += take;
            filled += take;
            if (filled == blockRows)
            {
                column.CloseBlock();
                filled = 0;
            }
        }

        if (filled > 0)
        {
            column.CloseBlock();
        }

        BlockStats merged = column.Chunk(0, column.Blocks.Count);

        BlockStats direct = default;
        BlockStatsPass.Accumulate(arena, node, 0, rows, ref direct);

        Assert.Equal(direct.Rows, merged.Rows);
        Assert.Equal(direct.NullCount, merged.NullCount);
        Assert.Equal(direct.TotalBytes, merged.TotalBytes);
        Assert.Equal(direct.HasBounds, merged.HasBounds);
        Assert.Equal(direct.Min.SignedValue, merged.Min.SignedValue);
        Assert.Equal(direct.Max.SignedValue, merged.Max.SignedValue);

        // A range that does not start at block 0, so the offset is exercised rather than assumed.
        int half = column.Blocks.Count / 2;
        if (half > 0)
        {
            BlockStats tail = column.Chunk(half, column.Blocks.Count - half);
            BlockStats head = column.Chunk(0, half);
            Assert.Equal(direct.Rows, head.Rows + tail.Rows);
            Assert.Equal(direct.NullCount, head.NullCount + tail.NullCount);
        }
    }

    /// <summary>An out-of-range block request is absent rather than wrong.</summary>
    [Fact]
    public void AnIncompleteBlockRangeYieldsNoStatistics()
    {
        ColumnWriter column = new ColumnWriter();
        Assert.False(column.Chunk(0, 1).IsPresent);
        Assert.False(column.Chunk(-1, 1).IsPresent);
        Assert.False(column.Chunk(0, 0).IsPresent);
    }

    private static class VortexBuffers
    {
        /// <summary>An i64 column with a run of nulls, so every accumulator has something to do.</summary>
        internal static void Primitive(CanonicalArena arena, DTypeArena types, int rows, out int node)
        {
            VortexBuffer values = arena.Allocate(
                rows * sizeof(long), sizeof(long), out Span<byte> destination);
            Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
            for (int i = 0; i < rows; i++)
            {
                longs[i] = ((long)i * 37) - 1_000;
            }

            int bitmapBytes = Math.Max(
                CanonicalSupport.BitmapByteCount(rows), 1);
            VortexBuffer bits = arena.Allocate(bitmapBytes, 1, out Span<byte> bitmap);
            bitmap.Clear();
            for (int i = 0; i < rows; i++)
            {
                if (i % 11 != 0)
                {
                    CanonicalSupport.SetBit(bitmap, i);
                }
            }

            int validity = arena.AddBool(
                types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0);
            node = arena.AddPrimitive(
                types.Primitive(PType.I64, Nullability.Nullable), rows,
                Validity.Bitmap(validity), PType.I64, values);
        }
    }
}
