using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// Segments kept across the scans of a session, least recently used first out, within one budget in
/// bytes for every file of the session.
/// </summary>
/// <remarks>
/// A scan reads each segment it needs once whether or not a cache is set; the cache is what makes a
/// second scan, or a scan of the same footer and index regions from another request, read nothing.
/// Thread-safe.
/// </remarks>
public sealed class SegmentCache
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
    private long _size;
    private long _hits;
    private long _misses;

    /// <summary>A cache of at most <paramref name="capacityBytes"/> bytes.</summary>
    /// <param name="capacityBytes">The budget; a segment larger than it is never kept.</param>
    public SegmentCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        Capacity = capacityBytes;
    }

    /// <summary>The budget in bytes.</summary>
    public long Capacity { get; }

    /// <summary>The bytes held now.</summary>
    public long Size => Interlocked.Read(ref _size);

    /// <summary>Segments served from the cache since it was created.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Segments looked up and not found since it was created.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Drops every segment.</summary>
    public void Clear()
    {
        List<SegmentOwner> released;
        lock (_gate)
        {
            released = new List<SegmentOwner>(_order.Count);
            foreach (Entry entry in _order)
            {
                released.Add(entry.Owner);
            }

            _order.Clear();
            _entries.Clear();
            Interlocked.Exchange(ref _size, 0);
        }

        foreach (SegmentOwner owner in released)
        {
            owner.Release();
        }
    }

    /// <summary>The segment at <paramref name="offset"/> of <paramref name="source"/>, its owner retained for the caller.</summary>
    internal bool TryGet(object source, long offset, int length, out SegmentOwner owner, out VortexBuffer buffer)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(new Key(source, offset, length), out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                owner = node.Value.Owner.Retain();
                buffer = node.Value.Buffer;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        owner = null!;
        buffer = default;
        return false;
    }

    /// <summary>Keeps a reference to <paramref name="owner"/> for the segment <paramref name="buffer"/>, evicting the least recently used to make room.</summary>
    internal void Add(object source, long offset, SegmentOwner owner, VortexBuffer buffer)
    {
        int length = buffer.Length;
        if (length > Capacity || length == 0)
        {
            return;
        }

        List<SegmentOwner>? evicted = null;
        lock (_gate)
        {
            Key key = new Key(source, offset, length);
            if (_entries.ContainsKey(key))
            {
                return;
            }

            while (_size + length > Capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
                _size -= last.Value.Buffer.Length;
                (evicted ??= []).Add(last.Value.Owner);
            }

            LinkedListNode<Entry> node = _order.AddFirst(new Entry(key, owner.Retain(), buffer));
            _entries[key] = node;
            _size += length;
        }

        if (evicted is not null)
        {
            foreach (SegmentOwner old in evicted)
            {
                old.Release();
            }
        }
    }

    /// <summary>Drops every segment of <paramref name="source"/>, whose file is closing.</summary>
    internal void Evict(object source)
    {
        List<SegmentOwner>? released = null;
        lock (_gate)
        {
            LinkedListNode<Entry>? node = _order.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (ReferenceEquals(node.Value.Key.Source, source))
                {
                    _order.Remove(node);
                    _entries.Remove(node.Value.Key);
                    _size -= node.Value.Buffer.Length;
                    (released ??= []).Add(node.Value.Owner);
                }

                node = next;
            }
        }

        if (released is not null)
        {
            foreach (SegmentOwner owner in released)
            {
                owner.Release();
            }
        }
    }

    private readonly record struct Key(object Source, long Offset, int Length);

    private readonly record struct Entry(Key Key, SegmentOwner Owner, VortexBuffer Buffer);
}
