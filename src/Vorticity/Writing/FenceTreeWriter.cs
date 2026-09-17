// The writer side of docs/13-dataset.md §6.3: a long run's segment table, cut into fence pages.
//
// A RUN OF FEW SEGMENTS STAYS AS IT WAS: its table in its options, its regions in the directory,
// byte for byte what the files before this step hold. A run of more than `InlineFences` segments
// has its table written as pages, level by level from the segments up. Each level is cut into
// pages of at most `PageBytes`, each page written becomes one fence of the level above, and the
// first level with at most `InlineFences` fences is the root, inlined in the run's options. A
// parent names its children by region and checksum, so a level is cut only once the level below
// is placed: the pages go out one at a time, after the index writer closes.
//
// EVERY LEVEL AT LEAST HALVES. A page takes a second fence whatever its size, so a key long enough
// to fill a page alone deepens the tree and never stops it. With keys of ordinary size a page holds
// hundreds of fences, and two levels under a root of 64 cover every run this format can hold.
using System;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>When a locating run's segment table goes to pages, and how large they are.</summary>
/// <param name="InlineFences">The most fences a run keeps inline: its segments, or its root's.</param>
/// <param name="PageBytes">The most bytes a page takes, unless its first two fences pass it.</param>
internal readonly record struct FenceShape(int InlineFences, int PageBytes)
{
    /// <summary>13 §6.3's: 64 fences inline, pages of 64 KiB.</summary>
    internal static FenceShape Default => new FenceShape(64, 64 << 10);

    /// <summary>Whether a run of <paramref name="fences"/> fences is paged.</summary>
    /// <param name="fences">The run's segments, or a level's pages.</param>
    internal bool Pages(int fences) => fences > InlineFences;
}

/// <summary>Cuts one run's fences into pages, level by level, as the pages are placed.</summary>
internal sealed class FenceTreeWriter
{
    private readonly FenceShape _shape;
    private List<KeySegment> _bounds;
    private List<IndexSegment[]> _regions;
    private List<long> _segments;
    private List<KeySegment> _upBounds = [];
    private List<IndexSegment[]> _upRegions = [];
    private List<long> _upSegments = [];
    private int _level;
    private int _next;
    private FencePage? _open;
    private int _openEnd;

    /// <param name="shape">The inline and page bounds.</param>
    /// <param name="bounds">Each segment's entries and keys, in key order; not modified.</param>
    /// <param name="regions">Each segment's arrays, parallel to <paramref name="bounds"/>.</param>
    /// <exception cref="ArgumentException">The run is short enough to keep its table inline.</exception>
    internal FenceTreeWriter(FenceShape shape, List<KeySegment> bounds, List<IndexSegment[]> regions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shape.InlineFences, 1);
        if (!shape.Pages(bounds.Count) || regions.Count != bounds.Count)
        {
            throw new ArgumentException("Only a run past the inline bound has its table paged.", nameof(bounds));
        }

        _shape = shape;
        _bounds = bounds;
        _regions = regions;
        _segments = new List<long>(bounds.Count);
        for (int s = 0; s < bounds.Count; s++)
        {
            _segments.Add(1);
        }
    }

    /// <summary>The root, once the last page is placed.</summary>
    internal FencePage? Root { get; private set; }

    /// <summary>The pages placed so far.</summary>
    internal int Pages { get; private set; }

    /// <summary>Their bytes.</summary>
    internal long Bytes { get; private set; }

    /// <summary>The next page to write; false once the root is known.</summary>
    /// <param name="page">The page's bytes.</param>
    /// <exception cref="InvalidOperationException">The page taken before was not placed.</exception>
    internal bool TryTake(out byte[] page)
    {
        if (Root is not null)
        {
            page = [];
            return false;
        }

        if (_open is not null)
        {
            throw new InvalidOperationException("A fence page is placed before the next one is taken.");
        }

        int start = _next;
        int end = start;
        long bytes = FencePage.HeaderBytes(_level);
        while (end < _bounds.Count)
        {
            int fence = FencePage.FenceBytes(_bounds[end], _regions[end], _segments[end], _level);
            if (end - start >= 2 && bytes + fence > _shape.PageBytes)
            {
                break;
            }

            bytes += fence;
            end++;
        }

        int count = end - start;
        _open = FencePage.Of(
            _level,
            _bounds.GetRange(start, count).ToArray(),
            _regions.GetRange(start, count).ToArray(),
            _segments.GetRange(start, count).ToArray());
        _openEnd = end;
        page = _open.ToBytes();
        return true;
    }

    /// <summary>Records where the page <see cref="TryTake"/> gave was written.</summary>
    /// <param name="region">Its region, checksummed.</param>
    /// <exception cref="InvalidOperationException">No page is waiting.</exception>
    internal void Placed(IndexSegment region)
    {
        FencePage page = _open ?? throw new InvalidOperationException("No fence page is waiting to be placed.");
        _open = null;
        _upBounds.Add(Span(page));
        _upRegions.Add([region]);
        _upSegments.Add(page.SegmentStarts[^1]);
        _next = _openEnd;
        Pages++;
        Bytes += region.Length;
        if (_next < _bounds.Count)
        {
            return;
        }

        // The level is written: its pages are the next level's fences.
        (_bounds, _upBounds) = (_upBounds, []);
        (_regions, _upRegions) = (_upRegions, []);
        (_segments, _upSegments) = (_upSegments, []);
        _level++;
        _next = 0;
        if (!_shape.Pages(_bounds.Count))
        {
            Root = FencePage.Of(_level, [.. _bounds], [.. _regions], [.. _segments]);
        }
    }

    /// <summary>A page's bounds: its entries, its first key and its last.</summary>
    private static KeySegment Span(FencePage page)
    {
        byte[] min = [];
        byte[] max = [];
        foreach (KeySegment fence in page.Bounds)
        {
            if (fence.Entries > 0)
            {
                min = fence.Min;
                break;
            }
        }

        for (int i = page.Count - 1; i >= 0; i--)
        {
            if (page.Bounds[i].Entries > 0)
            {
                max = page.Bounds[i].Max;
                break;
            }
        }

        return new KeySegment((ulong)page.EntryStarts[^1], min, max);
    }
}
