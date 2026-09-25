using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;

namespace Vorticity.Dataset;

/// <summary>
/// Keeps a bounded number of the dataset's data objects open, saving the tail read and footer parse
/// an open costs. Leases are reference-counted, since two scans may hold the same object and one of
/// them finishing must not close the file the other is reading. Opens run outside the gate, side by
/// side for different objects, and a rent that finds its object being opened waits for that open
/// rather than starting another.
/// </summary>
internal sealed class ObjectCache : IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly int _capacity;
    private readonly VortexSession _session;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, Held> _open = new Dictionary<string, Held>(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Held>> _opening = new Dictionary<string, Task<Held>>(StringComparer.Ordinal);
    private readonly LinkedList<string> _idle = new LinkedList<string>();
    private bool _disposed;

    internal ObjectCache(IObjectStore store, int capacity, VortexSession? session = null)
    {
        _store = store;
        _capacity = Math.Max(1, capacity);
        _session = session ?? VortexSession.Default;
    }

    /// <summary>
    /// How a data object opens. An object is written once and never appended to, so an end that does
    /// not parse is a corrupt object, not a torn append with a whole version before it to walk back to.
    /// </summary>
    internal static VortexOpenOptions OpenOptions { get; } = new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse };

    /// <summary>How many objects are open, leased or idle.</summary>
    internal int Count => _open.Count;

    /// <summary><see cref="OpenOptions"/> with the index fragments an object's entry names.</summary>
    internal static VortexOpenOptions OpenOptionsWith(List<ReadOnlyMemory<byte>> fragments) =>
        fragments.Count == 0 ? OpenOptions : OpenOptions with { Read = new VortexReadOptions { IndexFragments = fragments } };

    /// <summary>How many rents were answered without opening anything.</summary>
    internal long Hits { get; private set; }

    /// <summary>How many rents had to open the object.</summary>
    internal long Misses { get; private set; }

    /// <summary>
    /// Opens an object with the index fragments its entry names, or hands back the handle already
    /// open on that view of it.
    /// The caller disposes the lease when it is done reading.
    /// </summary>
    internal async ValueTask<ObjectLease> RentAsync(
        ObjectEntry entry, CommitPageSource pages, CancellationToken cancellationToken)
    {
        string handle = HandleOf(entry);
        while (true)
        {
            TaskCompletionSource<Held>? mine = null;
            Task<Held>? theirs;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_open.TryGetValue(handle, out Held? held))
                {
                    Hits++;
                    held.Leases++;
                    if (held.Idle is { } node)
                    {
                        _idle.Remove(node);
                        held.Idle = null;
                    }

                    return new ObjectLease(this, handle, held.File, cached: true) { FragmentRefusals = held.FragmentRefusals };
                }

                if (!_opening.TryGetValue(handle, out theirs))
                {
                    Misses++;
                    mine = new TaskCompletionSource<Held>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _opening[handle] = mine.Task;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (mine is not null)
            {
                return await OpenAsync(entry, pages, handle, mine, cancellationToken).ConfigureAwait(false);
            }

            // Another rent is opening this object, and shares it once open. An open cancelled by its
            // own caller is not this rent's failure: it tries again.
            try
            {
                await theirs!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Opens an object outside the gate, then publishes it to the rents waiting on it.</summary>
    private async ValueTask<ObjectLease> OpenAsync(
        ObjectEntry entry, CommitPageSource pages, string handle, TaskCompletionSource<Held> opening, CancellationToken cancellationToken)
    {
        Held held;
        try
        {
            held = await OpenHeldAsync(entry, pages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _opening.Remove(handle);
            }
            finally
            {
                _gate.Release();
            }

            opening.TrySetException(failure);
            throw;
        }

        bool disposed;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _opening.Remove(handle);
            disposed = _disposed;
            if (!disposed)
            {
                _open[handle] = held;
                await EvictAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (disposed)
        {
            await CloseAsync(held).ConfigureAwait(false);
            ObjectDisposedException closed = new ObjectDisposedException(nameof(ObjectCache));
            opening.TrySetException(closed);
            throw closed;
        }

        opening.TrySetResult(held);
        return new ObjectLease(this, handle, held.File, cached: false) { FragmentRefusals = held.FragmentRefusals };
    }

    /// <summary>Opens an object with the index fragments its entry names, leased once to the caller.</summary>
    private async ValueTask<Held> OpenHeldAsync(ObjectEntry entry, CommitPageSource pages, CancellationToken cancellationToken)
    {
        // A fragment whose bytes are not what its reference says is left out rather than fatal: an
        // index claims nothing it cannot prove, and the object answers exactly without it. A missing
        // commit object is not that -- the version is gone, and the read says so.
        List<ReadOnlyMemory<byte>> fragments = [];
        List<string> refused = [];
        foreach (PageReference reference in entry.Fragments)
        {
            ReadOnlyMemory<byte> fragment;
            try
            {
                fragment = await pages.ReadFragmentAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (CommitFormatException torn)
            {
                refused.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the fragment in version {reference.Version} at {reference.Offset}+{reference.Length}: {torn.Message}"));
                continue;
            }

            fragments.AddRange(FragmentBundle.Unpack(fragment));
        }

        // Through the session, so the store sees at most its reads in flight and a segment read once
        // is served from its cache to every scan that asks again.
        ObjectSegmentSource source = new ObjectSegmentSource(_store, entry.Key);
        try
        {
            VortexFile file = await VortexFile.OpenAsync(SessionReader.Wrap(source, _session), OpenOptionsWith(fragments), cancellationToken).ConfigureAwait(false);
            file.Session = _session;
            return new Held(file, source) { Leases = 1, FragmentRefusals = refused };
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gives a lease back; the object stays open until something else needs the room.</summary>
    internal async ValueTask ReturnAsync(string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_open.TryGetValue(key, out Held? held))
            {
                return;
            }

            held.Leases--;
            if (held.Leases <= 0 && held.Idle is null)
            {
                held.Idle = _idle.AddLast(key);
            }

            await EvictAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] opening;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            opening = [.. _opening.Values];
        }
        finally
        {
            _gate.Release();
        }

        // An open under way publishes nothing once the cache is disposed: it closes what it opened,
        // and needs the gate until it has.
        await Task.WhenAll(opening).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (Held held in _open.Values)
            {
                await CloseAsync(held).ConfigureAwait(false);
            }

            _open.Clear();
            _idle.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// What one open view of an object is kept under: its key, followed by its fragments' hashes.
    /// An object's bytes are immutable but its fragments are not, so a new set of fragments is a new
    /// view of the same bytes -- a second open, never a stale handle.
    /// </summary>
    private static string HandleOf(ObjectEntry entry)
    {
        if (entry.Fragments.Count == 0)
        {
            return entry.Key;
        }

        StringBuilder handle = new StringBuilder(entry.Key);
        foreach (PageReference fragment in entry.Fragments)
        {
            handle.Append('#').Append(fragment.Hash.ToString("x32", CultureInfo.InvariantCulture));
        }

        return handle.ToString();
    }

    /// <summary>
    /// Closes idle objects, oldest first, until the cache is within its capacity. A cache full of
    /// leased objects stays over capacity rather than close a file someone is reading: the capacity
    /// is a target, and making it a hard limit would trade bounded memory for unbounded latency.
    /// </summary>
    private async ValueTask EvictAsync()
    {
        while (_open.Count > _capacity && _idle.First is { } oldest)
        {
            _idle.RemoveFirst();
            if (_open.Remove(oldest.Value, out Held? held))
            {
                held.Idle = null;
                await CloseAsync(held).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask CloseAsync(Held held)
    {
        await held.File.DisposeAsync().ConfigureAwait(false);
        await held.Source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Held(VortexFile file, ObjectSegmentSource source)
    {
        internal VortexFile File { get; } = file;

        internal ObjectSegmentSource Source { get; } = source;

        internal int Leases { get; set; }

        internal LinkedListNode<string>? Idle { get; set; }

        internal IReadOnlyList<string> FragmentRefusals { get; init; } = [];
    }
}

/// <summary>One borrowing of an open data object.</summary>
internal sealed class ObjectLease(ObjectCache cache, string key, VortexFile file, bool cached) : IAsyncDisposable
{
    private bool _returned;

    /// <summary>The open file. Valid until this lease is disposed.</summary>
    internal VortexFile File => file;

    /// <summary>Whether the object was already open, so the lease cost no request.</summary>
    internal bool WasCached => cached;

    /// <summary>
    /// The fragments left out because their bytes are not what their references say; the file's own
    /// <see cref="VortexFile.IndexFragmentRefusals"/> covers the fragments it was given.
    /// </summary>
    internal IReadOnlyList<string> FragmentRefusals { get; init; } = [];

    public ValueTask DisposeAsync()
    {
        if (_returned)
        {
            return ValueTask.CompletedTask;
        }

        _returned = true;
        return cache.ReturnAsync(key);
    }
}
