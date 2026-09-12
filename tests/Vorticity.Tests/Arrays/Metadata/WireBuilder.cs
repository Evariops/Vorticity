// Test-only Protobuf wire builder. Hand-rolled so that a test can spell a malformed payload the
// production writer would refuse to produce.
using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Tests.Arrays.Metadata;

internal sealed class WireBuilder
{
    private readonly List<byte> _bytes = new List<byte>();

    public static byte[] Empty => Array.Empty<byte>();

    public WireBuilder Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _bytes.Add((byte)value);
        return this;
    }

    public WireBuilder Tag(int fieldNumber, ProtoWireType wireType) =>
        Varint(((ulong)(uint)fieldNumber << 3) | (byte)wireType);

    public WireBuilder VarintField(int fieldNumber, ulong value) =>
        Tag(fieldNumber, ProtoWireType.Varint).Varint(value);

    /// <summary>An int32/enum field carrying a negative value: ten bytes, sign-extended.</summary>
    public WireBuilder NegativeEnumField(int fieldNumber, int value) =>
        Tag(fieldNumber, ProtoWireType.Varint).Varint(unchecked((ulong)(long)value));

    public WireBuilder BytesField(int fieldNumber, ReadOnlySpan<byte> payload)
    {
        Tag(fieldNumber, ProtoWireType.LengthDelimited).Varint((ulong)payload.Length);
        return Raw(payload);
    }

    public WireBuilder Fixed32Field(int fieldNumber, uint value)
    {
        Tag(fieldNumber, ProtoWireType.Fixed32);
        for (int i = 0; i < 4; i++)
        {
            _bytes.Add((byte)(value >> (8 * i)));
        }

        return this;
    }

    public WireBuilder Fixed64Field(int fieldNumber, ulong value)
    {
        Tag(fieldNumber, ProtoWireType.Fixed64);
        for (int i = 0; i < 8; i++)
        {
            _bytes.Add((byte)(value >> (8 * i)));
        }

        return this;
    }

    public WireBuilder Raw(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            _bytes.Add(bytes[i]);
        }

        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>Offsets at which a top-level field starts, plus the end of the message.</summary>
    public static int[] FieldBoundaries(ReadOnlySpan<byte> message)
    {
        List<int> boundaries = new List<int>();
        ProtoReader reader = new ProtoReader(message);
        while (true)
        {
            int position = reader.Position;
            if (!reader.TryReadTag(out _, out ProtoWireType wire))
            {
                boundaries.Add(position);
                break;
            }

            boundaries.Add(position);
            reader.SkipField(wire);
        }

        return boundaries.ToArray();
    }

    /// <summary>Splices <paramref name="injection"/> into <paramref name="message"/> at <paramref name="offset"/>.</summary>
    public static byte[] InsertAt(ReadOnlySpan<byte> message, int offset, ReadOnlySpan<byte> injection)
    {
        byte[] result = new byte[message.Length + injection.Length];
        message[..offset].CopyTo(result);
        injection.CopyTo(result.AsSpan(offset));
        message[offset..].CopyTo(result.AsSpan(offset + injection.Length));
        return result;
    }

    /// <summary>The four skippable unknown-field spellings, at a field number no message defines.</summary>
    public static byte[][] UnknownFields()
    {
        const int Number = 4242;
        return new[]
        {
            new WireBuilder().VarintField(Number, 0xDEADBEEFUL).ToArray(),
            new WireBuilder().Fixed64Field(Number, 0x0102030405060708UL).ToArray(),
            new WireBuilder().BytesField(Number, new byte[] { 1, 2, 3, 4, 5 }).ToArray(),
            new WireBuilder().Fixed32Field(Number, 0x11223344U).ToArray(),
        };
    }

    /// <summary>A group-start tag (wire type 3): proto3 never emits one and every reader rejects it.</summary>
    public static byte[] GroupTag() => new WireBuilder().Varint((7UL << 3) | 3).ToArray();

    public static byte[] FromBase64(string base64) => Convert.FromBase64String(base64);
}
