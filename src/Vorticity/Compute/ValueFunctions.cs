using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// A column read through a function of its values, <c>r.At.Truncate(CalendarUnit.Day)</c>: resolved
/// as the column, the function then applied where the block lies. It reads the column's path, so
/// projections and passes read the column; its key, which carries the function, keeps it apart from
/// the column itself.
/// </summary>
internal sealed class FunctionFieldExpr : FieldExpr
{
    internal FunctionFieldExpr(FieldExpr column, ValueFunction function)
        : base(column.Segments ?? column.Path.Split('.'))
    {
        Column = column;
        Function = function;
    }

    /// <summary>The column, or the function of it this one applies to.</summary>
    internal FieldExpr Column { get; }

    internal ValueFunction Function { get; }

    internal override string Key => $"{Column.Key}.{Function.Text}";

    /// <summary>The values of the column's node through every function, the innermost first.</summary>
    internal int Apply(CanonicalArena arena, int node) =>
        Function.Apply(arena, Column is FunctionFieldExpr inner ? inner.Apply(arena, node) : node);

    /// <summary>The same functions over another reference to the column: its name in an object of a dataset.</summary>
    internal FunctionFieldExpr Over(FieldExpr column) =>
        new FunctionFieldExpr(Column is FunctionFieldExpr inner ? inner.Over(column) : column, Function);

    /// <summary>
    /// <paramref name="filter"/> as a scan plans and evaluates it: each comparison of a function of
    /// an integer storage with a literal as the range of the column's values it stands for, and each
    /// null check of a function as its column's, so that every structure of the column -- zone maps,
    /// statistics, sorted runs, indexes -- prunes, counts and locates for it. The filter itself when
    /// it has none.
    /// </summary>
    /// <remarks>
    /// Rewritten where the scan takes it, not where the predicate is written: a filter on groups
    /// recognizes a component of the key by its field, the function.
    /// </remarks>
    internal static VortexExpr Ranges(VortexExpr filter)
    {
        switch (filter)
        {
            case ComparisonExpr { Field: FunctionFieldExpr function } comparison:
                return function.Preimage(comparison.Op, comparison.Value) ?? filter;
            case NullCheckExpr { Field: FunctionFieldExpr } check:
                return new NullCheckExpr(Stored(check.Field), check.IsNull);
            case NotExpr not:
            {
                VortexExpr operand = Ranges(not.Operand);
                return ReferenceEquals(operand, not.Operand) ? filter : Expr.Not(operand);
            }

            case LogicalExpr logical:
            {
                VortexExpr left = Ranges(logical.Left);
                VortexExpr right = Ranges(logical.Right);
                return ReferenceEquals(left, logical.Left) && ReferenceEquals(right, logical.Right)
                    ? filter
                    : Expr.Logical(logical.IsAnd, left, right);
            }

            default:
                return filter;
        }
    }

    /// <summary><paramref name="field"/> compared with <paramref name="literal"/>, through its functions.</summary>
    private static VortexExpr Compare(FieldExpr field, ComparisonOp op, FilterLiteral literal) =>
        field is FunctionFieldExpr function && function.Preimage(op, literal) is { } range
            ? range
            : new ComparisonExpr(field, op, literal);

    /// <summary>The column <paramref name="field"/> reads through every function, null on the same rows.</summary>
    private static FieldExpr Stored(FieldExpr field) => field is FunctionFieldExpr function ? Stored(function.Column) : field;

    /// <summary>
    /// The comparison of the function's values with <paramref name="literal"/> as a range of its
    /// column's. Non-decreasing, the function reaches the value from the least one whose image
    /// does, and passes it from the least whose image reaches the next integer; a null row is
    /// unknown to the range as to the comparison, so they agree in three values, under a negation
    /// too. Null when the literal or the storage is not an integer.
    /// </summary>
    private VortexExpr? Preimage(ComparisonOp op, FilterLiteral literal)
    {
        if (Function.Storage.IsFloat() || !ValueFunction.TryInteger(literal, out Int128 value))
        {
            return null;
        }

        bool reaches = Function.TryLeast(value, out Int128 from);
        bool passes = Function.TryLeast(value + 1, out Int128 past);

        // No value's image is the literal itself when the two start at the same place.
        bool hit = reaches && !(passes && past == from);
        return op switch
        {
            ComparisonOp.GreaterOrEqual => reaches ? AtLeast(from) : Nothing(),
            ComparisonOp.Greater => passes ? AtLeast(past) : Nothing(),
            ComparisonOp.Less => reaches ? Below(from) : Everything(),
            ComparisonOp.LessOrEqual => passes ? Below(past) : Everything(),
            ComparisonOp.Equal => !hit ? Nothing() : passes ? Expr.And(AtLeast(from), Below(past)) : AtLeast(from),
            _ => !hit ? Everything() : passes ? Expr.Or(Below(from), AtLeast(past)) : Below(from),
        };
    }

    private VortexExpr AtLeast(Int128 value) => Compare(Column, ComparisonOp.GreaterOrEqual, Function.Literal(value));

    private VortexExpr Below(Int128 value) => Compare(Column, ComparisonOp.Less, Function.Literal(value));

    /// <summary>True on every value of the column and unknown on a null: at least the storage's least value.</summary>
    private VortexExpr Everything() => AtLeast(ValueFunction.Integers(Function.Storage).Min);

    /// <summary>False on every value of the column and unknown on a null.</summary>
    private VortexExpr Nothing() => Below(ValueFunction.Integers(Function.Storage).Min);
}

/// <summary>
/// A non-decreasing function of a column's values, evaluated on the storage's numbers: once per
/// constant and per run, per value elsewhere. Being non-decreasing, it maps a block's bounds to the
/// bounds of its values, which is what pruning reads, and the values whose image reaches a literal
/// are those from the least one whose image does, which is how a comparison of its values becomes
/// a range of the column's; a null stays null and a NaN stays NaN.
/// </summary>
/// <remarks>
/// Over integers, the function is a floor: it never sends a value above itself, and where the
/// floor lies below the storage's least value it sends that value, so that the kernel, a bound and
/// a literal see one function, non-decreasing over the whole storage.
/// </remarks>
internal abstract class ValueFunction
{
    /// <summary>The function as written, which a plan shows and an identity compares: <c>Truncate(Day)</c>.</summary>
    internal abstract string Text { get; }

    /// <summary>The storage the function reads and makes, the column's.</summary>
    internal abstract PType Storage { get; }

    /// <summary>
    /// How far below a value its image lies at most where the function does not saturate: the
    /// width of a bucket, the length of a calendar unit. Where the search for a preimage looks first.
    /// </summary>
    protected abstract Int128 Reach { get; }

    /// <summary>The function of one value of an integer storage, within it.</summary>
    protected abstract Int128 Image(Int128 value);

    /// <summary>The function of one value of a float storage.</summary>
    protected virtual double Image(double value) => throw new NotSupportedException($"{Text} reads integers.");

    /// <summary>The function of one value, a bound or a literal, in the storage's terms; false where it does not apply to a literal of that kind.</summary>
    /// <remarks>An integer beyond the storage is taken as the storage's nearest value, whose image is the least or the greatest the function makes.</remarks>
    internal bool TryMap(FilterLiteral value, out FilterLiteral mapped)
    {
        if (Storage.IsFloat())
        {
            mapped = value.Kind == FilterLiteralKind.Float ? FilterLiteral.From(Image(value.FloatValue)) : default;
            return value.Kind == FilterLiteralKind.Float;
        }

        if (!TryInteger(value, out Int128 integer))
        {
            mapped = default;
            return false;
        }

        (Int128 min, Int128 max) = Integers(Storage);
        mapped = Literal(Image(Int128.Clamp(integer, min, max)));
        return true;
    }

    /// <summary>
    /// The least value of the storage whose image is at least <paramref name="value"/>: where the
    /// rows of <c>f(x) ≥ value</c> begin, every value before it having an image below. A floor sends
    /// nothing below <paramref name="value"/> that far, so the search starts there, bisects the next
    /// <see cref="Reach"/> and the rest of the storage only when that holds no such value.
    /// </summary>
    /// <returns>False when no value of the storage has such an image, or the storage is a float's.</returns>
    internal bool TryLeast(Int128 value, out Int128 least)
    {
        least = default;
        if (Storage.IsFloat())
        {
            return false;
        }

        (Int128 min, Int128 max) = Integers(Storage);
        if (value > max)
        {
            return false;
        }

        Int128 low = Int128.Max(value, min);
        if (Image(low) >= value)
        {
            least = low;
            return true;
        }

        Int128 high = low + Int128.Min(Reach, max - low);
        if (Image(high) < value)
        {
            if (high == max || Image(max) < value)
            {
                return false;
            }

            low = high;
            high = max;
        }

        // The image of `low` lies below the value and that of `high` reaches it.
        while (high - low > 1)
        {
            Int128 middle = low + ((high - low) >> 1);
            if (Image(middle) >= value)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        least = high;
        return true;
    }

    /// <summary>An integer literal as one, whichever its sign; false for any other kind.</summary>
    internal static bool TryInteger(FilterLiteral value, out Int128 integer)
    {
        integer = value.Kind switch
        {
            FilterLiteralKind.Signed => value.SignedValue,
            FilterLiteralKind.Unsigned => value.UnsignedValue,
            _ => default,
        };
        return value.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned;
    }

    /// <summary>A value of the storage as the literal its bounds are read as: signed or unsigned.</summary>
    internal FilterLiteral Literal(Int128 value) =>
        Storage.IsSignedInteger() ? FilterLiteral.From((long)value) : FilterLiteral.From((ulong)value);

    /// <summary>The least and the greatest value of an integer storage.</summary>
    internal static (Int128 Min, Int128 Max) Integers(PType ptype) => ptype switch
    {
        PType.I8 => (sbyte.MinValue, sbyte.MaxValue),
        PType.I16 => (short.MinValue, short.MaxValue),
        PType.I32 => (int.MinValue, int.MaxValue),
        PType.I64 => (long.MinValue, long.MaxValue),
        PType.U8 => (byte.MinValue, byte.MaxValue),
        PType.U16 => (ushort.MinValue, ushort.MaxValue),
        PType.U32 => (uint.MinValue, uint.MaxValue),
        _ => (ulong.MinValue, ulong.MaxValue),
    };

    /// <summary>The function's values over a column's node: a node of the same type, in the same arena.</summary>
    /// <exception cref="NotSupportedException">The block is of a form no function reads.</exception>
    internal int Apply(CanonicalArena arena, int node)
    {
        CanonicalNode column = arena.GetNode(node);
        switch (column.Kind)
        {
            case CanonicalKind.Extension:
                return arena.AddExtension(column.DType, column.Length, Apply(arena, column.StorageIndex));
            case CanonicalKind.Primitive:
            {
                VortexBuffer values = arena.AllocateUninitialized(column.Length * column.PType.ByteWidth(), 64, out Span<byte> into);
                Map(column.PType, column.Values.Span, into);
                return arena.AddPrimitive(column.DType, column.Length, column.Validity, column.PType, values);
            }

            case CanonicalKind.Constant:
            {
                ReadOnlySpan<byte> element = column.ConstantElement;
                Span<byte> mapped = stackalloc byte[16];
                Map(column.DType.PType, element, mapped[..element.Length]);
                return arena.AddConstant(column.DType, column.Length, column.Validity, mapped[..element.Length]);
            }

            case CanonicalKind.RunEnd:
                return arena.AddRunEnd(column.DType, column.Length, column.Validity, arena.RecordRef(node).BufferA, Apply(arena, column.EncodedValuesIndex));
            case CanonicalKind.Dictionary:
                // Values mapped need not stay distinct, which a dictionary's are: its rows are read decoded.
                return Apply(arena, arena.Decoded(node));
            default:
                throw new NotSupportedException($"{Text} reads numbers and times, not a {column.Kind} block.");
        }
    }

    /// <summary>Maps values of <paramref name="ptype"/> from <paramref name="source"/> into <paramref name="destination"/>, as many.</summary>
    protected abstract void Map(PType ptype, ReadOnlySpan<byte> source, Span<byte> destination);
}

/// <summary>
/// An instant, an <c>i64</c> in its unit since the epoch, truncated to the start of a calendar unit:
/// as stored, or on a zone's calendar, where a value takes its interval's offset, an integer
/// truncation and the offset of the start back. The autumn's repeated hour is two hours with two
/// offsets, so two buckets of an hour; a day holds both.
/// </summary>
/// <remarks>
/// A zone's calendar holds between the years a <see cref="DateTime"/> holds: an instant before them
/// takes the storage's least value, one after them the start of the last unit they hold, which
/// keeps the function a floor over the whole storage.
/// </remarks>
internal sealed class InstantTruncate : ValueFunction
{
    private readonly CalendarUnit _unit;
    private readonly long _second;
    private readonly long _day;

    /// <summary>The least day whose start the storage holds.</summary>
    private readonly long _firstDay;

    private readonly ZoneOffsets? _zone;

    /// <summary>The instants of the zone's calendar: from the first year a <see cref="DateTime"/> holds to its last.</summary>
    private readonly long _from;
    private readonly long _until;

    internal InstantTruncate(CalendarUnit unit, TimeUnit storage, TimeZoneInfo? zone)
    {
        _unit = unit;
        _second = Civil.PerSecond(storage);
        _day = 86_400 * _second;
        _firstDay = long.MinValue / _day;
        _zone = zone is null || (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) ? null : ZoneOffsets.For(zone);
        Text = _zone is null ? $"Truncate({unit})" : $"Truncate({unit}, {zone!.Id})";

        // Far enough from the storage's ends for an offset and a unit either side.
        Int128 day = _day;
        _from = (long)Int128.Max(Civil.DaysFromCivil(1, 1, 1) * day, long.MinValue + (400 * day));
        _until = (long)Int128.Min((Civil.DaysFromCivil(10_000, 1, 1) * day) - 1, long.MaxValue - (2 * day));
    }

    internal override string Text { get; }

    internal override PType Storage => PType.I64;

    protected override Int128 Reach => (_unit switch
    {
        CalendarUnit.Minute or CalendarUnit.Hour or CalendarUnit.Day => 3,
        CalendarUnit.Week => 9,
        CalendarUnit.Month => 33,
        CalendarUnit.Quarter => 94,
        _ => 368,
    }) * (Int128)_day;

    protected override Int128 Image(Int128 value)
    {
        ZoneCursor cursor = default;
        return Truncate((long)value, ref cursor);
    }

    protected override void Map(PType ptype, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ReadOnlySpan<long> from = MemoryMarshal.Cast<byte, long>(source);
        Span<long> into = MemoryMarshal.Cast<byte, long>(destination);
        ZoneCursor cursor = default;
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = Truncate(from[i], ref cursor);
        }
    }

    private long Truncate(long instant, ref ZoneCursor cursor)
    {
        if (_zone is null)
        {
            return Local(instant);
        }

        if (instant < _from)
        {
            return long.MinValue;
        }

        long held = Math.Min(instant, _until);
        long offset = _zone.OffsetAt(held, _second, ref cursor);
        return _zone.Back(Local(held + offset), offset, _second, ref cursor);
    }

    private long Local(long ticks) => _unit switch
    {
        CalendarUnit.Minute => Civil.FloorTo(ticks, 60 * _second),
        CalendarUnit.Hour => Civil.FloorTo(ticks, 3_600 * _second),
        _ => Start(Civil.TruncateDays(Civil.FloorDiv(ticks, _day), _unit)),
    };

    /// <summary>The instant day <paramref name="days"/> starts at, or the storage's least value when that is before it.</summary>
    private long Start(long days) => days >= _firstDay ? days * _day : long.MinValue;
}

/// <summary>A date, an <c>i32</c> of days since the epoch, truncated to the first day of a week, a month, a quarter or a year.</summary>
internal sealed class DateTruncate(CalendarUnit unit) : ValueFunction
{
    internal override string Text { get; } = $"Truncate({unit})";

    internal override PType Storage => PType.I32;

    protected override Int128 Reach => 368;

    protected override Int128 Image(Int128 value) => Day((long)value);

    protected override void Map(PType ptype, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ReadOnlySpan<int> from = MemoryMarshal.Cast<byte, int>(source);
        Span<int> into = MemoryMarshal.Cast<byte, int>(destination);
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = Day(from[i]);
        }
    }

    private int Day(long days) => (int)Math.Max(Civil.TruncateDays(days, unit), int.MinValue);
}

/// <summary>An instant put in the bucket of <paramref name="width"/> units holding it, counted from the epoch: <c>⌊t / w⌋ × w</c>.</summary>
internal sealed class InstantBucket(long width, TimeSpan span) : ValueFunction
{
    internal override string Text { get; } = $"Bucket({span})";

    internal override PType Storage => PType.I64;

    protected override Int128 Reach => width;

    protected override Int128 Image(Int128 value) => Civil.FloorTo((long)value, width);

    protected override void Map(PType ptype, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ReadOnlySpan<long> from = MemoryMarshal.Cast<byte, long>(source);
        Span<long> into = MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = Civil.FloorTo(from[i], width);
        }
    }
}

/// <summary>
/// A number put in the bucket of the width holding it: <c>⌊v / w⌋ × w</c>, in integers for an
/// integer, saturating at the type's least value, and rounded to the type for a float.
/// </summary>
internal sealed class NumberBucket : ValueFunction
{
    private readonly Int128 _integer;
    private readonly double _float;

    internal NumberBucket(PType storage, Int128 integer, double floating, string width)
    {
        Storage = storage;
        _integer = integer;
        _float = floating;
        Text = $"Bucket({width})";
    }

    internal override string Text { get; }

    internal override PType Storage { get; }

    protected override Int128 Reach => _integer;

    protected override Int128 Image(Int128 value) => Int128.Max(Floor(value), Integers(Storage).Min);

    protected override double Image(double value)
    {
        double floor = Math.Floor(value / _float) * _float;
        return Storage switch
        {
            PType.F16 => (double)(Half)floor,
            PType.F32 => (float)floor,
            _ => floor,
        };
    }

    protected override void Map(PType ptype, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        switch (ptype)
        {
            case PType.I8:
                MapIntegers<sbyte>(source, destination);
                break;
            case PType.I16:
                MapIntegers<short>(source, destination);
                break;
            case PType.I32:
                MapIntegers<int>(source, destination);
                break;
            case PType.I64:
                MapIntegers<long>(source, destination);
                break;
            case PType.U8:
                MapIntegers<byte>(source, destination);
                break;
            case PType.U16:
                MapIntegers<ushort>(source, destination);
                break;
            case PType.U32:
                MapIntegers<uint>(source, destination);
                break;
            case PType.U64:
                MapIntegers<ulong>(source, destination);
                break;
            case PType.F16:
                MapFloats<Half>(source, destination);
                break;
            case PType.F32:
                MapFloats<float>(source, destination);
                break;
            default:
                MapFloats<double>(source, destination);
                break;
        }
    }

    private Int128 Floor(Int128 value)
    {
        (Int128 quotient, Int128 remainder) = Int128.DivRem(value, _integer);
        return (remainder < 0 ? quotient - 1 : quotient) * _integer;
    }

    private void MapIntegers<T>(ReadOnlySpan<byte> source, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> from = MemoryMarshal.Cast<byte, T>(source);
        Span<T> into = MemoryMarshal.Cast<byte, T>(destination);
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = T.CreateSaturating(Floor(Int128.CreateTruncating(from[i])));
        }
    }

    private void MapFloats<T>(ReadOnlySpan<byte> source, Span<byte> destination)
        where T : unmanaged, IFloatingPoint<T>
    {
        ReadOnlySpan<T> from = MemoryMarshal.Cast<byte, T>(source);
        Span<T> into = MemoryMarshal.Cast<byte, T>(destination);
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = T.CreateTruncating(Math.Floor(double.CreateTruncating(from[i]) / _float) * _float);
        }
    }
}

/// <summary>The calendar arithmetic of the functions: floor division, and the proleptic Gregorian calendar in days.</summary>
internal static class Civil
{
    /// <summary>The ticks of <paramref name="unit"/> in a second.</summary>
    internal static long PerSecond(TimeUnit unit) => unit switch
    {
        TimeUnit.Nanoseconds => 1_000_000_000,
        TimeUnit.Microseconds => 1_000_000,
        TimeUnit.Milliseconds => 1_000,
        _ => 1,
    };

    /// <summary>The quotient rounded toward negative infinity.</summary>
    internal static long FloorDiv(long value, long divisor)
    {
        long quotient = Math.DivRem(value, divisor, out long remainder);
        return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }

    /// <summary>
    /// The greatest multiple of <paramref name="unit"/>, a positive width, at or below
    /// <paramref name="value"/>; <see cref="long.MinValue"/> when that multiple lies below it.
    /// </summary>
    internal static long FloorTo(long value, long unit)
    {
        long remainder = value % unit;
        long floor = value - (remainder < 0 ? remainder + unit : remainder);

        // Below the least value, the subtraction comes back round above the value.
        return floor <= value ? floor : long.MinValue;
    }

    /// <summary>The first day of the unit holding day <paramref name="days"/> since 1970-01-01.</summary>
    internal static long TruncateDays(long days, CalendarUnit unit)
    {
        switch (unit)
        {
            case CalendarUnit.Week:
                // 1970-01-01 was a Thursday, three days after a Monday.
                return days - (days + 3 - (FloorDiv(days + 3, 7) * 7));
            case CalendarUnit.Month:
            case CalendarUnit.Quarter:
            case CalendarUnit.Year:
            {
                (long year, int month) = YearMonth(days);
                int first = unit == CalendarUnit.Month ? month : unit == CalendarUnit.Quarter ? ((month - 1) / 3 * 3) + 1 : 1;
                return DaysFromCivil(year, first, 1);
            }

            default:
                return days;
        }
    }

    /// <summary>The year and month of day <paramref name="days"/> since 1970-01-01 (Hinnant's civil_from_days).</summary>
    internal static (long Year, int Month) YearMonth(long days)
    {
        long z = days + 719_468;
        long era = (z >= 0 ? z : z - 146_096) / 146_097;
        long doe = z - (era * 146_097);
        long yoe = (doe - (doe / 1_460) + (doe / 36_524) - (doe / 146_096)) / 365;
        long doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        long mp = ((5 * doy) + 2) / 153;
        int month = (int)(mp < 10 ? mp + 3 : mp - 9);
        return ((yoe + (era * 400)) + (month <= 2 ? 1 : 0), month);
    }

    /// <summary>The days since 1970-01-01 of a date (Hinnant's days_from_civil).</summary>
    internal static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        long era = (year >= 0 ? year : year - 399) / 400;
        long yoe = year - (era * 400);
        long doy = ((153 * (month > 2 ? month - 3 : month + 9)) + 2) / 5 + day - 1;
        long doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146_097) + doe - 719_468;
    }
}

/// <summary>Where an instant is among a zone's offsets: the year last looked at, its transitions and their offsets.</summary>
internal struct ZoneCursor
{
    internal long From;
    internal long Until;
    internal long[]? Starts;
    internal long[]? Offsets;
}

/// <summary>
/// The offsets of a zone, a year at a time: the transitions of a year are found the first time an
/// instant of it is met, a day's offset at a time and a bisection where two days differ; an instant's
/// offset is then a look among the two or three intervals of its year.
/// </summary>
internal sealed class ZoneOffsets
{
    private static readonly ConcurrentDictionary<string, ZoneOffsets> Zones = new ConcurrentDictionary<string, ZoneOffsets>(StringComparer.Ordinal);

    private readonly TimeZoneInfo _zone;
    private readonly ConcurrentDictionary<long, (long[] Starts, long[] Offsets)> _years = new ConcurrentDictionary<long, (long[], long[])>();

    private ZoneOffsets(TimeZoneInfo zone) => _zone = zone;

    internal static ZoneOffsets For(TimeZoneInfo zone) => Zones.GetOrAdd(zone.Id, _ => new ZoneOffsets(zone));

    /// <summary>The zone's offset at <paramref name="instant"/>, in the instant's ticks.</summary>
    internal long OffsetAt(long instant, long second, ref ZoneCursor cursor) => OffsetSeconds(Civil.FloorDiv(instant, second), ref cursor) * second;

    /// <summary>
    /// The instant a local start <paramref name="local"/> is: with the value's own offset where the
    /// zone has that offset there, which keeps the two hours of an autumn's repeated hour apart; with
    /// the zone's offset there otherwise, a day's start before a transition the day holds.
    /// </summary>
    internal long Back(long local, long offset, long second, ref ZoneCursor cursor)
    {
        long utc = local - offset;
        long found = OffsetAt(utc, second, ref cursor);
        if (found == offset)
        {
            return utc;
        }

        long other = local - found;
        return OffsetAt(other, second, ref cursor) == found ? other : utc;
    }

    private long OffsetSeconds(long utc, ref ZoneCursor cursor)
    {
        if (cursor.Starts is null || utc < cursor.From || utc >= cursor.Until)
        {
            long year = Civil.YearMonth(Civil.FloorDiv(utc, 86_400)).Year;
            (cursor.Starts, cursor.Offsets) = _years.GetOrAdd(year, Transitions);
            cursor.From = Civil.DaysFromCivil(year, 1, 1) * 86_400;
            cursor.Until = Civil.DaysFromCivil(year + 1, 1, 1) * 86_400;
        }

        long[] starts = cursor.Starts;
        int at = starts.Length - 1;
        while (at > 0 && starts[at] > utc)
        {
            at--;
        }

        return cursor.Offsets![at];
    }

    private (long[] Starts, long[] Offsets) Transitions(long year)
    {
        // The zone's rules hold between the years a DateTime holds; beyond, the nearest year's.
        int held = (int)Math.Clamp(year, 1, 9_998);
        DateTime day = new DateTime(held, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long first = Civil.DaysFromCivil(year, 1, 1) * 86_400;
        List<long> starts = [first];
        List<long> offsets = [Offset(day)];
        while (day.Year == held)
        {
            DateTime next = day.AddDays(1);
            long now = Offset(next);
            if (now != offsets[^1])
            {
                starts.Add(first + (long)(Bisect(day, next, offsets[^1]) - new DateTime(held, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                offsets.Add(now);
            }

            day = next;
        }

        return ([.. starts], [.. offsets]);
    }

    /// <summary>The first second of (<paramref name="from"/>, <paramref name="to"/>] whose offset is not <paramref name="offset"/>.</summary>
    private DateTime Bisect(DateTime from, DateTime to, long offset)
    {
        while ((to - from).TotalSeconds > 1)
        {
            DateTime middle = from.AddSeconds(Math.Floor((to - from).TotalSeconds / 2));
            if (Offset(middle) == offset)
            {
                from = middle;
            }
            else
            {
                to = middle;
            }
        }

        return to;
    }

    private long Offset(DateTime utc) => (long)_zone.GetUtcOffset(utc).TotalSeconds;
}
