using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>One answer over the rows the scan keeps: <c>await scan.AggAsync(a =&gt; a.Count())</c>.</summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="aggregate">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answer.</returns>
    public async ValueTask<T> AggAsync<T>(Func<Aggregates<TRecord>, Sym<T>> aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ResultNode<T> node = AggregationPlan.Result(aggregate(new Aggregates<TRecord>(Binding)));
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([node], []), cancellationToken).ConfigureAwait(false);
        return node.Bind(outcome)(0);
    }

    /// <summary>
    /// Several answers over the rows the scan keeps, computed in one pass, into a record whose
    /// members take them in order: <c>await scan.AggAsync&lt;Summary&gt;(a =&gt; (a.Min(x =&gt; x.Celsius), a.Count()))</c>.
    /// </summary>
    /// <typeparam name="TResult">The record, whose members, in declaration order, are of the answers' types or their nullable forms.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates, returning a tuple of any length of them.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    /// <exception cref="ArgumentException">An element of the tuple is not a symbol.</exception>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its answer.</exception>
    public async ValueTask<TResult> AggAsync<TResult>(Func<Aggregates<TRecord>, ITuple> aggregates, CancellationToken cancellationToken = default)
        where TResult : IVortexRecord<TResult>
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        IResultNode[] nodes = GroupedScan<TRecord, TRecord>.Elements(aggregates(new Aggregates<TRecord>(Binding)), []);
        SymNode[] results = new SymNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            results[i] = (SymNode)nodes[i];
        }

        // Checked before the scan runs: a record that does not take the answers reads nothing.
        AggregationQuery query = new AggregationQuery(Host, new AggregationPlan(results, []), nodes, []).As(TResult.Schema, typeof(TResult));
        GroupBatches batches = query.Groups(cancellationToken);
        await using (batches.ConfigureAwait(false))
        {
            if (!await batches.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The aggregates of a scan are one row.");
            }

            RecordBatch batch = batches.Current;
            RecordBinding binding = RecordBinding.For<TResult>(query.Schema, query.Session.Options.Extensions);
            TResult[] row = new TResult[1];
            TResult.ReadRows(new Columns<TResult>(batch, batch.Arena, batch.RootIndex, binding, 0, default, 0, projected: false), row);
            return row[0];
        }
    }
}
