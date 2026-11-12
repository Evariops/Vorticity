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
/// Three things, each bounded: the pages read from the store, within a budget in bytes, the oldest
/// kept first out unless a walk read it since, and read without a lock (<see cref="ClockMemo{TKey, TValue}"/>); the part of the pages region that the read opening each of the last
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
    private readonly ClockMemo<PageReference, ReadOnlyMemory<byte>> _pages;
    private readonly Dictionary<ulong, ReadOnlyMemory<byte>> _held = [];

    // The versions held, the oldest first: a handful, so one forgotten is taken out where it lies.
    private readonly List<ulong> _heldOrder = new List<ulong>(HeldVersions + 1);

    private readonly Dictionary<ulong, long> _starts = [];
    private readonly Queue<ulong> _startOrder = new Queue<ulong>();

    /// <summary>A cache of at most <paramref name="capacityBytes"/> bytes of pages read from the store.</summary>
    /// <param name="capacityBytes">The budget; 0 keeps no page read, and a page larger than it is never kept.</param>
    public PageCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        Capacity = capacityBytes;
        _pages = new ClockMemo<PageReference, ReadOnlyMemory<byte>>(capacityBytes);
    }

    /// <summary>The budget in bytes of the pages read from the store.</summary>
    public long Capacity { get; }

    /// <summary>The bytes of the pages read from the store it holds now.</summary>
    public long Size => _pages.Held;

    /// <summary>The pages read from the store it holds now.</summary>
    public int Count => _pages.Count;

    /// <summary>A page read from the store that <paramref name="reference"/> names, when the cache holds it.</summary>
    public bool TryGet(PageReference reference, out ReadOnlyMemory<byte> page) => _pages.TryGet(reference, out page);

    /// <summary>
    /// Keeps a page read from the store and checked against <paramref name="reference"/>, letting the
    /// oldest kept and not read since go to make room; false when the page is one the budget cannot hold.
    /// </summary>
    public bool Add(PageReference reference, ReadOnlyMemory<byte> page) =>
        !page.IsEmpty && _pages.Keep(reference, page, page.Length, out _);

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
            _heldOrder.Add(version);
            if (_heldOrder.Count > HeldVersions)
            {
                _held.Remove(_heldOrder[0]);
                _heldOrder.RemoveAt(0);
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
            if (_held.Remove(version))
            {
                _heldOrder.Remove(version);
            }
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

    /// <summary>
    /// Remembers where the pages region of <paramref name="version"/> starts, the latest learned
    /// winning: a start read from the preamble of an object written again under its version, after
    /// one was removed as torn, replaces the one learned of the object before.
    /// </summary>
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
}
