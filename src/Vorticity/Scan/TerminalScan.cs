// The scan with a different output - docs/12-index-reads.md §5: AnyAsync, CountAsync, MinAsync and
// MaxAsync, computed without a RecordBatch and bounded by one batch of memory, whatever the file's
// size.
//
// THE ANSWER IS PUSHED INTO THE STRUCTURES, SPLIT BY SPLIT, CHEAPEST PROOF FIRST (§5.2, §5.3). A
// split the mask killed counts nothing and reads nothing. A split the zone maps decide -- the
// full-block proof, `ZonePruner.TryCount` over the verdict of step 10a; or, for an extreme, the
// zone's own bound -- answers from bounds already in memory. The rest are decoded, the filter
// evaluated, the trues counted or the extreme found among them: the first half of the batch
// enumerator's ApplyFilter without the gather, the projection trim, or the batch. The exact cover
// of an index (§5.2's first tier) is a case of the same loop, for when a source gives one.
//
// ONE CONTEXT, ONE SPLIT AT A TIME. A terminal runs on a single lane whatever the degree: it has no
// batch to hand out, so nothing is gained by decoding two splits at once, and the memory bound of
// §5 -- one batch -- is kept by construction. The read is the batch enumerator's own two-phase
// register-then-read, and the decode the same push-down of a take (SplitExecution), so a terminal
// cannot count a row a scan would not return; the tests hold that against the materialized scan
// with each tier switched off in turn.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>The terminals of one scan: what it would return, without returning it.</summary>
internal sealed class TerminalScan
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly VortexExpr? _filter;
    private readonly RowRange _rows;
    private readonly bool _wholeFile;
    private readonly long _cap;
    private readonly FieldMask _mask;
    private readonly RowSelection? _take;
    private readonly bool _prune;
    private readonly TerminalTiers _tiers;
    private readonly ScanMetrics? _metrics;

    internal TerminalScan(
        VortexFile file,
        LayoutTree tree,
        VortexExpr? filter,
        RowRange rows,
        bool wholeFile,
        long cap,
        Projection read,
        RowSelection? take,
        bool prune,
        TerminalTiers tiers,
        ScanMetrics? metrics)
    {
        _file = file;
        _tree = tree;
        _filter = filter;
        _rows = rows;
        _wholeFile = wholeFile;
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
        ZonePruner? zones = (_tiers & TerminalTiers.FullBlock) != 0 ? pruning.Zones : null;

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

    // ------------------------------------------------------------------------------ extremes

    /// <summary>
    /// The smallest or largest non-null value of <paramref name="path"/> among the scan's rows
    /// (docs/12 §5.3), <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    /// <param name="path">The column.</param>
    /// <param name="wantMin">Whether the smallest value is wanted, else the largest.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// §5.3's resolutions in order. The file statistic, when the scan is the whole file and the
    /// statistic is <c>Exact</c>: no read. The zone bounds, for every split that is whole zones
    /// of the column's map and whose rows all qualify (no filter, or one the zone maps prove for
    /// the split): an <c>Exact</c> bound is the split's answer, an <c>Inexact</c> one a
    /// candidate -- the true extreme is at or beyond it -- decoded only when it could still beat
    /// the best, cheapest first, which is usually one decode. Everything else is decoded: the
    /// filter evaluated, the extreme found among the rows it keeps, one split of memory at a
    /// time. An ordered source (§5.3's third resolution) waits for step 11.
    /// </remarks>
    internal async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        if (_rows.IsEmpty)
        {
            return FilterLiteral.Null;
        }

        if (_filter is null && _wholeFile && (_tiers & TerminalTiers.FileStatistic) != 0 &&
            TryFileStatistic(path, wantMin, out FilterLiteral statistic))
        {
            return statistic;
        }

        FieldExpr field = Expr.Field(path);
        SplitPlan plan = SplitPlan.Compute(_tree, _rows, in _mask, _cap);
        ZonePruningPlan.PruningPlan pruning = _prune && _filter is not null
            ? await ZonePruningPlan
                .PlanAsync(_file, _tree, _filter, cancellationToken, steps: null, _metrics)
                .ConfigureAwait(false)
            : default;
        BlockMask? live = pruning.Live;
        ZonePruner? zones = (_tiers & TerminalTiers.FullBlock) != 0 ? pruning.Zones : null;

        // The column's own zone map, for the bounds tier. Read through the pruning plan so that
        // its cost lands in the sink like any structure's; a filter on the same column reads the
        // map twice, which is one small segment.
        ZoneColumn? column = null;
        if ((_tiers & TerminalTiers.ZoneBounds) != 0 && _take is null)
        {
            ZonePruningPlan.PruningPlan own = await ZonePruningPlan
                .PlanAsync(_file, _tree, Expr.IsNotNull(field), cancellationToken, steps: null, _metrics)
                .ConfigureAwait(false);
            column = own.Zones?.Column(path);
        }

        // Pass 1, no read: what the bounds decide, what they only bound, what they say nothing of.
        FilterLiteral best = FilterLiteral.Null;
        List<(RowRange Split, FilterLiteral Bound)>? candidates = null;
        List<RowRange>? undecided = null;
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

            bool everyRowQualifies = _filter is null || (zones is not null && zones.MustMatch(split));
            if (column is not null && everyRowQualifies &&
                TryZoneBound(column, split, wantMin, out FilterLiteral bound, out bool exact))
            {
                if (bound.Kind == FilterLiteralKind.Null)
                {
                    // Every zone all null: nothing to contribute.
                    continue;
                }

                if (exact)
                {
                    Keep(ref best, bound, wantMin);
                }
                else
                {
                    (candidates ??= []).Add((split, bound));
                }

                continue;
            }

            (undecided ??= []).Add(split);
        }

        if (undecided is null && candidates is null)
        {
            return best;
        }

        // Pass 2: the decodes -- the splits the bounds could not decide first, so that the best
        // is as good as it can be before a candidate is weighed against it. Each decode hands the
        // readers a mask of exactly its split, so that the restricted decode of step 8b
        // materializes the split and not the chunk around it: on a single-chunk file, one block
        // instead of the whole column, which is the point of deciding from the bounds at all.
        using ScanContext context = new ScanContext(_file);
        BlockMask scope = new BlockMask(_tree.Root.RowCount, SplitPlan.NaturalBatchRows(_tree));
        context.LiveBlocks = scope;
        context.Metrics = _metrics;
        int capacity = (int)Math.Min(plan.MaxRows, int.MaxValue);
        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1));
        int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(capacity, 1));
        try
        {
            if (undecided is not null)
            {
                for (int i = 0; i < undecided.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scope.KeepOnly(undecided[i]);
                    FilterLiteral found = await DecodeExtremeAsync(
                        context, undecided[i], field, wantMin, states, indices, cancellationToken)
                        .ConfigureAwait(false);
                    Keep(ref best, found, wantMin);
                }
            }

            if (candidates is not null)
            {
                // Best stated bound first: the true extreme of a candidate is at or beyond its
                // bound, so once one is decoded, every candidate whose bound cannot beat the best
                // is out -- and so is every one after it in this order.
                candidates.Sort((a, b) => Order(a.Bound, b.Bound, wantMin));
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (best.Kind != FilterLiteralKind.Null && !Beats(candidates[i].Bound, best, wantMin))
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    scope.KeepOnly(candidates[i].Split);
                    FilterLiteral found = await DecodeExtremeAsync(
                        context, candidates[i].Split, field, wantMin, states, indices, cancellationToken)
                        .ConfigureAwait(false);
                    Keep(ref best, found, wantMin);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
            ArrayPool<byte>.Shared.Return(states);
        }

        return best;
    }

    /// <summary>§5.3's first resolution: the file's own statistic, when it is the true value.</summary>
    private bool TryFileStatistic(string path, bool wantMin, out FilterLiteral literal)
    {
        literal = FilterLiteral.Null;
        if (!_file.HasFileStatistics || _file.RowCount <= 0)
        {
            return false;
        }

        // The statistics are shallow, one per top-level field of a struct root.
        DType schema = _file.Schema;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct || path.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        FileStatistics statistics = _file.Statistics;
        int index = schema.IndexOfField(path);
        if (index < 0 || index >= statistics.FieldCount)
        {
            return false;
        }

        FieldStatistics field = statistics.GetField(index);
        if (wantMin)
        {
            return field.HasMin && field.MinPrecision == StatPrecision.Exact &&
                   FileStatisticsPruner.TryLiteral(field.Min, out literal);
        }

        return field.HasMax && field.MaxPrecision == StatPrecision.Exact &&
               FileStatisticsPruner.TryLiteral(field.Max, out literal);
    }

    /// <summary>
    /// §5.3's second resolution over one split: the extreme of the zone bounds, when the split
    /// is whole zones of the map and every zone with a value has the bound.
    /// </summary>
    /// <param name="column">The column's zone map.</param>
    /// <param name="split">The split, in file row coordinates.</param>
    /// <param name="wantMin">Whether the smallest value is wanted, else the largest.</param>
    /// <param name="bound">The extreme of the bounds, or null when every zone is all null.</param>
    /// <param name="exact">Whether every bound taken is the true extreme of its zone.</param>
    /// <returns>Whether the bounds decide or bound the split at all.</returns>
    private static bool TryZoneBound(
        ZoneColumn column, RowRange split, bool wantMin, out FilterLiteral bound, out bool exact)
    {
        bound = FilterLiteral.Null;
        exact = true;

        // A partial zone's bound says nothing of the part: the extreme may sit outside it.
        long zoneLength = column.ZoneLength;
        if (split.Start % zoneLength != 0 || (split.End % zoneLength != 0 && split.End != column.RowCount))
        {
            return false;
        }

        ZoneRange zones = column.Zones(split);
        if (zones.End <= zones.Start)
        {
            return false;
        }

        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);
            if (bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone))
            {
                // No values, nothing to bound.
                continue;
            }

            if (!(wantMin ? bounds.HasMin : bounds.HasMax))
            {
                return false;
            }

            FilterLiteral value = wantMin ? bounds.Min : bounds.Max;
            exact &= bounds.IsExact;
            Keep(ref bound, value, wantMin);
        }

        return true;
    }

    /// <summary>The decode resolution over one split: the filter's rows, the extreme among them.</summary>
    private async ValueTask<FilterLiteral> DecodeExtremeAsync(
        ScanContext context, RowRange split, FieldExpr field, bool wantMin, byte[] states, int[] indices,
        CancellationToken cancellationToken)
    {
        try
        {
            SplitExecution.Register(context, _tree, in _mask, split);
            _metrics?.AddRequests(context.Segments);
            await _file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            int root = SplitExecution.Execute(context, _tree, in _mask, split, _take);
            return Extreme(context, root, field, wantMin, states, indices);
        }
        finally
        {
            context.ResetBatch();
        }
    }

    private FilterLiteral Extreme(
        ScanContext context, int root, FieldExpr field, bool wantMin, byte[] states, int[] indices)
    {
        int rows = context.Canonical.GetNode(root).Length;
        if (rows == 0)
        {
            return FilterLiteral.Null;
        }

        int column = FilterEvaluator.Resolve(context.Canonical, root, field, rows);
        ReadOnlySpan<int> selected = default;
        bool listed = false;
        byte[] window = states.Length >= rows ? states : ArrayPool<byte>.Shared.Rent(rows);
        int[] list = indices.Length >= rows ? indices : ArrayPool<int>.Shared.Rent(rows);
        try
        {
            if (_filter is not null)
            {
                Span<byte> evaluated = window.AsSpan(0, rows);
                FilterEvaluator.Evaluate(_filter, context.Canonical, root, rows, evaluated);
                int count = Trilean.CountTrue(evaluated);
                if (count == 0)
                {
                    return FilterLiteral.Null;
                }

                if (count < rows)
                {
                    Span<int> kept = list.AsSpan(0, count);
                    CanonicalFilter.Select(evaluated, kept);
                    selected = kept;
                    listed = true;
                }
            }

            return Extremes.TryFind(context.Canonical, column, selected, listed, wantMin, out int bestRow) &&
                   LiteralReader.TryRead(context.Canonical, column, bestRow, out FilterLiteral literal)
                ? literal
                : FilterLiteral.Null;
        }
        finally
        {
            if (!ReferenceEquals(window, states))
            {
                ArrayPool<byte>.Shared.Return(window);
            }

            if (!ReferenceEquals(list, indices))
            {
                ArrayPool<int>.Shared.Return(list);
            }
        }
    }

    /// <summary>Keeps <paramref name="candidate"/> when it beats <paramref name="best"/>, or when there is no best yet.</summary>
    private static void Keep(ref FilterLiteral best, FilterLiteral candidate, bool wantMin)
    {
        if (candidate.Kind == FilterLiteralKind.Null)
        {
            return;
        }

        if (best.Kind == FilterLiteralKind.Null || Beats(candidate, best, wantMin))
        {
            best = candidate;
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is strictly better than <paramref name="best"/>, in the filter's order.</summary>
    private static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin) =>
        ZonePruner.TryCompare(candidate, best, out int order) && (wantMin ? order < 0 : order > 0);

    /// <summary>Best first: ascending for a minimum, descending for a maximum.</summary>
    private static int Order(FilterLiteral a, FilterLiteral b, bool wantMin)
    {
        int order = ZonePruner.TryCompare(a, b, out int o) ? o : 0;
        return wantMin ? order : -order;
    }
}
