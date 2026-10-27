using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Dataset;

/// <summary>
/// What a cluster's node runs (spec F, 3): the parts a coordinator sends it, each the named question
/// (<see cref="ClusterQuestions"/>) over its rows of the version it names, read by a handle of that version the worker
/// keeps while parts ask for it, so that its caches serve the parts after; and the warming of the objects it owns
/// (spec F, 7).
/// </summary>
public sealed class ClusterWorker : IAsyncDisposable
{
    /// <summary>The handles a worker keeps open at most, the versions asked last: an older one is closed once its parts end.</summary>
    internal const int HeldVersions = 4;

    private readonly ClusterQuestions _questions;
    private readonly Func<string, ulong, CancellationToken, ValueTask<VortexDataset>> _open;
    private readonly Dictionary<(string Location, ulong Version), Held> _held = [];
    private readonly LinkedList<(string Location, ulong Version)> _order = new LinkedList<(string Location, ulong Version)>();
    private bool _disposed;

    /// <summary>A worker named <paramref name="name"/>, which opens each dataset by its location in the session of <paramref name="options"/>.</summary>
    public ClusterWorker(string name, ClusterQuestions questions, DatasetOptions? options = null)
        : this(name, questions, (location, version, cancellationToken) => VortexDataset.OpenAsync(location, version, options, cancellationToken))
    {
    }

    /// <summary>A worker named <paramref name="name"/>, which opens a dataset's version by <paramref name="open"/>.</summary>
    public ClusterWorker(string name, ClusterQuestions questions, Func<string, ulong, CancellationToken, ValueTask<VortexDataset>> open)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(open);
        Name = name;
        _questions = questions;
        _open = open;
    }

    /// <summary>The node's name, which the cluster's membership lists and the owners' hash reads.</summary>
    public string Name { get; }

    /// <summary>The partial of <paramref name="task"/>'s part: its question over its rows of its version.</summary>
    /// <exception cref="ArgumentException">No question has the task's name.</exception>
    public async ValueTask<AggregationPartial> RunAsync(ClusterTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        Held held = Take(task.Location, task.Version);
        try
        {
            VortexDataset dataset = await held.Opening.ConfigureAwait(false);
            return await _questions.PartialAsync(task.Question, dataset, task.Rows, task.Arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await LeaveAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the metadata of the objects this node owns among <paramref name="nodes"/> in <paramref name="version"/> of the
    /// dataset at <paramref name="location"/>: each one's table of contents is in hand, its sparse index and the marks of its
    /// columns read into the handle's memo, which the analysis of its parts reads (spec F, 7).
    /// </summary>
    public async ValueTask WarmAsync(string location, ulong version, IReadOnlyList<string> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        Held held = Take(location, version);
        try
        {
            VortexDataset dataset = await held.Opening.ConfigureAwait(false);
            int fields = dataset.DType.FieldCount;
            int[] all = new int[fields];
            for (int field = 0; field < fields; field++)
            {
                all[field] = field;
            }

            await foreach (PositionedObject owned in dataset.ScanBuilder().ObjectsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (owned.Entry.Contents is not { } contents || !string.Equals(Rendezvous.Owner(owned.Entry.Uid, nodes), Name, StringComparison.Ordinal))
                {
                    continue;
                }

                await dataset.KeyIndexAsync(owned.Entry, contents, cancellationToken).ConfigureAwait(false);
                await dataset.ReadMarksAsync(owned.Entry, all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await LeaveAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>Closes every handle; the parts running finish first.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Held> closing = [];
        lock (_held)
        {
            _disposed = true;
            foreach (Held held in _held.Values)
            {
                held.Evicted = true;
                if (held.Users == 0)
                {
                    closing.Add(held);
                }
            }

            _held.Clear();
            _order.Clear();
        }

        foreach (Held held in closing)
        {
            await CloseAsync(held).ConfigureAwait(false);
        }
    }

    /// <summary>The handle of <paramref name="version"/> at <paramref name="location"/>, opened at its first part, a user more; an old one past the most kept evicted.</summary>
    private Held Take(string location, ulong version)
    {
        ArgumentNullException.ThrowIfNull(location);
        lock (_held)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_held.TryGetValue((location, version), out Held? held))
            {
                held = new Held(_open(location, version, CancellationToken.None).AsTask());
                _held[(location, version)] = held;
                _order.AddLast((location, version));
                while (_order.Count > HeldVersions)
                {
                    (string, ulong) oldest = _order.First!.Value;
                    _order.RemoveFirst();
                    if (_held.Remove(oldest, out Held? evicted))
                    {
                        evicted.Evicted = true;
                        if (evicted.Users == 0)
                        {
                            _ = CloseAsync(evicted).AsTask();
                        }
                    }
                }
            }

            held.Users++;
            return held;
        }
    }

    /// <summary>A user fewer of <paramref name="held"/>, closed when it was evicted and no part reads it any more.</summary>
    private async ValueTask LeaveAsync(Held held)
    {
        bool close;
        lock (_held)
        {
            held.Users--;
            close = held.Evicted && held.Users == 0;
        }

        if (close)
        {
            await CloseAsync(held).ConfigureAwait(false);
        }
    }

    private static async ValueTask CloseAsync(Held held)
    {
        try
        {
            // The handle was opened for a part of this worker, which awaited it before leaving.
#pragma warning disable VSTHRD003
            VortexDataset dataset = await held.Opening.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await dataset.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A handle that never opened has nothing to close; the part that asked for it saw why.
        }
    }

    /// <summary>A handle of a version, its opening, the parts reading it, and whether it left the handles kept.</summary>
    private sealed class Held(Task<VortexDataset> opening)
    {
        internal Task<VortexDataset> Opening { get; } = opening;

        internal int Users { get; set; }

        internal bool Evicted { get; set; }
    }
}
