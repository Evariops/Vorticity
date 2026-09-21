using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity;

/// <summary>An engine literal read back as the .NET value of a column: the inverse of <see cref="SymLowering"/>.</summary>
internal static class LiteralValues
{
    internal static T? ToValue<T>(FilterLiteral literal, VortexType column)
    {
        if (literal.Kind == FilterLiteralKind.Null)
        {
            return default;
        }

        ClrShape shape = ClrShape.For<T>.Value;
        Type core = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object value = shape.Kind switch
        {
            ClrKind.Bool => literal.BoolValue,
            ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float => Numeric(literal, core),
            ClrKind.String => Encoding.UTF8.GetString(literal.BytesValue),
            ClrKind.Binary => new ReadOnlyMemory<byte>(literal.BytesValue.ToArray()),
            ClrKind.DateOnly => DateOnly.FromDayNumber((int)(TemporalUnits.UnixEpochDayNumber
                + (column.Unit == TimeUnit.Milliseconds ? Math.Floor(Stored(literal) / 86_400_000d) : Stored(literal)))),
            ClrKind.TimeOnly => new TimeOnly(TemporalUnits.ToTicks(Stored(literal), column.Unit ?? TimeUnit.Microseconds)),
            ClrKind.DateTime => new DateTime(
                TemporalUnits.UnixEpochTicks + TemporalUnits.ToTicks(Stored(literal), column.Unit ?? TimeUnit.Microseconds),
                column.TimeZone is null ? DateTimeKind.Unspecified : DateTimeKind.Utc),
            ClrKind.DateTimeOffset => TimeZoneInfo.ConvertTime(
                new DateTimeOffset(TemporalUnits.UnixEpochTicks + TemporalUnits.ToTicks(Stored(literal), column.Unit ?? TimeUnit.Microseconds), TimeSpan.Zero),
                column.ZoneInfo),
            ClrKind.Guid => new Guid(literal.BytesValue, bigEndian: true),
            ClrKind.Decimal => Decimal(literal, column),
            ClrKind.VortexDecimal => Wide(literal, column),
            _ => throw new NotSupportedException($"A {typeof(T)} is not read back from a literal."),
        };

        return (T)value;
    }

    private static long Stored(FilterLiteral literal) =>
        literal.Kind == FilterLiteralKind.Unsigned ? (long)literal.UnsignedValue : literal.SignedValue;

    private static object Numeric(FilterLiteral literal, Type core)
    {
        if (core.IsEnum)
        {
            return Enum.ToObject(core, literal.Kind == FilterLiteralKind.Unsigned ? literal.UnsignedValue : (object)literal.SignedValue);
        }

        return literal.Kind switch
        {
            FilterLiteralKind.Float => core == typeof(Half) ? (Half)literal.FloatValue
                : core == typeof(float) ? (float)literal.FloatValue
                : Convert.ChangeType(literal.FloatValue, core, CultureInfo.InvariantCulture),
            FilterLiteralKind.Unsigned => Convert.ChangeType(literal.UnsignedValue, core, CultureInfo.InvariantCulture),
            _ => Convert.ChangeType(literal.SignedValue, core, CultureInfo.InvariantCulture),
        };
    }

    private static BigInteger Unscaled(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Bytes => new BigInteger(literal.BytesValue, isUnsigned: false, isBigEndian: false),
        FilterLiteralKind.Unsigned => literal.UnsignedValue,
        _ => literal.SignedValue,
    };

    private static decimal Decimal(FilterLiteral literal, VortexType column)
    {
        BigInteger unscaled = Unscaled(literal);
        bool negative = unscaled.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(unscaled);
        return new decimal(
            (int)(uint)(magnitude & uint.MaxValue),
            (int)(uint)((magnitude >> 32) & uint.MaxValue),
            (int)(uint)((magnitude >> 64) & uint.MaxValue),
            negative,
            (byte)column.Scale);
    }

    private static VortexDecimal Wide(FilterLiteral literal, VortexType column)
    {
        Span<byte> bytes = stackalloc byte[32];
        BigInteger unscaled = Unscaled(literal);
        bytes.Fill(unscaled.Sign < 0 ? (byte)0xFF : (byte)0);
        unscaled.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false);
        return new VortexDecimal(Int256.FromLittleEndianBytes(bytes), (byte)column.Precision, (sbyte)column.Scale);
    }
}
