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
    /// <summary>
    /// What an open object keeps besides the tail it read, per byte of its footer: its layout, types, statistics and zone
    /// maps, parsed as questions ask them. Measured at 5 to 7 (2026-11-10): 14.2 KB for a footer of 2.9 KB, 27 KB for one
    /// of 3.9 KB.
    /// </summary>
    internal const int ParsedPerFooterByte = 6;

    private readonly IObjectStore _store;
    private readonly int _capacity;
    private readonly long _byteCapacity;
    private readonly VortexSession _session;
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<string, Held> _open = new Dictionary<string, Held>(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Held>> _opening = new Dictionary<string, Task<Held>>(StringComparer.Ordinal);
    // The objects no lease holds, oldest first: linked through the objects themselves, so that one going idle makes no node.
    private Held? _idleOldest;
    private Held? _idleNewest;
    private int _idleCount;
    // What the open objects weigh (Held.Weight), leased or idle.
    private long _heldBytes;
    private bool _disposed;

    // The bytes of an object up to which a read of its rows opens it whole (WholeBytesUnder).
    private readonly long _wholeBytes;

    internal ObjectCache(
        IObjectStore store, int capacity, VortexSession? session = null, long byteCapacity = long.MaxValue, long wholeBytes = 0)
    {
        _store = store;
        _capacity = Math.Max(1, capacity);
        _byteCapacity = Math.Max(0, byteCapacity);
        _session = session ?? VortexSession.Default;
        _wholeBytes = wholeBytes;
    }

    /// <summary>
    /// The bytes of an object up to which a read of its rows opens it whole, where requests cost what
    /// <paramref name="profile"/> says: the tail an open reads, and the gap the profile reads two ranges across as one. A
    /// read of rows asks for the tail, then for the data ahead of it. An object that goes past the tail by no more than
    /// that gap costs less read whole than a second request: 320 KiB at the remote tier, and no more than the tail
    /// in memory, where a request costs nothing.
    /// </summary>
    internal static long WholeBytesUnder(CostProfile profile) => VortexOpenOptions.DefaultInitialReadBytes + (long)profile.JoinedGap;

    /// <summary>What the open objects weigh, leased or idle (<see cref="Held.Weight"/>).</summary>
    internal long HeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _heldBytes;
            }
        }
    }

    /// <summary>
    /// How a data object opens. An object is written once and never appended to, so an end that does
    /// not parse is a corrupt object, not a torn append with a whole version before it to walk back to.
    /// </summary>
    internal static VortexOpenOptions OpenOptions { get; } = new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse };

    /// <summary>The entries the handle's walks parsed, shared by every version that holds their pages.</summary>
    internal EntryMemo Entries { get; } = new EntryMemo();

    /// <summary>How many open objects a lease holds: none once every read has given its objects back.</summary>
    internal int Leased => _open.Count - _idleCount;

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
    /// then reads the object's footer in one request where it would take two. An object of
    /// <paramref name="wholeBytes"/> at most is read whole, its rows with its footer.
    /// </summary>
    internal static VortexOpenOptions OpenOptionsOf(ObjectEntry entry, List<ReadOnlyMemory<byte>> fragments, long wholeBytes = 0)
    {
        VortexOpenOptions options = OpenOptionsWith(fragments);
        if (entry.Bytes > VortexOpenOptions.DefaultInitialReadBytes && entry.Bytes <= Math.Min(wholeBytes, int.MaxValue))
        {
            return options with { InitialReadBytes = (int)entry.Bytes };
        }

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
    /// The caller disposes the lease when it is done reading. A caller that will read the object's rows says so
    /// (<paramref name="rows"/>): an open then reads whole an object that goes little past its tail
    /// (<see cref="WholeBytesUnder"/>), where the rows would cost a second request. An object open already is handed
    /// back as it is.
    /// </summary>
    internal async ValueTask<ObjectLease> RentAsync(
        ObjectEntry entry, CommitPageSource pages, CancellationToken cancellationToken, bool rows = false)
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
                    if (held.IsIdle)
                    {
                        Unidle(held);
                    }

                    return new ObjectLease(this, held, cached: true);
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
                return await OpenAsync(entry, pages, handle, mine, rows, cancellationToken).ConfigureAwait(false);
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
        ObjectEntry entry, CommitPageSource pages, string handle, TaskCompletionSource<Held> opening, bool rows, CancellationToken cancellationToken)
    {
        Held held;
        try
        {
            held = await OpenHeldAsync(entry, pages, handle, rows, cancellationToken).ConfigureAwait(false);
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
                _heldBytes += held.Weight;
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
        return new ObjectLease(this, held, cached: false);
    }

    /// <summary>Opens an object with the index fragments its entry names, leased once to the caller.</summary>
    private async ValueTask<Held> OpenHeldAsync(ObjectEntry entry, CommitPageSource pages, string handle, bool rows, CancellationToken cancellationToken)
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
            VortexOpenOptions options = OpenOptionsOf(entry, fragments, rows ? _wholeBytes : 0);
            VortexFile file = await VortexFile.OpenAsync(SessionReader.Wrap(read, _session), options, cancellationToken).ConfigureAwait(false);
            file.Session = _session;

            // What it keeps while open: the tail it read, and what it parses of its footer (ParsedPerFooterByte).
            long weight = file.RetainedTailBytes + ((long)ParsedPerFooterByte * file.FooterBytes);
            return new Held(file, source, handle) { Leases = 1, FragmentRefusals = refused, Weight = weight };
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Gives a lease of <paramref name="held"/> back; the object stays open until something else needs the room. The lease
    /// holds the object itself: no lookup by its handle, no closure over it, and no node to make as it goes idle.
    /// </summary>
    internal ValueTask ReturnAsync(Held held)
    {
        List<Held>? closing;
        lock (_gate)
        {
            // An object the cache let go, once disposed, is closed already.
            if (held.Gone)
            {
                return ValueTask.CompletedTask;
            }

            held.Leases--;
            if (held.Leases <= 0 && !held.IsIdle)
            {
                Idle(held);
            }

            closing = Evict();
        }

        return closing is null ? ValueTask.CompletedTask : CloseAsync(closing);
    }

    /// <summary>Puts <paramref name="held"/> at the newest end of the idle objects, under the gate.</summary>
    private void Idle(Held held)
    {
        held.IsIdle = true;
        held.IdleOlder = _idleNewest;
        held.IdleNewer = null;
        if (_idleNewest is { } newest)
        {
            newest.IdleNewer = held;
        }
        else
        {
            _idleOldest = held;
        }

        _idleNewest = held;
        _idleCount++;
    }

    /// <summary>Takes <paramref name="held"/> out of the idle objects, under the gate.</summary>
    private void Unidle(Held held)
    {
        if (held.IdleOlder is { } older)
        {
            older.IdleNewer = held.IdleNewer;
        }
        else
        {
            _idleOldest = held.IdleNewer;
        }

        if (held.IdleNewer is { } newer)
        {
            newer.IdleOlder = held.IdleOlder;
        }
        else
        {
            _idleNewest = held.IdleOlder;
        }

        held.IdleOlder = null;
        held.IdleNewer = null;
        held.IsIdle = false;
        _idleCount--;
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
            foreach (Held held in open)
            {
                held.Gone = true;
                held.IdleOlder = null;
                held.IdleNewer = null;
                held.IsIdle = false;
            }

            _open.Clear();
            _idleOldest = null;
            _idleNewest = null;
            _idleCount = 0;
            _heldBytes = 0;
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

        // Kept on the entry, which the walks share: the same string whichever rent makes it first.
        if (entry.OpenHandle is { } known)
        {
            return known;
        }

        StringBuilder handle = new StringBuilder(entry.Key);
        foreach (PageReference fragment in entry.Fragments)
        {
            handle.Append('#').Append(fragment.Hash.ToString("x32", CultureInfo.InvariantCulture));
        }

        string made = handle.ToString();
        entry.OpenHandle = made;
        return made;
    }

    /// <summary>
    /// Takes idle objects out, oldest first, until the cache is within its capacity, in objects and in bytes, under the
    /// gate: those to close, which the caller closes once it has let the gate go; null for none. A cache full of leased
    /// objects stays over capacity rather than close a file someone is reading: the capacity is a target, and making it a
    /// hard limit would trade bounded memory for unbounded latency.
    /// </summary>
    private List<Held>? Evict()
    {
        List<Held>? closing = null;
        while ((_open.Count > _capacity || _heldBytes > _byteCapacity) && _idleOldest is { } oldest)
        {
            Unidle(oldest);
            _open.Remove(oldest.Handle);
            _heldBytes -= oldest.Weight;
            oldest.Gone = true;
            (closing ??= []).Add(oldest);
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

    /// <summary>One open object: its file, its reader, its handle, its leases, and its place among the idle objects.</summary>
    internal sealed class Held(VortexFile file, ISegmentReader source, string handle)
    {
        internal VortexFile File { get; } = file;

        internal ISegmentReader Source { get; } = source;

        /// <summary>What the cache keeps it under (<see cref="HandleOf"/>).</summary>
        internal string Handle { get; } = handle;

        internal int Leases { get; set; }

        /// <summary>Whether no lease holds it, which puts it among the idle objects.</summary>
        internal bool IsIdle { get; set; }

        /// <summary>The idle object that went idle before it, or null.</summary>
        internal Held? IdleOlder { get; set; }

        /// <summary>The idle object that went idle after it, or null.</summary>
        internal Held? IdleNewer { get; set; }

        /// <summary>Whether the cache let it go, evicted or disposed: a lease given back after has nothing to return to.</summary>
        internal bool Gone { get; set; }

        internal IReadOnlyList<string> FragmentRefusals { get; init; } = [];

        /// <summary>What it keeps while open, as the cache bounds it: the tail its open read, and its footer parsed (<see cref="ParsedPerFooterByte"/>).</summary>
        internal long Weight { get; init; }
    }
}

/// <summary>One borrowing of an open data object.</summary>
internal sealed class ObjectLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _giveBack;
    private readonly ObjectCache? _cache;
    private readonly ObjectCache.Held? _held;
    private bool _returned;

    /// <summary>A lease of the object <paramref name="cache"/> holds open as <paramref name="held"/>, which disposing gives back.</summary>
    internal ObjectLease(ObjectCache cache, ObjectCache.Held held, bool cached)
    {
        File = held.File;
        WasCached = cached;
        FragmentRefusals = held.FragmentRefusals;
        _cache = cache;
        _held = held;
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
        return _cache is not null ? _cache.ReturnAsync(_held!) : _giveBack!();
    }
}
