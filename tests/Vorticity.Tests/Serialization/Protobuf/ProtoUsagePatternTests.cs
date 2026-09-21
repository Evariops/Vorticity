using System;
using Vorticity;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// The call shapes downstream codecs will actually use. <see cref="ProtoWriter"/> is a mutable
/// struct, so "does the documented pattern really mutate the writer the caller holds?" is a
/// correctness question, not a style question.
/// </summary>
public sealed class ProtoUsagePatternTests
{
    /// <summary>
    /// <c>using var</c> declares a read-only local. A read-only local of a non-readonly struct is
    /// still mutated in place (no defensive copy), so this pattern is safe - but it is worth
    /// pinning down, because if it ever stopped being true every writer in the library would
    /// silently emit nothing.
    /// </summary>
    [Fact]
    public void A_using_var_writer_is_mutated_in_place()
    {
        using ProtoWriter writer = new ProtoWriter(32);

        writer.WriteUInt32Always(1, 5);
        writer.WriteUInt32Always(2, 6);

        Assert.Equal(4, writer.Length);
        Assert.Equal(new byte[] { 0x08, 0x05, 0x10, 0x06 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void A_using_var_writer_supports_nested_scopes()
    {
        using ProtoWriter writer = new ProtoWriter(8);

        using (ProtoWriter.MessageScope scope = writer.BeginMessage(1))
        {
            writer.WriteBytesAlways(1, ProtoTestHelpers.Payload(200));
        }

        ProtoReader reader = new ProtoReader(writer.WrittenSpan);
        Assert.True(reader.TryReadTag(out int fieldNumber, out _));
        Assert.Equal(1, fieldNumber);
        Assert.Equal(203, reader.ReadMessage().Length);   // tag + 2-byte length + 200 bytes
        Assert.True(reader.End);
    }

    /// <summary>
    /// The shape the DType and Scalar codecs follow:
    /// <c>Write(ref ProtoWriter writer, ...)</c> writing into a caller-owned writer.
    /// </summary>
    /// <remarks>
    /// Note the plain local plus <c>try</c>/<c>finally</c> rather than <c>using var</c>: C# forbids
    /// passing a <c>using</c> variable as a <c>ref</c> argument (CS1657), so any codec that hands
    /// its writer to a helper has to own the disposal explicitly.
    /// </remarks>
    [Fact]
    public void A_ref_passed_writer_appends_to_the_callers_buffer()
    {
        ProtoWriter writer = new ProtoWriter(32);
        try
        {
            writer.WriteUInt32Always(1, 1);
            WriteNestedBody(ref writer, 2);
            writer.WriteUInt32Always(3, 3);

            ProtoReader reader = new ProtoReader(writer.WrittenSpan);

            Assert.True(reader.TryReadTag(out int field, out _));
            Assert.Equal(1, field);
            Assert.Equal(1U, reader.ReadVarint32());

            Assert.True(reader.TryReadTag(out field, out _));
            Assert.Equal(2, field);
            ProtoReader nested = reader.ReadMessage();
            Assert.True(nested.TryReadTag(out int nestedField, out _));
            Assert.Equal(7, nestedField);
            Assert.Equal(42U, nested.ReadVarint32());
            Assert.True(nested.End);

            Assert.True(reader.TryReadTag(out field, out _));
            Assert.Equal(3, field);
            Assert.Equal(3U, reader.ReadVarint32());
            Assert.True(reader.End);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteNestedBody(ref ProtoWriter writer, int fieldNumber)
    {
        using ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        writer.WriteUInt32Always(7, 42);
    }

    [Fact]
    public void SkipField_rejects_a_truncated_unknown_varint()
    {
        // Field 900 (unknown), varint framing, and a payload that never terminates.
        byte[] body = ProtoTestHelpers.Concat(
            ProtoTestHelpers.RawTag(900, 0),
            new byte[] { 0x80, 0x80 });

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(body);
            Assert.True(reader.TryReadTag(out _, out ProtoWireType wireType));
            reader.SkipField(wireType);
        });
    }

    [Fact]
    public void ReadMessage_rejects_a_nested_body_that_escapes_the_outer_message()
    {
        // Field 1, length-delimited, declares 64 bytes but only 3 follow.
        byte[] body = [0x0A, 0x40, 0x01, 0x02, 0x03];

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(body);
            Assert.True(reader.TryReadTag(out _, out _));
            reader.ReadMessage();
        });
    }

    /// <summary>
    /// A nested message is bounded by its own body: a field inside it cannot reach past the
    /// declared length into the parent's bytes, even when the parent has more to give.
    /// </summary>
    [Fact]
    public void A_nested_field_cannot_read_past_its_own_body_into_the_parent()
    {
        byte[] data = ProtoTestHelpers.Concat(
            new byte[] { 0x0A, 0x02, 0x0A, 0x08 },  // field 1 { field 1: bytes, length 8 } in a 2-byte body
            ProtoTestHelpers.Payload(32));          // plenty of bytes in the parent, out of reach

        ProtoReader probe = new ProtoReader(data);
        Assert.True(probe.TryReadTag(out _, out _));
        Assert.Equal(2, probe.ReadMessage().Length);

        Assert.Throws<VortexFormatException>(() =>
        {
            ProtoReader reader = new ProtoReader(data);
            Assert.True(reader.TryReadTag(out _, out _));
            ProtoReader nested = reader.ReadMessage();
            Assert.True(nested.TryReadTag(out _, out _));
            nested.ReadLengthDelimited();
        });
    }
}
