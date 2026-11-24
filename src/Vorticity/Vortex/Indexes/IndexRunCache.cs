using System;
using System.Collections.Generic;
using Vorticity.Buffers;

namespace Vorticity.Indexes;

/// <summary>
/// A decoded segment of a run: its keys and, for a sorted run, the rows they came from. A postings
/// run and a dictionary hold keys without rows. A segment owns its blocks rather than pointing into
/// a scan's arena, which is reset at every batch boundary, so that it can outlive the scan that
/// decoded it.
/// </summary>
/// <remarks>
/// Its blocks are the typed pool's, given back when the file that keeps it is disposed. Arrays of their
/// own, a run of 65 536 keys of 8 bytes was 768 KB of the large object heap at every open of its file,
/// which ran a full collection every few opens: with the pool's blocks moved to the pinned heap, the large
/// one kept a third of what it had, its budget shrank with it, and opening a file to count a band took
/// 205 µs where it took 170 (2026-11-16).
/// </remarks>
internal sealed class RunSegment
{
    /// <param name="keys">Fixed-width keys laid end to end; for byte keys, the bytes of every key laid end to end.</param>
    /// <param name="offsets">For byte keys, where each key starts in <paramref name="keys"/>, and one past the last; otherwise none.</param>
    /// <param name="rows">Each entry's row, relative to the run's first row; none for keys without rows.</param>
    /// <param name="count">The entries.</param>
    /// <param name="wideRows">The same, for a run spanning 2³² rows or more, whose rows are 64-bit; then <paramref name="rows"/> is none.</param>
    internal RunSegment(Block<byte> keys, Block<int> offsets, Block<uint> rows, int count, Block<ulong> wideRows = default)
    {
        Keys = keys;
        Offsets = offsets;
        Rows = rows;
        Count = count;
        WideRows = wideRows;
    }

    /// <summary>The keys laid end to end.</summary>
    internal Block<byte> Keys { get; }

    /// <summary>For byte keys, where each starts, and one past the last; none for fixed-width keys.</summary>
    internal Block<int> Offsets { get; }

    /// <summary>Each entry's row, relative to the run's first row; none for keys without rows or rows of 64 bits.</summary>
    internal Block<uint> Rows { get; }

    /// <summary>The entries.</summary>
    internal int Count { get; }

    /// <summary>The rows of a run spanning 2³² rows or more.</summary>
    internal Block<ulong> WideRows { get; }

    /// <summary>What the cache charges for it.</summary>
    internal long Bytes =>
        (long)Keys.Capacity + ((long)Offsets.Capacity * sizeof(int)) + ((long)Rows.Capacity * sizeof(uint)) + ((long)WideRows.Capacity * sizeof(ulong));

    /// <summary>Whether its entries carry rows.</summary>
    internal bool HasRows => !Rows.IsDefault || !WideRows.IsDefault;

    /// <summary>Entry <paramref name="index"/>'s row, relative to the run's first row.</summary>
    /// <param name="index">The entry.</param>
    internal long RowAt(int index) => !Rows.IsDefault ? Rows.AsSpan()[index] : checked((long)WideRows.AsSpan()[index]);

    /// <summary>Entry <paramref name="index"/>'s key bytes, keys of <paramref name="width"/> bytes when they have no offsets.</summary>
    internal ReadOnlySpan<byte> KeyAt(int index, int width)
    {
        if (Offsets.IsDefault)
        {
            return Keys.AsSpan(index * width, width);
        }

        ReadOnlySpan<int> starts = Offsets.AsSpan();
        return Keys.AsSpan(starts[index], starts[index + 1] - starts[index]);
    }

    /// <summary>Gives its blocks back to the pool: once, when nothing can read it any more.</summary>
    internal void Release()
    {
        Blocks.Return(Keys);
        Blocks.Return(Offsets);
        Blocks.Return(Rows);
        Blocks.Return(WideRows);
    }
}

/// <summary>Where a decoded segment was read: its index origin, and the offset of its first payload there.</summary>
/// <param name="Origin">
/// The origin of the run it belongs to: 0 for the file's own directory, then one per attached
/// fragment.
/// </param>
/// <param name="Offset">The offset of the segment's first payload, within that origin.</param>
/// <remarks>
/// The origin belongs in the key because an offset is unique within one origin only: every fragment's
/// offsets count from its own magic, so the first segments of two fragments sit at the same offset,
/// and a cache keyed by the offset alone would hand one run the other's keys.
/// </remarks>
internal readonly record struct RunSegmentKey(int Origin, ulong Offset);

/// <summary>An LRU of decoded run segments, keyed by where their first payload was read.</summary>
internal sealed class IndexRunCache
{
    private readonly long _budget;
    private readonly object _gate = new object();
    private readonly Dictionary<RunSegmentKey, LinkedListNode<(RunSegmentKey Key, RunSegment Segment)>> _map = [];
    private readonly LinkedList<(RunSegmentKey Key, RunSegment Segment)> _order = new();
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
    /// <param name="key">Where the segment's first payload was read.</param>
    /// <param name="segment">The segment.</param>
    /// <returns>Whether it was held.</returns>
    internal bool TryGet(RunSegmentKey key, out RunSegment segment)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(RunSegmentKey Key, RunSegment Segment)>? node))
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
    /// <param name="key">Where the segment's first payload was read.</param>
    /// <param name="segment">A freshly decoded segment.</param>
    /// <returns>The segment to use.</returns>
    internal RunSegment Add(RunSegmentKey key, RunSegment segment)
    {
        long bytes = segment.Bytes;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(RunSegmentKey Key, RunSegment Segment)>? held))
            {
                _order.Remove(held);
                _order.AddFirst(held);

                // The segment that lost the race was never handed to anyone: its blocks go back now.
                if (!ReferenceEquals(held.Value.Segment, segment))
                {
                    segment.Release();
                }

                return held.Value.Segment;
            }

            // A segment past the budget is not held: the caller reads it and lets it go, its blocks the collector's as an
            // evicted one's are, since nothing tells when its last reader is done.
            if (bytes > _budget)
            {
                return segment;
            }

            // A segment let go here may still be read by a lane that took it before: its blocks are the collector's, not
            // the pool's, so that no other segment is decoded into them while it is read.
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

    /// <summary>
    /// Gives back the blocks of every segment held, and holds none after: the file is disposed, and none of its scans reads
    /// a segment any more.
    /// </summary>
    internal void Release()
    {
        lock (_gate)
        {
            foreach ((RunSegmentKey _, RunSegment segment) in _order)
            {
                segment.Release();
            }

            _order.Clear();
            _map.Clear();
            _bytes = 0;
        }
    }
}
