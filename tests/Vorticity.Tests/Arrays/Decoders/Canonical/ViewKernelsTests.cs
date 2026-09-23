using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

/// <summary>
/// The dense view kernels, which read a row as two words masked to its size, held to the writer
/// that gathers each row byte-exact: every size around the inline limit, rows at the heap's very
/// end, and the two ways a row can be malformed.
/// </summary>
public sealed class ViewKernelsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ViewsCutByLengthsOrOffsetsAreTheWritersViews(int seed)
    {
        Random random = new Random(seed);
        for (int trial = 0; trial < 50; trial++)
        {
            int rows = 1 + random.Next(40);
            int[] sizes = new int[rows];
            for (int i = 0; i < rows; i++)
            {
                sizes[i] = random.Next(4) == 0 ? random.Next(13, 40) : random.Next(0, 14);
            }

            int total = 0;
            foreach (int size in sizes)
            {
                total += size;
            }

            byte[] heap = new byte[total];
            random.NextBytes(heap);
            byte[] expected = Expected(heap, sizes, out bool referenced);

            byte[] byLengths = new byte[rows * 16];
            bool reported = ViewKernels.BuildFromLengths(
                Int32s(sizes), PType.I32, default, heap, byLengths, rows, requireUtf8: false);
            Assert.Equal(expected, byLengths);
            Assert.Equal(referenced, reported);

            byte[] byOffsets = new byte[rows * 16];
            CanonicalArena arena = new CanonicalArena();
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s(Offsets(sizes)), PType.I32, heap, byOffsets, rows, requireUtf8: false, in mask, VarBinDecoder.Id);
            Assert.Equal(expected, byOffsets);
        }
    }

    [Fact]
    public void AUtf8RowMustStartOnACharacter()
    {
        // "é" is two bytes; cutting between them leaves a valid heap and two invalid rows.
        byte[] heap = Encoding.UTF8.GetBytes("aébcdefghijklmnopqé");
        int[] whole = [1, 2, heap.Length - 3];
        int[] split = [2, 1, heap.Length - 3];

        byte[] views = new byte[3 * 16];
        ViewKernels.BuildFromLengths(Int32s(whole), PType.I32, default, heap, views, 3, requireUtf8: true);
        Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s(split), PType.I32, default, heap, new byte[3 * 16], 3, requireUtf8: true));

        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(() =>
        {
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s(Offsets(split)), PType.I32, heap, new byte[3 * 16], 3, requireUtf8: true, in mask, VarBinDecoder.Id);
        });
    }

    [Fact]
    public void ARowPastTheHeapIsRefused()
    {
        byte[] heap = new byte[30];
        Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s([10, 10, 11]), PType.I32, default, heap, new byte[3 * 16], 3, requireUtf8: false));
        Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s([10, -1, 10]), PType.I32, default, heap, new byte[3 * 16], 3, requireUtf8: false));

        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(() =>
        {
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s([0, 10, 5, 30]), PType.I32, heap, new byte[3 * 16], 3, requireUtf8: false, in mask, VarBinDecoder.Id);
        });
    }

    /// <summary>
    /// Enough rows for several blocks, cut by offsets and by lengths, sized so that every loop a
    /// block can be cut by is taken: all inline, all out of line, both, twelve-byte rows among long
    /// ones, and the last block, which ends at the heap's end, byte-exact.
    /// </summary>
    [Theory]
    [InlineData(0, 12)]
    [InlineData(13, 40)]
    [InlineData(12, 20)]
    [InlineData(0, 30)]
    public void ViewsCutFromManyBlocksAreTheWritersViews(int shortest, int longest)
    {
        Random random = new Random(shortest * 100 + longest);
        int[] sizes = new int[3_000];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = random.Next(shortest, longest + 1);
        }

        byte[] heap = new byte[Offsets(sizes)[^1]];
        for (int i = 0; i < heap.Length; i++)
        {
            heap[i] = (byte)random.Next(0x20, 0x7F);
        }

        byte[] expected = Expected(heap, sizes, out bool referenced);
        CanonicalArena arena = new CanonicalArena();
        ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
        PType[] offsetTypes = heap.Length <= short.MaxValue
            ? [PType.I16, PType.U16, PType.I32, PType.U32, PType.I64, PType.U64]
            : [PType.I32, PType.U32, PType.I64, PType.U64];
        foreach (bool requireUtf8 in (bool[])[false, true])
        {
            foreach (PType ptype in offsetTypes)
            {
                byte[] views = new byte[sizes.Length * 16];
                ViewKernels.BuildFromOffsets(
                    Typed(Offsets(sizes), ptype), ptype, heap, views, sizes.Length, requireUtf8, in mask, VarBinDecoder.Id);
                Assert.Equal(expected, views);
            }

            foreach (PType ptype in (PType[])[PType.I8, PType.U8, PType.I16, PType.U16, PType.I32, PType.U32, PType.I64, PType.U64])
            {
                byte[] views = new byte[sizes.Length * 16];
                bool reported = ViewKernels.BuildFromLengths(
                    Typed(sizes, ptype), ptype, default, heap, views, sizes.Length, requireUtf8);
                Assert.Equal(expected, views);
                Assert.Equal(referenced, reported);
            }
        }
    }

    /// <summary>
    /// The sum of 32-bit signed lengths is a reduction, and the first negative one is still named,
    /// in the vector part and in the tail.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1_777)]
    [InlineData(2_998)]
    public void SignedLengthsAreSummedAndTheFirstNegativeOneIsNamed(int negativeAt)
    {
        Random random = new Random(negativeAt + 2);
        int[] sizes = new int[2_999];
        long total = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = random.Next(0, 1_000_000);
            total += sizes[i];
        }

        // One negative length only: a second one in the tail would be seen there whatever the
        // vector part missed.
        if (negativeAt >= 0)
        {
            sizes[negativeAt] = -3;
        }

        (long sum, _, int negative) = ViewKernels.SumLengths(Int32s(sizes), PType.I32, default, sizes.Length);
        Assert.Equal(negativeAt >= 0 ? negativeAt : -1, negative);
        if (negativeAt < 0)
        {
            Assert.Equal(total, sum);
        }
    }

    /// <summary>
    /// A row cut through a character is refused whichever loop cuts its block: all inline, all out
    /// of line, or both.
    /// </summary>
    [Theory]
    [InlineData(4, 12)]
    [InlineData(13, 40)]
    [InlineData(4, 30)]
    public void AUtf8RowStartingOffACharacterIsRefusedInAnyBlock(int shortest, int longest)
    {
        Random random = new Random(longest);
        int[] sizes = new int[3_000];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = random.Next(shortest, longest + 1);
        }

        // Row 1500 ends with the first byte of a two-byte character, so row 1501 starts on the
        // second: the heap is valid UTF-8, and the row starting off a character is reported.
        int[] offsets = Offsets(sizes);
        byte[] heap = new byte[offsets[^1]];
        heap.AsSpan().Fill((byte)'a');
        heap[offsets[1501] - 1] = 0xC3;
        heap[offsets[1501]] = 0xA9;
        Assert.True(System.Text.Unicode.Utf8.IsValid(heap));

        CanonicalArena arena = new CanonicalArena();
        VortexFormatException error = Assert.Throws<VortexFormatException>(() =>
        {
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s(offsets), PType.I32, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: true, in mask, VarBinDecoder.Id);
        });
        Assert.Contains("Row 1501 ", error.Message, StringComparison.Ordinal);

        VortexFormatException byLengths = Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s(sizes), PType.I32, default, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: true));
        Assert.Contains("Row 1501 ", byLengths.Message, StringComparison.Ordinal);

        ValidityMask binary = ValidityMask.From(arena, Validity.NonNullable);
        ViewKernels.BuildFromOffsets(
            Int32s(offsets), PType.I32, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: false, in binary, VarBinDecoder.Id);
        ViewKernels.BuildFromLengths(
            Int32s(sizes), PType.I32, default, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: false);
    }

    /// <summary>
    /// A negative length, or lengths that run past the heap, are refused in a block past the first,
    /// before any row of that block is read.
    /// </summary>
    [Fact]
    public void ALengthNegativeOrPastTheHeapIsRefusedInALaterBlock()
    {
        int[] sizes = new int[3_000];
        sizes.AsSpan().Fill(5);

        int[] negative = (int[])sizes.Clone();
        negative[2_000] = -1;
        VortexFormatException refused = Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s(negative), PType.I32, default, new byte[sizes.Length * 5], new byte[sizes.Length * 16], sizes.Length, requireUtf8: false));
        Assert.Contains("Row 2000 ", refused.Message, StringComparison.Ordinal);

        VortexFormatException past = Assert.Throws<VortexFormatException>(() => ViewKernels.BuildFromLengths(
            Int32s(sizes), PType.I32, default, new byte[2_100 * 5], new byte[sizes.Length * 16], sizes.Length, requireUtf8: false));
        Assert.Contains("Row 2100 ", past.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An offset that decreases, or that leaves the heap, is refused in a block past the first,
    /// before any row of that block is read.
    /// </summary>
    [Fact]
    public void AnOffsetOutOfOrderOrOutsideTheHeapIsRefusedInALaterBlock()
    {
        int[] sizes = new int[3_000];
        sizes.AsSpan().Fill(5);
        byte[] heap = new byte[sizes.Length * 5];
        CanonicalArena arena = new CanonicalArena();

        int[] fell = Offsets(sizes);
        fell[2_000] = fell[1_999] - 1;
        VortexFormatException decreasing = Assert.Throws<VortexFormatException>(() =>
        {
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s(fell), PType.I32, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: false, in mask, VarBinDecoder.Id);
        });
        Assert.Contains("must not decrease; offset 2000 ", decreasing.Message, StringComparison.Ordinal);

        int[] outside = Offsets(sizes);
        outside[2_100] = heap.Length + 1;
        VortexFormatException past = Assert.Throws<VortexFormatException>(() =>
        {
            ValidityMask mask = ValidityMask.From(arena, Validity.NonNullable);
            ViewKernels.BuildFromOffsets(
                Int32s(outside), PType.I32, heap, new byte[sizes.Length * 16], sizes.Length, requireUtf8: false, in mask, VarBinDecoder.Id);
        });
        Assert.Contains("offset 2100 ", past.Message, StringComparison.Ordinal);
    }

    /// <summary>The views the byte-exact writer makes of rows tiling <paramref name="heap"/>.</summary>
    private static byte[] Expected(byte[] heap, int[] sizes, out bool referenced)
    {
        byte[] views = new byte[sizes.Length * 16];
        referenced = false;
        int offset = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            referenced |= CanonicalSupport.WriteView(
                views.AsSpan(i * 16, 16), heap.AsSpan(offset, sizes[i]), sizes[i], bufferIndex: 0, offset: offset);
            offset += sizes[i];
        }

        return views;
    }

    private static int[] Offsets(int[] sizes)
    {
        int[] offsets = new int[sizes.Length + 1];
        for (int i = 0; i < sizes.Length; i++)
        {
            offsets[i + 1] = offsets[i] + sizes[i];
        }

        return offsets;
    }

    private static byte[] Int32s(int[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(int)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), values[i]);
        }

        return bytes;
    }

    /// <summary><paramref name="values"/> as <paramref name="ptype"/>, an integer of any width.</summary>
    private static byte[] Typed(int[] values, PType ptype)
    {
        if (ptype is PType.I32 or PType.U32)
        {
            return Int32s(values);
        }

        if (ptype is PType.I8 or PType.U8)
        {
            byte[] bytes8 = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                bytes8[i] = checked((byte)values[i]);
            }

            return bytes8;
        }

        if (ptype is PType.I16 or PType.U16)
        {
            byte[] narrow = new byte[values.Length * sizeof(short)];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(narrow.AsSpan(i * sizeof(short)), checked((ushort)values[i]));
            }

            return narrow;
        }

        byte[] bytes = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), values[i]);
        }

        return bytes;
    }
}
