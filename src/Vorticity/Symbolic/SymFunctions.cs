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
        VortexType type = source.Type;
        switch (type.ExtensionId)
        {
            case ExtensionIds.Timestamp:
            {
                // An instant bound as DateTimeOffset is on its column's zone; one bound as DateTime, as stored.
                TimeZoneInfo? calendar = zone ?? (offsets && type.TimeZone is not null ? type.ZoneInfo : null);
                return With<T>(source, new InstantTruncate(unit, type.Unit ?? TimeUnit.Microseconds, calendar));
            }

            // A date is days in an i32, or, from another writer, milliseconds in an i64: an instant's.
            case ExtensionIds.Date when unit >= CalendarUnit.Day:
                return With<T>(source, type.Unit == TimeUnit.Milliseconds ? new InstantTruncate(unit, TimeUnit.Milliseconds, null) : new DateTruncate(unit));
            case ExtensionIds.Date:
                throw new ArgumentOutOfRangeException(nameof(unit), unit, $"'{source.Field.Path}' is a date, whose units start at the day.");
            default:
                throw new ArgumentException($"'{source.Field.Path}' is a column of {type}, which has no calendar.", nameof(column));
        }
    }

    /// <summary>An instant in the bucket of <paramref name="width"/> holding it, counted from the epoch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, or the column's unit does not divide it.</exception>
    internal static Sym<T> Bucket<T>(Sym<T> column, TimeSpan width)
    {
        ColumnSym source = Source(column);
        VortexType type = source.Type;
        if (type.ExtensionId != ExtensionIds.Timestamp)
        {
            throw new ArgumentException($"'{source.Field.Path}' is a column of {type}: a bucket of a time span is an instant's.", nameof(column));
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
            ? With<T>(source, new InstantBucket(units, width))
            : throw new ArgumentOutOfRangeException(nameof(width), width, $"'{source.Field.Path}' is stored in {unit}, which do not divide {width}.");
    }

    /// <summary>A number in the bucket of <paramref name="width"/> holding it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive, not finite, or not an integer for an integer column.</exception>
    internal static Sym<T> Bucket<T, TNumber>(Sym<T> column, TNumber width)
        where TNumber : INumber<TNumber>
    {
        ColumnSym source = Source(column);
        if (!(width > TNumber.Zero) || !TNumber.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive and finite.");
        }

        VortexType type = source.Type;
        string text = width.ToString(null, CultureInfo.InvariantCulture);
        if (type.Kind == VortexTypeKind.Primitive && type.PrimitiveType.IsFloat())
        {
            return With<T>(source, new NumberBucket(type.PrimitiveType, Int128.Zero, double.CreateChecked(width), text));
        }

        if (type.Kind == VortexTypeKind.Primitive && (type.PrimitiveType.IsSignedInteger() || type.PrimitiveType.IsUnsignedInteger()))
        {
            return TNumber.IsInteger(width)
                ? With<T>(source, new NumberBucket(type.PrimitiveType, Int128.CreateChecked(width), double.CreateChecked(width), text))
                : throw new ArgumentOutOfRangeException(nameof(width), width, $"'{source.Field.Path}' holds integers, whose buckets are an integer wide.");
        }

        throw new NotSupportedException($"'{source.Field.Path}' is a column of {type}: a bucket is a number's, of an integer or a float column.");
    }

    /// <summary>The column a function applies to: a column of the scan, or a function of one.</summary>
    private static ColumnSym Source<T>(Sym<T> column) =>
        column.Node is ColumnSym { Field: not Aggregating.ResultFieldExpr } source
            ? source
            : throw new InvalidOperationException($"'{column.Node}' is not a column of the scan: a function applies to a column, before any aggregate.");

    private static Sym<T> With<T>(ColumnSym source, ValueFunction function) =>
        new Sym<T>(new ColumnSym(new FunctionFieldExpr(source.Field, function), source.Type, source.Extensions, source.Binding, source.Member, source.FieldPath));
}
