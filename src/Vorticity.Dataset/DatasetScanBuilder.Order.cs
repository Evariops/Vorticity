using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// The most objects the scan will hold open at once, stated before any is read: one in the
    /// tree's order, two with a prefetch, which opens the next object ahead; key-ordered on the
    /// sort key, level 0's objects, a late level's, and one per other level; on any other column
    /// every object, the summaries giving no disjointness to lean on.
    /// </summary>
    private long CursorBound(long objects, List<long> keptByLevel, long partitions)
    {
        if (objects == 0)
        {
            return 0;
        }

        if (_orderPaths is not { } paths)
        {
            return objects > 1 && _options.Prefetch > 0 ? 2 : 1;
        }

        if (!_summaries || KeyBounded(paths) is not { } order)
        {
            return objects;
        }

        // Level 0's objects, and a late level's, overlap: each may be open at once. A disjoint level holds one open in the
        // tree's order, and one a partition in the order of the key within it, whose partitions the merge reads side by side;
        // downward, one more, whose rows of a null first column the read gives last.
        long each = (ReferenceEquals(order.Order, _version.Schema.TreeOrder) ? 1 : Math.Max(partitions, 1)) * (_descending ? 2 : 1);
        long cursors = keptByLevel.Count > 0 ? keptByLevel[0] : 0;
        for (int level = 1; level < keptByLevel.Count; level++)
        {
            cursors += _version.Levels.IsDisjoint(level) ? Math.Min(keptByLevel[level], each) : keptByLevel[level];
        }

        return Math.Min(cursors, objects);
    }

    /// <summary>
    /// The order a merge on <paramref name="paths"/> bounds each object by exactly, from its keys in the tree: the tree's
    /// when the columns lead it (<see cref="DatasetSchema.TreeOrder"/>), the objects' own under a partition otherwise
    /// (<see cref="DatasetSchema.ObjectOrder"/>), with the values that precede the order's in an object's keys, the
    /// partition's for the objects' own. Null when the columns lead neither: the summaries bound the objects then.
    /// </summary>
    private (ColumnOrder Order, int Skip)? KeyBounded(string[] paths)
    {
        DatasetSchema schema = _version.Schema;
        if (schema.TreeOrder is { } tree && tree.Leads(paths))
        {
            return (tree, 0);
        }

        return schema.ObjectOrder is { } own && own.Leads(paths) ? (own, schema.Partitioned ? 1 : 0) : null;
    }

    /// <summary>
    /// The bound of an object on the first <paramref name="count"/> columns of <paramref name="order"/>, its key
    /// <paramref name="key"/> as the tree encodes it, past its first <paramref name="skip"/> values: the values at the order's
    /// positions, as a key of those columns encodes them, a slice of the key when they lie side by side. Empty when the key
    /// does not walk.
    /// </summary>
    private ReadOnlyMemory<byte> LowBound(ReadOnlyMemory<byte> key, ColumnOrder order, int skip, int count)
    {
        SortKey tree = _version.Schema.Tree!;
        int last = order.Positions[count - 1] + skip;
        Span<int> ends = stackalloc int[tree.Count];
        if (key.IsEmpty || !tree.Ends(key.Span, ends[..(last + 1)]))
        {
            return default;
        }

        static int Start(ReadOnlySpan<int> ends, int position) => position == 0 ? 0 : ends[position - 1];
        int first = order.Positions[0] + skip;
        bool adjacent = true;
        for (int i = 1; i < count && adjacent; i++)
        {
            adjacent = order.Positions[i] == order.Positions[i - 1] + 1;
        }

        if (adjacent)
        {
            return key.Slice(Start(ends, first), ends[last] - Start(ends, first));
        }

        int length = 0;
        for (int i = 0; i < count; i++)
        {
            int position = order.Positions[i] + skip;
            length += ends[position] - Start(ends, position);
        }

        byte[] bound = new byte[length];
        for (int i = 0, at = 0; i < count; i++)
        {
            int position = order.Positions[i] + skip;
            key.Span[Start(ends, position)..ends[position]].CopyTo(bound.AsSpan(at));
            at += ends[position] - Start(ends, position);
        }

        return bound;
    }

    /// <summary>
    /// The bound of an object on the first <paramref name="count"/> columns of <paramref name="order"/> downward: its last key
    /// <paramref name="last"/>, as the tree encodes it, past its first <paramref name="skip"/> values, read at the order's
    /// positions and encoded as <paramref name="merged"/>, the merge's key, orders rows. Empty when the key does not walk, or
    /// its first column is null: the object's rows then end with those nulls, which a downward read gives last.
    /// </summary>
    private ReadOnlyMemory<byte> HighBound(ReadOnlyMemory<byte> last, ColumnOrder order, int skip, int count, SortKey merged)
    {
        SortKey tree = _version.Schema.Tree!;
        Span<int> ends = stackalloc int[tree.Count];
        if (last.IsEmpty || !tree.Ends(last.Span, ends[..(order.Positions[count - 1] + skip + 1)]))
        {
            return default;
        }

        FilterLiteral[] values = new FilterLiteral[count];
        for (int i = 0; i < count; i++)
        {
            int position = order.Positions[i] + skip;
            values[i] = RowEncoder.ReadFirstValue(last.Span[(position == 0 ? 0 : ends[position - 1])..], tree.TypeAt(position));
        }

        return values[0].Kind == FilterLiteralKind.Null ? default : merged.Encode(values);
    }

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged.</summary>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        FieldMask mask = _mask ?? FieldMask.All;
        bool drop = !mask.IsAll && !Array.TrueForAll(paths, path => Covers(mask, _version.Schema.DType, path));
        ScanProjection? kept = null;
        // Read downward in their own order, objects give the reverse of the key's order, the nulls of its first column last.
        SortKey key = SortKey.OfColumns(_version.Schema.DType, paths, _descending, reversed: InObjectOrder(paths));
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _version,
            MergeObjectsAsync(paths, key, cancellationToken),
            key,
            rankedTies: true,
            (lease, entry) => OrderedBatchesAsync(lease, entry, paths, drop, cancellationToken),
            lease => RecordOpen(lease),
            cancellationToken);
        RecordBatch? view = null;
        await using (merge.ConfigureAwait(false))
        {
            try
            {
                while (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    RecordBatch run = merge.Current;
                    long firstRow = merge.CurrentFirstRow;
                    if (!drop)
                    {
                        yield return InDataset(run, firstRow, ref view);
                        continue;
                    }

                    kept ??= ScanProjection.Parse(run.DType, PathsOf(mask, _version.Schema.DType));
                    RecordBatch projected = run.Project(kept.Value);
                    try
                    {
                        yield return InDataset(projected, firstRow, ref view);
                    }
                    finally
                    {
                        projected.Dispose();
                    }
                }
            }
            finally
            {
                if (_metrics is { } metrics)
                {
                    metrics.Cursors = Math.Max(metrics.Cursors, merge.MostOpen);
                }
            }
        }
    }

    /// <summary>
    /// The objects the merge reads, in the order of a bound on their keys in the merge's, <paramref name="key"/>, each with
    /// the rank its ties go by. On columns that lead the tree's order upward, the tree already is that order and its keys
    /// are exact minima: the walk is lazy, so a consumer that stops early stops the walk. On columns that lead the objects'
    /// own order, the kept objects are collected and sorted without being opened, by their exact minima upward, their tree
    /// keys past the partition, and their exact maxima downward, their last keys: a read in the key's order under a
    /// partition holds open an object of each partition, where it held every object. On any other columns they are sorted
    /// by their summaries' bound on the first one, upward alone for several.
    /// </summary>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string[] paths, SortKey key, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string path = paths[0];
        (ColumnOrder Order, int Skip)? bounded = _summaries ? KeyBounded(paths) : null;
        if (!_summaries || (!_descending && bounded is { } byTree && ReferenceEquals(byTree.Order, _version.Schema.TreeOrder)))
        {
            // Without summaries there is no bound to lean on: every object is opened before a row
            // goes out, and the lazy merge must answer the same as this eager one.
            long ordinal = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> bound = bounded is { } tree ? LowBound(VortexDataset.OrderOf(held.TreeKey), tree.Order, tree.Skip, paths.Length) : default;
                yield return new MergeObject(held.Entry, bound, _descending ? -ordinal : ordinal, held.FirstRow);
                ordinal++;
            }

            yield break;
        }

        DType dtype = DatasetKey.Resolve(_version.Schema.DType, path);
        RowSortField field = KeyOrderedMerge.Field(_descending);
        List<MergeObject> objects = [];
        long position = 0;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> bound = bounded is { } exact
                ? _descending
                    ? HighBound(held.Entry.LastKey, exact.Order, exact.Skip, paths.Length, key)
                    : LowBound(VortexDataset.OrderOf(held.TreeKey), exact.Order, exact.Skip, paths.Length)
                : paths.Length > 1 && _descending
                    ? default
                    : SummaryBound(held.Entry.Summaries, path, dtype, field, _descending);
            objects.Add(new MergeObject(held.Entry, bound, _descending ? -position : position, held.FirstRow));
            position++;
        }

        objects.Sort(static (left, right) =>
        {
            int order = left.Bound.Span.SequenceCompareTo(right.Bound.Span);
            return order != 0 ? order : left.Rank.CompareTo(right.Rank);
        });
        foreach (MergeObject held in objects)
        {
            yield return held;
        }
    }

    /// <summary>
    /// A summary's bound on a column, encoded as the merge compares keys: its minimum upward, its
    /// maximum downward, and empty when the summary gives none that the key order respects. A float
    /// gives none, its zone bounds excluding NaN while the key order places NaN first or last, so an
    /// object holding one would have a key outside its own summary.
    /// </summary>
    private static ReadOnlyMemory<byte> SummaryBound(
        ObjectSummaries summaries, string path, DType dtype, RowSortField field, bool descending)
    {
        if (!HasKeyOrderBounds(dtype) || !summaries.TryGet(path, out ColumnSummary column))
        {
            return default;
        }

        bool known = descending ? column.HasMax : column.HasMin;
        FilterLiteral bound = descending ? column.Max : column.Min;
        if (!known || bound.Kind == FilterLiteralKind.Null)
        {
            return default;
        }

        return RowEncoder.EncodeKey([bound], [dtype], [field]);
    }

    /// <summary>
    /// Whether a column's summary bounds are bounds in the key order (see <see cref="SummaryBound"/>). An
    /// extension's, a timestamp's say, are its storage's, which the key order encodes it as.
    /// </summary>
    private static bool HasKeyOrderBounds(DType dtype) => dtype.Kind switch
    {
        DTypeKind.Primitive => !dtype.PType.IsFloat(),
        DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary => true,
        DTypeKind.Extension => HasKeyOrderBounds(dtype.StorageType),
        _ => false,
    };

    /// <summary>Whether a projection already reads a path, itself or through an ancestor.</summary>
    private static bool Covers(FieldMask mask, DType schema, string path)
    {
        FieldMask at = mask;
        DType type = schema;
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            if (at.IsAll)
            {
                return true;
            }

            int field = ColumnPath.IndexOf(type, rest[segment]);
            if (field < 0 || !at.Includes(field))
            {
                return false;
            }

            at = at.Descend(field);
            type = type.GetField(field);
        }

        return true;
    }

    /// <summary>The <c>.</c>-separated paths a projection reads, by the schema's field names.</summary>
    private static string[] PathsOf(FieldMask mask, DType schema)
    {
        List<string> paths = [];
        Collect(mask, schema, string.Empty, paths);
        return [.. paths];

        static void Collect(FieldMask at, DType type, string prefix, List<string> into)
        {
            if (prefix.Length > 0 && (at.IsAll || at.IsEmpty || type.Kind != DTypeKind.Struct))
            {
                into.Add(prefix);
                return;
            }

            for (int i = 0; i < at.NamedFieldCount; i++)
            {
                int field = at.GetNamedField(i);
                string name = type.GetFieldName(field);
                Collect(at.Descend(field), type.GetField(field), prefix.Length == 0 ? name : prefix + "." + name, into);
            }
        }
    }

    /// <summary>
    /// Whether the columns lead those whose order every object's rows lie in, nulls last (<see cref="DatasetSchema.ObjectOrder"/>),
    /// or the tree's, whose partition each object holds one value of (<see cref="DatasetSchema.TreeOrder"/>): an object is
    /// then read in its own order, with no index.
    /// </summary>
    private bool InObjectOrder(string[] paths) =>
        _version.Schema.ObjectOrder?.Leads(paths) == true || _version.Schema.TreeOrder?.Leads(paths) == true;

    /// <summary><paramref name="mask"/> with the key's columns <paramref name="paths"/> read too when <paramref name="withKey"/> asks.</summary>
    private FieldMask? Keyed(FieldMask? mask, string[] paths, bool withKey)
    {
        if (!withKey || mask is not { } projection)
        {
            return mask;
        }

        FieldMaskBuilder keyed = new FieldMaskBuilder().Include(in projection);
        foreach (string path in paths)
        {
            keyed.Include(ToolPaths.Resolve(_version.Schema.Columns, path));
        }

        return keyed.Build();
    }

    /// <summary>
    /// One object's rows in the order of columns that lead the dataset's key, <paramref name="first"/> the first of them as the
    /// object names it: the object's own order, its writer having sorted its rows by the key, nulls last. Downward, its rows
    /// whose first column holds a value come in reverse, then those where it is null, in reverse too, as a key-ordered read
    /// gives them; its marked rows left out either way.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> InObjectOrderAsync(
        VortexFile file, ObjectEntry entry, string first, VortexExpr? filter, FieldMask? projection, bool canonical, CancellationToken cancellationToken)
    {
        if (!_descending)
        {
            return LiveAsync(Scan(null));
        }

        bool nullable = ColumnPath.MayBeNull(file.DType, first);
        IAsyncEnumerable<RecordBatch> keyed = LiveRows.ReversedAsync(
            LiveAsync(Scan(nullable ? Expr.IsNotNull(Expr.Field(first)) : null).InReverse()), cancellationToken);
        return nullable
            ? System.Linq.AsyncEnumerable.Concat(keyed, LiveRows.ReversedAsync(LiveAsync(Scan(Expr.IsNull(Expr.Field(first))).InReverse()), cancellationToken))
            : keyed;

        ScanBuilder Scan(VortexExpr? also) =>
            Configure(file.ScanBuilder(), also is null ? filter : filter is null ? also : Expr.And(also, filter), projection, canonical);

        IAsyncEnumerable<RecordBatch> LiveAsync(ScanBuilder scan) =>
            entry.HasDeletions
                ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
                : scan.ExecuteAsync();
    }

    /// <summary>One object's own key-ordered scan, carrying this builder's filter, projection and options.</summary>
    private ScanBuilder OrderedOf(VortexFile file, string[] paths, bool withKey)
    {
        ScanBuilder scan = Configure(file.ScanBuilder(), _filter, _mask, canonical: false);
        if (withKey)
        {
            scan.Project(paths);
        }

        return scan.InKeyOrder(paths, _descending);
    }

    /// <summary>
    /// One object's rows in key order, gathered: its own key-ordered scan, reshaped when it was
    /// written under another schema. An object that lacks the key's column holds only null keys,
    /// which come last in either direction, so its rows come in their own order; a tuple holding a
    /// null is in no run, so an object that lacks a column of a composite key delivers none.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> OrderedBatchesAsync(ObjectLease lease, ObjectEntry entry, string[] paths, bool withKey, CancellationToken cancellationToken)
    {
        bool inObjectOrder = InObjectOrder(paths);
        if (ColumnsOf(lease, entry) is not { } columns)
        {
            if (inObjectOrder)
            {
                return InObjectOrderAsync(lease.File, entry, paths[0], _filter, Keyed(_mask, paths, withKey), canonical: false, cancellationToken);
            }

            return entry.HasDeletions
                ? LiveOrderedAsync(lease.File, entry, paths, withKey, cancellationToken)
                : OrderedOf(lease.File, paths, withKey).ExecuteAsync();
        }

        FieldMask projection = _mask ?? FieldMask.All;
        if (withKey)
        {
            FieldMaskBuilder keyed = new FieldMaskBuilder().Include(in projection);
            foreach (string path in paths)
            {
                keyed.Include(ToolPaths.Resolve(_version.Schema.Columns, path));
            }

            projection = keyed.Build();
        }

        ObjectReadPlan read = columns.Read(_filter, projection);
        string[]? source = columns.SourcePaths(paths);
        if (read.Fate == FilterFate.None || (source is null && paths.Length > 1))
        {
            return System.Linq.AsyncEnumerable.Empty<RecordBatch>();
        }

        ScanBuilder scan = Configure(lease.File.ScanBuilder(), read.Pushed, read.Shape.Source, canonical: true).WithCompaction(true);
        IAsyncEnumerable<RecordBatch> rows;
        if (source is not null && inObjectOrder)
        {
            rows = InObjectOrderAsync(lease.File, entry, source[0], read.Pushed, read.Shape.Source, canonical: true, cancellationToken);
        }
        else if (source is not null)
        {
            scan = scan.InKeyOrder(source, _descending);
            rows = entry.HasDeletions
                ? LiveRows.OrderedAsync(
                    scan,
                    NullsOf(lease.File, source[0], paths.Length > 1, read.Pushed, read.Shape.Source),
                    entry.Deletions,
                    _descending,
                    cancellationToken)
                : scan.ExecuteAsync();
        }
        else
        {
            // Every key is null, and null keys come last in their own order either way.
            rows = entry.HasDeletions
                ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
                : scan.ExecuteAsync();
        }

        return ObjectColumns.ReshapeAsync(rows, read.Shape, read.Evaluated, 0, compact: true, cancellationToken);
    }

    /// <summary>
    /// An object's rows in key order without the ones it deleted: its own key-ordered scan, which
    /// skips their entries as it walks, then the live rows whose key is null.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> LiveOrderedAsync(VortexFile file, ObjectEntry entry, string[] paths, bool withKey, CancellationToken cancellationToken)
    {
        FieldMask? projection = _mask;
        if (withKey && _mask is { } mask)
        {
            FieldMaskBuilder keyed = new FieldMaskBuilder().Include(in mask);
            foreach (string path in paths)
            {
                keyed.Include(ToolPaths.Resolve(_version.Schema.Columns, path));
            }

            projection = keyed.Build();
        }

        return LiveRows.OrderedAsync(
            OrderedOf(file, paths, withKey),
            NullsOf(file, paths[0], paths.Length > 1, _filter, projection),
            entry.Deletions,
            _descending,
            cancellationToken);
    }

    /// <summary>
    /// The scan of an object's rows whose key is null under a filter and a projection, decoded; null
    /// when the key cannot be null there, or is composite, whose run holds no tuple with a null.
    /// </summary>
    private ScanBuilder? NullsOf(VortexFile file, string path, bool composite, VortexExpr? filter, FieldMask? projection)
    {
        if (composite || !ColumnPath.MayBeNull(file.DType, path))
        {
            return null;
        }

        VortexExpr isNull = Expr.IsNull(Expr.Field(path));
        return Configure(file.ScanBuilder(), filter is null ? isNull : Expr.And(isNull, filter), projection, canonical: true);
    }
}
