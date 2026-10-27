using System;
using System.Collections.Generic;
using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>The shapes of a predicate an encoding may answer over its own values.</summary>
internal enum EncodedPredicateKind : byte
{
    /// <summary>The column compared with a literal.</summary>
    Compare = 0,

    /// <summary>The column between two literals, each bound open or closed.</summary>
    Between = 1,

    /// <summary>The column equal to one of a set of literals.</summary>
    In = 2,

    /// <summary>The column starting with a bytes literal.</summary>
    Prefix = 3,
}

/// <summary>
/// A predicate on one column that an encoding may answer without decoding the column: a comparison
/// with a literal, a range between two, a set of literals, or a prefix. Built once a scan and read by
/// every chunk the scan meets.
/// </summary>
/// <remarks>
/// <para>
/// An encoding of integers answers in its own domain, so a predicate that an integer column can hold
/// is also given as the values it selects (<see cref="Integers"/>, <see cref="Members"/>), worked out
/// here once, by the rules the comparison kernels follow over the decoded column: a literal of the
/// other signedness, a float, or one past the column's range settles as there.
/// </para>
/// <para>
/// A null literal, a null member and a literal of another kind are given no integer form: every row
/// is unknown, or the kernels refuse, and the encoding declines rather than answer otherwise.
/// </para>
/// </remarks>
internal sealed class EncodedPredicate
{
    private EncodedPredicate(
        EncodedPredicateKind kind, ComparisonOp op, FilterLiteral literal, FilterLiteral high, bool lowInclusive,
        bool highInclusive, FilterLiteral[] members)
    {
        Kind = kind;
        Op = op;
        Literal = literal;
        High = high;
        LowInclusive = lowInclusive;
        HighInclusive = highInclusive;
        Values = members;
        Integers = kind switch
        {
            EncodedPredicateKind.Compare => IntegerRange.OfComparison(op, literal, decimals: false),
            EncodedPredicateKind.Between => IntegerRange.OfBetween(literal, lowInclusive, high, highInclusive, decimals: false),
            _ => null,
        };
        Members = kind == EncodedPredicateKind.In ? IntegerMembers(members, decimals: false) : null;
        Decimals = kind switch
        {
            EncodedPredicateKind.Compare => IntegerRange.OfComparison(op, literal, decimals: true),
            EncodedPredicateKind.Between => IntegerRange.OfBetween(literal, lowInclusive, high, highInclusive, decimals: true),
            _ => null,
        };
        DecimalMembers = kind == EncodedPredicateKind.In ? IntegerMembers(members, decimals: true) : null;
    }

    /// <summary>The predicate's shape.</summary>
    internal EncodedPredicateKind Kind { get; }

    /// <summary>The comparison, for <see cref="EncodedPredicateKind.Compare"/>.</summary>
    internal ComparisonOp Op { get; }

    /// <summary>
    /// The literal compared with, the low bound of <see cref="EncodedPredicateKind.Between"/>, or the prefix of
    /// <see cref="EncodedPredicateKind.Prefix"/>.
    /// </summary>
    internal FilterLiteral Literal { get; }

    /// <summary>The high bound of <see cref="EncodedPredicateKind.Between"/>.</summary>
    internal FilterLiteral High { get; }

    /// <summary>Whether the low bound of <see cref="EncodedPredicateKind.Between"/> is included.</summary>
    internal bool LowInclusive { get; }

    /// <summary>Whether the high bound of <see cref="EncodedPredicateKind.Between"/> is included.</summary>
    internal bool HighInclusive { get; }

    /// <summary>The literals of <see cref="EncodedPredicateKind.In"/>, as given; empty otherwise.</summary>
    internal FilterLiteral[] Values { get; }

    /// <summary>
    /// The integers a comparison or a range selects, as a range of them; null for another shape, or a literal that is
    /// no number.
    /// </summary>
    internal IntegerRange? Integers { get; }

    /// <summary>
    /// The integers of <see cref="EncodedPredicateKind.In"/>, ascending and distinct, each a value some integer column
    /// can equal; null for another shape, or a set with a null or a member that is no number.
    /// </summary>
    internal Int128[]? Members { get; }

    /// <summary>
    /// The unscaled integers a comparison or a range of a decimal column selects, its literals read as the decimal
    /// comparison reads them (<see cref="ComparisonKernels.TryDecimal"/>); null for another shape or literal.
    /// </summary>
    internal IntegerRange? Decimals { get; }

    /// <summary>The unscaled integers of <see cref="EncodedPredicateKind.In"/> on a decimal column, as <see cref="Members"/>.</summary>
    internal Int128[]? DecimalMembers { get; }

    /// <summary><c>column op literal</c>.</summary>
    internal static EncodedPredicate Compare(ComparisonOp op, FilterLiteral literal) =>
        new EncodedPredicate(EncodedPredicateKind.Compare, op, literal, default, false, false, []);

    /// <summary>The column between <paramref name="low"/> and <paramref name="high"/>, each bound included or not.</summary>
    internal static EncodedPredicate Between(FilterLiteral low, bool lowInclusive, FilterLiteral high, bool highInclusive) =>
        new EncodedPredicate(EncodedPredicateKind.Between, ComparisonOp.Equal, low, high, lowInclusive, highInclusive, []);

    /// <summary>The column equal to one of <paramref name="members"/>.</summary>
    internal static EncodedPredicate In(FilterLiteral[] members) =>
        new EncodedPredicate(EncodedPredicateKind.In, ComparisonOp.Equal, default, default, false, false, members);

    /// <summary>The column starting with the bytes of <paramref name="prefix"/>.</summary>
    internal static EncodedPredicate Prefix(FilterLiteral prefix) =>
        new EncodedPredicate(EncodedPredicateKind.Prefix, ComparisonOp.Equal, prefix, default, false, false, []);

    /// <summary>
    /// The members as integers, ascending and distinct: a float member that is no integer equals no integer and is left
    /// out; null when one is null, which turns a miss unknown, or is no number.
    /// </summary>
    private static Int128[]? IntegerMembers(FilterLiteral[] members, bool decimals)
    {
        List<Int128> integers = new List<Int128>(members.Length);
        foreach (FilterLiteral member in members)
        {
            switch (member.Kind)
            {
                case FilterLiteralKind.Signed:
                    integers.Add(member.SignedValue);
                    break;
                case FilterLiteralKind.Bytes when decimals && ComparisonKernels.TryDecimal(member, out Types.Numerics.Int256 wide):
                    // A member past every unscaled value an integer holds equals none of them.
                    if (wide.TryToInt128(out Int128 narrow))
                    {
                        integers.Add(narrow);
                    }

                    break;
                case FilterLiteralKind.Unsigned when !decimals:
                    integers.Add(member.UnsignedValue);
                    break;
                case FilterLiteralKind.Float when !decimals:
                    double value = member.FloatValue;
                    if (!double.IsNaN(value) && Math.Floor(value) == value && Math.Abs(value) < IntegerRange.Beyond)
                    {
                        integers.Add((Int128)value);
                    }

                    break;
                default:
                    return null;
            }
        }

        integers.Sort();
        int distinct = 0;
        for (int i = 0; i < integers.Count; i++)
        {
            if (distinct == 0 || integers[i] != integers[distinct - 1])
            {
                integers[distinct++] = integers[i];
            }
        }

        return [.. integers.GetRange(0, distinct)];
    }
}

/// <summary>
/// The values of an integer column a predicate selects: those from <see cref="Low"/> to <see cref="High"/>, or when
/// <see cref="Negated"/> every other one. An empty range selects none, or every value when negated.
/// </summary>
/// <remarks>
/// <see cref="Int128"/> holds every value of every integer column, signed or not, and a bound past them, so a literal
/// of the other signedness or past the column's range needs no case of its own: it lies outside the values the column
/// holds, and clamping the range to them (<see cref="Within"/>) settles it.
/// </remarks>
internal readonly record struct IntegerRange(Int128 Low, Int128 High, bool Negated)
{
    /// <summary>A magnitude past every integer a column holds, which a float bound beyond it is clamped to.</summary>
    internal const double Beyond = 1e30;

    private static readonly Int128 Far = (Int128)Beyond;

    /// <summary>Whether the range holds no value.</summary>
    internal bool IsEmpty => Low > High;

    /// <summary>Whether <paramref name="value"/> is selected.</summary>
    internal bool Holds(Int128 value) => (value >= Low && value <= High) != Negated;

    /// <summary>The range cut to the values from <paramref name="least"/> to <paramref name="greatest"/>, which select as it does.</summary>
    internal IntegerRange Within(Int128 least, Int128 greatest) =>
        new IntegerRange(Int128.Max(Low, least), Int128.Min(High, greatest), Negated);

    /// <summary>
    /// The values <c>column op literal</c> selects; null for a literal that is no number. Of a decimal column when
    /// <paramref name="decimals"/>: its unscaled values, against a signed literal or one of 16 or 32 bytes.
    /// </summary>
    internal static IntegerRange? OfComparison(ComparisonOp op, FilterLiteral literal, bool decimals)
    {
        if (decimals)
        {
            if (literal.Kind != FilterLiteralKind.Signed && literal.Kind != FilterLiteralKind.Bytes
                || !ComparisonKernels.TryDecimal(literal, out Types.Numerics.Int256 wide))
            {
                return null;
            }

            // A literal past every unscaled value an integer holds settles as one at the clamp.
            return OfBound(op, wide.TryToInt128(out Int128 narrow) ? Int128.Clamp(narrow, -Far, Far) : wide.IsNegative ? -Far : Far);
        }

        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                return OfBound(op, literal.SignedValue);
            case FilterLiteralKind.Unsigned:
                return OfBound(op, literal.UnsignedValue);
            case FilterLiteralKind.Float:
                double value = literal.FloatValue;
                if (double.IsNaN(value))
                {
                    // Nothing equals or orders against a NaN, and everything is unequal to it.
                    return None(negated: op == ComparisonOp.NotEqual);
                }

                // As the kernels move a float onto the integers: an equality with a fraction holds for no row, and an
                // order holds for the integers on its side of it.
                double floor = Math.Floor(value);
                switch (op)
                {
                    case ComparisonOp.Equal:
                    case ComparisonOp.NotEqual:
                        return floor == value ? OfBound(op, Clamp(value)) : None(negated: op == ComparisonOp.NotEqual);
                    case ComparisonOp.Less:
                    case ComparisonOp.GreaterOrEqual:
                        return OfBound(op, Clamp(Math.Ceiling(value)));
                    default:
                        return OfBound(op, Clamp(floor));
                }

            default:
                return null;
        }
    }

    /// <summary>The values between two literals, each bound included or not; null for a bound that is no number.</summary>
    internal static IntegerRange? OfBetween(FilterLiteral low, bool lowInclusive, FilterLiteral high, bool highInclusive, bool decimals)
    {
        if (OfComparison(lowInclusive ? ComparisonOp.GreaterOrEqual : ComparisonOp.Greater, low, decimals) is not { } above
            || OfComparison(highInclusive ? ComparisonOp.LessOrEqual : ComparisonOp.Less, high, decimals) is not { } below)
        {
            return null;
        }

        // An order never negates: a NaN bound is an empty range, which the intersection keeps empty.
        return new IntegerRange(Int128.Max(above.Low, below.Low), Int128.Min(above.High, below.High), Negated: false);
    }

    /// <summary>The values <c>column op bound</c> selects, for an integral bound.</summary>
    private static IntegerRange OfBound(ComparisonOp op, Int128 bound) => op switch
    {
        ComparisonOp.Equal => new IntegerRange(bound, bound, Negated: false),
        ComparisonOp.NotEqual => new IntegerRange(bound, bound, Negated: true),
        ComparisonOp.Less => new IntegerRange(-Far, bound - 1, Negated: false),
        ComparisonOp.LessOrEqual => new IntegerRange(-Far, bound, Negated: false),
        ComparisonOp.Greater => new IntegerRange(bound + 1, Far, Negated: false),
        _ => new IntegerRange(bound, Far, Negated: false),
    };

    /// <summary>No value, or every value when <paramref name="negated"/>.</summary>
    private static IntegerRange None(bool negated) => new IntegerRange(1, 0, negated);

    /// <summary>An integral float as an integer, one past every column's values clamped to <see cref="Beyond"/>.</summary>
    private static Int128 Clamp(double value) =>
        value >= Beyond ? Far : value <= -Beyond ? -Far : (Int128)value;
}
