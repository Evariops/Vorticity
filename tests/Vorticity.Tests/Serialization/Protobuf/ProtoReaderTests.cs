using System;
using Vorticity;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// Tag decoding, fixed-width reads and length-delimited framing under hostile input.
/// Every rejection here must be a <see cref="VortexFormatException"/> and nothing else.
/// </summary>
public sealed class ProtoReaderTests
{
    [Fact]
    public void Default_reader_is_empty_and_at_end()
    {
        ProtoReader reader = default;
        Assert.True(reader.End);
        Assert.Equal(0, reader.Position);
        Assert.Equal(0, reader.Length);
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(0, fieldNumber);
        Assert.Equal(ProtoWireType.Varint, wireType);
    }

    [Fact]
    public void Empty_message_body_yields_no_fields()
    {
        ProtoReader reader = new ProtoReader(Array.Empty<byte>());
        Assert.False(reader.TryReadTag(out _, out _));
    }

    [Fact]
    public void TryReadTag_returns_false_once_the_body_is_consumed()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt32Always(1, 7));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(1, fieldNumber);
        Assert.Equal(ProtoWireType.Varint, wireType);
        Assert.Equal(7U, reader.ReadVarint32());
        Assert.False(reader.TryReadTag(out _, out _));
    }

    /// <summary>Field number 0 is reserved by the wire format and never assigned.</summary>
    [Theory]
    [InlineData(0)]  // tag 0x00: field 0, varint
    [InlineData(1)]  // tag 0x01: field 0, fixed64
    [InlineData(2)]  // tag 0x02: field 0, length-delimited
    [InlineData(5)]  // tag 0x05: field 0, fixed32
    public void TryReadTag_rejects_field_number_zero(int wireType)
    {
        byte[] data = ProtoTestHelpers.RawTag(0, wireType);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.TryReadTag(out _, out _);
        });
    }

    /// <summary>
    /// Groups (wire types 3 and 4) are on the reject side of metadata parsing: proto3 never
    /// emits them, so their presence is malformed input, not a forward-compatible extension.
    /// They are refused at the tag, before a caller can dispatch on the field number.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void TryReadTag_rejects_group_wire_types(int wireType)
    {
        byte[] data = ProtoTestHelpers.RawTag(1, wireType);

        VortexFormatException ex = Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.TryReadTag(out _, out _);
        });

        Assert.Contains("group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void TryReadTag_rejects_reserved_wire_types(int wireType)
    {
        byte[] data = ProtoTestHelpers.RawTag(1, wireType);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.TryReadTag(out _, out _);
        });
    }

    [Fact]
    public void TryReadTag_rejects_a_tag_wider_than_32_bits()
    {
        // A well-formed varint, but a tag is a uint32; without this check the >> 3 below would
        // narrow into a bogus (possibly negative) field number.
        byte[] data = ProtoTestHelpers.Varint(0x1_0000_0000UL);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.TryReadTag(out _, out _);
        });
    }

    [Fact]
    public void TryReadTag_accepts_the_largest_legal_field_number()
    {
        const int MaxFieldNumber = (1 << 29) - 1;
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt64Always(MaxFieldNumber, 9));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(MaxFieldNumber, fieldNumber);
        Assert.Equal(ProtoWireType.Varint, wireType);
        Assert.Equal(9UL, reader.ReadVarint());
    }

    [Fact]
    public void TryReadTag_rejects_a_truncated_tag_varint()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x80 });
            reader.TryReadTag(out _, out _);
        });
    }

    // -----------------------------------------------------------------------------------------
    // Fixed-width
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadFixed32_rejects_a_short_tail(int available)
    {
        byte[] data = new byte[available];

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadFixed32();
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void ReadFixed64_rejects_a_short_tail(int available)
    {
        byte[] data = new byte[available];

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadFixed64();
        });
    }

    [Fact]
    public void ReadFixed32_and_ReadFixed64_are_little_endian()
    {
        ProtoReader thirtyTwo = new ProtoReader(new byte[] { 0x78, 0x56, 0x34, 0x12 });
        Assert.Equal(0x12345678U, thirtyTwo.ReadFixed32());
        Assert.Equal(4, thirtyTwo.Position);

        ProtoReader sixtyFour = new ProtoReader(
            new byte[] { 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01 });
        Assert.Equal(0x0123456789ABCDEFUL, sixtyFour.ReadFixed64());
        Assert.Equal(8, sixtyFour.Position);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    [InlineData(float.Epsilon)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReadFloat_is_bit_preserving(float value)
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloatAlways(1, value));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out ProtoWireType wireType));
        Assert.Equal(ProtoWireType.Fixed32, wireType);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(value),
            BitConverter.SingleToUInt32Bits(reader.ReadFloat()));
    }

    [Fact]
    public void ReadFloat_preserves_a_NaN_payload()
    {
        float nan = BitConverter.UInt32BitsToSingle(0x7FC0_1234);
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloatAlways(1, nan));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(0x7FC0_1234U, BitConverter.SingleToUInt32Bits(reader.ReadFloat()));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(2.718281828459045d)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    [InlineData(double.NegativeInfinity)]
    public void ReadDouble_is_bit_preserving(double value)
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteDoubleAlways(1, value));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out ProtoWireType wireType));
        Assert.Equal(ProtoWireType.Fixed64, wireType);
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(value),
            BitConverter.DoubleToUInt64Bits(reader.ReadDouble()));
    }

    // -----------------------------------------------------------------------------------------
    // Length-delimited
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ReadLengthDelimited_returns_an_empty_span_for_a_zero_length_field()
    {
        ProtoReader reader = new ProtoReader(new byte[] { 0x00 });
        Assert.True(reader.ReadLengthDelimited().IsEmpty);
        Assert.True(reader.End);
    }

    [Fact]
    public void ReadLengthDelimited_slices_the_input_without_copying()
    {
        byte[] payload = ProtoTestHelpers.Payload(5);
        byte[] data = ProtoTestHelpers.Concat(new byte[] { 5 }, payload);

        ProtoReader reader = new ProtoReader(data);
        ReadOnlySpan<byte> slice = reader.ReadLengthDelimited();
        Assert.True(slice.SequenceEqual(payload));
        Assert.Equal(6, reader.Position);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(255)]
    public void ReadLengthDelimited_rejects_a_length_that_escapes_the_buffer(int declared)
    {
        byte[] data = ProtoTestHelpers.Concat(
            ProtoTestHelpers.Varint((ulong)declared),
            new byte[declared - 1]);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadLengthDelimited();
        });
    }

    [Theory]
    [InlineData(0x8000_0000UL)]
    [InlineData(0x1_0000_0000UL)]
    [InlineData(ulong.MaxValue)]
    public void ReadLengthDelimited_rejects_a_length_that_is_not_addressable(ulong declared)
    {
        byte[] data = ProtoTestHelpers.Concat(ProtoTestHelpers.Varint(declared), new byte[8]);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadLengthDelimited();
        });
    }

    /// <summary>
    /// The overflow case a 32-bit bounds check gets wrong: <c>position + length</c> wraps to a
    /// negative number that compares as "inside the buffer". The check must be 64-bit.
    /// </summary>
    [Fact]
    public void ReadLengthDelimited_rejects_a_length_that_overflows_the_position_arithmetic()
    {
        byte[] data = ProtoTestHelpers.Concat(
            new byte[8],                                 // consumed as a fixed64, position -> 8
            ProtoTestHelpers.Varint(int.MaxValue),       // position -> 13 after the length varint
            new byte[4]);

        // 13 + int.MaxValue overflows a 32-bit sum to a negative number, which a naive
        // `position + length <= Length` test would read as "inside the buffer".
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.SkipField(ProtoWireType.Fixed64);
            Assert.Equal(8, reader.Position);
            reader.ReadLengthDelimited();
        });
    }

    [Fact]
    public void ReadLengthDelimited_rejects_a_truncated_length_varint()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x80 });
            reader.ReadLengthDelimited();
        });
    }

    [Fact]
    public void ReadMessage_yields_a_sub_reader_that_cannot_see_past_its_body()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            using (ProtoWriter.MessageScope scope = w.BeginMessage(1))
            {
                w.WriteUInt32Always(1, 11);
                w.WriteUInt32Always(2, 22);
            }

            w.WriteUInt32Always(2, 33);
        });

        ProtoReader outer = new ProtoReader(data);
        Assert.True(outer.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(1, fieldNumber);
        Assert.Equal(ProtoWireType.LengthDelimited, wireType);

        ProtoReader inner = outer.ReadMessage();
        Assert.True(inner.TryReadTag(out int innerField, out _));
        Assert.Equal(1, innerField);
        Assert.Equal(11U, inner.ReadVarint32());
        Assert.True(inner.TryReadTag(out innerField, out _));
        Assert.Equal(2, innerField);
        Assert.Equal(22U, inner.ReadVarint32());
        Assert.True(inner.End);
        Assert.False(inner.TryReadTag(out _, out _));

        // The outer reader resumed exactly after the nested body.
        Assert.True(outer.TryReadTag(out fieldNumber, out _));
        Assert.Equal(2, fieldNumber);
        Assert.Equal(33U, outer.ReadVarint32());
        Assert.True(outer.End);
    }

    // -----------------------------------------------------------------------------------------
    // SkipField
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProtoWireType.StartGroup)]
    [InlineData(ProtoWireType.EndGroup)]
    public void SkipField_rejects_groups(ProtoWireType wireType)
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            reader.SkipField(wireType);
        });
    }

    [Theory]
    [InlineData((ProtoWireType)6)]
    [InlineData((ProtoWireType)7)]
    [InlineData((ProtoWireType)255)]
    public void SkipField_rejects_an_undefined_wire_type(ProtoWireType wireType)
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            reader.SkipField(wireType);
        });
    }

    [Fact]
    public void SkipField_rejects_a_truncated_fixed_payload()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x01, 0x02, 0x03 });
            reader.SkipField(ProtoWireType.Fixed32);
        });

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            reader.SkipField(ProtoWireType.Fixed64);
        });
    }

    [Fact]
    public void SkipField_rejects_a_truncated_length_delimited_payload()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(new byte[] { 0x10, 0x00, 0x00 });
            reader.SkipField(ProtoWireType.LengthDelimited);
        });
    }

    [Fact]
    public void SkipField_advances_exactly_the_payload_width()
    {
        byte[] data = new byte[16];

        ProtoReader fixed32 = new ProtoReader(data);
        fixed32.SkipField(ProtoWireType.Fixed32);
        Assert.Equal(4, fixed32.Position);

        ProtoReader fixed64 = new ProtoReader(data);
        fixed64.SkipField(ProtoWireType.Fixed64);
        Assert.Equal(8, fixed64.Position);

        ProtoReader varint = new ProtoReader(ProtoTestHelpers.Varint(ulong.MaxValue));
        varint.SkipField(ProtoWireType.Varint);
        Assert.Equal(10, varint.Position);
    }
}
