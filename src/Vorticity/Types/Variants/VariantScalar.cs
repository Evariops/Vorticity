using System;
using System.Buffers.Binary;

namespace Vorticity.Types.Variants;

/// <summary>
/// One value of the Parquet Variant encoding read in place, whatever its type: a primitive with its
/// payload, a string, or the kind of an object or an array, which <see cref="VariantNested"/> reads.
/// </summary>
/// <remarks>
/// A ref struct because <see cref="Bytes"/> views the caller's buffer. <see cref="Integer"/> holds
/// what is a whole number in storage: an integer, a decimal's unscaled value, a date's days, a time's
/// or a timestamp's count of its unit. Nothing here allocates.
/// </remarks>
internal readonly ref struct VariantScalar
{
    private readonly ReadOnlySpan<byte> _bytes;

    private VariantScalar(VariantKind kind, Int128 integer, double number, byte scale, bool nanoseconds, ReadOnlySpan<byte> bytes)
    {
        Kind = kind;
        Integer = integer;
        Number = number;
        Scale = scale;
        Nanoseconds = nanoseconds;
        _bytes = bytes;
    }

    /// <summary>What the value is.</summary>
    internal VariantKind Kind { get; }

    /// <summary>A boolean as 0 or 1, an integer, a decimal's unscaled value, a date's days, a time's or a timestamp's count of its unit.</summary>
    internal Int128 Integer { get; }

    /// <summary>A float or a double.</summary>
    internal double Number { get; }

    /// <summary>A decimal's scale.</summary>
    internal byte Scale { get; }

    /// <summary>Whether a timestamp counts nanoseconds rather than microseconds.</summary>
    internal bool Nanoseconds { get; }

    /// <summary>A string's UTF-8 bytes, a binary's bytes, a uuid's sixteen bytes in their big-endian order.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Whether the value is an integer of any width.</summary>
    internal bool IsInteger => Kind is VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64;

    /// <summary>The value <paramref name="value"/> starts with.</summary>
    /// <exception cref="VortexFormatException">The value is empty or cut short.</exception>
    /// <exception cref="VortexUnsupportedException">The value is a primitive of a type the encoding does not define.</exception>
    internal static VariantScalar Read(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new VortexFormatException("A variant's value is empty.");
        }

        byte header = value[0];
        ReadOnlySpan<byte> payload = value[1..];
        switch (header & 0x03)
        {
            case ParquetVariant.BasicShortString:
            {
                int length = header >> 2;
                return new VariantScalar(VariantKind.String, 0, 0, 0, false, Take(payload, length));
            }

            case ParquetVariant.BasicObject:
                return new VariantScalar(VariantKind.Object, 0, 0, 0, false, default);
            case ParquetVariant.BasicArray:
                return new VariantScalar(VariantKind.Array, 0, 0, 0, false, default);
        }

        int typeId = header >> 2;
        switch (typeId)
        {
            case 0:
                return new VariantScalar(VariantKind.Null, 0, 0, 0, false, default);
            case 1:
            case 2:
                return new VariantScalar(VariantKind.Bool, typeId == 1 ? 1 : 0, 0, 0, false, default);
            case 3:
                return new VariantScalar(VariantKind.Int8, (sbyte)Take(payload, 1)[0], 0, 0, false, default);
            case 4:
                return new VariantScalar(VariantKind.Int16, BinaryPrimitives.ReadInt16LittleEndian(Take(payload, 2)), 0, 0, false, default);
            case 5:
                return new VariantScalar(VariantKind.Int32, BinaryPrimitives.ReadInt32LittleEndian(Take(payload, 4)), 0, 0, false, default);
            case 6:
                return new VariantScalar(VariantKind.Int64, BinaryPrimitives.ReadInt64LittleEndian(Take(payload, 8)), 0, 0, false, default);
            case 7:
                return new VariantScalar(VariantKind.Double, 0, BinaryPrimitives.ReadDoubleLittleEndian(Take(payload, 8)), 0, false, default);
            case 8:
            {
                ReadOnlySpan<byte> bytes = Take(payload, 5);
                return new VariantScalar(VariantKind.Decimal, BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]), 0, ScaleOf(bytes[0]), false, default);
            }

            case 9:
            {
                ReadOnlySpan<byte> bytes = Take(payload, 9);
                return new VariantScalar(VariantKind.Decimal, BinaryPrimitives.ReadInt64LittleEndian(bytes[1..]), 0, ScaleOf(bytes[0]), false, default);
            }

            case 10:
            {
                ReadOnlySpan<byte> bytes = Take(payload, 17);
                return new VariantScalar(VariantKind.Decimal, BinaryPrimitives.ReadInt128LittleEndian(bytes[1..]), 0, ScaleOf(bytes[0]), false, default);
            }

            case 11:
                return new VariantScalar(VariantKind.Date, BinaryPrimitives.ReadInt32LittleEndian(Take(payload, 4)), 0, 0, false, default);
            case 12:
            case 18:
                return new VariantScalar(VariantKind.Timestamp, BinaryPrimitives.ReadInt64LittleEndian(Take(payload, 8)), 0, 0, typeId == 18, default);
            case 13:
            case 19:
                return new VariantScalar(VariantKind.LocalTimestamp, BinaryPrimitives.ReadInt64LittleEndian(Take(payload, 8)), 0, 0, typeId == 19, default);
            case 14:
                return new VariantScalar(VariantKind.Float, 0, BinaryPrimitives.ReadSingleLittleEndian(Take(payload, 4)), 0, false, default);
            case 15:
            case 16:
            {
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(Take(payload, 4));
                ReadOnlySpan<byte> rest = payload[4..];
                if (length > (uint)rest.Length)
                {
                    throw new VortexFormatException($"A variant string or binary declares {length} bytes; {rest.Length} remain.");
                }

                return new VariantScalar(typeId == 16 ? VariantKind.String : VariantKind.Binary, 0, 0, 0, false, rest[..(int)length]);
            }

            case 17:
                return new VariantScalar(VariantKind.Time, BinaryPrimitives.ReadInt64LittleEndian(Take(payload, 8)), 0, 0, false, default);
            case 20:
                return new VariantScalar(VariantKind.Uuid, 0, 0, 0, false, Take(payload, 16));
            default:
                throw new VortexUnsupportedException(
                    "vortex.parquet.variant",
                    VortexComponentKind.Array,
                    $"the value is primitive type {typeId}, which the variant encoding does not define.");
        }
    }

    /// <summary>The value's kind alone, read from its header: what <see cref="Read"/> would say, without its payload.</summary>
    internal static VariantKind KindOf(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new VortexFormatException("A variant's value is empty.");
        }

        byte header = value[0];
        return (header & 0x03) switch
        {
            ParquetVariant.BasicShortString => VariantKind.String,
            ParquetVariant.BasicObject => VariantKind.Object,
            ParquetVariant.BasicArray => VariantKind.Array,
            _ => (header >> 2) switch
            {
                0 => VariantKind.Null,
                1 or 2 => VariantKind.Bool,
                3 => VariantKind.Int8,
                4 => VariantKind.Int16,
                5 => VariantKind.Int32,
                6 => VariantKind.Int64,
                7 => VariantKind.Double,
                8 or 9 or 10 => VariantKind.Decimal,
                11 => VariantKind.Date,
                12 or 18 => VariantKind.Timestamp,
                13 or 19 => VariantKind.LocalTimestamp,
                14 => VariantKind.Float,
                15 => VariantKind.Binary,
                16 => VariantKind.String,
                17 => VariantKind.Time,
                20 => VariantKind.Uuid,
                int typeId => throw new VortexUnsupportedException(
                    "vortex.parquet.variant",
                    VortexComponentKind.Array,
                    $"the value is primitive type {typeId}, which the variant encoding does not define."),
            },
        };
    }

    /// <summary>
    /// The value converted to an integer: an integer, a float or a decimal with no fraction within
    /// 64 bits, a boolean as 0 or 1, or text that parses as an integer; false when it does not convert.
    /// </summary>
    internal bool TryToInt64(out long value)
    {
        value = 0;
        switch (Kind)
        {
            case VariantKind.Bool:
            case VariantKind.Int8:
            case VariantKind.Int16:
            case VariantKind.Int32:
            case VariantKind.Int64:
                value = (long)Integer;
                return true;
            case VariantKind.Float:
            case VariantKind.Double:
                if (!double.IsInteger(Number) || Number < long.MinValue || Number >= 9.2233720368547758E18)
                {
                    return false;
                }

                value = (long)Number;
                return true;
            case VariantKind.Decimal:
            {
                Int128 power = Int128.One;
                for (int i = 0; i < Scale; i++)
                {
                    power *= 10;
                }

                (Int128 whole, Int128 fraction) = Int128.DivRem(Integer, power);
                if (fraction != 0 || whole < long.MinValue || whole > long.MaxValue)
                {
                    return false;
                }

                value = (long)whole;
                return true;
            }

            case VariantKind.String:
                return long.TryParse(Bytes, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    /// <summary>
    /// The value converted to a double: any number, a boolean as 0 or 1, or text that parses as a
    /// number; false when it does not convert.
    /// </summary>
    internal bool TryToDouble(out double value)
    {
        value = 0;
        switch (Kind)
        {
            case VariantKind.Bool:
            case VariantKind.Int8:
            case VariantKind.Int16:
            case VariantKind.Int32:
            case VariantKind.Int64:
                value = (double)Integer;
                return true;
            case VariantKind.Float:
            case VariantKind.Double:
                value = Number;
                return true;
            case VariantKind.Decimal:
            {
                double power = 1;
                for (int i = 0; i < Scale; i++)
                {
                    power *= 10;
                }

                value = (double)Integer / power;
                return true;
            }

            case VariantKind.String:
                return double.TryParse(Bytes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    /// <summary>A timestamp's or a time's count of microseconds, a nanosecond count floored to the microsecond.</summary>
    internal long Microseconds => Nanoseconds ? (long)Int128.DivRem(Integer - (Integer < 0 ? 999 : 0), 1000).Quotient : (long)Integer;

    private static byte ScaleOf(byte scale) =>
        scale <= 38 ? scale : throw new VortexFormatException($"A variant decimal declares a scale of {scale}, past the 38 digits a decimal holds.");

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> payload, int count) =>
        payload.Length >= count
            ? payload[..count]
            : throw new VortexFormatException($"A variant value needs {count} bytes past its header; {payload.Length} remain.");
}
