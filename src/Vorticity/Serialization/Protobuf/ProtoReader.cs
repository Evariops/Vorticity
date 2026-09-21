using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// A forward-only proto3 decoder over a caller-owned span. Allocates nothing, copies nothing:
/// length-delimited payloads are returned as slices of the input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown field numbers are skipped, never rejected.</b> A caller loops on
/// <see cref="TryReadTag"/>, handles the field numbers it knows, and passes everything else to
/// <see cref="SkipField"/>, which dispatches on the wire type alone. This is the read-forever
/// promise: a producer may add an <c>optional</c> field to a per-encoding metadata message without
/// minting a new encoding id, and a reader that refused the unknown number would fail on a
/// perfectly legal file.
/// </para>
/// <para>
/// The complementary rule is just as load-bearing: what is <em>tolerated</em> is an unrecognized
/// field number. What is <em>rejected</em> is a value outside the wire format's domain — a
/// truncated varint, an 11-byte varint, a length that escapes the buffer, a group tag. Every such
/// rejection is a <see cref="VortexFormatException"/> and nothing else.
/// </para>
/// <para>
/// Every read validates against the real remaining length <em>before</em> touching a byte, so a
/// hostile payload can never produce an out-of-range access.
/// </para>
/// </remarks>
internal ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>Creates a reader over the body of one Protobuf message.</summary>
    /// <param name="data">
    /// The message body with no outer tag and no outer length prefix. May be empty: an empty
    /// proto3 message is legal and means "every field has its default value".
    /// </param>
    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>True when every byte of the message body has been consumed.</summary>
    public readonly bool End => _position >= _data.Length;

    /// <summary>Byte offset of the next unread byte, relative to the start of the body.</summary>
    public readonly int Position => _position;

    /// <summary>Total length of the message body in bytes.</summary>
    public readonly int Length => _data.Length;

    /// <summary>Bytes still unread.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>Reads the next varint, or reports that the buffer is exhausted.</summary>
    /// <param name="value">The varint read; zero when the buffer is exhausted.</param>
    /// <returns><see langword="false"/> at the end of the buffer.</returns>
    /// <remarks>
    /// For packed repeated fields, whose elements carry no tags: the reader is handed the field's
    /// length-delimited body and drains it. <see cref="TryReadTag"/> cannot serve, because inside a
    /// packed blob the bytes are values rather than tags and would decode as field numbers.
    /// </remarks>
    public bool TryReadVarint(out ulong value)
    {
        if (_position >= _data.Length)
        {
            value = 0;
            return false;
        }

        value = ReadVarint();
        return true;
    }

    /// <summary>
    /// Reads the next field tag.
    /// </summary>
    /// <param name="fieldNumber">The field number, at least 1 and below 2^29. Zero when the method returns false.</param>
    /// <param name="wireType">How the field's payload is framed.</param>
    /// <returns>False at the end of the message body; true when a tag was read.</returns>
    /// <exception cref="VortexFormatException">
    /// The tag is a malformed or oversized varint, its field number is 0, or its wire type is a
    /// group (3, 4) or reserved (6, 7).
    /// </exception>
    public bool TryReadTag(out int fieldNumber, out ProtoWireType wireType)
    {
        int tagPosition = _position;
        if (tagPosition >= _data.Length)
        {
            fieldNumber = 0;
            wireType = default;
            return false;
        }

        ulong tag = ReadVarint();

        // A tag is a uint32. Enforcing that before the shift below is what keeps `tag >> 3` inside
        // int range, so the narrowing cannot wrap into a negative field number.
        if (tag > uint.MaxValue)
        {
            ProtoThrow.TagExceeds32Bits(tagPosition, tag);
        }

        int wire = (int)(tag & ProtoWire.WireTypeMask);
        int number = (int)(tag >> ProtoWire.TagTypeBits);

        if (number == 0)
        {
            ProtoThrow.FieldNumberZero(tagPosition);
        }

        // Rejected here rather than at SkipField so a *known* field number carrying group framing
        // cannot slip past a caller that dispatches on the number alone.
        if (wire is (int)ProtoWireType.StartGroup or (int)ProtoWireType.EndGroup)
        {
            ProtoThrow.GroupWireType(tagPosition, wire);
        }

        if (wire > (int)ProtoWireType.Fixed32)
        {
            ProtoThrow.ReservedWireType(tagPosition, wire);
        }

        fieldNumber = number;
        wireType = (ProtoWireType)wire;
        return true;
    }

    /// <summary>
    /// Reads a base-128 varint.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The varint runs past the end of the body, needs an eleventh byte, or sets a bit above
    /// bit 63 in its tenth byte.
    /// </exception>
    /// <remarks>
    /// Overlong-but-representable encodings (for example <c>80 00</c> for zero) are accepted:
    /// they are legal on the wire and refusing them would break the read-forever promise for no
    /// safety gain.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadVarint()
    {
        // Fast path: one byte, which is what almost every metadata field encodes to.
        int pos = _position;
        ReadOnlySpan<byte> data = _data;
        if ((uint)pos < (uint)data.Length)
        {
            byte b = data[pos];
            if (b < 0x80)
            {
                _position = pos + 1;
                return b;
            }
        }

        return ReadVarintMultiByte();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ReadVarintMultiByte()
    {
        ReadOnlySpan<byte> data = _data;
        int pos = _position;
        ulong result = 0;

        // Bytes 1..9 carry seven value bits each: bits 0..62.
        for (int shift = 0; shift <= 56; shift += 7)
        {
            if ((uint)pos >= (uint)data.Length)
            {
                return ProtoThrow.TruncatedVarint<ulong>(pos);
            }

            byte b = data[pos++];
            result |= (ulong)(b & 0x7Fu) << shift;
            if ((b & 0x80) == 0)
            {
                _position = pos;
                return result;
            }
        }

        // Byte 10 carries exactly one value bit (bit 63). Any other bit set means either the
        // continuation bit (an 11-byte varint) or a value bit above 63 - both unrepresentable.
        if ((uint)pos >= (uint)data.Length)
        {
            return ProtoThrow.TruncatedVarint<ulong>(pos);
        }

        byte last = data[pos++];
        if (last > 1)
        {
            return ProtoThrow.MalformedVarint<ulong>(pos - 1, last);
        }

        result |= (ulong)last << 63;
        _position = pos;
        return result;
    }

    /// <summary>
    /// Reads a varint that must fit in 32 bits.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The varint is malformed, or its value exceeds <see cref="uint.MaxValue"/>.
    /// </exception>
    /// <remarks>
    /// Deliberately <em>not</em> the same rule as <see cref="ReadInt32"/>: a <c>uint32</c> field
    /// is out of domain above 2^32-1 and is rejected, whereas a negative <c>int32</c> is legally
    /// sign-extended to ten bytes and must be truncated instead. Merging the two would either
    /// reject every negative <c>int32</c> or accept an out-of-domain <c>uint32</c>.
    /// </remarks>
    public uint ReadVarint32()
    {
        int start = _position;
        ulong value = ReadVarint();
        if (value > uint.MaxValue)
        {
            return ProtoThrow.VarintExceeds32Bits<uint>(start, value);
        }

        return (uint)value;
    }

    /// <summary>Reads a <c>bool</c>. Any non-zero varint is true, as the wire format requires.</summary>
    /// <exception cref="VortexFormatException">The varint is malformed.</exception>
    public bool ReadBool() => ReadVarint() != 0;

    /// <summary>
    /// Reads an <c>int32</c>: a two's-complement varint, ten bytes long when negative.
    /// </summary>
    /// <exception cref="VortexFormatException">The varint is malformed.</exception>
    /// <remarks>
    /// The high 32 bits are discarded rather than validated. That is the encoder's own rule —
    /// <c>-1</c> is written as <c>FF FF FF FF FF FF FF FF FF 01</c> — and matching it is what lets
    /// a negative <c>int32</c> round-trip. Use <see cref="ReadVarint32"/> when the field is a
    /// <c>uint32</c> and the extra bits really are out of domain.
    /// </remarks>
    public int ReadInt32() => unchecked((int)(uint)ReadVarint());

    /// <summary>Reads an <c>int64</c>: a two's-complement varint.</summary>
    /// <exception cref="VortexFormatException">The varint is malformed.</exception>
    public long ReadInt64() => unchecked((long)ReadVarint());

    /// <summary>Reads a ZigZag-encoded <c>sint32</c>.</summary>
    /// <exception cref="VortexFormatException">The varint is malformed.</exception>
    public int ReadSInt32() => ProtoWire.ZigZagDecode32(unchecked((uint)ReadVarint()));

    /// <summary>
    /// Reads a ZigZag-encoded <c>sint64</c> — the encoding of
    /// <c>vortex.scalar.ScalarValue.int64_value</c>.
    /// </summary>
    /// <exception cref="VortexFormatException">The varint is malformed.</exception>
    public long ReadSInt64() => ProtoWire.ZigZagDecode64(ReadVarint());

    /// <summary>Reads four little-endian bytes.</summary>
    /// <exception cref="VortexFormatException">Fewer than four bytes remain.</exception>
    public uint ReadFixed32()
    {
        int pos = _position;
        int remaining = _data.Length - pos;
        if (remaining < sizeof(uint))
        {
            return ProtoThrow.Truncated<uint>("fixed32", pos, sizeof(uint), remaining);
        }

        _position = pos + sizeof(uint);
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(pos, sizeof(uint)));
    }

    /// <summary>Reads eight little-endian bytes.</summary>
    /// <exception cref="VortexFormatException">Fewer than eight bytes remain.</exception>
    public ulong ReadFixed64()
    {
        int pos = _position;
        int remaining = _data.Length - pos;
        if (remaining < sizeof(ulong))
        {
            return ProtoThrow.Truncated<ulong>("fixed64", pos, sizeof(ulong), remaining);
        }

        _position = pos + sizeof(ulong);
        return BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(pos, sizeof(ulong)));
    }

    /// <summary>Reads a <c>float</c> (wire type <see cref="ProtoWireType.Fixed32"/>).</summary>
    /// <exception cref="VortexFormatException">Fewer than four bytes remain.</exception>
    /// <remarks>Bit-preserving: NaN payloads and negative zero survive unchanged.</remarks>
    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadFixed32());

    /// <summary>Reads a <c>double</c> (wire type <see cref="ProtoWireType.Fixed64"/>).</summary>
    /// <exception cref="VortexFormatException">Fewer than eight bytes remain.</exception>
    /// <remarks>Bit-preserving: NaN payloads and negative zero survive unchanged.</remarks>
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    /// <summary>
    /// Reads a length-delimited payload and returns it as a slice of the input — no copy.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The length varint is malformed, exceeds <see cref="int.MaxValue"/>, or the payload escapes
    /// the message body.
    /// </exception>
    /// <remarks>
    /// The bounds test is done in 64-bit arithmetic so <c>position + length</c> cannot wrap:
    /// a file that declares a length near <see cref="int.MaxValue"/> at a high position is the
    /// exact input that defeats a 32-bit check.
    /// </remarks>
    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int lengthPosition = _position;
        ulong rawLength = ReadVarint();
        if (rawLength > int.MaxValue)
        {
            return ProtoThrow.LengthTooLarge<ReadOnlySpan<byte>>(lengthPosition, rawLength);
        }

        int length = (int)rawLength;
        int pos = _position;
        if ((long)pos + length > _data.Length)
        {
            return ProtoThrow.LengthEscapesBuffer<ReadOnlySpan<byte>>(pos, length, _data.Length - pos);
        }

        _position = pos + length;
        return _data.Slice(pos, length);
    }

    /// <summary>
    /// Reads a nested message and returns a reader positioned at the start of its body.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The length varint is malformed or the body escapes the message.
    /// </exception>
    public ProtoReader ReadMessage() => new ProtoReader(ReadLengthDelimited());

    /// <summary>
    /// Consumes the payload of the field whose tag was just read, without interpreting it. This is
    /// the unknown-field path: a field number the caller does not know is skipped from its wire
    /// type alone.
    /// </summary>
    /// <param name="wireType">The wire type returned by <see cref="TryReadTag"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is truncated, or <paramref name="wireType"/> is a group (3, 4) or reserved
    /// (6, 7) — those have no skippable framing.
    /// </exception>
    public void SkipField(ProtoWireType wireType)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint:
                ReadVarint();
                return;

            case ProtoWireType.Fixed64:
                SkipRaw(sizeof(ulong), "fixed64");
                return;

            case ProtoWireType.LengthDelimited:
                ReadLengthDelimited();
                return;

            case ProtoWireType.Fixed32:
                SkipRaw(sizeof(uint), "fixed32");
                return;

            case ProtoWireType.StartGroup:
            case ProtoWireType.EndGroup:
                ProtoThrow.GroupWireType(_position, (int)wireType);
                return;

            default:
                ProtoThrow.ReservedWireType(_position, (int)wireType);
                return;
        }
    }

    private void SkipRaw(int count, string what)
    {
        int pos = _position;
        int remaining = _data.Length - pos;
        if (remaining < count)
        {
            ProtoThrow.TruncatedVoid(what, pos, count, remaining);
        }

        _position = pos + count;
    }
}
