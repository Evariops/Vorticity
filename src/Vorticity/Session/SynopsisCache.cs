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
/// data: the tables of their synopses, their slices' chunk directories, their index directories and their
/// variant columns' path directories. A question over a month answers from the disk once those regions
/// have been read, and makes no request to the store for them.
/// </summary>
/// <remarks>
/// <para>
/// An object never changes, so an entry needs no invalidation: its key is the object's uid, the region's
/// place in it and the region's checksum, all known from the dataset's entry before any read. Only a region
/// its object's table of contents declares, with a checksum, enters the cache, never the data nor the
/// footer. Every read from the disk checks the checksum, and a region that fails it is dropped and read
/// again from the store.
/// </para>
/// <para>
/// An entry is a file, in directories sharded by the object's uid, written under a temporary name and
/// then renamed, so a reader never sees half a file. The cache walks its directory when it opens to know
/// what it holds. One cache owns a directory, through a lock of the operating system: a second one, in
/// this process or another, is refused. Questions that miss the same region at once wait for one read.
/// </para>
/// <para>
/// A dataset whose objects are sealed reads them through the cache only once the cache can keep their
/// sealed bytes: until then, it reads them from the store. Past its capacity, the cache drops the entries
/// read least since they came in, by their bytes.
/// </para>
/// </remarks>
public sealed class SynopsisCache : IDisposable
{
    private const string LockName = ".lock";
    private const string TemporaryExtension = ".tmp";

    private readonly FileStream _lock;
    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _queue = new LinkedList<Entry>();
    private readonly Dictionary<string, TaskCompletionSource> _reading = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
    private LinkedListNode<Entry>? _hand;
    private long _held;
    private long _hits;
    private long _misses;
    private long _corrupt;
    private long _evictions;
    private long _refused;
    private int _disposed;

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

    /// <summary>The bytes of regions the cache holds now.</summary>
    public long HeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _held;
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
    /// The region of object <paramref name="uid"/> from the disk, checked against its checksum; false when the
    /// cache does not hold it, or held it torn, which it then drops.
    /// </summary>
    internal bool TryRead(UInt128 uid, IndexSegment region, out SegmentOwner owner)
    {
        owner = null!;
        string name = NameOf(uid, region);
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out entry))
            {
                _misses++;
                return false;
            }

            entry.Visited = true;
        }

        int length = checked((int)region.Length);
        NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
        try
        {
            Span<byte> bytes = block.WritableSpan[..length];
            bool whole;
            try
            {
                using SafeFileHandle file = System.IO.File.OpenHandle(PathOf(entry), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                whole = RandomAccess.GetLength(file) == length && ReadAll(file, bytes);
            }
            catch (IOException)
            {
                whole = false;
            }

            if (!whole || !region.Holds(bytes))
            {
                Drop(entry, corrupt: true);
                return false;
            }

            owner = new IO.SliceSegmentOwner(block, VortexBuffer.FromPinned(bytes, region.AlignmentExponent));
            lock (_gate)
            {
                _hits++;
            }

            return true;
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// Keeps the region of object <paramref name="uid"/> read from the store, when it holds its checksum and
    /// fits the capacity, and drops what the capacity no longer holds.
    /// </summary>
    internal void Admit(UInt128 uid, IndexSegment region, ReadOnlySpan<byte> bytes)
    {
        if (region.Checksum is null || !region.Holds(bytes) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (bytes.Length > CapacityBytes)
        {
            lock (_gate)
            {
                _refused++;
            }

            return;
        }

        string name = NameOf(uid, region);
        Entry entry = new Entry(name, ShardOf(uid), bytes.Length);
        lock (_gate)
        {
            if (_entries.ContainsKey(name))
            {
                return;
            }
        }

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
        }
        catch (IOException)
        {
            TryDelete(temporary);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return;
        }

        List<Entry> evicted = [];
        lock (_gate)
        {
            if (_entries.ContainsKey(name))
            {
                return;
            }

            Add(entry);
            Evict(evicted);
        }

        foreach (Entry victim in evicted)
        {
            TryDelete(PathOf(victim));
        }
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

    /// <summary>Releases the directory's lock; the entries stay on the disk for the next cache that opens it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lock.Dispose();
        }
    }

    /// <summary>The file name of a region of an object: its uid, its place, its length and its checksum.</summary>
    internal static string NameOf(UInt128 uid, IndexSegment region) => string.Create(
        CultureInfo.InvariantCulture, $"{uid:x32}-{region.Offset:x}-{region.Length:x}-{region.Checksum ?? 0:x16}");

    private long Count(ref long counter)
    {
        lock (_gate)
        {
            return counter;
        }
    }

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
            foreach ((Entry entry, _) in found)
            {
                Add(entry);
            }

            Evict(evicted);
        }

        foreach (Entry victim in evicted)
        {
            TryDelete(PathOf(victim));
        }
    }

    /// <summary>Puts an entry at the head of the queue, the newest place.</summary>
    private void Add(Entry entry)
    {
        _entries[entry.Name] = entry;
        entry.Node = _queue.AddFirst(entry);
        _held += entry.Bytes;
    }

    /// <summary>
    /// Drops entries past the capacity, a hand walking from the oldest towards the newest: an entry read since
    /// the hand last passed is kept and loses its mark, the first one not read is dropped.
    /// </summary>
    private void Evict(List<Entry> evicted)
    {
        while (_held > CapacityBytes && _queue.Count > 0)
        {
            LinkedListNode<Entry> node = _hand ?? _queue.Last!;
            while (node.Value.Visited)
            {
                node.Value.Visited = false;
                node = node.Previous ?? _queue.Last!;
            }

            _hand = node.Previous;
            Remove(node.Value);
            evicted.Add(node.Value);
            _evictions++;
        }
    }

    private void Remove(Entry entry)
    {
        if (entry.Node is not { } node || !_entries.Remove(entry.Name))
        {
            return;
        }

        if (ReferenceEquals(_hand, node))
        {
            _hand = node.Previous;
        }

        _queue.Remove(node);
        entry.Node = null;
        _held -= entry.Bytes;
    }

    private void Drop(Entry entry, bool corrupt)
    {
        lock (_gate)
        {
            Remove(entry);
            if (corrupt)
            {
                _corrupt++;
            }
        }

        TryDelete(PathOf(entry));
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>An entry: its file's name and shard, its bytes, and whether it was read since the hand passed.</summary>
    private sealed class Entry(string name, string shard, long bytes)
    {
        internal string Name { get; } = name;

        internal string Shard { get; } = shard;

        internal long Bytes { get; } = bytes;

        internal bool Visited { get; set; }

        internal LinkedListNode<Entry>? Node { get; set; }
    }
}
