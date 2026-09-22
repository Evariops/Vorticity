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
                Int32s(Offsets(sizes)), PType.I32, heap, byOffsets, rows, requireUtf8: false, in mask);
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
                Int32s(Offsets(split)), PType.I32, heap, new byte[3 * 16], 3, requireUtf8: true, in mask);
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
                Int32s([0, 10, 5, 30]), PType.I32, heap, new byte[3 * 16], 3, requireUtf8: false, in mask);
        });
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
}
