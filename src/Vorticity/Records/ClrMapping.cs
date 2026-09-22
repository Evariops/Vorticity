using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vorticity.Types;

namespace Vorticity;

/// <summary>What a .NET type is, as far as the column mapping is concerned.</summary>
internal enum ClrKind : byte
{
    Unsupported,
    Bool,
    Signed,
    Unsigned,
    Float,
    String,
    Binary,
    Decimal,
    VortexDecimal,
    DateOnly,
    TimeOnly,
    DateTime,
    DateTimeOffset,
    Guid,
    List,
    Extension,
}

/// <summary>The shape of a .NET type a column may be read or written as.</summary>
internal sealed class ClrShape
{
    private static readonly ConcurrentDictionary<Type, ClrShape> Shapes = new();

    private ClrShape(Type type, ClrKind kind, PType ptype, bool nullableValue, ClrShape? element)
    {
        Type = type;
        Kind = kind;
        PType = ptype;
        IsNullableValue = nullableValue;
        Element = element;
    }

    internal Type Type { get; }

    internal ClrKind Kind { get; }

    /// <summary>The physical type of a numeric, a <see cref="bool"/> aside.</summary>
    internal PType PType { get; }

    /// <summary>Whether the type is a <see cref="Nullable{T}"/>.</summary>
    internal bool IsNullableValue { get; }

    /// <summary>The element of a <c>ReadOnlyMemory&lt;T&gt;</c> list.</summary>
    internal ClrShape? Element { get; }

    internal static ClrShape Of(Type type) => Shapes.GetOrAdd(type, Build);

    private static ClrShape Build(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        bool nullable = underlying is not null;
        Type core = underlying ?? type;
        if (core.IsEnum)
        {
            core = Enum.GetUnderlyingType(core);
        }

        if (core.IsGenericType && core.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>))
        {
            Type element = core.GetGenericArguments()[0];
            return element == typeof(byte)
                ? new ClrShape(type, ClrKind.Binary, default, nullable, null)
                : new ClrShape(type, ClrKind.List, default, nullable, Of(element));
        }

        (ClrKind kind, PType ptype) = core switch
        {
            _ when core == typeof(bool) => (ClrKind.Bool, default(PType)),
            _ when core == typeof(sbyte) => (ClrKind.Signed, PType.I8),
            _ when core == typeof(short) => (ClrKind.Signed, PType.I16),
            _ when core == typeof(int) => (ClrKind.Signed, PType.I32),
            _ when core == typeof(long) => (ClrKind.Signed, PType.I64),
            _ when core == typeof(byte) => (ClrKind.Unsigned, PType.U8),
            _ when core == typeof(ushort) => (ClrKind.Unsigned, PType.U16),
            _ when core == typeof(uint) => (ClrKind.Unsigned, PType.U32),
            _ when core == typeof(ulong) => (ClrKind.Unsigned, PType.U64),
            _ when core == typeof(Half) => (ClrKind.Float, PType.F16),
            _ when core == typeof(float) => (ClrKind.Float, PType.F32),
            _ when core == typeof(double) => (ClrKind.Float, PType.F64),
            _ when core == typeof(string) => (ClrKind.String, default(PType)),
            _ when core == typeof(decimal) => (ClrKind.Decimal, default(PType)),
            _ when core == typeof(VortexDecimal) => (ClrKind.VortexDecimal, default(PType)),
            _ when core == typeof(DateOnly) => (ClrKind.DateOnly, default(PType)),
            _ when core == typeof(TimeOnly) => (ClrKind.TimeOnly, default(PType)),
            _ when core == typeof(DateTime) => (ClrKind.DateTime, default(PType)),
            _ when core == typeof(DateTimeOffset) => (ClrKind.DateTimeOffset, default(PType)),
            _ when core == typeof(Guid) => (ClrKind.Guid, default(PType)),
            _ => (ClrKind.Unsupported, default(PType)),
        };

        return new ClrShape(type, kind, ptype, nullable, null);
    }

    /// <summary>The shape of <typeparamref name="T"/>, computed once.</summary>
    internal static class For<T>
    {
        internal static readonly ClrShape Value = Of(typeof(T));
    }
}

/// <summary>Whether a .NET type fits a column, by the mapping table of the public surface.</summary>
internal static class ClrFit
{
    /// <summary>Throws <see cref="VortexSchemaException"/> when <typeparamref name="T"/> cannot read or write <paramref name="column"/>.</summary>
    internal static void Require<T>(VortexType column, string what, VortexExtensionRegistry? extensions)
    {
        if (!Fits(ClrShape.For<T>.Value, column, extensions, out string? reason))
        {
            throw new VortexSchemaException($"{what} is a column of {column}, which {Name(typeof(T))} does not map to: {reason}");
        }
    }

    /// <summary>A type as C# spells it, for a message: <c>int?</c>, <c>ReadOnlyMemory&lt;byte&gt;</c>, not <c>Nullable`1</c>.</summary>
    internal static string Name(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying) + "?";
        }

        string? keyword = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool",
            TypeCode.SByte => "sbyte",
            TypeCode.Byte => "byte",
            TypeCode.Int16 => "short",
            TypeCode.UInt16 => "ushort",
            TypeCode.Int32 => "int",
            TypeCode.UInt32 => "uint",
            TypeCode.Int64 => "long",
            TypeCode.UInt64 => "ulong",
            TypeCode.Single => "float",
            TypeCode.Double => "double",
            TypeCode.Decimal => "decimal",
            TypeCode.String => "string",
            TypeCode.Char => "char",
            _ => null,
        };
        if (keyword is not null && !type.IsEnum)
        {
            return keyword;
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        Type[] arguments = type.GetGenericArguments();
        string[] names = new string[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            names[i] = Name(arguments[i]);
        }

        return (tick < 0 ? name : name[..tick]) + "<" + string.Join(", ", names) + ">";
    }

    internal static bool Fits(ClrShape shape, VortexType column, VortexExtensionRegistry? extensions, out string? reason)
    {
        reason = null;
        if (extensions is not null && column.Kind == VortexTypeKind.Extension && extensions.TryGet(column.ExtensionId!, out ExtensionRegistration registration)
            && registration.ClrType == (Nullable.GetUnderlyingType(shape.Type) ?? shape.Type))
        {
            if (registration.StorageType.NonNullable == column.StorageType!.NonNullable)
            {
                return true;
            }

            reason = $"the file stores '{column.ExtensionId}' as {column.StorageType.NonNullable}, and its registration reads {registration.StorageType.NonNullable}.";
            return false;
        }

        if (shape.Kind == ClrKind.Unsupported)
        {
            reason = shape.Type == typeof(char)
                ? "a char is not a dtype; read the text column as string."
                : column.Kind == VortexTypeKind.Extension
                    ? $"'{column.ExtensionId}' is not registered on the session, or not by this type."
                    : "the type has no mapping to a dtype.";
            return false;
        }

        VortexType target = column;
        switch (shape.Kind)
        {
            case ClrKind.Bool:
                return Expect(target.Kind == VortexTypeKind.Bool, "a bool reads a bool column.", out reason);
            case ClrKind.Signed:
            case ClrKind.Unsigned:
            case ClrKind.Float:
                // A date, a time or a timestamp is its storage to a caller who asks for the storage.
                if (target.Kind == VortexTypeKind.Extension && target.StorageType is { Kind: VortexTypeKind.Primitive } storage)
                {
                    target = storage;
                }

                return Expect(target.Kind == VortexTypeKind.Primitive && target.PrimitiveType == shape.PType,
                    $"a {Name(shape.Type)} maps to a {shape.PType.Name()} column exactly; convert it before a write or after a read.", out reason);
            case ClrKind.String:
                return Expect(target.Kind == VortexTypeKind.Utf8, "a string reads a utf8 column.", out reason);
            case ClrKind.Binary:
                return Expect(target.Kind is VortexTypeKind.Binary or VortexTypeKind.Utf8, "a ReadOnlyMemory<byte> reads a binary column.", out reason);
            case ClrKind.Decimal:
                return Expect(target.Kind == VortexTypeKind.Decimal && target.Precision <= 28 && target.Scale is >= 0 and <= 28,
                    "a decimal reads a decimal column of at most 28 digits and scale; read a wider one as VortexDecimal.", out reason);
            case ClrKind.VortexDecimal:
                return Expect(target.Kind == VortexTypeKind.Decimal, "a VortexDecimal reads a decimal column.", out reason);
            case ClrKind.DateOnly:
                return Expect(target.ExtensionId == ExtensionIds.Date, "a DateOnly reads a vortex.date column.", out reason);
            case ClrKind.TimeOnly:
                return Expect(target.ExtensionId == ExtensionIds.Time, "a TimeOnly reads a vortex.time column.", out reason);
            case ClrKind.DateTime:
                return Expect(target.ExtensionId == ExtensionIds.Timestamp && (target.TimeZone is null || TimeZones.IsUtc(target.TimeZone)),
                    "a DateTime reads a naive or UTC vortex.timestamp column; read a zoned one as DateTimeOffset.", out reason);
            case ClrKind.DateTimeOffset:
                return Expect(target.ExtensionId == ExtensionIds.Timestamp && target.TimeZone is { } zone && TimeZones.TryResolve(zone, out _),
                    "a DateTimeOffset reads a vortex.timestamp column whose zone this host resolves; read another as long.", out reason);
            case ClrKind.Guid:
                return Expect(target.ExtensionId == ExtensionIds.Uuid, "a Guid reads a vortex.uuid column.", out reason);
            case ClrKind.List:
                if (target.Kind is not (VortexTypeKind.List or VortexTypeKind.FixedSizeList))
                {
                    reason = "a ReadOnlyMemory<T> reads a list column.";
                    return false;
                }

                return Fits(shape.Element!, target.ElementType!, extensions, out reason);
            default:
                reason = "the type has no mapping to a dtype.";
                return false;
        }
    }

    private static bool Expect(bool condition, string reason, out string? message)
    {
        message = condition ? null : reason;
        return condition;
    }
}

/// <summary>Time zones, resolved once per id.</summary>
internal static class TimeZones
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Resolved = new(StringComparer.Ordinal);

    internal static bool IsUtc(string zone) =>
        zone is "UTC" or "Etc/UTC" or "Z" or "+00:00" or "Etc/GMT" or "GMT";

    internal static bool TryResolve(string zone, out TimeZoneInfo info)
    {
        TimeZoneInfo? found = Resolved.GetOrAdd(zone, Find);
        info = found!;
        return found is not null;
    }

    private static TimeZoneInfo? Find(string zone)
    {
        if (IsUtc(zone))
        {
            return TimeZoneInfo.Utc;
        }

        // The name comes from the file: an empty one, or one the host will not even look up, is
        // a zone this host cannot resolve, like a name it does not know.
        if (string.IsNullOrWhiteSpace(zone))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The instants of the temporal columns, converted between .NET ticks and a column's unit.</summary>
internal static class TemporalUnits
{
    internal const long UnixEpochTicks = 621_355_968_000_000_000;
    internal const int UnixEpochDayNumber = 719_162;
    internal const long MillisecondsPerDay = 86_400_000;

    /// <summary>Ticks (100 ns) in one <paramref name="unit"/>, or 0 for nanoseconds, which are finer than a tick.</summary>
    internal static long TicksPer(TimeUnit unit) => unit switch
    {
        TimeUnit.Microseconds => TimeSpan.TicksPerMicrosecond,
        TimeUnit.Milliseconds => TimeSpan.TicksPerMillisecond,
        TimeUnit.Seconds => TimeSpan.TicksPerSecond,
        TimeUnit.Days => TimeSpan.TicksPerDay,
        _ => 0,
    };

    /// <summary>Floor division: <c>/</c> truncates toward zero, which moves an instant before the epoch toward the future.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long FloorDiv(long value, long divisor)
    {
        long quotient = Math.DivRem(value, divisor, out long remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// <paramref name="value"/>, counted in <paramref name="unit"/>, as 100 ns ticks: a nanosecond count
    /// floors to the start of its tick, and false when the count overflows the ticks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryToTicks(long value, TimeUnit unit, out long ticks)
    {
        if (unit == TimeUnit.Nanoseconds)
        {
            ticks = FloorDiv(value, 100);
            return true;
        }

        long high = Math.BigMul(value, TicksPer(unit), out long low);
        ticks = low;
        return high == low >> 63;
    }

    /// <summary>A tick count as a count of <paramref name="unit"/>: the count at or below it, and whether it is that count exactly.</summary>
    internal static (Int128 Floor, bool Exact) FromTicks(Int128 ticks, TimeUnit unit)
    {
        if (unit == TimeUnit.Nanoseconds)
        {
            return (ticks * 100, true);
        }

        (Int128 quotient, Int128 remainder) = Int128.DivRem(ticks, TicksPer(unit));
        return remainder < 0 ? (quotient - 1, false) : (quotient, remainder == 0);
    }

    /// <summary>A day count from the epoch as a date; false outside <see cref="DateOnly"/>'s range.</summary>
    internal static bool TryDate(long days, out DateOnly date)
    {
        if (days < -UnixEpochDayNumber || days > DateOnly.MaxValue.DayNumber - UnixEpochDayNumber)
        {
            date = default;
            return false;
        }

        date = DateOnly.FromDayNumber((int)(days + UnixEpochDayNumber));
        return true;
    }

    /// <summary>A stored time of day as a <see cref="TimeOnly"/>; false unless it lies in <c>[0, 24h)</c>.</summary>
    internal static bool TryTime(long stored, TimeUnit unit, out TimeOnly time)
    {
        if (!TryToTicks(stored, unit, out long ticks) || (ulong)ticks >= TimeSpan.TicksPerDay)
        {
            time = default;
            return false;
        }

        time = new TimeOnly(ticks);
        return true;
    }

    /// <summary>A stored instant as UTC ticks; false outside <see cref="DateTime"/>'s range.</summary>
    internal static bool TryInstant(long stored, TimeUnit unit, out long utcTicks)
    {
        if (!TryToTicks(stored, unit, out long ticks) || ticks < -UnixEpochTicks || ticks > DateTime.MaxValue.Ticks - UnixEpochTicks)
        {
            utcTicks = 0;
            return false;
        }

        utcTicks = UnixEpochTicks + ticks;
        return true;
    }

    /// <summary>UTC ticks seen in <paramref name="zone"/>; false when the local time falls outside <see cref="DateTime"/>'s range.</summary>
    internal static bool TryZoned(long utcTicks, TimeZoneInfo zone, out DateTimeOffset value)
    {
        TimeSpan offset = zone.GetUtcOffset(new DateTime(utcTicks, DateTimeKind.Utc));
        long local = utcTicks + offset.Ticks;
        if (local < 0 || local > DateTime.MaxValue.Ticks)
        {
            value = default;
            return false;
        }

        value = new DateTimeOffset(local, offset);
        return true;
    }
}
