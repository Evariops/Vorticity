using System;
using Vorticity.Arrays;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// A pushed predicate judged on each value of a canonical column, a state each (<see cref="Trilean"/>): what a dictionary
/// asks of its values once, then spreads over its rows by their codes.
/// </summary>
internal static class ValueVerdicts
{
    /// <summary>
    /// The verdict of <paramref name="predicate"/> on each value of <paramref name="values"/>, into
    /// <paramref name="states"/>; <paramref name="scratch"/> as long. False for a match of bytes on no text, which has no
    /// verdict.
    /// </summary>
    internal static bool TryJudge(
        CanonicalArena arena, int values, EncodedPredicate predicate, Span<byte> states, Span<byte> scratch)
    {
        switch (predicate.Kind)
        {
            case EncodedPredicateKind.Prefix or EncodedPredicateKind.Contains:
                if (arena.GetNode(values).DType.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary)
                    || predicate.Literal.Kind != FilterLiteralKind.Bytes)
                {
                    return false;
                }

                ComparisonKernels.StringMatch(
                    arena, values, predicate.Kind == EncodedPredicateKind.Prefix ? StringMatchOp.StartsWith : StringMatchOp.Contains,
                    predicate.Literal, escape: 0, states);
                return true;

            case EncodedPredicateKind.Compare:
                ComparisonKernels.Compare(arena, values, predicate.Op, predicate.Literal, states);
                return true;

            case EncodedPredicateKind.Between:
                ComparisonKernels.Compare(
                    arena, values, predicate.LowInclusive ? ComparisonOp.GreaterOrEqual : ComparisonOp.Greater, predicate.Literal, states);
                ComparisonKernels.Compare(
                    arena, values, predicate.HighInclusive ? ComparisonOp.LessOrEqual : ComparisonOp.Less, predicate.High, scratch);
                Trilean.And(states, scratch);
                return true;

            case EncodedPredicateKind.In:
                ComparisonKernels.In(arena, values, predicate.Values, states, scratch, prepared: null);
                return true;

            default:
                return false;
        }
    }
}
