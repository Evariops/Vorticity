using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// A query whose result is a stream of batches: what a result's scan reads, whatever produced it.
/// Its batches are borrowed, each valid until the next is asked for.
/// </summary>
internal abstract class ResultQuery
{
    /// <summary>The result's columns.</summary>
    internal abstract VortexSchema Schema { get; }

    /// <summary>The session whose pool and options the query runs with.</summary>
    internal abstract VortexSession Session { get; }

    /// <summary>What the query's own scan did, which a result's scan reports as its own.</summary>
    internal abstract ScanCounters Counters { get; }

    /// <summary>The result's batches; the query runs as they are asked for.</summary>
    internal abstract IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken);

    /// <summary>The plan of the query's scan.</summary>
    internal abstract ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken);

    /// <summary>The same query, its result's columns named and typed by the members of a record, in order.</summary>
    /// <param name="record">The record's schema.</param>
    /// <param name="type">The record's type, for a message.</param>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its element.</exception>
    internal abstract ResultQuery As(VortexSchema record, Type type);

    /// <summary>The same query, its rows past the first <paramref name="skip"/> of those it delivers, <paramref name="take"/> of them at most.</summary>
    internal abstract ResultQuery Limit(long skip, long take);

    /// <summary>How the values of column <paramref name="column"/> are records: a custom aggregate's state; null for a column of values.</summary>
    internal virtual IVortexRecord? RecordOf(int column) => null;

    /// <summary>What the query's group by did, once it ran; null without a group by.</summary>
    internal virtual GroupMetrics? Grouping => null;

    /// <summary>The scan's metrics, with what its group by did.</summary>
    internal ScanMetrics Metrics() => ScanMetrics.From(Counters) with { Grouping = Grouping };

    /// <summary>The window [<paramref name="skip"/>, <paramref name="skip"/> + <paramref name="take"/>) of a window [<paramref name="skipped"/>, <paramref name="skipped"/> + <paramref name="taken"/>): the operators in the order written.</summary>
    internal static (long Skip, long Take) Within(long skipped, long taken, long skip, long take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        long left = taken == long.MaxValue ? long.MaxValue : Math.Max(0, taken - skip);
        return (skipped + skip, Math.Min(left, take));
    }
}

/// <summary>
/// The source of a result's scan: the batches of a query's result, with what a scan asks of its
/// source applied to each — the filter evaluated on it, the rows by their position in the order
/// the result is delivered, the columns projected. A result has no statistics, zone maps or
/// indexes: nothing is pruned, and every batch is evaluated.
/// </summary>
internal sealed class ResultScanSource : StreamScanSource
{
    private readonly ResultQuery _query;

    internal ResultScanSource(ResultQuery query) => _query = query;

    internal override VortexSchema Schema => _query.Schema;

    internal override VortexSession Session => _query.Session;

    private protected override string Kind => "result";

    internal override ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken) => _query.ExplainAsync(cancellationToken);

    /// <summary>The result's batches, every column of every row: the query delivers them as it runs.</summary>
    private protected override IAsyncEnumerator<RecordBatch> Stream(ScanSpec spec, int[]? columns, ScanCounters metrics, CancellationToken cancellationToken) =>
        _query.Batches(cancellationToken);
}
