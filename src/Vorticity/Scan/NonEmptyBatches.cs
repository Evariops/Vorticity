// Skipping the batches a filter emptied.
//
// A selective filter rejects whole splits, and handing the caller an `await foreach` that yields
// empty batches would make every consumer write the same `if (batch.RowCount == 0) continue;`. This
// wrapper writes it once.
//
// It exists as a separate type, applied ONLY when a filter is present, for a measurable reason:
// docs/03-architecture.md §3.7 hand-writes the scan enumerator precisely to avoid the compiler's
// state machine, and ScanAllocationTests pins the per-batch figure at exactly one RecordBatch. An
// unfiltered scan therefore keeps the object graph it had, and only a filtered one pays the one
// extra per-enumeration allocation this adds.
//
// The lifetime contract is unchanged and is why this forwards rather than buffers: the inner
// enumerator disposes the previous batch on the next MoveNextAsync, so skipping an empty batch
// disposes it exactly the way delivering it would have.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;

namespace Vorticity.Scan;

/// <summary>A scan that does not yield batches whose rows were all filtered out.</summary>
internal sealed class NonEmptyBatches : IAsyncEnumerable<RecordBatch>
{
    private readonly IAsyncEnumerable<RecordBatch> _inner;

    internal NonEmptyBatches(IAsyncEnumerable<RecordBatch> inner) => _inner = inner;

    /// <inheritdoc/>
    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        CancellationToken cancellationToken = default) =>
        new Enumerator(_inner.GetAsyncEnumerator(cancellationToken));

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly IAsyncEnumerator<RecordBatch> _inner;

        internal Enumerator(IAsyncEnumerator<RecordBatch> inner) => _inner = inner;

        public RecordBatch Current => _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (await _inner.MoveNextAsync().ConfigureAwait(false))
            {
                if (_inner.Current.RowCount > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
