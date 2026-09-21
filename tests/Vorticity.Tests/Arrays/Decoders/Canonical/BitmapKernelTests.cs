// The two bitmap kernels that avoid bit-at-a-time loops, checked against the bit-at-a-time loops
// they stand in for.
//
// A bitmap kernel is exactly the kind of code where a fast path is right for 4094 of 4096
// alignments: the head, the tail and the single-byte range are each a different shape, and the
// shifted copy reads a byte PAST the one holding its own first bit. So the tests enumerate the
// alignments rather than sampling them, and compare against a reference written the obvious way.
using System;

using Vorticity.Arrays.Decoders.Canonical;

using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class BitmapKernelTests
{
    /// <summary>A deterministic bitmap; the pattern matters less than it not being uniform.</summary>
    private static byte[] Pattern(int bytes, int seed)
    {
        byte[] bits = new byte[bytes];
        uint state = (uint)seed + 0x9E3779B9u;
        for (int i = 0; i < bytes; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bits[i] = (byte)(state >> 17);
        }

        return bits;
    }

    private static bool BitAt(ReadOnlySpan<byte> bits, int index) =>
        (bits[index >> 3] & (1 << (index & 7))) != 0;

    [Fact]
    public void CountSetMatchesTheBitAtATimeCount()
    {
        byte[] bits = Pattern(40, 7);

        for (int start = 0; start <= 24; start++)
        {
            for (int count = 0; count <= 200; count++)
            {
                int expected = 0;
                for (int i = 0; i < count; i++)
                {
                    if (BitAt(bits, start + i))
                    {
                        expected++;
                    }
                }

                Assert.Equal(expected, BitmapKernels.CountSet(bits, start, count));
            }
        }
    }

    [Fact]
    public void CountSetIsZeroForAnEmptyRange()
    {
        byte[] bits = Pattern(8, 1);
        Assert.Equal(0, BitmapKernels.CountSet(bits, 3, 0));
        Assert.Equal(0, BitmapKernels.CountSet(bits, 3, -5));
    }

    [Fact]
    public void CountSetCountsAWholeUniformBitmap()
    {
        byte[] ones = new byte[16];
        ones.AsSpan().Fill(0xFF);
        Assert.Equal(128, BitmapKernels.CountSet(ones, 0, 128));
        Assert.Equal(127, BitmapKernels.CountSet(ones, 1, 127));
        Assert.Equal(0, BitmapKernels.CountSet(new byte[16], 0, 128));
    }

    [Fact]
    public void CopyRangeMatchesTheBitAtATimeCopyAtEveryAlignment()
    {
        byte[] source = Pattern(48, 11);

        // Both alignments, independently: the kernel's byte-wise path exists only when the SOURCE
        // shift is constant relative to a byte-aligned destination, so a source offset that differs
        // from the destination's is the case that actually exercises the shifted read.
        for (int sourceStart = 0; sourceStart <= 16; sourceStart++)
        {
            for (int destinationStart = 0; destinationStart <= 16; destinationStart++)
            {
                for (int count = 0; count <= 100; count++)
                {
                    byte[] expected = new byte[48];
                    for (int i = 0; i < count; i++)
                    {
                        if (BitAt(source, sourceStart + i))
                        {
                            expected[(destinationStart + i) >> 3] |=
                                (byte)(1 << ((destinationStart + i) & 7));
                        }
                    }

                    byte[] actual = new byte[48];
                    BitmapKernels.CopyRange(source, sourceStart, actual, destinationStart, count);

                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void CopyRangeClearsWhereTheSourceIsClear()
    {
        // The loop this replaced only ever SET bits, so it needed a cleared destination. The kernel
        // writes both values, which is what lets a caller stop clearing first.
        byte[] source = new byte[8];
        byte[] destination = new byte[8];
        destination.AsSpan().Fill(0xFF);

        BitmapKernels.CopyRange(source, 0, destination, 3, 40);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(BitAt(destination, i));
        }

        for (int i = 3; i < 43; i++)
        {
            Assert.False(BitAt(destination, i));
        }

        for (int i = 43; i < 64; i++)
        {
            Assert.True(BitAt(destination, i));
        }
    }

    [Fact]
    public void CopyRangeTouchesNothingOutsideTheRange()
    {
        byte[] source = Pattern(16, 3);
        byte[] destination = new byte[16];
        destination.AsSpan().Fill(0xAA);
        byte[] before = (byte[])destination.Clone();

        BitmapKernels.CopyRange(source, 5, destination, 9, 0);
        Assert.Equal(before, destination);

        BitmapKernels.CopyRange(source, 5, destination, 9, -1);
        Assert.Equal(before, destination);
    }
}
