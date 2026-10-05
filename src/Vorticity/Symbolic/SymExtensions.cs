using System;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>The predicates that only some column types have: text matching, boolean tests, list membership.</summary>
/// <remarks>
/// The text predicates are generic over the text type so that <c>Sym&lt;string&gt;</c> and
/// <c>Sym&lt;string?&gt;</c> both take them without a nullability warning; only <see cref="string"/>
/// satisfies the constraint among the types a column maps to.
/// </remarks>
public static class SymExtensions
{
    extension<TText>(Sym<TText> column)
        where TText : IComparable<string?>?
    {
        /// <summary>The rows whose text begins with <paramref name="prefix"/>, compared as UTF-8 bytes.</summary>
        /// <param name="prefix">The prefix.</param>
        /// <returns>The predicate.</returns>
        public Predicate StartsWith(string prefix) => SymLowering.StringMatch(column.Column, StringMatchOp.StartsWith, prefix);

        /// <summary>The rows whose text contains <paramref name="text"/>, compared as UTF-8 bytes.</summary>
        /// <param name="text">The text sought.</param>
        /// <returns>The predicate.</returns>
        public Predicate Contains(string text) => SymLowering.StringMatch(column.Column, StringMatchOp.Contains, text);

        /// <summary>The rows whose text matches the SQL pattern: <c>%</c> any run, <c>_</c> one character.</summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="escape">The character that makes the next one literal.</param>
        /// <returns>The predicate.</returns>
        public Predicate Like(string pattern, char escape = '\\') => SymLowering.StringMatch(column.Column, StringMatchOp.Like, pattern, escape);
    }

    extension(Sym<DateTime> column)
    {
        /// <summary>The start of the calendar unit holding the instant, as it is stored: <c>r.At.Truncate(CalendarUnit.Day)</c>.</summary>
        /// <param name="unit">The unit.</param>
        /// <returns>The instants, truncated; a null stays null.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is not a calendar unit.</exception>
        public Sym<DateTime> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: false);

        /// <summary>The start of the calendar unit holding the instant on <paramref name="zone"/>'s calendar: a day in Paris starts at 22:00 or 23:00 UTC.</summary>
        /// <param name="unit">The unit.</param>
        /// <param name="zone">The zone whose calendar the units follow.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTime> Truncate(CalendarUnit unit, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);
            return SymFunctions.Truncate(column, unit, zone, offsets: false);
        }

        /// <summary>The start of the bucket of <paramref name="width"/> holding the instant, counted from 1970-01-01T00:00Z.</summary>
        /// <param name="width">The bucket's width, which the column's unit divides.</param>
        /// <returns>The instants, bucketed.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not positive, or the column's unit does not divide it.</exception>
        public Sym<DateTime> Bucket(TimeSpan width) => SymFunctions.Bucket(column, width);
    }

    extension(Sym<DateTime?> column)
    {
        /// <summary>The start of the calendar unit holding the instant, as it is stored; a null stays null.</summary>
        /// <param name="unit">The unit.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTime?> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: false);

        /// <summary>The start of the calendar unit holding the instant on <paramref name="zone"/>'s calendar.</summary>
        /// <param name="unit">The unit.</param>
        /// <param name="zone">The zone whose calendar the units follow.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTime?> Truncate(CalendarUnit unit, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);
            return SymFunctions.Truncate(column, unit, zone, offsets: false);
        }

        /// <summary>The start of the bucket of <paramref name="width"/> holding the instant, counted from 1970-01-01T00:00Z.</summary>
        /// <param name="width">The bucket's width, which the column's unit divides.</param>
        /// <returns>The instants, bucketed.</returns>
        public Sym<DateTime?> Bucket(TimeSpan width) => SymFunctions.Bucket(column, width);
    }

    extension(Sym<DateTimeOffset> column)
    {
        /// <summary>The start of the calendar unit holding the instant, on the calendar of its column's zone.</summary>
        /// <param name="unit">The unit.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTimeOffset> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: true);

        /// <summary>The start of the calendar unit holding the instant on <paramref name="zone"/>'s calendar.</summary>
        /// <param name="unit">The unit.</param>
        /// <param name="zone">The zone whose calendar the units follow.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTimeOffset> Truncate(CalendarUnit unit, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);
            return SymFunctions.Truncate(column, unit, zone, offsets: true);
        }

        /// <summary>The start of the bucket of <paramref name="width"/> holding the instant, counted from 1970-01-01T00:00Z.</summary>
        /// <param name="width">The bucket's width, which the column's unit divides.</param>
        /// <returns>The instants, bucketed.</returns>
        public Sym<DateTimeOffset> Bucket(TimeSpan width) => SymFunctions.Bucket(column, width);
    }

    extension(Sym<DateTimeOffset?> column)
    {
        /// <summary>The start of the calendar unit holding the instant, on the calendar of its column's zone; a null stays null.</summary>
        /// <param name="unit">The unit.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTimeOffset?> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: true);

        /// <summary>The start of the calendar unit holding the instant on <paramref name="zone"/>'s calendar.</summary>
        /// <param name="unit">The unit.</param>
        /// <param name="zone">The zone whose calendar the units follow.</param>
        /// <returns>The instants, truncated.</returns>
        public Sym<DateTimeOffset?> Truncate(CalendarUnit unit, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);
            return SymFunctions.Truncate(column, unit, zone, offsets: true);
        }

        /// <summary>The start of the bucket of <paramref name="width"/> holding the instant, counted from 1970-01-01T00:00Z.</summary>
        /// <param name="width">The bucket's width, which the column's unit divides.</param>
        /// <returns>The instants, bucketed.</returns>
        public Sym<DateTimeOffset?> Bucket(TimeSpan width) => SymFunctions.Bucket(column, width);
    }

    extension(Sym<DateOnly> column)
    {
        /// <summary>The first day of the week, month, quarter or year holding the date; a date's units start at the day.</summary>
        /// <param name="unit">The unit, from <see cref="CalendarUnit.Day"/> up.</param>
        /// <returns>The dates, truncated.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is finer than a day.</exception>
        public Sym<DateOnly> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: false);
    }

    extension(Sym<DateOnly?> column)
    {
        /// <summary>The first day of the week, month, quarter or year holding the date; a null stays null.</summary>
        /// <param name="unit">The unit, from <see cref="CalendarUnit.Day"/> up.</param>
        /// <returns>The dates, truncated.</returns>
        public Sym<DateOnly?> Truncate(CalendarUnit unit) => SymFunctions.Truncate(column, unit, null, offsets: false);
    }

    extension<T>(Sym<T> column)
        where T : System.Numerics.INumber<T>
    {
        /// <summary>The start of the bucket of <paramref name="width"/> holding the value: <c>⌊v / width⌋ × width</c>.</summary>
        /// <param name="width">The bucket's width: positive, and an integer for an integer column.</param>
        /// <returns>The values, bucketed; a NaN stays NaN.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not positive, or not an integer for an integer column.</exception>
        public Sym<T> Bucket(T width) => SymFunctions.Bucket(column, width);
    }

    extension<T>(Sym<T?> column)
        where T : struct, System.Numerics.INumber<T>
    {
        /// <summary>The start of the bucket of <paramref name="width"/> holding the value; a null stays null.</summary>
        /// <param name="width">The bucket's width: positive, and an integer for an integer column.</param>
        /// <returns>The values, bucketed.</returns>
        public Sym<T?> Bucket(T width) => SymFunctions.Bucket(column, width);
    }

    extension(Sym<bool> column)
    {
        /// <summary>The rows whose value is true.</summary>
        public Predicate IsTrue => column == true;

        /// <summary>The rows whose value is false.</summary>
        public Predicate IsFalse => column == false;
    }

    extension(Sym<bool?> column)
    {
        /// <summary>The rows whose value is true; a null is not.</summary>
        public Predicate IsTrue => column == true;

        /// <summary>The rows whose value is false; a null is not.</summary>
        public Predicate IsFalse => column == false;
    }

    extension<T>(Sym<ReadOnlyMemory<T>> list)
    {
        /// <summary>The rows whose list holds an element equal to <paramref name="value"/>.</summary>
        /// <param name="value">The element sought.</param>
        /// <returns>The predicate.</returns>
        public Predicate Contains(T value) => SymLowering.ListContains(list.Column, value);
    }
}
