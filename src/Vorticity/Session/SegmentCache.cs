using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity;

/// <summary>
/// Segments kept across scans, the oldest kept first out unless a lookup used it since (a clock, or second chance),
/// within one budget in bytes for every file of the sessions that share it.
/// </summary>
/// <remarks>
/// <para>
/// A scan reads each segment it needs once whether or not a cache is set; the cache is what makes a
/// second scan, or a scan of the same footer and index regions from another request, read nothing.
/// The budget counts what the cache holds: a segment read as a view of a larger block, one run of
/// a coalesced read, is copied into a block of its own as it is kept. Thread-safe.
/// </para>
/// <para>
/// A hit marks its entry used and moves nothing: an eviction that meets a used entry at the tail puts it back at the head,
/// unmarked, and goes on. A hit that relinked its entry, a strict order of use, wrote to two neighbours' lines under the
/// lock every lane of the process takes, and cost a lookup of 4,096 segments 30 to 40 % more.
/// </para>
/// </remarks>
public sealed class SegmentCache
{
    // Whether the reads of the flow under way keep nothing new: a dataset's job's (Pass).
    private static readonly AsyncLocal<bool> s_passing = new AsyncLocal<bool>();

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
        CapacityBytes = capacityBytes;
    }

    /// <summary>
    /// The process's cache, which every session that gives none shares (<see cref="VortexSessionOptions.SegmentCache"/>):
    /// a sixteenth of the memory the process may use, 1 GiB at most.
    /// </summary>
    /// <remarks>
    /// Its segments are keyed by the reader that read them, as any cache's: a file opened by one session finds nothing
    /// another kept, except the objects of one store a dataset reads, whose bytes a key names once. A file that closes
    /// takes its segments with it.
    /// </remarks>
    public static SegmentCache Shared => Process.Cache;

    /// <summary>The budget in bytes.</summary>
    public long CapacityBytes { get; }

    /// <summary>Whether the reads of the flow under way keep nothing new in a cache (<see cref="Pass"/>).</summary>
    internal static bool Passing => s_passing.Value;

    /// <summary>The bytes held now.</summary>
    public long HeldBytes => Interlocked.Read(ref _size);

    /// <summary>Segments served from the cache since it was created.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Segments looked up and not found since it was created.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Has the reads of the flow under way, and of the tasks it starts, keep nothing new in a cache until the scope ends,
    /// what a cache holds still serving them: a merge or a consolidation reads each of its objects once, and what it kept
    /// would push out what questions read again. A consolidation of four hours of log records read 1.8 GB through a cache
    /// of 1 GiB, which it held full of segments no question would ask.
    /// </summary>
    internal static PassingScope Pass()
    {
        bool was = s_passing.Value;
        s_passing.Value = true;
        return new PassingScope(was);
    }

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
                entry.Referenced = true;
                owner = entry.Owner.Retain();
                buffer = entry.Buffer;
            }
            else
            {
                owner = null!;
                buffer = default;
            }
        }

        // Counted out of the lock, which every lane of every question takes.
        Counted(owner is null ? 0 : 1, owner is null ? 1 : 0);
        return owner is not null;
    }

    /// <summary>
    /// Fills from the cache every slot of <paramref name="requests"/> no one filled yet, the segments of <paramref name="source"/>,
    /// under one taking of the lock; whether every slot is filled after it.
    /// </summary>
    /// <remarks>
    /// A scan's request set asks for each segment of a split at once, and was looked up a segment at a time: a split of R
    /// segments took the process's lock R times, with a relinking, a counter and a meter in it at each hit. A hit now only
    /// marks its entry used (<see cref="Entry.Referenced"/>), and the set's hits and misses are counted once, out of the lock.
    /// </remarks>
    internal bool Fill(object source, SegmentRequestSet requests)
    {
        int hits = 0;
        int misses = 0;
        lock (_gate)
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                Serialization.Schemas.SegmentSpec spec = requests.GetSpec(slot);
                if (_entries.TryGetValue(new Key(source, (long)spec.Offset, (int)spec.Length), out Entry? entry))
                {
                    entry.Referenced = true;
                    requests.SetSharedResult(slot, entry.Owner, entry.Buffer);
                    requests.NoteCacheHit();
                    hits++;
                }
                else
                {
                    misses++;
                }
            }
        }

        Counted(hits, misses);
        return misses == 0;
    }

    /// <summary>Adds hits and misses to the cache's counts and to the process's meters.</summary>
    private void Counted(int hits, int misses)
    {
        if (hits > 0)
        {
            Interlocked.Add(ref _hits, hits);
            VortexTelemetry.CacheHit(hits);
        }

        if (misses > 0)
        {
            Interlocked.Add(ref _misses, misses);
            VortexTelemetry.CacheMiss(misses);
        }
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
        if (length > CapacityBytes || length == 0)
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
                while (_size + length > CapacityBytes && _oldest is { } last)
                {
                    // A second chance (clock): an entry used since it was last passed goes back to the head, unmarked,
                    // which a hit no longer does itself. Every entry passed is unmarked, so the walk ends.
                    Unlink(last);
                    if (last.Referenced)
                    {
                        last.Referenced = false;
                        LinkNewest(last);
                        continue;
                    }

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
    internal static NativeSegmentOwner Copy(VortexBuffer segment)
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

    /// <summary>The scope of <see cref="Pass"/>, which gives the flow back what it kept before.</summary>
    internal readonly struct PassingScope(bool was) : IDisposable
    {
        public void Dispose() => s_passing.Value = was;
    }

    // Made at its first use, so that a process whose sessions each give their own makes none.
    private static class Process
    {
        internal static readonly SegmentCache Cache = new SegmentCache(Math.Min(1L << 30, QueryMemoryBudget.ProcessLimit() / 16));
    }

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

        /// <summary>Whether a hit used the entry since an eviction last passed it, which then passes it over once.</summary>
        internal bool Referenced { get; set; }
    }
}
