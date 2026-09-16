// The scan with a different output - docs/12-index-reads.md §5: AnyAsync and CountAsync, computed
// without a RecordBatch and bounded by one batch of memory, whatever the file's size.
//
// THE COUNT IS PUSHED INTO THE STRUCTURES, SPLIT BY SPLIT, CHEAPEST PROOF FIRST (§5.2). A split
// the mask killed counts nothing and reads nothing. A split the zone maps decide -- the full-block
// proof, `ZonePruner.TryCount` over the verdict of step 10a -- counts from bounds already in
// memory. The rest are decoded, the filter evaluated, the trues counted: the first half of the
// batch enumerator's ApplyFilter without the gather, the projection trim, or the batch. The exact
// cover of an index (§5.2's first tier) is a case of the same loop, for when a source gives one.
//
// ONE CONTEXT, ONE SPLIT AT A TIME. A terminal runs on a single lane whatever the degree: it has no
// batch to hand out, so nothing is gained by decoding two splits at once, and the memory bound of
// §5 -- one batch -- is kept by construction. The read is the batch enumerator's own two-phase
// register-then-read, and the decode the same push-down of a take (SplitExecution), so a terminal
// cannot count a row a scan would not return; the tests hold that against the materialized scan
// with each tier switched off in turn.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;

namespace Vorticity.Scan;

/// <summary>The terminals of one scan: what it would return, without returning it.</summary>
internal sealed class TerminalScan
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly VortexExpr? _filter;
    private readonly RowRange _rows;
    private readonly long _cap;
    private readonly FieldMask _mask;
    private readonly RowSelection? _take;
    private readonly bool _prune;
    private readonly CountTiers _tiers;
    private readonly ScanMetrics? _metrics;

    internal TerminalScan(
        VortexFile file,
        LayoutTree tree,
        VortexExpr? filter,
        RowRange rows,
        long cap,
        Projection read,
        RowSelection? take,
        bool prune,
        CountTiers tiers,
        ScanMetrics? metrics)
    {
        _file = file;
        _tree = tree;
        _filter = filter;
        _rows = rows;
        _cap = cap;
        _mask = read.RootMask;
        _take = take;
        _prune = prune;
        _tiers = tiers;
        _metrics = metrics;
    }

    /// <summary>How many rows the scan would return (docs/12 §5.2).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal ValueTask<long> CountAsync(CancellationToken cancellationToken) =>
        RunAsync(stopAtFirst: false, cancellationToken);

    /// <summary>Whether the scan would return at least one row (docs/12 §5.1).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<bool> AnyAsync(CancellationToken cancellationToken) =>
        await RunAsync(stopAtFirst: true, cancellationToken).ConfigureAwait(false) > 0;

    private async ValueTask<long> RunAsync(bool stopAtFirst, CancellationToken cancellationToken)
    {
        if (_filter is null)
        {
            // Without a filter the count is arithmetic: the rows, or the taken rows, which Take
            // already checked against the file and collapsed.
            return _take is not null ? _take.Count : _rows.Length;
        }

        if (_rows.IsEmpty)
        {
            return 0;
        }

        SplitPlan plan = SplitPlan.Compute(_tree, _rows, in _mask, _cap);
        ZonePruningPlan.PruningPlan pruning = _prune
            ? await ZonePruningPlan
                .PlanAsync(_file, _tree, _filter, cancellationToken, steps: null, _metrics)
                .ConfigureAwait(false)
            : default;
        BlockMask? live = pruning.Live;
        ZonePruner? zones = (_tiers & CountTiers.FullBlock) != 0 ? pruning.Zones : null;

        long total = 0;
        using ScanContext context = new ScanContext(_file);
        context.LiveBlocks = live;
        context.Metrics = _metrics;

        // One evaluation window for the whole count, sized for the largest split the plan
        // produces: rented once, so a block costs no allocation (docs/12 §11).
        int capacity = (int)Math.Min(plan.MaxRows, int.MaxValue);
        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1));
        try
        {
            SplitCursor cursor = plan.CreateCursor();
            while (cursor.TryNext(out RowRange split))
            {
                if (_take is not null && !_take.Touches(split))
                {
                    continue;
                }

                if (live is not null && !live.AnyLive(split))
                {
                    continue;
                }

                if (zones is not null && TryProve(zones, split, out long proven))
                {
                    total += proven;
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += await DecodeAndCountAsync(context, split, states, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (stopAtFirst && total > 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
        }

        return total;
    }

    /// <summary>
    /// The full-block proof: what the zone maps decide of the split without a read. Under a take
    /// the maps say how many rows of the split match and never which, so only a whole answer
    /// serves -- every row, and therefore every taken row, or none.
    /// </summary>
    private bool TryProve(ZonePruner zones, RowRange split, out long count)
    {
        if (_take is null)
        {
            return zones.TryCount(split, out count);
        }

        RangeVerdict verdict = zones.Verdict(split);
        if (verdict.IsAllTrue)
        {
            count = _take.CountIn(split);
            return true;
        }

        count = 0;
        return verdict.IsNoneTrue;
    }

    /// <summary>The third tier: one decode, one evaluation, one count, no copy.</summary>
    private async ValueTask<long> DecodeAndCountAsync(
        ScanContext context, RowRange split, byte[] states, CancellationToken cancellationToken)
    {
        try
        {
            SplitExecution.Register(context, _tree, in _mask, split);
            _metrics?.AddRequests(context.Segments);
            await _file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            int root = SplitExecution.Execute(context, _tree, in _mask, split, _take);
            return CountTrue(context, root, states);
        }
        finally
        {
            // The split is counted and its arenas are free for the next one: the memory bound.
            context.ResetBatch();
        }
    }

    private long CountTrue(ScanContext context, int root, byte[] states)
    {
        int rows = context.Canonical.GetNode(root).Length;
        if (rows == 0)
        {
            return 0;
        }

        // The window is sized for the plan's largest split; a decoder cannot produce more rows
        // than the split has, but a rent covers the day one does.
        byte[] buffer = states.Length >= rows ? states : ArrayPool<byte>.Shared.Rent(rows);
        try
        {
            Span<byte> window = buffer.AsSpan(0, rows);
            FilterEvaluator.Evaluate(_filter!, context.Canonical, root, rows, window);
            return Trilean.CountTrue(window);
        }
        finally
        {
            if (!ReferenceEquals(buffer, states))
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
