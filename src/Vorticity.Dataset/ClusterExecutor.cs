using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Dataset;

/// <summary>How a cluster's coordinator spreads a question over its nodes (spec F, 3 and 4).</summary>
public sealed record ClusterOptions
{
    /// <summary>The parts a node reads at once, and the parts a question is cut in for each node; 4 by default.</summary>
    public int TasksPerNode { get; init; } = 4;

    /// <summary>The times a part a node failed is run again on the next of its owners; 2 by default.</summary>
    public int Retries { get; init; } = 2;
}

/// <summary>
/// A cluster's coordinator (spec F, 3): a named question (<see cref="ClusterQuestions"/>) cut into parts, each sent to the
/// owner of the object of its first row among the nodes (<see cref="Rendezvous"/>), every node reading the version this
/// handle reads; the parts' partials merged as they arrive, to the bits the question gives on one node. A part a node
/// fails is run again on the next of its owners.
/// </summary>
public sealed class ClusterExecutor
{
    private readonly string[] _nodes;
    private readonly IClusterTransport _transport;
    private readonly ClusterQuestions _questions;
    private readonly ClusterOptions _options;

    /// <summary>The coordinator of <paramref name="nodes"/>, reached by <paramref name="transport"/>, which answer <paramref name="questions"/>.</summary>
    /// <param name="nodes">The cluster's nodes, by name, as the application knows them.</param>
    /// <param name="transport">How the coordinator reaches them.</param>
    /// <param name="questions">The questions, the same every node holds.</param>
    /// <param name="options">How it spreads them; null for the defaults.</param>
    /// <exception cref="ArgumentException">No node is named, or one twice.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A node reads no part at once, or the retries are negative.</exception>
    public ClusterExecutor(IReadOnlyList<string> nodes, IClusterTransport transport, ClusterQuestions questions, ClusterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(questions);
        _nodes = [.. nodes];
        if (_nodes.Length == 0 || new HashSet<string>(_nodes, StringComparer.Ordinal).Count != _nodes.Length)
        {
            throw new ArgumentException("A cluster has a node at least, each named once.", nameof(nodes));
        }

        _options = options ?? new ClusterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.TasksPerNode, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.Retries, nameof(options));
        _transport = transport;
        _questions = questions;
    }

    /// <summary>
    /// The question <paramref name="question"/>, of one result a group, over the version <paramref name="dataset"/> reads,
    /// from its parts run on the cluster's nodes.
    /// </summary>
    /// <typeparam name="TResult">The type of the question's results.</typeparam>
    /// <param name="dataset">The handle whose version every node reads.</param>
    /// <param name="question">The question's name.</param>
    /// <param name="arguments">Its arguments, as the application codes them.</param>
    /// <param name="location">Where the nodes open the dataset; null for the handle's own location.</param>
    /// <param name="cancellationToken">Cancels the cut into parts, and the parts' reads with the merge's.</param>
    /// <returns>
    /// The merged aggregation, enumerated as any other, once: its enumeration sends the parts and folds each partial as it
    /// arrives (spec F, 5), and throws the error of a part that failed on every node it was given to.
    /// </returns>
    /// <exception cref="ArgumentException">No question has that name or those results, or the handle has no location the nodes resolve and none is given.</exception>
    /// <exception cref="NotSupportedException">A state does not answer one of the question's aggregates whole.</exception>
    public async ValueTask<Aggregation<TResult>> RunAsync<TResult>(
        VortexDataset dataset, string question, ReadOnlyMemory<byte> arguments = default, string? location = null, CancellationToken cancellationToken = default) =>
        new Aggregation<TResult>(await MergedAsync(dataset, question, typeof(TResult), arguments, location, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The question <paramref name="question"/>, of several results a group read through a record
    /// (<see cref="Aggregation.As{TRecord}"/>), over the version <paramref name="dataset"/> reads, from its parts run on the
    /// cluster's nodes.
    /// </summary>
    /// <param name="dataset">The handle whose version every node reads.</param>
    /// <param name="question">The question's name.</param>
    /// <param name="arguments">Its arguments, as the application codes them.</param>
    /// <param name="location">Where the nodes open the dataset; null for the handle's own location.</param>
    /// <param name="cancellationToken">Cancels the cut into parts, and the parts' reads with the merge's.</param>
    /// <returns>
    /// The merged aggregation, read once: its reading sends the parts and folds each partial as it arrives (spec F, 5), and
    /// throws the error of a part that failed on every node it was given to.
    /// </returns>
    /// <exception cref="ArgumentException">No question has that name or those results, or the handle has no location the nodes resolve and none is given.</exception>
    /// <exception cref="NotSupportedException">A state does not answer one of the question's aggregates whole.</exception>
    public async ValueTask<Aggregation> RunAsync(
        VortexDataset dataset, string question, ReadOnlyMemory<byte> arguments = default, string? location = null, CancellationToken cancellationToken = default) =>
        new Aggregation(await MergedAsync(dataset, question, null, arguments, location, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Has every node warm the objects it owns in <paramref name="version"/> of the dataset at <paramref name="location"/>
    /// (spec F, 7): what a handle's <see cref="DatasetOptions.Moved"/> calls, so that a new object's owner reads its
    /// metadata before its first part.
    /// </summary>
    public async ValueTask WarmAsync(string location, ulong version, CancellationToken cancellationToken = default)
    {
        Task[] warming = new Task[_nodes.Length];
        for (int i = 0; i < _nodes.Length; i++)
        {
            warming[i] = _transport.WarmAsync(_nodes[i], location, version, _nodes, cancellationToken).AsTask();
        }

        await Task.WhenAll(warming).ConfigureAwait(false);
    }

    /// <summary>
    /// The merge of the question <paramref name="question"/>'s parts: the whole question cut into
    /// <see cref="ClusterOptions.TasksPerNode"/> parts a node, each given to the owner of the object of its first row, their
    /// partials gathered as the merge reads them.
    /// </summary>
    private async ValueTask<AggregationQuery> MergedAsync(
        VortexDataset dataset, string question, Type? results, ReadOnlyMemory<byte> arguments, string? location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        location ??= dataset.Location ?? throw new ArgumentException("A handle opened over a store has no location the nodes resolve: give one.", nameof(location));
        AggregationQuery whole = _questions.Build(question, results, dataset, ClusterQuestions.Every, arguments);
        RowRange[] ranges = await AggregationPartials.PartsAsync(whole, _nodes.Length * _options.TasksPerNode, cancellationToken).ConfigureAwait(false);
        (long First, long End, UInt128 Uid)[] objects = await ObjectsAsync(dataset, cancellationToken).ConfigureAwait(false);
        (ClusterTask Task, UInt128 Affinity)[] parts = new (ClusterTask, UInt128)[ranges.Length];
        for (int p = 0; p < parts.Length; p++)
        {
            parts[p] = (new ClusterTask(question, arguments, location, dataset.Version, ranges[p]), Affinity(objects, ranges[p].Start));
        }

        return AggregationPartials.Merged(whole, GatherAsync(parts, cancellationToken));
    }

    /// <summary>
    /// The partials of <paramref name="parts"/> as they arrive from the nodes, which the merge folds while the nodes read the
    /// others (spec F, 5): the parts are sent when the merge asks for the first, <see cref="ClusterOptions.TasksPerNode"/>
    /// at most on a node at once. A part that failed on every node it was given to ends the sequence with its error; the
    /// sequence ended, the parts still in flight stop.
    /// </summary>
    private async IAsyncEnumerable<AggregationPartial> GatherAsync(
        (ClusterTask Task, UInt128 Affinity)[] parts, CancellationToken asked, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(asked, cancellationToken);
        Channel<AggregationPartial> arriving = Channel.CreateUnbounded<AggregationPartial>(new UnboundedChannelOptions { SingleReader = true });
        SemaphoreSlim[] gates = new SemaphoreSlim[_nodes.Length];
        for (int i = 0; i < gates.Length; i++)
        {
            gates[i] = new SemaphoreSlim(_options.TasksPerNode);
        }

        Task[] running = new Task[parts.Length];
        for (int p = 0; p < parts.Length; p++)
        {
            running[p] = SendAsync(parts[p].Task, parts[p].Affinity, gates, arriving.Writer, stop.Token);
        }

        try
        {
            for (int left = parts.Length; left > 0; left--)
            {
                AggregationPartial? partial;
                while (!arriving.Reader.TryRead(out partial))
                {
                    // Only a part's failure completes the channel, and the wait throws it.
                    await arriving.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false);
                }

                yield return partial;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(running).ConfigureAwait(false);
            foreach (SemaphoreSlim gate in gates)
            {
                gate.Dispose();
            }
        }
    }

    /// <summary>Runs <paramref name="task"/> and hands its partial to <paramref name="arriving"/>, which its failure completes.</summary>
    private async Task SendAsync(
        ClusterTask task, UInt128 affinity, SemaphoreSlim[] gates, ChannelWriter<AggregationPartial> arriving, CancellationToken cancellationToken)
    {
        try
        {
            arriving.TryWrite(await RunPartAsync(task, affinity, gates, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception failure)
        {
            // The first failure ends the gathering, whose reader throws it.
            arriving.TryComplete(failure);
        }
    }

    /// <summary>
    /// Runs <paramref name="task"/> on the owners of <paramref name="affinity"/> in their order, a node that is not reached or
    /// went away passing it on; an error of the question itself is not run again.
    /// </summary>
    private async Task<AggregationPartial> RunPartAsync(ClusterTask task, UInt128 affinity, SemaphoreSlim[] gates, CancellationToken cancellationToken)
    {
        string[] order = Rendezvous.Order(affinity, _nodes);
        for (int attempt = 0; ; attempt++)
        {
            string node = order[attempt % order.Length];
            SemaphoreSlim gate = gates[Array.IndexOf(_nodes, node)];
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _transport.RunAsync(node, task, cancellationToken).ConfigureAwait(false);
            }
            catch (ClusterTransportException) when (attempt < _options.Retries)
            {
                // The next of the part's owners runs it: its objects are immutable, the part idempotent.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>The objects of the version <paramref name="dataset"/> reads, by their rows: each one's first row, its end, and its identity.</summary>
    private static async ValueTask<(long First, long End, UInt128 Uid)[]> ObjectsAsync(VortexDataset dataset, CancellationToken cancellationToken)
    {
        List<(long First, long End, UInt128 Uid)> objects = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add((held.FirstRow, held.FirstRow + held.Entry.Rows, held.Entry.Uid));
        }

        return [.. objects];
    }

    /// <summary>The identity of the object that holds row <paramref name="row"/>; the row itself, as an identity, when none does.</summary>
    private static UInt128 Affinity((long First, long End, UInt128 Uid)[] objects, long row)
    {
        int low = 0;
        int high = objects.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (objects[middle].End <= row)
            {
                low = middle + 1;
            }
            else if (objects[middle].First > row)
            {
                high = middle - 1;
            }
            else
            {
                return objects[middle].Uid;
            }
        }

        return (UInt128)(ulong)row;
    }
}

/// <summary>
/// The nodes of a cluster in one process (spec F, 3): workers each with its own handles and their caches, which a test and
/// a machine of several logical nodes run. A node it does not hold is not reached.
/// </summary>
public sealed class LocalClusterTransport : IClusterTransport
{
    private readonly Dictionary<string, ClusterWorker> _workers = new Dictionary<string, ClusterWorker>(StringComparer.Ordinal);

    /// <summary>The transport over <paramref name="workers"/>, each by its name.</summary>
    public LocalClusterTransport(IEnumerable<ClusterWorker> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        foreach (ClusterWorker worker in workers)
        {
            _workers.Add(worker.Name, worker);
        }
    }

    /// <inheritdoc/>
    public ValueTask<AggregationPartial> RunAsync(string node, ClusterTask task, CancellationToken cancellationToken) =>
        Of(node).RunAsync(task, cancellationToken);

    /// <inheritdoc/>
    public ValueTask WarmAsync(string node, string location, ulong version, IReadOnlyList<string> nodes, CancellationToken cancellationToken) =>
        Of(node).WarmAsync(location, version, nodes, cancellationToken);

    private ClusterWorker Of(string node) =>
        _workers.TryGetValue(node, out ClusterWorker? worker) ? worker : throw new ClusterTransportException($"No node '{node}' runs in this process.");
}
