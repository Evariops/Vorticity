using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What a dataset scan skipped, and how: counters rather than timings, so that a caller can check
/// how few objects were read instead of believing a speed.
/// </summary>
public sealed class DatasetScanMetrics
{
    /// <summary>Leaf entries the walk reached.</summary>
    public long ObjectsConsidered { get; internal set; }

    /// <summary>Objects whose own summaries refuted the predicate, so nothing was opened.</summary>
    public long ObjectsSkipped { get; internal set; }

    /// <summary>Objects the scan went to read. <see cref="CacheHits"/> of them cost no open.</summary>
    public long ObjectsOpened { get; internal set; }

    /// <summary>Child pages a node's summaries refuted, each a whole subtree never read.</summary>
    public long SubtreesSkipped { get; internal set; }

    /// <summary>Rents the object cache answered without opening anything.</summary>
    public long CacheHits { get; internal set; }

    /// <summary>The objects a count answered from their entries, without opening them.</summary>
    public long ObjectsCounted { get; internal set; }

    /// <summary>
    /// The most data objects the scan read at once: one in the tree's order, and under
    /// <see cref="DatasetScanBuilder.InKeyOrder(string, bool)"/> the inputs the merge held open.
    /// </summary>
    public int Cursors { get; internal set; }
}

/// <summary>Builds a scan over every object of one version of a dataset.</summary>
public sealed class DatasetScanBuilder
{
    private readonly VortexDataset _dataset;
    private VortexExpr? _filter;
    private string[]? _projection;
    private bool _indexes = true;
    private bool _summaries = true;
    private long _from;
    private long _to = long.MaxValue;
    private bool _rowsSet;
    private string[]? _orderPaths;
    private bool _descending;
    private DatasetScanMetrics? _metrics;

    internal DatasetScanBuilder(VortexDataset dataset) => _dataset = dataset;

    /// <summary>Keeps the rows the predicate selects.</summary>
    public DatasetScanBuilder Where(VortexExpr filter)
    {
        _filter = filter;
        return this;
    }

    /// <summary>Reads only these columns.</summary>
    public DatasetScanBuilder Select(params string[] paths)
    {
        _projection = paths;
        return this;
    }

    /// <summary>
    /// Reads only the dataset's rows <c>[from, to)</c>, in the tree's order. Answered through the
    /// tree's row sums, so an object outside the range is neither opened nor counted and a subtree
    /// outside it is never read.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="InKeyOrder(string, bool)"/> was already set.</exception>
    public DatasetScanBuilder Rows(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        if (_orderPaths is not null)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks the objects' key sources; it cannot also select rows by position (12 §6).");
        }

        _from = from;
        _to = to;
        _rowsSet = true;
        return this;
    }

    /// <summary>
    /// Delivers the rows in the key order of a column, across every object the scan reads, equal
    /// keys in the dataset's order. A batch holds a run of one object's rows, so objects whose keys
    /// interleave give batches as short as one row.
    /// </summary>
    /// <remarks>
    /// On the clustering key an object is opened only once it could hold the next row, so a consumer
    /// that stops after <c>k</c> rows never opens an object whose minimum lies past the k-th key; on
    /// any other column every object the summaries keep may be open at once. A row whose key is null
    /// comes last in both directions, and an object with no source for the column is refused when
    /// the merge reaches it. Mutually exclusive with <see cref="Rows"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="Rows"/> was already set.</exception>
    public DatasetScanBuilder InKeyOrder(string path, bool descending = false) => InKeyOrder([path], descending);

    /// <summary>
    /// Delivers the rows in the order of a composite key, the tuple of the given columns in key
    /// order. A row whose tuple holds a null is in no run and is not delivered.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Rows"/> was already set.</exception>
    public DatasetScanBuilder InKeyOrder(IReadOnlyList<string> paths, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("A key has at least one column.", nameof(paths));
        }

        foreach (string path in paths)
        {
            _ = ClusteringKey.Resolve(_dataset.Schema, path);
        }

        if (_rowsSet)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks the objects' key sources; it cannot also select rows by position (12 §6).");
        }

        _orderPaths = [.. paths];
        _descending = descending;
        return this;
    }

    /// <summary>Turns the per-file index chain on or off; the answer is the same either way.</summary>
    public DatasetScanBuilder WithIndexes(bool indexes)
    {
        _indexes = indexes;
        return this;
    }

    /// <summary>
    /// Turns pruning by the node and object summaries on or off. Off, every object is opened and
    /// every subtree read, and the answer must not change.
    /// </summary>
    public DatasetScanBuilder WithSummaries(bool summaries)
    {
        _summaries = summaries;
        return this;
    }

    /// <summary>Records what the walk skipped.</summary>
    public DatasetScanBuilder WithMetrics(DatasetScanMetrics metrics)
    {
        _metrics = metrics;
        return this;
    }

    /// <summary>The batches of every object the scan did not skip, in key order.</summary>
    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_orderPaths is { } paths)
        {
            await foreach (RecordBatch run in MergedAsync(paths, cancellationToken).ConfigureAwait(false))
            {
                yield return run;
            }

            yield break;
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectLease lease = await _dataset.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                await foreach (RecordBatch batch in Of(lease.File, held).ExecuteAsync()
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>
    /// The rows the scan selects, without materialising them. A range on the clustering key is
    /// counted from the entries: an object whose summary bounds lie wholly inside the range, and
    /// whose key holds no null, contributes its row count without being opened. Any other filter
    /// opens every object the summaries keep.
    /// </summary>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        KeyRange? range = _summaries && !_rowsSet ? KeyRange.Of(_dataset, _filter) : null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (range is { } key && key.Holds(held.Entry.Summaries))
            {
                rows += held.Entry.Rows;
                if (_metrics is { } counted)
                {
                    counted.ObjectsCounted++;
                }

                continue;
            }

            ObjectLease lease = await _dataset.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await Of(lease.File, held).CountAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether the scan would return at least one row; the first object that holds one answers for
    /// the dataset.
    /// </summary>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectLease lease = await _dataset.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                if (await Of(lease.File, held).AnyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The smallest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MinAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>
    /// The largest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MaxAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: false, cancellationToken);

    /// <summary>
    /// The extreme over the objects, each one skipped when its own summary cannot beat the best so
    /// far. A summary's min is at or below the object's true minimum, so a summary min that already
    /// loses cannot hide a winner behind it; the bounds need not be exact, and no shortcut here may
    /// assume they are.
    /// </summary>
    private async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        FilterLiteral best = FilterLiteral.Null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_summaries && Loses(held.Entry.Summaries, path, best, wantMin))
            {
                if (_metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            ObjectLease lease = await _dataset.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                ScanBuilder scan = Of(lease.File, held);
                FilterLiteral candidate = wantMin
                    ? await scan.MinAsync(path, cancellationToken).ConfigureAwait(false)
                    : await scan.MaxAsync(path, cancellationToken).ConfigureAwait(false);
                if (candidate.Kind != FilterLiteralKind.Null && Beats(candidate, best, wantMin))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>Whether an object's summary proves it cannot hold a better value than the best.</summary>
    private static bool Loses(ObjectSummaries summaries, string path, FilterLiteral best, bool wantMin)
    {
        if (best.Kind == FilterLiteralKind.Null || !summaries.TryGet(path, out ColumnSummary column))
        {
            return false;
        }

        if (wantMin)
        {
            return column.HasMin && Comparable(column.Min, best) && KeyCursor.Compare(column.Min, best) >= 0;
        }

        return column.HasMax && Comparable(column.Max, best) && KeyCursor.Compare(column.Max, best) <= 0;
    }

    private static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin) =>
        best.Kind == FilterLiteralKind.Null
        || !Comparable(candidate, best)
        || (wantMin ? KeyCursor.Compare(candidate, best) < 0 : KeyCursor.Compare(candidate, best) > 0);

    /// <summary>Whether two literals are of one comparison kind, so that an order holds between them.</summary>
    private static bool Comparable(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind != FilterLiteralKind.Null;

    /// <summary>
    /// What the scan would read, without reading a data object. A level 0 that has outgrown its
    /// ceiling is reported as a lag rather than refused, since compaction is the caller's own
    /// background job and the answers stay correct meanwhile.
    /// </summary>
    public async ValueTask<DatasetPlan> ExplainAsync(CancellationToken cancellationToken = default)
    {
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        DatasetScanBuilder counted = new DatasetScanBuilder(_dataset)
        {
            _filter = _filter,
            _projection = _projection,
            _indexes = _indexes,
            _summaries = _summaries,
            _from = _from,
            _to = _to,
            _rowsSet = _rowsSet,
            _orderPaths = _orderPaths,
            _descending = _descending,
            _metrics = metrics,
        };

        long objects = 0;
        long rows = 0;
        List<long> keptByLevel = [];
        await foreach (PositionedObject held in counted.WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            objects++;
            rows += held.Entry.Rows;
            while (keptByLevel.Count <= held.Level)
            {
                keptByLevel.Add(0);
            }

            keptByLevel[held.Level]++;
        }

        DatasetLevels levels = _dataset.Levels;
        long[] byLevel = new long[Math.Max(levels.Count, 1)];
        for (int level = 0; level < byLevel.Length; level++)
        {
            byLevel[level] = levels[level].Entries;
        }

        return new DatasetPlan(
            _dataset.Version,
            byLevel,
            levels.LagAtLevelZero(),
            _dataset.ClusteringKeyPaths.Count > 0,
            objects,
            rows,
            metrics.ObjectsSkipped,
            metrics.SubtreesSkipped,
            _orderPaths is null ? null : _orderPaths.Length == 1 ? _orderPaths[0] : "(" + string.Join(", ", _orderPaths) + ")",
            CursorBound(objects, keptByLevel));
    }

    /// <summary>
    /// The most objects the scan will hold open at once, stated before any is read: one in the
    /// tree's order; key-ordered on the clustering key, level 0's objects and one per level above
    /// it; on any other column every object, the summaries giving no disjointness to lean on.
    /// </summary>
    private long CursorBound(long objects, List<long> keptByLevel)
    {
        if (objects == 0)
        {
            return 0;
        }

        if (_orderPaths is not { } paths)
        {
            return 1;
        }

        if (!_summaries || !RidesDisjointLevels(paths))
        {
            return objects;
        }

        long cursors = keptByLevel.Count > 0 ? keptByLevel[0] : 0;
        for (int level = 1; level < keptByLevel.Count; level++)
        {
            if (keptByLevel[level] > 0)
            {
                cursors++;
            }
        }

        return cursors;
    }

    /// <summary>
    /// Whether a merge on those columns reads the key-disjoint objects of a level one after another:
    /// on the clustering key, upward by the tree's exact minima, downward by summary maxima that the
    /// key order respects, which a composite key has none of.
    /// </summary>
    private bool RidesDisjointLevels(string[] paths) =>
        IsClusteringKey(paths)
        && (!_descending || (paths.Length == 1 && HasKeyOrderBounds(ClusteringKey.Resolve(_dataset.Schema, paths[0]))));

    /// <summary>
    /// The objects this scan will read, in key order, each with its first row: the walk with its
    /// skips applied, and not one byte of any data object read.
    /// </summary>
    public IAsyncEnumerable<PositionedObject> ObjectsAsync(CancellationToken cancellationToken = default) =>
        WalkAsync(cancellationToken);

    private IAsyncEnumerable<PositionedObject> WalkAsync(CancellationToken cancellationToken) =>
        _dataset.WalkAsync(
            _summaries && _filter is { } filter ? new SummaryPruner(filter) : null,
            _from,
            _to,
            _metrics,
            cancellationToken);

    private void RecordOpen(ObjectLease lease)
    {
        if (_metrics is { } metrics)
        {
            metrics.ObjectsOpened++;
            metrics.Cursors = Math.Max(metrics.Cursors, 1);
            if (lease.WasCached)
            {
                metrics.CacheHits++;
            }
        }
    }

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged.</summary>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string[] paths, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        string[] selected = _projection ?? [];
        bool drop = _projection is not null && !Array.TrueForAll(paths, path => Covers(selected, path));
        Projection? kept = null;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _dataset,
            MergeObjectsAsync(paths, cancellationToken),
            paths,
            _descending,
            rankedTies: true,
            file => OrderedOf(file, paths, drop),
            RecordOpen,
            cancellationToken);
        await using (merge.ConfigureAwait(false))
        {
            try
            {
                while (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    RecordBatch run = merge.Current;
                    if (!drop)
                    {
                        yield return run;
                        continue;
                    }

                    kept ??= Projection.Parse(run.Schema, selected);
                    RecordBatch projected = run.Project(kept.Value);
                    try
                    {
                        yield return projected;
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
    /// its ties go by. On the clustering key upward the tree already is that order, and the walk is
    /// lazy, so a consumer that stops early stops the walk; anywhere else the kept objects are
    /// collected and sorted by their summaries' bound, without being opened. A composite key is
    /// bounded upward by its first column, whose encoded minimum prefixes every tuple above it, and
    /// not downward.
    /// </summary>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string[] paths, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string path = paths[0];
        if (!_summaries || (!_descending && IsClusteringKey(paths)))
        {
            // Without summaries there is no bound to lean on: every object is opened before a row
            // goes out, and the lazy merge must answer the same as this eager one.
            long ordinal = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> bound = _summaries ? VortexDataset.OrderOf(held.TreeKey) : default;
                yield return new MergeObject(held.Entry, bound, _descending ? -ordinal : ordinal);
                ordinal++;
            }

            yield break;
        }

        DType dtype = ClusteringKey.Resolve(_dataset.Schema, path);
        RowSortField field = KeyOrderedMerge.Field(_descending);
        List<MergeObject> objects = [];
        long position = 0;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> bound = paths.Length > 1 && _descending
                ? default
                : SummaryBound(held.Entry.Summaries, path, dtype, field, _descending);
            objects.Add(new MergeObject(held.Entry, bound, _descending ? -position : position));
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

    /// <summary>Whether the columns are the dataset's whole clustering key, in order.</summary>
    private bool IsClusteringKey(string[] paths) =>
        _dataset.Key is { } key && System.Linq.Enumerable.SequenceEqual(key.Paths, paths, StringComparer.Ordinal);

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

    /// <summary>Whether a column's summary bounds are bounds in the key order (see <see cref="SummaryBound"/>).</summary>
    private static bool HasKeyOrderBounds(DType dtype) => dtype.Kind switch
    {
        DTypeKind.Primitive => !dtype.PType.IsFloat(),
        DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary => true,
        _ => false,
    };

    /// <summary>Whether a projection already reads a path, itself or through an ancestor.</summary>
    private static bool Covers(string[] projection, string path)
    {
        foreach (string selected in projection)
        {
            if (string.Equals(selected, path, StringComparison.Ordinal)
                || (path.StartsWith(selected, StringComparison.Ordinal)
                    && path.Length > selected.Length
                    && path[selected.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One object's own key-ordered scan, carrying this builder's filter and projection.</summary>
    private ScanBuilder OrderedOf(VortexFile file, string[] paths, bool withKey)
    {
        ScanBuilder scan = file.Scan();
        if (_filter is { } filter)
        {
            scan = scan.Where(filter);
        }

        if (_projection is { } projection)
        {
            scan = scan.Project(projection);
            if (withKey)
            {
                scan = scan.Project(paths);
            }
        }

        scan = scan.InKeyOrder(paths, _descending);
        return _indexes ? scan : scan.WithIndexes(false);
    }

    /// <summary>The file's own scan, carrying this builder's filter, projection and row range.</summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held)
    {
        ScanBuilder scan = file.Scan();
        if (_filter is { } filter)
        {
            scan = scan.Where(filter);
        }

        if (_projection is { } projection)
        {
            scan = scan.Project(projection);
        }

        // The range is the dataset's; the file's is the part of it that falls inside this object.
        long lower = Math.Max(_from - held.FirstRow, 0);
        long upper = _to == long.MaxValue ? held.Entry.Rows : Math.Min(_to - held.FirstRow, held.Entry.Rows);
        if (lower > 0 || upper < held.Entry.Rows)
        {
            scan = scan.Rows(new RowRange(lower, Math.Max(upper, lower)));
        }

        return _indexes ? scan : scan.WithIndexes(false);
    }
}

/// <summary>One data object and where its rows start in the dataset.</summary>
/// <param name="Entry">Its leaf entry, summaries included.</param>
/// <param name="FirstRow">Its first row among the dataset's, in the tree's order.</param>
public readonly record struct PositionedObject(ObjectEntry Entry, long FirstRow)
{
    /// <summary>The level whose tree holds it; level 0 is the one an append lands in.</summary>
    public int Level { get; init; }

    /// <summary>Its key in that tree: what orders it, then its uid.</summary>
    internal ReadOnlyMemory<byte> TreeKey { get; init; }
}

/// <summary>What a dataset scan would read, and what it costs.</summary>
/// <param name="Version">The version it would read.</param>
/// <param name="ObjectsByLevel">How many objects each level holds, level 0 first.</param>
/// <param name="Lag">
/// The objects level 0 holds above its ceiling: the extra objects every key lookup has to touch
/// until compaction catches up, and zero when it has.
/// </param>
/// <param name="IsClustered">
/// Whether a clustering key is declared. Without one, levels are size tiers and a lookup touches
/// every object the summaries cannot refute, which is output-sensitive and not bounded.
/// </param>
/// <param name="Objects">The objects the scan would open.</param>
/// <param name="Rows">Their rows, before the filter.</param>
/// <param name="ObjectsSkipped">Objects whose own summaries refuted the predicate.</param>
/// <param name="SubtreesSkipped">Child pages a node's summaries refuted, unread.</param>
/// <param name="Order">The column a key-ordered scan delivers its rows by, or null for the tree's order.</param>
/// <param name="Cursors">
/// The most objects the scan will hold open at once: one in the tree's order; key-ordered on the
/// clustering key, level 0's objects and one per level above it; on any other column, every object
/// it would open.
/// </param>
public sealed record DatasetPlan(
    ulong Version,
    IReadOnlyList<long> ObjectsByLevel,
    long Lag,
    bool IsClustered,
    long Objects,
    long Rows,
    long ObjectsSkipped,
    long SubtreesSkipped,
    string? Order,
    long Cursors);
