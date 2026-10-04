using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What a dataset scan skipped, and how: counters rather than timings, so that a caller can check
/// how few objects were read instead of believing a speed.
/// </summary>
internal sealed class DatasetScanMetrics
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

    /// <summary>The objects an answer took from their entries, without opening them.</summary>
    public long ObjectsCounted { get; internal set; }

    /// <summary>
    /// The most data objects the scan read at once: one in the tree's order, and under
    /// <see cref="DatasetScanBuilder.InKeyOrder(string, bool)"/> the inputs the merge held open.
    /// </summary>
    public int Cursors { get; internal set; }
}

/// <summary>
/// Builds a scan over every object of one version of a dataset: the engine a
/// <see cref="DatasetScanSource"/> compiles the core's scans to. Each object is read by the core's
/// own file scan, carrying the filter, the projection, the rows and the options, and its batches
/// are counted from the object's first row among the dataset's.
/// </summary>
internal sealed class DatasetScanBuilder
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;
    private VortexExpr? _filter;
    private FieldMask? _mask;
    private bool _indexes = true;
    private bool _summaries = true;
    private long _from;
    private long _to = long.MaxValue;
    private bool _rowsSet;
    private long[]? _take;
    private string[]? _orderPaths;
    private bool _descending;
    private DatasetScanMetrics? _metrics;
    private ScanMetrics? _counters;
    private ScanOptions _options = ScanOptions.Default;
    private bool _keepEncodings;
    private bool _sinkDecodes;
    private bool _positionsUnread;

    internal DatasetScanBuilder(VortexDataset dataset, DatasetSnapshot version)
    {
        _dataset = dataset;
        _version = version;
    }

    /// <summary>Keeps the rows the predicate selects.</summary>
    public DatasetScanBuilder Where(VortexExpr filter)
    {
        _filter = filter;
        return this;
    }

    /// <summary>Reads only the columns of <paramref name="mask"/>, which is resolved against the dataset's schema and so fits every object.</summary>
    public DatasetScanBuilder Project(in FieldMask mask)
    {
        _mask = mask;
        return this;
    }

    /// <summary>
    /// Reads only the dataset's rows <c>[from, to)</c>, in the tree's order. Answered through the
    /// tree's row sums, so an object outside the range is neither opened nor counted and a subtree
    /// outside it is never read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan is key-ordered, or selects rows by index.</exception>
    public DatasetScanBuilder Rows(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        if (_orderPaths is not null || _take is not null)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range, by index or by key order, one of them at a time.");
        }

        _from = from;
        _to = to;
        _rowsSet = true;
        return this;
    }

    /// <summary>
    /// Reads only the dataset's rows at <paramref name="rows"/>, sorted and deduplicated. An object
    /// that holds none of them is neither opened nor counted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A row is negative, or past the version's rows.</exception>
    /// <exception cref="InvalidOperationException">The scan is key-ordered, or selects a range.</exception>
    public DatasetScanBuilder Take(ReadOnlySpan<long> rows)
    {
        if (_orderPaths is not null || _rowsSet)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range, by index or by key order, one of them at a time.");
        }

        long[] sorted = rows.ToArray();
        Array.Sort(sorted);
        int distinct = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i == 0 || sorted[i] != sorted[distinct - 1])
            {
                sorted[distinct++] = sorted[i];
            }
        }

        if (distinct > 0 && (sorted[0] < 0 || sorted[distinct - 1] >= _version.RowCount))
        {
            long wrong = sorted[0] < 0 ? sorted[0] : sorted[distinct - 1];
            throw new ArgumentOutOfRangeException(
                nameof(rows), wrong, $"Version {_version.Version} of the dataset holds {_version.RowCount} rows.");
        }

        _take = sorted.AsSpan(0, distinct).ToArray();
        _from = distinct == 0 ? 0 : _take[0];
        _to = distinct == 0 ? 0 : _take[^1] + 1;
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
    /// the merge reaches it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The scan selects rows by range or by index.</exception>
    public DatasetScanBuilder InKeyOrder(string path, bool descending = false) => InKeyOrder([path], descending);

    /// <summary>
    /// Delivers the rows in the order of a composite key, the tuple of the given columns in key
    /// order. A row whose tuple holds a null is in no run and is not delivered.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan selects rows by range or by index.</exception>
    public DatasetScanBuilder InKeyOrder(IReadOnlyList<string> paths, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("A key has at least one column.", nameof(paths));
        }

        foreach (string path in paths)
        {
            _ = ClusteringKey.Resolve(_version.Schema.DType, path);
        }

        if (_rowsSet || _take is not null)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks the objects' key sources; it cannot also select rows by range or by index.");
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

    /// <summary>Adds what every object's own scan requests, decodes and delivers to <paramref name="counters"/>.</summary>
    public DatasetScanBuilder WithCounters(ScanMetrics counters)
    {
        _counters = counters;
        return this;
    }

    /// <summary>
    /// Runs every object's scan under <paramref name="options"/>: the batch cap, zone-map pruning,
    /// the degree of parallelism, the prefetch and compaction; <paramref name="keepEncodings"/> lets
    /// the decoders deliver dictionary and run-end columns encoded, <paramref name="sinkDecodes"/>
    /// says the consumer reads those forms itself, so that only the blocks it decodes count as decoded,
    /// and <paramref name="positionsUnread"/> that it reads no row's place, so that the rows an object
    /// deleted may leave its batches, when they are not compacted, by their selection alone.
    /// </summary>
    public DatasetScanBuilder WithOptions(ScanOptions options, bool keepEncodings, bool sinkDecodes = false, bool positionsUnread = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keepEncodings = keepEncodings;
        _sinkDecodes = sinkDecodes;
        _positionsUnread = positionsUnread;
        return this;
    }

    /// <summary>The batches of every object the scan did not skip, in key order, borrowed: each is valid until the next is asked for.</summary>
    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_orderPaths is { } paths)
        {
            await foreach (RecordBatch run in MergedAsync(paths, cancellationToken).ConfigureAwait(false))
            {
                yield return run;
            }

            yield break;
        }

        RecordBatch? view = null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                ObjectColumns? columns = ColumnsOf(lease, held.Entry);
                if (columns is null && !held.Entry.HasDeletions)
                {
                    // The object's own scan, which every object of the version's schema with no row
                    // marked is read by, rebased into one view for the whole scan.
                    await foreach (RecordBatch batch in Of(lease.File, held).ExecuteAsync()
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        yield return InDataset(batch, held.FirstRow, ref view);
                    }

                    continue;
                }

                if (BatchesOfAsync(lease.File, held, PartOf(columns, _mask), held.FirstRow, _options.Compact, cancellationToken) is { } batches)
                {
                    await foreach (RecordBatch batch in batches.ConfigureAwait(false))
                    {
                        yield return batch;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The rows the scan selects, without materialising them, cheapest proof first: an object whose
    /// covered rows are all kept is counted from its entry without being opened, which is every
    /// object when there is no filter and, under a range on the clustering key, an object whose
    /// summary bounds lie wholly inside it and whose key holds no null; any other object is counted
    /// by its own file's count, from statistics, zone maps and indexes before any decode.
    /// </summary>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                rows += covered;
                RecordCounted();
                continue;
            }

            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether the scan would return at least one row; the first object that holds one answers for
    /// the dataset, and one whose entry settles it answers without being opened.
    /// </summary>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                RecordCounted();
                return true;
            }

            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                if (await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: true, cancellationToken)
                    .ConfigureAwait(false) > 0)
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
    /// What the scan would read, without reading a data object. A level 0 that has outgrown its
    /// ceiling is reported as a lag rather than refused, since compaction is the caller's own
    /// background job and the answers stay correct meanwhile.
    /// </summary>
    public async ValueTask<DatasetPlan> ExplainAsync(CancellationToken cancellationToken = default)
    {
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        long objects = 0;
        long rows = 0;
        List<long> keptByLevel = [];
        await foreach (PositionedObject held in _version
            .WalkAsync(Pruner(), _from, _to, metrics, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            objects++;
            rows += held.Entry.Rows;
            while (keptByLevel.Count <= held.Level)
            {
                keptByLevel.Add(0);
            }

            keptByLevel[held.Level]++;
        }

        DatasetLevels levels = _version.Levels;
        long[] byLevel = new long[Math.Max(levels.Count, 1)];
        for (int level = 0; level < byLevel.Length; level++)
        {
            byLevel[level] = levels[level].Entries;
        }

        return new DatasetPlan(
            _version.Version,
            byLevel,
            levels.LagAtLevelZero(),
            _version.Header.ClusteringKey.Count > 0,
            objects,
            rows,
            metrics.ObjectsSkipped,
            metrics.SubtreesSkipped,
            _orderPaths is null ? null : _orderPaths.Length == 1 ? _orderPaths[0] : "(" + string.Join(", ", _orderPaths) + ")",
            CursorBound(objects, keptByLevel));
    }

    /// <summary>
    /// What the scan will do as the core's plan, summed over the objects it would open: each one's
    /// own plan, worked out from its statistics, zone maps and indexes, and before them the rows the
    /// summaries refute as one step. No data segment is read, but every kept object is opened.
    /// </summary>
    /// <remarks>
    /// An object the summaries refute is never opened, so its blocks are counted at the block size
    /// of the objects that were, or at the dataset's own when none was.
    /// </remarks>
    public async ValueTask<ScanPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        long keptRows = 0;
        long blockRows = 0;
        long blocks = 0;
        long live = 0;
        long segments = 0;
        long bytes = 0;
        bool mayMatch = false;
        List<PruningStep> steps = [];
        Dictionary<string, int> stepAt = new Dictionary<string, int>(StringComparer.Ordinal);
        bool exact = true;
        long exactRows = 0;
        long pruned = 0;
        long proven = 0;
        long decoded = 0;
        List<string> sources = [];
        long runs = 0;
        long entries = 0;
        KeyRange? range = CountRange();
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0)
            {
                continue;
            }

            ScanExplanation plan;
            bool evaluated = false;
            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);

                // An object is planned as its own scan reads it: the filter it takes, or none when the
                // filter is evaluated on its reshaped batches.
                ObjectPart part = PartOf(ColumnsOf(lease, held.Entry), _mask);
                if (part.Fate == FilterFate.None)
                {
                    continue;
                }

                evaluated = part.Fate == FilterFate.Reshaped;
                ScanBuilder scan = _orderPaths is { } paths && part.Mapped is null
                    ? OrderedOf(lease.File, paths, withKey: false)
                    : ScanOf(lease.File, held, part);
                plan = await scan.ExplainAsync(cancellationToken).ConfigureAwait(false);
            }

            keptRows += covered;

            blockRows = blockRows == 0 ? plan.BlockRows : blockRows;
            blocks += plan.Blocks;
            live += plan.LiveBlocks;
            segments += plan.SegmentsToRead;
            bytes += plan.BytesToRead;
            mayMatch |= plan.FileMayMatch;
            foreach (PruningStep step in plan.Pruning)
            {
                if (stepAt.TryGetValue(step.Structure, out int at))
                {
                    PruningStep sum = steps[at];
                    steps[at] = sum with
                    {
                        BlocksPruned = sum.BlocksPruned + step.BlocksPruned,
                        SegmentsRead = sum.SegmentsRead + step.SegmentsRead,
                        BytesRead = sum.BytesRead + step.BytesRead,
                    };
                }
                else
                {
                    stepAt[step.Structure] = steps.Count;
                    steps.Add(step);
                }
            }

            if (evaluated || (held.Entry.HasDeletions && !Settled(held, range)))
            {
                // Its rows are counted by evaluating the filter on them, or its own plan counts rows
                // that are deleted: no plan settles the count.
                exact = false;
            }
            else if (Settled(held, range) || plan.Count is null)
            {
                exactRows += covered;
            }
            else
            {
                CountExplanation count = plan.Count;
                exact &= count.ExactCover;
                exactRows += count.ExactCount;
                pruned += count.SplitsPruned;
                proven += count.SplitsProven;
                decoded += count.SplitsDecoded;
            }

            if (plan.Order is { } order)
            {
                string source = order.Source.ToString();
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                }

                runs += order.RunsInRange;
                entries += order.EntriesInRange;
            }
        }

        long coveredRows = _take is { } take ? take.Length : Math.Max(Math.Min(_to, _version.RowCount) - _from, 0);
        long refuted = Math.Max(coveredRows - keptRows, 0);
        long perBlock = blockRows > 0 ? blockRows : Math.Max(_dataset.Options.Write.BlockRows, 1);
        long refutedBlocks = (refuted + perBlock - 1) / perBlock;
        List<PruningStep> pruning = new List<PruningStep>(steps.Count + 1);
        if (refutedBlocks > 0)
        {
            pruning.Add(new PruningStep("object summaries", Narrow(refutedBlocks), 0, 0));
        }

        pruning.AddRange(steps);
        return new ScanPlan(
            coveredRows,
            Narrow(blocks + refutedBlocks),
            Narrow(live),
            Narrow(segments),
            bytes,
            mayMatch,
            [.. pruning],
            new CountPlan(exact, exactRows, Narrow(pruned), Narrow(proven), Narrow(decoded)),
            _orderPaths is null
                ? null
                : new OrderPlan(sources.Count == 0 ? nameof(KeySourceKind.None) : string.Join('+', sources), Narrow(runs), entries, _descending));
    }

    /// <summary>
    /// The objects this scan will read, in key order, each with its first row: the walk with its
    /// skips applied, and not one byte of any data object read.
    /// </summary>
    public IAsyncEnumerable<PositionedObject> ObjectsAsync(CancellationToken cancellationToken = default) =>
        WalkAsync(cancellationToken);

    /// <summary>
    /// The extreme over the objects, each one skipped when its own summary cannot beat the best so
    /// far, and taken from its summary without being opened when that summary is exact and covers
    /// every row the scan keeps in it. A summary's min is at or below the object's true minimum, so
    /// a summary min that already loses cannot hide a winner behind it.
    /// </summary>
    private async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        FieldMask projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(_version.Schema.Columns, path)).Build();
        FilterLiteral best = FilterLiteral.Null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (_summaries && Loses(held.Entry.Summaries, path, best, wantMin))
            {
                if (_metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            if (_summaries && _filter is null && Whole(held) && Exact(held.Entry.Summaries, path, wantMin, out FilterLiteral bound))
            {
                if (Beats(bound, best, wantMin))
                {
                    best = bound;
                }

                RecordCounted();
                continue;
            }

            ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                FilterLiteral candidate = await ObjectExtremeAsync(lease.File, held, ColumnsOf(lease, held.Entry), path, projection, wantMin, cancellationToken)
                    .ConfigureAwait(false);
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

    /// <summary>The true extreme an object's summary records, when it records one.</summary>
    private static bool Exact(ObjectSummaries summaries, string path, bool wantMin, out FilterLiteral bound)
    {
        bound = FilterLiteral.Null;
        if (!summaries.TryGet(path, out ColumnSummary column) || !column.IsExact)
        {
            return false;
        }

        bound = wantMin ? column.Min : column.Max;
        return (wantMin ? column.HasMin : column.HasMax) && bound.Kind != FilterLiteralKind.Null;
    }

    private static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin) =>
        best.Kind == FilterLiteralKind.Null
        || !Comparable(candidate, best)
        || (wantMin ? KeyCursor.Compare(candidate, best) < 0 : KeyCursor.Compare(candidate, best) > 0);

    /// <summary>Whether two literals are of one comparison kind, so that an order holds between them.</summary>
    private static bool Comparable(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind != FilterLiteralKind.Null;

    /// <summary>The range on the clustering key the entries can count the filter by, or null.</summary>
    private KeyRange? CountRange() => _filter is not null && _summaries ? KeyRange.Of(_dataset, _filter) : null;

    /// <summary>
    /// Whether every row the scan covers in an object is kept, known without opening it: there is
    /// no filter, or the object lies wholly inside the scan and inside a range on the clustering key.
    /// </summary>
    private bool Settled(in PositionedObject held, KeyRange? range) =>
        _filter is null || (range is { } key && Whole(held) && key.Holds(held.Entry.Summaries));

    /// <summary>Whether the scan covers every row of an object.</summary>
    private bool Whole(in PositionedObject held) =>
        _take is null && _from <= held.FirstRow && held.FirstRow + held.Entry.Rows <= _to;

    /// <summary>The object's rows the scan covers: those it takes by index, or its part of the range.</summary>
    private long Covered(in PositionedObject held)
    {
        if (_take is not null)
        {
            return TakenBy(held).Count;
        }

        long lower = Math.Max(_from, held.FirstRow);
        long upper = Math.Min(_to, held.FirstRow + held.Entry.Rows);
        return Math.Max(upper - lower, 0);
    }

    /// <summary>Where the object's rows begin and how many there are among the rows the scan takes.</summary>
    private (int First, int Count) TakenBy(in PositionedObject held)
    {
        long[] take = _take!;
        int first = LowerBound(take, held.FirstRow);
        int end = LowerBound(take, held.FirstRow + held.Entry.Rows);
        return (first, end - first);
    }

    private static int LowerBound(long[] sorted, long value)
    {
        int found = Array.BinarySearch(sorted, value);
        return found >= 0 ? found : ~found;
    }

    private static int Narrow(long value) => (int)Math.Min(value, int.MaxValue);

    /// <summary>
    /// A file's batch seen as the dataset's rows: row 0 is the object's row <c>StartRow</c>, after
    /// the rows before the object. One view serves the whole stream, bound again per batch, since a
    /// batch is valid until the next one is asked for and a view per batch would be an allocation
    /// per batch.
    /// </summary>
    /// <param name="batch">The file's batch.</param>
    /// <param name="firstRow">The dataset row the object's row 0 is.</param>
    /// <param name="view">The stream's view, made at the first batch that needs one.</param>
    private static RecordBatch InDataset(RecordBatch batch, long firstRow, ref RecordBatch? view)
    {
        if (firstRow == 0)
        {
            return batch;
        }

        if (view is null)
        {
            view = batch.Rebased(firstRow + batch.StartRow);
            return view;
        }

        view.Dispose();
        view.RebindRebased(batch, firstRow + batch.StartRow);
        return view;
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
        && (!_descending || (paths.Length == 1 && HasKeyOrderBounds(ClusteringKey.Resolve(_version.Schema.DType, paths[0]))));

    private SummaryPruner? Pruner() => _summaries && _filter is { } filter ? new SummaryPruner(filter) : null;

    private IAsyncEnumerable<PositionedObject> WalkAsync(CancellationToken cancellationToken) =>
        _version.WalkAsync(Pruner(), _from, _to, _metrics, cancellationToken);

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

    private void RecordCounted()
    {
        if (_metrics is { } metrics)
        {
            metrics.ObjectsCounted++;
        }
    }

    /// <summary>The key-ordered read: every kept object's own key-ordered scan, merged.</summary>
    private async IAsyncEnumerable<RecordBatch> MergedAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The key is read whatever the projection says, and dropped before a batch goes out.
        FieldMask mask = _mask ?? FieldMask.All;
        bool drop = !mask.IsAll && !Array.TrueForAll(paths, path => Covers(mask, _version.Schema.DType, path));
        ScanProjection? kept = null;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _version,
            MergeObjectsAsync(paths, cancellationToken),
            paths,
            _descending,
            rankedTies: true,
            (lease, entry) => OrderedBatchesAsync(lease, entry, paths, drop, cancellationToken),
            RecordOpen,
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
    /// its ties go by. On the clustering key upward the tree already is that order, and the walk is
    /// lazy, so a consumer that stops early stops the walk; anywhere else the kept objects are
    /// collected and sorted by their summaries' bound, without being opened. A composite key is
    /// bounded upward by its first column, whose encoded minimum prefixes every tuple above it, and
    /// not downward.
    /// </summary>
    private async IAsyncEnumerable<MergeObject> MergeObjectsAsync(
        string[] paths, [EnumeratorCancellation] CancellationToken cancellationToken)
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
                yield return new MergeObject(held.Entry, bound, _descending ? -ordinal : ordinal, held.FirstRow);
                ordinal++;
            }

            yield break;
        }

        DType dtype = ClusteringKey.Resolve(_version.Schema.DType, path);
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

    /// <summary>Whether the columns are the dataset's whole clustering key, in order.</summary>
    private bool IsClusteringKey(string[] paths) =>
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

    /// <summary>Whether a column's summary bounds are bounds in the key order (see <see cref="SummaryBound"/>).</summary>
    private static bool HasKeyOrderBounds(DType dtype) => dtype.Kind switch
    {
        DTypeKind.Primitive => !dtype.PType.IsFloat(),
        DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary => true,
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
        if (ColumnsOf(lease, entry) is not { } columns)
        {
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

        ObjectRead read = columns.Read(_filter, projection);
        string[]? source = columns.SourcePaths(paths);
        if (read.Fate == FilterFate.None || (source is null && paths.Length > 1))
        {
            return System.Linq.AsyncEnumerable.Empty<RecordBatch>();
        }

        ScanBuilder scan = Configure(lease.File.ScanBuilder(), read.Pushed, read.Shape.Source, canonical: true).WithCompaction(true);
        IAsyncEnumerable<RecordBatch> rows;
        if (source is not null)
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
    /// How an object answers for the version's columns: null when it holds exactly them, the case of
    /// every object written since the schema last changed, whose own scan then serves unchanged.
    /// </summary>
    private ObjectColumns? ColumnsOf(ObjectLease lease, ObjectEntry entry) => _version.Schema.ColumnsOf(lease.File.DType, entry.Key);

    /// <summary>
    /// An object's part of this read of the columns <paramref name="mask"/> names: what the filter is
    /// for it, from <paramref name="columns"/> for an object of an earlier schema, and for one of the
    /// version's own the filter whole, which its scan takes as it is. What an object was written under
    /// is settled here, once, and every terminal goes by the filter's fate alone.
    /// </summary>
    private ObjectPart PartOf(ObjectColumns? columns, FieldMask? mask)
    {
        if (columns is null)
        {
            return new ObjectPart(_filter is null ? FilterFate.Every : FilterFate.Pushed, _filter, mask, null);
        }

        ObjectRead read = columns.Read(_filter, mask ?? FieldMask.All);
        return new ObjectPart(read.Fate, read.Pushed, read.Shape.Source, read);
    }

    /// <summary>The object's own scan for its part of the read: the filter it takes and the fields it reads, decoded whole when its batches are to be reshaped.</summary>
    private ScanBuilder ScanOf(VortexFile file, PositionedObject held, in ObjectPart part) =>
        Of(file, held, part.Pushed, part.Source, canonical: part.Mapped is not null);

    /// <summary>
    /// The batches of an object that its own scan alone does not read as the version's rows, one of an
    /// earlier schema or one with rows marked, counted from <paramref name="baseRow"/>; null when the
    /// filter can keep no row of it. The one place where the steps compose, in this order whatever
    /// reads the batches: the object's scan, its marked rows taken out or deselected, the batches
    /// reshaped to the version's columns, and the filter evaluated on them where it could not go to
    /// the scan.
    /// </summary>
    private IAsyncEnumerable<RecordBatch>? BatchesOfAsync(
        VortexFile file, PositionedObject held, in ObjectPart part, long baseRow, bool compact, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.None)
        {
            return null;
        }

        return part.Mapped is { } read
            ? ReshapedAsync(file, held, read, baseRow, compact, cancellationToken)
            : LiveAsync(file, held, part.Pushed, part.Source, baseRow, compact, reshaped: false, cancellationToken);
    }

    /// <summary>An object of another schema read as the version's: its own scan, each batch reshaped and counted from <paramref name="baseRow"/>.</summary>
    private IAsyncEnumerable<RecordBatch> ReshapedAsync(
        VortexFile file, PositionedObject held, ObjectRead read, long baseRow, bool compact, CancellationToken cancellationToken) =>
        ObjectColumns.ReshapeAsync(
            held.Entry.HasDeletions
                ? LiveAsync(file, held, read.Pushed, read.Shape.Source, 0, compact && read.Evaluated is null, reshaped: true, cancellationToken)
                : Of(file, held, read.Pushed, read.Shape.Source, canonical: true).ExecuteAsync(),
            read.Shape,
            read.Evaluated,
            baseRow,
            compact,
            cancellationToken);

    /// <summary>
    /// The rows of an object its part of the read keeps; with <paramref name="stopAtOne"/>, 1 at the
    /// first. None when the filter keeps none, every row the scan covers when it keeps all, the
    /// object's own count when the filter goes to its scan, which leaves its marked rows out in the
    /// same pass, and the rows selected on its reshaped batches otherwise.
    /// </summary>
    private async ValueTask<long> CountOfAsync(
        VortexFile file, PositionedObject held, ObjectPart part, bool stopAtOne, CancellationToken cancellationToken)
    {
        switch (part.Fate)
        {
            case FilterFate.None:
                return 0;
            case FilterFate.Every:
                return stopAtOne ? Math.Min(Covered(held), 1) : Covered(held);
            case FilterFate.Pushed:
                return stopAtOne
                    ? await LiveAnyAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false) ? 1 : 0
                    : await LiveCountAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false);
            default:
                long rows = 0;
                await foreach (RecordBatch batch in BatchesOfAsync(file, held, part, 0, compact: false, cancellationToken)!.ConfigureAwait(false))
                {
                    rows += batch.SelectedRows;
                    if (stopAtOne && rows > 0)
                    {
                        break;
                    }
                }

                return rows;
        }
    }

    /// <summary>
    /// The extreme of a column among the rows an object keeps: none when it lacks the column; the
    /// object's own extreme, under the column's name there, when the filter goes to its scan and no
    /// row is marked; its live extreme when rows are; and the extreme of its reshaped batches when
    /// the filter is evaluated on them.
    /// </summary>
    private async ValueTask<FilterLiteral> ObjectExtremeAsync(
        VortexFile file, PositionedObject held, ObjectColumns? columns, string path, FieldMask projection, bool wantMin, CancellationToken cancellationToken)
    {
        string? named = columns is null ? path : columns.SourcePath(path);
        ObjectPart part = PartOf(columns, projection);
        if (named is null || part.Fate == FilterFate.None)
        {
            return FilterLiteral.Null;
        }

        if (part.Fate == FilterFate.Reshaped)
        {
            return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
        }

        if (held.Entry.HasDeletions)
        {
            return await LiveExtremeAsync(file, held, part, named, path, wantMin, cancellationToken).ConfigureAwait(false);
        }

        ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
        return wantMin
            ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
            : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file's own scan, carrying this builder's filter, projection, options and the rows that fall in this object.</summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held) => Of(file, held, _filter, _mask, canonical: false);

    /// <summary>
    /// An object's part of a read, whatever schema it was written under: what the filter is for it,
    /// the filter its own scan takes, the fields that scan reads, and how its batches become the
    /// version's rows; none for an object of the version's own columns, whose batches already are.
    /// </summary>
    private readonly record struct ObjectPart(FilterFate Fate, VortexExpr? Pushed, FieldMask? Source, ObjectRead? Mapped);

    /// <summary>
    /// The file's own scan under <paramref name="filter"/> and <paramref name="mask"/>, with this
    /// builder's options and the rows that fall in this object; with <paramref name="canonical"/>,
    /// every column decoded, which the reshaping of another schema's batches takes.
    /// </summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, bool canonical)
    {
        ScanBuilder scan = Configure(file.ScanBuilder(), filter, mask, canonical);
        DeletionVector deletions = held.Entry.Deletions;
        if (_take is { } take)
        {
            // A row taken by its place among the dataset's live rows is at that place among the
            // object's live rows, which the deleted rows before it push further on.
            (int first, int count) = TakenBy(held);
            long[] local = new long[count];
            for (int i = 0; i < count; i++)
            {
                local[i] = deletions.Physical(take[first + i] - held.FirstRow);
            }

            return scan.Take(local);
        }

        // The range is the dataset's; the file's is the part of it that falls inside this object.
        (long lower, long upper) = PhysicalRange(held);
        if (lower > 0 || upper < held.Entry.PhysicalRows)
        {
            scan.Rows(new RowRange(lower, Math.Max(upper, lower)));
        }

        return scan;
    }

    /// <summary>
    /// The part of the scan's range inside an object, at the object's own positions: the range
    /// counts live rows, and the deleted rows before and inside it push it further on.
    /// </summary>
    private (long Lower, long Upper) PhysicalRange(in PositionedObject held)
    {
        long lower = Math.Max(_from - held.FirstRow, 0);
        long upper = _to == long.MaxValue ? held.Entry.Rows : Math.Min(_to - held.FirstRow, held.Entry.Rows);
        DeletionVector deletions = held.Entry.Deletions;
        if (deletions.IsEmpty)
        {
            return (lower, upper);
        }

        long first = deletions.Physical(lower);
        return (first, upper <= lower ? first : deletions.Physical(upper - 1) + 1);
    }

    /// <summary>
    /// An object's rows under <paramref name="filter"/> and <paramref name="mask"/> without the
    /// ones it deleted: its own scan run without compaction, so that the deleted rows can be found
    /// at their places, then taken out, which gathers decoded columns, and the rest numbered from
    /// <paramref name="baseRow"/>. Batches not compacted that nobody reads a place of keep their rows
    /// instead, the deleted ones deselected, and their columns encoded as an object without deletions
    /// delivers them; <paramref name="reshaped"/> decodes every column all the same, for the
    /// reshaping of another schema's batches.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> LiveAsync(
        VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, long baseRow, bool compact, bool reshaped, CancellationToken cancellationToken)
    {
        bool deselect = !compact && _positionsUnread;
        IAsyncEnumerable<RecordBatch> rows = Of(file, held, filter, mask, canonical: reshaped || !deselect).WithCompaction(false).ExecuteAsync();
        return deselect
            ? LiveRows.DeselectedAsync(rows, held.Entry.Deletions, baseRow, cancellationToken)
            : LiveRows.WithoutDeletedAsync(rows, held.Entry.Deletions, compact, baseRow, cancellationToken);
    }

    /// <summary>
    /// The rows of an object <paramref name="filter"/> keeps, the deleted ones not counted: its own
    /// count with them left out, cheapest proof first, in one pass. A take of the dataset's rows
    /// names live rows only.
    /// </summary>
    private async ValueTask<long> LiveCountAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
        return _take is null && held.Entry.HasDeletions
            ? await scan.CountExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="filter"/> keeps a live row of an object, the first one found answering.</summary>
    private async ValueTask<bool> LiveAnyAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
        return _take is null && held.Entry.HasDeletions
            ? await scan.AnyExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The extreme of a column among an object's live rows, under the filter its part of the read
    /// gives its scan, the column <paramref name="named"/> as the object names it. A column the
    /// object's statistics say is sorted and holds no null nor NaN has it at its first or its last
    /// live row, when every row is kept: the object's own extreme while that row is the column's end,
    /// and that row's value, one row read, once a delete took the end. Otherwise the object's own
    /// extreme is the answer when a live row holds it, which a count of the rows equal to it settles;
    /// when every row holding it is gone, or the column is a float whose equality a count cannot
    /// settle, its live rows are read for it, as the version's columns, under <paramref name="path"/>.
    /// </summary>
    private async ValueTask<FilterLiteral> LiveExtremeAsync(
        VortexFile file, PositionedObject held, ObjectPart part, string named, string path, bool wantMin, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.Every && Whole(held) && SortedWithoutGaps(file, named))
        {
            ObjectEntry entry = held.Entry;
            long end = wantMin ? 0 : entry.PhysicalRows - 1;
            long live = entry.Deletions.Physical(wantMin ? 0 : entry.Rows - 1);
            ScanBuilder ends = Configure(file.ScanBuilder(), null, null, canonical: false);
            if (live != end)
            {
                ends.Take([live]);
            }

            return wantMin
                ? await ends.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await ends.MaxAsync(named, cancellationToken).ConfigureAwait(false);
        }

        DType column = ClusteringKey.Resolve(file.DType, named);
        if (column.Kind is DTypeKind.Utf8 or DTypeKind.Binary or DTypeKind.Bool or DTypeKind.Decimal
            || (column.Kind == DTypeKind.Primitive && !column.PType.IsFloat()))
        {
            ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
            FilterLiteral found = wantMin
                ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
            if (found.Kind == FilterLiteralKind.Null)
            {
                return found;
            }

            VortexExpr equal = Expr.Eq(Expr.Field(named), Expr.Literal(found));
            if (await LiveCountAsync(file, held, part.Pushed is null ? equal : Expr.And(part.Pushed, equal), cancellationToken).ConfigureAwait(false) > 0)
            {
                return found;
            }
        }

        return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
    }

    /// <summary>Whether a file's statistics say a top-level column is sorted, and holds no null and no NaN.</summary>
    private static bool SortedWithoutGaps(VortexFile file, string path)
    {
        if (!KeyCursorBuilder.StatedSorted(file, path))
        {
            return false;
        }

        int field = file.DType.IndexOfField(path);
        FieldStatistics statistics = file.FileStatistics.GetField(field);
        bool floats = file.DType.GetField(field) is { Kind: DTypeKind.Primitive } column && column.PType.IsFloat();
        return statistics.TryGetStoredNullCount(out ulong nulls) && nulls == 0
            && (!floats || (statistics.TryGetNanCount(out ulong nans) && nans == 0));
    }

    /// <summary>The extreme of a column over a stream of batches that hold it.</summary>
    private static async ValueTask<FilterLiteral> ExtremeOfAsync(IAsyncEnumerable<RecordBatch> batches, string path, bool wantMin)
    {
        FilterLiteral best = FilterLiteral.Null;
        await foreach (RecordBatch batch in batches.ConfigureAwait(false))
        {
            int node = ColumnPath.NodeOf(batch, path);
            if (!Extremes.TryFind(batch.Arena, node, default, listed: false, wantMin, out int row))
            {
                continue;
            }

            bool numeric = batch.Arena.GetNode(ComparisonKernels.Unwrap(batch.Arena, node)).DType.Kind == DTypeKind.Decimal;
            bool read = numeric
                ? LiteralReader.TryReadDecimal(batch.Arena, node, row, out FilterLiteral value)
                : LiteralReader.TryRead(batch.Arena, node, row, out value);
            if (read && Beats(value, best, wantMin))
            {
                best = value;
            }
        }

        return best;
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

    /// <summary>A file's scan under a filter, a projection and this builder's options.</summary>
    private ScanBuilder Configure(ScanBuilder scan, VortexExpr? filter, FieldMask? projection, bool canonical)
    {
        if (filter is not null)
        {
            scan.Where(filter);
        }

        if (projection is { } mask)
        {
            scan.ProjectMask(in mask);
        }

        if (_options.BatchRows > 0)
        {
            scan.WithMaxBatchRows(_options.BatchRows);
        }

        int degree = _options.DegreeOfParallelism > 0
            ? _options.DegreeOfParallelism
            : _dataset.Session.Options.MaxDegreeOfParallelism;
        scan.WithPruning(_options.Pruning)
            .WithIndexes(_indexes)
            .WithDegreeOfParallelism(Math.Max(degree, 1))
            .WithPrefetch(_options.Prefetch)
            .WithCompaction(_options.Compact)
            .WithEncodings(!canonical && _keepEncodings, !canonical && _sinkDecodes);
        return _counters is { } counters ? scan.WithMetrics(counters) : scan;
    }
}

/// <summary>One data object and where its rows start in the dataset.</summary>
/// <param name="Entry">Its leaf entry, summaries included.</param>
/// <param name="FirstRow">Its first row among the dataset's, in the tree's order.</param>
internal readonly record struct PositionedObject(ObjectEntry Entry, long FirstRow)
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
internal sealed record DatasetPlan(
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
