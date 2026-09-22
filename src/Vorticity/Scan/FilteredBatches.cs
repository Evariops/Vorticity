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

namespace Vorticity.Scanning;

/// <summary>
/// A filtered scan: zone-pruned before reading, empty batches skipped after, so that no consumer has
/// to write the row-count check itself. Both passes live in this wrapper rather than in the inner
/// enumerator, which is hand-written to avoid a compiler state machine and its per-batch allocation;
/// only a filtered scan pays the one extra per-enumeration allocation this adds. The pruning pass is
/// deferred to the first <c>MoveNextAsync</c> because reading zone maps is I/O, which keeps a scan
/// that is built and never enumerated free. Batches are forwarded rather than buffered: the inner
/// enumerator disposes the previous batch on its next step, so skipping an empty one disposes it
/// exactly as delivering it would have.
/// </summary>
internal sealed class FilteredBatches : IAsyncEnumerable<RecordBatch>
{
    private readonly BatchAsyncEnumerable _inner;
    private readonly VortexExpr? _filter;
    private readonly bool _prune;
    private readonly bool _indexes;

    private readonly RowRange _rows;

    internal FilteredBatches(
        BatchAsyncEnumerable inner, VortexExpr? filter, bool prune, bool indexes = true, RowRange? rows = null)
    {
        _inner = inner;
        _filter = filter;
        _prune = prune;
        _indexes = indexes;
        _rows = rows ?? new RowRange(0, inner.File.RowCount);
    }

    /// <summary>Whether <see cref="Live"/> is the filter's mask, refined already by the caller, so that no structure is read again.</summary>
    internal bool Refined { get; init; }

    /// <summary>The refined mask when <see cref="Refined"/>, or null when no structure prunes anything.</summary>
    internal BlockMask? Live { get; init; }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        CancellationToken cancellationToken = default) =>
        new Enumerator(this, cancellationToken);

    /// <summary>
    /// Whether an exact source could prove rows worth reading: not when the pruning left no block,
    /// nor once the zone maps alone prove more rows than a batch over the live blocks, since the
    /// source then declines anyway, and asking it would read a data segment for nothing.
    /// </summary>
    /// <param name="plan">The scan's splits.</param>
    /// <param name="cap">The rows of a batch, past which an exact source declines.</param>
    /// <param name="zones">The zone maps the pruning read, or null.</param>
    /// <param name="live">The blocks the pruning left, or null for all.</param>
    internal static bool MayFitBatch(SplitPlan plan, long cap, ZonePruner? zones, BlockMask? live)
    {
        if (live is { IsEmpty: true })
        {
            return false;
        }

        if (zones is null)
        {
            return true;
        }

        long proven = 0;
        SplitCursor cursor = plan.CreateCursor();
        while (cursor.TryNext(out RowRange split))
        {
            if ((live is null || live.AnyLive(split)) && zones.TryCount(split, out long count))
            {
                proven += count;
                if (proven > cap)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly BatchAsyncEnumerable _source;
        private readonly VortexExpr? _filter;
        private readonly bool _prune;
        private readonly bool _indexes;
        private readonly RowRange _rows;
        private readonly bool _refined;
        private readonly BlockMask? _live;
        private readonly CancellationToken _token;
        private IAsyncEnumerator<RecordBatch>? _inner;

        internal Enumerator(FilteredBatches owner, CancellationToken token)
        {
            _source = owner._inner;
            _filter = owner._filter;
            _prune = owner._prune;
            _indexes = owner._indexes;
            _rows = owner._rows;
            _refined = owner.Refined;
            _live = owner.Live;
            _token = token;
        }

        /// <summary>
        /// The rows an exact source proves the filter selects, when there is one and they fit a
        /// batch: the scan then reads them and evaluates nothing.
        /// </summary>
        /// <param name="pruning">What the pruning read and left: its zone maps, handed to the source so that it does not read them again, and its blocks.</param>
        private async ValueTask<RowSelection?> ProvenAsync(ZonePruningPlan.PruningPlan pruning)
        {
            ZonePruner? zones = pruning.Zones;
            if (!_prune || _filter is null || _source.HasTake || pruning.Located
                || !MayFitBatch(_source.Plan, SplitPlan.NaturalBatchRows(_source.Tree), zones, pruning.Live))
            {
                return null;
            }

            ExactCover? cover = await ExactCover
                .TryCreateAsync(_source.File, _filter, _indexes, _token, zones, _source.Metrics)
                .ConfigureAwait(false);
            if (cover is null)
            {
                return null;
            }

            try
            {
                long[]? rows = await cover
                    .RowsAsync(_rows, SplitPlan.NaturalBatchRows(_source.Tree), _token)
                    .ConfigureAwait(false);
                return rows is null ? null : RowSelection.Create(rows, _source.File.RowCount);
            }
            finally
            {
                await cover.DisposeAsync().ConfigureAwait(false);
            }
        }

        public RecordBatch Current =>
            _inner is null ? ScanThrow.NoCurrentBatch<RecordBatch>() : _inner.Current;

        // Pooled, as the inner enumerator's own steps are: a step that suspends on its read rents
        // its state machine instead of allocating one per batch.
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_inner is null)
            {
                // One read of every zone map the filter can use, before the first batch, and one
                // mask of live blocks refined from it. Both stay in memory for the rest of the
                // scan; every split asks the mask, never the zone maps.
                ZonePruningPlan.PruningPlan pruning = _refined
                    ? new ZonePruningPlan.PruningPlan(_live, null)
                    : _prune && _filter is not null
                        ? await ZonePruningPlan
                            .PlanAsync(_source.File, _source.Tree, _filter, _token, steps: null, _source.Metrics, _indexes)
                            .ConfigureAwait(false)
                        : default;

                // An exact index gathers the rows it proved, which a scan delivering whole blocks
                // does not want.
                _inner = _source.Compact && await ProvenAsync(pruning).ConfigureAwait(false) is { } proven
                    ? _source.GetAsyncEnumerator(live: null, proven, _token)
                    : _source.GetAsyncEnumerator(pruning.Live, _token);
            }

            while (await _inner.MoveNextAsync().ConfigureAwait(false))
            {
                if (_inner.Current.SelectedRows > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? default;
    }
}
