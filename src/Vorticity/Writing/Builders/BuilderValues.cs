using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Columns;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>The conversions of the builders: a .NET value to the stored value of its column.</summary>
internal static class BuilderValues
{
    private static readonly UInt128[] Pow10 = PowersOfTen128();
    private static readonly ulong[][] Pow10Wide = PowersOfTen256();

    internal static void AppendDate(FixedStore store, DateOnly value)
    {
        long days = (long)value.DayNumber - TemporalUnits.UnixEpochDayNumber;
        AppendInteger(store, store.TemporalUnit == TimeUnit.Milliseconds ? days * TemporalConvert.MillisecondsPerDay : days, "date");
    }

    internal static void AppendTime(FixedStore store, TimeOnly value)
    {
        long ticks = value.Ticks;
        long stored = store.TemporalUnit switch
        {
            TimeUnit.Nanoseconds => ticks * 100,
            TimeUnit.Days => throw new VortexSchemaException($"The column is {store.Type}: a time is not stored in days."),
            _ => ticks / TemporalUnits.TicksPer(store.TemporalUnit),
        };
        AppendInteger(store, stored, "time");
    }

    internal static void AppendTimestamp(FixedStore store, DateTime value)
    {
        if (store.UtcTimestamp && value.Kind == DateTimeKind.Local)
        {
            value = value.ToUniversalTime();
        }

        AppendInteger(store, Instant(store, value.Ticks - TemporalUnits.UnixEpochTicks), "timestamp");
    }

    internal static void AppendTimestamp(FixedStore store, DateTimeOffset value) =>
        AppendInteger(store, Instant(store, value.UtcTicks - TemporalUnits.UnixEpochTicks), "timestamp");

    internal static void AppendDecimal(FixedStore store, decimal value)
    {
        RequireDecimal(store);
        Int128 unscaled = Unscaled(value, store.Scale, store.Precision);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(bytes, unscaled);
        Span<byte> slot = store.Reserve(1);
        if (slot.Length <= 16)
        {
            bytes[..slot.Length].CopyTo(slot);
        }
        else
        {
            bytes.CopyTo(slot);
            slot[16..].Fill(unscaled < 0 ? (byte)0xFF : (byte)0);
        }
    }

    internal static void AppendDecimal(FixedStore store, VortexDecimal value)
    {
        RequireDecimal(store);
        Int256 unscaled = Unscaled(value, store.Scale, store.Precision);
        Span<byte> bytes = stackalloc byte[Int256.ByteCount];
        unscaled.WriteLittleEndianBytes(bytes);
        bytes[..store.Width].CopyTo(store.Reserve(1));
    }

    /// <summary>Writes a registered extension's value through its storage.</summary>
    internal static void AppendExtension<T>(ColumnStore leaf, in T value)
        where T : IVortexExtension<T>
    {
        Span<byte> slot;
        switch (leaf)
        {
            case FixedStore fixedStore:
                slot = fixedStore.Reserve(1);
                break;
            case FixedListStore { Elements: FixedStore elements } list:
                slot = elements.Reserve(list.Size);
                list.Count++;
                break;
            default:
                throw new VortexSchemaException(
                    $"'{T.Id}' is stored as {leaf.Type}; a registered type is written through a primitive, a decimal or a fixed-size list of primitives.");
        }

        slot.Clear();
        T.ToStorage(in value, slot, leaf.ExtensionMetadata);
    }

    private static void RequireDecimal(FixedStore store)
    {
        if (!store.IsDecimal)
        {
            throw new VortexSchemaException($"The column is {store.Type}, not a decimal.");
        }
    }

    private static long Instant(FixedStore store, long ticks)
    {
        switch (store.TemporalUnit)
        {
            case TimeUnit.Nanoseconds:
                if (ticks > long.MaxValue / 100 || ticks < long.MinValue / 100)
                {
                    throw new VortexSchemaException(
                        $"The column is {store.Type}: an instant {ticks} ticks from 1970 is outside what nanoseconds in 64 bits hold.");
                }

                return ticks * 100;
            default:
                return TemporalConvert.FloorDiv(ticks, TemporalUnits.TicksPer(store.TemporalUnit));
        }
    }

    private static void AppendInteger(FixedStore store, long value, string what)
    {
        switch (store.PType)
        {
            case PType.I64:
                store.Append(value);
                return;
            case PType.I32 when value is >= int.MinValue and <= int.MaxValue:
                store.Append((int)value);
                return;
            case PType.I32:
                throw new VortexSchemaException($"The column is {store.Type}: the {what} is {value} in its unit, outside a 32-bit storage.");
            default:
                throw new VortexSchemaException($"The column is {store.Type}, stored as {store.PType.Name()}; a {what} is not written to it.");
        }
    }

    /// <summary>The unscaled value of <paramref name="value"/> at <paramref name="scale"/>, exactly, within <paramref name="precision"/> digits.</summary>
    internal static Int128 Unscaled(decimal value, int scale, int precision)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        UInt128 magnitude = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        bool negative = bits[3] < 0;
        int from = (bits[3] >> 16) & 0xFF;
        UInt128 limit = precision >= Pow10.Length ? UInt128.MaxValue : Pow10[precision];
        while (from > scale)
        {
            (magnitude, UInt128 remainder) = UInt128.DivRem(magnitude, 10);
            if (remainder != 0)
            {
                throw Inexact(value.ToString(System.Globalization.CultureInfo.InvariantCulture), scale);
            }

            from--;
        }

        while (from < scale)
        {
            if (magnitude > UInt128.MaxValue / 10)
            {
                throw Overflow(value.ToString(System.Globalization.CultureInfo.InvariantCulture), precision, scale);
            }

            magnitude *= 10;
            from++;
        }

        if (magnitude >= limit)
        {
            throw Overflow(value.ToString(System.Globalization.CultureInfo.InvariantCulture), precision, scale);
        }

        return negative ? -(Int128)magnitude : (Int128)magnitude;
    }

    /// <summary>The unscaled value of <paramref name="value"/> at <paramref name="scale"/>, exactly, within <paramref name="precision"/> digits.</summary>
    internal static Int256 Unscaled(VortexDecimal value, int scale, int precision)
    {
        Int256 unscaled = value.Unscaled;
        unscaled.GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        int from = value.Scale;
        while (from > scale)
        {
            if (DivRemTen(ref m0, ref m1, ref m2, ref m3) != 0)
            {
                throw Inexact(value.ToString(), scale);
            }

            from--;
        }

        while (from < scale)
        {
            if (!Int256.TryMultiplyMagnitudeByTen(ref m0, ref m1, ref m2, ref m3))
            {
                throw Overflow(value.ToString(), precision, scale);
            }

            from++;
        }

        ulong[] limit = Pow10Wide[Math.Min(precision, Pow10Wide.Length - 1)];
        if (Int256.CompareMagnitudes(m0, m1, m2, m3, limit[0], limit[1], limit[2], limit[3]) >= 0)
        {
            throw Overflow(value.ToString(), precision, scale);
        }

        Int256 result = Int256.FromLimbs(m0, m1, m2, m3);
        return unscaled.IsNegative ? Int256.Negate(result) : result;
    }

    private static ulong DivRemTen(ref ulong m0, ref ulong m1, ref ulong m2, ref ulong m3)
    {
        UInt128 current = m3;
        m3 = (ulong)(current / 10);
        UInt128 remainder = current % 10;
        current = (remainder << 64) | m2;
        m2 = (ulong)(current / 10);
        remainder = current % 10;
        current = (remainder << 64) | m1;
        m1 = (ulong)(current / 10);
        remainder = current % 10;
        current = (remainder << 64) | m0;
        m0 = (ulong)(current / 10);
        return (ulong)(current % 10);
    }

    private static VortexSchemaException Inexact(string value, int scale) =>
        new VortexSchemaException($"{value} has more fractional digits than the column's scale of {scale} holds exactly.");

    private static VortexSchemaException Overflow(string value, int precision, int scale) =>
        new VortexSchemaException($"{value} does not fit a decimal({precision}, {scale}) column.");

    private static UInt128[] PowersOfTen128()
    {
        UInt128[] powers = new UInt128[39];
        powers[0] = 1;
        for (int i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }

    private static ulong[][] PowersOfTen256()
    {
        ulong[][] powers = new ulong[77][];
        ulong m0 = 1, m1 = 0, m2 = 0, m3 = 0;
        for (int i = 0; i < powers.Length; i++)
        {
            powers[i] = [m0, m1, m2, m3];
            Int256.TryMultiplyMagnitudeByTen(ref m0, ref m1, ref m2, ref m3);
        }

        return powers;
    }
}

/// <summary>Appends a span of list elements to the elements column, in bulk for the primitives.</summary>
internal static class ElementWriter
{
    internal static void Append<T>(ColumnStore leaf, ReadOnlySpan<T> values)
    {
        if (IsPrimitive<T>())
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), checked(values.Length * Unsafe.SizeOf<T>()));
            ((FixedStore)leaf).AppendBytes(bytes);
            return;
        }

        if (typeof(T) == typeof(bool))
        {
            ((BoolStore)leaf).Append(As<T, bool>(values));
            return;
        }

        if (typeof(T) == typeof(string))
        {
            VarBinStore text = (VarBinStore)leaf;
            foreach (string? value in As<T, string?>(values))
            {
                if (value is null)
                {
                    text.AppendNull();
                }
                else
                {
                    text.Append(value.AsSpan());
                }
            }

            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            VarBinStore bytes = (VarBinStore)leaf;
            foreach (ReadOnlyMemory<byte> value in As<T, ReadOnlyMemory<byte>>(values))
            {
                bytes.Append(value.Span);
            }

            return;
        }

        if (!AppendConverted(leaf, values))
        {
            throw new VortexSchemaException(
                $"A list of {ClrFit.Name(typeof(T))} is not appended in one call; append its elements one by one through Elements.");
        }
    }

    private static bool AppendConverted<T>(ColumnStore leaf, ReadOnlySpan<T> values)
    {
        if (typeof(T) == typeof(decimal))
        {
            foreach (decimal value in As<T, decimal>(values))
            {
                BuilderValues.AppendDecimal((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(VortexDecimal))
        {
            foreach (VortexDecimal value in As<T, VortexDecimal>(values))
            {
                BuilderValues.AppendDecimal((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(DateOnly))
        {
            foreach (DateOnly value in As<T, DateOnly>(values))
            {
                BuilderValues.AppendDate((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(TimeOnly))
        {
            foreach (TimeOnly value in As<T, TimeOnly>(values))
            {
                BuilderValues.AppendTime((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(DateTime))
        {
            foreach (DateTime value in As<T, DateTime>(values))
            {
                BuilderValues.AppendTimestamp((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(DateTimeOffset))
        {
            foreach (DateTimeOffset value in As<T, DateTimeOffset>(values))
            {
                BuilderValues.AppendTimestamp((FixedStore)leaf, value);
            }
        }
        else if (typeof(T) == typeof(Guid))
        {
            foreach (Guid value in As<T, Guid>(values))
            {
                ((FixedListStore)leaf).AppendGuid(value);
            }
        }
        else
        {
            return AppendNullable(leaf, values);
        }

        return true;
    }

    private static bool AppendNullable<T>(ColumnStore leaf, ReadOnlySpan<T> values)
    {
        if (typeof(T) == typeof(sbyte?)) { Nullable(leaf, As<T, sbyte?>(values)); }
        else if (typeof(T) == typeof(short?)) { Nullable(leaf, As<T, short?>(values)); }
        else if (typeof(T) == typeof(int?)) { Nullable(leaf, As<T, int?>(values)); }
        else if (typeof(T) == typeof(long?)) { Nullable(leaf, As<T, long?>(values)); }
        else if (typeof(T) == typeof(byte?)) { Nullable(leaf, As<T, byte?>(values)); }
        else if (typeof(T) == typeof(ushort?)) { Nullable(leaf, As<T, ushort?>(values)); }
        else if (typeof(T) == typeof(uint?)) { Nullable(leaf, As<T, uint?>(values)); }
        else if (typeof(T) == typeof(ulong?)) { Nullable(leaf, As<T, ulong?>(values)); }
        else if (typeof(T) == typeof(Half?)) { Nullable(leaf, As<T, Half?>(values)); }
        else if (typeof(T) == typeof(float?)) { Nullable(leaf, As<T, float?>(values)); }
        else if (typeof(T) == typeof(double?)) { Nullable(leaf, As<T, double?>(values)); }
        else if (typeof(T) == typeof(bool?))
        {
            BoolStore bits = (BoolStore)leaf;
            foreach (bool? value in As<T, bool?>(values))
            {
                if (value is { } present)
                {
                    bits.Append(present);
                }
                else
                {
                    bits.AppendNull();
                }
            }
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            VarBinStore bytes = (VarBinStore)leaf;
            foreach (ReadOnlyMemory<byte>? value in As<T, ReadOnlyMemory<byte>?>(values))
            {
                if (value is { } present)
                {
                    bytes.Append(present.Span);
                }
                else
                {
                    bytes.AppendNull();
                }
            }
        }
        else if (typeof(T) == typeof(decimal?) || typeof(T) == typeof(VortexDecimal?) || typeof(T) == typeof(DateOnly?)
            || typeof(T) == typeof(TimeOnly?) || typeof(T) == typeof(DateTime?) || typeof(T) == typeof(DateTimeOffset?)
            || typeof(T) == typeof(Guid?))
        {
            NullableConverted(leaf, values);
        }
        else
        {
            return false;
        }

        return true;
    }

    private static void NullableConverted<T>(ColumnStore leaf, ReadOnlySpan<T> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ref T value = ref Unsafe.AsRef(in values[i]);
            if (typeof(T) == typeof(decimal?) && Unsafe.As<T, decimal?>(ref value) is { } d)
            {
                BuilderValues.AppendDecimal((FixedStore)leaf, d);
            }
            else if (typeof(T) == typeof(VortexDecimal?) && Unsafe.As<T, VortexDecimal?>(ref value) is { } v)
            {
                BuilderValues.AppendDecimal((FixedStore)leaf, v);
            }
            else if (typeof(T) == typeof(DateOnly?) && Unsafe.As<T, DateOnly?>(ref value) is { } date)
            {
                BuilderValues.AppendDate((FixedStore)leaf, date);
            }
            else if (typeof(T) == typeof(TimeOnly?) && Unsafe.As<T, TimeOnly?>(ref value) is { } time)
            {
                BuilderValues.AppendTime((FixedStore)leaf, time);
            }
            else if (typeof(T) == typeof(DateTime?) && Unsafe.As<T, DateTime?>(ref value) is { } instant)
            {
                BuilderValues.AppendTimestamp((FixedStore)leaf, instant);
            }
            else if (typeof(T) == typeof(DateTimeOffset?) && Unsafe.As<T, DateTimeOffset?>(ref value) is { } offset)
            {
                BuilderValues.AppendTimestamp((FixedStore)leaf, offset);
            }
            else if (typeof(T) == typeof(Guid?) && Unsafe.As<T, Guid?>(ref value) is { } guid)
            {
                ((FixedListStore)leaf).AppendGuid(guid);
            }
            else
            {
                leaf.AppendNull();
            }
        }
    }

    private static void Nullable<TValue>(ColumnStore leaf, ReadOnlySpan<TValue?> values)
        where TValue : unmanaged
    {
        FixedStore store = (FixedStore)leaf;
        foreach (TValue? value in values)
        {
            if (value is { } present)
            {
                store.Append(present);
            }
            else
            {
                store.AppendNull();
            }
        }
    }

    private static bool IsPrimitive<T>() =>
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(short) || typeof(T) == typeof(int) || typeof(T) == typeof(long)
        || typeof(T) == typeof(byte) || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong)
        || typeof(T) == typeof(Half) || typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T).IsEnum;

    private static ReadOnlySpan<TOut> As<T, TOut>(ReadOnlySpan<T> values) =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, TOut>(ref MemoryMarshal.GetReference(values)), values.Length);
}
