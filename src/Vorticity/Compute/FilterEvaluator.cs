using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Evaluates a filter over a decoded batch, producing one three-valued truth per row; a row is
/// selected only when the whole expression comes out true. The scratch one operand needs while the
/// other is evaluated is rented from the shared pool rather than taken from the arena, because it
/// dies with the call while the arena's blocks live until the batch is disposed.
/// </summary>
/// <remarks>
/// One of these belongs to one scan, because some of what a filter needs is worth computing once
/// for the whole file rather than once per batch: an <c>IN</c> hashes its candidates, and a scan of
/// a million rows in eight-thousand-row batches would otherwise hash them a hundred and twenty
/// times. The prepared state is keyed by the expression node it belongs to and never outlives the
/// scan.
/// </remarks>
internal sealed class FilterEvaluator
{
    /// <summary>
    /// How deep a filter expression may nest. The tree comes from the caller rather than from a
    /// file, so evaluation is capped like anything else read from outside: generous for anything
    /// written by hand or by a query planner, and far below what recursion here can survive.
    /// </summary>
    internal const int MaxDepth = 64;

    private readonly VortexExpr _filter;

    /// <summary>
    /// One entry per <c>IN</c> met so far, or null while none has been.
    /// </summary>
    /// <remarks>
    /// Nothing is allocated until an <c>IN</c> is actually reached, which is what keeps a filter
    /// without one free: the read paths are held to a ceiling in bytes, and a table nobody puts
    /// anything in still costs more than the evaluator that owns it.
    ///
    /// Grown by replacement rather than mutated, so a reader never sees a half-written array and no
    /// lock is needed: batches of one scan can be evaluated from several threads. Two racing here
    /// build equal sets and one of the arrays is dropped, which costs a rebuild on a later batch
    /// and nothing else.
    /// </remarks>
    private volatile Prepared[]? _prepared;

    /// <summary>
    /// The mask that reads only the column a pushed comparison names, built by the first split
    /// that pushes it rather than by every split: it is a few objects, and a split must allocate none.
    /// </summary>
    /// <remarks>
    /// One reference, published whole, because the lanes of a scan race here: a field index and
    /// a mask held apart could be read half-written, and a mask read as its default reads every
    /// column, which the pushed pass must not.
    /// </remarks>
    private volatile PushedField? _pushed;

    /// <summary>Prepares an evaluator for one filter, for the length of one scan.</summary>
    /// <param name="filter">The expression every call will evaluate.</param>
    internal FilterEvaluator(VortexExpr filter)
    {
        _filter = filter;
    }

    /// <summary>The expression this evaluator answers.</summary>
    internal VortexExpr Filter => _filter;

    /// <summary>The mask that reads field <paramref name="field"/> of the root struct and nothing else.</summary>
    /// <param name="field">The index of the column the pushed comparison names.</param>
    internal Layouts.FieldMask OnlyField(int field)
    {
        PushedField? held = _pushed;
        if (held is null || held.Field != field)
        {
            held = new PushedField(field, new Layouts.FieldMaskBuilder().IncludeField(field).Build());
            _pushed = held;
        }

        return held.Mask;
    }

    /// <summary>A column index and the mask that reads it alone.</summary>
    private sealed record PushedField(int Field, Layouts.FieldMask Mask);

    /// <summary>One <c>IN</c>'s candidates, hashed for the column they were met over.</summary>
    /// <param name="Node">The expression node the set belongs to.</param>
    /// <param name="Signed">The signedness it was built for.</param>
    /// <param name="Set">The set, or null when an OR of equalities is the better answer.</param>
    private sealed record Prepared(InExpr Node, bool Signed, InSet? Set);

    /// <summary>
    /// Evaluates <paramref name="filter"/> once, keeping nothing. For a caller that has one batch
    /// to answer rather than a scan's worth.
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
        new FilterEvaluator(filter).Evaluate(arena, rootIndex, rows, destination);
    }

    /// <summary>
    /// Evaluates this evaluator's filter over the batch rooted at <paramref name="rootIndex"/>.
    /// </summary>
    /// <param name="arena">The arena holding the decoded batch.</param>
    /// <param name="rootIndex">The batch's root node, a struct for a tabular file.</param>
    /// <param name="rows">The batch's row count.</param>
    /// <param name="destination">Receives one <see cref="Trilean"/> state per row.</param>
    /// <exception cref="ArgumentException">A field path names nothing in the batch's schema.</exception>
    /// <exception cref="NotSupportedException">A column's type is outside the 1.0 filter scope.</exception>
    internal void Evaluate(CanonicalArena arena, int rootIndex, int rows, Span<byte> destination)
    {
        Evaluate(_filter, arena, rootIndex, rows, destination, 0);
    }

    /// <summary>
    /// The hashed candidates for this <c>IN</c> against this column, built once per scan.
    /// </summary>
    /// <param name="membership">The expression node, which is what the set belongs to.</param>
    /// <param name="arena">The arena holding the decoded batch.</param>
    /// <param name="column">The resolved column.</param>
    /// <returns><see langword="null"/> when an OR of equalities is the better answer.</returns>
    /// <remarks>
    /// Keyed by signedness as well, because a candidate above <c>i64::MaxValue</c> shares its bits
    /// with a negative value: a set built for one kind of column would answer wrongly for the
    /// other. Within a scan the schema fixes it, so the rebuild is a guard rather than a cost.
    /// </remarks>
    private InSet? PreparedFor(InExpr membership, CanonicalArena arena, int column)
    {
        if (!ComparisonKernels.TryIntegerColumn(arena, column, out _, out bool signed))
        {
            return null;
        }

        Prepared[]? held = _prepared;
        if (held is not null)
        {
            for (int i = 0; i < held.Length; i++)
            {
                Prepared entry = held[i];
                if (ReferenceEquals(entry.Node, membership) && entry.Signed == signed)
                {
                    return entry.Set;
                }
            }
        }

        Prepared fresh = new Prepared(membership, signed, InSet.TryBuild(membership.Literals, signed));
        int length = held?.Length ?? 0;
        Prepared[] grown = new Prepared[length + 1];
        held?.CopyTo(grown, 0);
        grown[length] = fresh;
        _prepared = grown;
        return fresh.Set;
    }

    private void Evaluate(
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

            case ExprKind.ColumnComparison:
            {
                ColumnComparisonExpr columns = (ColumnComparisonExpr)filter;
                int left = Resolve(arena, rootIndex, columns.Left, rows);
                int right = Resolve(arena, rootIndex, columns.Right, rows);
                ComparisonKernels.CompareColumns(arena, left, columns.Op, right, destination);
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

            case ExprKind.ListContains:
            {
                ListContainsExpr contains = (ListContainsExpr)filter;
                int column = Resolve(arena, rootIndex, contains.Field, rows);
                ListKernels.Contains(arena, column, contains.Value, destination);
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
                InSet? prepared = PreparedFor(membership, arena, column);
                byte[] scratch = ArrayPool<byte>.Shared.Rent(rows);
                try
                {
                    ComparisonKernels.In(
                        arena, column, membership.Literals, destination, scratch.AsSpan(0, rows),
                        prepared);
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
                // A bare field or literal is not a predicate. The expression factories make this
                // unreachable from the public API; the throw keeps another node kind from being
                // evaluated as something it is not.
                throw new ArgumentException(
                    $"A filter's root must be a predicate, not a {filter.Kind}.", nameof(filter));
        }
    }

    /// <summary>
    /// Walks a dotted path from the batch rooted at <paramref name="rootIndex"/> to the column
    /// <paramref name="field"/> names.
    /// </summary>
    /// <param name="arena">The arena holding the decoded batch.</param>
    /// <param name="rootIndex">The batch's root node.</param>
    /// <param name="field">The path.</param>
    /// <param name="rows">The batch's row count, which the column must have.</param>
    /// <returns>The column's node.</returns>
    /// <exception cref="ArgumentException">The path names nothing in the batch's schema.</exception>
    /// <remarks>
    /// Allocation-free: the segments were encoded when the expression was built, and
    /// <see cref="DType.IndexOfField(ReadOnlySpan{byte})"/> resolves interned names to handles
    /// rather than comparing strings.
    /// </remarks>
    internal static int Resolve(CanonicalArena arena, int rootIndex, FieldExpr field, int rows)
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

            current = MaskedBy(arena, node.GetFieldIndex(index), node.Validity);
        }

        CanonicalNode column = arena.GetNode(current);
        if (column.Length != rows)
        {
            throw new ArgumentException(
                $"'{field.Path}' has {column.Length} rows in a batch of {rows}.", nameof(field));
        }

        return current;
    }

    /// <summary>The dtype of the validity bitmaps made here; a bitmap's own dtype is never read.</summary>
    private static readonly DType BitmapType = new DTypeArena().Bool(Nullability.NonNullable);

    /// <summary>
    /// A struct's field as a filter reads it: null wherever the struct is, whatever its own buffer
    /// holds there.
    /// </summary>
    /// <remarks>
    /// A field is read through the struct's own validity, so a filter on `person.name` cannot match
    /// a row whose `person` is null by whatever that row's field buffer happens to hold. A struct
    /// with no null costs nothing here; one with nulls re-publishes the field over the same buffers
    /// with the two validities intersected.
    /// </remarks>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="child">The field's node.</param>
    /// <param name="parent">The struct's validity.</param>
    /// <returns>The field's node, or its masked re-publication.</returns>
    internal static int MaskedBy(CanonicalArena arena, int child, Validity parent)
    {
        ValidityMask outer = ValidityMask.From(arena, parent);
        if (outer.AllValid)
        {
            return child;
        }

        CanonicalNode field = arena.GetNode(child);
        CanonicalNode storage = field;
        while (storage.Kind == CanonicalKind.Extension)
        {
            storage = arena.GetNode(storage.StorageIndex);
        }

        int rows = field.Length;
        ValidityMask own = ValidityMask.From(arena, storage.Validity);
        Validity validity = Validity.AllInvalid;
        if (!outer.AllInvalid && !own.AllInvalid)
        {
            VortexBuffer bits = arena.Allocate(Math.Max((rows + 7) / 8, 1), 1, out Span<byte> raw);
            raw.Clear();
            for (int row = 0; row < rows; row++)
            {
                if (outer.IsValid(row) && own.IsValid(row))
                {
                    raw[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(BitmapType, rows, Validity.NonNullable, bits, 0));
        }

        return CanonicalRewrap.WithValidity(arena, child, field.DType, validity, rows);
    }
}
