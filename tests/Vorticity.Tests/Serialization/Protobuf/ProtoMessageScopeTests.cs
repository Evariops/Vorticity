using System;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// Nested-message backpatching. One byte is reserved for the length varint before the body is
/// written, so the interesting cases are exactly the ones where the finished body needs a wider
/// prefix and the body has to shift right - including when that happens inside another scope.
/// </summary>
public sealed class ProtoMessageScopeTests
{
    /// <summary>Independent re-implementation of the length-prefix width.</summary>
    private static int VarintSize(int value) => ProtoTestHelpers.ExpectedVarintSize((ulong)value);

    /// <summary>
    /// Builds <c>field 1 { field 1: bytes(payloadLength) }</c> and returns the encoding.
    /// The inner body is <c>1 tag byte + VarintSize(payloadLength) + payloadLength</c>, which is
    /// what walks across the 127/128 and 16383/16384 prefix boundaries.
    /// </summary>
    private static byte[] BuildNested(int payloadLength)
    {
        byte[] payload = ProtoTestHelpers.Payload(payloadLength);
        return ProtoTestHelpers.Write(
            (ref ProtoWriter w) =>
            {
                using (ProtoWriter.MessageScope scope = w.BeginMessage(1))
                {
                    w.WriteBytesAlways(1, payload);
                }
            },
            initialCapacity: 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(124)]     // body 126 -> one-byte prefix
    [InlineData(125)]     // body 127 -> one-byte prefix, the last that fits
    [InlineData(126)]     // body 128 -> two-byte prefix, the shift kicks in
    [InlineData(127)]
    [InlineData(200)]
    [InlineData(16379)]   // body 16382 -> two-byte prefix
    [InlineData(16380)]   // body 16383 -> two-byte prefix, the last that fits
    [InlineData(16381)]   // body 16384 -> three-byte prefix
    [InlineData(40000)]
    public void A_nested_message_backpatches_the_right_prefix_width(int payloadLength)
    {
        byte[] payload = ProtoTestHelpers.Payload(payloadLength);
        byte[] data = BuildNested(payloadLength);

        int bodyLength = 1 + VarintSize(payloadLength) + payloadLength;
        int expectedTotal = 1 + VarintSize(bodyLength) + bodyLength;
        Assert.Equal(expectedTotal, data.Length);

        ProtoReader outer = new ProtoReader(data);
        Assert.True(outer.TryReadTag(out int fieldNumber, out ProtoWireType wireType));
        Assert.Equal(1, fieldNumber);
        Assert.Equal(ProtoWireType.LengthDelimited, wireType);

        ProtoReader inner = outer.ReadMessage();
        Assert.True(outer.End);

        Assert.Equal(bodyLength, inner.Length);
        Assert.True(inner.TryReadTag(out int innerField, out _));
        Assert.Equal(1, innerField);
        Assert.True(inner.ReadLengthDelimited().SequenceEqual(payload));
        Assert.True(inner.End);
    }

    [Fact]
    public void An_empty_nested_message_is_a_tag_and_a_zero_length()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            using (ProtoWriter.MessageScope scope = w.BeginMessage(3))
            {
            }
        });

        Assert.Equal(new byte[] { 0x1A, 0x00 }, data);

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int fieldNumber, out _));
        Assert.Equal(3, fieldNumber);
        Assert.Equal(0, reader.ReadMessage().Length);
        Assert.True(reader.End);
    }

    /// <summary>
    /// Three levels deep, with the innermost body forced past 127 bytes so its shift happens
    /// while two enclosing reserved slots are still open. Their prefixes must be backpatched from
    /// the shifted lengths, not the pre-shift ones.
    /// </summary>
    [Fact]
    public void Three_deep_nesting_round_trips_when_the_innermost_body_forces_a_shift()
    {
        byte[] payload = ProtoTestHelpers.Payload(300);

        byte[] data = ProtoTestHelpers.Write(
            (ref ProtoWriter w) =>
            {
                w.WriteUInt32Always(1, 111);
                using (ProtoWriter.MessageScope level1 = w.BeginMessage(2))
                {
                    w.WriteUInt32Always(1, 222);
                    using (ProtoWriter.MessageScope level2 = w.BeginMessage(2))
                    {
                        w.WriteUInt32Always(1, 333);
                        using (ProtoWriter.MessageScope level3 = w.BeginMessage(2))
                        {
                            w.WriteBytesAlways(1, payload);
                        }

                        w.WriteUInt32Always(3, 444);
                    }

                    w.WriteUInt32Always(3, 555);
                }

                w.WriteUInt32Always(3, 666);
            },
            initialCapacity: 8);

        ProtoReader root = new ProtoReader(data);

        Assert.True(root.TryReadTag(out int field, out _));
        Assert.Equal(1, field);
        Assert.Equal(111U, root.ReadVarint32());

        Assert.True(root.TryReadTag(out field, out _));
        Assert.Equal(2, field);
        ProtoReader level1Reader = root.ReadMessage();

        Assert.True(root.TryReadTag(out field, out _));
        Assert.Equal(3, field);
        Assert.Equal(666U, root.ReadVarint32());
        Assert.True(root.End);

        Assert.True(level1Reader.TryReadTag(out field, out _));
        Assert.Equal(1, field);
        Assert.Equal(222U, level1Reader.ReadVarint32());
        Assert.True(level1Reader.TryReadTag(out field, out _));
        Assert.Equal(2, field);
        ProtoReader level2Reader = level1Reader.ReadMessage();
        Assert.True(level1Reader.TryReadTag(out field, out _));
        Assert.Equal(3, field);
        Assert.Equal(555U, level1Reader.ReadVarint32());
        Assert.True(level1Reader.End);

        Assert.True(level2Reader.TryReadTag(out field, out _));
        Assert.Equal(1, field);
        Assert.Equal(333U, level2Reader.ReadVarint32());
        Assert.True(level2Reader.TryReadTag(out field, out _));
        Assert.Equal(2, field);
        ProtoReader level3Reader = level2Reader.ReadMessage();
        Assert.True(level2Reader.TryReadTag(out field, out _));
        Assert.Equal(3, field);
        Assert.Equal(444U, level2Reader.ReadVarint32());
        Assert.True(level2Reader.End);

        Assert.True(level3Reader.TryReadTag(out field, out _));
        Assert.Equal(1, field);
        Assert.True(level3Reader.ReadLengthDelimited().SequenceEqual(payload));
        Assert.True(level3Reader.End);
    }

    [Fact]
    public void Sibling_scopes_at_the_same_depth_are_independent()
    {
        byte[] first = ProtoTestHelpers.Payload(150);
        byte[] second = ProtoTestHelpers.Payload(3);

        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            using (ProtoWriter.MessageScope a = w.BeginMessage(1))
            {
                w.WriteBytesAlways(1, first);
            }

            using (ProtoWriter.MessageScope b = w.BeginMessage(1))
            {
                w.WriteBytesAlways(1, second);
            }
        });

        ProtoReader reader = new ProtoReader(data);

        Assert.True(reader.TryReadTag(out _, out _));
        ProtoReader firstReader = reader.ReadMessage();
        Assert.True(firstReader.TryReadTag(out _, out _));
        Assert.True(firstReader.ReadLengthDelimited().SequenceEqual(first));
        Assert.True(firstReader.End);

        Assert.True(reader.TryReadTag(out _, out _));
        ProtoReader secondReader = reader.ReadMessage();
        Assert.True(secondReader.TryReadTag(out _, out _));
        Assert.True(secondReader.ReadLengthDelimited().SequenceEqual(second));
        Assert.True(secondReader.End);

        Assert.True(reader.End);
    }

    [Fact]
    public void Fields_written_after_a_scope_closes_land_outside_it()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            using (ProtoWriter.MessageScope scope = w.BeginMessage(1))
            {
                w.WriteUInt32Always(1, 1);
            }

            w.WriteUInt32Always(2, 2);
        });

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int field, out _));
        Assert.Equal(1, field);
        ProtoReader inner = reader.ReadMessage();
        Assert.Equal(2, inner.Length);

        Assert.True(reader.TryReadTag(out field, out _));
        Assert.Equal(2, field);
        Assert.Equal(2U, reader.ReadVarint32());
        Assert.True(reader.End);
    }

    [Fact]
    public void End_is_idempotent()
    {
        byte[] data = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            ProtoWriter.MessageScope scope = w.BeginMessage(1);
            w.WriteUInt32Always(1, 9);
            scope.End();
            scope.End();
            scope.Dispose();
        });

        Assert.Equal(new byte[] { 0x0A, 0x02, 0x08, 0x09 }, data);
    }

    [Fact]
    public void A_default_scope_does_nothing_when_disposed()
    {
        ProtoWriter.MessageScope scope = default;
        scope.Dispose();
        scope.End();
    }

    /// <summary>
    /// Closing an enclosing scope before one it contains would backpatch the wrong length. That is
    /// a programming error in the caller, not malformed input, so it is an
    /// <see cref="InvalidOperationException"/> rather than a <c>VortexFormatException</c>.
    /// </summary>
    [Fact]
    public void Closing_scopes_out_of_order_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            ProtoWriter writer = new ProtoWriter(64);
            try
            {
                ProtoWriter.MessageScope outer = writer.BeginMessage(1);
                ProtoWriter.MessageScope inner = writer.BeginMessage(2);
                writer.WriteUInt32Always(1, 1);

                outer.End();   // the inner scope is still open
                inner.End();
            }
            finally
            {
                writer.Dispose();
            }
        });
    }

    [Fact]
    public void Closing_a_scope_after_Clear_is_rejected_rather_than_corrupting_the_buffer()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            ProtoWriter writer = new ProtoWriter(64);
            try
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(1);
                writer.WriteUInt32Always(1, 1);
                writer.Clear();
                scope.End();
            }
            finally
            {
                writer.Dispose();
            }
        });
    }

    /// <summary>
    /// The scope holds a <c>ref</c> to the writer, so a body that outgrows the pooled array and
    /// forces a reallocation must still backpatch into the array the writer ended up with.
    /// </summary>
    [Fact]
    public void A_body_that_reallocates_the_pooled_array_still_backpatches_correctly()
    {
        const int Fields = 3000;

        byte[] data = ProtoTestHelpers.Write(
            (ref ProtoWriter w) =>
            {
                using (ProtoWriter.MessageScope scope = w.BeginMessage(1))
                {
                    for (int i = 0; i < Fields; i++)
                    {
                        w.WriteUInt32Always(1, (uint)i);
                    }
                }
            },
            initialCapacity: 4);

        ProtoReader reader = new ProtoReader(data);
        Assert.True(reader.TryReadTag(out int field, out _));
        Assert.Equal(1, field);

        ProtoReader inner = reader.ReadMessage();
        Assert.True(reader.End);

        for (int i = 0; i < Fields; i++)
        {
            Assert.True(inner.TryReadTag(out int innerField, out _));
            Assert.Equal(1, innerField);
            Assert.Equal((uint)i, inner.ReadVarint32());
        }

        Assert.True(inner.End);
    }
}
