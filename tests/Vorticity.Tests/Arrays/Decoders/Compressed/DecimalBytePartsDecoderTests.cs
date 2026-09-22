// vortex.decimal_byte_parts. The corpus already proves the happy path on 32 files; what it cannot
// prove is the failure surface, because the reference writer never produces any of it. Everything
// here is a file the reference CANNOT write and a conformant reader must still refuse cleanly.
//
// The one positive test that is not redundant with the corpus is the width one: it pins the
// decision that the canonical storage follows the MSP CHILD rather than the precision, which is the
// point on which upstream's own two code paths disagree (see the decoder's header comment).
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class DecimalBytePartsDecoderTests
{
    [Theory]
    [InlineData(PType.I8, DecimalStorageType.I8)]
    [InlineData(PType.I16, DecimalStorageType.I16)]
    [InlineData(PType.I32, DecimalStorageType.I32)]
    [InlineData(PType.I64, DecimalStorageType.I64)]
    internal void TheCanonicalStorageWidthFollowsTheMspChildNotThePrecision(
        PType msp, DecimalStorageType expected)
    {
        // Precision 18 would need i64 storage if the precision decided. It does not: upstream's
        // `to_canonical_decimal` reinterprets the child's own buffer, so an i8 msp stays i8.
        int width = msp.ByteWidth();
        byte[] encoded = new byte[3 * width];
        encoded[0] = 7;
        encoded[width] = 9;

        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(msp))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(18, 4, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, 3));

        Assert.Equal(CanonicalKind.Decimal, node.Kind);
        Assert.Equal(expected, node.Storage);
        Assert.Equal(18, node.DType.Precision);
        Assert.Equal(4, node.DType.Scale);
        Assert.Equal(7, ReadUnscaled(node, 0, width));
        Assert.Equal(9, ReadUnscaled(node, 1, width));
        Assert.Equal(0, ReadUnscaled(node, 2, width));
    }

    [Fact]
    public void TheDecodeIsZeroCopyOverTheChildsValuesBuffer()
    {
        // The whole point of the encoding's shape: no arithmetic recomposition, so no new buffer.
        byte[] encoded = TestBuffers.Int64(-5, 0, 5);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I64))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, 3));

        Assert.Equal(-5, ReadUnscaled(node, 0, 8));
        Assert.Equal(5, ReadUnscaled(node, 2, 8));
        Assert.Equal(24, node.Values.Length);
    }

    [Fact]
    public void ValidityComesFromTheMspChild()
    {
        byte[] encoded = TestBuffers.Int32(1, 2, 3);
        byte[] validity = TestBuffers.Bitmap(true, false, true);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I32))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(1)));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded, validity);
        DType dtype = harness.Types.Decimal(9, 2, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, 3));

        Assert.Equal(CanonicalKind.Decimal, node.Kind);
        Assert.False(node.Validity.IsAllValid);
    }

    [Fact]
    public void AnUnsignedMspIsRejected()
    {
        // `DecimalBytePartsData::validate`: the first part must be signed. An unsigned child would
        // read the sign bit as magnitude and silently double every negative value.
        byte[] encoded = TestBuffers.UInt32(1, 2);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.U32))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(9, 2, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 2));
    }

    [Fact]
    public void AFloatMspIsRejected()
    {
        byte[] encoded = TestBuffers.Int64(1, 2);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.F64))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 2));
    }

    [Fact]
    public void ANonDecimalDTypeIsRejected()
    {
        byte[] encoded = TestBuffers.Int64(1, 2);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I64))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 2));
    }

    [Fact]
    public void ANonZeroLowerPartCountIsRejected()
    {
        // Hand-rolled, because TestMetadata deliberately cannot express it: field 2 varint 1.
        // The format pins lower_part_count to zero - a non-zero one means a wide decimal
        // whose lower limbs we would silently drop, producing a plausible but wrong number.
        byte[] metadata = [0x08, (byte)PType.I64, 0x10, 0x01];
        byte[] encoded = TestBuffers.Int64(1, 2);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 2));
    }

    [Fact]
    public void ABufferOnTheNodeIsRejected()
    {
        // nbuffers() is 0 upstream, and `buffer()` panics: a buffer here means we misread the node.
        byte[] encoded = TestBuffers.Int64(1, 2);
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I64))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 2));
    }

    [Fact]
    public void AMissingChildIsRejected()
    {
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I64));

        using DecodeHarness harness = DecodeHarness.Load(root, []);
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 2));
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyDecimal()
    {
        TestNode root = new TestNode("vortex.decimal_byte_parts")
            .WithMetadata(TestMetadata.DecimalByteParts(PType.I64))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        // One buffer, zero bytes long: the child declares buffer 0, and a zero-row array still has
        // it - the reference writes an empty buffer, not a missing one.
        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType dtype = harness.Types.Decimal(18, 2, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, 0));

        Assert.Equal(CanonicalKind.Decimal, node.Kind);
        Assert.Equal(0, node.Length);
    }

    private static long ReadUnscaled(CanonicalNode node, int index, int width)
    {
        ReadOnlySpan<byte> bytes = node.Values.Span.Slice(index * width, width);
        return width switch
        {
            1 => (sbyte)bytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadInt64LittleEndian(bytes),
        };
    }
}
