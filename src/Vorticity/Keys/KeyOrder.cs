// The key order of docs/12-index-reads.md §4.4, and the one a sorted column is actually in.
//
// TWO ORDERS, AND THEY ARE NOT THE SAME ONE. A cursor is an ordinal structure and needs a TOTAL
// order; a filter is a predicate structure and follows IEEE 754, where NaN is unordered
// (docs/08-semantics.md §2). The two coincide on every dtype but the floats, where they differ in
// exactly two places: the total order puts NaN at the ends and separates `-0.0` from `+0.0`, and
// IEEE orders neither.
//
// SO THE COMPARATOR BELONGS TO THE SOURCE, not to the cursor. `Total` is what §4.4 states and what
// `KeyCursor.Compare` exposes, because a consumer merging two cursors needs one answer. `Ieee` is
// what a `SortedColumn` source is sorted in, because `is_sorted` is computed with IEEE comparisons
// (vortex-array-0.86.1 `aggregate_fn/fns/is_sorted`): a column whose statistics claim it is sorted
// is non-decreasing in IEEE and may, across a `-0.0` / `+0.0` pair, descend in the total order. A
// bisection in the wrong one of the two would miss a key, so each source names its own.
using System;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>Orders two keys.</summary>
internal static class KeyOrder
{
    /// <summary>
    /// The total order of docs/12-index-reads.md §4.4: numeric for integers, bytewise for strings,
    /// and for floats the order of docs/06-row-encoding.md §3 -- negative NaN, then the negatives
    /// down to <c>-0.0</c>, then <c>+0.0</c> up through the positives, then positive NaN.
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sign of <c>left - right</c>; <c>0</c> when they are the same key.</returns>
    /// <exception cref="ArgumentException">The two are of different domains.</exception>
    internal static int Total(FilterLiteral left, FilterLiteral right)
    {
        RequireSameKind(left, right);
        return left.Kind switch
        {
            FilterLiteralKind.Bool => left.BoolValue.CompareTo(right.BoolValue),
            FilterLiteralKind.Signed => left.SignedValue.CompareTo(right.SignedValue),
            FilterLiteralKind.Unsigned => left.UnsignedValue.CompareTo(right.UnsignedValue),
            FilterLiteralKind.Float => TotalFloat(left.FloatValue, right.FloatValue),
            FilterLiteralKind.Bytes => Math.Sign(left.BytesValue.SequenceCompareTo(right.BytesValue)),
            _ => 0,
        };
    }

    /// <summary>
    /// The order a <c>SortedColumn</c> source is in: the total order everywhere but the floats,
    /// which follow IEEE, so that <c>-0.0</c> and <c>+0.0</c> are ONE key.
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sign of <c>left - right</c>.</returns>
    /// <remarks>
    /// A NaN cannot reach this: a float column holding one is not <c>is_sorted</c>, since the
    /// reference computes that flag with the same IEEE comparisons, and every comparison against a
    /// NaN is false.
    /// </remarks>
    internal static int Ieee(FilterLiteral left, FilterLiteral right)
    {
        RequireSameKind(left, right);
        return left.Kind == FilterLiteralKind.Float
            ? left.FloatValue.CompareTo(right.FloatValue)
            : Total(left, right);
    }

    /// <summary>
    /// Whether <paramref name="literal"/> can be a key of a column whose keys are
    /// <paramref name="kind"/>.
    /// </summary>
    /// <param name="literal">The literal a caller seeks with.</param>
    /// <param name="kind">The column's key domain.</param>
    internal static bool Fits(FilterLiteral literal, FilterLiteralKind kind) =>
        literal.Kind == kind && kind != FilterLiteralKind.Null;

    private static void RequireSameKind(FilterLiteral left, FilterLiteral right)
    {
        if (left.Kind != right.Kind)
        {
            throw new ArgumentException(
                $"A key of domain {left.Kind} cannot be ordered against one of domain " +
                $"{right.Kind}: docs/12-index-reads.md §4.4 gives an order per dtype, not across " +
                "them.",
                nameof(right));
        }
    }

    /// <summary>
    /// IEEE doubles read as the total order of docs/06 §3, through the transform that file's
    /// encoder uses: a non-negative gets its sign bit set, a negative gets every bit flipped.
    /// </summary>
    private static int TotalFloat(double left, double right)
    {
        ulong a = Ordered(BitConverter.DoubleToUInt64Bits(left));
        ulong b = Ordered(BitConverter.DoubleToUInt64Bits(right));
        return a.CompareTo(b);
    }

    private static ulong Ordered(ulong bits) =>
        bits ^ ((bits >> 63) == 0 ? 0x8000_0000_0000_0000ul : 0xFFFF_FFFF_FFFF_FFFFul);
}
