// Adversarial cases for vortex.null, vortex.bool, vortex.primitive and vortex.decimal: the child
// and buffer count boundaries, the metadata rule, the bit offset, and the two class I buffer checks
// (exact length, real address alignment).
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class LeafDecoderTests
{
    // ------------------------------------------------------------------------------ vortex.null

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1025)]
    public void NullDecodesToAnAllInvalidNode(int length)
    {
        using DecodeHarness h = new DecodeHarness();
        int index = h.Decode(new BlobBuilder(), new BlobNode("vortex.null"), h.Types.Null(Nullability.Nullable), length);

        CanonicalNode node = h.Node(index);
        Assert.Equal(CanonicalKind.Null, node.Kind);
        Assert.Equal(length, node.Length);
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
    }

    [Fact]
    public void NullRejectsNonEmptyMetadata()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobNode node = new BlobNode("vortex.null").WithMetadata([0x08, 0x00]);
        Assert.Throws<VortexFormatException>(
            () => h.Decode(new BlobBuilder(), node, h.Types.Null(Nullability.Nullable), 4));
    }

    [Fact]
    public void NullRejectsABuffer()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int buffer = b.AddBuffer([1, 2, 3, 4]);
        BlobNode node = new BlobNode("vortex.null").WithBuffers(buffer);
        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Null(Nullability.Nullable), 4));
    }

    [Fact]
    public void NullRejectsANonNullDType()
    {
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => h.Decode(
            new BlobBuilder(), new BlobNode("vortex.null"), h.Types.Bool(Nullability.NonNullable), 4));
    }

    // ------------------------------------------------------------------------------ vortex.bool

    [Theory]
    [InlineData(0, 8)]
    [InlineData(3, 8)]
    [InlineData(5, 8)]
    [InlineData(7, 1)]
    [InlineData(7, 2)]
    [InlineData(7, 9)]
    public void BoolAppliesTheBitOffset(int offset, int length)
    {
        // The values are chosen so that a decoder that drops the offset reads a different pattern.
        byte[] bits = new byte[(offset + length + 7) / 8];
        for (int i = 0; i < length; i++)
        {
            if (((i * 3) % 5) < 2)
            {
                int bit = offset + i;
                bits[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int buffer = b.AddBuffer(bits);
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool((uint)offset))
            .WithBuffers(buffer);

        int index = h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), length);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(offset, decoded.BitOffset);
        for (int i = 0; i < length; i++)
        {
            bool expected = ((i * 3) % 5) < 2;
            Assert.Equal(expected, CanonicalBits.Get(decoded.Bits.Span, decoded.BitOffset + i));
        }
    }

    [Fact]
    public void BoolRejectsAnOffsetOfEight()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int buffer = b.AddBuffer([0xFF, 0xFF]);
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool(8))
            .WithBuffers(buffer);

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), 4));
    }

    [Fact]
    public void BoolRejectsABitmapOneByteShort()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int buffer = b.AddBuffer([0xFF]);
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool(3))
            .WithBuffers(buffer);

        // 3 + 6 = 9 bits needs 2 bytes.
        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), 6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void BoolRejectsTheWrongBufferCount(int bufferCount)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0));
        for (int i = 0; i < bufferCount; i++)
        {
            node.WithBuffers(b.AddBuffer([0xFF]));
        }

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), 4));
    }

    [Theory]
    [InlineData(true, ValidityKind.AllValid)]
    [InlineData(false, ValidityKind.AllInvalid)]
    public void AConstantValidityChildCollapses(bool value, ValidityKind expected)
    {
        // AllInvalid reaches the wire as vortex.constant(false), and the collapse back into the
        // enum is required, not an optimization.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int bits = b.AddBuffer([0b0000_1010]);
        int scalar = b.AddBuffer(TestMetadata.ScalarBool(value));

        BlobNode validity = new BlobNode("vortex.constant").WithBuffers(scalar);
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool(0))
            .WithBuffers(bits)
            .WithChildren(validity);

        int index = h.Decode(b, node, h.Types.Bool(Nullability.Nullable), 4);
        Assert.Equal(expected, h.Node(index).Validity.Kind);
    }

    [Fact]
    public void BoolRejectsTwoChildren()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int bits = b.AddBuffer([0xFF]);
        int scalar = b.AddBuffer(TestMetadata.ScalarBool(true));

        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool(0))
            .WithBuffers(bits)
            .WithChildren(
                new BlobNode("vortex.constant").WithBuffers(scalar),
                new BlobNode("vortex.constant").WithBuffers(scalar));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Bool(Nullability.Nullable), 4));
    }

    // ------------------------------------------------------------------------- vortex.primitive

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void PrimitiveReadsValuesZeroCopy(int length)
    {
        byte[] values = new byte[length * 4];
        for (int i = 0; i < length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(values.AsSpan(i * 4, 4), i - 500);
        }

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.primitive").WithBuffers(b.AddBuffer(values));

        int index = h.Decode(b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), length);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(PType.I32, decoded.PType);
        Assert.Equal(length * 4, decoded.Values.Length);
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(i - 500, BinaryPrimitives.ReadInt32LittleEndian(decoded.Values.Span.Slice(i * 4, 4)));
        }
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void PrimitiveRejectsAWrongLengthBuffer(int byteCount)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.primitive").WithBuffers(b.AddBuffer(new byte[byteCount]));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 4));
    }

    [Fact]
    public void PrimitiveRejectsAMisalignedBuffer()
    {
        // A one-byte buffer in front shifts the i64 values off an 8-byte boundary. Upstream's
        // `vortex_ensure!(buffer.is_aligned_to(...))` and ours: rejected, never silently copied.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        b.AddBuffer([0xAA], alignmentExponent: 0);
        int values = b.AddBuffer(new byte[16], alignmentExponent: 0);

        BlobNode node = new BlobNode("vortex.primitive").WithBuffers(values);
        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Primitive(PType.I64, Nullability.NonNullable), 2));
    }

    [Fact]
    public void PrimitiveRejectsNonEmptyMetadata()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.primitive")
            .WithMetadata([0x08, 0x00])
            .WithBuffers(b.AddBuffer(new byte[16]));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 4));
    }

    // --------------------------------------------------------------------------- vortex.decimal

    [Fact]
    public void DecimalReadsI64Storage()
    {
        byte[] values = new byte[3 * 8];
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(0, 8), 12345);
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(8, 8), -5);
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(16, 8), 0);

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.decimal")
            .WithMetadata(TestMetadata.Decimal((int)DecimalStorageType.I64))
            .WithBuffers(b.AddBuffer(values));

        int index = h.Decode(b, node, h.Types.Decimal(18, 4, Nullability.NonNullable), 3);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(DecimalStorageType.I64, decoded.Storage);
        Assert.Equal((byte)18, decoded.Precision);
        Assert.Equal((sbyte)4, decoded.Scale);
        Assert.Equal(12345, BinaryPrimitives.ReadInt64LittleEndian(decoded.Values.Span[..8]));
    }

    [Fact]
    public void DecimalRejectsAStorageTooNarrowForThePrecision()
    {
        // Precision 18 needs i64; prost would coerce an unknown tag to I8 and read one byte a row.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.decimal")
            .WithMetadata(TestMetadata.Decimal((int)DecimalStorageType.I32))
            .WithBuffers(b.AddBuffer(new byte[12]));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Decimal(18, 4, Nullability.NonNullable), 3));
    }

    [Fact]
    public void DecimalRejectsAnUndefinedStorageTag()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.decimal")
            .WithMetadata(TestMetadata.Decimal(6))
            .WithBuffers(b.AddBuffer(new byte[24]));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Decimal(18, 4, Nullability.NonNullable), 3));
    }

    [Fact]
    public void DecimalAcceptsAWiderStorageThanThePrecisionNeeds()
    {
        // The wire says what the stride IS; a wider one is legal as long as it holds the precision.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.decimal")
            .WithMetadata(TestMetadata.Decimal((int)DecimalStorageType.I128))
            .WithBuffers(b.AddBuffer(new byte[2 * 16], alignmentExponent: 4));

        int index = h.Decode(b, node, h.Types.Decimal(18, 4, Nullability.NonNullable), 2);
        Assert.Equal(DecimalStorageType.I128, h.Node(index).Storage);
    }
}
