using System;
using Vorticity;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// Varint decoding: the single primitive every other proto3 read is built on, so its malformed
/// cases are the ones a hostile file will reach for first.
/// </summary>
public sealed class ProtoVarintTests
{
    // The ten-byte encoding of ulong.MaxValue: nine 0xFF groups plus the single surviving bit 63.
    private static readonly byte[] UInt64MaxEncoding =
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]              // last one-byte value
    [InlineData(128UL)]              // first two-byte value
    [InlineData(300UL)]
    [InlineData(16383UL)]            // last two-byte value
    [InlineData(16384UL)]            // first three-byte value
    [InlineData(2097151UL)]
    [InlineData(2097152UL)]
    [InlineData(268435455UL)]
    [InlineData(268435456UL)]
    [InlineData(uint.MaxValue)]
    [InlineData((ulong)long.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void Varint_round_trips_at_every_width_boundary(ulong value)
    {
        byte[] encoded = ProtoTestHelpers.Varint(value);

        Assert.Equal(ProtoTestHelpers.ExpectedVarintSize(value), encoded.Length);

        ProtoReader reader = new ProtoReader(encoded);
        Assert.Equal(value, reader.ReadVarint());
        Assert.True(reader.End);
        Assert.Equal(encoded.Length, reader.Position);
    }

    [Fact]
    public void Varint_encoding_matches_the_wire_format_by_hand()
    {
        Assert.Equal(new byte[] { 0x00 }, ProtoTestHelpers.Varint(0));
        Assert.Equal(new byte[] { 0x7F }, ProtoTestHelpers.Varint(127));
        Assert.Equal(new byte[] { 0x80, 0x01 }, ProtoTestHelpers.Varint(128));
        Assert.Equal(new byte[] { 0xAC, 0x02 }, ProtoTestHelpers.Varint(300));
        Assert.Equal(new byte[] { 0xFF, 0x7F }, ProtoTestHelpers.Varint(16383));
        Assert.Equal(new byte[] { 0x80, 0x80, 0x01 }, ProtoTestHelpers.Varint(16384));
        Assert.Equal(UInt64MaxEncoding, ProtoTestHelpers.Varint(ulong.MaxValue));
    }

    [Fact]
    public void Varint_tenth_byte_of_one_is_accepted()
    {
        ProtoReader reader = new ProtoReader(UInt64MaxEncoding);
        Assert.Equal(ulong.MaxValue, reader.ReadVarint());
        Assert.Equal(10, reader.Position);
    }

    /// <summary>
    /// Byte ten carries exactly one value bit. Anything else there is either an eleventh-byte
    /// continuation or a value bit above bit 63 - both unrepresentable, therefore malformed.
    /// </summary>
    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x03)]
    [InlineData((byte)0x7F)]
    [InlineData((byte)0x80)]   // continuation set: an 11-byte varint
    [InlineData((byte)0xFF)]
    public void Varint_tenth_byte_beyond_bit_zero_is_rejected(byte tenth)
    {
        byte[] data = (byte[])UInt64MaxEncoding.Clone();
        data[9] = tenth;

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadVarint();
        });
    }

    [Fact]
    public void Varint_of_eleven_bytes_is_rejected()
    {
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadVarint();
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void Varint_truncated_before_its_last_byte_is_rejected(int keep)
    {
        byte[] data = UInt64MaxEncoding.AsSpan(0, keep).ToArray();

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadVarint();
        });
    }

    [Fact]
    public void Varint_on_an_empty_buffer_is_rejected()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(default);
            reader.ReadVarint();
        });
    }

    /// <summary>
    /// Overlong encodings are legal on the wire. Refusing them would buy no safety and would break
    /// the promise to read any legal metadata, from a producer we do not control.
    /// </summary>
    [Fact]
    public void Varint_overlong_but_representable_encoding_is_accepted()
    {
        ProtoReader two = new ProtoReader(new byte[] { 0x80, 0x00 });
        Assert.Equal(0UL, two.ReadVarint());

        // Ten bytes whose tenth is 0x00: every bit up to 62, i.e. long.MaxValue.
        byte[] ten = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        ProtoReader wide = new ProtoReader(ten);
        Assert.Equal((ulong)long.MaxValue, wide.ReadVarint());
        Assert.True(wide.End);
    }

    [Fact]
    public void ReadVarint32_accepts_the_full_unsigned_range()
    {
        ProtoReader reader = new ProtoReader(ProtoTestHelpers.Varint(uint.MaxValue));
        Assert.Equal(uint.MaxValue, reader.ReadVarint32());
    }

    [Theory]
    [InlineData(0x1_0000_0000UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData((ulong)long.MaxValue)]
    public void ReadVarint32_rejects_a_value_that_does_not_fit_in_32_bits(ulong value)
    {
        byte[] data = ProtoTestHelpers.Varint(value);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            reader.ReadVarint32();
        });
    }

    /// <summary>
    /// The contract's sharpest edge: the same ten bytes are a legal <c>int32</c> and an illegal
    /// <c>uint32</c>. ReadInt32 truncates, ReadVarint32 rejects - they are not interchangeable.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-42)]
    [InlineData(int.MinValue)]
    public void Negative_int32_is_ten_bytes_that_ReadInt32_truncates_and_ReadVarint32_rejects(int value)
    {
        byte[] encoded = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteVarint(unchecked((ulong)(long)value)));
        Assert.Equal(10, encoded.Length);

        ProtoReader reader = new ProtoReader(encoded);
        Assert.Equal(value, reader.ReadInt32());
        Assert.True(reader.End);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader strict = new ProtoReader(encoded);
            strict.ReadVarint32();
        });
    }

    [Fact]
    public void ReadInt32_discards_bits_above_32_rather_than_rejecting_them()
    {
        // 0x1_0000_0001 is out of int32 range but a conforming encoder can produce it for an
        // int32 field; the decoder's rule is truncation, not rejection.
        ProtoReader reader = new ProtoReader(ProtoTestHelpers.Varint(0x1_0000_0001UL));
        Assert.Equal(1, reader.ReadInt32());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void ReadInt64_round_trips_two_s_complement(long value)
    {
        byte[] encoded = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteVarint(unchecked((ulong)value)));
        ProtoReader reader = new ProtoReader(encoded);
        Assert.Equal(value, reader.ReadInt64());
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(2UL, true)]
    [InlineData(ulong.MaxValue, true)]
    public void ReadBool_treats_every_non_zero_varint_as_true(ulong raw, bool expected)
    {
        ProtoReader reader = new ProtoReader(ProtoTestHelpers.Varint(raw));
        Assert.Equal(expected, reader.ReadBool());
    }

    [Fact]
    public void Position_tracks_consecutive_varints()
    {
        byte[] data = ProtoTestHelpers.Concat(
            ProtoTestHelpers.Varint(1),
            ProtoTestHelpers.Varint(128),
            ProtoTestHelpers.Varint(ulong.MaxValue));

        ProtoReader reader = new ProtoReader(data);
        Assert.Equal(0, reader.Position);
        Assert.Equal(1UL, reader.ReadVarint());
        Assert.Equal(1, reader.Position);
        Assert.Equal(128UL, reader.ReadVarint());
        Assert.Equal(3, reader.Position);
        Assert.Equal(ulong.MaxValue, reader.ReadVarint());
        Assert.Equal(13, reader.Position);
        Assert.True(reader.End);
        Assert.Equal(0, reader.Remaining);
    }
}
