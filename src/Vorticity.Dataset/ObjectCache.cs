using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// Keeps a bounded number of the dataset's data objects open, saving the tail read and footer parse
/// an open costs. Leases are reference-counted, since two scans may hold the same object and one of
/// them finishing must not close the file the other is reading. Opens run outside the gate, side by
/// side for different objects, and a rent that finds its object being opened waits for that open
/// rather than starting another.
/// </summary>
/// <remarks>
/// The gate is a lock held for the dictionaries' work alone, never across an await: opens and closes
/// run outside it. Every rent and return of every question takes it, some 30 s of waiting in a 15 s run
/// at 32 clients when it was a semaphore, waited for asynchronously by those and synchronously by
/// <see cref="Holds"/>, whose blocked threads could hold up the continuation of the waiter the
/// semaphore had passed to (2026-11-08).
/// </remarks>
internal sealed class ObjectCache : IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly int _capacity;
    private readonly VortexSession _session;
    private readonly Lock _gate = new Lock();
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

    /// <summary>The entries the handle's walks parsed, shared by every version that holds their pages.</summary>
    internal EntryMemo Entries { get; } = new EntryMemo();

    /// <summary>How many open objects a lease holds: none once every read has given its objects back.</summary>
    internal int Leased => _open.Count - _idle.Count;

    /// <summary>
    /// <see cref="OpenOptions"/> with the index fragments an object's entry names, which the dataset
    /// vouches for: they come from its commits, sealed whole when it is encrypted, and a session that
    /// refuses plaintext opens no other.
    /// </summary>
    internal static VortexOpenOptions OpenOptionsWith(List<ReadOnlyMemory<byte>> fragments) =>
        fragments.Count == 0
            ? OpenOptions
            : OpenOptions with { Read = new VortexReadOptions { IndexFragments = fragments, IndexFragmentsAuthenticated = true } };

    /// <summary>
    /// <see cref="OpenOptionsWith"/>, with the footer's length when the entry records one: the open
    /// then reads the object's footer in one request where it would take two.
    /// </summary>
    internal static VortexOpenOptions OpenOptionsOf(ObjectEntry entry, List<ReadOnlyMemory<byte>> fragments)
    {
        VortexOpenOptions options = OpenOptionsWith(fragments);
        return entry.FooterBytes > 0
            ? options with { InitialReadBytes = (int)Math.Min(entry.FooterBytes, int.MaxValue) }
            : options;
    }

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
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<Held>? mine = null;
            Task<Held>? theirs;
            lock (_gate)
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
            lock (_gate)
            {
                _opening.Remove(handle);
            }

            opening.TrySetException(failure);
            throw;
        }

        bool disposed;
        lock (_gate)
        {
            _opening.Remove(handle);
            disposed = _disposed;
            if (!disposed)
            {
                // No eviction here, only at a return: a walk that rents the objects in the same
                // order every time would close, at each open, the idle object it rents next.
                _open[handle] = held;
            }
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
        // is served from its cache to every scan that asks again. An encrypted dataset's object is
        // opened through a sealed reader, its object id checked against the entry's uid.
        // A plain object's regions of indexes, those its table of contents declares and the pages and segments its lookups
        // name, and its data when the cache keeps data, come from the session's synopsis cache as they do with no open: a lookup through an open object asked the
        // store for its fence page and segments at every question.
        ISegmentReader source = await SealedObjectStore.OpenDataAsync(_store, entry.Key, entry.Uid, entry.Bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            ISegmentReader read = source is ObjectStoreSource
                ? SynopsisCacheReader.Over(source, _session.Options.SynopsisCache, entry.Uid, entry.Contents)
                : source;
            VortexFile file = await VortexFile.OpenAsync(SessionReader.Wrap(read, _session), OpenOptionsOf(entry, fragments), cancellationToken).ConfigureAwait(false);
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
    internal ValueTask ReturnAsync(string key)
    {
        List<Held>? closing;
        lock (_gate)
        {
            if (!_open.TryGetValue(key, out Held? held))
            {
                return ValueTask.CompletedTask;
            }

            held.Leases--;
            if (held.Leases <= 0 && held.Idle is null)
            {
                held.Idle = _idle.AddLast(key);
            }

            closing = Evict();
        }

        return closing is null ? ValueTask.CompletedTask : CloseAsync(closing);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] opening;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            opening = [.. _opening.Values];
        }

        // An open under way publishes nothing once the cache is disposed: it closes what it opened
        // before its rents hear of it.
        await Task.WhenAll(opening).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        List<Held> open;
        lock (_gate)
        {
            open = [.. _open.Values];
            _open.Clear();
            _idle.Clear();
        }

        await CloseAsync(open).ConfigureAwait(false);
    }

    /// <summary>
    /// What one open view of an object is kept under: its key, followed by its fragments' hashes.
    /// An object's bytes are immutable but its fragments are not, so a new set of fragments is a new
    /// view of the same bytes -- a second open, never a stale handle.
    /// </summary>
    /// <summary>Whether the object <paramref name="entry"/> names is open on the view its fragments make, or being opened: a rent of it reads nothing more.</summary>
    internal bool Holds(ObjectEntry entry)
    {
        string handle = HandleOf(entry);
        lock (_gate)
        {
            return _open.ContainsKey(handle) || _opening.ContainsKey(handle);
        }
    }

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
    /// Takes idle objects out, oldest first, until the cache is within its capacity, under the gate:
    /// those to close, which the caller closes once it has let the gate go; null for none. A cache full
    /// of leased objects stays over capacity rather than close a file someone is reading: the capacity
    /// is a target, and making it a hard limit would trade bounded memory for unbounded latency.
    /// </summary>
    private List<Held>? Evict()
    {
        List<Held>? closing = null;
        while (_open.Count > _capacity && _idle.First is { } oldest)
        {
            _idle.RemoveFirst();
            if (_open.Remove(oldest.Value, out Held? held))
            {
                held.Idle = null;
                (closing ??= []).Add(held);
            }
        }

        return closing;
    }

    private static async ValueTask CloseAsync(List<Held> closing)
    {
        foreach (Held held in closing)
        {
            await CloseAsync(held).ConfigureAwait(false);
        }
    }

    private static async ValueTask CloseAsync(Held held)
    {
        await held.File.DisposeAsync().ConfigureAwait(false);
        await held.Source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Held(VortexFile file, ISegmentReader source)
    {
        internal VortexFile File { get; } = file;

        internal ISegmentReader Source { get; } = source;

        internal int Leases { get; set; }

        internal LinkedListNode<string>? Idle { get; set; }

        internal IReadOnlyList<string> FragmentRefusals { get; init; } = [];
    }
}

/// <summary>One borrowing of an open data object.</summary>
internal sealed class ObjectLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _giveBack;
    private bool _returned;

    /// <summary>A lease of the object <paramref name="key"/> names, open as <paramref name="file"/>, which disposing gives back to <paramref name="cache"/>.</summary>
    internal ObjectLease(ObjectCache cache, string key, VortexFile file, bool cached)
        : this(file, cached, () => cache.ReturnAsync(key))
    {
    }

    /// <summary>A lease of <paramref name="file"/>, which disposing gives back through <paramref name="giveBack"/>.</summary>
    internal ObjectLease(VortexFile file, bool cached, Func<ValueTask> giveBack)
    {
        File = file;
        WasCached = cached;
        _giveBack = giveBack;
    }

    /// <summary>The open file. Valid until this lease is disposed.</summary>
    internal VortexFile File { get; }

    /// <summary>Whether the object was already open, so the lease cost no request.</summary>
    internal bool WasCached { get; }

    /// <summary>
    /// Whether <see cref="File"/> is a copy of the object written again, the rows in their order but its chunks its own
    /// (<see cref="LevelZeroMemo"/>), rather than the object as it lies: the regions its entry's table of contents names, its
    /// marks and its sparse index, lie in the object, not in the copy, which is read whole.
    /// </summary>
    internal bool IsCopy { get; init; }

    /// <summary>
    /// The fragments left out because their bytes are not what their references say; the file's own
    /// <see cref="VortexFile.IndexFragmentRefusals"/> covers the fragments it was given.
    /// </summary>
    internal IReadOnlyList<string> FragmentRefusals { get; init; } = [];

    // The question that keeps what it reads of the file while this lease lasts, told when it ends.
    private Indexes.QuestionReads? _question;

    /// <summary>
    /// Makes <paramref name="question"/> keep what it reads of <see cref="File"/> until this lease is given back, and no longer
    /// (<see cref="Indexes.QuestionReads.Hold"/>): this lease.
    /// </summary>
    internal ObjectLease HeldBy(Indexes.QuestionReads question)
    {
        question.Hold(File);
        _question = question;
        return this;
    }

    public ValueTask DisposeAsync()
    {
        if (_returned)
        {
            return ValueTask.CompletedTask;
        }

        _returned = true;
        _question?.Release(File);
        return _giveBack();
    }
}
