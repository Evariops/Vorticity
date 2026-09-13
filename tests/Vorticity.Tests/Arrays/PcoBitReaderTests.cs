// The pco bit reader, checked against a naive bit-by-bit oracle rather than against my reading of
// the reference.
//
// THE ORACLE IS THE POINT. The implementation is the fast shape upstream uses - a little-endian word
// shifted and masked, with a second overlapping word past 57 bits - and that shape is exactly where
// an off-by-one hides: a shift of 64, a boundary at bit 57, a read starting seven bits into a byte.
// The oracle here reads one bit at a time from the same definition and cannot share those mistakes,
// so every case below is decided by a disagreement between two independent implementations.
using System;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoBitReaderTests
{
    /// <summary>Reads <paramref name="width"/> bits one at a time, LSB first.</summary>
    private static ulong Naive(ReadOnlySpan<byte> source, long bitPosition, int width)
    {
        ulong value = 0;
        for (int i = 0; i < width; i++)
        {
            long bit = bitPosition + i;
            int b = (int)(bit >> 3);
            if (b < source.Length && (source[b] & (1 << (int)(bit & 7))) != 0)
            {
                value |= 1UL << i;
            }
        }

        return value;
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        uint state = (uint)seed | 1u;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }

        return bytes;
    }

    /// <summary>Every width from 0 to 64, at every bit offset in a byte, agrees with the oracle.</summary>
    /// <remarks>
    /// The offsets matter as much as the widths: the fast path shifts a word right by the offset,
    /// so an offset of 7 with a 57-bit read is the exact boundary between the one-word and two-word
    /// forms, and an offset of 0 with a 64-bit read is the shift-by-64 that the seven-byte overlap
    /// exists to avoid.
    /// </remarks>
    [Fact]
    public void EveryWidthAndOffsetAgreesWithTheNaiveReader()
    {
        byte[] source = Pattern(64, 7);
        int checks = 0;

        for (int offset = 0; offset < 8; offset++)
        {
            for (int width = 0; width <= 64; width++)
            {
                PcoBitReader reader = new PcoBitReader(source);
                if (offset > 0)
                {
                    reader.ReadUInt(offset);
                }

                ulong expected = Naive(source, offset, width);
                Assert.Equal(expected, reader.ReadUInt(width));
                Assert.Equal(offset + width, reader.BitPosition);
                checks++;
            }
        }

        Assert.Equal(8 * 65, checks);
    }

    /// <summary>A run of varying widths stays in step with the oracle across byte boundaries.</summary>
    [Fact]
    public void ASequenceOfMixedWidthsStaysInStep()
    {
        byte[] source = Pattern(256, 11);
        int[] widths = [1, 3, 7, 8, 9, 13, 25, 26, 31, 32, 33, 56, 57, 58, 63, 64, 5, 2];

        PcoBitReader reader = new PcoBitReader(source);
        long position = 0;
        foreach (int width in widths)
        {
            if (reader.BitsRemaining < width)
            {
                break;
            }

            Assert.Equal(Naive(source, position, width), reader.ReadUInt(width));
            position += width;
            Assert.Equal(position, reader.BitPosition);
        }

        Assert.True(position > 400, "the sequence should have crossed many byte boundaries");
    }

    /// <summary>A read past the end is refused rather than zero-filled.</summary>
    /// <remarks>
    /// Reading zeroes past the end is what the WINDOW does internally, so that a masked read needs
    /// no padding buffer; it is not what a READ may do. A stream that ends mid-field is corrupt and
    /// the distinction is the difference between an error and a plausible value.
    /// </remarks>
    [Fact]
    public void AReadPastTheEndThrows()
    {
        byte[] source = [0xFF, 0xFF];
        PcoBitReader reader = new PcoBitReader(source);
        reader.ReadUInt(15);

        Assert.Equal(1, reader.BitsRemaining);
        Assert.Throws<VortexFormatException>(() =>
        {
            PcoBitReader inner = new PcoBitReader(source);
            inner.ReadUInt(15);
            inner.ReadUInt(2);
        });
    }

    /// <summary>Draining to a byte boundary accepts zero padding and refuses anything else.</summary>
    [Fact]
    public void DrainingRefusesNonZeroPadding()
    {
        // 0b0000_0101: three bits read, the remaining five are zero.
        byte[] clean = [0b0000_0101, 0x00];
        PcoBitReader reader = new PcoBitReader(clean);
        Assert.Equal(5UL, reader.ReadUInt(3));
        reader.DrainEmptyByte("test field");
        Assert.Equal(8, reader.BitPosition);

        // 0b1000_0101: bit 7 is set, so the padding is not empty.
        byte[] dirty = [0b1000_0101, 0x00];
        Assert.Throws<VortexFormatException>(() =>
        {
            PcoBitReader inner = new PcoBitReader(dirty);
            inner.ReadUInt(3);
            inner.DrainEmptyByte("test field");
        });
    }

    /// <summary>Aligned byte reads require alignment and hand back the bytes themselves.</summary>
    [Fact]
    public void AlignedBytesRequireAlignment()
    {
        byte[] source = [1, 2, 3, 4, 5];
        PcoBitReader reader = new PcoBitReader(source);
        reader.ReadUInt(8);

        List<byte> taken = [];
        foreach (byte b in reader.ReadAlignedBytes(3))
        {
            taken.Add(b);
        }

        Assert.Equal<byte>([2, 3, 4], taken);
        Assert.Equal(32, reader.BitPosition);

        Assert.Throws<VortexFormatException>(() =>
        {
            PcoBitReader inner = new PcoBitReader(source);
            inner.ReadUInt(3);
            inner.ReadAlignedBytes(1);
        });
    }
}
