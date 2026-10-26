using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of a query whose source keeps states of some of its rows: once the source has said whether a
/// part of its rows would come as states (<see cref="ScanSource.TakesStatesAsync"/>), the groups of a
/// blocking pass that takes them; otherwise those of the pass the query takes without them, as they close
/// on a key that streams.
/// </summary>
internal sealed class StatesOrStreams(AggregationQuery query, StateRequest states, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
{
    private IAsyncEnumerator<RecordBatch>? _groups;

    public RecordBatch Current => _groups!.Current;

    public async ValueTask<bool> MoveNextAsync()
    {
        if (_groups is null)
        {
            // A source that learns the partials of a question's family takes its states to learn them, or to answer from them.
            ScanSpec spec = await query.Host.AlignedSpecAsync(query.Plan, query.RowFilter, cancellationToken).ConfigureAwait(false);
            ScanSource source = query.Host.Source;
            spec = spec with { Family = source.LearnsPartials && spec.Options.UseSynopses ? LearnedFamily.Of(query.Plan, spec.Filter, source) : null };
            bool taken = await source.TakesStatesAsync(spec, states, cancellationToken).ConfigureAwait(false);
            _groups = taken ? new GroupBatches(query, cancellationToken) : query.Streamed(cancellationToken);
        }

        return await _groups.MoveNextAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _groups?.DisposeAsync() ?? ValueTask.CompletedTask;
}
