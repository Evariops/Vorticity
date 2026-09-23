using System;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// The validity of a delta array is the deltas', one bit per decoded value, and a row past the
/// array's offset reads the bit of the value it decodes to, whole or in a range.
/// </summary>
public sealed class DeltaDecoderTests
{
    private const int BlockSize = 1024;

    /// <summary>A u32 block holds its bases in 32 lanes.</summary>
    private const int Lanes = 32;

    private const uint Offset = 3;

    /// <summary>The one null, at decoded value 4: row 1 of the array, past its offset of 3.</summary>
    private const int NullValue = 4;

    [Fact]
    public void ARowPastTheOffsetReadsTheValidityOfItsOwnValue()
    {
        using DecodeHarness harness = Load();
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 5));

        Assert.Equal(5, node.Length);
        for (int row = 0; row < 5; row++)
        {
            Assert.Equal(row + (int)Offset != NullValue, harness.IsValid(node, row));
        }
    }

    [Fact]
    public void ARangeReadsTheValidityOfItsOwnValues()
    {
        using DecodeHarness harness = Load();
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(
            harness.Scan.Decode.DecodeRootRange(harness.Scan.Nodes.Root, u32, 5, 1, 3, keepEncoding: false));

        Assert.Equal(3, node.Length);
        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(1 + row + (int)Offset != NullValue, harness.IsValid(node, row));
        }
    }

    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    internal void AWholeBlockIsEachLanesRunningSumWhateverTheWidth(PType ptype)
    {
        // Random bases and deltas, so that every lane carries and wraps: the block is each lane's
        // running sum in FastLanes' transposed rows, read back in row order.
        int width = ptype.ByteWidth();
        int lanes = BlockSize / (width * 8);
        Random random = new Random(width);
        byte[] bases = new byte[lanes * width];
        byte[] deltas = new byte[BlockSize * width];
        random.NextBytes(bases);
        random.NextBytes(deltas);

        ulong mask = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
        ulong[] running = new ulong[lanes];
        for (int lane = 0; lane < lanes; lane++)
        {
            running[lane] = Read(bases, lane, width);
        }

        ulong[] block = new ulong[BlockSize];
        ReadOnlySpan<byte> order = Vorticity.Arrays.Decoders.Compressed.FastLanes.Order;
        for (int row = 0; row < BlockSize / lanes; row++)
        {
            int at = (order[row >> 3] * 16) + ((row & 7) * 128);
            for (int lane = 0; lane < lanes; lane++)
            {
                running[lane] = (running[lane] + Read(deltas, at + lane, width)) & mask;
                block[at + lane] = running[lane];
            }
        }

        TestNode root = new TestNode("fastlanes.delta")
            .WithMetadata(TestMetadata.Delta(BlockSize, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
        using DecodeHarness harness = DecodeHarness.Load(root, bases, deltas);
        DType dtype = harness.Types.Primitive(ptype, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, BlockSize));

        ReadOnlySpan<byte> values = node.Values.Span;
        ReadOnlySpan<int> untranspose = Vorticity.Arrays.Decoders.Compressed.FastLanes.UntransposeTable;
        for (int p = 0; p < BlockSize; p++)
        {
            Assert.Equal(block[untranspose[p]], Read(values, p, width));
        }
    }

    private static ulong Read(ReadOnlySpan<byte> bytes, int index, int width) => width switch
    {
        1 => bytes[index],
        2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index * 2)..]),
        4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]),
        _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[(index * 8)..]),
    };

    /// <summary>One block of zero deltas over zero bases, with every value valid but one.</summary>
    private static DecodeHarness Load()
    {
        bool[] valid = new bool[BlockSize];
        for (int i = 0; i < valid.Length; i++)
        {
            valid[i] = i != NullValue;
        }

        TestNode root = new TestNode("fastlanes.delta")
            .WithMetadata(TestMetadata.Delta(BlockSize, Offset))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)));

        return DecodeHarness.Load(
            root, new byte[Lanes * sizeof(uint)], new byte[BlockSize * sizeof(uint)], TestBuffers.Bitmap(valid));
    }
}
