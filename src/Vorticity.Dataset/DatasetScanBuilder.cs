// A scan across a dataset's objects - docs/13-dataset.md §14's acceptance: "every answer it gives
// must equal the answer a single file would give".
//
// THE WHOLE SHAPE OF IT, and why it is this small. A data object is a plain Vortex file (§3), so
// the scan a dataset runs over one object is the scan the core already runs over a file, filter
// included: the pruning of 11 §6, the zone maps, the indexes, all of it. What a dataset adds is
// the ORDER -- the objects in key order -- and the two skips that happen BEFORE any of that: a
// subtree whose summaries refute the predicate is never descended into, and an object whose own
// summaries refute it is never opened (§4.2).
//
// WHICH IS WHY THE PRUNING IS MEASURED AND NOT ASSERTED. `DatasetScanMetrics` counts the objects a
// walk considered, the ones it opened, and the subtrees it skipped whole, so a test can say "this
// filter opened nothing" instead of "this filter is fast". The same filter with `WithIndexes(false)`
// and with the summaries refuting nothing must still return the same rows -- that is the acceptance,
// and the metrics are how the SHORTCUT is shown to have been taken rather than assumed.
//
// THE TERMINALS ARE THE SCAN WITH A DIFFERENT OUTPUT, one level up (12 §5). `AnyAsync` stops at the
// first object that holds a row; `MinAsync` and `MaxAsync` skip an object whose own summary cannot
// beat the best so far, which is §6.6's "`ORDER BY x LIMIT k` prunes by the summaries" at k = 1.
//
// KEY ORDER ACROSS OBJECTS IS THE COMPACTION'S MERGE, READ INSTEAD OF WRITTEN (§6.6). `InKeyOrder`
// runs the core's own `InKeyOrder` over each object — its run or its sorted column, with this
// builder's filter and projection — and `KeyOrderedMerge` merges them, the very merge a compaction
// writes its outputs with (§5.3). What a dataset adds is the order the objects are OFFERED in: on the
// clustering key, the tree's own order, whose leaf key is the exact encoded minimum, walked lazily so
// a consumer that stops early stops the walk too; on any other column, or backwards, the objects the
// summaries keep, sorted by the bound their summaries give. `ORDER BY x LIMIT k` is that read and a
// consumer that stops: an object is opened only once it could hold the next row.
//
// THE KEY COLUMN IS READ WHATEVER `Select` SAYS, because a merge compares rows by it, and dropped
// before a batch is handed on (`RecordBatch.Project`) — the scan's own "where then project" for a
// filter's columns, applied one level up.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scan;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>What a dataset scan skipped, and how.</summary>
/// <remarks>
/// Counters, not timings: the claim a dataset makes is that it reads fewer objects, and that is a
/// number the caller can check rather than a speed it has to believe.
/// </remarks>
public sealed class DatasetScanMetrics
{
    /// <summary>Leaf entries the walk reached.</summary>
    public long ObjectsConsidered { get; internal set; }

    /// <summary>Objects whose own summaries refuted the predicate, so nothing was opened.</summary>
    public long ObjectsSkipped { get; internal set; }

    /// <summary>Objects the scan went to read. <see cref="CacheHits"/> of them cost no open.</summary>
    public long ObjectsOpened { get; internal set; }

    /// <summary>Child pages a node's summaries refuted, each a whole subtree never read (§4.2).</summary>
    public long SubtreesSkipped { get; internal set; }

    /// <summary>Rents the object cache answered without opening anything.</summary>
    public long CacheHits { get; internal set; }

    /// <summary>
    /// The most data objects the scan read at once: one in the tree's order, and under
    /// <see cref="DatasetScanBuilder.InKeyOrder"/> the inputs the merge held open — §6.6's
    /// "≤ 8 + L cursors", measured.
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
    private string? _orderPath;
    private bool _descending;
    private DatasetScanMetrics? _metrics;

    internal DatasetScanBuilder(VortexDataset dataset) => _dataset = dataset;

    /// <summary>Keeps the rows the predicate selects.</summary>
    /// <param name="filter">The predicate, in the core's expression model.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder Where(VortexExpr filter)
    {
        _filter = filter;
        return this;
    }

    /// <summary>Reads only these columns.</summary>
    /// <param name="paths">Their paths.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder Select(params string[] paths)
    {
        _projection = paths;
        return this;
    }

    /// <summary>Reads only the dataset's rows <c>[from, to)</c>, in the tree's order (§6.6).</summary>
    /// <param name="from">The first row, inclusive.</param>
    /// <param name="to">One past the last.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is negative or inverted.</exception>
    /// <exception cref="InvalidOperationException"><see cref="InKeyOrder"/> was already set.</exception>
    /// <remarks>
    /// Answered through the tree's row sums, so an object outside the range is neither opened nor
    /// counted and a subtree outside it is never read: O(log N) plus the objects the range touches.
    /// </remarks>
    public DatasetScanBuilder Rows(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        if (_orderPath is not null)
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
    /// Delivers the rows in the key order of <paramref name="path"/>, across every object the scan
    /// reads (12 §6, 13 §6.6).
    /// </summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="descending">Whether the largest key comes first, ties then in the exact reverse order.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// Each object is read by the core's own <c>InKeyOrder</c> with this builder's filter and
    /// projection, and the objects are merged: batches in key order, consecutive batches in key
    /// order, and equal keys in the dataset's order — the tree's order of the objects, then each
    /// one's row order. A batch holds a run of one object's rows, so objects whose keys interleave
    /// give batches as short as one row.
    /// </para>
    /// <para>
    /// <b>What it costs</b> depends on the column. On the dataset's clustering key an object is
    /// opened only once it could hold the next row: the inputs held open are level 0's objects and
    /// one per level above it (§6.6: "≤ 8 + L cursors, bounded"), and a consumer that stops after
    /// <c>k</c> rows has never opened an object whose minimum lies past the k-th key — which is
    /// <c>ORDER BY key LIMIT k</c>. On any other column every object the summaries keep may be open
    /// at once: output-sensitive, as §6.6 says. <see cref="ExplainAsync"/> states the bound before
    /// reading; <see cref="DatasetScanMetrics.Cursors"/> measures it after.
    /// </para>
    /// <para>
    /// <b>A row whose key is null is in no key source and is not delivered</b> (12 §6). An object
    /// with no source for the column — no run on it and no sorted column — is refused when the merge
    /// reaches it, as the core refuses a file. Mutually exclusive with <see cref="Rows"/>, as in the
    /// core.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> names no column of the dataset.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Rows"/> was already set.</exception>
    public DatasetScanBuilder InKeyOrder(string path, bool descending = false)
    {
        _ = ClusteringKey.Resolve(_dataset.Schema, path);
        if (_rowsSet)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks the objects' key sources; it cannot also select rows by position (12 §6).");
        }

        _orderPath = path;
        _descending = descending;
        return this;
    }

    /// <summary>Turns the per-file index chain on or off (08 §1's equivalence).</summary>
    /// <param name="indexes">Whether to consult them.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder WithIndexes(bool indexes)
    {
        _indexes = indexes;
        return this;
    }

    /// <summary>Turns the node and object summaries of §4.2 on or off.</summary>
    /// <param name="summaries">Whether to prune with them.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Off, every object is opened and every subtree read, and the ANSWER MUST NOT CHANGE. That is
    /// the equivalence of 08 §1 applied one level up, and it is what the acceptance tests compare.
    /// </remarks>
    public DatasetScanBuilder WithSummaries(bool summaries)
    {
        _summaries = summaries;
        return this;
    }

    /// <summary>Records what the walk skipped.</summary>
    /// <param name="metrics">The counters to fill.</param>
    /// <returns>This builder.</returns>
    public DatasetScanBuilder WithMetrics(DatasetScanMetrics metrics)
    {
        _metrics = metrics;
        return this;
    }

    /// <summary>The batches of every object the scan did not skip, in key order.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_orderPath is { } path)
        {
            await foreach (RecordBatch run in MergedAsync(path, cancellationToken).ConfigureAwait(false))
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

    /// <summary>The rows the scan selects, without materialising them.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectLease lease = await _dataset.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await Of(lease.File, held).CountAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>Whether the scan would return at least one row (12 §5.1, across objects).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether some row matches.</returns>
    /// <remarks>
    /// The summaries answer for the objects they refute without opening them, and the first object
    /// that holds a row answers for the dataset: a membership probe over a dataset reads, at worst,
    /// what it would read over the one object that has the answer.
    /// </remarks>
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

    /// <summary>The smallest non-null value of a column among the rows the scan would return.</summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The minimum, or <see cref="FilterLiteral.Null"/> when there is none.</returns>
    public ValueTask<FilterLiteral> MinAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>The largest non-null value of a column among the rows the scan would return.</summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The maximum, or <see cref="FilterLiteral.Null"/> when there is none.</returns>
    public ValueTask<FilterLiteral> MaxAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: false, cancellationToken);

    /// <summary>
    /// The extreme over the objects, each one skipped when its own summary cannot beat the best so
    /// far — §6.6's third answer, "<c>ORDER BY x LIMIT k</c> prunes by the summaries", at k = 1.
    /// </summary>
    /// <param name="path">The column.</param>
    /// <param name="wantMin">Whether the smallest is wanted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// AN INEXACT BOUND IS STILL A BOUND, and that is what makes this legal: the summary's
    /// <c>min</c> is at or below the object's true minimum, so a summary min that already loses to
    /// the best cannot hide a winner behind it. The same holds the other way for <c>max</c>. Nothing
    /// here needs the bounds to be exact, and nothing here may use the equality shortcut that would.
    /// </remarks>
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
    /// What the scan would read, and what §5.2's invariant costs it, without reading a data object.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads of the tree.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// §5.1 asks for exactly one thing here: "`Explain` reports every violation of it as a lag,
    /// with the count". A dataset whose level 0 has outgrown its ceiling still answers every
    /// question correctly and answers them by touching more objects, and the only honest way to
    /// surface that is a number a caller can read — not a refusal, because compaction is the user's
    /// background job (§5.3).
    /// </remarks>
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
            _orderPath = _orderPath,
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
            _orderPath,
            CursorBound(objects, keptByLevel));
    }

    /// <summary>The most objects the scan will hold open at once, stated before any is read.</summary>
    /// <param name="objects">The objects it will read.</param>
    /// <param name="keptByLevel">How many of them each level holds.</param>
    /// <remarks>
    /// One in the tree's order, where objects are read one after another. Key-ordered on the
    /// clustering key, level 0's objects and one per level above it — §6.6's "≤ 8 + L cursors",
    /// which §5.2's invariant bounds and `Lag` reports the excess of. On any other column, every
    /// object: the summaries give no disjointness to lean on.
    /// </remarks>
    private long CursorBound(long objects, List<long> keptByLevel)
    {
        if (objects == 0)
        {
            return 0;
        }

        if (_orderPath is not { } path)
        {
            return 1;
        }

        if (!_summaries || !RidesDisjointLevels(path))
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
    /// Whether a merge on <paramref name="path"/> reads the key-disjoint objects of a level one after
    /// another: on the clustering key, upward by the tree's exact minima, downward by summary maxima
    /// that the key order respects.
    /// </summary>
    private bool RidesDisjointLevels(string path) =>
        IsClusteringKey(path)
        && (!_descending || HasKeyOrderBounds(ClusteringKey.Resolve(_dataset.Schema, path)));

    /// <summary>The objects this scan will read, in key order, each with its first row.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The objects.</returns>
    /// <remarks>
    /// What `Explain` will report and what a test asserts against: the walk with its two skips
    /// applied, and not one byte of any data object read.
    /// </remarks>
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

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged (§6.6).</summary>
    /// <param name="path">The key column.</param>
    /// <param name="cancellationToken">Cancels the walk, the opens and the reads.</param>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        string[] selected = _projection ?? [];
        bool drop = _projection is not null && !Covers(selected, path);
        Projection? kept = null;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _dataset,
            MergeObjectsAsync(path, cancellationToken),
            [path],
            _descending,
            rankedTies: true,
            file => OrderedOf(file, path, drop),
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
    /// its ties go by.
    /// </summary>
    /// <param name="path">The key column.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <remarks>
    /// ON THE CLUSTERING KEY, UPWARD, THE TREE ALREADY IS THAT ORDER: a leaf key is the encoded
    /// minimum of the object, exact (§4.1), and the walk hands the objects over as it reads them —
    /// so a consumer that stops early stops the walk too, and a subtree past the k-th key is never
    /// read. Anywhere else the kept objects are collected and sorted by their summaries' bound: the
    /// leaf pages are read, the objects are not opened.
    /// </remarks>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_summaries || (!_descending && IsClusteringKey(path)))
        {
            // Without summaries there is no bound to lean on, and every object is opened before a
            // row goes out: the eager merge, which is what a lazy one must answer the same as.
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
            ReadOnlyMemory<byte> bound = SummaryBound(held.Entry.Summaries, path, dtype, field, _descending);
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

    /// <summary>Whether <paramref name="path"/> is the dataset's whole clustering key.</summary>
    private bool IsClusteringKey(string path) =>
        _dataset.Key is { IsComposite: false } key && string.Equals(key.Paths[0], path, StringComparison.Ordinal);

    /// <summary>
    /// A summary's bound on a column, encoded as the merge compares keys: its minimum upward, its
    /// maximum downward. Empty when the summary gives none that the key order respects.
    /// </summary>
    /// <remarks>
    /// ONLY WHERE THE ZONE STATISTICS AND THE KEY ORDER AGREE: integers, booleans, strings and
    /// bytes — a truncated string bound stays a bound, its maximum being rounded up (step 19). A
    /// FLOAT HAS NONE: its zone min and max exclude NaN (08 §2), while the key order puts a negative
    /// NaN first and a positive one last (12 §4.4), so an object holding one has a key outside its
    /// own summary. Such objects are opened before the merge emits anything, which is correct and is
    /// the output-sensitive cost §6.6 states. A decimal's bound is not relied on either.
    /// </remarks>
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

    /// <summary>Whether a projection already reads <paramref name="path"/>, itself or through an ancestor.</summary>
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
    /// <param name="file">One data object.</param>
    /// <param name="path">The key column.</param>
    /// <param name="withKey">Whether the key must be read on top of the projection.</param>
    private ScanBuilder OrderedOf(VortexFile file, string path, bool withKey)
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
                scan = scan.Project(path);
            }
        }

        scan = scan.InKeyOrder(path, _descending);
        return _indexes ? scan : scan.WithIndexes(false);
    }

    /// <summary>The file's own scan, carrying this builder's filter, projection and row range.</summary>
    /// <param name="file">One data object.</param>
    /// <param name="held">Where its rows sit in the dataset.</param>
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
    /// <summary>The level whose tree holds it (§5.2); level 0 is the one an append lands in.</summary>
    public int Level { get; init; }

    /// <summary>Its key in that tree: what orders it, then its uid (§4.1).</summary>
    internal ReadOnlyMemory<byte> TreeKey { get; init; }
}

/// <summary>What a dataset scan would read, and what it costs (§5.1).</summary>
/// <param name="Version">The version it would read.</param>
/// <param name="ObjectsByLevel">How many objects each level holds, level 0 first (§5.2).</param>
/// <param name="Lag">
/// The objects level 0 holds above its ceiling: zero when §5.2's invariant holds, and otherwise the
/// number of extra objects every key lookup has to touch until compaction catches up.
/// </param>
/// <param name="IsClustered">
/// Whether a clustering key is declared. Without one, levels are size tiers and a lookup touches
/// every object the summaries cannot refute: "output-sensitive, not bounded, and the dataset says
/// so in `Explain`" (§5.2).
/// </param>
/// <param name="Objects">The objects the scan would open.</param>
/// <param name="Rows">Their rows, before the filter.</param>
/// <param name="ObjectsSkipped">Objects whose own summaries refuted the predicate.</param>
/// <param name="SubtreesSkipped">Child pages a node's summaries refuted, unread (§4.2).</param>
/// <param name="Order">The column a key-ordered scan delivers its rows by, or null for the tree's order.</param>
/// <param name="Cursors">
/// The most objects the scan will hold open at once: one in the tree's order; key-ordered on the
/// clustering key, level 0's objects and one per level above it (§6.6: "≤ 8 + L cursors"); on any
/// other column, every object it would open.
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
