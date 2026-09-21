using System;
using Vorticity;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// The read-forever guarantee for metadata: a per-encoding metadata message may grow
/// an <c>optional</c> field upstream without minting a new encoding id, so a reader that rejected
/// an unknown field number would fail on a perfectly legal file. Unknown numbers are skipped by
/// wire type; only values outside the wire format's own domain are refused.
/// </summary>
public sealed class ProtoUnknownFieldTests
{
    /// <summary>
    /// A stand-in for one of the real metadata messages - shaped like
    /// <c>fastlanes.bitpacked</c>'s <c>BitPackedMetadata { uint32 bit_width = 1; uint32 offset = 2; }</c>
    /// - parsed the way every metadata decoder will parse: known numbers
    /// handled, everything else skipped.
    /// </summary>
    private static (uint BitWidth, uint Offset, int SkippedFields) Parse(ReadOnlySpan<byte> body)
    {
        uint bitWidth = 0;
        uint offset = 0;
        int skipped = 0;

        ProtoReader reader = new ProtoReader(body);
        while (reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType))
        {
            switch (fieldNumber)
            {
                case 1 when wireType == ProtoWireType.Varint:
                    bitWidth = reader.ReadVarint32();
                    break;
                case 2 when wireType == ProtoWireType.Varint:
                    offset = reader.ReadVarint32();
                    break;
                default:
                    reader.SkipField(wireType);
                    skipped++;
                    break;
            }
        }

        return (bitWidth, offset, skipped);
    }

    /// <summary>
    /// The proof: one unknown field of <em>every</em> wire type, interleaved between two known
    /// fields. Both known fields must still parse with the right values.
    /// </summary>
    [Fact]
    public void Unknown_fields_of_every_wire_type_are_skipped_between_two_known_fields()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32Always(1, 13);                       // known: bit_width

            w.WriteUInt64Always(100, ulong.MaxValue);         // unknown varint (10 bytes)
            w.WriteFloatAlways(101, 3.5f);                    // unknown fixed32
            w.WriteDoubleAlways(102, -1.25d);                 // unknown fixed64
            w.WriteBytesAlways(103, ProtoTestHelpers.Payload(37));  // unknown length-delimited

            w.WriteUInt32Always(2, 900);                      // known: offset
        });

        (uint bitWidth, uint offset, int skipped) = Parse(body);

        Assert.Equal(13U, bitWidth);
        Assert.Equal(900U, offset);
        Assert.Equal(4, skipped);
    }

    [Fact]
    public void Unknown_fields_are_skipped_when_they_come_first_and_last()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteBytesAlways(50, ProtoTestHelpers.Payload(3));
            w.WriteUInt32Always(1, 7);
            w.WriteUInt32Always(2, 8);
            w.WriteUInt64Always(51, 0);            // a trailing unknown at its default value
        });

        (uint bitWidth, uint offset, int skipped) = Parse(body);

        Assert.Equal(7U, bitWidth);
        Assert.Equal(8U, offset);
        Assert.Equal(2, skipped);
    }

    /// <summary>
    /// A future upstream field could itself be a nested message. Skipping it must consume the
    /// whole sub-message, not descend into it.
    /// </summary>
    [Fact]
    public void An_unknown_nested_message_is_skipped_whole()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32Always(1, 5);

            using (ProtoWriter.MessageScope scope = w.BeginMessage(77))
            {
                // Deliberately reuses field numbers 1 and 2 inside: a descend-instead-of-skip bug
                // would corrupt bit_width and offset with these values.
                w.WriteUInt32Always(1, 4242);
                w.WriteUInt32Always(2, 9999);
                w.WriteBytesAlways(3, ProtoTestHelpers.Payload(200));
            }

            w.WriteUInt32Always(2, 6);
        });

        (uint bitWidth, uint offset, int skipped) = Parse(body);

        Assert.Equal(5U, bitWidth);
        Assert.Equal(6U, offset);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void An_unknown_field_with_the_largest_legal_number_is_skipped()
    {
        const int MaxFieldNumber = (1 << 29) - 1;

        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32Always(1, 1);
            w.WriteBytesAlways(MaxFieldNumber, ProtoTestHelpers.Payload(9));
            w.WriteUInt32Always(2, 2);
        });

        (uint bitWidth, uint offset, int skipped) = Parse(body);

        Assert.Equal(1U, bitWidth);
        Assert.Equal(2U, offset);
        Assert.Equal(1, skipped);
    }

    /// <summary>
    /// The tolerate/reject line, drawn on one input: the unknown <em>number</em> would be fine,
    /// but group <em>framing</em> is outside the wire format proto3 defines, so the message is
    /// malformed rather than merely unrecognized.
    /// </summary>
    [Fact]
    public void An_unknown_field_using_group_framing_is_still_rejected()
    {
        byte[] body = ProtoTestHelpers.Concat(
            ProtoTestHelpers.Write((ref ProtoWriter w) => w.WriteUInt32Always(1, 1)),
            ProtoTestHelpers.RawTag(200, 3),
            new byte[] { 0x00 });

        Assert.Throws<VortexFormatException>(() => { Parse(body); });
    }

    /// <summary>
    /// A field number that is known but arrives with unexpected framing falls through to the skip
    /// path rather than being mis-decoded. That keeps a future re-typing of a field from producing
    /// a silently wrong value.
    /// </summary>
    [Fact]
    public void A_known_number_with_unexpected_framing_falls_through_to_skip()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteBytesAlways(1, ProtoTestHelpers.Payload(4));  // field 1 as bytes, not varint
            w.WriteUInt32Always(2, 77);
        });

        (uint bitWidth, uint offset, int skipped) = Parse(body);

        Assert.Equal(0U, bitWidth);
        Assert.Equal(77U, offset);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void A_long_run_of_unknown_fields_terminates_and_leaves_the_reader_at_the_end()
    {
        const int Count = 500;

        byte[] body = ProtoTestHelpers.Write(
            (ref ProtoWriter w) =>
            {
                for (int i = 0; i < Count; i++)
                {
                    w.WriteUInt64Always(1000 + i, (ulong)i);
                }
            },
            initialCapacity: 16);

        ProtoReader reader = new ProtoReader(body);
        int seen = 0;
        while (reader.TryReadTag(out _, out ProtoWireType wireType))
        {
            reader.SkipField(wireType);
            seen++;
        }

        Assert.Equal(Count, seen);
        Assert.True(reader.End);
        Assert.Equal(body.Length, reader.Position);
    }

    /// <summary>
    /// Skipping must be exact: a truncated unknown payload is malformed input, not something to
    /// shrug at. Tolerance covers unknown numbers, never a payload that runs off the end.
    /// </summary>
    [Fact]
    public void A_truncated_unknown_field_is_rejected_rather_than_ignored()
    {
        byte[] complete = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32Always(1, 3);
            w.WriteBytesAlways(99, ProtoTestHelpers.Payload(20));
            w.WriteUInt32Always(2, 4);
        });

        byte[] truncated = complete.AsSpan(0, complete.Length - 8).ToArray();

        Assert.Throws<VortexFormatException>(() => { Parse(truncated); });
    }
}
