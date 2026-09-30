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

        if (TryPrimitive(literal, out T? primitive))
        {
            return primitive;
        }

        ClrShape shape = ClrShape.For<T>.Value;
        Type core = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object value = shape.Kind switch
        {
            ClrKind.Bool => literal.BoolValue,
            ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float => Numeric(literal, core),
            ClrKind.String => Encoding.UTF8.GetString(literal.BytesValue),
            ClrKind.Binary => new ReadOnlyMemory<byte>(literal.BytesValue.ToArray()),
            ClrKind.DateOnly or ClrKind.TimeOnly or ClrKind.DateTime or ClrKind.DateTimeOffset => Temporal(literal, column, shape.Kind),
            ClrKind.Guid => new Guid(literal.BytesValue, bigEndian: true),
            ClrKind.Decimal => Decimal(literal, column),
            ClrKind.VortexDecimal => Wide(literal, column),
            ClrKind.TimeSpan => new TimeSpan(Stored(literal)),
            ClrKind.Integer128 => Integer<Int128>(literal),
            ClrKind.UInteger128 => Integer<UInt128>(literal),
            ClrKind.BigInteger => Unscaled(literal),
            _ => throw new NotSupportedException($"A {ClrFit.Name(typeof(T))} is not read back from a literal."),
        };

        return (T)value;
    }

    /// <summary>
    /// A key of a primitive type without a box: under the <c>typeof</c> tests the JIT specializes each
    /// <c>(T)(object)</c> away, so a cursor walking a column of integers reads its keys for free.
    /// </summary>
    private static bool TryPrimitive<T>(FilterLiteral literal, out T? value)
    {
        long signed = literal.Kind == FilterLiteralKind.Unsigned ? unchecked((long)literal.UnsignedValue) : literal.SignedValue;
        if (literal.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned)
        {
            if (typeof(T) == typeof(long)) { value = (T)(object)signed; return true; }
            if (typeof(T) == typeof(int)) { value = (T)(object)(int)signed; return true; }
            if (typeof(T) == typeof(short)) { value = (T)(object)(short)signed; return true; }
            if (typeof(T) == typeof(sbyte)) { value = (T)(object)(sbyte)signed; return true; }
            if (typeof(T) == typeof(ulong)) { value = (T)(object)unchecked((ulong)signed); return true; }
            if (typeof(T) == typeof(uint)) { value = (T)(object)(uint)signed; return true; }
            if (typeof(T) == typeof(ushort)) { value = (T)(object)(ushort)signed; return true; }
            if (typeof(T) == typeof(byte)) { value = (T)(object)(byte)signed; return true; }
            if (typeof(T) == typeof(char)) { value = (T)(object)(char)signed; return true; }
            if (typeof(T) == typeof(nint)) { value = (T)(object)(nint)signed; return true; }
            if (typeof(T) == typeof(nuint)) { value = (T)(object)unchecked((nuint)(ulong)signed); return true; }
        }
        else if (literal.Kind == FilterLiteralKind.Float)
        {
            if (typeof(T) == typeof(double)) { value = (T)(object)literal.FloatValue; return true; }
            if (typeof(T) == typeof(float)) { value = (T)(object)(float)literal.FloatValue; return true; }
        }

        value = default;
        return false;
    }

    private static long Stored(FilterLiteral literal) =>
        literal.Kind == FilterLiteralKind.Unsigned ? (long)literal.UnsignedValue : literal.SignedValue;

    /// <summary>A stored temporal value as its .NET value; a value outside the .NET type's range is a claim the file cannot make.</summary>
    private static object Temporal(FilterLiteral literal, VortexType column, ClrKind kind)
    {
        long stored = Stored(literal);
        TimeUnit unit = column.Unit ?? TimeUnit.Microseconds;
        switch (kind)
        {
            case ClrKind.DateOnly when TemporalUnits.TryDate(column.Unit == TimeUnit.Milliseconds ? TemporalUnits.FloorDiv(stored, TemporalUnits.MillisecondsPerDay) : stored, out DateOnly date):
                return date;
            case ClrKind.TimeOnly when TemporalUnits.TryTime(stored, unit, out TimeOnly time):
                return time;
            case ClrKind.DateTime when TemporalUnits.TryInstant(stored, unit, out long ticks):
                return new DateTime(ticks, column.TimeZone is null ? DateTimeKind.Unspecified : DateTimeKind.Utc);
            case ClrKind.DateTimeOffset when TemporalUnits.TryInstant(stored, unit, out long ticks)
                && TemporalUnits.TryZoned(ticks, column.ZoneInfo, out DateTimeOffset zoned):
                return zoned;
            default:
                throw new VortexFormatException($"The value {stored} of a column of {column} is outside what a {kind} holds.");
        }
    }

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

    /// <summary>An unscaled integer as a 128-bit integer; one past its range is a value this type cannot read.</summary>
    private static object Integer<TInteger>(FilterLiteral literal)
    {
        BigInteger unscaled = Unscaled(literal);
        if (typeof(TInteger) == typeof(Int128) && unscaled >= (BigInteger)Int128.MinValue && unscaled <= (BigInteger)Int128.MaxValue)
        {
            return (Int128)unscaled;
        }

        if (typeof(TInteger) == typeof(UInt128) && unscaled.Sign >= 0 && unscaled <= (BigInteger)UInt128.MaxValue)
        {
            return (UInt128)unscaled;
        }

        throw new VortexFormatException($"The value {unscaled} is outside what a {typeof(TInteger).Name} holds.");
    }

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
