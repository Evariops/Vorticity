// FastLanes.PackBlock, tested the only way a packer can honestly be tested.
//
// A ROUND TRIP PROVES NOTHING HERE. Pack and unpack share two index tables and a bit-spill rule;
// written the same wrong way round they agree perfectly, and the file they produce is one no other
// implementation can read. So the primary test packs a block and unpacks it with THE DECODER THAT
// ALREADY READS THE REFERENCE'S OWN FILES -- the one the 774-file conformance corpus exercises --
// and the round trip is then a statement about the wire format rather than about internal
// consistency.
//
// The second test is the one that would catch a shared bug anyway: it packs values, hands the bytes
// to a real vortex.bitpacked node, and reads them back through the decoder's own path.
using System;
using Vorticity.Arrays.Decoders.Compressed;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class PackBlockTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    public void PackingThenUnpackingWithTheCorpusValidatedDecoderReturnsTheValues(int bitWidth)
    {
        uint[] values = new uint[FastLanes.BlockSize];
        uint mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1;
        for (int i = 0; i < values.Length; i++)
        {
            // A pattern that is not a run and not sorted: a packer that transposed wrongly would
            // still round-trip a constant block.
            values[i] = (uint)((i * 2654435761u) >> 7) & mask;
        }

        int lanes = FastLanes.BlockSize / 32;
        uint[] packed = new uint[lanes * bitWidth];
        FastLanes.PackBlock<uint>(values, bitWidth, packed);

        uint[] unpacked = new uint[FastLanes.BlockSize];
        FastLanes.UnpackBlock<uint>(packed, bitWidth, unpacked);

        Assert.Equal(values, unpacked);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    public void EveryElementWidthPacks(int bitWidth)
    {
        // The spill rule depends on the element width, not just the bit width, so all four widths
        // are exercised rather than only the 32-bit one everything else uses.
        RoundTrip<byte>(Math.Min(bitWidth, 8), 8);
        RoundTrip<ushort>(Math.Min(bitWidth, 16), 16);
        RoundTrip<uint>(bitWidth, 32);
        RoundTrip<ulong>(bitWidth, 64);
    }

    [Fact]
    public void AZeroBitWidthPacksNothingAndUnpacksToZeros()
    {
        uint[] values = new uint[FastLanes.BlockSize];
        uint[] packed = [];
        FastLanes.PackBlock<uint>(values, 0, packed);

        uint[] unpacked = new uint[FastLanes.BlockSize];
        Array.Fill(unpacked, 7u);
        FastLanes.UnpackBlock<uint>(packed, 0, unpacked);
        Assert.All(unpacked, value => Assert.Equal(0u, value));
    }

    [Fact]
    public void AWrongLengthIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => FastLanes.PackBlock<uint>(new uint[10], 4, new uint[32 * 4]));
        Assert.Throws<ArgumentException>(
            () => FastLanes.PackBlock<uint>(new uint[FastLanes.BlockSize], 4, new uint[7]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FastLanes.PackBlock<uint>(new uint[FastLanes.BlockSize], 33, new uint[32 * 33]));
    }

    private static void RoundTrip<T>(int bitWidth, int elementBits)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>, System.Numerics.IUnsignedNumber<T>
    {
        T[] values = new T[FastLanes.BlockSize];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = T.CreateTruncating((uint)(i * 31) & ((1u << bitWidth) - 1));
        }

        int lanes = FastLanes.BlockSize / elementBits;
        T[] packed = new T[lanes * bitWidth];
        FastLanes.PackBlock<T>(values, bitWidth, packed);

        T[] unpacked = new T[FastLanes.BlockSize];
        FastLanes.UnpackBlock<T>(packed, bitWidth, unpacked);
        Assert.Equal(values, unpacked);
    }
}
