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
    private long CursorBound(long objects, List<long> keptByLevel)
    {
        if (objects == 0)
        {
            return 0;
        }

        if (_orderPaths is not { } paths)
        {
            return objects > 1 && _options.Prefetch > 0 ? 2 : 1;
        }

        if (!_summaries || !RidesDisjointLevels(paths))
        {
            return objects;
        }

        // Level 0's objects, and a late level's, overlap: each may be open at once.
        long cursors = keptByLevel.Count > 0 ? keptByLevel[0] : 0;
        for (int level = 1; level < keptByLevel.Count; level++)
        {
            cursors += _version.Levels.IsDisjoint(level) ? Math.Min(keptByLevel[level], 1) : keptByLevel[level];
        }

        return cursors;
    }

    /// <summary>
    /// Whether a merge on those columns reads the key-disjoint objects of a level one after another:
    /// on the sort key, upward by the tree's exact minima, downward by summary maxima that the
    /// key order respects, which a composite key has none of.
    /// </summary>
    private bool RidesDisjointLevels(string[] paths) =>
        IsSortKey(paths)
        && (!_descending || (paths.Length == 1 && HasKeyOrderBounds(DatasetKey.Resolve(_version.Schema.DType, paths[0]))));

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged.</summary>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        FieldMask mask = _mask ?? FieldMask.All;
        bool drop = !mask.IsAll && !Array.TrueForAll(paths, path => Covers(mask, _version.Schema.DType, path));
        ScanProjection? kept = null;
        // Read downward in their own order, objects give the reverse of the key's order, the nulls of its first column last.
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _version,
            MergeObjectsAsync(paths, cancellationToken),
            SortKey.OfColumns(_version.Schema.DType, paths, _descending, reversed: InObjectOrder(paths)),
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
    /// The objects the merge reads, in the order of a lower bound on their keys, each with the rank
    /// its ties go by. On the sort key upward the tree already is that order, and the walk is
    /// lazy, so a consumer that stops early stops the walk; anywhere else the kept objects are
    /// collected and sorted by their summaries' bound, without being opened. A composite key is
    /// bounded upward by its first column, whose encoded minimum prefixes every tuple above it, and
    /// not downward.
    /// </summary>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string path = paths[0];
        if (!_summaries || (!_descending && IsSortKey(paths)))
        {
            // Without summaries there is no bound to lean on: every object is opened before a row
            // goes out, and the lazy merge must answer the same as this eager one.
            long ordinal = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> bound = _summaries ? VortexDataset.OrderOf(held.TreeKey) : default;
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
            ReadOnlyMemory<byte> bound = paths.Length > 1 && _descending
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

    /// <summary>Whether the columns are the dataset's whole sort key, in order.</summary>
    private bool IsSortKey(string[] paths) =>
        _version.Schema.Key is { } key && System.Linq.Enumerable.SequenceEqual(key.Paths, paths, StringComparer.Ordinal);

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
    /// Whether the columns lead the dataset's key components as they are (<see cref="DatasetSchema.KeyColumns"/>), in whose
    /// order every object's rows lie, nulls last: an object is then read in its own order, with no index.
    /// </summary>
    private bool InObjectOrder(string[] paths)
    {
        if (_version.Schema.KeyColumns is not { } key || paths.Length > key.Count)
        {
            return false;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if (!string.Equals(paths[i], key[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

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
