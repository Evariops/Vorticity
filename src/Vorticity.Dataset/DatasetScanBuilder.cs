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
// WHAT IT DOES NOT DO YET: order across objects by a clustering key (`InKeyOrder`, §6.6) and the
// terminals that ride on it. Those need the mandatory run of §6.1 on the level-0 objects, which is
// the rest of the plan's step 39.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

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
    /// <remarks>
    /// Answered through the tree's row sums, so an object outside the range is neither opened nor
    /// counted and a subtree outside it is never read: O(log N) plus the objects the range touches.
    /// </remarks>
    public DatasetScanBuilder Rows(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        _from = from;
        _to = to;
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
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectLease lease = await _dataset.RentAsync(held.Entry.Key, cancellationToken).ConfigureAwait(false);
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
            ObjectLease lease = await _dataset.RentAsync(held.Entry.Key, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await Of(lease.File, held).CountAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

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
            if (lease.WasCached)
            {
                metrics.CacheHits++;
            }
        }
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
public readonly record struct PositionedObject(ObjectEntry Entry, long FirstRow);
