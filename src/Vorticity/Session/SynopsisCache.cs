using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Indexes;

namespace Vorticity;

/// <summary>
/// A cache on the local disk of what a question reads of a dataset's objects without the rest of their
/// data: their slices' chunk directories, their index directories, their variant columns' path
/// directories, and the pages and segments of their indexes' runs a lookup reads;
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
/// Given a quota of its own (<see cref="DataCapacityBytes"/>), the cache keeps besides the other segments its readers
/// read of a plain object, its data and its footer, under the object's uid and the segment's place alone: no directory
/// gives their checksum, so their file holds their XXH3-64 after them, which every read from the disk checks. They leave
/// as the regions do, within their quota, and never push a region out. A sealed object's are never kept, nor are those
/// whose writes would hold up the read past the bytes waiting: no plaintext reaches the disk, and no question waits on
/// the cache's writes for its data.
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
/// The caches of several processes share a root through <see cref="OpenShared(string, long)"/>: each owns a directory under
/// it, and reads from the others' what they keep before it asks the store, keeping none of it again.
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

    /// <summary>The files of the other caches of the root the cache keeps open at most, the least recently read closed first.</summary>
    private const int MaxPeerHandles = 1024;

    /// <summary>The bytes of regions waiting for their write at most, past which a region is written before its reader goes on.</summary>
    private const long MaxPendingBytes = 256L << 20;

    /// <summary>The writers of the regions waiting that run at once at most.</summary>
    private const int MaxWriters = 2;

    /// <summary>The prefix of the directories the caches that share a root take, one each: <c>p0</c>, <c>p1</c> and on.</summary>
    private const string SlotPrefix = "p";

    /// <summary>The caches that share a root at most.</summary>
    private const int MaxSlots = 64;

    /// <summary>How long the directories of the other caches of a root are taken as last listed, before they are listed again: a listing of at most 64 directories, once a second.</summary>
    private const long PeersListedMilliseconds = 1_000;

    private readonly FileStream _lock;

    // Of a cache that shares a root (OpenShared): the root, and its lock, which every cache of the root holds shared and a
    // cache that would own the root alone is refused; the directories of the other caches of the root as last listed, and
    // when.
    private readonly string? _root;
    private readonly FileStream? _rootLock;
    private string[] _peers = [];
    private long _peersListed;

    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _open = new LinkedList<Entry>();

    // The files of the other caches of the root read, kept open, the most recently read first, each with whether its bytes
    // held their checksum once: a file never changes once renamed, so a read of it again needs neither an open, which costs
    // about 85 µs on Windows, nor a hash.
    private readonly Dictionary<string, PeerFile> _peerFiles = new Dictionary<string, PeerFile>(StringComparer.Ordinal);
    private readonly LinkedList<PeerFile> _peerOrder = new LinkedList<PeerFile>();

    // The regions of indexes and the pages of trees, and the data segments, each within its own capacity.
    private readonly Tier _regions;
    private readonly Tier _data;
    private readonly Dictionary<string, TaskCompletionSource> _reading = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);

    // How many writes wait, as the gate last left them: what a read asks before it takes the gate to look among them.
    private int _pendingCount;
    private readonly Queue<Pending> _queue = new Queue<Pending>();
    private long _pendingHeld;
    private int _writers;
    private long _hits;
    private long _peerHits;
    private long _misses;
    private long _corrupt;
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
        : this(directory, capacityBytes, dataCapacityBytes: 0)
    {
    }

    /// <summary>
    /// Opens the cache in <paramref name="directory"/>, as <see cref="SynopsisCache(string, long)"/> does, keeping besides up
    /// to <paramref name="dataCapacityBytes"/> of the data segments its readers read of plain objects.
    /// </summary>
    /// <param name="directory">The directory, which this cache owns while it is open.</param>
    /// <param name="capacityBytes">The bytes of regions it keeps at most.</param>
    /// <param name="dataCapacityBytes">The bytes of data segments it keeps at most, beside the regions; 0 keeps none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive, or <paramref name="dataCapacityBytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Another cache, in this process or another, owns the directory.</exception>
    public SynopsisCache(string directory, long capacityBytes, long dataCapacityBytes)
        : this(directory, capacityBytes, dataCapacityBytes, root: null, rootLock: null)
    {
    }

    private SynopsisCache(string directory, long capacityBytes, long dataCapacityBytes, string? root, FileStream? rootLock)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(dataCapacityBytes);
        Directory = Path.GetFullPath(directory);
        CapacityBytes = capacityBytes;
        _regions = new Tier(capacityBytes);
        _data = new Tier(dataCapacityBytes);
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            _lock = new FileStream(Path.Combine(Directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException cause)
        {
            throw new InvalidOperationException($"The synopsis cache directory '{Directory}' is owned by another cache.", cause);
        }

        _root = root;
        _rootLock = rootLock;
        Walk();
        if (root is not null)
        {
            ListPeers();
        }
    }

    /// <summary>
    /// Opens a cache that shares <paramref name="root"/> with the caches of the machine's other processes: it takes the
    /// first directory under the root no other cache holds, keeps up to <paramref name="capacityBytes"/> of regions there
    /// as a cache of its own does, and on a miss reads the region from the directories of the others before the store
    /// sends it. What it reads from another's directory stays there, the other cache's to keep or to drop.
    /// </summary>
    /// <param name="root">The directory the caches share, created if it does not exist.</param>
    /// <param name="capacityBytes">The bytes of regions this cache keeps at most in the directory it takes.</param>
    /// <returns>The cache; its <see cref="Directory"/> is the directory it took, which it owns while it is open.</returns>
    /// <remarks>
    /// The directory a cache took outlives it, as a cache's of its own does: the next cache of the root to take it finds
    /// what it kept, and until then the others read it. The caches of a root each keep their own share of the disk, and
    /// a region two of them read from the store before either kept it lies in both directories.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">A cache of its own owns the root, or 64 caches share it already.</exception>
    public static SynopsisCache OpenShared(string root, long capacityBytes) => OpenShared(root, capacityBytes, dataCapacityBytes: 0);

    /// <summary>
    /// Opens a cache that shares <paramref name="root"/>, as <see cref="OpenShared(string, long)"/> does, keeping besides up to
    /// <paramref name="dataCapacityBytes"/> of the data segments its readers read of plain objects, which it too reads from
    /// the other caches' directories before the store sends them.
    /// </summary>
    /// <param name="root">The directory the caches share, created if it does not exist.</param>
    /// <param name="capacityBytes">The bytes of regions this cache keeps at most in the directory it takes.</param>
    /// <param name="dataCapacityBytes">The bytes of data segments it keeps at most there, beside the regions; 0 keeps none.</param>
    /// <returns>The cache; its <see cref="Directory"/> is the directory it took, which it owns while it is open.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive, or <paramref name="dataCapacityBytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">A cache of its own owns the root, or 64 caches share it already.</exception>
    public static SynopsisCache OpenShared(string root, long capacityBytes, long dataCapacityBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(dataCapacityBytes);
        string full = Path.GetFullPath(root);
        System.IO.Directory.CreateDirectory(full);

        // Held shared by every cache of the root, which a cache of its own, holding it alone, refuses, and is refused.
        FileStream rootLock;
        try
        {
            rootLock = new FileStream(Path.Combine(full, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException cause)
        {
            throw new InvalidOperationException($"The synopsis cache directory '{full}' is owned by a cache of its own.", cause);
        }

        for (int slot = 0; slot < MaxSlots; slot++)
        {
            try
            {
                return new SynopsisCache(Path.Combine(full, SlotPrefix + slot.ToString(CultureInfo.InvariantCulture)), capacityBytes, dataCapacityBytes, full, rootLock);
            }
            catch (InvalidOperationException)
            {
                // Another cache holds this directory: the next one.
            }
        }

        rootLock.Dispose();
        throw new InvalidOperationException($"The synopsis cache directory '{full}' is shared by {MaxSlots} caches already.");
    }

    /// <summary>The directory the cache keeps its entries in.</summary>
    public string Directory { get; }

    /// <summary>The bytes of regions the cache keeps at most.</summary>
    public long CapacityBytes { get; }

    /// <summary>The bytes of data segments the cache keeps at most, beside its regions; 0 when it keeps none.</summary>
    public long DataCapacityBytes => _data.Capacity;

    /// <summary>The bytes the cache holds now, its regions' and its data segments', on the disk or waiting for their write.</summary>
    public long HeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _regions.Held + _data.Held + _pendingHeld;
            }
        }
    }

    /// <summary>The bytes of data segments the cache holds now, on the disk or waiting for their write.</summary>
    public long DataHeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _data.Held + _data.Pending;
            }
        }
    }

    /// <summary>Regions read from the disk.</summary>
    internal long Hits => Count(ref _hits);

    /// <summary>Regions read from the directory of another cache of the root, counted among the <see cref="Hits"/> too.</summary>
    internal long PeerHits => Count(ref _peerHits);

    /// <summary>Regions asked of the cache that it did not hold, nor, when it shares a root, another cache of the root.</summary>
    internal long Misses => Count(ref _misses);

    /// <summary>Regions whose file did not hold their checksum, dropped.</summary>
    internal long Corrupt => Count(ref _corrupt);

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
            return _root is not null && TryPeerRegion(region, name, out owner);
        }

        // The entry's file mapped, once, while the cache keeps its handle: a region read is then a view of it, with no
        // read of the system's and no copy, where a read took a fifth of a warm question over 48 objects' tables on
        // Windows. A file is whole once renamed, and only its cache writes its directory, the caches that share its
        // root reading it and mapping none of it: no mapping sees its file cut short, which would fault its reader.
        int length = checked((int)region.Length);
        if (entry.Bytes == length && length > 0 && LeaseMap(entry) is { } map)
        {
            bool held;
            try
            {
                // Its bytes verified once a mapping: no one writes a file once it is renamed, so a region its checksum
                // held holds it at every next read of the same mapping, where each read hashed it again, a sixth of a
                // question at sixteen lanes; a file mapped again, once let go, is verified again.
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
            return _root is not null && TryPeerBytes(name, destination);
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
            return _root is not null && TryPeerWhole(name, maxLength, out owner);
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

    /// <summary>Whether the cache keeps the data segments of plain objects: whether it has a quota for them.</summary>
    internal bool KeepsData => _data.Capacity > 0;

    /// <summary>
    /// The data segment <paramref name="segment"/> of object <paramref name="uid"/> from the disk, checked against the
    /// checksum its file holds after it; false when the cache does not hold it, or, when it shares a root, another cache of
    /// the root, or held it torn, which it then drops.
    /// </summary>
    internal bool TryReadData(UInt128 uid, IndexSegment segment, out SegmentOwner owner)
    {
        owner = null!;
        ReadOnlySpan<char> name = DataNameOf(uid, segment, stackalloc char[MaxDataNameLength]);
        if (TryPending(name, out owner))
        {
            return true;
        }

        if (Find(name) is not { } entry)
        {
            return _root is not null && TryPeerData(segment, name, out owner);
        }

        // Mapped and verified once a mapping, as a region is.
        int length = checked((int)segment.Length);
        if (entry.Bytes == length + DataTrailerBytes && LeaseMap(entry) is { } map)
        {
            bool held;
            try
            {
                VortexBuffer view = map.View(0, length, segment.AlignmentExponent);
                held = ReferenceEquals(entry.Verified, map) || HoldsTrailer(view.Span, map.View(length, DataTrailerBytes, 0).Span);
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

            if (!held)
            {
                Drop(entry, corrupt: true);
                return false;
            }

            Served(length);
            return true;
        }

        NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(length + DataTrailerBytes, VortexLimits.MaxAlignment);
        try
        {
            Span<byte> bytes = block.WritableSpan[..(length + DataTrailerBytes)];
            if (!ReadWhole(entry, bytes) || !HoldsTrailer(bytes[..length], bytes[length..]))
            {
                Drop(entry, corrupt: true);
                return false;
            }

            owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes[..length], segment.AlignmentExponent));
            Served(length);
            return true;
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// Keeps the data segment <paramref name="segment"/> of object <paramref name="uid"/> read from the store, behind its
    /// caller as a region is (<see cref="Admit(UInt128, IndexSegment, SegmentOwner, VortexBuffer)"/>), its checksum
    /// written after it by the cache's writer; never on the caller's own time: past the bytes waiting for their write, or
    /// with no quota for data, it is not kept.
    /// </summary>
    internal void AdmitData(UInt128 uid, IndexSegment segment, SegmentOwner owner, VortexBuffer bytes)
    {
        if (KeepsData && bytes.Length == segment.Length && bytes.Length > 0)
        {
            _ = TryQueue(uid, DataNameOf(uid, segment), owner, bytes, page: null, data: true);
        }
    }

    /// <summary>As <see cref="TryBeginRead"/>, for a data segment.</summary>
    internal bool TryBeginReadData(UInt128 uid, IndexSegment segment, out TaskCompletionSource? mine, out Task? theirs) =>
        BeginReadOf(DataNameOf(uid, segment), out mine, out theirs);

    /// <summary>As <see cref="EndRead"/>, for a data segment.</summary>
    internal void EndReadData(UInt128 uid, IndexSegment segment, TaskCompletionSource mine) => EndReadOf(DataNameOf(uid, segment), mine);

    /// <summary>Whether <paramref name="trailer"/> holds the XXH3-64 of <paramref name="bytes"/>, as a data segment's file keeps it.</summary>
    private static bool HoldsTrailer(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> trailer) =>
        trailer.Length == DataTrailerBytes && XxHash3.HashToUInt64(bytes) == BinaryPrimitives.ReadUInt64LittleEndian(trailer);

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
    private bool TryQueue(UInt128 uid, string name, SegmentOwner? owner, VortexBuffer bytes, byte[]? page, bool data = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        Tier tier = data ? _data : _regions;
        int length = (page?.Length ?? bytes.Length) + (data ? DataTrailerBytes : 0);
        if (length > tier.Capacity)
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

            if (_pendingHeld + length > Math.Min(MaxPendingBytes, tier.Capacity))
            {
                return false;
            }

            Pending pending = new Pending(new Entry(name, ShardOf(uid), length, tier), owner?.Retain(), bytes, page) { Data = data };
            _pending[name] = pending;
            Volatile.Write(ref _pendingCount, _pending.Count);
            _queue.Enqueue(pending);
            _pendingHeld += length;
            tier.Pending += length;
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
            written = Write(pending.Entry, pending.Span, pending.Data);
        }
        finally
        {
            // Read meanwhile, it comes in with its reads, as it would have from the disk.
            lock (_gate)
            {
                _pending.Remove(pending.Entry.Name);
                Volatile.Write(ref _pendingCount, _pending.Count);
                _pendingHeld -= pending.Entry.Bytes;
                pending.Entry.Tier.Pending -= pending.Entry.Bytes;
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
            return _root is not null ? TryPeerPage(name, length) : null;
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

    /// <summary>
    /// Whether the cache holds the sealed bytes kept under <paramref name="name"/>, without reading them, or, when it
    /// shares a root, another cache of the root does: those it reads, and does not keep again.
    /// </summary>
    internal bool HoldsSealed(string name)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(name))
            {
                return true;
            }
        }

        return _root is not null && PeersHold(name);
    }

    /// <summary>
    /// The entry kept under <paramref name="name"/>, a read of it counted; null when there is none, counted a miss unless
    /// the cache shares a root, whose caller asks the other caches of the root first.
    /// </summary>
    private Entry? Find(string name) => Find(name.AsSpan());

    /// <summary>As <see cref="Find(string)"/>, the name as characters a caller made where it liked.</summary>
    private Entry? Find(ReadOnlySpan<char> name)
    {
        lock (_gate)
        {
            if (!_entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out Entry? entry))
            {
                if (_root is null)
                {
                    Missed();
                }

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

    /// <summary>Counts a miss: a region the cache did not hold, nor, when it shares a root, another cache of the root.</summary>
    private void Missed()
    {
        Interlocked.Increment(ref _misses);
        VortexTelemetry.SynopsisMiss();
    }

    /// <summary>Counts <paramref name="length"/> bytes served from the directory of another cache of the root, a hit.</summary>
    private void ServedByPeer(int length)
    {
        Interlocked.Increment(ref _peerHits);
        VortexTelemetry.SynopsisPeerHit();
        Served(length);
    }

    /// <summary>
    /// The region named <paramref name="name"/> read from the directory of another cache of the root, checked against its
    /// checksum, counted a hit; false, counted a miss, when no directory holds it whole. It stays in that directory, which
    /// the other cache keeps or drops as its own reads say: a region the caches read is kept once.
    /// </summary>
    private bool TryPeerRegion(IndexSegment region, ReadOnlySpan<char> name, out SegmentOwner owner)
    {
        owner = null!;
        int length = checked((int)region.Length);
        if (LeasePeer(name, length) is { } file)
        {
            NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
            try
            {
                Span<byte> bytes = block.WritableSpan[..length];
                if (ReadPeer(file, bytes) && (file.Verified || region.Holds(bytes)))
                {
                    file.Verified = true;
                    owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes, region.AlignmentExponent));
                    ServedByPeer(length);
                    return true;
                }

                ForgetPeer(file);
            }
            finally
            {
                block.Release();
                file.Handle.DangerousRelease();
            }
        }

        Missed();
        return false;
    }

    /// <summary>
    /// As <see cref="TryPeerRegion"/>, for a data segment, checked against the checksum its file holds after it. It stays in
    /// that directory, so that a segment the caches read lies once on the disk.
    /// </summary>
    private bool TryPeerData(IndexSegment segment, ReadOnlySpan<char> name, out SegmentOwner owner)
    {
        owner = null!;
        int length = checked((int)segment.Length);
        if (LeasePeer(name, length + DataTrailerBytes) is { } file)
        {
            NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(length + DataTrailerBytes, VortexLimits.MaxAlignment);
            try
            {
                // Once its bytes held their checksum, a file's read leaves the checksum unread.
                bool verified = file.Verified;
                Span<byte> bytes = block.WritableSpan[..(verified ? length : length + DataTrailerBytes)];
                if (ReadPeer(file, bytes) && (verified || HoldsTrailer(bytes[..length], bytes[length..])))
                {
                    file.Verified = true;
                    owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes[..length], segment.AlignmentExponent));
                    ServedByPeer(length);
                    return true;
                }

                ForgetPeer(file);
            }
            finally
            {
                block.Release();
                file.Handle.DangerousRelease();
            }
        }

        Missed();
        return false;
    }

    /// <summary>
    /// The file named <paramref name="name"/> of another cache of the root, of <paramref name="length"/> bytes, a reference
    /// to its handle taken for the caller to release: the one this cache kept open from an earlier read, or one it opens
    /// now and keeps, the least recently read let go past <see cref="MaxPeerHandles"/>; null when no directory holds such a
    /// file. A file its cache deletes meanwhile is read as it was, which is what it holds: an entry never changes.
    /// </summary>
    private PeerFile? LeasePeer(ReadOnlySpan<char> name, int length)
    {
        bool added = false;
        lock (_gate)
        {
            if (_peerFiles.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out PeerFile? kept) && kept.Length == length)
            {
                kept.Handle.DangerousAddRef(ref added);
                _peerOrder.Remove(kept.Node!);
                kept.Node = _peerOrder.AddFirst(kept);
                return kept;
            }
        }

        if (OpenPeer(name, length, out _) is not { } opened)
        {
            return null;
        }

        PeerFile made = new PeerFile(new string(name), opened, length);
        PeerFile? closed = null;
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    closed = made;
                    return null;
                }

                if (_peerFiles.TryGetValue(made.Name, out PeerFile? raced))
                {
                    closed = made;
                    raced.Handle.DangerousAddRef(ref added);
                    return raced.Length == length ? raced : Released(raced);
                }

                _peerFiles[made.Name] = made;
                made.Node = _peerOrder.AddFirst(made);
                if (_peerOrder.Count > MaxPeerHandles)
                {
                    closed = _peerOrder.Last!.Value;
                    _peerOrder.RemoveLast();
                    _peerFiles.Remove(closed.Name);
                }

                made.Handle.DangerousAddRef(ref added);
                return made;
            }
        }
        finally
        {
            // A handle let go closes once the reads that hold a reference to it release them.
            closed?.Handle.Dispose();
        }

        static PeerFile? Released(PeerFile file)
        {
            file.Handle.DangerousRelease();
            return null;
        }
    }

    /// <summary>Whether <paramref name="bytes"/> were read whole from the start of a file of another cache of the root.</summary>
    private static bool ReadPeer(PeerFile file, Span<byte> bytes)
    {
        try
        {
            return ReadAll(file.Handle, bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Lets go of a file of another cache of the root that a read found torn: the next read opens it again.</summary>
    private void ForgetPeer(PeerFile file)
    {
        lock (_gate)
        {
            if (!_peerFiles.TryGetValue(file.Name, out PeerFile? kept) || !ReferenceEquals(kept, file))
            {
                return;
            }

            _peerFiles.Remove(file.Name);
            _peerOrder.Remove(file.Node!);
        }

        file.Handle.Dispose();
    }

    /// <summary>
    /// As <see cref="TryPeerRegion"/>, for the bytes of a sealed object kept under <paramref name="name"/>, which their
    /// reader authenticates, read whole into <paramref name="destination"/>.
    /// </summary>
    private bool TryPeerBytes(ReadOnlySpan<char> name, Span<byte> destination)
    {
        using SafeFileHandle? file = OpenPeer(name, destination.Length, out _);
        if (file is not null && ReadAll(file, destination))
        {
            ServedByPeer(destination.Length);
            return true;
        }

        Missed();
        return false;
    }

    /// <summary>As <see cref="TryPeerBytes"/>, for a sealed object's trailer, whose length only its file says, of no more than <paramref name="maxLength"/>.</summary>
    private bool TryPeerWhole(string name, int maxLength, out SegmentOwner owner)
    {
        owner = null!;
        using (SafeFileHandle? file = OpenPeer(name, -maxLength, out long length))
        {
            if (file is not null)
            {
                NativeSegmentOwner block = AlignedBufferPool.Shared.Rent((int)length, VortexLimits.MaxAlignment);
                try
                {
                    Span<byte> bytes = block.WritableSpan[..(int)length];
                    if (ReadAll(file, bytes))
                    {
                        owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes, 0));
                        ServedByPeer((int)length);
                        return true;
                    }
                }
                finally
                {
                    block.Release();
                }
            }
        }

        Missed();
        return false;
    }

    /// <summary>As <see cref="TryPeerBytes"/>, for a page of a dataset's tree, read into a new array, which its reader checks against its hash.</summary>
    private byte[]? TryPeerPage(string name, int length)
    {
        using SafeFileHandle? file = OpenPeer(name, length, out _);
        byte[]? page = file is null ? null : new byte[length];
        if (page is not null && ReadAll(file!, page))
        {
            ServedByPeer(length);
            return page;
        }

        Missed();
        return null;
    }

    /// <summary>Whether another cache of the root keeps a file named <paramref name="name"/>, without reading it.</summary>
    private bool PeersHold(string name)
    {
        foreach (string peer in Peers())
        {
            if (System.IO.File.Exists(Path.Join(peer, ShardOfName(name), name)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The file named <paramref name="name"/> in the directory of another cache of the root, opened to read whatever that
    /// cache does with it meanwhile, of <paramref name="length"/> bytes, or of any length up to -<paramref name="length"/>
    /// when it is negative; null when no directory holds such a file. A file the other cache deletes once opened is read
    /// as it was: an entry never changes.
    /// </summary>
    private SafeFileHandle? OpenPeer(ReadOnlySpan<char> name, int length, out long found)
    {
        found = 0;
        foreach (string peer in Peers())
        {
            SafeFileHandle? file;
            try
            {
                file = System.IO.File.OpenHandle(
                    Path.Join(peer, ShardOfName(name), name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                long bytes = RandomAccess.GetLength(file);
                if (length >= 0 ? bytes == length : bytes > 0 && bytes <= -(long)length)
                {
                    found = bytes;
                    SafeFileHandle kept = file;
                    file = null;
                    return kept;
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                file?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// The directories the other caches of the root took, listed again once the last listing is older than
    /// <see cref="PeersListedMilliseconds"/>.
    /// </summary>
    private string[] Peers()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _peersListed) >= PeersListedMilliseconds)
        {
            ListPeers();
        }

        return Volatile.Read(ref _peers);
    }

    /// <summary>
    /// Lists the directories the other caches of the root took, open or not: what a cache that closed kept is read until
    /// another takes its directory. A listing the system refuses leaves the last one.
    /// </summary>
    internal void ListPeers()
    {
        try
        {
            List<string> peers = [];
            string own = Path.GetFileName(Directory);
            foreach (string slot in System.IO.Directory.EnumerateDirectories(_root!, SlotPrefix + "*"))
            {
                if (!string.Equals(Path.GetFileName(slot), own, StringComparison.Ordinal))
                {
                    peers.Add(slot);
                }
            }

            Volatile.Write(ref _peers, [.. peers]);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Volatile.Write(ref _peersListed, Environment.TickCount64);
    }

    /// <summary>
    /// The shard of the file named <paramref name="name"/>: every name opens with the 32 hexadecimal digits of the object's
    /// uid, or of a page's hash, whose last two, its last byte, name the shard (<see cref="ShardOf"/>).
    /// </summary>
    private static ReadOnlySpan<char> ShardOfName(ReadOnlySpan<char> name) => name.Slice(30, 2);

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
        if (bytes.Length > _regions.Capacity)
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _refused);
            }

            VortexTelemetry.SynopsisRefusal();
            return;
        }

        Entry entry = new Entry(name, ShardOf(uid), bytes.Length, _regions);
        lock (_gate)
        {
            if (_entries.ContainsKey(name) || _pending.ContainsKey(name))
            {
                return;
            }
        }

        if (!Write(entry, bytes, trailer: false))
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

    /// <summary>
    /// Writes the file of <paramref name="entry"/>, under a temporary name then renamed, so a reader never sees half of it,
    /// with the XXH3-64 of <paramref name="bytes"/> after them when <paramref name="trailer"/> says so; false when the disk
    /// refused it.
    /// </summary>
    private bool Write(Entry entry, ReadOnlySpan<byte> bytes, bool trailer)
    {
        string path = PathOf(entry);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (SafeFileHandle file = System.IO.File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                RandomAccess.Write(file, bytes, 0);
                if (trailer)
                {
                    Span<byte> sum = stackalloc byte[DataTrailerBytes];
                    BinaryPrimitives.WriteUInt64LittleEndian(sum, XxHash3.HashToUInt64(bytes));
                    RandomAccess.Write(file, sum, bytes.Length);
                }
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
        Tier tier = entry.Tier;
        bool returning = tier.Ghosts.Remove(entry.Name, out LinkedListNode<(string Name, long Bytes)>? ghost);
        if (returning)
        {
            tier.Ghost.Remove(ghost!);
            tier.GhostHeld -= ghost!.Value.Bytes;
        }

        entry.Hits = hits;
        Add(entry, main: returning);
        Evict(tier, evicted);
    }

    /// <summary>
    /// Whether the caller reads the region, holding <paramref name="mine"/> until <see cref="EndRead"/>; false
    /// when another read of it is in flight, which <paramref name="theirs"/> completes.
    /// </summary>
    internal bool TryBeginRead(UInt128 uid, IndexSegment region, out TaskCompletionSource? mine, out Task? theirs) =>
        BeginReadOf(NameOf(uid, region), out mine, out theirs);

    private bool BeginReadOf(string name, out TaskCompletionSource? mine, out Task? theirs)
    {
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
    internal void EndRead(UInt128 uid, IndexSegment region, TaskCompletionSource mine) => EndReadOf(NameOf(uid, region), mine);

    private void EndReadOf(string name, TaskCompletionSource mine)
    {
        lock (_gate)
        {
            _reading.Remove(name);
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

            ClosePeers(closed);
        }

        foreach (SafeFileHandle handle in closed)
        {
            handle.Dispose();
        }
    }

    /// <summary>Takes every file of another cache of the root the cache keeps open out of it, for the caller to dispose. Under the gate.</summary>
    private void ClosePeers(List<SafeFileHandle> closed)
    {
        foreach (PeerFile peer in _peerOrder)
        {
            closed.Add(peer.Handle);
        }

        _peerOrder.Clear();
        _peerFiles.Clear();
    }

    /// <summary>Releases the directory's lock; the entries stay on the disk for the next cache that opens it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // What waits for its write is written: the next cache to open the directory finds it.
            Flush();
            List<SafeFileHandle> peers = [];
            lock (_gate)
            {
                while (_open.First is { } open)
                {
                    Close(open.Value)?.Dispose();
                }

                ClosePeers(peers);
            }

            foreach (SafeFileHandle peer in peers)
            {
                peer.Dispose();
            }

            _lock.Dispose();
            _rootLock?.Dispose();
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

    /// <summary>The file name of a data segment of an object: its uid, its place and its length.</summary>
    internal static string DataNameOf(UInt128 uid, IndexSegment segment) => new string(DataNameOf(uid, segment, stackalloc char[MaxDataNameLength]));

    /// <summary><see cref="DataNameOf(UInt128, IndexSegment)"/> written into <paramref name="room"/>, of <see cref="MaxDataNameLength"/> characters.</summary>
    private static ReadOnlySpan<char> DataNameOf(UInt128 uid, IndexSegment segment, Span<char> room) =>
        room.TryWrite(CultureInfo.InvariantCulture, $"{uid:x32}-{segment.Offset:x}-{segment.Length:x}{DataSuffix}", out int written)
            ? room[..written]
            : throw new InvalidOperationException($"A data segment's name takes more than the {MaxDataNameLength} characters made for it.");

    /// <summary>The suffix of a data segment's file name, which tells its tier when the cache walks its directory.</summary>
    private const string DataSuffix = "-data";

    /// <summary>The characters of a data segment's name at most: a uid's 32, two places' 16 each, two dashes and the suffix.</summary>
    private const int MaxDataNameLength = 32 + 16 + 16 + 2 + 5;

    /// <summary>The bytes of a data segment's checksum, which its file holds after it.</summary>
    private const int DataTrailerBytes = sizeof(ulong);

    /// <summary>The tier an entry's name says it belongs to: a data segment's, or a region's and a page's.</summary>
    private Tier TierOf(string name) => name.EndsWith(DataSuffix, StringComparison.Ordinal) ? _data : _regions;

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
            // A shard is named by the two hexadecimal digits of a uid's last byte (ShardOf): the directories the caches of
            // a shared root took, which a cache of its own may find there once they closed, are none.
            string shardName = Path.GetFileName(shard);
            if (shardName.Length != 2 || !char.IsAsciiHexDigitLower(shardName[0]) || !char.IsAsciiHexDigitLower(shardName[1]))
            {
                continue;
            }

            foreach (FileInfo file in new DirectoryInfo(shard).EnumerateFiles())
            {
                if (file.Name.EndsWith(TemporaryExtension, StringComparison.Ordinal))
                {
                    TryDelete(file.FullName);
                    continue;
                }

                found.Add((new Entry(file.Name, shardName, file.Length, TierOf(file.Name)), file.LastWriteTimeUtc));
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

            Evict(_regions, evicted);
            Evict(_data, evicted);
        }

        Delete(evicted);
    }

    /// <summary>Puts an entry at the head of the small queue, or of the main one, the newest place.</summary>
    private void Add(Entry entry, bool main)
    {
        _entries[entry.Name] = entry;
        Enqueue(entry, main);
        entry.Tier.Held += entry.Bytes;
        VortexTelemetry.SynopsisHolds(entry.Bytes);
    }

    private void Enqueue(Entry entry, bool main)
    {
        entry.Main = main;
        entry.Node = main ? entry.Tier.Main.AddFirst(entry) : entry.Tier.Small.AddFirst(entry);
        if (!main)
        {
            entry.Tier.SmallHeld += entry.Bytes;
        }
    }

    private void Dequeue(Entry entry)
    {
        (entry.Main ? entry.Tier.Main : entry.Tier.Small).Remove(entry.Node!);
        entry.Node = null;
        if (!entry.Main)
        {
            entry.Tier.SmallHeld -= entry.Bytes;
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
    private void Evict(Tier tier, List<Entry> evicted)
    {
        long smallCapacity = Math.Max(1, tier.Capacity / 10);
        while (tier.Held > tier.Capacity && (tier.Small.Count > 0 || tier.Main.Count > 0))
        {
            if (tier.Small.Count > 0 && (tier.SmallHeld >= smallCapacity || tier.Main.Count == 0))
            {
                Entry oldest = tier.Small.Last!.Value;
                Dequeue(oldest);
                if (oldest.Hits > 0)
                {
                    oldest.Hits = 0;
                    Enqueue(oldest, main: true);
                    continue;
                }

                Evicted(oldest, evicted);
                tier.Ghosts[oldest.Name] = tier.Ghost.AddFirst((oldest.Name, oldest.Bytes));
                tier.GhostHeld += oldest.Bytes;
                while (tier.GhostHeld > tier.Capacity - smallCapacity && tier.Ghost.Last is { } forgotten)
                {
                    tier.Ghost.RemoveLast();
                    tier.Ghosts.Remove(forgotten.Value.Name);
                    tier.GhostHeld -= forgotten.Value.Bytes;
                }

                continue;
            }

            Entry last = tier.Main.Last!.Value;
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
        entry.Tier.Held -= entry.Bytes;
        VortexTelemetry.SynopsisHolds(-entry.Bytes);
        evicted.Add(entry);
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
        entry.Tier.Held -= entry.Bytes;
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
                    victim.Tier.Held += victim.Bytes;
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
        doomed.Tier.Held -= doomed.Bytes;
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
    /// An entry: its file's name and shard, its bytes, its tier, its reads since it came in or last passed the main
    /// queue's tail, up to <see cref="MaxHits"/>, and its queue.
    /// </summary>
    private sealed class Entry(string name, string shard, long bytes, Tier tier)
    {
        internal string Name { get; } = name;

        internal string Shard { get; } = shard;

        internal long Bytes { get; } = bytes;

        /// <summary>The queues and the capacity it is kept within: the regions' or the data's.</summary>
        internal Tier Tier { get; } = tier;

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
    /// A file of another cache of the root, kept open: its name, its handle, its length, whether a read found its bytes to
    /// hold their checksum, and its place among those kept.
    /// </summary>
    private sealed class PeerFile(string name, SafeFileHandle handle, long length)
    {
        internal string Name { get; } = name;

        internal SafeFileHandle Handle { get; } = handle;

        internal long Length { get; } = length;

        private int _verified;

        /// <summary>Whether a read found the file's bytes to hold their checksum; set by any reader, read by every one.</summary>
        internal bool Verified
        {
            get => Volatile.Read(ref _verified) != 0;
            set => Volatile.Write(ref _verified, value ? 1 : 0);
        }

        internal LinkedListNode<PeerFile>? Node { get; set; }
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

        /// <summary>Whether it is a data segment, whose file holds its checksum after its bytes.</summary>
        internal bool Data { get; init; }

        internal int Hits { get; set; }
    }

    /// <summary>
    /// The entries kept within one capacity, S3-FIFO's way: the small queue, the main queue, and the names of those the
    /// small queue let go not long ago, with the bytes each holds. Under the gate.
    /// </summary>
    private sealed class Tier(long capacity)
    {
        /// <summary>The bytes its entries hold at most.</summary>
        internal long Capacity { get; } = capacity;

        internal LinkedList<Entry> Small { get; } = new LinkedList<Entry>();

        internal LinkedList<Entry> Main { get; } = new LinkedList<Entry>();

        internal LinkedList<(string Name, long Bytes)> Ghost { get; } = new LinkedList<(string, long)>();

        internal Dictionary<string, LinkedListNode<(string Name, long Bytes)>> Ghosts { get; } =
            new Dictionary<string, LinkedListNode<(string Name, long Bytes)>>(StringComparer.Ordinal);

        /// <summary>The bytes of its entries on the disk, and of the files let go that did not go yet.</summary>
        internal long Held { get; set; }

        /// <summary>The bytes of its entries waiting for their write.</summary>
        internal long Pending { get; set; }

        internal long SmallHeld { get; set; }

        internal long GhostHeld { get; set; }
    }
}
