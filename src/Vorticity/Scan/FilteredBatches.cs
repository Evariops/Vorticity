using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;

namespace Vorticity.Scan;

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

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        CancellationToken cancellationToken = default) =>
        new Enumerator(_inner, _filter, _prune, _indexes, _rows, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly BatchAsyncEnumerable _source;
        private readonly VortexExpr? _filter;
        private readonly bool _prune;
        private readonly bool _indexes;
        private readonly RowRange _rows;
        private readonly CancellationToken _token;
        private IAsyncEnumerator<RecordBatch>? _inner;

        internal Enumerator(
            BatchAsyncEnumerable source, VortexExpr? filter, bool prune, bool indexes, RowRange rows,
            CancellationToken token)
        {
            _source = source;
            _filter = filter;
            _prune = prune;
            _indexes = indexes;
            _rows = rows;
            _token = token;
        }

        /// <summary>
        /// The rows an exact source proves the filter selects, when there is one and they fit a
        /// batch: the scan then reads them and evaluates nothing.
        /// </summary>
        private async ValueTask<RowSelection?> ProvenAsync()
        {
            if (!_prune || _filter is null || _source.HasTake)
            {
                return null;
            }

            ExactCover? cover = await ExactCover
                .TryCreateAsync(_source.File, _filter, _indexes, _token)
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

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_inner is null && await ProvenAsync().ConfigureAwait(false) is { } proven)
            {
                _inner = _source.GetAsyncEnumerator(live: null, proven, _token);
            }

            if (_inner is null)
            {
                // One read of every zone map the filter can use, before the first batch, and one
                // mask of live blocks refined from it. Both stay in memory for the rest of the
                // scan; every split asks the mask, never the zone maps.
                BlockMask? live = _prune && _filter is not null
                    ? await ZonePruningPlan
                        .RefineAsync(_source.File, _source.Tree, _filter, _token, steps: null, _source.Metrics, _indexes)
                        .ConfigureAwait(false)
                    : null;

                _inner = _source.GetAsyncEnumerator(live, _token);
            }

            while (await _inner.MoveNextAsync().ConfigureAwait(false))
            {
                if (_inner.Current.RowCount > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? default;
    }
}
