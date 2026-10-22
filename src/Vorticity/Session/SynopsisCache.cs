using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Indexes;

namespace Vorticity;

/// <summary>
/// A cache on the local disk of what a question reads of a dataset's objects without the rest of their
/// data: the tables of their synopses, their slices' chunk directories, their index directories, their
/// variant columns' path directories, and the pages and segments of their indexes' runs a lookup reads;
/// and the pages of the dataset's tree, which say what objects it holds. A question over a month answers
/// from the disk once those regions have been read, and makes no request to the store for them.
/// </summary>
/// <remarks>
/// <para>
/// An object never changes, so an entry needs no invalidation: its key is the object's uid, the region's
/// place in it and the region's checksum, all known before it is read. Only a region of an index enters the
/// cache, never the data nor the footer: one its object's table of contents declares, with a checksum, or a
/// page or a segment of a run its reader names with the checksum its directory or its parent page gave.
/// Every read from the disk checks the checksum, and a region that fails it is dropped and read again from
/// the store.
/// </para>
/// <para>
/// A page of a dataset's tree is kept by the XXH3-128 hash of its bytes, which the page or the header naming
/// it gives: a page never changes, and every version that names it holds the same bytes, so a handle opened
/// later, whose own pages start empty, reads the tree from the disk. Its reader checks the hash at each read.
/// A sealed dataset's pages stay off the disk: they are plaintext of its commit objects.
/// </para>
/// <para>
/// A region read from the store enters the cache behind the question that read it: the buffer its reader
/// read it into is held until a writer of the cache's own has written it, and a read of the region meanwhile
/// is served from that buffer. A file costs about 1.7 ms to create, close and rename on Windows, which the
/// question paid for each region it read: past a quarter of a second of a cold question that read 104 MB of
/// tables. Past <see cref="MaxPendingBytes"/> waiting, a region is written before its reader goes on. A page of
/// a tree waits so too, its array its own, which never changes. A sealed object's frames are written at once.
/// </para>
/// <para>
/// A sealed object leaves no plaintext on the disk. The cache keeps its trailer and the frames that hold a
/// region its table of contents declares, the bytes its store holds, and its reader decrypts them at each
/// read: a frame whose tag fails, or a region whose plaintext fails its checksum, is dropped and read again
/// from the store. No key is kept: the session unwraps the object's data key as it does for a read from the
/// store.
/// </para>
/// <para>
/// An entry is a file, in directories sharded by the object's uid, written under a temporary name and
/// then renamed, so a reader never sees half a file. The cache walks its directory when it opens to know
/// what it holds. One cache owns a directory, through a lock of the operating system: a second one, in
/// this process or another, is refused. Questions that miss the same region of a plain object at once wait
/// for one read. Those that miss the same frames of a sealed object at once read them each.
/// </para>
/// <para>
/// Past its capacity, the cache drops entries as S3-FIFO does, by their bytes: a new entry waits in a small
/// queue, a tenth of the capacity, and only one read again there moves on to the main queue, so a sweep of a
/// long window passes through without pushing
/// out what is read often. An entry dropped from the small queue and asked again soon goes straight to the
/// main queue. A replay of question traces chose it over SIEVE and W-TinyLFU
/// (<c>vortex-queries --micro cachetrace</c>).
/// </para>
/// </remarks>
public sealed class SynopsisCache : IDisposable
{
    private const string LockName = ".lock";
    private const string TemporaryExtension = ".tmp";

    /// <summary>The reads an entry counts at most, which the main queue gives back one at a time.</summary>
    private const int MaxHits = 3;

    /// <summary>The files the cache keeps open at most, the least recently opened closed first.</summary>
    private const int MaxOpenHandles = 4096;

    /// <summary>The bytes of regions waiting for their write at most, past which a region is written before its reader goes on.</summary>
    private const long MaxPendingBytes = 256L << 20;

    /// <summary>The writers of the regions waiting that run at once at most.</summary>
    private const int MaxWriters = 2;

    private readonly FileStream _lock;
    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _small = new LinkedList<Entry>();
    private readonly LinkedList<Entry> _main = new LinkedList<Entry>();
    private readonly LinkedList<Entry> _open = new LinkedList<Entry>();
    private readonly LinkedList<(string Name, long Bytes)> _ghost = new LinkedList<(string, long)>();
    private readonly Dictionary<string, LinkedListNode<(string Name, long Bytes)>> _ghosts = new Dictionary<string, LinkedListNode<(string Name, long Bytes)>>(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _reading = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);

    // How many writes wait, as the gate last left them: what a read asks before it takes the gate to look among them.
    private int _pendingCount;
    private readonly Queue<Pending> _queue = new Queue<Pending>();
    private long _pendingHeld;
    private int _writers;
    private long _held;
    private long _smallHeld;
    private long _ghostHeld;
    private long _hits;
    private long _misses;
    private long _corrupt;
    private long _evictions;
    private long _refused;
    private long _failedWrites;
    private int _disposed;

    // The entries let go whose file did not go, a reader mapping it on Windows: counted among the bytes held until it does,
    // and tried again at each eviction. Under the gate.
    private readonly List<Entry> _doomed = [];

    /// <summary>Opens the cache in <paramref name="directory"/>, created if it does not exist, and walks what it holds.</summary>
    /// <param name="directory">The directory, which this cache owns while it is open.</param>
    /// <param name="capacityBytes">The bytes of regions it keeps at most.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">Another cache, in this process or another, owns the directory.</exception>
    public SynopsisCache(string directory, long capacityBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        Directory = Path.GetFullPath(directory);
        CapacityBytes = capacityBytes;
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            _lock = new FileStream(Path.Combine(Directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException cause)
        {
            throw new InvalidOperationException($"The synopsis cache directory '{Directory}' is owned by another cache.", cause);
        }

        Walk();
    }

    /// <summary>The directory the cache keeps its entries in.</summary>
    public string Directory { get; }

    /// <summary>The bytes of regions the cache keeps at most.</summary>
    public long CapacityBytes { get; }

    /// <summary>The bytes of regions the cache holds now, on the disk or waiting for their write.</summary>
    public long HeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _held + _pendingHeld;
            }
        }
    }

    /// <summary>Regions read from the disk.</summary>
    internal long Hits => Count(ref _hits);

    /// <summary>Regions asked of the cache that it did not hold.</summary>
    internal long Misses => Count(ref _misses);

    /// <summary>Regions whose file did not hold their checksum, dropped.</summary>
    internal long Corrupt => Count(ref _corrupt);

    /// <summary>Entries dropped past the capacity.</summary>
    internal long Evictions => Count(ref _evictions);

    /// <summary>Regions larger than the capacity, never kept.</summary>
    internal long Refused => Count(ref _refused);

    /// <summary>
    /// Told of each region a reader asks of the cache, held or not, once per ask, in the order asked: the trace a
    /// replay of eviction policies reads (<c>vortex-queries --micro cachetrace</c>). Null, the default, tells nobody.
    /// </summary>
    internal Action<UInt128, IndexSegment>? Asked { get; set; }

    /// <summary>
    /// The region of object <paramref name="uid"/> from the disk, checked against its checksum; false when the
    /// cache does not hold it, or held it torn, which it then drops.
    /// </summary>
    internal bool TryRead(UInt128 uid, IndexSegment region, out SegmentOwner owner)
    {
        owner = null!;
        // The region's name made on the stack and looked up as it lies: a string made for each read was one an object
        // a question read.
        ReadOnlySpan<char> name = NameOf(uid, region, stackalloc char[MaxNameLength]);
        if (TryPending(name, out owner))
        {
            return true;
        }

        if (Find(name) is not { } entry)
        {
            return false;
        }

        // On Windows, the entry's file mapped, once, while the cache keeps its handle: a region read is then a view of
        // it, with no read of the system's and no copy, where a read took a fifth of a warm question over 48 objects'
        // tables. Elsewhere a read of the system's cache costs less, and a file cut short under a mapping faults its
        // reader, where a read finds it torn.
        int length = checked((int)region.Length);
        if (OperatingSystem.IsWindows() && entry.Bytes == length && length > 0 && LeaseMap(entry) is { } map)
        {
            bool held;
            try
            {
                // Its bytes verified once a mapping: no one writes a file mapped, which Windows refuses, so a region its
                // checksum held holds it at every next read of the same mapping, where each read hashed it again, a sixth
                // of a question at sixteen lanes; a file mapped again, once let go, is verified again.
                VortexBuffer view = map.View(0, length, region.AlignmentExponent);
                held = ReferenceEquals(entry.Verified, map) || region.Holds(view.Span);
                if (held)
                {
                    entry.Verified = map;
                    owner = new IO.SliceSegmentOwner(map, view);
                }
            }
            finally
            {
                map.Release();
            }

            // A torn file is let go of, its mapping first, then deleted.
            if (!held)
            {
                Drop(entry, corrupt: true);
                return false;
            }

            Served(length);
            return true;
        }

        NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
        try
        {
            Span<byte> bytes = block.WritableSpan[..length];
            if (!ReadWhole(entry, bytes) || !region.Holds(bytes))
            {
                Drop(entry, corrupt: true);
                return false;
            }

            owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes, region.AlignmentExponent));
            Served(length);
            return true;
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// The bytes kept under <paramref name="name"/>, a sealed object's as its store holds them, read whole into
    /// <paramref name="destination"/>, whose length the file must have; false when the cache does not hold them,
    /// or held them torn, which it then drops. The reader authenticates them, and drops them through
    /// <see cref="DropSealed"/> when they fail: they then count as read from the disk and as corrupt.
    /// </summary>
    internal bool TryReadSealed(string name, Span<byte> destination)
    {
        if (Find(name) is not { } entry)
        {
            return false;
        }

        if (!ReadWhole(entry, destination))
        {
            Drop(entry, corrupt: true);
            return false;
        }

        Served(destination.Length);
        return true;
    }

    /// <summary>
    /// As <see cref="TryReadSealed(string, Span{byte})"/>, for bytes whose length only their file says, no more
    /// than <paramref name="maxLength"/>: an object's trailer.
    /// </summary>
    internal bool TryReadSealed(string name, int maxLength, out SegmentOwner owner)
    {
        owner = null!;
        if (Find(name) is not { } entry)
        {
            return false;
        }

        long length = entry.Bytes;
        NativeSegmentOwner? block = length is > 0 && length <= maxLength ? AlignedBufferPool.Shared.Rent((int)length, VortexLimits.MaxAlignment) : null;
        if (block is null || !ReadWhole(entry, block.WritableSpan[..(int)length]))
        {
            block?.Release();
            Drop(entry, corrupt: true);
            return false;
        }

        owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(block.WritableSpan[..(int)length], 0));
        block.Release();
        Served((int)length);
        return true;
    }

    /// <summary>
    /// Keeps the region of object <paramref name="uid"/> read from the store, when it holds its checksum and
    /// fits the capacity, and drops what the capacity no longer holds: on the disk before it returns.
    /// </summary>
    internal void Admit(UInt128 uid, IndexSegment region, ReadOnlySpan<byte> bytes)
    {
        if (region.Checksum is null || !region.Holds(bytes))
        {
            return;
        }

        Keep(uid, NameOf(uid, region), bytes);
    }

    /// <summary>
    /// As <see cref="Admit(UInt128, IndexSegment, ReadOnlySpan{byte})"/>, behind its caller: <paramref name="owner"/>,
    /// whose memory holds <paramref name="bytes"/>, is held until a writer of the cache's has written them, and a read
    /// of the region meanwhile is served from them; written at once past <see cref="MaxPendingBytes"/> waiting.
    /// </summary>
    internal void Admit(UInt128 uid, IndexSegment region, SegmentOwner owner, VortexBuffer bytes)
    {
        if (region.Checksum is null || !region.Holds(bytes.Span))
        {
            return;
        }

        string name = NameOf(uid, region);
        if (!TryQueue(uid, name, owner, bytes, page: null))
        {
            Keep(uid, name, bytes.Span);
        }
    }

    /// <summary>
    /// Waits for every region waiting for its write to be written: what the cache's dispose waits on, and a reader of
    /// its directory from outside it, a test.
    /// </summary>
    internal void Flush()
    {
        lock (_gate)
        {
            // A writer stops once no region waits, and one starts whenever a region comes to wait.
            while (_writers > 0)
            {
                Monitor.Wait(_gate);
            }
        }
    }

    /// <summary>
    /// Puts a region read from the store to wait for its write, a writer started if fewer than <see cref="MaxWriters"/>
    /// run; false when the caller writes it, past the bytes waiting or the capacity. A page of a tree comes as
    /// <paramref name="page"/>, with no owner nor buffer.
    /// </summary>
    private bool TryQueue(UInt128 uid, string name, SegmentOwner? owner, VortexBuffer bytes, byte[]? page)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        int length = page?.Length ?? bytes.Length;
        if (length > CapacityBytes)
        {
            return false;
        }

        bool start;
        lock (_gate)
        {
            if (_entries.ContainsKey(name) || _pending.ContainsKey(name))
            {
                return true;
            }

            if (_pendingHeld + length > Math.Min(MaxPendingBytes, CapacityBytes))
            {
                return false;
            }

            Pending pending = new Pending(new Entry(name, ShardOf(uid), length), owner?.Retain(), bytes, page);
            _pending[name] = pending;
            Volatile.Write(ref _pendingCount, _pending.Count);
            _queue.Enqueue(pending);
            _pendingHeld += length;
            start = _writers < MaxWriters;
            _writers += start ? 1 : 0;
        }

        VortexTelemetry.SynopsisFetched(length);
        if (start)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static cache => cache.Drain(), this, preferLocal: false);
        }

        return true;
    }

    /// <summary>
    /// Writes the regions waiting, the oldest first, until none is left. A region whose write fails, whatever the
    /// failure, is counted and left to the store: the cache only spares reads, and a writer that ended on an exception
    /// would end its thread of the pool and leave a flush waiting for it.
    /// </summary>
    private void Drain()
    {
        while (true)
        {
            Pending? pending;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out pending))
                {
                    if (--_writers == 0)
                    {
                        Monitor.PulseAll(_gate);
                    }

                    return;
                }
            }

            try
            {
                Settle(pending);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    Interlocked.Increment(ref _failedWrites);
                }
            }
        }
    }

    /// <summary>Writes <paramref name="pending"/>'s file and takes it in, or lets it go when the write fails.</summary>
    private void Settle(Pending pending)
    {
        bool written = false;
        List<Entry> evicted = [];
        try
        {
            WriteFault?.Invoke(PathOf(pending.Entry));
            written = Write(pending.Entry, pending.Span);
        }
        finally
        {
            // Read meanwhile, it comes in with its reads, as it would have from the disk.
            lock (_gate)
            {
                _pending.Remove(pending.Entry.Name);
                Volatile.Write(ref _pendingCount, _pending.Count);
                _pendingHeld -= pending.Entry.Bytes;
                if (written)
                {
                    Enter(pending.Entry, pending.Hits, evicted);
                }
            }

            pending.Owner?.Release();
        }

        Delete(evicted);
    }

    /// <summary>A fault a test raises before a region's file is written, given its path; null for none.</summary>
    internal Action<string>? WriteFault { get; set; }

    /// <summary>The regions a writer of the cache's failed to write, past the failures of the disk it expects.</summary>
    internal long FailedWrites => Count(ref _failedWrites);

    /// <summary>A region waiting for its write, served from the buffer its reader handed over, the read counted; false when none waits under <paramref name="name"/>.</summary>
    private bool TryPending(ReadOnlySpan<char> name, out SegmentOwner owner)
    {
        // No write waiting, the case of every read once the cache holds a question's regions: no gate taken. A count
        // read as a write is queued may say none, and the region is then read where it lies, as before its write.
        if (Volatile.Read(ref _pendingCount) == 0)
        {
            owner = null!;
            return false;
        }

        lock (_gate)
        {
            if (_pending.Count == 0
                || !_pending.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out Pending? pending)
                || pending.Owner is not { } held)
            {
                owner = null!;
                return false;
            }

            pending.Hits = Math.Min(MaxHits, pending.Hits + 1);
            owner = new IO.SliceSegmentOwner(held, pending.Bytes);
        }

        Served(owner.Length);
        return true;
    }

    /// <summary>
    /// Keeps, under <paramref name="name"/>, bytes of the sealed object <paramref name="uid"/> as its store holds
    /// them, which its reader authenticated before handing them over, when they fit the capacity.
    /// </summary>
    internal void AdmitSealed(UInt128 uid, string name, ReadOnlySpan<byte> bytes) => Keep(uid, name, bytes);

    /// <summary>
    /// Keeps a page of a dataset's tree, named by the XXH3-128 <paramref name="hash"/> of its bytes, which never change: a
    /// page any version or dataset holds under that hash is these bytes. A handle opened later, by this process or the next,
    /// then reads its tree from the disk, where its own cache of pages starts empty. Written behind its reader, as a region
    /// is: <paramref name="page"/> is held until then, and must not change.
    /// </summary>
    internal void AdmitPage(UInt128 hash, byte[] page)
    {
        string name = PageNameOf(hash, page.Length);
        if (!TryQueue(hash, name, owner: null, default, page))
        {
            Keep(hash, name, page);
        }
    }

    /// <summary>
    /// The page of <paramref name="length"/> bytes kept under its <paramref name="hash"/>, read whole into a new array, or
    /// the one waiting for its write; null when the cache does not hold it. Its reader checks it against the hash, and
    /// drops it through <see cref="DropPage"/> when it does not hold it.
    /// </summary>
    internal byte[]? TryReadPage(UInt128 hash, int length)
    {
        string name = PageNameOf(hash, length);
        byte[]? waiting = null;
        lock (_gate)
        {
            if (_pending.Count > 0 && _pending.TryGetValue(name, out Pending? pending) && pending.Page is { } held)
            {
                pending.Hits = Math.Min(MaxHits, pending.Hits + 1);
                waiting = held;
            }
        }

        if (waiting is not null)
        {
            Served(length);
            return waiting;
        }

        if (Find(name) is not { } entry)
        {
            return null;
        }

        byte[] page = new byte[length];
        if (!ReadWhole(entry, page))
        {
            Drop(entry, corrupt: true);
            return null;
        }

        Served(length);
        return page;
    }

    /// <summary>Drops the page kept under <paramref name="hash"/>, which did not hash to it: a corrupt entry.</summary>
    internal void DropPage(UInt128 hash, int length) => DropSealed(PageNameOf(hash, length));

    /// <summary>The file name of a tree page: its hash and its length.</summary>
    internal static string PageNameOf(UInt128 hash, int length) => string.Create(CultureInfo.InvariantCulture, $"{hash:x32}-{length:x}-page");

    /// <summary>Drops the sealed bytes kept under <paramref name="name"/>, which did not authenticate: a corrupt entry.</summary>
    internal void DropSealed(string name)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(name, out entry);
        }

        if (entry is not null)
        {
            Drop(entry, corrupt: true);
        }
    }

    /// <summary>Whether the cache holds the sealed bytes kept under <paramref name="name"/>, without reading them.</summary>
    internal bool HoldsSealed(string name)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>The entry kept under <paramref name="name"/>, a read of it counted; null, counted a miss, when there is none.</summary>
    private Entry? Find(string name) => Find(name.AsSpan());

    /// <summary>As <see cref="Find(string)"/>, the name as characters a caller made where it liked.</summary>
    private Entry? Find(ReadOnlySpan<char> name)
    {
        lock (_gate)
        {
            if (!_entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out Entry? entry))
            {
                Interlocked.Increment(ref _misses);
                VortexTelemetry.SynopsisMiss();
                return null;
            }

            entry.Hits = Math.Min(MaxHits, entry.Hits + 1);
            return entry;
        }
    }

    /// <summary>Whether the file of <paramref name="entry"/> holds exactly as many bytes as <paramref name="bytes"/>, read into it.</summary>
    /// <remarks>
    /// Through the entry's handle, opened at its first read and kept while it is held: an open and a close cost
    /// about 85 µs on Windows, where a read of a region the system caches costs a few, and they took half of a
    /// warm question's time. Its length is the one the cache learned when it kept the file or found it.
    /// </remarks>
    private bool ReadWhole(Entry entry, Span<byte> bytes)
    {
        if (entry.Bytes != bytes.Length)
        {
            return false;
        }

        try
        {
            (SafeFileHandle file, bool kept) = Lease(entry);
            try
            {
                return ReadAll(file, bytes);
            }
            finally
            {
                if (kept)
                {
                    file.DangerousRelease();
                }
                else
                {
                    file.Dispose();
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The handle of <paramref name="entry"/>'s file, a reference to it taken, and whether the entry keeps it; one opened
    /// for the read alone when the entry left the cache meanwhile, which the caller disposes.
    /// </summary>
    private (SafeFileHandle File, bool Kept) Lease(Entry entry)
    {
        bool added = false;
        lock (_gate)
        {
            if (entry.Handle is { } held)
            {
                held.DangerousAddRef(ref added);
                return (held, true);
            }
        }

        SafeFileHandle opened = System.IO.File.OpenHandle(PathOf(entry), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        SafeFileHandle? closed = null;
        try
        {
            lock (_gate)
            {
                if (entry.Handle is { } raced)
                {
                    closed = opened;
                    raced.DangerousAddRef(ref added);
                    return (raced, true);
                }

                if (entry.Node is null || Volatile.Read(ref _disposed) != 0)
                {
                    return (opened, false);
                }

                entry.Handle = opened;
                entry.OpenNode = _open.AddFirst(entry);
                if (_open.Count > MaxOpenHandles)
                {
                    closed = Close(_open.Last!.Value);
                }

                opened.DangerousAddRef(ref added);
                return (opened, true);
            }
        }
        finally
        {
            closed?.Dispose();
        }
    }

    /// <summary>
    /// The mapping of <paramref name="entry"/>'s file, a reference to it taken for the caller to release: the one the
    /// entry keeps while the cache keeps its handle, made at its first read; one made for the read alone when the entry
    /// left the cache meanwhile. Null when the file does not map.
    /// </summary>
    private IO.MappedFileOwner? LeaseMap(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Map is { } held)
            {
                held.Retain();
                return held;
            }
        }

        IO.MappedFileOwner made;
        bool kept;
        try
        {
            (SafeFileHandle file, kept) = Lease(entry);
            try
            {
                // A file cut short maps no region of its length: the read finds it torn.
                if (RandomAccess.GetLength(file) != entry.Bytes)
                {
                    return null;
                }

                made = IO.MappedFileOwner.Map(file, entry.Bytes, ownsHandle: false);
            }
            finally
            {
                if (kept)
                {
                    file.DangerousRelease();
                }
                else
                {
                    file.Dispose();
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        lock (_gate)
        {
            if (entry.Map is { } raced)
            {
                made.Dispose();
                raced.Retain();
                return raced;
            }

            // Kept by the entry, the reference it was made with the entry's, and one more the caller's.
            if (kept && entry.Handle is not null && entry.Node is not null)
            {
                entry.Map = made;
                made.Retain();
            }

            return made;
        }
    }

    /// <summary>Takes the handle an entry keeps out of it, for the caller to dispose, and lets its mapping go; null when it keeps none. Under the gate.</summary>
    private SafeFileHandle? Close(Entry entry)
    {
        if (entry.Map is { } map)
        {
            entry.Map = null;
            map.Dispose();
        }

        if (entry.Handle is not { } handle)
        {
            return null;
        }

        _open.Remove(entry.OpenNode!);
        entry.OpenNode = null;
        entry.Handle = null;
        return handle;
    }

    /// <summary>Counts <paramref name="length"/> bytes served from the disk.</summary>
    private void Served(int length)
    {
        // Counted with no gate: a read the cache serves took it to find its region, and every lane reads.
        Interlocked.Increment(ref _hits);
        VortexTelemetry.SynopsisHit(length);
    }

    /// <summary>Writes <paramref name="bytes"/> to the disk under <paramref name="name"/>, and drops what the capacity no longer holds.</summary>
    private void Keep(UInt128 uid, string name, ReadOnlySpan<byte> bytes)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        VortexTelemetry.SynopsisFetched(bytes.Length);
        if (bytes.Length > CapacityBytes)
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _refused);
            }

            VortexTelemetry.SynopsisRefusal();
            return;
        }

        Entry entry = new Entry(name, ShardOf(uid), bytes.Length);
        lock (_gate)
        {
            if (_entries.ContainsKey(name) || _pending.ContainsKey(name))
            {
                return;
            }
        }

        if (!Write(entry, bytes))
        {
            return;
        }

        List<Entry> evicted = [];
        lock (_gate)
        {
            Enter(entry, 0, evicted);
        }

        Delete(evicted);
    }

    /// <summary>Writes the file of <paramref name="entry"/>, under a temporary name then renamed, so a reader never sees half of it; false when the disk refused it.</summary>
    private bool Write(Entry entry, ReadOnlySpan<byte> bytes)
    {
        string path = PathOf(entry);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (SafeFileHandle file = System.IO.File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                RandomAccess.Write(file, bytes, 0);
            }

            System.IO.File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            TryDelete(temporary);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    /// <summary>
    /// Takes in an entry whose file is written, with the reads it had while it waited, unless another came in under
    /// its name meanwhile, and drops what the capacity no longer holds. Under the gate.
    /// </summary>
    private void Enter(Entry entry, int hits, List<Entry> evicted)
    {
        if (_entries.ContainsKey(entry.Name))
        {
            return;
        }

        // A region dropped not long ago and asked again goes to the main queue at once.
        bool returning = _ghosts.Remove(entry.Name, out LinkedListNode<(string Name, long Bytes)>? ghost);
        if (returning)
        {
            _ghost.Remove(ghost!);
            _ghostHeld -= ghost!.Value.Bytes;
        }

        entry.Hits = hits;
        Add(entry, main: returning);
        Evict(evicted);
    }

    /// <summary>
    /// Whether the caller reads the region, holding <paramref name="mine"/> until <see cref="EndRead"/>; false
    /// when another read of it is in flight, which <paramref name="theirs"/> completes.
    /// </summary>
    internal bool TryBeginRead(UInt128 uid, IndexSegment region, out TaskCompletionSource? mine, out Task? theirs)
    {
        string name = NameOf(uid, region);
        lock (_gate)
        {
            if (_reading.TryGetValue(name, out TaskCompletionSource? other))
            {
                mine = null;
                theirs = other.Task;
                return false;
            }

            mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            theirs = null;
            _reading[name] = mine;
            return true;
        }
    }

    /// <summary>Ends a read <see cref="TryBeginRead"/> handed to the caller: those who wait on it look again.</summary>
    internal void EndRead(UInt128 uid, IndexSegment region, TaskCompletionSource mine)
    {
        lock (_gate)
        {
            _reading.Remove(NameOf(uid, region));
        }

        mine.TrySetResult();
    }

    /// <summary>
    /// Writes every region waiting, closes every file the cache keeps open, and lets their mappings go once the regions
    /// read from them are: what a writer of the files from outside the cache, a test's, waits on, a mapped file being
    /// one no one writes.
    /// </summary>
    internal void CloseFiles()
    {
        Flush();
        List<SafeFileHandle> closed = [];
        lock (_gate)
        {
            while (_open.First is { } open)
            {
                if (Close(open.Value) is { } handle)
                {
                    closed.Add(handle);
                }
            }
        }

        foreach (SafeFileHandle handle in closed)
        {
            handle.Dispose();
        }
    }

    /// <summary>Releases the directory's lock; the entries stay on the disk for the next cache that opens it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // What waits for its write is written: the next cache to open the directory finds it.
            Flush();
            lock (_gate)
            {
                while (_open.First is { } open)
                {
                    Close(open.Value)?.Dispose();
                }
            }

            _lock.Dispose();
            VortexTelemetry.SynopsisHolds(-HeldBytes);
        }
    }

    /// <summary>The file name of a region of an object: its uid, its place, its length and its checksum.</summary>
    internal static string NameOf(UInt128 uid, IndexSegment region) => new string(NameOf(uid, region, stackalloc char[MaxNameLength]));

    /// <summary>
    /// <see cref="NameOf(UInt128, IndexSegment)"/> written into <paramref name="room"/>, of <see cref="MaxNameLength"/>
    /// characters: the one place the name's form is spelt, which a lookup reads as it lies, with no string made.
    /// </summary>
    private static ReadOnlySpan<char> NameOf(UInt128 uid, IndexSegment region, Span<char> room) =>
        room.TryWrite(CultureInfo.InvariantCulture, $"{uid:x32}-{region.Offset:x}-{region.Length:x}-{region.Checksum ?? 0:x16}", out int written)
            ? room[..written]
            : throw new InvalidOperationException($"A region's name takes more than the {MaxNameLength} characters made for it.");

    /// <summary>The characters of a region's name at most (<see cref="NameOf(UInt128, IndexSegment)"/>): a uid's 32, two places' 16 each, a checksum's 16 and three dashes.</summary>
    private const int MaxNameLength = 32 + 16 + 16 + 16 + 3;

    /// <summary>The file name of a frame of a sealed object: its uid, the frame's place in the object and its length.</summary>
    internal static string FrameNameOf(UInt128 uid, long cipherOffset, int length) => string.Create(
        CultureInfo.InvariantCulture, $"{uid:x32}-{cipherOffset:x}-{length:x}-sealed");

    /// <summary>The file name of a sealed object's trailer.</summary>
    internal static string TrailerNameOf(UInt128 uid) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-trailer");

    private static long Count(ref long counter) => Interlocked.Read(ref counter);

    private static string ShardOf(UInt128 uid) => ((byte)uid).ToString("x2", CultureInfo.InvariantCulture);

    private string PathOf(Entry entry) => Path.Combine(Directory, entry.Shard, entry.Name);

    private static bool ReadAll(SafeFileHandle file, Span<byte> bytes)
    {
        int done = 0;
        while (done < bytes.Length)
        {
            int read = RandomAccess.Read(file, bytes[done..], done);
            if (read <= 0)
            {
                return false;
            }

            done += read;
        }

        return true;
    }

    /// <summary>
    /// Learns what the directory holds: every entry by its file's size, oldest written first, the files a
    /// write left under their temporary name deleted.
    /// </summary>
    private void Walk()
    {
        List<(Entry Entry, DateTime Written)> found = [];
        foreach (string shard in System.IO.Directory.EnumerateDirectories(Directory))
        {
            string shardName = Path.GetFileName(shard);
            foreach (FileInfo file in new DirectoryInfo(shard).EnumerateFiles())
            {
                if (file.Name.EndsWith(TemporaryExtension, StringComparison.Ordinal))
                {
                    TryDelete(file.FullName);
                    continue;
                }

                found.Add((new Entry(file.Name, shardName, file.Length), file.LastWriteTimeUtc));
            }
        }

        found.Sort(static (a, b) => a.Written.CompareTo(b.Written));
        List<Entry> evicted = [];
        lock (_gate)
        {
            // What an earlier cache kept has outlived its small queue: the main queue takes it, oldest at the tail.
            foreach ((Entry entry, _) in found)
            {
                Add(entry, main: true);
            }

            Evict(evicted);
        }

        Delete(evicted);
    }

    /// <summary>Puts an entry at the head of the small queue, or of the main one, the newest place.</summary>
    private void Add(Entry entry, bool main)
    {
        _entries[entry.Name] = entry;
        Enqueue(entry, main);
        _held += entry.Bytes;
        VortexTelemetry.SynopsisHolds(entry.Bytes);
    }

    private void Enqueue(Entry entry, bool main)
    {
        entry.Main = main;
        entry.Node = main ? _main.AddFirst(entry) : _small.AddFirst(entry);
        if (!main)
        {
            _smallHeld += entry.Bytes;
        }
    }

    private void Dequeue(Entry entry)
    {
        (entry.Main ? _main : _small).Remove(entry.Node!);
        entry.Node = null;
        if (!entry.Main)
        {
            _smallHeld -= entry.Bytes;
        }
    }

    /// <summary>Whether the cache holds the region of object <paramref name="uid"/>, without reading it: no hit counted, no string made.</summary>
    internal bool Holds(UInt128 uid, IndexSegment region)
    {
        ReadOnlySpan<char> name = NameOf(uid, region, stackalloc char[MaxNameLength]);
        lock (_gate)
        {
            return _entries.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name)
                || (_pending.Count > 0 && _pending.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name));
        }
    }

    /// <summary>
    /// Drops entries past the capacity, S3-FIFO's way. While the small queue holds its tenth of the capacity or
    /// more, its oldest entry leaves it: to the main queue if it was read since it came in, dropped otherwise, its
    /// name kept among the ghosts. Then the main queue's oldest: back at its head with one read less while it has
    /// reads, dropped otherwise.
    /// </summary>
    private void Evict(List<Entry> evicted)
    {
        long smallCapacity = Math.Max(1, CapacityBytes / 10);
        while (_held > CapacityBytes && (_small.Count > 0 || _main.Count > 0))
        {
            if (_small.Count > 0 && (_smallHeld >= smallCapacity || _main.Count == 0))
            {
                Entry oldest = _small.Last!.Value;
                Dequeue(oldest);
                if (oldest.Hits > 0)
                {
                    oldest.Hits = 0;
                    Enqueue(oldest, main: true);
                    continue;
                }

                Evicted(oldest, evicted);
                _ghosts[oldest.Name] = _ghost.AddFirst((oldest.Name, oldest.Bytes));
                _ghostHeld += oldest.Bytes;
                while (_ghostHeld > CapacityBytes - smallCapacity && _ghost.Last is { } forgotten)
                {
                    _ghost.RemoveLast();
                    _ghosts.Remove(forgotten.Value.Name);
                    _ghostHeld -= forgotten.Value.Bytes;
                }

                continue;
            }

            Entry last = _main.Last!.Value;
            Dequeue(last);
            if (last.Hits > 0)
            {
                last.Hits--;
                Enqueue(last, main: true);
                continue;
            }

            Evicted(last, evicted);
        }
    }

    /// <summary>An entry dropped for the capacity, its file to delete once the gate is let go.</summary>
    private void Evicted(Entry entry, List<Entry> evicted)
    {
        Close(entry)?.Dispose();
        _entries.Remove(entry.Name);
        _held -= entry.Bytes;
        VortexTelemetry.SynopsisHolds(-entry.Bytes);
        evicted.Add(entry);
        Interlocked.Increment(ref _evictions);
        VortexTelemetry.SynopsisEvicted();
    }

    private void Remove(Entry entry)
    {
        if (entry.Node is null || !_entries.Remove(entry.Name))
        {
            return;
        }

        Close(entry)?.Dispose();
        Dequeue(entry);
        _held -= entry.Bytes;
        VortexTelemetry.SynopsisHolds(-entry.Bytes);
    }

    private void Drop(Entry entry, bool corrupt)
    {
        lock (_gate)
        {
            Remove(entry);
            if (corrupt)
            {
                Interlocked.Increment(ref _corrupt);
                VortexTelemetry.SynopsisCorruption();
            }
        }

        Delete([entry]);
    }

    /// <summary>
    /// Deletes the files of the entries the cache let go, and tries again those an earlier deletion left. A file that does
    /// not go, which a reader still maps on Windows, stays counted among the bytes held, so that the cache keeps its disk
    /// within its capacity, and is tried again at the next eviction; one whose region the cache holds or writes again by
    /// then is forgotten, its file the region's once more.
    /// </summary>
    private void Delete(List<Entry> evicted)
    {
        List<Entry>? again = null;
        lock (_gate)
        {
            if (_doomed.Count > 0)
            {
                again = [];
                foreach (Entry doomed in _doomed)
                {
                    if (_entries.ContainsKey(doomed.Name) || _pending.ContainsKey(doomed.Name))
                    {
                        Uncount(doomed);
                    }
                    else
                    {
                        again.Add(doomed);
                    }
                }

                _doomed.Clear();
            }
        }

        foreach (Entry victim in evicted)
        {
            if (!Deleted(victim))
            {
                lock (_gate)
                {
                    _doomed.Add(victim);
                    _held += victim.Bytes;
                    VortexTelemetry.SynopsisHolds(victim.Bytes);
                }
            }
        }

        foreach (Entry victim in again ?? [])
        {
            bool gone = Deleted(victim);
            lock (_gate)
            {
                if (gone)
                {
                    Uncount(victim);
                }
                else
                {
                    _doomed.Add(victim);
                }
            }
        }
    }

    /// <summary>Whether the file of an entry let go is deleted: false while the system refuses, or a test does (<see cref="DeleteFault"/>).</summary>
    private bool Deleted(Entry victim)
    {
        string path = PathOf(victim);
        return DeleteFault?.Invoke(path) != true && TryDelete(path);
    }

    /// <summary>
    /// A test's refusal to delete a file the cache let go, given its path, as the system refuses while another process
    /// holds the file without sharing its deletion: true refuses it. Null, the default, refuses none.
    /// </summary>
    internal Func<string, bool>? DeleteFault { get; set; }

    /// <summary>Takes the bytes of a file that went, or became an entry's again, out of those held. Under the gate.</summary>
    private void Uncount(Entry doomed)
    {
        _held -= doomed.Bytes;
        VortexTelemetry.SynopsisHolds(-doomed.Bytes);
    }

    /// <summary>Deletes a file; false when it is still there, the system refusing.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// An entry: its file's name and shard, its bytes, its reads since it came in or last passed the main queue's
    /// tail, up to <see cref="MaxHits"/>, and its queue.
    /// </summary>
    private sealed class Entry(string name, string shard, long bytes)
    {
        internal string Name { get; } = name;

        internal string Shard { get; } = shard;

        internal long Bytes { get; } = bytes;

        internal int Hits { get; set; }

        internal bool Main { get; set; }

        internal LinkedListNode<Entry>? Node { get; set; }

        /// <summary>The handle of its file, kept open from its first read while the cache holds it; null before.</summary>
        internal SafeFileHandle? Handle { get; set; }

        /// <summary>Its place among the entries whose handle is open, the most recently opened first.</summary>
        internal LinkedListNode<Entry>? OpenNode { get; set; }

        /// <summary>Its file mapped, while its handle is kept open: what a region read is a view of; null before.</summary>
        internal IO.MappedFileOwner? Map { get; set; }

        /// <summary>The mapping whose bytes a read found to hold their region's checksum; null before one did.</summary>
        internal IO.MappedFileOwner? Verified { get; set; }
    }

    /// <summary>
    /// A region waiting for its write: its entry to be, the buffer its reader handed over, held, and its reads meanwhile;
    /// or a page of a tree, in an array of its own.
    /// </summary>
    private sealed class Pending(Entry entry, SegmentOwner? owner, VortexBuffer bytes, byte[]? page)
    {
        internal Entry Entry { get; } = entry;

        /// <summary>What holds a region's buffer until its write; null for a page.</summary>
        internal SegmentOwner? Owner { get; } = owner;

        internal VortexBuffer Bytes { get; } = bytes;

        /// <summary>A page's bytes; null for a region.</summary>
        internal byte[]? Page { get; } = page;

        /// <summary>The bytes its write writes.</summary>
        internal ReadOnlySpan<byte> Span => Page is null ? Bytes.Span : new ReadOnlySpan<byte>(Page);

        internal int Hits { get; set; }
    }
}
