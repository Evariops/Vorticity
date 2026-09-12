using System;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// ZigZag (<c>sint32</c>/<c>sint64</c>). <c>vortex.scalar.ScalarValue.int64_value</c> is a
/// <c>sint64</c> (spec/proto/scalar.proto), so every integral scalar in a Vortex file passes
/// through this transform - including the extremes, where the shift overflows by design.
/// </summary>
public sealed class ProtoZigZagTests
{
    [Theory]
    [InlineData(0L, 0UL)]
    [InlineData(-1L, 1UL)]
    [InlineData(1L, 2UL)]
    [InlineData(-2L, 3UL)]
    [InlineData(2L, 4UL)]
    [InlineData(2147483647L, 4294967294UL)]
    [InlineData(-2147483648L, 4294967295UL)]
    [InlineData(long.MaxValue, ulong.MaxValue - 1)]
    [InlineData(long.MinValue, ulong.MaxValue)]
    public void SInt64_uses_the_documented_zigzag_mapping(long value, ulong expectedRaw)
    {
        byte[] encoded = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt64Always(1, value));

        // Field 1, wire type 0.
        Assert.Equal(0x08, encoded[0]);

        ProtoReader raw = new ProtoReader(encoded.AsSpan(1).ToArray());
        Assert.Equal(expectedRaw, raw.ReadVarint());

        ProtoReader reader = new ProtoReader(encoded);
        Assert.True(reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(1, fieldNumber);
        Assert.Equal(ProtoWireType.Varint, wireType);
        Assert.Equal(value, reader.ReadSInt64());
        Assert.True(reader.End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(63)]
    [InlineData(-64)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void SInt32_round_trips_including_int_MinValue(int value)
    {
        byte[] encoded = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt32Always(1, value));

        ProtoReader reader = new ProtoReader(encoded);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(value, reader.ReadSInt32());
        Assert.True(reader.End);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void SInt64_round_trips_including_long_MinValue(long value)
    {
        byte[] encoded = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt64Always(1, value));

        ProtoReader reader = new ProtoReader(encoded);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(value, reader.ReadSInt64());
        Assert.True(reader.End);
    }

    /// <summary>
    /// A small magnitude must stay small on the wire; that is the entire reason sint64 exists.
    /// Compare against the plain int64 encoding of the same value.
    /// </summary>
    [Fact]
    public void SInt64_keeps_small_negatives_short_where_int64_needs_ten_bytes()
    {
        byte[] zigzag = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt64Always(1, -1));
        byte[] twosComplement = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt64Always(1, -1));

        Assert.Equal(2, zigzag.Length);          // tag + one byte
        Assert.Equal(11, twosComplement.Length); // tag + ten bytes
    }

    /// <summary>
    /// ZigZag is defined over the raw 32-bit group: a sint32 field whose varint carries junk in
    /// the high 32 bits decodes from the low half, exactly as a conforming decoder does.
    /// </summary>
    [Fact]
    public void SInt32_decodes_from_the_low_32_bits()
    {
        ProtoReader reader = new ProtoReader(ProtoTestHelpers.Varint(0xFFFF_FFFF_FFFF_FFFFUL));
        Assert.Equal(int.MinValue, reader.ReadSInt32());
    }
}
