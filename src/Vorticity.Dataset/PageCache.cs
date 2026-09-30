using System;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// What a dataset handle keeps of the tree pages from one version it reads to the next. A page is
/// immutable and a version names every page it did not rewrite by the reference an earlier one gave
/// it, so a page kept at one version serves each later one that still points to it: a refresh, or a
/// commit, reads again only the pages that changed.
/// </summary>
/// <remarks>
/// Three things, each bounded: the pages read from the store, least recently used first out within
/// a budget in bytes; the part of the pages region that the read opening each of the last
/// <see cref="HeldVersions"/> versions brought back, where the pages those versions wrote lie; and
/// where each version's pages region starts, which reading any of its pages needs. A region is kept
/// by version and not page by page because most of what a version wrote, the next one rewrites:
/// kept by the byte budget, those pages would fill it. Thread-safe.
/// </remarks>
internal sealed class PageCache
{
    /// <summary>How many versions' regions it keeps, the oldest held first out.</summary>
    internal const int HeldVersions = 16;

    /// <summary>How many versions' starts it remembers, the oldest learned first out.</summary>
    internal const int MaxStarts = 4096;

    private readonly Lock _gate = new Lock();
    private readonly Dictionary<PageReference, Entry> _pages = [];
    private readonly Dictionary<ulong, ReadOnlyMemory<byte>> _held = [];
    private readonly Queue<ulong> _heldOrder = new Queue<ulong>();
    private readonly Dictionary<ulong, long> _starts = [];
    private readonly Queue<ulong> _startOrder = new Queue<ulong>();
    private Entry? _newest;
    private Entry? _oldest;
    private long _size;

    /// <summary>A cache of at most <paramref name="capacityBytes"/> bytes of pages read from the store.</summary>
    /// <param name="capacityBytes">The budget; 0 keeps no page read, and a page larger than it is never kept.</param>
    public PageCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        Capacity = capacityBytes;
    }

    /// <summary>The budget in bytes of the pages read from the store.</summary>
    public long Capacity { get; }

    /// <summary>The bytes of the pages read from the store it holds now.</summary>
    public long Size
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    /// <summary>The pages read from the store it holds now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>A page read from the store that <paramref name="reference"/> names, when the cache holds it.</summary>
    public bool TryGet(PageReference reference, out ReadOnlyMemory<byte> page)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(reference, out Entry? entry))
            {
                Unlink(entry);
                LinkNewest(entry);
                page = entry.Bytes;
                return true;
            }
        }

        page = default;
        return false;
    }

    /// <summary>
    /// Keeps a page read from the store and checked against <paramref name="reference"/>, evicting the
    /// least recently used to make room.
    /// </summary>
    public void Add(PageReference reference, ReadOnlyMemory<byte> page)
    {
        if (page.Length > Capacity || page.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_pages.ContainsKey(reference))
            {
                return;
            }

            while (_size + page.Length > Capacity && _oldest is { } last)
            {
                Unlink(last);
                _pages.Remove(last.Reference);
                _size -= last.Bytes.Length;
            }

            Entry entry = new Entry(reference, page);
            _pages[reference] = entry;
            LinkNewest(entry);
            _size += page.Length;
        }
    }

    /// <summary>
    /// The start of the pages region of <paramref name="version"/> that the read opening it brought
    /// back, when the version is one of the last the cache held; its pages are not checked yet.
    /// </summary>
    public bool TryGetHeld(ulong version, out ReadOnlyMemory<byte> region)
    {
        lock (_gate)
        {
            return _held.TryGetValue(version, out region);
        }
    }

    /// <summary>
    /// Keeps what the read opening <paramref name="version"/> brought back of its pages region, in
    /// place of what an earlier open of it brought back: the latest read is of the object there now.
    /// </summary>
    public void Hold(ulong version, ReadOnlyMemory<byte> region)
    {
        if (region.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_held.ContainsKey(version))
            {
                _held[version] = region;
                return;
            }

            _held[version] = region;
            _heldOrder.Enqueue(version);
            while (_held.Count > HeldVersions)
            {
                _held.Remove(_heldOrder.Dequeue());
            }
        }
    }

    /// <summary>
    /// Lets go of the region kept for <paramref name="version"/>, which held a page that does not hash
    /// to its reference: the version's object was replaced since, as a torn one is.
    /// </summary>
    public void Forget(ulong version)
    {
        lock (_gate)
        {
            _held.Remove(version);
        }
    }

    /// <summary>Where the pages region of <paramref name="version"/> starts, when the cache learned it.</summary>
    public bool TryGetStart(ulong version, out long start)
    {
        lock (_gate)
        {
            return _starts.TryGetValue(version, out start);
        }
    }

    /// <summary>Remembers where the pages region of <paramref name="version"/> starts, the latest learned winning.</summary>
    public void AddStart(ulong version, long start)
    {
        lock (_gate)
        {
            if (_starts.ContainsKey(version))
            {
                _starts[version] = start;
                return;
            }

            _starts[version] = start;
            _startOrder.Enqueue(version);
            while (_starts.Count > MaxStarts)
            {
                _starts.Remove(_startOrder.Dequeue());
            }
        }
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

    /// <summary>A page kept, in the order of use.</summary>
    private sealed class Entry(PageReference reference, ReadOnlyMemory<byte> bytes)
    {
        internal PageReference Reference { get; } = reference;

        internal ReadOnlyMemory<byte> Bytes { get; } = bytes;

        internal Entry? Newer { get; set; }

        internal Entry? Older { get; set; }
    }
}
