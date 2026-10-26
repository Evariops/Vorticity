using System;
using System.Globalization;
using System.Numerics;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity;

/// <summary>
/// The value expressions a column takes, checked when the lambda runs: the function, its argument
/// and the column's type, a symbol of the same column through it.
/// </summary>
internal static class SymFunctions
{
    /// <summary>A time truncated to a calendar unit: as stored, on <paramref name="zone"/>'s calendar, or, for an instant bound as an offset, on its column's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The unit is undefined, or finer than a date's day.</exception>
    internal static Sym<T> Truncate<T>(Sym<T> column, CalendarUnit unit, TimeZoneInfo? zone, bool offsets)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a calendar unit.");
        }

        ColumnSym source = Source(column);
        return With<T>(source, Truncation(source.Type, source.Field.Path, unit, zone, offsets));
    }

    /// <summary>
    /// The truncation of a time column of <paramref name="type"/>, named <paramref name="path"/>, to a calendar unit: what
    /// <see cref="Truncate"/> applies, for a caller that names the column rather than binds it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The unit is undefined, or finer than a date's day.</exception>
    /// <exception cref="ArgumentException">The column holds no time.</exception>
    internal static ValueFunction Truncation(VortexType type, string path, CalendarUnit unit, TimeZoneInfo? zone, bool offsets)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a calendar unit.");
        }

        switch (type.ExtensionId)
        {
            case ExtensionIds.Timestamp:
            {
                // An instant bound as DateTimeOffset is on its column's zone; one bound as DateTime, as stored.
                TimeZoneInfo? calendar = zone ?? (offsets && type.TimeZone is not null ? type.ZoneInfo : null);
                return new InstantTruncate(unit, type.Unit ?? TimeUnit.Microseconds, calendar);
            }

            // A date is days in an i32, or, from another writer, milliseconds in an i64: an instant's.
            case ExtensionIds.Date when unit >= CalendarUnit.Day:
                return type.Unit == TimeUnit.Milliseconds ? new InstantTruncate(unit, TimeUnit.Milliseconds, null) : new DateTruncate(unit);
            case ExtensionIds.Date:
                throw new ArgumentOutOfRangeException(nameof(unit), unit, $"'{path}' is a date, whose units start at the day.");
            default:
                throw new ArgumentException($"'{path}' is a column of {type}, which has no calendar.", nameof(type));
        }
    }

    /// <summary>An instant in the bucket of <paramref name="width"/> holding it, counted from the epoch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, or the column's unit does not divide it.</exception>
    internal static Sym<T> Bucket<T>(Sym<T> column, TimeSpan width)
    {
        ColumnSym source = Source(column);
        return With<T>(source, Buckets(source.Type, source.Field.Path, width));
    }

    /// <summary>
    /// The buckets of <paramref name="width"/> of an instant column of <paramref name="type"/>, named <paramref name="path"/>:
    /// what <see cref="Bucket{T}(Sym{T}, TimeSpan)"/> applies, for a caller that names the column rather than binds it.
    /// </summary>
    /// <exception cref="ArgumentException">The column holds no instant.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, or the column's unit does not divide it.</exception>
    internal static ValueFunction Buckets(VortexType type, string path, TimeSpan width)
    {
        if (type.ExtensionId != ExtensionIds.Timestamp)
        {
            throw new ArgumentException($"'{path}' is a column of {type}: a bucket of a time span is an instant's.", nameof(type));
        }

        if (width <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive.");
        }

        // A TimeSpan counts 100 ns ticks; the column, its own unit.
        long ticks = width.Ticks;
        TimeUnit unit = type.Unit ?? TimeUnit.Microseconds;
        long units = unit switch
        {
            TimeUnit.Nanoseconds => ticks <= long.MaxValue / 100 ? ticks * 100 : -1,
            TimeUnit.Microseconds => ticks % 10 == 0 ? ticks / 10 : -1,
            TimeUnit.Milliseconds => ticks % 10_000 == 0 ? ticks / 10_000 : -1,
            _ => ticks % 10_000_000 == 0 ? ticks / 10_000_000 : -1,
        };

        return units > 0
            ? new InstantBucket(units, width)
            : throw new ArgumentOutOfRangeException(nameof(width), width, $"'{path}' is stored in {unit}, which do not divide {width}.");
    }

    /// <summary>A number in the bucket of <paramref name="width"/> holding it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, not finite, or not an integer for an integer column.</exception>
    internal static Sym<T> Bucket<T, TNumber>(Sym<T> column, TNumber width)
        where TNumber : INumber<TNumber>
    {
        ColumnSym source = Source(column);
        return With<T>(source, Buckets(source.Type, source.Field.Path, width));
    }

    /// <summary>
    /// The buckets of <paramref name="width"/> of a number column of <paramref name="type"/>, named <paramref name="path"/>:
    /// what <see cref="Bucket{T, TNumber}(Sym{T}, TNumber)"/> applies, for a caller that names the column rather than binds it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, not finite, or not an integer for an integer column.</exception>
    /// <exception cref="NotSupportedException">The column holds no number.</exception>
    internal static ValueFunction Buckets<TNumber>(VortexType type, string path, TNumber width)
        where TNumber : INumber<TNumber>
    {
        if (!(width > TNumber.Zero) || !TNumber.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive and finite.");
        }

        string text = width.ToString(null, CultureInfo.InvariantCulture);
        if (type.Kind == VortexTypeKind.Primitive && type.PrimitiveType.IsFloat())
        {
            return new NumberBucket(type.PrimitiveType, Int128.Zero, double.CreateChecked(width), text);
        }

        if (type.Kind == VortexTypeKind.Primitive && (type.PrimitiveType.IsSignedInteger() || type.PrimitiveType.IsUnsignedInteger()))
        {
            return TNumber.IsInteger(width)
                ? new NumberBucket(type.PrimitiveType, Int128.CreateChecked(width), double.CreateChecked(width), text)
                : throw new ArgumentOutOfRangeException(nameof(width), width, $"'{path}' holds integers, whose buckets are an integer wide.");
        }

        throw new NotSupportedException($"'{path}' is a column of {type}: a bucket is a number's, of an integer or a float column.");
    }

    /// <summary>The column a function applies to: a column of the scan, or a function of one.</summary>
    private static ColumnSym Source<T>(Sym<T> column) =>
        column.Node is ColumnSym { Field: not Aggregating.ResultFieldExpr } source
            ? source
            : throw new InvalidOperationException($"'{column.Node}' is not a column of the scan: a function applies to a column, before any aggregate.");

    private static Sym<T> With<T>(ColumnSym source, ValueFunction function) =>
        new Sym<T>(new ColumnSym(new FunctionFieldExpr(source.Field, function), source.Type, source.Extensions, source.Binding, source.Member, source.FieldPath));
}
