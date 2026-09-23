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
    /// The columns the filter reads, when it can be evaluated on them alone, planned by the first
    /// split that asks rather than by every split.
    /// </summary>
    /// <remarks>
    /// One reference, published whole, because the lanes of a scan race here: a mask read half
    /// written, or as its default, reads every column, which the filter's own pass must not.
    /// </remarks>
    private volatile FilterColumns? _columns;

    /// <summary>The share of a split's rows the filter kept, per mille, smoothed over the splits so far; -1 before the first.</summary>
    private int _keptShare = -1;

    /// <summary>Whether the scan's second passes read the projection whole and gather what the filter kept.</summary>
    private bool _keepsMost;

    /// <summary>The smoothed share, per mille, from which a scan reading the kept rows alone reads them whole instead.</summary>
    private const int EnterMostPerMille = 330;

    /// <summary>The smoothed share, per mille, under which a scan reading whole goes back to the kept rows alone.</summary>
    private const int LeaveMostPerMille = 270;

    /// <summary>Prepares an evaluator for one filter, for the length of one scan.</summary>
    /// <param name="filter">The expression every call will evaluate.</param>
    internal FilterEvaluator(VortexExpr filter)
    {
        _filter = filter;
    }

    /// <summary>
    /// The zone maps the scan's pruning read, when it read any: a split whose every row they prove
    /// the filter selects is delivered without evaluating it. Held here rather than on the scan so
    /// that only a filtered scan carries the reference.
    /// </summary>
    internal ZonePruner? Zones { get; init; }

    /// <summary>The expression this evaluator answers.</summary>
    internal VortexExpr Filter => _filter;

    /// <summary>
    /// The columns of the root struct the filter reads, when it can be evaluated on them alone and a
    /// split it rejects whole can be delivered without reading anything else.
    /// </summary>
    /// <param name="root">The file's dtype.</param>
    /// <param name="shape">The dtype of the batches the scan delivers.</param>
    /// <param name="columns">The filter's columns, each whole.</param>
    /// <returns>
    /// Whether the filter reads only fields of a non-nullable root struct, each named by itself, and
    /// every column of the batch has a zero-row form.
    /// </returns>
    internal bool TryOwnColumns(DType root, DType shape, out Layouts.FieldMask columns)
    {
        FilterColumns planned = _columns ??= FilterColumns.Plan(_filter, root, shape);
        columns = planned.Mask;
        return !ReferenceEquals(planned, FilterColumns.None);
    }

    /// <summary>
    /// Whether the second pass of a split the filter kept <paramref name="count"/> of
    /// <paramref name="rows"/> rows of reads the projection whole and gathers them, rather than
    /// reading the kept rows alone.
    /// </summary>
    /// <param name="count">The rows the filter kept, neither none nor all.</param>
    /// <param name="rows">The split's rows.</param>
    /// <remarks>
    /// <para>
    /// Measured on 64 bit-packed integer columns, a filter keeping the same share of every split:
    /// reading the kept rows alone takes 61 % less time than reading every row and gathering at
    /// 5 %, 34 % less at 10 %, 8 % less at 20 %, as much at 25 to 30 %, and more from there up.
    /// </para>
    /// <para>
    /// The choice is the scan's rather than each split's: made on the share kept so far, smoothed,
    /// and changed only past a margin on either side of the crossing. Splits of one chunk read
    /// both ways decode it twice -- the kept rows of the first ones block by block, then all of it
    /// for the first one read whole -- which cost 14 % more than either way alone on a filter
    /// keeping 30 % of every split. The lanes of a scan race on the two fields; a lost update moves
    /// the share by one split's weight, and each lane reads a choice one of them made.
    /// </para>
    /// </remarks>
    internal bool KeepsMost(int count, int rows)
    {
        int share = (int)(count * 1_000L / rows);
        int held = _keptShare;
        int smoothed = held < 0 ? share : ((held * 7) + share) / 8;
        bool most = _keepsMost ? smoothed >= LeaveMostPerMille : smoothed >= EnterMostPerMille;
        _keptShare = smoothed;
        _keepsMost = most;
        return most;
    }

    /// <summary>The columns a filter reads, or <see cref="None"/> when it is not evaluated on them alone.</summary>
    private sealed class FilterColumns
    {
        internal static readonly FilterColumns None = new FilterColumns(default);

        private const int StackFields = 256;

        private FilterColumns(Layouts.FieldMask mask)
        {
            Mask = mask;
        }

        internal Layouts.FieldMask Mask { get; }

        internal static FilterColumns Plan(VortexExpr filter, DType root, DType shape)
        {
            if (root.Kind != DTypeKind.Struct || root.IsNullable || !CanonicalFill.CanBuild(shape))
            {
                return None;
            }

            int fieldCount = root.FieldCount;
            Span<bool> stack = stackalloc bool[StackFields];
            Scratch<bool> named = new Scratch<bool>(fieldCount, stack);
            try
            {
                Span<bool> marks = named.Span;
                marks.Clear();
                Marker marker = new Marker(root, marks);
                if (!Scanning.ScanBuilder.VisitFields(filter, ref marker))
                {
                    return None;
                }

                int count = marks.Count(true);
                if (count == 0)
                {
                    return None;
                }

                if (count == 1)
                {
                    return new FilterColumns(Layouts.FieldMask.Single(marks.IndexOf(true)));
                }

                int[] fields = new int[count];
                int next = 0;
                for (int f = 0; f < fieldCount; f++)
                {
                    if (marks[f])
                    {
                        fields[next++] = f;
                    }
                }

                return new FilterColumns(Layouts.FieldMask.Subset(new Layouts.FieldMaskNode(fields, null)));
            }
            finally
            {
                named.Dispose();
            }
        }
    }

    /// <summary>Marks the root fields a filter names, and stops at a path that is not one.</summary>
    private ref struct Marker : Scanning.ScanBuilder.IFieldVisitor
    {
        private readonly DType _root;
        private readonly Span<bool> _named;

        internal Marker(DType root, Span<bool> named)
        {
            _root = root;
            _named = named;
        }

        public bool Visit(FieldExpr field)
        {
            byte[][] segments = field.SegmentsUtf8;
            int index = segments.Length == 1 ? _root.IndexOfField(segments[0]) : -1;
            if (index < 0)
            {
                return false;
            }

            _named[index] = true;
            return true;
        }
    }

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
    /// Evaluates this evaluator's filter over the one column it reads, decoded on its own rather
    /// than as the field of a struct: every field the filter names is that column.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="column">The column's node.</param>
    /// <param name="rows">The column's row count.</param>
    /// <param name="destination">Receives one <see cref="Trilean"/> state per row.</param>
    /// <remarks>
    /// A struct of the one column would need a dtype of its own, derived per scan in the scan's
    /// dtype arena, whose first struct node makes it allocate its field tables.
    /// </remarks>
    internal void EvaluateColumn(CanonicalArena arena, int column, int rows, Span<byte> destination)
    {
        Evaluate(_filter, arena, ~column, rows, destination, 0);
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
    /// <param name="rootIndex">
    /// The batch's root node, or the complement of the one column the filter reads when it was
    /// decoded on its own.
    /// </param>
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
        if (rootIndex < 0)
        {
            // The one column the filter reads, evaluated on its own: see EvaluateColumn.
            int only = ~rootIndex;
            if (arena.GetNode(only).Length != rows)
            {
                throw new ArgumentException(
                    $"'{field.Path}' has {arena.GetNode(only).Length} rows in a batch of {rows}.", nameof(field));
            }

            return only;
        }

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
