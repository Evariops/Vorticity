using System;
using System.Collections.Generic;
using System.Numerics;

namespace Vorticity.Benchmarks.Queries;

/// <summary>A region asked of a cache: its key, and its bytes, which its capacity counts.</summary>
internal readonly record struct CacheAccess(long Key, long Bytes);

/// <summary>An eviction policy replayed on a trace, over a capacity in bytes.</summary>
internal interface ICachePolicy
{
    /// <summary>The policy's name, as a table prints it.</summary>
    string Name { get; }

    /// <summary>Asks for a region: true when the cache held it, false when it read it, and kept it if it admits it.</summary>
    bool Access(long key, long bytes);
}

/// <summary>
/// The replay of a trace through eviction policies at several capacities: the share of the bytes and of the
/// asks each serves from what it holds.
/// </summary>
internal static class CacheReplay
{
    /// <summary>Each policy's hit rates on <paramref name="trace"/>, by its bytes and by its asks.</summary>
    internal static (double Bytes, double Asks) Replay(IReadOnlyList<CacheAccess> trace, ICachePolicy policy)
    {
        long bytes = 0;
        long hitBytes = 0;
        long hits = 0;
        foreach (CacheAccess access in trace)
        {
            bytes += access.Bytes;
            if (policy.Access(access.Key, access.Bytes))
            {
                hits++;
                hitBytes += access.Bytes;
            }
        }

        return trace.Count == 0 ? (0, 0) : ((double)hitBytes / bytes, (double)hits / trace.Count);
    }

    /// <summary>The three policies the design weighs, over <paramref name="capacity"/> bytes and about <paramref name="entries"/> distinct regions.</summary>
    internal static ICachePolicy[] Policies(long capacity, int entries) =>
        [new Sieve(capacity), new S3Fifo(capacity), new WTinyLfu(capacity, entries)];

    /// <summary>
    /// SIEVE, the cache's own: one queue, newest at its head, a bit set on a hit; a hand walks from the tail toward
    /// the head, clearing the bits it passes and evicting the first entry without one.
    /// </summary>
    internal sealed class Sieve(long capacity) : ICachePolicy
    {
        private readonly LinkedList<Entry> _queue = new LinkedList<Entry>();
        private readonly Dictionary<long, LinkedListNode<Entry>> _map = [];
        private LinkedListNode<Entry>? _hand;
        private long _held;

        public string Name => "SIEVE";

        public bool Access(long key, long bytes)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                node.Value.Visited = true;
                return true;
            }

            if (bytes > capacity)
            {
                return false;
            }

            while (_held + bytes > capacity)
            {
                LinkedListNode<Entry> at = _hand ?? _queue.Last!;
                while (at.Value.Visited)
                {
                    at.Value.Visited = false;
                    at = at.Previous ?? _queue.Last!;
                }

                _hand = at.Previous;
                _queue.Remove(at);
                _map.Remove(at.Value.Key);
                _held -= at.Value.Bytes;
            }

            _map[key] = _queue.AddFirst(new Entry(key, bytes));
            _held += bytes;
            return false;
        }

        private sealed class Entry(long key, long bytes)
        {
            internal long Key { get; } = key;

            internal long Bytes { get; } = bytes;

            internal bool Visited { get; set; }
        }
    }

    /// <summary>
    /// S3-FIFO: a small queue that takes a tenth of the bytes and every new entry, a main queue for what was asked
    /// again, and a ghost queue of the keys the small one dropped, which go straight to the main one when asked
    /// again. Each entry counts its hits up to three; the main queue gives one back each time it passes an entry.
    /// </summary>
    internal sealed class S3Fifo(long capacity, double smallShare = 0.1) : ICachePolicy
    {
        private readonly long _smallCapacity = Math.Max(1, (long)(capacity * smallShare));
        private readonly LinkedList<Entry> _small = new LinkedList<Entry>();
        private readonly LinkedList<Entry> _main = new LinkedList<Entry>();
        private readonly LinkedList<(long Key, long Bytes)> _ghost = new LinkedList<(long, long)>();
        private readonly Dictionary<long, LinkedListNode<Entry>> _map = [];
        private readonly Dictionary<long, LinkedListNode<(long, long)>> _ghosts = [];
        private long _smallHeld;
        private long _mainHeld;
        private long _ghostHeld;

        public string Name => smallShare == 0.1 ? "S3-FIFO" : $"S3-FIFO {smallShare:P0}";

        public bool Access(long key, long bytes)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                node.Value.Hits = Math.Min(3, node.Value.Hits + 1);
                return true;
            }

            if (bytes > capacity)
            {
                return false;
            }

            bool returning = _ghosts.Remove(key, out LinkedListNode<(long Key, long Bytes)>? ghost);
            if (returning)
            {
                _ghost.Remove(ghost!);
                _ghostHeld -= ghost!.Value.Bytes;
            }

            while (_smallHeld + _mainHeld + bytes > capacity)
            {
                Evict();
            }

            Entry entry = new Entry(key, bytes);
            _map[key] = returning ? _main.AddFirst(entry) : _small.AddFirst(entry);
            if (returning)
            {
                _mainHeld += bytes;
            }
            else
            {
                _smallHeld += bytes;
            }

            return false;
        }

        private void Evict()
        {
            if (_smallHeld >= _smallCapacity || _main.Count == 0)
            {
                // The small queue's oldest: to the main queue if asked again since it came, to the ghosts otherwise.
                LinkedListNode<Entry> oldest = _small.Last!;
                _small.RemoveLast();
                _smallHeld -= oldest.Value.Bytes;
                if (oldest.Value.Hits > 0)
                {
                    oldest.Value.Hits = 0;
                    _main.AddFirst(oldest);
                    _mainHeld += oldest.Value.Bytes;
                    return;
                }

                _map.Remove(oldest.Value.Key);
                _ghosts[oldest.Value.Key] = _ghost.AddFirst((oldest.Value.Key, oldest.Value.Bytes));
                _ghostHeld += oldest.Value.Bytes;
                while (_ghostHeld > capacity - _smallCapacity && _ghost.Last is { } gone)
                {
                    _ghost.RemoveLast();
                    _ghosts.Remove(gone.Value.Key);
                    _ghostHeld -= gone.Value.Bytes;
                }

                return;
            }

            // The main queue's oldest: again at its head while it has hits to give back, out otherwise.
            while (true)
            {
                LinkedListNode<Entry> oldest = _main.Last!;
                _main.RemoveLast();
                if (oldest.Value.Hits > 0)
                {
                    oldest.Value.Hits--;
                    _main.AddFirst(oldest);
                    continue;
                }

                _map.Remove(oldest.Value.Key);
                _mainHeld -= oldest.Value.Bytes;
                return;
            }
        }

        private sealed class Entry(long key, long bytes)
        {
            internal long Key { get; } = key;

            internal long Bytes { get; } = bytes;

            internal int Hits { get; set; }
        }
    }

    /// <summary>
    /// W-TinyLFU: a window that takes a hundredth of the bytes and every new entry, in order of use, ahead of a
    /// main part in two segments, on probation and protected, in order of use; an entry leaving the window enters
    /// the main part only if a sketch of the asks, halved every ten asks an entry, says it was asked more than what
    /// it would push out.
    /// </summary>
    internal sealed class WTinyLfu : ICachePolicy
    {
        private readonly long _windowCapacity;
        private readonly long _protectedCapacity;
        private readonly long _mainCapacity;
        private readonly LinkedList<Entry> _window = new LinkedList<Entry>();
        private readonly LinkedList<Entry> _probation = new LinkedList<Entry>();
        private readonly LinkedList<Entry> _protected = new LinkedList<Entry>();
        private readonly Dictionary<long, LinkedListNode<Entry>> _map = [];
        private readonly byte[][] _counters;
        private readonly int _mask;
        private readonly int _sample;
        private int _added;
        private long _windowHeld;
        private long _probationHeld;
        private long _protectedHeld;

        internal WTinyLfu(long capacity, int entries)
        {
            _windowCapacity = Math.Max(1, capacity / 100);
            _mainCapacity = capacity - _windowCapacity;
            _protectedCapacity = _mainCapacity * 4 / 5;
            int width = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, entries));
            _counters = [new byte[width], new byte[width], new byte[width], new byte[width]];
            _mask = width - 1;
            _sample = 10 * Math.Max(64, entries);
        }

        public string Name => "W-TinyLFU";

        public bool Access(long key, long bytes)
        {
            Count(key);
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                Promote(node);
                return true;
            }

            if (bytes > _mainCapacity + _windowCapacity)
            {
                return false;
            }

            _map[key] = _window.AddFirst(new Entry(key, bytes, Segment.Window));
            _windowHeld += bytes;
            while (_windowHeld > _windowCapacity && _window.Count > 0)
            {
                LinkedListNode<Entry> candidate = _window.Last!;
                _window.RemoveLast();
                _windowHeld -= candidate.Value.Bytes;
                Admit(candidate);
            }

            return false;
        }

        private void Promote(LinkedListNode<Entry> node)
        {
            Entry entry = node.Value;
            switch (entry.Segment)
            {
                case Segment.Window:
                    _window.Remove(node);
                    _window.AddFirst(node);
                    break;
                case Segment.Protected:
                    _protected.Remove(node);
                    _protected.AddFirst(node);
                    break;
                default:
                    _probation.Remove(node);
                    _probationHeld -= entry.Bytes;
                    entry.Segment = Segment.Protected;
                    _protected.AddFirst(node);
                    _protectedHeld += entry.Bytes;
                    while (_protectedHeld > _protectedCapacity && _protected.Last is { } demoted && demoted != node)
                    {
                        _protected.RemoveLast();
                        _protectedHeld -= demoted.Value.Bytes;
                        demoted.Value.Segment = Segment.Probation;
                        _probation.AddFirst(demoted);
                        _probationHeld += demoted.Value.Bytes;
                    }

                    break;
            }
        }

        /// <summary>An entry out of the window: into probation if room, or if asked more than each entry it pushes out.</summary>
        private void Admit(LinkedListNode<Entry> candidate)
        {
            if (candidate.Value.Bytes > _mainCapacity)
            {
                _map.Remove(candidate.Value.Key);
                return;
            }

            int frequency = Frequency(candidate.Value.Key);
            while (_probationHeld + _protectedHeld + candidate.Value.Bytes > _mainCapacity)
            {
                LinkedList<Entry> from = _probation.Count > 0 ? _probation : _protected;
                LinkedListNode<Entry> victim = from.Last!;
                if (Frequency(victim.Value.Key) >= frequency)
                {
                    _map.Remove(candidate.Value.Key);
                    return;
                }

                from.RemoveLast();
                _map.Remove(victim.Value.Key);
                if (victim.Value.Segment == Segment.Probation)
                {
                    _probationHeld -= victim.Value.Bytes;
                }
                else
                {
                    _protectedHeld -= victim.Value.Bytes;
                }
            }

            candidate.Value.Segment = Segment.Probation;
            _probation.AddFirst(candidate);
            _probationHeld += candidate.Value.Bytes;
        }

        private void Count(long key)
        {
            for (int row = 0; row < _counters.Length; row++)
            {
                ref byte counter = ref _counters[row][Slot(key, row)];
                if (counter < 15)
                {
                    counter++;
                }
            }

            if (++_added >= _sample)
            {
                // The asks of long ago weigh half as much each time a sample has gone by.
                _added = 0;
                foreach (byte[] row in _counters)
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i] >>= 1;
                    }
                }
            }
        }

        private int Frequency(long key)
        {
            int least = int.MaxValue;
            for (int row = 0; row < _counters.Length; row++)
            {
                least = Math.Min(least, _counters[row][Slot(key, row)]);
            }

            return least;
        }

        private int Slot(long key, int row)
        {
            ulong mixed = (ulong)key * (0x9E3779B97F4A7C15UL + ((ulong)row * 0xBF58476D1CE4E5B9UL));
            return (int)((mixed ^ (mixed >> 31)) & (ulong)_mask);
        }

        private enum Segment
        {
            Window,
            Probation,
            Protected,
        }

        private sealed class Entry(long key, long bytes, Segment segment)
        {
            internal long Key { get; } = key;

            internal long Bytes { get; } = bytes;

            internal Segment Segment { get; set; } = segment;
        }
    }
}
