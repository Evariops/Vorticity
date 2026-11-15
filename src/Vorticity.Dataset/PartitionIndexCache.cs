using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The indexes of a dataset's partitions a handle has read, kept for the questions after it: each one's directory parsed,
/// its last bytes, and the pages its questions loaded, the least recently asked let go past <paramref name="budget"/> bytes,
/// and, asked by the registry of kept memory, above <paramref name="floor"/> (<see cref="LetGo"/>). An index object is never
/// written again, a new one taking its place under a new uid, so what it holds stays true.
/// </summary>
/// <remarks>
/// A question on a partition's values read the index's last bytes, then its directory, parsed, then each page it sought,
/// at every asking: S1's two of a pod's values, asked warm, read 1 MB and made 1.5 MB of pages each time, in three or four
/// requests of the store.
/// </remarks>
internal sealed class PartitionIndexCache(long budget, long floor = 0)
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(string Key, UInt128 Uid), LinkedListNode<Held>> _held = [];
    private readonly LinkedList<Held> _order = [];
    private long _bytes;

    // The turn of the registry's slow timer under way: an index asked or kept in it carries it.
    private int _turn;

    /// <summary>
    /// A handle's cache: with no budget of its own, it keeps the indexes its questions read, and the registry of kept memory
    /// takes back what it holds above <paramref name="floorBytes"/>.
    /// </summary>
    internal static PartitionIndexCache Kept(long floorBytes) => new PartitionIndexCache(long.MaxValue, floorBytes);

    /// <summary>
    /// Lets go of up to <paramref name="bytes"/> of the indexes above the floor, the least recently asked first; only those
    /// no question asked since the timer's turn before when <paramref name="stale"/>, which then starts a turn: the bytes let go.
    /// </summary>
    internal long LetGo(long bytes, bool stale)
    {
        long given = 0;
        lock (_gate)
        {
            for (LinkedListNode<Held>? node = _order.Last; node is not null && given < bytes && _bytes > floor;)
            {
                LinkedListNode<Held>? newer = node.Previous;
                if (!stale || node.Value.Turn != _turn)
                {
                    _order.Remove(node);
                    _held.Remove(node.Value.Key);
                    _bytes -= node.Value.Bytes;
                    given += node.Value.Bytes;
                    node.Value.Gone = true;
                }

                node = newer;
            }

            if (stale)
            {
                _turn++;
            }
        }

        return given;
    }

    /// <summary>The bytes the cache holds, its directories reckoned at their encoded size.</summary>
    internal long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>What the cache holds of the index object <paramref name="key"/> of uid <paramref name="uid"/>; null for nothing.</summary>
    internal Held? Of(string key, UInt128 uid)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue((key, uid), out LinkedListNode<Held>? node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            node.Value.Turn = _turn;
            return node.Value;
        }
    }

    /// <summary>
    /// Keeps <paramref name="index"/>, the directory of the index object <paramref name="key"/> of uid <paramref name="uid"/>
    /// of <paramref name="directoryBytes"/> encoded bytes, and its last bytes <paramref name="tail"/> from
    /// <paramref name="start"/>: what the cache holds of it after, or null when it alone passes the budget.
    /// </summary>
    internal Held? Keep(string key, UInt128 uid, PartitionIndex index, long directoryBytes, byte[] tail, long start)
    {
        Held held = new Held(this, index, tail, start, directoryBytes + tail.Length);
        if (held.Bytes > budget)
        {
            return null;
        }

        lock (_gate)
        {
            if (_held.TryGetValue((key, uid), out LinkedListNode<Held>? known))
            {
                return known.Value;
            }

            _held[(key, uid)] = _order.AddFirst(held);
            held.Key = (key, uid);
            held.Turn = _turn;
            Grow(held.Bytes);
            return held;
        }
    }

    /// <summary>Counts <paramref name="bytes"/> more, the least recently asked indexes let go while they pass the budget; under the lock.</summary>
    private void Grow(long bytes)
    {
        _bytes += bytes;
        while (_bytes > budget && _order.Last is { } last)
        {
            _order.RemoveLast();
            _held.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
            last.Value.Gone = true;
        }
    }

    /// <summary>
    /// A page of an index the cache holds, checked against its checksum when kept: its bytes when they lie before the
    /// object's last bytes, null when those hold them; and where its strides start, once a lookup made it
    /// (<see cref="PartitionIndex.Entry.Restarts"/>).
    /// </summary>
    internal sealed class KnownPage(byte[]? bytes)
    {
        private ulong[]? _restarts;

        /// <summary>Its bytes, null when the object's last bytes hold them.</summary>
        internal byte[]? Bytes => bytes;

        /// <summary>Where its strides start, null until a lookup made it.</summary>
        internal ulong[]? Restarts => Volatile.Read(ref _restarts);

        /// <summary>Sets <see cref="Restarts"/>; under the cache's lock.</summary>
        internal void SetRestarts(ulong[] restarts) => Volatile.Write(ref _restarts, restarts);
    }

    /// <summary>What the cache holds of one index object: its directory, its last bytes, and the pages loaded since.</summary>
    internal sealed class Held(PartitionIndexCache cache, PartitionIndex index, byte[] tail, long start, long bytes)
    {
        private readonly Dictionary<long, KnownPage> _pages = [];

        /// <summary>The index's directory.</summary>
        internal PartitionIndex Index => index;

        /// <summary>The object's last bytes, from <see cref="Start"/>.</summary>
        internal byte[] Tail => tail;

        /// <summary>Where <see cref="Tail"/> starts in the object.</summary>
        internal long Start => start;

        /// <summary>The bytes it holds, its directory reckoned at its encoded size.</summary>
        internal long Bytes { get; private set; } = bytes;

        /// <summary>Its key in the cache.</summary>
        internal (string Key, UInt128 Uid) Key { get; set; }

        /// <summary>Whether the cache let it go: a page loaded after is kept no more.</summary>
        internal bool Gone { get; set; }

        /// <summary>The timer's turn it was last asked or kept in; under the cache's lock.</summary>
        internal int Turn { get; set; }

        /// <summary>The page at <paramref name="offset"/>, loaded and checked before; null for none.</summary>
        internal KnownPage? Page(long offset)
        {
            lock (cache._gate)
            {
                return _pages.GetValueOrDefault(offset);
            }
        }

        /// <summary>
        /// Keeps the page at <paramref name="offset"/>, checked against its checksum, while the index is held: a copy of
        /// <paramref name="page"/>, its bytes, or none when they are empty, the object's last bytes holding them. The page the
        /// cache holds after, null when the index went.
        /// </summary>
        internal KnownPage? Keep(long offset, ReadOnlySpan<byte> page)
        {
            KnownPage kept = new KnownPage(page.IsEmpty ? null : page.ToArray());
            lock (cache._gate)
            {
                if (Gone)
                {
                    return null;
                }

                if (!_pages.TryAdd(offset, kept))
                {
                    return _pages[offset];
                }

                Bytes += page.Length;
                cache.Grow(page.Length);
                return kept;
            }
        }

        /// <summary>Keeps <paramref name="restarts"/>, where the strides of <paramref name="page"/> start, while the index is held.</summary>
        internal void KeepRestarts(KnownPage page, ulong[] restarts)
        {
            lock (cache._gate)
            {
                if (Gone || page.Restarts is not null)
                {
                    return;
                }

                page.SetRestarts(restarts);
                Bytes += (long)restarts.Length * sizeof(ulong);
                cache.Grow((long)restarts.Length * sizeof(ulong));
            }
        }
    }
}
