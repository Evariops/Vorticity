using System.Diagnostics;

namespace Vorticity.Compute;

/// <summary>
/// How many rows of a range a predicate selects, refuses and leaves unknown, when the statistics
/// decide it. Statistics say how many rows, never which, so the three counts are independent and
/// each carries its own decided flag; combining two verdicts is three-valued logic over counts,
/// which only closes when one side is uniform or the two sides are unknown on the very same rows,
/// the case a verdict tracks by naming the column whose nulls its unknowns are. An undecided count
/// is always safe here, whereas a wrong one would be a wrong answer.
/// </summary>
internal readonly struct RangeVerdict
{
    private RangeVerdict(
        long rows,
        long trueCount, bool trueKnown,
        long falseCount, bool falseKnown,
        long unknownCount, bool unknownKnown,
        ZoneColumn? nullsOf)
    {
        // Two decided counts decide the third.
        if (trueKnown && falseKnown && !unknownKnown)
        {
            unknownCount = rows - trueCount - falseCount;
            unknownKnown = true;
        }
        else if (trueKnown && unknownKnown && !falseKnown)
        {
            falseCount = rows - trueCount - unknownCount;
            falseKnown = true;
        }
        else if (falseKnown && unknownKnown && !trueKnown)
        {
            trueCount = rows - falseCount - unknownCount;
            trueKnown = true;
        }

        Debug.Assert(!trueKnown || (trueCount >= 0 && trueCount <= rows), "a true count is within the range");
        Debug.Assert(!falseKnown || (falseCount >= 0 && falseCount <= rows), "a false count is within the range");
        Debug.Assert(!unknownKnown || (unknownCount >= 0 && unknownCount <= rows), "an unknown count is within the range");
        Debug.Assert(!(trueKnown && falseKnown) || trueCount + falseCount + unknownCount == rows, "the three counts cover the range");
        Debug.Assert(nullsOf is null || unknownKnown, "a verdict names its nulls only when it counted them");

        Rows = rows;
        TrueCount = trueKnown ? trueCount : 0;
        TrueKnown = trueKnown;
        FalseCount = falseKnown ? falseCount : 0;
        FalseKnown = falseKnown;
        UnknownCount = unknownKnown ? unknownCount : 0;
        UnknownKnown = unknownKnown;
        NullsOf = nullsOf;
    }

    /// <summary>The range's length.</summary>
    internal long Rows { get; }

    /// <summary>Rows on which the predicate is true; zero unless <see cref="TrueKnown"/>.</summary>
    internal long TrueCount { get; }

    /// <summary>Whether <see cref="TrueCount"/> is decided.</summary>
    internal bool TrueKnown { get; }

    /// <summary>Rows on which the predicate is false; zero unless <see cref="FalseKnown"/>.</summary>
    internal long FalseCount { get; }

    /// <summary>Whether <see cref="FalseCount"/> is decided.</summary>
    internal bool FalseKnown { get; }

    /// <summary>Rows on which the predicate is unknown; zero unless <see cref="UnknownKnown"/>.</summary>
    internal long UnknownCount { get; }

    /// <summary>Whether <see cref="UnknownCount"/> is decided.</summary>
    internal bool UnknownKnown { get; }

    /// <summary>
    /// The column whose null rows are exactly the unknown rows, when the unknown rows are one
    /// column's nulls and nothing else; null otherwise.
    /// </summary>
    /// <remarks>
    /// What lets two predicates on the same column combine: their unknown rows coincide, so a
    /// conjunction is unknown on those and decided on the rest, which the counts alone could not
    /// say.
    /// </remarks>
    internal ZoneColumn? NullsOf { get; }

    /// <summary>Whether every count is decided.</summary>
    internal bool IsExact => TrueKnown && FalseKnown && UnknownKnown;

    /// <summary>True on every row.</summary>
    internal bool IsAllTrue => TrueKnown && TrueCount == Rows;

    /// <summary>True on no row.</summary>
    internal bool IsNoneTrue => TrueKnown && TrueCount == 0;

    /// <summary>False on every row.</summary>
    internal bool IsAllFalse => FalseKnown && FalseCount == Rows;

    /// <summary>False on no row: true wherever it is decided.</summary>
    internal bool IsNoneFalse => FalseKnown && FalseCount == 0;

    /// <summary>Unknown on every row.</summary>
    internal bool IsAllUnknown => UnknownKnown && UnknownCount == Rows;

    /// <summary>Unknown on no row.</summary>
    internal bool IsNoneUnknown => UnknownKnown && UnknownCount == 0;

    /// <summary>Nothing decided over <paramref name="rows"/> rows.</summary>
    /// <param name="rows">The range's length.</param>
    internal static RangeVerdict Undecided(long rows) =>
        new RangeVerdict(rows, 0, false, 0, false, 0, false, null);

    /// <summary>A verdict from the counts that are decided; null is undecided.</summary>
    /// <param name="rows">The range's length.</param>
    /// <param name="trueCount">The true rows, when decided.</param>
    /// <param name="falseCount">The false rows, when decided.</param>
    /// <param name="unknownCount">The unknown rows, when decided.</param>
    /// <param name="nullsOf">The column whose nulls the unknown rows are, if they are.</param>
    internal static RangeVerdict Of(
        long rows, long? trueCount, long? falseCount, long? unknownCount, ZoneColumn? nullsOf = null) =>
        new RangeVerdict(
            rows,
            trueCount ?? 0, trueCount.HasValue,
            falseCount ?? 0, falseCount.HasValue,
            unknownCount ?? 0, unknownCount.HasValue,
            unknownCount.HasValue ? nullsOf : null);

    /// <summary>True on every row.</summary>
    /// <param name="rows">The range's length.</param>
    internal static RangeVerdict AllTrue(long rows) => Of(rows, rows, 0, 0);

    /// <summary>False on every row.</summary>
    /// <param name="rows">The range's length.</param>
    internal static RangeVerdict AllFalse(long rows) => Of(rows, 0, rows, 0);

    /// <summary>Unknown on every row.</summary>
    /// <param name="rows">The range's length.</param>
    /// <param name="nullsOf">The column whose nulls those rows all are, if they are.</param>
    internal static RangeVerdict AllUnknown(long rows, ZoneColumn? nullsOf = null) =>
        Of(rows, 0, 0, rows, nullsOf);

    /// <summary>
    /// This verdict restricted to <paramref name="part"/> of its rows that the statistics cannot
    /// tell apart from the rest: a count survives only when it was uniform over the whole -- none
    /// or all.
    /// </summary>
    /// <param name="part">The sub-range's length.</param>
    internal RangeVerdict Restrict(long part)
    {
        bool trueKnown = IsNoneTrue || IsAllTrue;
        bool falseKnown = IsNoneFalse || IsAllFalse;
        bool unknownKnown = IsNoneUnknown || IsAllUnknown;
        return new RangeVerdict(
            part,
            IsAllTrue ? part : 0, trueKnown,
            IsAllFalse ? part : 0, falseKnown,
            IsAllUnknown ? part : 0, unknownKnown,
            unknownKnown ? NullsOf : null);
    }

    /// <summary>
    /// The sum of two verdicts over adjacent, disjoint ranges: a count is decided when both parts
    /// decided it, and the unknown rows stay one column's nulls only when they were on both sides.
    /// </summary>
    /// <param name="left">One part.</param>
    /// <param name="right">The other.</param>
    internal static RangeVerdict Concat(RangeVerdict left, RangeVerdict right) =>
        new RangeVerdict(
            left.Rows + right.Rows,
            left.TrueCount + right.TrueCount, left.TrueKnown && right.TrueKnown,
            left.FalseCount + right.FalseCount, left.FalseKnown && right.FalseKnown,
            left.UnknownCount + right.UnknownCount, left.UnknownKnown && right.UnknownKnown,
            ReferenceEquals(left.NullsOf, right.NullsOf) ? left.NullsOf : null);

    /// <summary><c>NOT a</c>: true and false swap, unknown stays.</summary>
    /// <param name="a">The operand.</param>
    internal static RangeVerdict Not(RangeVerdict a) =>
        new RangeVerdict(
            a.Rows,
            a.FalseCount, a.FalseKnown,
            a.TrueCount, a.TrueKnown,
            a.UnknownCount, a.UnknownKnown,
            a.NullsOf);

    /// <summary><c>a AND b</c> over the same range.</summary>
    /// <param name="a">The left operand.</param>
    /// <param name="b">The right operand.</param>
    internal static RangeVerdict And(RangeVerdict a, RangeVerdict b)
    {
        Debug.Assert(a.Rows == b.Rows, "the operands cover the same range");
        long rows = a.Rows;
        bool sameNulls = a.NullsOf is not null && ReferenceEquals(a.NullsOf, b.NullsOf);

        // True where both are true. A side that is true wherever it is decided, and decided on
        // every row the other side is, leaves the other side's trues.
        long? trueCount = null;
        if (a.IsAllTrue || (sameNulls && a.IsNoneFalse))
        {
            trueCount = b.TrueKnown ? b.TrueCount : null;
        }
        else if (b.IsAllTrue || (sameNulls && b.IsNoneFalse))
        {
            trueCount = a.TrueKnown ? a.TrueCount : null;
        }
        else if (a.IsNoneTrue || b.IsNoneTrue)
        {
            trueCount = 0;
        }

        // False where either is false.
        long? falseCount = null;
        if (a.IsAllFalse || b.IsAllFalse)
        {
            falseCount = rows;
        }
        else if (a.IsNoneFalse || (sameNulls && b.IsNoneTrue))
        {
            // Nothing false on a, or b false on every row a could be: the union is b's falses.
            falseCount = b.FalseKnown ? b.FalseCount : null;
        }
        else if (b.IsNoneFalse || (sameNulls && a.IsNoneTrue))
        {
            falseCount = a.FalseKnown ? a.FalseCount : null;
        }

        // Unknown where one is unknown and the other is not false.
        long? unknownCount = null;
        ZoneColumn? nullsOf = null;
        if (a.IsAllTrue)
        {
            unknownCount = b.UnknownKnown ? b.UnknownCount : null;
            nullsOf = b.NullsOf;
        }
        else if (b.IsAllTrue)
        {
            unknownCount = a.UnknownKnown ? a.UnknownCount : null;
            nullsOf = a.NullsOf;
        }
        else if (a.IsAllFalse || b.IsAllFalse)
        {
            unknownCount = 0;
        }
        else if (sameNulls)
        {
            // The same rows are unknown on both sides and every other row is decided on both.
            unknownCount = a.UnknownCount;
            nullsOf = a.NullsOf;
        }
        else if (a.IsAllUnknown && b.FalseKnown)
        {
            // unknown AND false = false; unknown AND anything else = unknown.
            unknownCount = rows - b.FalseCount;
        }
        else if (b.IsAllUnknown && a.FalseKnown)
        {
            unknownCount = rows - a.FalseCount;
        }
        else if (a.IsNoneUnknown && b.IsNoneUnknown)
        {
            unknownCount = 0;
        }

        return Of(rows, trueCount, falseCount, unknownCount, nullsOf);
    }

    /// <summary><c>a OR b</c> over the same range.</summary>
    /// <param name="a">The left operand.</param>
    /// <param name="b">The right operand.</param>
    internal static RangeVerdict Or(RangeVerdict a, RangeVerdict b)
    {
        Debug.Assert(a.Rows == b.Rows, "the operands cover the same range");
        long rows = a.Rows;
        bool sameNulls = a.NullsOf is not null && ReferenceEquals(a.NullsOf, b.NullsOf);

        // True where either is true.
        long? trueCount = null;
        if (a.IsAllTrue || b.IsAllTrue)
        {
            trueCount = rows;
        }
        else if (a.IsNoneTrue || (sameNulls && b.IsNoneFalse))
        {
            // Nothing true on a, or b true on every row a could be: the union is b's trues.
            trueCount = b.TrueKnown ? b.TrueCount : null;
        }
        else if (b.IsNoneTrue || (sameNulls && a.IsNoneFalse))
        {
            trueCount = a.TrueKnown ? a.TrueCount : null;
        }

        // False where both are false.
        long? falseCount = null;
        if (a.IsAllFalse || (sameNulls && a.IsNoneTrue))
        {
            // Nothing true on a, so a is false wherever it is decided: the intersection is b's
            // falses.
            falseCount = b.FalseKnown ? b.FalseCount : null;
        }
        else if (b.IsAllFalse || (sameNulls && b.IsNoneTrue))
        {
            falseCount = a.FalseKnown ? a.FalseCount : null;
        }
        else if (a.IsNoneFalse || b.IsNoneFalse)
        {
            falseCount = 0;
        }

        // Unknown where neither is true and one is unknown.
        long? unknownCount = null;
        ZoneColumn? nullsOf = null;
        if (a.IsAllTrue || b.IsAllTrue)
        {
            unknownCount = 0;
        }
        else if (a.IsAllFalse)
        {
            unknownCount = b.UnknownKnown ? b.UnknownCount : null;
            nullsOf = b.NullsOf;
        }
        else if (b.IsAllFalse)
        {
            unknownCount = a.UnknownKnown ? a.UnknownCount : null;
            nullsOf = a.NullsOf;
        }
        else if (sameNulls)
        {
            unknownCount = a.UnknownCount;
            nullsOf = a.NullsOf;
        }
        else if (a.IsAllUnknown && b.TrueKnown)
        {
            // unknown OR true = true; unknown OR anything else = unknown.
            unknownCount = rows - b.TrueCount;
        }
        else if (b.IsAllUnknown && a.TrueKnown)
        {
            unknownCount = rows - a.TrueCount;
        }
        else if (a.IsNoneUnknown && b.IsNoneUnknown)
        {
            unknownCount = 0;
        }

        return Of(rows, trueCount, falseCount, unknownCount, nullsOf);
    }
}
