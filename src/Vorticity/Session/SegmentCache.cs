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
/// The budget counts what the cache holds: a segment read as a view of a larger block, one run of
/// a coalesced read, is copied into a block of its own as it is kept. Thread-safe.
/// </remarks>
public sealed class SegmentCache
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<Key, Entry> _entries = [];

    // Each source's most recently kept entry, the head of its chain: a file that closes drops the
    // entries it chains, and walks none of the others.
    private readonly Dictionary<object, Entry> _sources = new Dictionary<object, Entry>(ReferenceEqualityComparer.Instance);
    private Entry? _newest;
    private Entry? _oldest;
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
            released = new List<SegmentOwner>(_entries.Count);
            for (Entry? entry = _newest; entry is not null; entry = entry.Older)
            {
                released.Add(entry.Owner);
            }

            _newest = null;
            _oldest = null;
            _entries.Clear();
            _sources.Clear();
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
            if (_entries.TryGetValue(new Key(source, offset, length), out Entry? entry))
            {
                Unlink(entry);
                LinkNewest(entry);
                owner = entry.Owner.Retain();
                buffer = entry.Buffer;
                Interlocked.Increment(ref _hits);
                VortexTelemetry.CacheHit();
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        VortexTelemetry.CacheMiss();
        owner = null!;
        buffer = default;
        return false;
    }

    /// <summary>
    /// Keeps the segment <paramref name="buffer"/>, evicting the least recently used to make room: a
    /// reference to <paramref name="owner"/> when the segment is all it holds, and a copy when the
    /// segment is a view of a larger block, which the budget would count at the segment's length
    /// while the block stayed whole.
    /// </summary>
    internal void Add(object source, long offset, SegmentOwner owner, VortexBuffer buffer)
    {
        int length = buffer.Length;
        if (length > Capacity || length == 0)
        {
            return;
        }

        SegmentOwner? copy = owner.Buffer.Length > length ? Copy(buffer) : null;
        List<SegmentOwner>? evicted = null;
        lock (_gate)
        {
            Key key = new Key(source, offset, length);
            if (!_entries.ContainsKey(key))
            {
                while (_size + length > Capacity && _oldest is { } last)
                {
                    Unlink(last);
                    UnlinkFromSource(last);
                    _entries.Remove(last.Key);
                    _size -= last.Buffer.Length;
                    (evicted ??= []).Add(last.Owner);
                }

                Entry entry = copy is null ? new Entry(key, owner.Retain(), buffer) : new Entry(key, copy, copy.Buffer);
                copy = null;
                _entries[key] = entry;
                LinkNewest(entry);
                if (_sources.TryGetValue(source, out Entry? head))
                {
                    entry.NextOfSource = head;
                    head.PreviousOfSource = entry;
                }

                _sources[source] = entry;
                _size += length;
            }
        }

        // Another scan kept the segment first.
        copy?.Release();
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
            if (_sources.Remove(source, out Entry? entry))
            {
                for (; entry is not null; entry = entry.NextOfSource)
                {
                    Unlink(entry);
                    _entries.Remove(entry.Key);
                    _size -= entry.Buffer.Length;
                    (released ??= []).Add(entry.Owner);
                }
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

    /// <summary>The bytes of <paramref name="segment"/> in a 64-byte aligned block of their own, which keeps every alignment a segment may declare.</summary>
    private static NativeSegmentOwner Copy(VortexBuffer segment)
    {
        NativeSegmentOwner copy = AlignedBufferPool.Shared.Rent(segment.Length, VortexLimits.MaxAlignment);
        segment.Span.CopyTo(copy.WritableSpan);
        return copy;
    }

    /// <summary>Puts <paramref name="entry"/>, out of the order of use, at its head: the most recently used.</summary>
    private void LinkNewest(Entry entry)
    {
        entry.Older = _newest;
        entry.Newer = null;
        if (_newest is not null)
        {
            _newest.Newer = entry;
        }

        _newest = entry;
        _oldest ??= entry;
    }

    /// <summary>Takes <paramref name="entry"/> out of the order of use.</summary>
    private void Unlink(Entry entry)
    {
        if (entry.Newer is null)
        {
            _newest = entry.Older;
        }
        else
        {
            entry.Newer.Older = entry.Older;
        }

        if (entry.Older is null)
        {
            _oldest = entry.Newer;
        }
        else
        {
            entry.Older.Newer = entry.Newer;
        }

        entry.Newer = null;
        entry.Older = null;
    }

    /// <summary>Takes <paramref name="entry"/> out of its source's chain.</summary>
    private void UnlinkFromSource(Entry entry)
    {
        if (entry.PreviousOfSource is null)
        {
            if (entry.NextOfSource is null)
            {
                _sources.Remove(entry.Key.Source);
            }
            else
            {
                _sources[entry.Key.Source] = entry.NextOfSource;
            }
        }
        else
        {
            entry.PreviousOfSource.NextOfSource = entry.NextOfSource;
        }

        if (entry.NextOfSource is not null)
        {
            entry.NextOfSource.PreviousOfSource = entry.PreviousOfSource;
        }

        entry.PreviousOfSource = null;
        entry.NextOfSource = null;
    }

    private readonly record struct Key(object Source, long Offset, int Length);

    /// <summary>A segment kept, in the order of use and in its source's chain.</summary>
    private sealed class Entry(Key key, SegmentOwner owner, VortexBuffer buffer)
    {
        internal Key Key { get; } = key;

        internal SegmentOwner Owner { get; } = owner;

        internal VortexBuffer Buffer { get; } = buffer;

        internal Entry? Newer { get; set; }

        internal Entry? Older { get; set; }

        internal Entry? NextOfSource { get; set; }

        internal Entry? PreviousOfSource { get; set; }
    }
}
