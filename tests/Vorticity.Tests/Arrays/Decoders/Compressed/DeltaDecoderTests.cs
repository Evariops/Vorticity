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
