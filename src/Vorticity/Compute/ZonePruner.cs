// Deciding whether a row range can contain a matching row, from zone maps alone - F6.
//
// THE INVARIANT THIS FILE EXISTS TO NOT BREAK, quoted from docs/08-semantics.md §1:
//
//     Pruning may never eliminate a row that full materialization would have returned.
//
// The converse -- pruning too little -- is only a performance loss. Every decision below is
// therefore skewed one way: anything unknown, absent, unresolvable or merely awkward answers "may
// match", and only a positive proof of impossibility prunes. That asymmetry is why `Unknown`
// bounds, missing aggregates and unsupported operators all take the same branch.
//
// The test is over a ROW RANGE rather than a single zone, because a split rarely lines up with a
// zone: a range may match if ANY zone overlapping it may match. And the combination across an
// expression is per-range, not per-row:
//
//     A AND B may match in R  <=  A may match in R  and  B may match in R
//
// which is weaker than the truth (the two could match on different rows of R) and therefore safe.
// Reading it the other way round -- pruning R when A and B cannot match the SAME row -- would need
// per-row evidence a zone map does not carry.
//
// NOT is handled by pushing it into the comparison rather than by negating a bound. Negating "this
// zone may contain a match" gives "this zone may contain a non-match", which is not the same
// question and is almost always true anyway.
using System;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Compute;

/// <summary>Zone-map pruning for one scan's filter.</summary>
internal sealed class ZonePruner
{
    private readonly VortexExpr _filter;
    private readonly ZoneColumn[] _columns;

    internal ZonePruner(VortexExpr filter, ZoneColumn[] columns)
    {
        _filter = filter;
        _columns = columns;
    }

    /// <summary>Whether any column carries usable statistics; a pruner with none never prunes.</summary>
    internal bool IsUseful
    {
        get
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                if (_columns[i].HasStatistics)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether <paramref name="rows"/> may contain a row the filter selects.</summary>
    /// <param name="rows">The candidate range, in file row coordinates.</param>
    internal bool MayMatch(RowRange rows) => MayMatch(_filter, rows, negated: false);

    private bool MayMatch(VortexExpr expr, RowRange rows, bool negated)
    {
        switch (expr.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)expr;
                ComparisonOp op = negated ? Negate(comparison.Op) : comparison.Op;
                return MayMatchComparison(comparison.Field, op, comparison.Value, rows);
            }

            case ExprKind.NullCheck:
            {
                NullCheckExpr check = (NullCheckExpr)expr;
                return MayMatchNullCheck(check.Field, check.IsNull != negated, rows);
            }

            case ExprKind.In:
            {
                InExpr membership = (InExpr)expr;
                if (negated)
                {
                    // NOT IN prunes nothing a zone map can prove; a zone almost always holds some
                    // value outside a small candidate set.
                    return true;
                }

                FilterLiteral[] literals = membership.Literals;
                for (int i = 0; i < literals.Length; i++)
                {
                    if (MayMatchComparison(membership.Field, ComparisonOp.Equal, literals[i], rows))
                    {
                        return true;
                    }
                }

                return false;
            }

            case ExprKind.Not:
                return MayMatch(((NotExpr)expr).Operand, rows, !negated);

            case ExprKind.Logical:
            {
                LogicalExpr logical = (LogicalExpr)expr;

                // De Morgan: under a negation an AND behaves as an OR and vice versa.
                bool isAnd = logical.IsAnd != negated;
                bool left = MayMatch(logical.Left, rows, negated);
                if (isAnd)
                {
                    return left && MayMatch(logical.Right, rows, negated);
                }

                return left || MayMatch(logical.Right, rows, negated);
            }

            default:
                return true;
        }
    }

    private bool MayMatchComparison(
        FieldExpr field, ComparisonOp op, FilterLiteral value, RowRange rows)
    {
        if (value.Kind == FilterLiteralKind.Null)
        {
            // Unknown for every row, so nothing is selected anywhere - but proving that is the
            // evaluator's job, and pruning on it would be a correctness claim resting on a
            // constant. Left alone.
            return true;
        }

        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        foreach (int zone in column.Zones(rows))
        {
            if (ZoneMayMatch(column, zone, op, value))
            {
                return true;
            }
        }

        return false;
    }

    private bool MayMatchNullCheck(FieldExpr field, bool isNull, RowRange rows)
    {
        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        foreach (int zone in column.Zones(rows))
        {
            ZoneBounds bounds = column.Bounds(zone);
            if (!bounds.HasNullCount)
            {
                return true;
            }

            long zoneRows = column.RowsInZone(zone);
            bool possible = isNull ? bounds.NullCount > 0 : bounds.NullCount < zoneRows;
            if (possible)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ZoneMayMatch(ZoneColumn column, int zone, ComparisonOp op, FilterLiteral value)
    {
        ZoneBounds bounds = column.Bounds(zone);

        // "a zone where null_count == row_count can be skipped for any predicate that is not
        // satisfiable by nulls" (docs/08-semantics.md §3). A comparison is never satisfiable by a
        // null, so an all-null zone cannot match one.
        if (bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone))
        {
            return false;
        }

        switch (op)
        {
            case ComparisonOp.Greater:
            case ComparisonOp.GreaterOrEqual:
                // x > k is impossible when every value is <= max < k.
                return !bounds.HasMax ||
                       Satisfiable(op, Compare(bounds.Max, value), upper: true);

            case ComparisonOp.Less:
            case ComparisonOp.LessOrEqual:
                return !bounds.HasMin ||
                       Satisfiable(op, Compare(bounds.Min, value), upper: false);

            case ComparisonOp.Equal:
                // k has to sit inside [min, max]. Inexact bounds only widen that interval, so the
                // containment test stays sound; what Inexact forbids is the OTHER shortcut,
                // "min == max means the zone is constant", which is not used here.
                if (bounds.HasMin && Compare(bounds.Min, value) is int low && low > 0)
                {
                    return false;
                }

                if (bounds.HasMax && Compare(bounds.Max, value) is int high && high < 0)
                {
                    return false;
                }

                return true;

            default:
                // NotEqual: a zone can only be ruled out when every value equals k, which needs
                // min == max AND both exact. Rare enough that the check earns nothing; kept as
                // "may match".
                return true;
        }
    }

    /// <summary>
    /// Whether the predicate can hold somewhere in a zone whose relevant bound compares
    /// <paramref name="order"/> against the constant.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="order">The sign of <c>bound - constant</c>.</param>
    /// <param name="upper">Whether the bound is the zone's maximum.</param>
    private static bool Satisfiable(ComparisonOp op, int order, bool upper) => op switch
    {
        // Some value may exceed k only if the largest one does.
        ComparisonOp.Greater => upper && order > 0,
        ComparisonOp.GreaterOrEqual => upper && order >= 0,

        // Some value may fall below k only if the smallest one does.
        ComparisonOp.Less => !upper && order < 0,
        ComparisonOp.LessOrEqual => !upper && order <= 0,
        _ => true,
    };

    /// <summary>
    /// Orders a bound against a constant, in the comparison kernels' own domain.
    /// </summary>
    /// <returns>
    /// The sign of <c>bound - value</c>, or <c>0</c> when the two are not comparable -- which makes
    /// every caller fall back to "may match" rather than guessing an order.
    /// </returns>
    private static int Compare(FilterLiteral bound, FilterLiteral value)
    {
        if (bound.Kind == value.Kind)
        {
            return bound.Kind switch
            {
                FilterLiteralKind.Bool => bound.BoolValue.CompareTo(value.BoolValue),
                FilterLiteralKind.Signed => bound.SignedValue.CompareTo(value.SignedValue),
                FilterLiteralKind.Unsigned => bound.UnsignedValue.CompareTo(value.UnsignedValue),
                FilterLiteralKind.Float => CompareFloat(bound.FloatValue, value.FloatValue),
                FilterLiteralKind.Bytes => Math.Sign(bound.BytesValue.SequenceCompareTo(value.BytesValue)),
                _ => 0,
            };
        }

        // Mixed integer signs, the one cross-kind pair a filter can produce.
        if (bound.Kind == FilterLiteralKind.Signed && value.Kind == FilterLiteralKind.Unsigned)
        {
            return bound.SignedValue < 0
                ? -1
                : ((ulong)bound.SignedValue).CompareTo(value.UnsignedValue);
        }

        if (bound.Kind == FilterLiteralKind.Unsigned && value.Kind == FilterLiteralKind.Signed)
        {
            return value.SignedValue < 0
                ? 1
                : bound.UnsignedValue.CompareTo((ulong)value.SignedValue);
        }

        if (bound.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned &&
            value.Kind == FilterLiteralKind.Float)
        {
            double left = bound.Kind == FilterLiteralKind.Signed
                ? bound.SignedValue
                : bound.UnsignedValue;
            return CompareFloat(left, value.FloatValue);
        }

        return 0;
    }

    /// <summary>
    /// Orders two floats, treating a NaN bound as "no information".
    /// </summary>
    /// <remarks>
    /// A zone's min/max exclude NaN upstream (docs/08-semantics.md §2), so a NaN bound should not
    /// occur; if one does, ordering it would license a prune from a value that compares false
    /// against everything.
    /// </remarks>
    private static int CompareFloat(double bound, double value) =>
        double.IsNaN(bound) || double.IsNaN(value) ? 0 : bound.CompareTo(value);

    private ZoneColumn? Find(FieldExpr field)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            if (ReferenceEquals(_columns[i].Field, field) ||
                string.Equals(_columns[i].Field.Path, field.Path, StringComparison.Ordinal))
            {
                return _columns[i];
            }
        }

        return null;
    }

    private static ComparisonOp Negate(ComparisonOp op) => op switch
    {
        ComparisonOp.Equal => ComparisonOp.NotEqual,
        ComparisonOp.NotEqual => ComparisonOp.Equal,
        ComparisonOp.Less => ComparisonOp.GreaterOrEqual,
        ComparisonOp.LessOrEqual => ComparisonOp.Greater,
        ComparisonOp.Greater => ComparisonOp.LessOrEqual,
        _ => ComparisonOp.Less,
    };
}
