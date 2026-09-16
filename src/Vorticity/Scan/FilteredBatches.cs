// The two things a filtered scan does around its batches: prune before reading, skip after.
//
// A selective filter rejects whole splits, and handing the caller an `await foreach` that yields
// empty batches would make every consumer write the same `if (batch.RowCount == 0) continue;`. This
// wrapper writes it once.
//
// Both live here rather than in the enumerator because both are things ONLY a filtered scan does,
// and docs/03-architecture.md §3.7 hand-writes that enumerator precisely to avoid a compiler state
// machine: ScanAllocationTests pins its per-batch figure at exactly one RecordBatch. An unfiltered
// scan therefore keeps the object graph it had, and only a filtered one pays the one extra
// per-enumeration allocation this adds.
//
// The pruning pass is why this is lazy. Reading the zone maps is I/O, ExecuteAsync is synchronous,
// and doing it on the first MoveNextAsync is what lets a scan that is built and never enumerated
// cost nothing at all.
//
// The lifetime contract is unchanged and is why this forwards rather than buffers: the inner
// enumerator disposes the previous batch on the next MoveNextAsync, so skipping an empty batch
// disposes it exactly the way delivering it would have.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Scan;

/// <summary>A filtered scan: zone-pruned before reading, empty batches skipped after.</summary>
internal sealed class FilteredBatches : IAsyncEnumerable<RecordBatch>
{
    private readonly BatchAsyncEnumerable _inner;
    private readonly VortexExpr? _filter;
    private readonly bool _prune;

    internal FilteredBatches(BatchAsyncEnumerable inner, VortexExpr? filter, bool prune)
    {
        _inner = inner;
        _filter = filter;
        _prune = prune;
    }

    /// <inheritdoc/>
    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        CancellationToken cancellationToken = default) =>
        new Enumerator(_inner, _filter, _prune, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly BatchAsyncEnumerable _source;
        private readonly VortexExpr? _filter;
        private readonly bool _prune;
        private readonly CancellationToken _token;
        private IAsyncEnumerator<RecordBatch>? _inner;

        internal Enumerator(
            BatchAsyncEnumerable source, VortexExpr? filter, bool prune, CancellationToken token)
        {
            _source = source;
            _filter = filter;
            _prune = prune;
            _token = token;
        }

        public RecordBatch Current =>
            _inner is null ? ScanThrow.NoCurrentBatch<RecordBatch>() : _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_inner is null)
            {
                // One read of every zone map the filter can use, before the first batch, and one
                // mask of live blocks refined from it (docs/11 §6.1). Both are memory-resident for
                // the rest of the scan; every split asks the mask, never the zone maps.
                BlockMask? live = _prune && _filter is not null
                    ? await ZonePruningPlan
                        .RefineAsync(_source.File, _source.Tree, _filter, _token)
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
