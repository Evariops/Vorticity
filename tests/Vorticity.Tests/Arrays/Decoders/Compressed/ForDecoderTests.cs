using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class ForDecoderTests
{
    [Fact]
    public void AddsTheReferenceToEveryValue()
    {
        byte[] encoded = TestBuffers.Int32(0, 1, 2, 3);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(100)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));

        Assert.Equal(CanonicalKind.Primitive, node.Kind);
        Assert.Equal(PType.I32, node.PType);
        Assert.Equal([100, 101, 102, 103], ReadInt32(node));
    }

    [Fact]
    public void EmptyMetadataIsRejectedBecauseItDecodesToANullReference()
    {
        // Contract §0a C2: spec/METADATA.md once listed fastlanes.for under "Empty metadata".
        // An empty ScalarValue message carries no kind at all.
        byte[] encoded = TestBuffers.Int32(1, 2);
        TestNode root = new TestNode("fastlanes.for")
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void AnExplicitlyNullReferenceIsRejected()
    {
        byte[] encoded = TestBuffers.Int32(1, 2);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Null()))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        VortexFormatException error =
            Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
        Assert.Contains("Reference value cannot be null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceArithmeticWrapsRatherThanThrowing()
    {
        // long.MinValue + (-1) must wrap to long.MaxValue, exactly as upstream's wrapping_add does.
        byte[] encoded = TestBuffers.Int64(-1, 0, 1);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(long.MinValue)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, 3));

        long[] values = new long[3];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(i * 8, 8));
        }

        Assert.Equal([long.MaxValue, long.MinValue, long.MinValue + 1], values);
    }

    [Fact]
    public void AZeroReferenceReturnsTheChildUnchanged()
    {
        byte[] encoded = TestBuffers.Int32(7, 8);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(0)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 2));
        Assert.Equal([7, 8], ReadInt32(node));
    }

    [Fact]
    public void ValidityComesFromTheEncodedChild()
    {
        byte[] encoded = TestBuffers.Int32(1, 2, 3, 4);
        byte[] validity = TestBuffers.Bitmap(true, false, true, true);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(10)))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(1)));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded, validity);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        Assert.Equal([11, 12, 13, 14], ReadInt32(node));
    }

    [Fact]
    public void ANonIntegerDTypeIsRejected()
    {
        byte[] encoded = new byte[8];
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(1)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AWrongChildCountIsRejected(int children)
    {
        byte[] encoded = TestBuffers.Int32(1, 2);
        TestNode root = new TestNode("fastlanes.for").WithMetadata(Reference(s => s.Int64(1)));
        for (int i = 0; i < children; i++)
        {
            root.WithChild(new TestNode("vortex.primitive").WithBuffer(0));
        }

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void ABufferOnTheNodeItselfIsRejected()
    {
        byte[] encoded = TestBuffers.Int32(1);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(s => s.Int64(1)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void AReferenceOutsideTheDTypeRangeIsRejected()
    {
        byte[] encoded = TestBuffers.Bytes(1, 2);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(1000)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType u8 = harness.Types.Primitive(PType.U8, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u8, 2));
    }

    [Fact]
    public void ZeroRowsDecodeToAnEmptyArray()
    {
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(Reference(store => store.Int64(5)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 0));
        Assert.Equal(0, node.Length);
    }

    internal static byte[] Reference(Func<ScalarStore, ScalarValue> build)
    {
        ScalarStore store = new();
        return TestMetadata.Scalar(build(store));
    }

    internal static int[] ReadInt32(CanonicalNode node)
    {
        int[] values = new int[node.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(node.Values.Span.Slice(i * 4, 4));
        }

        return values;
    }
}
