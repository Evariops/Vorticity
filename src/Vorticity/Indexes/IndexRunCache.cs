// The decoded runs one open file keeps for its cursors - docs/12-index-reads.md §9, "the run cache".
//
// SHARED, THREAD-SAFE, DETACHED. A file serves concurrent scans and cursors (docs/09-contracts.md
// §1), so a segment of keys decoded by one is there for the next; and what is kept owns its arrays,
// because a scan arena resets at every batch boundary, exactly as `ZoneBounds` owns its bounds.
//
// BOUNDED, LEAST RECENTLY USED FIRST OUT. A run's keys can be a chunk's rows, so the cache has the
// budget `VortexReadOptions.IndexCacheBytes` gives it. A load happens outside the lock: two readers
// of a cold segment may both decode it, and the second insert finds the first and keeps it -- the
// two are equal, so the race costs a decode and never an answer. An entry larger than the budget is
// handed back and not kept.
using System;
using System.Collections.Generic;

namespace Vorticity.Indexes;

/// <summary>
/// A decoded segment of a run: its keys and, for a sorted run, the rows they came from. A postings
/// run and a dictionary hold keys without rows.
/// </summary>
/// <param name="Keys">
/// Fixed-width keys laid end to end; for byte keys, the bytes of every key laid end to end.
/// </param>
/// <param name="Offsets">For byte keys, where each key starts in <paramref name="Keys"/>, and one past the last; otherwise null.</param>
/// <param name="Rows">Each entry's row, relative to the run's first row; null for keys without rows.</param>
/// <param name="Count">The entries.</param>
internal sealed record RunSegment(byte[] Keys, int[]? Offsets, uint[]? Rows, int Count)
{
    /// <summary>What the cache charges for it.</summary>
    internal long Bytes =>
        Keys.LongLength + ((Offsets?.LongLength ?? 0) * sizeof(int)) + ((Rows?.LongLength ?? 0) * sizeof(uint));
}

/// <summary>An LRU of decoded run segments, keyed by the file offset of their first payload.</summary>
internal sealed class IndexRunCache
{
    private readonly long _budget;
    private readonly object _gate = new object();
    private readonly Dictionary<ulong, LinkedListNode<(ulong Key, RunSegment Segment)>> _map = [];
    private readonly LinkedList<(ulong Key, RunSegment Segment)> _order = new();
    private long _bytes;

    /// <param name="budget">The bytes it may hold.</param>
    internal IndexRunCache(long budget) => _budget = Math.Max(budget, 0);

    /// <summary>The bytes held.</summary>
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

    /// <summary>The segments held.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>The segment stored under <paramref name="key"/>, now the most recent.</summary>
    /// <param name="key">The file offset of the segment's first payload.</param>
    /// <param name="segment">The segment.</param>
    /// <returns>Whether it was held.</returns>
    internal bool TryGet(ulong key, out RunSegment segment)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(ulong Key, RunSegment Segment)>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                segment = node.Value.Segment;
                return true;
            }
        }

        segment = null!;
        return false;
    }

    /// <summary>
    /// Keeps <paramref name="segment"/> under <paramref name="key"/>, evicting the least recently
    /// used, and returns what the cache holds under the key -- the first insert wins a race.
    /// </summary>
    /// <param name="key">The file offset of the segment's first payload.</param>
    /// <param name="segment">A freshly decoded segment.</param>
    /// <returns>The segment to use.</returns>
    internal RunSegment Add(ulong key, RunSegment segment)
    {
        long bytes = segment.Bytes;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(ulong Key, RunSegment Segment)>? held))
            {
                _order.Remove(held);
                _order.AddFirst(held);
                return held.Value.Segment;
            }

            if (bytes > _budget)
            {
                return segment;
            }

            while (_bytes + bytes > _budget && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Segment.Bytes;
            }

            _map[key] = _order.AddFirst((key, segment));
            _bytes += bytes;
            return segment;
        }
    }
}
