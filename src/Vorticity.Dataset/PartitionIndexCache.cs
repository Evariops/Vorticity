using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The indexes of a dataset's partitions a handle has read, kept for the questions after it: each one's directory parsed,
/// its last bytes, and the pages its questions loaded, the least recently asked let go past <paramref name="budget"/> bytes.
/// An index object is never written again, a new one taking its place under a new uid, so what it holds stays true.
/// </summary>
/// <remarks>
/// A question on a partition's values read the index's last bytes, then its directory, parsed, then each page it sought,
/// at every asking: S1's two of a pod's values, asked warm, read 1 MB and made 1.5 MB of pages each time, in three or four
/// requests of the store.
/// </remarks>
internal sealed class PartitionIndexCache(long budget)
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(string Key, UInt128 Uid), LinkedListNode<Held>> _held = [];
    private readonly LinkedList<Held> _order = [];
    private long _bytes;

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

    /// <summary>What the cache holds of one index object: its directory, its last bytes, and the pages loaded since.</summary>
    internal sealed class Held(PartitionIndexCache cache, PartitionIndex index, byte[] tail, long start, long bytes)
    {
        private readonly Dictionary<long, byte[]> _pages = [];

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

        /// <summary>The bytes of the page at <paramref name="offset"/>, loaded before; null for none.</summary>
        internal byte[]? Page(long offset)
        {
            lock (cache._gate)
            {
                return _pages.GetValueOrDefault(offset);
            }
        }

        /// <summary>Keeps <paramref name="page"/>, the bytes of the page at <paramref name="offset"/>, while the index is held.</summary>
        internal void Keep(long offset, ReadOnlySpan<byte> page)
        {
            byte[] kept = page.ToArray();
            lock (cache._gate)
            {
                if (Gone || !_pages.TryAdd(offset, kept))
                {
                    return;
                }

                Bytes += kept.Length;
                cache.Grow(kept.Length);
            }
        }
    }
}
