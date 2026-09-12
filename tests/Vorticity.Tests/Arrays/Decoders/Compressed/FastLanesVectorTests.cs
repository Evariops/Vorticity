// The vector unpack against the scalar one, at every width, exhaustively.
//
// The scalar loop is the transcription of the crate's `unpack!` macro and is pinned to its
// known-value table by FastLanesTests. So the vector paths need only one thing proved about them,
// and it is the strongest available: that they produce the same 1024 elements, for every element
// width and every bit width in [1, elementBits), over data whose every bit is set somewhere.
//
// WHY EXHAUSTIVE RATHER THAN SAMPLED. The word-boundary arithmetic is the whole difficulty of the
// kernel, and which bit widths cross a boundary awkwardly is a property of the pair
// (bitWidth, elementBits) - 7-bit values in 64-bit words spill differently from 17-bit ones, and
// the last row of a lane spilling EXACTLY onto a boundary is its own case that only some widths
// reach. There are 8 + 16 + 32 + 64 = 120 pairs in total. Sampling them would be a decision to not
// test the case that breaks.
//
// The corpus cannot substitute for this: it exercises the bit widths the reference's compressor
// happened to choose, which is a handful.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Compressed;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class FastLanesVectorTests
{
    [Fact]
    public void EveryBitWidthUnpacksIdenticallyToTheScalarLoop()
    {
        for (int bitWidth = 1; bitWidth < 8; bitWidth++)
        {
            Compare<byte>(bitWidth, 8);
        }

        for (int bitWidth = 1; bitWidth < 16; bitWidth++)
        {
            Compare<ushort>(bitWidth, 16);
        }

        for (int bitWidth = 1; bitWidth < 32; bitWidth++)
        {
            Compare<uint>(bitWidth, 32);
        }

        for (int bitWidth = 1; bitWidth < 64; bitWidth++)
        {
            Compare<ulong>(bitWidth, 64);
        }
    }

    /// <summary>
    /// The two special cases the vector path never sees, asserted where they are branched out.
    /// </summary>
    [Fact]
    public void TheDegenerateWidthsAreUnchanged()
    {
        // W == 0 stores nothing and every value is zero.
        ulong[] zero = new ulong[FastLanes.BlockSize];
        Array.Fill(zero, 1UL);
        FastLanes.UnpackBlock<ulong>([], 0, zero);
        Assert.All(zero, value => Assert.Equal(0UL, value));

        // W == T copies straight through.
        ulong[] packed = new ulong[16 * 64];
        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] = 0x0123_4567_89AB_CDEFUL ^ (ulong)i;
        }

        ulong[] output = new ulong[FastLanes.BlockSize];
        FastLanes.UnpackBlock<ulong>(packed, 64, output);

        ReadOnlySpan<int> index = FastLanes.PackedIndexTable(64);
        for (int lane = 0; lane < 16; lane++)
        {
            for (int row = 0; row < 64; row++)
            {
                Assert.Equal(packed[(16 * row) + lane], output[index[(row * 16) + lane]]);
            }
        }
    }

    /// <summary>
    /// A round trip through the packer, which is what the file format actually does.
    /// </summary>
    /// <remarks>
    /// Weaker than the differential test above on its own - a pack and an unpack written the same
    /// wrong way round-trip perfectly - but it is the one that fails if the vector path and the
    /// PACKER disagree, which the differential test cannot see because it never packs.
    /// </remarks>
    [Fact]
    public void PackedValuesComeBackThroughTheVectorPath()
    {
        Random random = new Random(20260912);
        for (int bitWidth = 1; bitWidth < 64; bitWidth++)
        {
            ulong mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1;
            ulong[] values = new ulong[FastLanes.BlockSize];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ((ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 32)) & mask;
            }

            ulong[] packed = new ulong[16 * bitWidth];
            FastLanes.PackBlock<ulong>(values, bitWidth, packed);

            ulong[] output = new ulong[FastLanes.BlockSize];
            FastLanes.UnpackBlock<ulong>(packed, bitWidth, output);

            Assert.Equal(values, output);
        }
    }

    private static void Compare<T>(int bitWidth, int elementBits)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>, System.Numerics.IUnsignedNumber<T>
    {
        int lanes = FastLanes.BlockSize / elementBits;
        T[] packed = new T[lanes * bitWidth];

        // Deterministic and dense: every bit of every packed word participates, so a shift or a
        // mask that is off by one changes a value rather than a padding bit nobody reads.
        Span<byte> bytes = MemoryMarshal.AsBytes(packed.AsSpan());
        Random random = new Random((elementBits * 1000) + bitWidth);
        random.NextBytes(bytes);

        T[] expected = new T[FastLanes.BlockSize];
        FastLanes.UnpackBlockScalar<T>(
            packed, bitWidth, expected, FastLanes.PackedIndexTable(elementBits), elementBits, lanes);

        T[] actual = new T[FastLanes.BlockSize];
        FastLanes.UnpackBlock<T>(packed, bitWidth, actual);

        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(
                expected[i].Equals(actual[i]),
                $"{elementBits}-bit elements at width {bitWidth}, element {i}: " +
                $"scalar {expected[i]}, vector {actual[i]}");
        }
    }
}
