using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Types.Numerics;

namespace Vorticity.Types.Variant;

/// <summary>What a decoded variant value turned out to be.</summary>
internal enum VariantKind : byte
{
    /// <summary>The variant `null`, which is a value in its own right, not an absent row.</summary>
    Null = 0,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A signed 8-bit integer.</summary>
    Int8,

    /// <summary>A signed 16-bit integer.</summary>
    Int16,

    /// <summary>A signed 32-bit integer.</summary>
    Int32,

    /// <summary>A signed 64-bit integer.</summary>
    Int64,

    /// <summary>A 32-bit float.</summary>
    Float,

    /// <summary>A 64-bit float.</summary>
    Double,

    /// <summary>A UTF-8 string, either short-form or long-form.</summary>
    String,

    /// <summary>Arbitrary bytes.</summary>
    Binary,
}

/// <summary>One decoded Parquet Variant value.</summary>
/// <remarks>
/// A ref struct because <see cref="Bytes"/> views the caller's buffer; nothing here copies.
/// </remarks>
internal readonly ref struct VariantValue
{
    private readonly ReadOnlySpan<byte> _bytes;

    internal VariantValue(VariantKind kind, long integer, double number, ReadOnlySpan<byte> bytes)
    {
        Kind = kind;
        Integer = integer;
        Number = number;
        _bytes = bytes;
    }

    /// <summary>What the value is.</summary>
    internal VariantKind Kind { get; }

    /// <summary>The value, for <see cref="VariantKind.Bool"/> and the integer kinds.</summary>
    internal long Integer { get; }

    /// <summary>The value, for <see cref="VariantKind.Float"/> and <see cref="VariantKind.Double"/>.</summary>
    internal double Number { get; }

    /// <summary>The bytes, for <see cref="VariantKind.String"/> and <see cref="VariantKind.Binary"/>.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes;
}

/// <summary>
/// Reads and writes the Parquet Variant binary encoding, where a value is two little-endian byte
/// strings: metadata, a version byte and a string dictionary, and value, a tagged encoding of one
/// JSON-like value.
/// </summary>
/// <remarks>
/// Only the primitives and strings are encoded and decoded, in both directions; an object, an
/// array, a decimal or a uuid raises <see cref="VortexUnsupportedException"/> naming what was
/// found, rather than yielding a wrong value or a silent null.
/// </remarks>
internal static class ParquetVariant
{
    /// <summary>The only metadata version this encoding has ever had.</summary>
    private const byte Version = 1;

    /// <summary>Basic type 0: the header's remaining six bits name a primitive.</summary>
    private const int BasicPrimitive = 0;

    /// <summary>Basic type 1: the header's remaining six bits are the string's length.</summary>
    private const int BasicShortString = 1;

    /// <summary>Basic type 2: an object.</summary>
    private const int BasicObject = 2;

    /// <summary>Basic type 3: an array.</summary>
    private const int BasicArray = 3;

    /// <summary>
    /// The metadata of a variant whose value names no dictionary string: version 1, no entries.
    /// </summary>
    /// <remarks>
    /// Three bytes: header <c>0x01</c> is version 1 with <c>offset_size</c> 1 and the strings
    /// unsorted, then a one-byte <c>dictionary_size</c> of 0, then the one offset a zero-entry
    /// dictionary still needs.
    /// </remarks>
    internal static ReadOnlySpan<byte> EmptyMetadata => [0x01, 0x00, 0x00];

    /// <summary>Validates a metadata blob and returns its dictionary entry count.</summary>
    /// <param name="metadata">The metadata bytes.</param>
    /// <returns>How many strings the dictionary holds.</returns>
    /// <exception cref="VortexFormatException">The metadata is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">The version is not 1.</exception>
    internal static int ReadDictionarySize(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length < 1)
        {
            throw new VortexFormatException("A variant's metadata is empty.");
        }

        byte header = metadata[0];
        int version = header & 0x0F;
        if (version != Version)
        {
            throw new VortexUnsupportedException(
                "vortex.parquet.variant",
                VortexComponentKind.Array,
                $"the variant metadata declares version {version}; only version 1 is defined.");
        }

        int offsetSize = ((header >> 6) & 0x03) + 1;
        if (metadata.Length < 1 + offsetSize)
        {
            throw new VortexFormatException(
                $"A variant's metadata is {metadata.Length} bytes; its {offsetSize}-byte " +
                "dictionary size does not fit.");
        }

        long size = ReadUnsigned(metadata.Slice(1, offsetSize));
        long offsetsEnd = 1L + offsetSize + ((size + 1) * offsetSize);
        if (size < 0 || offsetsEnd > metadata.Length)
        {
            throw new VortexFormatException(
                $"A variant's metadata declares {size} dictionary entries, whose offsets do not " +
                $"fit in {metadata.Length} bytes.");
        }

        return checked((int)size);
    }

    /// <summary>Decodes one variant value.</summary>
    /// <param name="metadata">The row's metadata; validated, and needed only by nested values.</param>
    /// <param name="value">The row's value bytes.</param>
    /// <returns>The decoded value, viewing <paramref name="value"/>.</returns>
    /// <exception cref="VortexFormatException">The value is truncated or malformed.</exception>
    /// <exception cref="VortexUnsupportedException">The value is of a kind this build refuses.</exception>
    internal static VariantValue Read(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        _ = ReadDictionarySize(metadata);

        if (value.Length < 1)
        {
            throw new VortexFormatException("A variant's value is empty.");
        }

        byte header = value[0];
        int basic = header & 0x03;
        ReadOnlySpan<byte> payload = value[1..];

        switch (basic)
        {
            case BasicShortString:
            {
                int length = header >> 2;
                if (payload.Length < length)
                {
                    throw new VortexFormatException(
                        $"A variant short string declares {length} bytes; {payload.Length} remain.");
                }

                return new VariantValue(VariantKind.String, 0, 0, payload[..length]);
            }

            case BasicObject:
            case BasicArray:
                throw new VortexUnsupportedException(
                    "vortex.parquet.variant",
                    VortexComponentKind.Array,
                    basic == BasicObject
                        ? "the value is a variant OBJECT; this build decodes primitives and strings."
                        : "the value is a variant ARRAY; this build decodes primitives and strings.");

            default:
                return ReadPrimitive(header >> 2, payload);
        }
    }

    private static VariantValue ReadPrimitive(int typeId, ReadOnlySpan<byte> payload)
    {
        switch (typeId)
        {
            case 0:
                return new VariantValue(VariantKind.Null, 0, 0, default);
            case 1:
                return new VariantValue(VariantKind.Bool, 1, 0, default);
            case 2:
                return new VariantValue(VariantKind.Bool, 0, 0, default);
            case 3:
                return new VariantValue(VariantKind.Int8, (sbyte)Take(payload, 1)[0], 0, default);
            case 4:
                return new VariantValue(
                    VariantKind.Int16, BinaryPrimitives.ReadInt16LittleEndian(Take(payload, 2)), 0, default);
            case 5:
                return new VariantValue(
                    VariantKind.Int32, BinaryPrimitives.ReadInt32LittleEndian(Take(payload, 4)), 0, default);
            case 6:
                return new VariantValue(
                    VariantKind.Int64, BinaryPrimitives.ReadInt64LittleEndian(Take(payload, 8)), 0, default);
            case 7:
                return new VariantValue(
                    VariantKind.Double, 0, BinaryPrimitives.ReadDoubleLittleEndian(Take(payload, 8)), default);
            case 14:
                return new VariantValue(
                    VariantKind.Float, 0, BinaryPrimitives.ReadSingleLittleEndian(Take(payload, 4)), default);
            case 15:
            case 16:
            {
                ReadOnlySpan<byte> lengthBytes = Take(payload, 4);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
                ReadOnlySpan<byte> bytes = payload[4..];
                if (length > (uint)bytes.Length)
                {
                    throw new VortexFormatException(
                        $"A variant string or binary declares {length} bytes; {bytes.Length} remain.");
                }

                return new VariantValue(
                    typeId == 16 ? VariantKind.String : VariantKind.Binary,
                    0,
                    0,
                    bytes[..(int)length]);
            }

            default:
                throw new VortexUnsupportedException(
                    "vortex.parquet.variant",
                    VortexComponentKind.Array,
                    $"the value is primitive type {typeId}; this build decodes null, booleans, " +
                    "int8 through int64, float, double, string and binary.");
        }
    }

    /// <summary>
    /// Bytes a variant value takes when it encodes <paramref name="scalar"/>, or -1 when it cannot.
    /// </summary>
    /// <param name="scalar">The value to encode.</param>
    /// <param name="dtype">Its dtype.</param>
    internal static int MeasureValue(scoped in TypedScalar scalar, DType dtype)
    {
        if (scalar.IsNull)
        {
            return 1;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                return 1;
            case DTypeKind.Bool:
                return 1;
            case DTypeKind.Primitive:
                return dtype.PType switch
                {
                    PType.I8 => 2,
                    PType.I16 => 3,
                    PType.I32 => 5,
                    PType.I64 => 9,
                    PType.U8 or PType.U16 or PType.U32 or PType.U64 when SignedWidth(scalar.AsUInt64, dtype.PType) is int width and > 0 => 1 + width,
                    PType.F32 => 5,
                    PType.F64 => 9,
                    _ => -1,
                };
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            {
                int length = scalar.AsBinary.Length;
                bool shortForm = dtype.Kind == DTypeKind.Utf8 && length <= 63;
                return shortForm ? 1 + length : 5 + length;
            }

            default:
                return -1;
        }
    }

    /// <summary>Encodes <paramref name="scalar"/> into <paramref name="destination"/>.</summary>
    /// <param name="scalar">The value; its dtype decides the encoding.</param>
    /// <param name="dtype">The scalar's dtype.</param>
    /// <param name="destination">Exactly <see cref="MeasureValue"/> bytes.</param>
    /// <exception cref="VortexUnsupportedException">The dtype has no primitive encoding here.</exception>
    internal static void WriteValue(scoped in TypedScalar scalar, DType dtype, Span<byte> destination)
    {
        if (scalar.IsNull || dtype.Kind == DTypeKind.Null)
        {
            destination[0] = Primitive(0);
            return;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
                destination[0] = Primitive(scalar.AsBool ? 1 : 2);
                return;

            case DTypeKind.Primitive:
                WritePrimitive(in scalar, dtype.PType, destination);
                return;

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            {
                ReadOnlySpan<byte> bytes = scalar.AsBinary;
                if (dtype.Kind == DTypeKind.Utf8 && bytes.Length <= 63)
                {
                    destination[0] = (byte)((bytes.Length << 2) | BasicShortString);
                    bytes.CopyTo(destination[1..]);
                    return;
                }

                destination[0] = Primitive(dtype.Kind == DTypeKind.Utf8 ? 16 : 15);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], (uint)bytes.Length);
                bytes.CopyTo(destination[5..]);
                return;
            }

            default:
                throw new VortexUnsupportedException(
                    "vortex.variant",
                    VortexComponentKind.Array,
                    $"a constant variant of dtype {dtype.Kind} has no primitive encoding here; " +
                    "null, booleans, integers, floats, strings and binary do.");
        }
    }

    private static void WritePrimitive(scoped in TypedScalar scalar, PType ptype, Span<byte> destination)
    {
        switch (ptype)
        {
            case PType.I8:
                WriteSigned(scalar.AsInt64, 1, destination);
                return;
            case PType.I16:
                WriteSigned(scalar.AsInt64, 2, destination);
                return;
            case PType.I32:
                WriteSigned(scalar.AsInt64, 4, destination);
                return;
            case PType.I64:
                WriteSigned(scalar.AsInt64, 8, destination);
                return;
            case PType.U8:
            case PType.U16:
            case PType.U32:
            case PType.U64:
            {
                ulong value = scalar.AsUInt64;
                int width = SignedWidth(value, ptype);
                if (width == 0)
                {
                    throw new VortexUnsupportedException(
                        "vortex.variant",
                        VortexComponentKind.Array,
                        $"a constant variant of {ptype.Name()} {value} has no encoding: a variant integer is signed, 64 bits at most.");
                }

                WriteSigned((long)value, width, destination);
                return;
            }

            case PType.F32:
                destination[0] = Primitive(14);
                BinaryPrimitives.WriteSingleLittleEndian(destination[1..], scalar.AsF32);
                return;
            case PType.F64:
                destination[0] = Primitive(7);
                BinaryPrimitives.WriteDoubleLittleEndian(destination[1..], scalar.AsF64);
                return;
            default:
                throw new VortexUnsupportedException(
                    "vortex.variant",
                    VortexComponentKind.Array,
                    $"a constant variant of physical type {ptype.Name()} has no encoding here.");
        }
    }

    /// <summary>
    /// The bytes of the signed integer an unsigned value of <paramref name="ptype"/> is written as,
    /// a variant having no unsigned integer: its own width when it fits, twice that when it does not,
    /// and 0 past 64 bits.
    /// </summary>
    private static int SignedWidth(ulong value, PType ptype) => ptype switch
    {
        PType.U8 => value <= (ulong)sbyte.MaxValue ? 1 : 2,
        PType.U16 => value <= (ulong)short.MaxValue ? 2 : 4,
        PType.U32 => value <= int.MaxValue ? 4 : 8,
        _ => value <= long.MaxValue ? 8 : 0,
    };

    /// <summary>Writes <paramref name="value"/> as the variant integer of <paramref name="width"/> bytes.</summary>
    private static void WriteSigned(long value, int width, Span<byte> destination)
    {
        switch (width)
        {
            case 1:
                destination[0] = Primitive(3);
                destination[1] = unchecked((byte)value);
                return;
            case 2:
                destination[0] = Primitive(4);
                BinaryPrimitives.WriteInt16LittleEndian(destination[1..], unchecked((short)value));
                return;
            case 4:
                destination[0] = Primitive(5);
                BinaryPrimitives.WriteInt32LittleEndian(destination[1..], unchecked((int)value));
                return;
            default:
                destination[0] = Primitive(6);
                BinaryPrimitives.WriteInt64LittleEndian(destination[1..], value);
                return;
        }
    }

    private static byte Primitive(int typeId) => (byte)((typeId << 2) | BasicPrimitive);

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> payload, int count)
    {
        if (payload.Length < count)
        {
            throw new VortexFormatException(
                $"A variant primitive needs {count} bytes; {payload.Length} remain.");
        }

        return payload[..count];
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
