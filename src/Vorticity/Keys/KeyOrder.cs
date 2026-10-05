using System;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>Orders two keys.</summary>
/// <remarks>
/// Two orders live here because a cursor is an ordinal structure and needs a total order, while a
/// float column is sorted under IEEE comparisons, which order neither NaN nor <c>-0.0</c> against
/// <c>+0.0</c>. The two agree on every dtype but the floats. Each source names the one it is
/// actually sorted in, since bisecting in the other would step past a key it was meant to find.
/// </remarks>
internal static class KeyOrder
{
    /// <summary>
    /// The total order a cursor exposes: numeric for integers, bytewise for strings, and for floats
    /// negative NaN, then the negatives down to <c>-0.0</c>, then <c>+0.0</c> up through the
    /// positives, then positive NaN.
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
    /// which follow IEEE, so that <c>-0.0</c> and <c>+0.0</c> are a single key.
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sign of <c>left - right</c>.</returns>
    /// <remarks>
    /// A NaN cannot reach this: a float column holding one is never flagged sorted, because that
    /// flag is computed with the same IEEE comparisons and every comparison against a NaN is false.
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
                $"{right.Kind}: keys are ordered within one dtype, and there is no order across " +
                "two.",
                nameof(right));
        }
    }

    /// <summary>
    /// IEEE doubles read as the total order, through the same transform the row encoder uses: a
    /// non-negative gets its sign bit set, a negative gets every bit flipped.
    /// </summary>
    private static int TotalFloat(double left, double right)
    {
        ulong a = Ordered(BitConverter.DoubleToUInt64Bits(left));
        ulong b = Ordered(BitConverter.DoubleToUInt64Bits(right));
        return a.CompareTo(b);
    }

    /// <summary>A double's bits as an unsigned integer in the total order.</summary>
    internal static ulong Ordered(ulong bits) =>
        bits ^ ((bits >> 63) == 0 ? 0x8000_0000_0000_0000ul : 0xFFFF_FFFF_FFFF_FFFFul);
}
