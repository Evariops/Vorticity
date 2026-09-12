using System;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// proto3 presence rules and buffer management. Both presence behaviours are used by real
/// messages: <c>Extension.metadata</c> in spec/proto/dtype.proto is <c>optional bytes</c> and
/// every arm of <c>ScalarValue.kind</c> in spec/proto/scalar.proto is a <c>oneof</c> member, where
/// "present and zero" must survive the round trip.
/// </summary>
public sealed class ProtoWriterTests
{
    // -----------------------------------------------------------------------------------------
    // Implicit presence: the default value is omitted
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Default_valued_fields_are_omitted()
    {
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBool(1, false)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt32(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt64(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt32(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt64(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt32(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteSInt64(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteEnum(1, 0)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloat(1, 0f)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteDouble(1, 0d)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBytes(1, default)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteStringUtf8(1, default)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteString(1, string.Empty)));
    }

    [Fact]
    public void Non_default_values_are_written()
    {
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBool(1, true)));
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt32(1, -1)));
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt64(1, 1)));
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloat(1, float.NaN)));
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBytes(1, new byte[] { 0 })));
        Assert.NotEmpty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteString(1, "x")));
    }

    // -----------------------------------------------------------------------------------------
    // Explicit presence: the ...Always variants
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Always_variants_emit_the_field_at_its_default_value()
    {
        Assert.Equal(new byte[] { 0x08, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBoolAlways(1, false)));
        Assert.Equal(new byte[] { 0x08, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt32Always(1, 0)));
        Assert.Equal(new byte[] { 0x08, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt64Always(1, 0)));
        Assert.Equal(new byte[] { 0x08, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt32Always(1, 0)));
        Assert.Equal(new byte[] { 0x08, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteEnumAlways(1, 0)));
        Assert.Equal(new byte[] { 0x0A, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteBytesAlways(1, default)));
    }

    /// <summary>
    /// A <c>oneof</c> member that happens to hold zero is present, not absent, so the round trip
    /// has to distinguish "wrote zero" from "wrote nothing".
    /// </summary>
    [Fact]
    public void A_zero_valued_Always_field_reads_back_as_present()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt64Always(4, 0));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(4, fieldNumber);
        Assert.Equal(ProtoWireType.Varint, wireType);
        Assert.Equal(0UL, reader.ReadVarint());
        Assert.True(reader.End);
    }

    /// <summary>
    /// Negative zero equals the proto3 default under <c>==</c>, so the implicit-presence writer
    /// drops it - matching every conforming encoder. The Always variant is the escape hatch that
    /// keeps the sign bit, which is what a <c>ScalarValue.f32_value</c> needs.
    /// </summary>
    [Fact]
    public void Negative_zero_is_omitted_by_WriteFloat_and_preserved_by_WriteFloatAlways()
    {
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloat(1, -0f)));
        Assert.Empty(ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteDouble(1, -0d)));

        byte[] single = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteFloatAlways(1, -0f));
        ProtoReader reader = new ProtoReader(single);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(0x8000_0000U, BitConverter.SingleToUInt32Bits(reader.ReadFloat()));

        byte[] twin = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteDoubleAlways(1, -0d));
        ProtoReader wide = new ProtoReader(twin);
        Assert.True(wide.TryReadTag(out _, out _));
        Assert.Equal(0x8000_0000_0000_0000UL, BitConverter.DoubleToUInt64Bits(wide.ReadDouble()));
    }

    // -----------------------------------------------------------------------------------------
    // Encoding shape
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_int32_is_sign_extended_to_ten_bytes(int value)
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteInt32(1, value));

        Assert.Equal(11, data.Length);   // one tag byte + ten varint bytes
        Assert.Equal(0x01, data[10]);    // the tenth varint byte carries only bit 63

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(value, reader.ReadInt32());
    }

    [Fact]
    public void A_negative_enum_is_sign_extended_like_an_int32()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteEnum(1, -3));
        Assert.Equal(11, data.Length);

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out _));
        Assert.Equal(-3, reader.ReadInt32());
    }

    [Fact]
    public void Tags_pack_the_field_number_and_wire_type()
    {
        Assert.Equal(new byte[] { 0x08 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteTag(1, ProtoWireType.Varint)));
        Assert.Equal(new byte[] { 0x0D }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteTag(1, ProtoWireType.Fixed32)));
        Assert.Equal(new byte[] { 0x11 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteTag(2, ProtoWireType.Fixed64)));
        Assert.Equal(new byte[] { 0x1A }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteTag(3, ProtoWireType.LengthDelimited)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1 << 29)]
    [InlineData(int.MaxValue)]
    public void WriteTag_rejects_an_illegal_field_number(int fieldNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            ProtoWriter writer = new ProtoWriter(16);
            try
            {
                writer.WriteTag(fieldNumber, ProtoWireType.Varint);
            }
            finally
            {
                writer.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(ProtoWireType.StartGroup)]
    [InlineData(ProtoWireType.EndGroup)]
    [InlineData((ProtoWireType)6)]
    public void WriteTag_refuses_to_emit_a_framing_the_reader_would_reject(ProtoWireType wireType)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            ProtoWriter writer = new ProtoWriter(16);
            try
            {
                writer.WriteTag(1, wireType);
            }
            finally
            {
                writer.Dispose();
            }
        });
    }

    [Fact]
    public void WriteTag_accepts_the_largest_legal_field_number()
    {
        const int MaxFieldNumber = (1 << 29) - 1;
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteTag(MaxFieldNumber, ProtoWireType.Varint));

        ProtoReader reader = new ProtoReader(ProtoTestHelpers.Concat(data, new byte[] { 0x00 }));
        Assert.True(reader.TryReadTag(out int fieldNumber, out _));
        Assert.Equal(MaxFieldNumber, fieldNumber);
    }

    // -----------------------------------------------------------------------------------------
    // Strings
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a")]
    [InlineData("hello")]
    [InlineData("héllo")]                 // two-byte UTF-8
    [InlineData("世界")]               // three-byte UTF-8
    [InlineData("🌍")]               // four-byte UTF-8 (surrogate pair)
    [InlineData("vortex.date")]                // a real extension dtype id
    public void Strings_are_written_as_UTF8_with_an_exact_length_prefix(string value)
    {
        byte[] expected = Encoding.UTF8.GetBytes(value);
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteString(1, value));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out ProtoWireType wireType));
        Assert.Equal(ProtoWireType.LengthDelimited, wireType);
        Assert.True(reader.ReadLengthDelimited().SequenceEqual(expected));
        Assert.True(reader.End);
    }

    [Fact]
    public void WriteString_and_WriteStringUtf8_agree()
    {
        const string Value = "struct{a: i32}";
        byte[] fromString = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteString(7, Value));
        byte[] fromUtf8 = ProtoTestHelpers.Write(
            (ref ProtoWriter w) => w.WriteStringUtf8(7, Encoding.UTF8.GetBytes(Value)));

        Assert.Equal(fromString, fromUtf8);
    }

    /// <summary>
    /// An unpaired surrogate cannot be encoded; the transcoder substitutes U+FFFD. The point of
    /// the test is that <c>GetByteCount</c> and <c>GetBytes</c> substitute identically, so the
    /// length prefix can never disagree with the bytes that follow it.
    /// </summary>
    [Fact]
    public void An_unpaired_surrogate_still_produces_a_self_consistent_length_prefix()
    {
        string broken = "a\ud800b";
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteString(1, broken));

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out _, out _));
        ReadOnlySpan<byte> payload = reader.ReadLengthDelimited();
        Assert.True(reader.End);
        Assert.Equal(Encoding.UTF8.GetByteCount(broken), payload.Length);
    }

    [Fact]
    public void WriteStringAlways_emits_an_empty_string()
    {
        Assert.Equal(new byte[] { 0x0A, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteStringAlways(1, string.Empty)));
        Assert.Equal(new byte[] { 0x0A, 0x00 }, ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteStringUtf8Always(1, default)));
    }

    // -----------------------------------------------------------------------------------------
    // Buffer management
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_default_writer_rents_lazily_and_still_writes()
    {
        ProtoWriter writer = default;
        try
        {
            Assert.Equal(0, writer.Length);
            Assert.True(writer.WrittenSpan.IsEmpty);

            writer.WriteUInt64Always(1, ulong.MaxValue);
            Assert.Equal(11, writer.Length);

            ProtoReader reader = new ProtoReader(writer.WrittenSpan);
            Assert.True(reader.TryReadTag(out int fieldNumber, out _));
            Assert.Equal(1, fieldNumber);
            Assert.Equal(ulong.MaxValue, reader.ReadVarint());
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void A_zero_capacity_writer_grows_on_demand()
    {
        ProtoWriter writer = new ProtoWriter(0);
        try
        {
            writer.WriteBytesAlways(1, ProtoTestHelpers.Payload(4096));
            Assert.True(writer.Length > 4096);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Growth_preserves_everything_written_so_far()
    {
        const int Fields = 4000;

        ProtoWriter writer = new ProtoWriter(8);
        try
        {
            for (int i = 0; i < Fields; i++)
            {
                writer.WriteUInt64Always(1, (ulong)i * 0x0101_0101UL);
            }

            ProtoReader reader = new ProtoReader(writer.WrittenSpan);
            for (int i = 0; i < Fields; i++)
            {
                Assert.True(reader.TryReadTag(out int fieldNumber, out _));
                Assert.Equal(1, fieldNumber);
                Assert.Equal((ulong)i * 0x0101_0101UL, reader.ReadVarint());
            }

            Assert.True(reader.End);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Clear_rewinds_without_releasing_the_buffer()
    {
        ProtoWriter writer = new ProtoWriter(32);
        try
        {
            writer.WriteUInt32Always(1, 5);
            Assert.Equal(2, writer.Length);

            writer.Clear();
            Assert.Equal(0, writer.Length);
            Assert.True(writer.WrittenSpan.IsEmpty);

            writer.WriteUInt32Always(2, 6);
            Assert.Equal(new byte[] { 0x10, 0x06 }, writer.WrittenSpan.ToArray());
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Dispose_is_idempotent_and_leaves_an_empty_writer()
    {
        ProtoWriter writer = new ProtoWriter(32);
        writer.WriteUInt32Always(1, 5);

        writer.Dispose();
        Assert.Equal(0, writer.Length);
        Assert.True(writer.WrittenSpan.IsEmpty);

        writer.Dispose();
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void A_disposed_writer_can_be_written_to_again_after_renting_lazily()
    {
        ProtoWriter writer = new ProtoWriter(32);
        writer.WriteUInt32Always(1, 5);
        writer.Dispose();

        try
        {
            writer.WriteUInt32Always(2, 6);
            Assert.Equal(new byte[] { 0x10, 0x06 }, writer.WrittenSpan.ToArray());
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void WriteRawBytes_appends_without_a_tag_or_a_length()
    {
        byte[] payload = ProtoTestHelpers.Payload(9);
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteRawBytes(default);
            w.WriteRawBytes(payload);
        });

        Assert.Equal(payload, data);
    }

    [Fact]
    public void A_negative_initial_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            ProtoWriter writer = new ProtoWriter(-1);
            writer.Dispose();
        });
    }

    /// <summary>
    /// End-to-end shape check against a message the format actually defines: <c>DictMetadata</c>
    /// from docs/02-format.md §5.3 - <c>uint32 values_len = 1; PType codes_ptype = 2;
    /// optional bool is_nullable_codes = 3;</c>. The optional field is written even when false.
    /// </summary>
    [Fact]
    public void A_realistic_metadata_message_round_trips()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32(1, 1024);            // values_len
            w.WriteEnum(2, 6);                 // codes_ptype = I32
            w.WriteBoolAlways(3, false);       // optional bool, explicitly present
        });

        uint valuesLen = 0;
        int codesPType = -1;
        bool sawNullableCodes = false;
        bool nullableCodes = true;

        ProtoReader reader = new ProtoReader(body);
        while (reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    valuesLen = reader.ReadVarint32();
                    break;
                case 2:
                    codesPType = reader.ReadInt32();
                    break;
                case 3:
                    nullableCodes = reader.ReadBool();
                    sawNullableCodes = true;
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        Assert.Equal(1024U, valuesLen);
        Assert.Equal(6, codesPType);
        Assert.True(sawNullableCodes);
        Assert.False(nullableCodes);
    }
}
