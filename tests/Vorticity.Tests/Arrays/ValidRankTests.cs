using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ValidRankTests
{
    private static readonly byte[] Bits = Bitmap(4_096);

    // A context reads a node's ranges in ascending order and counts from where the last stopped;
    // one that goes back, or reads another node, counts again from the first row.
    [Fact]
    public void EveryRangeCountsTheValidRowsBeforeIt()
    {
        using ScanContext context = new ScanContext([]);
        context.BeginNodeCheckScope(3);
        ArrayNode node = default;

        foreach (int start in (int[])[100, 900, 901, 2_048, 4_000, 700, 3_333, 0, 64])
        {
            Assert.Equal(BitmapKernels.CountSet(Bits, 0, start), context.Decode.ValidBefore(in node, Bits, 0, start));
        }
    }

    // Two nodes of one segment read in turn: each count is its own node's, whatever the other left.
    [Fact]
    public void AnotherNodeIsCountedFromItsFirstRow()
    {
        byte[] other = Bitmap(4_096, seed: 9);
        using ScanContext context = new ScanContext([]);
        context.BeginNodeCheckScope(3);
        ArrayNode first = default;

        Assert.Equal(BitmapKernels.CountSet(Bits, 0, 1_000), context.Decode.ValidBefore(in first, Bits, 0, 1_000));
        Assert.Equal(BitmapKernels.CountSet(other, 0, 2_000), CountOther(context, other, 2_000));
        Assert.Equal(BitmapKernels.CountSet(Bits, 0, 3_000), context.Decode.ValidBefore(in first, Bits, 0, 3_000));
    }

    // A bitmap that starts past its first byte counts from its offset at every range: the bits just
    // past each range's start are set and those just past its end clear, so a count taken from
    // the wrong bit comes out different.
    [Fact]
    public void ABitmapWithAnOffsetCountsFromIt()
    {
        byte[] bits = (byte[])Bits.Clone();
        for (int bit = 1_000; bit < 1_005; bit++)
        {
            bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }

        for (int bit = 2_500; bit < 2_505; bit++)
        {
            bits[bit >> 3] &= (byte)~(1 << (bit & 7));
        }

        using ScanContext context = new ScanContext([]);
        context.BeginNodeCheckScope(3);
        ArrayNode node = default;

        Assert.Equal(BitmapKernels.CountSet(bits, 5, 995), context.Decode.ValidBefore(in node, bits, 5, 995));
        Assert.Equal(BitmapKernels.CountSet(bits, 5, 2_495), context.Decode.ValidBefore(in node, bits, 5, 2_495));
    }

    [Fact]
    public void WithoutAScopeEveryRangeCountsFromTheFirstRow()
    {
        using ScanContext context = new ScanContext([]);
        ArrayNode node = default;

        Assert.Equal(BitmapKernels.CountSet(Bits, 0, 1_000), context.Decode.ValidBefore(in node, Bits, 0, 1_000));
        Assert.Equal(BitmapKernels.CountSet(Bits, 0, 2_000), context.Decode.ValidBefore(in node, Bits, 0, 2_000));
    }

    /// <summary>The count of a node other than the default one, in the same scope.</summary>
    private static int CountOther(ScanContext context, byte[] bits, int start)
    {
        ArrayNode other = new ArrayNode(null!, 1);
        return context.Decode.ValidBefore(in other, bits, 0, start);
    }

    private static byte[] Bitmap(int rows, int seed = 21)
    {
        byte[] bits = new byte[rows / 8];
        new Random(seed).NextBytes(bits);
        return bits;
    }
}
