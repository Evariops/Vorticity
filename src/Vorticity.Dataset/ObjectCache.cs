// The cache of open data objects - docs/13-dataset.md §3: a data object is immutable, "a version of
// the bytes, never of the file", so a handle on one can never go stale and re-opening it can only
// cost what it cost the first time.
//
// WHAT IT ACTUALLY SAVES, and it is not bytes. Opening a Vortex file is a 64 KiB tail read and the
// parse of a footer (02 §3): one dependent round trip and some CPU, per object, per scan. A dataset
// scan that answers a hundred point queries over eight objects pays that eight hundred times
// without a cache and eight times with one. §9.1 counts dependent round trips and nothing else,
// which is the reason this exists and the reason it is keyed by object rather than by anything finer
// -- by object and the index fragments it is opened with, since step 42b, because an object's bytes
// never change and its fragments do.
//
// REFERENCE-COUNTED, NOT JUST LRU, because two scans may hold the same object at once and one of
// them finishing must not close the file the other is reading. An object with no lease goes to the
// idle list and is closed only when a later open pushes the cache over its capacity, oldest first.
//
// ONE OPEN AT A TIME, under the gate, and that is a deliberate simplification rather than an
// oversight: it makes a double open impossible without a second map of in-flight opens, and a
// dataset scan is sequential anyway. A reader that wants objects opened in parallel wants the
// prefetch of §6.6 ("children prefetched in parallel"), which is a different thing and is not here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;

namespace Vorticity.Dataset;

/// <summary>Keeps a bounded number of the dataset's data objects open.</summary>
internal sealed class ObjectCache : IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly int _capacity;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, Held> _open = new Dictionary<string, Held>(StringComparer.Ordinal);
    private readonly LinkedList<string> _idle = new LinkedList<string>();
    private bool _disposed;

    internal ObjectCache(IObjectStore store, int capacity)
    {
        _store = store;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>How many objects are open, leased or idle.</summary>
    internal int Count => _open.Count;

    /// <summary>How many rents were answered without opening anything.</summary>
    internal long Hits { get; private set; }

    /// <summary>How many rents had to open the object.</summary>
    internal long Misses { get; private set; }

    /// <summary>
    /// Opens an object with the index fragments its entry names, or hands back the handle already
    /// open on that view of it.
    /// </summary>
    /// <param name="entry">The object's leaf entry: its key, and the fragments attached to it (§6.4).</param>
    /// <param name="pages">Where the fragments are read: the commit objects that wrote them.</param>
    /// <param name="cancellationToken">Cancels the reads and the open.</param>
    /// <returns>A lease the caller disposes when it is done reading.</returns>
    internal async ValueTask<ObjectLease> RentAsync(
        ObjectEntry entry, CommitPageSource pages, CancellationToken cancellationToken)
    {
        string handle = HandleOf(entry);
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

            Misses++;
            // A compacted fragment is a bundle of containers (§6.4): each goes to the reader as the
            // fragment it was. One whose bytes are not what its reference says is LEFT OUT, the rule
            // of a region that fails its checksum (§7): an index claims nothing it cannot prove, and
            // the object answers exactly without it. `verify` names it. A missing commit object is
            // not that: the version is gone, and the read says so.
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

            VortexOpenOptions options = fragments.Count == 0
                ? new VortexOpenOptions()
                : new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = fragments } };
            ObjectSegmentSource source = new ObjectSegmentSource(_store, entry.Key);
            VortexFile file;
            try
            {
                file = await VortexFile.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await source.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _open[handle] = new Held(file, source) { Leases = 1, FragmentRefusals = refused };
            await EvictAsync().ConfigureAwait(false);
            return new ObjectLease(this, handle, file, cached: false) { FragmentRefusals = refused };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gives a lease back; the object stays open until something else needs the room.</summary>
    /// <param name="key">The object's key.</param>
    /// <returns>When the release, and any eviction it allowed, is done.</returns>
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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

    /// <summary>What one open view of an object is kept under: the object, and the fragments it holds.</summary>
    /// <param name="entry">The object's leaf entry.</param>
    /// <returns>The object's key, followed by its fragments' hashes when it has any.</returns>
    /// <remarks>
    /// A DATA OBJECT IS IMMUTABLE, ITS FRAGMENTS ARE NOT (§6.4). An indexer attaches one in a later
    /// commit, and a file reads its index directory once and keeps it; so a new set of fragments is a
    /// new view of the same bytes — a second open, never a stale handle — and the view it replaces
    /// ages out of the cache like any idle object.
    /// </remarks>
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

    /// <summary>Closes idle objects, oldest first, until the cache is within its capacity.</summary>
    /// <remarks>
    /// A cache full of LEASED objects stays over capacity rather than closing a file someone is
    /// reading: the capacity is a target, and the only thing that would make it a hard limit is
    /// making a reader wait, which trades a bounded amount of memory for an unbounded latency.
    /// </remarks>
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

    /// <inheritdoc/>
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
