// Evaluating a filter expression over one decoded batch - docs/01-scope.md F7.
//
// Recursion over the expression, one rented byte buffer per node, three-valued logic throughout,
// and a row is selected only when the whole thing comes out True (docs/08-semantics.md §3).
//
// The buffers come from ArrayPool rather than the canonical arena: they are scratch that dies with
// the call, while the arena's blocks live until the batch is disposed. Renting is what keeps the
// per-batch managed allocation at zero once the pool is warm, which is the same trade
// BitPackedDecoder already makes for its unpack scratch.
//
// Expression depth is bounded, because the tree comes from the CALLER and a 100000-deep NOT chain
// would blow the stack before any file was read. It is the same class of check as the format's own
// depth caps, applied to the one input that does not come from a file.
using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Evaluates a filter over a decoded batch, producing one truth value per row.</summary>
internal static class FilterEvaluator
{
    /// <summary>
    /// How deep a filter expression may nest. Generous for anything written by hand or by a query
    /// planner, and far below what recursion here can survive.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// Evaluates <paramref name="filter"/> over the batch rooted at <paramref name="rootIndex"/>.
    /// </summary>
    /// <param name="filter">The expression.</param>
    /// <param name="arena">The arena holding the decoded batch.</param>
    /// <param name="rootIndex">The batch's root node, a struct for a tabular file.</param>
    /// <param name="rows">The batch's row count.</param>
    /// <param name="destination">Receives one <see cref="Trilean"/> state per row.</param>
    /// <exception cref="ArgumentException">A field path names nothing in the batch's schema.</exception>
    /// <exception cref="NotSupportedException">A column's type is outside the 1.0 filter scope.</exception>
    internal static void Evaluate(
        VortexExpr filter, CanonicalArena arena, int rootIndex, int rows, Span<byte> destination)
    {
        Evaluate(filter, arena, rootIndex, rows, destination, 0);
    }

    private static void Evaluate(
        VortexExpr filter, CanonicalArena arena, int rootIndex, int rows, Span<byte> destination,
        int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException(
                $"A filter expression nests deeper than {MaxDepth} levels.", nameof(filter));
        }

        switch (filter.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)filter;
                int column = Resolve(arena, rootIndex, comparison.Field, rows);
                ComparisonKernels.Compare(arena, column, comparison.Op, comparison.Value, destination);
                return;
            }

            case ExprKind.StringMatch:
            {
                StringMatchExpr match = (StringMatchExpr)filter;
                int column = Resolve(arena, rootIndex, match.Field, rows);
                ComparisonKernels.StringMatch(
                    arena, column, match.Op, match.Pattern, match.Escape, destination);
                return;
            }

            case ExprKind.NullCheck:
            {
                NullCheckExpr check = (NullCheckExpr)filter;
                int column = Resolve(arena, rootIndex, check.Field, rows);
                ComparisonKernels.NullCheck(arena, column, check.IsNull, destination);
                return;
            }

            case ExprKind.In:
            {
                InExpr membership = (InExpr)filter;
                int column = Resolve(arena, rootIndex, membership.Field, rows);
                byte[] scratch = ArrayPool<byte>.Shared.Rent(rows);
                try
                {
                    ComparisonKernels.In(
                        arena, column, membership.Literals, destination, scratch.AsSpan(0, rows));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }

                return;
            }

            case ExprKind.Not:
                Evaluate(((NotExpr)filter).Operand, arena, rootIndex, rows, destination, depth + 1);
                Trilean.Not(destination);
                return;

            case ExprKind.Logical:
            {
                LogicalExpr logical = (LogicalExpr)filter;
                Evaluate(logical.Left, arena, rootIndex, rows, destination, depth + 1);

                byte[] right = ArrayPool<byte>.Shared.Rent(rows);
                try
                {
                    Span<byte> other = right.AsSpan(0, rows);
                    Evaluate(logical.Right, arena, rootIndex, rows, other, depth + 1);
                    if (logical.IsAnd)
                    {
                        Trilean.And(destination, other);
                    }
                    else
                    {
                        Trilean.Or(destination, other);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(right);
                }

                return;
            }

            default:
                // A bare field or literal is not a predicate. Expr's factories make this
                // unreachable from the public API; it is here so a future node type cannot be
                // silently evaluated as something else.
                throw new ArgumentException(
                    $"A filter's root must be a predicate, not a {filter.Kind}.", nameof(filter));
        }
    }

    /// <summary>
    /// Walks a dotted path from the batch's root to the column it names.
    /// </summary>
    /// <remarks>
    /// Allocation-free: the segments were encoded at expression-construction time and
    /// <see cref="DType.IndexOfField(ReadOnlySpan{byte})"/> resolves interned names to handles rather than comparing
    /// strings.
    /// </remarks>
    private static int Resolve(CanonicalArena arena, int rootIndex, FieldExpr field, int rows)
    {
        int current = rootIndex;
        byte[][] segments = field.SegmentsUtf8;

        for (int i = 0; i < segments.Length; i++)
        {
            // An extension can wrap a struct; unwrapping before descending is what lets a path
            // reach through one.
            current = ComparisonKernels.Unwrap(arena, current);
            CanonicalNode node = arena.GetNode(current);

            if (node.Kind != CanonicalKind.Struct)
            {
                throw new ArgumentException(
                    $"'{field.Path}' descends into a {node.Kind} column, which has no fields.",
                    nameof(field));
            }

            int index = node.DType.IndexOfField(segments[i]);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"'{field.Path}' names a field the batch's schema does not have.", nameof(field));
            }

            current = node.GetFieldIndex(index);
        }

        CanonicalNode column = arena.GetNode(current);
        if (column.Length != rows)
        {
            throw new ArgumentException(
                $"'{field.Path}' has {column.Length} rows in a batch of {rows}.", nameof(field));
        }

        return current;
    }
}
