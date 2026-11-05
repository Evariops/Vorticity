using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Expressions;
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

        if (TryTyped(literal, column, out T? typed))
        {
            return typed;
        }

        ClrShape shape = ClrShape.For<T>.Value;
        object value = shape.Kind switch
        {
            ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float => Numeric(literal, shape.Core),
            ClrKind.String => Encoding.UTF8.GetString(literal.BytesValue),
            ClrKind.Binary => new ReadOnlyMemory<byte>(literal.BytesValue.ToArray()),
            ClrKind.VortexDecimal => Wide(literal, column),
            ClrKind.Integer128 => Integer<Int128>(literal),
            ClrKind.UInteger128 => Integer<UInt128>(literal),
            ClrKind.BigInteger => Unscaled(literal),
            _ => throw new NotSupportedException($"A {ClrFit.Name(typeof(T))} is not read back from a literal."),
        };

        return (T)value;
    }

    /// <summary>
    /// A date, a time, an instant or a duration from its integer storage, nullable or not, without a
    /// box; false for any other <typeparamref name="T"/>, which the JIT resolves per instantiation.
    /// </summary>
    internal static bool TryFromStorage<T>(long stored, VortexType column, out T? value)
    {
        if (Is<DateTime, T>()) { value = Wrap<DateTime, T>(Instant(stored, column)); return true; }
        if (Is<DateTimeOffset, T>()) { value = Wrap<DateTimeOffset, T>(Zoned(stored, column)); return true; }
        if (Is<DateOnly, T>()) { value = Wrap<DateOnly, T>(Date(stored, column)); return true; }
        if (Is<TimeOnly, T>()) { value = Wrap<TimeOnly, T>(Time(stored, column)); return true; }
        if (Is<TimeSpan, T>()) { value = Wrap<TimeSpan, T>(new TimeSpan(stored)); return true; }
        value = default;
        return false;
    }

    /// <summary>An unscaled value as a decimal of the column's scale; the column holds at most 28 digits, as a decimal does.</summary>
    internal static decimal Decimal(Int128 unscaled, VortexType column)
    {
        bool negative = unscaled < 0;
        UInt128 magnitude = (UInt128)(negative ? -unscaled : unscaled);
        Debug.Assert(magnitude >> 96 == 0, "A decimal reads a column of at most 28 digits.");
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)column.Scale);
    }

    /// <summary>A uuid's bits, read big-endian, as a <see cref="Guid"/>.</summary>
    internal static Guid Uuid(UInt128 bits)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, bits);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Whether <typeparamref name="T"/> is <typeparamref name="TCore"/> or its <see cref="Nullable{T}"/>, a constant per instantiation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Is<TCore, T>()
        where TCore : struct =>
        typeof(T) == typeof(TCore) || typeof(T) == typeof(TCore?);

    /// <summary><paramref name="value"/> as <typeparamref name="T"/>, which <see cref="Is{TCore, T}"/>: no box.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Wrap<TCore, T>(TCore value)
        where TCore : struct
    {
        if (typeof(T) == typeof(TCore))
        {
            return Unsafe.As<TCore, T>(ref value);
        }

        TCore? nullable = value;
        return Unsafe.As<TCore?, T>(ref nullable);
    }

    /// <summary>
    /// A key of a primitive type, nullable or not, without a box: under the <c>typeof</c> tests the JIT
    /// keeps the one branch of <typeparamref name="T"/>, so a cursor walking a column of integers reads
    /// its keys for free.
    /// </summary>
    private static bool TryPrimitive<T>(FilterLiteral literal, out T? value)
    {
        long signed = literal.Kind == FilterLiteralKind.Unsigned ? unchecked((long)literal.UnsignedValue) : literal.SignedValue;
        if (literal.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned)
        {
            if (Is<long, T>()) { value = Wrap<long, T>(signed); return true; }
            if (Is<int, T>()) { value = Wrap<int, T>((int)signed); return true; }
            if (Is<short, T>()) { value = Wrap<short, T>((short)signed); return true; }
            if (Is<sbyte, T>()) { value = Wrap<sbyte, T>((sbyte)signed); return true; }
            if (Is<ulong, T>()) { value = Wrap<ulong, T>(unchecked((ulong)signed)); return true; }
            if (Is<uint, T>()) { value = Wrap<uint, T>((uint)signed); return true; }
            if (Is<ushort, T>()) { value = Wrap<ushort, T>((ushort)signed); return true; }
            if (Is<byte, T>()) { value = Wrap<byte, T>((byte)signed); return true; }
            if (Is<char, T>()) { value = Wrap<char, T>((char)signed); return true; }
            if (Is<nint, T>()) { value = Wrap<nint, T>((nint)signed); return true; }
            if (Is<nuint, T>()) { value = Wrap<nuint, T>(unchecked((nuint)(ulong)signed)); return true; }
        }
        else if (literal.Kind == FilterLiteralKind.Float)
        {
            if (Is<double, T>()) { value = Wrap<double, T>(literal.FloatValue); return true; }
            if (Is<float, T>()) { value = Wrap<float, T>((float)literal.FloatValue); return true; }
            if (Is<Half, T>()) { value = Wrap<Half, T>((Half)literal.FloatValue); return true; }
        }

        value = default;
        return false;
    }

    /// <summary>A bool, a date, a time, an instant, a duration, a uuid or a decimal, nullable or not, without a box.</summary>
    private static bool TryTyped<T>(FilterLiteral literal, VortexType column, out T? value)
    {
        if (TryFromStorage(Stored(literal), column, out value))
        {
            return true;
        }

        if (Is<Guid, T>()) { value = Wrap<Guid, T>(new Guid(literal.BytesValue, bigEndian: true)); return true; }
        if (Is<decimal, T>()) { value = Wrap<decimal, T>(Decimal(literal, column)); return true; }
        if (Is<bool, T>()) { value = Wrap<bool, T>(literal.BoolValue); return true; }
        value = default;
        return false;
    }

    private static long Stored(FilterLiteral literal) =>
        literal.Kind == FilterLiteralKind.Unsigned ? (long)literal.UnsignedValue : literal.SignedValue;

    // A stored temporal value as its .NET value; a value outside the .NET type's range is a claim the file cannot make.
    private static DateOnly Date(long stored, VortexType column) =>
        TemporalUnits.TryDate(column.Unit == TimeUnit.Milliseconds ? TemporalUnits.FloorDiv(stored, TemporalUnits.MillisecondsPerDay) : stored, out DateOnly date)
            ? date
            : throw Outside(stored, column, nameof(DateOnly));

    private static TimeOnly Time(long stored, VortexType column) =>
        TemporalUnits.TryTime(stored, column.Unit ?? TimeUnit.Microseconds, out TimeOnly time)
            ? time
            : throw Outside(stored, column, nameof(TimeOnly));

    private static DateTime Instant(long stored, VortexType column) =>
        TemporalUnits.TryInstant(stored, column.Unit ?? TimeUnit.Microseconds, out long ticks)
            ? new DateTime(ticks, column.IsZoned ? DateTimeKind.Utc : DateTimeKind.Unspecified)
            : throw Outside(stored, column, nameof(DateTime));

    private static DateTimeOffset Zoned(long stored, VortexType column) =>
        TemporalUnits.TryInstant(stored, column.Unit ?? TimeUnit.Microseconds, out long ticks)
        && TemporalUnits.TryZoned(ticks, column.ZoneInfo, out DateTimeOffset zoned)
            ? zoned
            : throw Outside(stored, column, nameof(DateTimeOffset));

    private static VortexFormatException Outside(long stored, VortexType column, string type) =>
        new VortexFormatException($"The value {stored} of a column of {column} is outside what a {type} holds.");

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

    /// <summary>A decimal literal as a decimal: an integer one without a BigInteger, a wide one through it.</summary>
    private static decimal Decimal(FilterLiteral literal, VortexType column) => literal.Kind switch
    {
        FilterLiteralKind.Signed => Decimal((Int128)literal.SignedValue, column),
        FilterLiteralKind.Unsigned => Decimal((Int128)literal.UnsignedValue, column),
        _ => Decimal((Int128)Unscaled(literal), column),
    };

    private static VortexDecimal Wide(FilterLiteral literal, VortexType column)
    {
        Span<byte> bytes = stackalloc byte[32];
        BigInteger unscaled = Unscaled(literal);
        bytes.Fill(unscaled.Sign < 0 ? (byte)0xFF : (byte)0);
        unscaled.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false);
        return new VortexDecimal(Int256.FromLittleEndianBytes(bytes), (byte)column.Precision, (sbyte)column.Scale);
    }
}
