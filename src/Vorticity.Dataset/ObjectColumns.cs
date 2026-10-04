using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>How an object's column becomes the dataset's.</summary>
internal enum ColumnChange : byte
{
    /// <summary>It has the dataset's type: the object's node is the column.</summary>
    None = 0,

    /// <summary>It was written non-nullable and the dataset's is nullable: the node is declared again, over the same buffers.</summary>
    Nullable = 1,

    /// <summary>It holds narrower numbers of the same kind: the values are widened, which is exact.</summary>
    Widen = 2,
}

/// <summary>What a filter is for one object, once the columns the object lacks read as nulls.</summary>
internal enum FilterFate : byte
{
    /// <summary>A filter over the object's own columns, which its scan pushes down whole.</summary>
    Pushed = 0,

    /// <summary>True for every row: the object's scan takes no filter.</summary>
    Every = 1,

    /// <summary>True for no row: the object is not read.</summary>
    None = 2,

    /// <summary>
    /// Evaluated on the reshaped batches: a missing column leaves an unknown that no filter over the
    /// object's own columns can state, under a negation, or a comparison needs the dataset's type.
    /// </summary>
    Reshaped = 3,
}

/// <summary>An object's part of a read: what its filter came to, and the shape its batches take.</summary>
/// <param name="Fate">What the filter is for the object.</param>
/// <param name="Pushed">The filter the object's scan takes, when <paramref name="Fate"/> is <see cref="FilterFate.Pushed"/>.</param>
/// <param name="Evaluated">The filter evaluated on the reshaped batches, when <paramref name="Fate"/> is <see cref="FilterFate.Reshaped"/>.</param>
/// <param name="Shape">The fields the object's scan reads and how they become the dataset's columns.</param>
internal readonly record struct ObjectRead(FilterFate Fate, VortexExpr? Pushed, VortexExpr? Evaluated, ObjectShape Shape);

/// <summary>
/// How a data object written under an earlier schema answers for the dataset's columns: per
/// column, the object's field that holds it, under its own name or one it had before a rename, and
/// how its values become the column's; or none, which reads as nulls. Fields of dropped columns are
/// not read.
/// </summary>
/// <remarks>
/// Made when an object is opened for a version whose schema it does not have, which is a cold path
/// and allocates; the batches it reshapes cost records in their own arena, and a widened column its
/// values once. An object written under the current schema has none of this: a read of it is the
/// object's own scan, as it always was. Compaction writes every object it reads in the current
/// schema, so the reshaping lasts until the level the object sits in is next compacted.
/// </remarks>
internal sealed class ObjectColumns
{
    private readonly DatasetSchema _schema;
    private readonly DType _file;
    private readonly string _key;
    private readonly int[] _sources;
    private readonly ColumnChange[] _changes;

    private ObjectColumns(DatasetSchema schema, DType file, string key, int[] sources, ColumnChange[] changes)
    {
        _schema = schema;
        _file = file;
        _key = key;
        _sources = sources;
        _changes = changes;
    }

    /// <summary>
    /// How an object of dtype <paramref name="file"/> answers for <paramref name="schema"/>'s
    /// columns. With <paramref name="strict"/>, a field no column of the dataset ever had is refused
    /// rather than left unread: what an import asks, where it would be data the dataset drops.
    /// </summary>
    /// <exception cref="VortexSchemaException">The object's columns do not read as the dataset's.</exception>
    internal static ObjectColumns Map(DatasetSchema schema, DType file, string objectKey, bool strict)
    {
        DType dataset = schema.DType;
        if (file.Kind != DTypeKind.Struct)
        {
            throw new VortexSchemaException($"'{objectKey}' holds one column of {file}, where the dataset's rows are a struct of columns.");
        }

        int[] sources = new int[dataset.FieldCount];
        Array.Fill(sources, -1);
        for (int field = 0; field < file.FieldCount; field++)
        {
            string name = file.GetFieldName(field);
            string? current = schema.CurrentOf(name);
            if (current is null)
            {
                if (strict && !schema.IsRetired(name))
                {
                    throw new VortexSchemaException(
                        $"'{objectKey}' holds '{name}', which is no column of the dataset: its values would be read by nothing.");
                }

                continue;
            }

            int column = dataset.IndexOfField(current);
            if (sources[column] >= 0)
            {
                throw new VortexSchemaException(
                    $"'{objectKey}' holds the column '{current}' twice, as '{file.GetFieldName(sources[column])}' and as '{name}'.");
            }

            sources[column] = field;
        }

        ColumnChange[] changes = new ColumnChange[dataset.FieldCount];
        for (int column = 0; column < changes.Length; column++)
        {
            DType type = dataset.GetField(column);
            if (sources[column] < 0)
            {
                if (type.Nullability != Nullability.Nullable)
                {
                    throw new VortexSchemaException(
                        $"'{objectKey}' holds no column '{dataset.GetFieldName(column)}', which the dataset holds as not nullable.");
                }

                continue;
            }

            DType was = file.GetField(sources[column]);
            changes[column] = ChangeOf(was, type) ?? throw new VortexSchemaException(
                $"'{objectKey}' holds '{file.GetFieldName(sources[column])}' as {was}, which does not read as the dataset's {type}.");
            if (changes[column] != ColumnChange.None && schema.IsKeyed(dataset.GetFieldName(column)))
            {
                throw new VortexSchemaException(
                    $"'{objectKey}' holds '{dataset.GetFieldName(column)}' as {was}; it is a column of the clustering key, which orders " +
                    $"every object, and every object holds it as the dataset's {type}.");
            }
        }

        return new ObjectColumns(schema, file, objectKey, sources, changes);
    }

    /// <summary>
    /// How values of <paramref name="was"/> become values of <paramref name="type"/>, or null when
    /// some would not survive: the same type, the same one become nullable, or a number of the same
    /// kind widened, nullable or not as it was, or become nullable.
    /// </summary>
    internal static ColumnChange? ChangeOf(DType was, DType type)
    {
        if (was == type)
        {
            return ColumnChange.None;
        }

        bool relaxes = was.Nullability == type.Nullability || type.Nullability == Nullability.Nullable;
        if (!relaxes)
        {
            return null;
        }

        if (was.Nullability != type.Nullability && was.Kind == type.Kind && Nullable(was, type.Nullability) == type)
        {
            return ColumnChange.Nullable;
        }

        return was.Kind == DTypeKind.Primitive && type.Kind == DTypeKind.Primitive && Widens(was.PType, type.PType)
            ? ColumnChange.Widen
            : null;
    }

    /// <summary>
    /// <paramref name="type"/> under another nullability, made in an arena of its own: a dtype's
    /// arena is shared with every reader of the file or the version it belongs to, and is not one to
    /// add a node to.
    /// </summary>
    private static DType Nullable(DType type, Nullability nullability) =>
        DTypeImport.Into(new DTypeArena(), type).WithNullability(nullability);

    /// <summary>Whether every value of <paramref name="from"/> is exactly a value of <paramref name="to"/>, of the same kind.</summary>
    private static bool Widens(PType from, PType to) => (from, to) switch
    {
        (PType.I8, PType.I16 or PType.I32 or PType.I64) => true,
        (PType.I16, PType.I32 or PType.I64) => true,
        (PType.I32, PType.I64) => true,
        (PType.U8, PType.U16 or PType.U32 or PType.U64) => true,
        (PType.U16, PType.U32 or PType.U64) => true,
        (PType.U32, PType.U64) => true,
        (PType.F16, PType.F32 or PType.F64) => true,
        (PType.F32, PType.F64) => true,
        _ => false,
    };

    /// <summary>
    /// The dataset's paths as the object names them, or null when the object holds no column of one
    /// of them, whose values it then reads as nulls.
    /// </summary>
    internal string[]? SourcePaths(IReadOnlyList<string> paths)
    {
        string[] named = new string[paths.Count];
        for (int i = 0; i < named.Length; i++)
        {
            if (SourcePath(paths[i]) is not { } path)
            {
                return null;
            }

            named[i] = path;
        }

        return named;
    }

    /// <summary>A dataset path as the object names it, or null when the object holds no column of it.</summary>
    internal string? SourcePath(string path)
    {
        int column = ColumnPath.TopOf(_schema.DType, path);
        if (column < 0 || _sources[column] < 0)
        {
            return null;
        }

        int field = _sources[column];
        if (_file.GetFieldNameUtf8(field).SequenceEqual(_schema.DType.GetFieldNameUtf8(column)))
        {
            return path;
        }

        int dot = path.IndexOf('.', StringComparison.Ordinal);
        string name = _file.GetFieldName(field);
        return dot < 0 ? name : name + path[dot..];
    }

    /// <summary>
    /// The object's part of a read of <paramref name="projection"/> filtered by
    /// <paramref name="filter"/>, both over the dataset's columns.
    /// </summary>
    internal ObjectRead Read(VortexExpr? filter, in FieldMask projection)
    {
        if (filter is null)
        {
            return new ObjectRead(FilterFate.Every, null, null, Shape(projection, null));
        }

        (Truth truth, VortexExpr? folded) = Fold(filter, select: true);
        return truth switch
        {
            Truth.Expression => new ObjectRead(FilterFate.Pushed, folded, null, Shape(projection, null)),
            Truth.True => new ObjectRead(FilterFate.Every, null, null, Shape(projection, null)),
            Truth.False or Truth.Unknown => new ObjectRead(FilterFate.None, null, null, Shape(projection, null)),
            _ => new ObjectRead(FilterFate.Reshaped, null, filter, Shape(projection, filter)),
        };
    }

    /// <summary>
    /// The batches of <paramref name="source"/>, read by <paramref name="shape"/>'s fields, reshaped
    /// to the dataset's columns and counted from <paramref name="baseRow"/>; filtered here by
    /// <paramref name="evaluated"/> when there is one, the rows it keeps gathered with
    /// <paramref name="compact"/> and selected otherwise.
    /// </summary>
    /// <remarks>
    /// One view serves the stream, bound again per batch, and the reshaping adds records to the
    /// batch's own arena: nothing is allocated per batch but what a widened column's values take.
    /// </remarks>
    internal static async IAsyncEnumerable<RecordBatch> ReshapeAsync(
        IAsyncEnumerable<RecordBatch> source,
        ObjectShape shape,
        VortexExpr? evaluated,
        long baseRow,
        bool compact,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FilterEvaluator? evaluator = evaluated is null ? null : new FilterEvaluator(evaluated);
        byte[] states = [];
        int[] indices = [];
        RecordBatch? view = null;
        try
        {
            await foreach (RecordBatch batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int rows = batch.RowCount;
                CanonicalArena arena = batch.Arena;
                int root = shape.Build(batch);
                VortexBuffer words = default;
                int selected = -1;
                if (evaluator is not null)
                {
                    Pooled.Grow(ref states, rows);

                    Span<byte> truths = states.AsSpan(0, rows);
                    evaluator.Evaluate(arena, root, rows, truths);
                    Unselected(batch.SelectionWords, truths);
                    int count = Trilean.CountTrue(truths);
                    if (count == 0)
                    {
                        continue;
                    }

                    root = shape.Final(arena, root);
                    if (count < rows && compact)
                    {
                        Pooled.Grow(ref indices, rows);
                        CanonicalFilter.Select(truths, indices);
                        root = CanonicalFilter.Apply(arena, root, indices.AsSpan(0, count));
                    }
                    else if (count < rows)
                    {
                        words = arena.AllocateUninitialized(((rows + 63) >> 6) * sizeof(ulong), 64, out Span<byte> raw);
                        selected = Trilean.ToWords(truths, Trilean.True, equal: true, MemoryMarshal.Cast<byte, ulong>(raw));
                    }
                }

                // A gather leaves fewer rows than the batch's selection describes, and the view of
                // it selects them all.
                view?.Dispose();
                view = batch.Reshaped(root, baseRow + batch.StartRow, view);
                if (selected >= 0)
                {
                    view.Select(words, selected);
                }

                yield return view;
            }
        }
        finally
        {
            view?.Dispose();
            Pooled.Return(states);
            Pooled.Return(indices);
        }
    }

    /// <summary>
    /// The rows a batch does not select, a take's for instance, made false: a filter evaluated here
    /// narrows the batch's own selection, and a gather or a new selection would otherwise let them back.
    /// </summary>
    private static void Unselected(ReadOnlySpan<ulong> selection, Span<byte> truths)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        int rows = truths.Length;
        for (int word = 0; word < (rows + 63) >> 6; word++)
        {
            // The bits past the rows are never set in a selection, so only the last word is cut.
            ulong dropped = ~selection[word];
            int left = rows - (word << 6);
            if (left < 64)
            {
                dropped &= (1UL << left) - 1;
            }

            for (; dropped != 0; dropped &= dropped - 1)
            {
                truths[(word << 6) + BitOperations.TrailingZeroCount(dropped)] = Trilean.False;
            }
        }
    }

    /// <summary>
    /// The shape of a read of <paramref name="projection"/>: the dataset's columns it asks for, and
    /// whole the columns <paramref name="evaluated"/> reads, which the batches hold until the filter
    /// has been evaluated on them.
    /// </summary>
    private ObjectShape Shape(in FieldMask projection, VortexExpr? evaluated)
    {
        DType dataset = _schema.DType;
        int count = dataset.FieldCount;
        bool[] asked = new bool[count];
        bool[] held = new bool[count];
        FieldMask[] masks = new FieldMask[count];
        for (int column = 0; column < count; column++)
        {
            asked[column] = projection.Includes(column);
            held[column] = asked[column];
            masks[column] = projection.Descend(column);
        }

        if (evaluated is not null)
        {
            List<string> paths = [];
            evaluated.CollectFields(paths);
            foreach (string path in paths)
            {
                int column = ColumnPath.TopOf(dataset, path);
                if (column >= 0)
                {
                    held[column] = true;
                    masks[column] = FieldMask.All;
                }
            }
        }

        // The object's fields, ascending as a mask names them, each with the part of it read.
        List<(int Field, FieldMask Mask)> reads = [];
        for (int column = 0; column < count; column++)
        {
            if (held[column] && _sources[column] >= 0)
            {
                reads.Add((_sources[column], _changes[column] == ColumnChange.Widen ? FieldMask.All : masks[column]));
            }
        }

        if (reads.Count == 0 && _file.FieldCount > 0)
        {
            // A scan of no field is not one a file answers with rows; the first field is read, and
            // nothing of it is used.
            reads.Add((0, FieldMask.All));
        }

        reads.Sort(static (left, right) => left.Field.CompareTo(right.Field));
        int[] fields = new int[reads.Count];
        FieldMask[] below = new FieldMask[reads.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = reads[i].Field;
            below[i] = reads[i].Mask;
        }

        List<int> target = [];
        List<FieldMask> targetMasks = [];
        for (int column = 0; column < count; column++)
        {
            if (held[column])
            {
                target.Add(column);
                targetMasks.Add(masks[column]);
            }
        }

        FieldMask targetMask = FieldMask.Subset(new FieldMaskNode([.. target], [.. targetMasks]));
        DTypeArena types = new DTypeArena();
        DType shape = VortexTypes.ToDType(ToolPaths.Project(_schema.Columns, targetMask), types);
        int[] positions = new int[target.Count];
        ColumnChange[] changes = new ColumnChange[target.Count];
        for (int p = 0; p < positions.Length; p++)
        {
            int column = target[p];
            positions[p] = _sources[column] < 0 ? -1 : Array.IndexOf(fields, _sources[column]);
            changes[p] = _changes[column];
        }

        int[]? kept = null;
        FieldMask[]? trims = null;
        DType final = shape;
        if (evaluated is not null)
        {
            // The filter read its columns whole; the batch keeps only what was asked for, and of a
            // struct asked for in part, only that part.
            List<int> keep = [];
            List<int> keptColumns = [];
            List<FieldMask> keptMasks = [];
            bool narrows = false;
            for (int p = 0; p < target.Count; p++)
            {
                if (asked[target[p]])
                {
                    FieldMask part = projection.Descend(target[p]);
                    keep.Add(p);
                    keptColumns.Add(target[p]);
                    keptMasks.Add(part);
                    narrows |= !part.IsAll && masks[target[p]].IsAll;
                }
            }

            if (keep.Count < target.Count || narrows)
            {
                kept = [.. keep];
                trims = [.. keptMasks];
                FieldMask finalMask = FieldMask.Subset(new FieldMaskNode([.. keptColumns], [.. keptMasks]));
                final = VortexTypes.ToDType(ToolPaths.Project(_schema.Columns, finalMask), types);
            }
        }

        FieldMask source = FieldMask.Subset(new FieldMaskNode(fields, below));
        return new ObjectShape(source, shape, positions, changes, kept, trims, final, _key);
    }

    /// <summary>A filter's truth for one object, three-valued, or an expression over its own columns.</summary>
    private enum Truth : byte
    {
        Expression,
        True,
        False,
        Unknown,
        Unsettled,
    }

    /// <summary>
    /// The filter as the object's columns state it: its predicates renamed to the object's fields,
    /// and those over a column it lacks folded to what a null makes them. Under
    /// <paramref name="select"/>, where only a true row counts, an unknown is as good as false; under
    /// a negation it is not, and an unknown that meets an expression there cannot be stated.
    /// </summary>
    private (Truth Truth, VortexExpr? Expression) Fold(VortexExpr node, bool select)
    {
        switch (node)
        {
            case LogicalExpr logical:
            {
                (Truth, VortexExpr?) left = Fold(logical.Left, select);
                (Truth, VortexExpr?) right = Fold(logical.Right, select);
                return logical.IsAnd ? And(logical, left, right, select) : Or(logical, left, right, select);
            }

            case NotExpr negation:
            {
                (Truth truth, VortexExpr? operand) = Fold(negation.Operand, select: false);
                return truth switch
                {
                    Truth.Expression => (Truth.Expression, ReferenceEquals(operand, negation.Operand) ? negation : Expr.Not(operand!)),
                    Truth.True => (Truth.False, null),
                    Truth.False => (Truth.True, null),
                    _ => (truth, null),
                };
            }

            case NullCheckExpr check:
            {
                if (Leaf(check.Field, pushable: true) is { } checkedField)
                {
                    return (Truth.Expression, ReferenceEquals(checkedField, check.Field) ? check : new NullCheckExpr(checkedField, check.IsNull));
                }

                // A missing column is null on every row, which a null check settles.
                return Absent(check.Field) ? (check.IsNull ? Truth.True : Truth.False, null) : (Truth.Unsettled, null);
            }

            case ComparisonExpr comparison:
                // A literal beyond a narrower column's range is compared in the wider type by the
                // kernels and the zone maps alike, so a comparison goes to a widened column as it is.
                return Leaf(comparison.Field, pushable: true) is { } compared
                    ? (Truth.Expression, ReferenceEquals(compared, comparison.Field) ? comparison : new ComparisonExpr(compared, comparison.Op, comparison.Value))
                    : Missing(comparison.Field);

            case InExpr membership:
                return Leaf(membership.Field, pushable: false) is { } member
                    ? (Truth.Expression, ReferenceEquals(member, membership.Field) ? membership : new InExpr(member, membership.Literals))
                    : Missing(membership.Field);

            case StringMatchExpr match:
                return Leaf(match.Field, pushable: false) is { } matched
                    ? (Truth.Expression, ReferenceEquals(matched, match.Field) ? match : new StringMatchExpr(matched, match.Op, match.Pattern, match.Escape))
                    : Missing(match.Field);

            case ListContainsExpr contains:
                return Leaf(contains.Field, pushable: false) is { } list
                    ? (Truth.Expression, ReferenceEquals(list, contains.Field) ? contains : new ListContainsExpr(list, contains.Value))
                    : Missing(contains.Field);

            case ColumnComparisonExpr columns:
            {
                if (Absent(columns.Left) || Absent(columns.Right))
                {
                    return (Truth.Unknown, null);
                }

                // Two columns compare when their types are one; a column read under another type
                // compares on the reshaped batch.
                FieldExpr? left = Leaf(columns.Left, pushable: false);
                FieldExpr? right = Leaf(columns.Right, pushable: false);
                return left is null || right is null
                    ? (Truth.Unsettled, null)
                    : (Truth.Expression, ReferenceEquals(left, columns.Left) && ReferenceEquals(right, columns.Right)
                        ? columns
                        : new ColumnComparisonExpr(left, columns.Op, right));
            }

            case FieldExpr field:
                return Leaf(field, pushable: false) is { } flag
                    ? (Truth.Expression, flag)
                    : Missing(field);

            case LiteralExpr { Value.Kind: FilterLiteralKind.Bool } literal:
                return (literal.Value.BoolValue ? Truth.True : Truth.False, null);

            case LiteralExpr { Value.Kind: FilterLiteralKind.Null }:
                return (Truth.Unknown, null);

            default:
                return (Truth.Unsettled, null);
        }
    }

    /// <summary>A predicate over a column the object lacks is unknown, since it reads as null; over one read under another type, it waits for the reshaped batch.</summary>
    private (Truth, VortexExpr?) Missing(FieldExpr field) => Absent(field) ? (Truth.Unknown, null) : (Truth.Unsettled, null);

    private static (Truth, VortexExpr?) And(LogicalExpr node, (Truth Truth, VortexExpr? Expression) left, (Truth Truth, VortexExpr? Expression) right, bool select)
    {
        if (left.Truth == Truth.False || right.Truth == Truth.False)
        {
            return (Truth.False, null);
        }

        if (select && (left.Truth == Truth.Unknown || right.Truth == Truth.Unknown))
        {
            // Never true, which is all a selection asks.
            return (Truth.False, null);
        }

        if (left.Truth == Truth.Unsettled || right.Truth == Truth.Unsettled)
        {
            return (Truth.Unsettled, null);
        }

        if (left.Truth == Truth.True)
        {
            return right;
        }

        if (right.Truth == Truth.True)
        {
            return left;
        }

        if (left.Truth == Truth.Unknown || right.Truth == Truth.Unknown)
        {
            return left.Truth == right.Truth ? (Truth.Unknown, null) : (Truth.Unsettled, null);
        }

        return (Truth.Expression, ReferenceEquals(left.Expression, node.Left) && ReferenceEquals(right.Expression, node.Right)
            ? node
            : Expr.And(left.Expression!, right.Expression!));
    }

    private static (Truth, VortexExpr?) Or(LogicalExpr node, (Truth Truth, VortexExpr? Expression) left, (Truth Truth, VortexExpr? Expression) right, bool select)
    {
        if (left.Truth == Truth.True || right.Truth == Truth.True)
        {
            return (Truth.True, null);
        }

        if (left.Truth == Truth.False)
        {
            return right;
        }

        if (right.Truth == Truth.False)
        {
            return left;
        }

        if (select && left.Truth == Truth.Unknown)
        {
            // Unknown or x selects what x selects.
            return right;
        }

        if (select && right.Truth == Truth.Unknown)
        {
            return left;
        }

        if (left.Truth == Truth.Unsettled || right.Truth == Truth.Unsettled)
        {
            return (Truth.Unsettled, null);
        }

        if (left.Truth == Truth.Unknown || right.Truth == Truth.Unknown)
        {
            return left.Truth == right.Truth ? (Truth.Unknown, null) : (Truth.Unsettled, null);
        }

        return (Truth.Expression, ReferenceEquals(left.Expression, node.Left) && ReferenceEquals(right.Expression, node.Right)
            ? node
            : Expr.Or(left.Expression!, right.Expression!));
    }

    /// <summary>Whether the object holds no column of a path, which then reads as null.</summary>
    private bool Absent(FieldExpr field) => Column(field) is { } column && _sources[column] < 0;

    /// <summary>
    /// A field as the object names it: itself when the names agree, renamed when they do not; null
    /// when the object lacks the column, or when the column is read under another type and the
    /// predicate is not one that compares in the wider type.
    /// </summary>
    private FieldExpr? Leaf(FieldExpr field, bool pushable)
    {
        if (Column(field) is not { } column || _sources[column] < 0)
        {
            return null;
        }

        if (_changes[column] == ColumnChange.Widen && !pushable)
        {
            return null;
        }

        if (_file.GetFieldNameUtf8(_sources[column]).SequenceEqual(field.SegmentsUtf8[0]))
        {
            return field;
        }

        string[] renamed = field.Segments is { } segments ? (string[])segments.Clone() : field.Path.Split('.');
        renamed[0] = _file.GetFieldName(_sources[column]);

        // A value expression keeps its functions over the column's name in the object.
        return field is Compute.FunctionFieldExpr function ? function.Over(new FieldExpr(renamed)) : new FieldExpr(renamed);
    }

    /// <summary>The dataset column a path starts from, or null when it names none, which the filter's check has already refused.</summary>
    private int? Column(FieldExpr field)
    {
        int column = _schema.DType.IndexOfField(field.SegmentsUtf8[0]);
        return column < 0 ? null : column;
    }
}

/// <summary>
/// The fields an object's scan reads for one read, and how each batch it delivers becomes the
/// dataset's: its fields placed where the dataset's columns are, a missing column filled with
/// nulls, a column written non-nullable declared nullable, and narrower numbers widened.
/// </summary>
internal sealed class ObjectShape
{
    private readonly DType _shape;
    private readonly int[] _positions;
    private readonly ColumnChange[] _changes;
    private readonly int[]? _kept;
    private readonly FieldMask[]? _trims;
    private readonly DType _final;
    private readonly string _key;

    internal ObjectShape(
        FieldMask source, DType shape, int[] positions, ColumnChange[] changes, int[]? kept, FieldMask[]? trims, DType final, string key)
    {
        Source = source;
        _shape = shape;
        _positions = positions;
        _changes = changes;
        _kept = kept;
        _trims = trims;
        _final = final;
        _key = key;
    }

    /// <summary>The object's fields its scan reads.</summary>
    internal FieldMask Source { get; }

    /// <summary>The batch's rows as the dataset's columns, a struct built in the batch's arena; its index.</summary>
    internal int Build(RecordBatch batch)
    {
        CanonicalArena arena = batch.Arena;
        int rows = batch.RowCount;
        int count = _positions.Length;
        int[]? rented = null;
        Span<int> children = count <= 32 ? stackalloc int[32] : (rented = ArrayPool<int>.Shared.Rent(count));
        try
        {
            for (int p = 0; p < count; p++)
            {
                DType type = _shape.GetField(p);
                int at = _positions[p];
                if (at < 0)
                {
                    children[p] = CanonicalFill.BuildZeroed(arena, type, rows, Validity.AllInvalid);
                    continue;
                }

                // Read again each time: the nodes added above may have moved the arena's records.
                int node = arena.GetNode(batch.RootIndex).GetFieldIndex(at);
                children[p] = _changes[p] switch
                {
                    ColumnChange.None => node,
                    ColumnChange.Nullable => arena.Retyped(node, type),
                    _ => Widen(arena, node, type),
                };
            }

            return arena.AddStruct(_shape, rows, arena.GetNode(batch.RootIndex).Validity, children[..count]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>The struct of the columns asked for, once the ones only the filter read have served it.</summary>
    internal int Final(CanonicalArena arena, int root)
    {
        if (_kept is null)
        {
            return root;
        }

        int[]? rented = null;
        Span<int> children = _kept.Length <= 32 ? stackalloc int[32] : (rented = ArrayPool<int>.Shared.Rent(_kept.Length));
        try
        {
            for (int k = 0; k < _kept.Length; k++)
            {
                int child = arena.GetNode(root).GetFieldIndex(_kept[k]);
                children[k] = _trims![k].IsAll
                    ? child
                    : ProjectionTrim.Apply(arena, child, FieldMask.All, in _trims[k], _final.GetField(k));
            }

            CanonicalNode node = arena.GetNode(root);
            return arena.AddStruct(_final, node.Length, node.Validity, children[.._kept.Length]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>A column of narrower numbers as the dataset's wider ones, in a buffer of the batch's arena.</summary>
    private int Widen(CanonicalArena arena, int nodeIndex, DType type)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int length = node.Length;
        Validity validity = node.Validity.Kind == ValidityKind.NonNullable && type.Nullability == Nullability.Nullable
            ? Validity.AllValid
            : node.Validity;
        if (node.Kind == CanonicalKind.Null)
        {
            return arena.AddNull(type, length);
        }

        PType from = node.DType.PType;
        PType to = type.PType;
        ReadOnlySpan<byte> values = node.Values.Span;
        VortexBuffer buffer = arena.AllocateUninitialized(length * to.ByteWidth(), 64, out Span<byte> into);
        switch (from)
        {
            case PType.I8:
                Spread<sbyte>(values, to, into);
                break;
            case PType.I16:
                Spread<short>(values, to, into);
                break;
            case PType.I32:
                Spread<int>(values, to, into);
                break;
            case PType.U8:
                Spread<byte>(values, to, into);
                break;
            case PType.U16:
                Spread<ushort>(values, to, into);
                break;
            case PType.U32:
                Spread<uint>(values, to, into);
                break;
            case PType.F16:
                Spread<Half>(values, to, into);
                break;
            case PType.F32:
                Spread<float>(values, to, into);
                break;
            default:
                throw new VortexSchemaException($"'{_key}' holds a column of {from}, which is not widened to {to}.");
        }

        return arena.AddPrimitive(type, length, validity, to, buffer);
    }

    private static void Spread<TFrom>(ReadOnlySpan<byte> values, PType to, Span<byte> into)
        where TFrom : unmanaged, INumberBase<TFrom>
    {
        ReadOnlySpan<TFrom> from = MemoryMarshal.Cast<byte, TFrom>(values);
        switch (to)
        {
            case PType.I16:
                Write(from, MemoryMarshal.Cast<byte, short>(into));
                break;
            case PType.I32:
                Write(from, MemoryMarshal.Cast<byte, int>(into));
                break;
            case PType.I64:
                Write(from, MemoryMarshal.Cast<byte, long>(into));
                break;
            case PType.U16:
                Write(from, MemoryMarshal.Cast<byte, ushort>(into));
                break;
            case PType.U32:
                Write(from, MemoryMarshal.Cast<byte, uint>(into));
                break;
            case PType.U64:
                Write(from, MemoryMarshal.Cast<byte, ulong>(into));
                break;
            case PType.F32:
                Write(from, MemoryMarshal.Cast<byte, float>(into));
                break;
            default:
                Write(from, MemoryMarshal.Cast<byte, double>(into));
                break;
        }
    }

    /// <summary>Each value as the wider type, which holds it exactly.</summary>
    private static void Write<TFrom, TTo>(ReadOnlySpan<TFrom> from, Span<TTo> into)
        where TFrom : unmanaged, INumberBase<TFrom>
        where TTo : unmanaged, INumberBase<TTo>
    {
        into = into[..from.Length];
        for (int i = 0; i < from.Length; i++)
        {
            into[i] = TTo.CreateTruncating(from[i]);
        }
    }
}
