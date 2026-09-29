// Plan memory -- a column's last plan, kept while it still predicts its output -- against the
// candidates that cost nothing to price.
//
// A chunk that straddles a jump in the values is bit-packed, rightly; every chunk after it can be
// a pure progression. A plan memory that re-prices the held packing, finds it holds again, and
// returns it without ever asking the statistics whether the chunk is a sequence writes a packed
// chunk where 32 bytes are exact. The fault shows in the file's BYTES before it shows in time.
//
// NOTHING IN THE CORPUS HAS A BIT-PACKED CHUNK FOLLOWED BY A PROGRESSION, so the byte-identity
// anchor (`WrittenSizeTests`) never moved and the differential (`ChooserDifferentialTests`) counted
// zero memory disagreements. This is the smallest file that has the transition, and it is a unit
// test for the same reason `BitPackedWidthTests` is one: the corpus cannot be relied on to contain
// every sequence of shapes a column can take from chunk to chunk.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Tests.Columns;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A remembered plan never stands in front of a candidate the statistics price for free.</summary>
public sealed class PlanMemoryTests
{
    /// <summary>Rows per chunk: a multiple of every row block used here, so each batch goes out as its own chunk.</summary>
    private const int Rows = 8192;

    /// <summary>
    /// Four chunks of one i64 column -- a bit-packed one, a progression, a constant, a progression
    /// -- each one block long. After the packing has held, the progression must still be written as
    /// a sequence and the constant as a single run: the verdicts the reference chooser reaches on
    /// every chunk, and the bytes the writer produces without plan memory.
    /// </summary>
    [Fact]
    public async Task AHeldBitPackingDoesNotOutrankAProgressionOrAConstant()
    {
        // One block per chunk: one packed chunk of 8 192 seventeen-bit values (17 408 bytes), the
        // three chunks after it as a sequence, a constant and a sequence, and framing. A packing
        // carried over those three would pack them too.
        (long size, List<string> disagreements) = await Write(rowBlock: Rows);
        // The framing includes the file's identity, which every postscript carries (16 bytes of
        // value, the entry's key and segment in the postscript, and the padding they move: 104
        // bytes here), and the file statistics segment (120 bytes).
        Assert.Equal(19_156, size);
        Assert.True(
            disagreements.Count == 0,
            "the chooser and the reference disagree on " + disagreements.Count + " chunk(s):\n  "
            + string.Join("\n  ", disagreements));
    }

    /// <summary>
    /// The same four chunks, eight blocks each. The first block of a chunk is stepped from the last
    /// row of the chunk before it, and a jump there is a jump between chunks, not inside this one:
    /// the chunk is a progression all the same, and the column's bytes are the one-block file's.
    /// </summary>
    /// <remarks>
    /// The block seeded from the previous chunk's last row must not carry the jump as its own
    /// break: the merge would take a broken block for a broken chunk, and BOTH choosers, trusting
    /// the merge instead of the walk they make when the steps are unknown, would pack every
    /// progression. Plan memory plays no part: the fused pass steps every block from its
    /// predecessor. The 672 bytes over the one-block file are the zone map's: 32 blocks summarised
    /// instead of 4.
    /// </remarks>
    [Fact]
    public async Task AProgressionThatStartsAChunkAfterAJumpIsStillAProgression()
    {
        (long size, List<string> disagreements) = await Write(rowBlock: 1024);
        Assert.Equal(19_828, size);
        Assert.True(
            disagreements.Count == 0,
            "the chooser and the reference disagree on " + disagreements.Count + " chunk(s):\n  "
            + string.Join("\n  ", disagreements));
    }

    /// <summary>
    /// A dictionary column across four chunks -- 200 distinct strings, 8 192 rows each: the plan
    /// holds, the distinct table serves every chunk after the first, and the plans are the
    /// reference chooser's on every chunk.
    /// </summary>
    /// <remarks>
    /// Plan memory holds a dictionary to its own layer, not to the bytes its SUBTREE produces --
    /// the codes and the values after their own schemes, far below the layer's price. Held to
    /// those, it would "break" on every chunk, the table would never be expected to serve, and
    /// each chunk would walk for a dictionary the table had already built.
    /// </remarks>
    [Fact]
    public async Task AHeldDictionaryLetsTheTableServeTheChunksThatFollow()
    {
        Decoders.EnsureRegistered();

        using ColumnFixture f = new ColumnFixture();
        const int Chunks = 4;
        int[] roots = new int[Chunks];
        for (int c = 0; c < Chunks; c++)
        {
            byte[]?[] values = new byte[]?[Rows];
            for (int i = 0; i < Rows; i++)
            {
                values[i] = System.Text.Encoding.UTF8.GetBytes("value-" + ((c * 7) + i) % 200);
            }

            roots[c] = f.Utf8Node(values, Nullability.NonNullable);
        }

        DType dtype = f.Arena.GetNode(roots[0]).DType;
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Rows,
            DataBlockTargetBytes = 1024,
            WritePolicy = WritePolicy.Auto,
            StringBoundBytes = 0,
        };

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-memory-dict-{Guid.NewGuid():N}.vortex");
        List<string> disagreements = [];
        try
        {
            long fromTable;
            long withoutTable;
            ColumnCompressor.Differential.Value = new ColumnCompressor.DifferentialProbe(
                false,
                (node, chosen, reference) => disagreements.Add("node " + node + ": " + chosen + " => " + reference));
            try
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(path, dtype, options);
                for (int c = 0; c < Chunks; c++)
                {
                    await writer.WriteAsync(f.Batch(roots[c], (long)c * Rows), CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
                fromTable = writer.ChunksFromTable;
                withoutTable = writer.ChunksWithoutTable;
            }
            finally
            {
                ColumnCompressor.Differential.Value = null;
            }

            Assert.True(
                disagreements.Count == 0,
                "the chooser and the reference disagree on " + disagreements.Count + " chunk(s):\n  "
                + string.Join("\n  ", disagreements));
            Assert.Equal(Chunks - 1, fromTable);
            Assert.Equal(0, withoutTable);
            // The bytes the reference chooser's dictionaries make, whether the table or a walk built
            // them: pinned so that a table that served a different dictionary would show here first.
            // The column is a dictionary, so under Auto the file also carries its dict.probe entry
            // and the directory that lists it; the zones carry no text bounds.
            Assert.Equal(35_845, new FileInfo(path).Length);
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
    /// A bit-packed column whose batches never line up with the blocks, so every chunk after the
    /// first opens on the rows the emission before it carried: once the packing has held, the
    /// chunk that opens on a carried tail reads its widths like every chunk after it.
    /// </summary>
    /// <remarks>
    /// The carried rows are ingested before the plan holds, so the block they open has no
    /// histogram of its own: uncounted, the chunk it opens walks all the same, and the count on
    /// that chunk's other blocks serves nobody. `ColumnWriter.Reprobe` counts the carried rows
    /// when the table sees them again, so the block is whole.
    /// </remarks>
    [Fact]
    public async Task ACarriedTailCompletesTheOpenBlockSoTheNextChunkReadsItsWidths()
    {
        Decoders.EnsureRegistered();

        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);

        // Four batches of 8 131 rows -- the scan's batch, which never lines up with a block -- of
        // values in [0, 1000): too many distinct for a dictionary, no progression, and a zero every
        // thousand rows so every chunk's frame of reference is zero and its widths the ingested ones.
        const int Batch = 8131;
        const int Batches = 4;
        int[] roots = new int[Batches];
        long row = 0;
        for (int b = 0; b < Batches; b++)
        {
            VortexBuffer values = arena.Allocate(Batch * sizeof(long), sizeof(long), out Span<byte> destination);
            for (int i = 0; i < Batch; i++, row++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                    destination.Slice(i * sizeof(long), sizeof(long)), (row * 7919) % 1000);
            }

            roots[b] = arena.AddPrimitive(dtype, Batch, Validity.NonNullable, PType.I64, values);
        }

        // Blocks of 1 024 rows and at least 64 KB per chunk, cut from the batches in transit: the
        // chunks are 15 360, 8 192, 8 192 and the 780 rows left, and each after the first opens on
        // a carried tail.
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = 1024,
            DataBlockTargetBytes = 65_536,
        };

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-memory-widths-{Guid.NewGuid():N}.vortex");
        try
        {
            long fromWidths;
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, dtype, options))
            {
                for (int b = 0; b < Batches; b++)
                {
                    using RecordBatch batch = new RecordBatch(arena, roots[b], (long)b * Batch);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
                fromWidths = writer.ChunksFromWidths;
            }

            // The first chunk walks for its histogram; the three that open on a carried tail read
            // their widths. With the tail left uncounted, the second walked too. The bytes are the
            // walk's, whichever priced the packing.
            Assert.Equal(3, fromWidths);
            Assert.Equal(43_540, new FileInfo(path).Length);
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
    /// Writes the four chunks with <paramref name="rowBlock"/>-row blocks, one chunk per batch,
    /// with the differential probe installed; reads the file back; returns its size and the
    /// chunks on which the chooser and the reference chooser disagreed.
    /// </summary>
    private static async Task<(long Size, List<string> Disagreements)> Write(int rowBlock)
    {
        Decoders.EnsureRegistered();

        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);

        // Chunk 1: two progressions with a jump between them, 8 192 distinct values, so neither a
        // sequence nor a dictionary nor run-end applies and frame-of-reference packing wins.
        // Chunk 2: a progression. Chunk 3: one value. Chunk 4: a progression again, from further on.
        long[][] chunks =
        [
            Values(static i => i < Rows / 2 ? i : 100_000 + i),
            Values(static i => 200_000 + i),
            Values(static _ => 7),
            Values(static i => 300_000 + (3 * i)),
        ];

        int[] roots = new int[chunks.Length];
        for (int c = 0; c < chunks.Length; c++)
        {
            VortexBuffer values = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> destination);
            for (int i = 0; i < Rows; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                    destination.Slice(i * sizeof(long), sizeof(long)), chunks[c][i]);
            }

            roots[c] = arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, values);
        }

        // A batch that is a multiple of the row block and above the byte target goes out where it
        // lies, one chunk per batch -- the shape that makes chunk N's memory chunk N+1's input.
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = rowBlock,
            DataBlockTargetBytes = 1024,
        };

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-memory-{Guid.NewGuid():N}.vortex");
        List<string> disagreements = [];
        try
        {
            // The probe runs the reference chooser -- every candidate priced on every chunk -- next
            // to the one that decides, and reports each chunk where the two differ; a verdict plan
            // memory reached is prefixed "memory ". None is allowed here: memory may skip pricing,
            // it may not change the plan.
            ColumnCompressor.Differential.Value = new ColumnCompressor.DifferentialProbe(
                false,
                (node, chosen, reference) => disagreements.Add("node " + node + ": " + chosen + " => " + reference));
            try
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(path, dtype, options);
                for (int c = 0; c < roots.Length; c++)
                {
                    using RecordBatch batch = new RecordBatch(arena, roots[c], (long)c * Rows);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }
            finally
            {
                ColumnCompressor.Differential.Value = null;
            }

            long size = new FileInfo(path).Length;

            long[] actual = new long[Rows * chunks.Length];
            int seen = 0;
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                PrimitiveColumn<long> column = batch.Root.AsPrimitive<long>();
                for (int i = 0; i < column.Length; i++)
                {
                    actual[seen + i] = column[i];
                }

                seen += column.Length;
            }

            Assert.Equal(Rows * chunks.Length, seen);
            for (int c = 0; c < chunks.Length; c++)
            {
                Assert.Equal(chunks[c], actual.AsSpan(c * Rows, Rows).ToArray());
            }

            return (size, disagreements);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private static long[] Values(Func<long, long> of)
    {
        long[] values = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = of(i);
        }

        return values;
    }
}
