using System;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class RunEndDecoderTests
{
    [Fact]
    public void ExpandsEachRunOverItsRows()
    {
        // ends [2, 5, 6] over values [10, 20, 30] -> 10 10 20 20 20 30.
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 3, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(2, 5, 6), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 6));
        Assert.Equal([10, 10, 20, 20, 20, 30], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void AnOffsetTrimsTheLeadingRunsAndClampsTheTrailingOnes()
    {
        // trimmed_ends_iter: end_i = min(ends[i] - offset, length).
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 3, 3));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(4, 8, 12), TestBuffers.Int32(1, 2, 3));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 5));

        // Rows 3..8 of 1 1 1 1 2 2 2 2 3 3 3 3 are 1 2 2 2 2.
        Assert.Equal([1, 2, 2, 2, 2], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void NonIncreasingRunEndsAreRejected()
    {
        // Upstream only debug_asserts strict sortedness; its `take` binary-searches these.
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 3, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(2, 2, 6), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 6));
    }

    [Fact]
    public void DecreasingRunEndsAreRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 3, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(6, 5, 2), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 6));
    }

    [Fact]
    public void ALastRunEndBelowOffsetPlusLengthIsRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 2, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(2, 4), TestBuffers.Int32(10, 20));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 5));
    }

    [Fact]
    public void AFirstRunEndBelowANonZeroOffsetIsRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 2, 5));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(2, 9), TestBuffers.Int32(10, 20));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 4));
    }

    [Fact]
    public void ZeroRunsWithANonZeroOffsetIsRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 0, 1));
        using DecodeHarness harness = DecodeHarness.Load(
            root, Array.Empty<byte>(), Array.Empty<byte>());

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 0));
    }

    [Fact]
    public void ZeroRunsOverANonEmptyArrayIsRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 0, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, Array.Empty<byte>(), Array.Empty<byte>());

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 3));
    }

    [Fact]
    public void ZeroRunsOverAnEmptyArrayIsAccepted()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.U32, 0, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, Array.Empty<byte>(), Array.Empty<byte>());

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Equal(0, harness.Node(harness.DecodeRoot(i32, 0)).Length);
    }

    [Fact]
    public void SignedRunEndsAreRejected()
    {
        TestNode root = Node(TestMetadata.RunEnd(PType.I32, 2, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Int32(2, 4), TestBuffers.Int32(10, 20));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 4));
    }

    [Fact]
    public void AWrongChildCountIsRejected()
    {
        TestNode root = new TestNode("vortex.runend")
            .WithMetadata(TestMetadata.RunEnd(PType.U32, 1, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.UInt32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void NullRunValuesExpandIntoNullRows()
    {
        TestNode root = new TestNode("vortex.runend")
            .WithMetadata(TestMetadata.RunEnd(PType.U32, 3, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.UInt32(2, 4, 6),
            TestBuffers.Int32(10, 20, 30),
            TestBuffers.Bitmap(true, false, true));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 6));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        // rows 0,1 valid; 2,3 null; 4,5 valid.
        Assert.Equal(0b0011_0011, bits.Bits.Span[0]);
    }

    [Fact]
    public void AllNullRunValuesCollapseToAllInvalid()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.runend")
            .WithMetadata(TestMetadata.RunEnd(PType.U32, 2, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.constant").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(2, 4), TestMetadata.Scalar(store.Null()));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8193)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        // One run of every 7 rows plus a final run covering the remainder.
        int runs = ((rows + 6) / 7) is int n && n > 0 ? n : 1;
        uint[] ends = new uint[runs];
        int[] values = new int[runs];
        for (int i = 0; i < runs; i++)
        {
            ends[i] = (uint)Math.Min((i + 1) * 7, rows);
            values[i] = i;
        }

        ends[runs - 1] = (uint)rows;

        TestNode root = Node(TestMetadata.RunEnd(PType.U32, (ulong)runs, 0));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(ends), TestBuffers.Int32(values));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, rows));
        Assert.Equal(rows, node.Length);

        int[] decoded = ForDecoderTests.ReadInt32(node);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(Math.Min(i / 7, runs - 1), decoded[i]);
        }
    }

    private static TestNode Node(byte[] metadata) =>
        new TestNode("vortex.runend")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
}
