// The transposition table is asserted LITERALLY, against the known values of the crate's
// `test_transpose_known_indices` - never against our own implementation. Writing
// `untranspose(x) = transpose(x)` survives a round-trip test written the same wrong way in both
// directions, and these known values are the only thing that catches it.
//
// The bit-packing kernel is checked against a naive bit-by-bit reference packer written from the
// layout definition rather than from the unpack kernel, so a shared misunderstanding of the
// iteration order cannot cancel out.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays.Decoders.Compressed;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class FastLanesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 64)]
    [InlineData(16, 32)]
    [InlineData(32, 16)]
    [InlineData(48, 48)]
    [InlineData(64, 8)]
    [InlineData(128, 1)]
    [InlineData(1023, 1023)]
    public void TransposeMatchesTheCrateKnownValues(int index, int expected) =>
        Assert.Equal(expected, FastLanes.Transpose(index));

    [Fact]
    public void TransposeIsAPermutationOfTheBlock()
    {
        bool[] seen = new bool[FastLanes.BlockSize];
        for (int i = 0; i < FastLanes.BlockSize; i++)
        {
            int output = FastLanes.Transpose(i);
            Assert.InRange(output, 0, FastLanes.BlockSize - 1);
            Assert.False(seen[output], $"transpose produced duplicate index {output}");
            seen[output] = true;
        }

        Assert.DoesNotContain(false, seen);
    }

    [Fact]
    public void UntransposeIsTheInverseMappingAndNotASecondTranspose()
    {
        for (int i = 0; i < FastLanes.BlockSize; i++)
        {
            Assert.Equal(i, FastLanes.Untranspose(FastLanes.Transpose(i)));
        }

        // The one pair that makes the difference visible: if untranspose were a second transpose,
        // untranspose(64) would be 8 rather than 1.
        Assert.Equal(64, FastLanes.Transpose(1));
        Assert.Equal(8, FastLanes.Transpose(64));
        Assert.Equal(1, FastLanes.Untranspose(64));
    }

    [Fact]
    public void FlOrderIsItsOwnInverse()
    {
        ReadOnlySpan<byte> order = FastLanes.Order;
        for (int i = 0; i < order.Length; i++)
        {
            Assert.Equal(i, order[order[i]]);
        }
    }

    [Fact]
    public void TransposeBlockAndUntransposeBlockRoundTrip()
    {
        int[] input = new int[FastLanes.BlockSize];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = i * 7;
        }

        int[] transposed = new int[FastLanes.BlockSize];
        int[] back = new int[FastLanes.BlockSize];
        FastLanes.TransposeBlock<int>(input, transposed);
        FastLanes.UntransposeBlock<int>(transposed, back);
        Assert.Equal(input, back);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void PackedIndexTableIsAPermutation(int elementBits)
    {
        ReadOnlySpan<int> table = FastLanes.PackedIndexTable(elementBits);
        Assert.Equal(FastLanes.BlockSize, table.Length);

        bool[] seen = new bool[FastLanes.BlockSize];
        for (int i = 0; i < table.Length; i++)
        {
            Assert.InRange(table[i], 0, FastLanes.BlockSize - 1);
            Assert.False(seen[table[i]]);
            seen[table[i]] = true;
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void PackedRowAndLaneTablesInvertTheIndexTable(int elementBits)
    {
        int lanes = FastLanes.BlockSize / elementBits;
        ReadOnlySpan<int> index = FastLanes.PackedIndexTable(elementBits);
        ReadOnlySpan<int> rows = FastLanes.PackedRowTable(elementBits);
        ReadOnlySpan<int> laneTable = FastLanes.PackedLaneTable(elementBits);

        for (int row = 0; row < elementBits; row++)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                int logical = index[(row * lanes) + lane];
                Assert.Equal(row, rows[logical]);
                Assert.Equal(lane, laneTable[logical]);
            }
        }
    }

    [Fact]
    public void UnpackRoundTripsEveryBitWidthForByte()
    {
        for (int width = 0; width <= 8; width++)
        {
            AssertRoundTrip<byte>(width, 8);
        }
    }

    [Fact]
    public void UnpackRoundTripsEveryBitWidthForUInt16()
    {
        for (int width = 0; width <= 16; width++)
        {
            AssertRoundTrip<ushort>(width, 16);
        }
    }

    [Fact]
    public void UnpackRoundTripsEveryBitWidthForUInt32()
    {
        for (int width = 0; width <= 32; width++)
        {
            AssertRoundTrip<uint>(width, 32);
        }
    }

    [Fact]
    public void UnpackRoundTripsEveryBitWidthForUInt64()
    {
        for (int width = 0; width <= 64; width++)
        {
            AssertRoundTrip<ulong>(width, 64);
        }
    }

    [Fact]
    public void UnpackWithZeroBitWidthProducesZeros()
    {
        uint[] output = new uint[FastLanes.BlockSize];
        Array.Fill(output, 0xDEADBEEF);
        FastLanes.UnpackBlock<uint>(ReadOnlySpan<uint>.Empty, 0, output);
        Assert.All(output, v => Assert.Equal(0u, v));
    }

    [Fact]
    public void UnpackWithFullBitWidthCopiesThroughTheIndexTable()
    {
        // W == T is the "packs nothing" fast path: packed[LANES * row + lane] is the value.
        const int elementBits = 32;
        int lanes = FastLanes.BlockSize / elementBits;
        uint[] packed = new uint[lanes * elementBits];
        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] = 0x8000_0000u | (uint)i;
        }

        uint[] output = new uint[FastLanes.BlockSize];
        FastLanes.UnpackBlock<uint>(packed, elementBits, output);

        ReadOnlySpan<int> index = FastLanes.PackedIndexTable(elementBits);
        for (int row = 0; row < elementBits; row++)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                Assert.Equal(packed[(lanes * row) + lane], output[index[(row * lanes) + lane]]);
            }
        }
    }

    [Fact]
    public void UnpackRejectsAWrongPackedLength()
    {
        uint[] packed = new uint[31 * 4];   // one word short of 32 lanes * 4 bits
        uint[] output = new uint[FastLanes.BlockSize];
        Assert.Throws<ArgumentException>(() => FastLanes.UnpackBlock<uint>(packed, 4, output));
    }

    [Fact]
    public void UnpackRejectsAWrongOutputLength()
    {
        uint[] packed = new uint[32 * 4];
        uint[] output = new uint[FastLanes.BlockSize - 1];
        Assert.Throws<ArgumentException>(() => FastLanes.UnpackBlock<uint>(packed, 4, output));
    }

    [Fact]
    public void UnpackRejectsABitWidthWiderThanTheElement()
    {
        uint[] packed = new uint[32 * 33];
        uint[] output = new uint[FastLanes.BlockSize];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FastLanes.UnpackBlock<uint>(packed, 33, output));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(255)]
    public void IndexTablesRejectAnUnsupportedElementWidth(int elementBits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanes.PackedIndexTable(elementBits));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanes.PackedRowTable(elementBits));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanes.PackedLaneTable(elementBits));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024)]
    public void TransposeRejectsAnIndexOutsideTheBlock(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanes.Transpose(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanes.Untranspose(index));
    }

    private static void AssertRoundTrip<T>(int bitWidth, int elementBits)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        T[] values = Sample<T>(bitWidth, elementBits);
        T[] packed = NaivePack(values, bitWidth, elementBits);
        T[] output = new T[FastLanes.BlockSize];
        FastLanes.UnpackBlock<T>(packed, bitWidth, output);

        for (int i = 0; i < FastLanes.BlockSize; i++)
        {
            Assert.True(
                values[i] == output[i],
                $"width {bitWidth} of {elementBits}: index {i} unpacked to {output[i]}, expected {values[i]}");
        }
    }

    // Deterministic pseudo-random values, already masked to the bit width so the round trip is
    // exact. xorshift rather than Random so the sequence is fixed by the source, not by a seed
    // whose algorithm may change between runtimes.
    private static T[] Sample<T>(int bitWidth, int elementBits)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        T[] values = new T[FastLanes.BlockSize];
        if (bitWidth == 0)
        {
            return values;
        }

        ulong mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1;
        ulong state = 0x2545F4914F6CDD1DUL ^ ((ulong)elementBits << 32) ^ (ulong)bitWidth;
        for (int i = 0; i < values.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            values[i] = T.CreateTruncating(state & mask);
        }

        return values;
    }

    // Independent reference: lane L's bit stream is the concatenation, LSB first, of the words
    // packed[LANES * w + L] for w in 0..W, and the value at index(row, lane) occupies bits
    // [row * W, (row + 1) * W) of it. Written bit by bit, so it shares no structure with the
    // kernel under test.
    private static T[] NaivePack<T>(T[] values, int bitWidth, int elementBits)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int lanes = FastLanes.BlockSize / elementBits;
        T[] packed = new T[lanes * bitWidth];
        if (bitWidth == 0)
        {
            return packed;
        }

        ReadOnlySpan<byte> order = FastLanes.Order;
        for (int lane = 0; lane < lanes; lane++)
        {
            for (int row = 0; row < elementBits; row++)
            {
                int logical = (order[row / 8] * 16) + ((row % 8) * 128) + lane;
                ulong value = ulong.CreateTruncating(values[logical]);
                for (int bit = 0; bit < bitWidth; bit++)
                {
                    if (((value >> bit) & 1) == 0)
                    {
                        continue;
                    }

                    int position = (row * bitWidth) + bit;
                    int word = position / elementBits;
                    int offset = position % elementBits;
                    packed[(lanes * word) + lane] |= T.One << offset;
                }
            }
        }

        Assert.Equal(lanes * bitWidth, packed.Length);
        Assert.Equal(elementBits, Unsafe.SizeOf<T>() * 8);
        return packed;
    }
}
