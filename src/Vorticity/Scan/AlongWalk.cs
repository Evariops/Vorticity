using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Scanning;

/// <summary>
/// The walk of a scan over the splits past those started whose small segments its batches read along with their own: its
/// cursor, the number of the batch its next split is, and the scratch a window ahead is planned in.
/// </summary>
/// <remarks>
/// <para>
/// A split reads a large segment of its own when the split before it does not ask for it too, a chunk of several splits
/// being read by the first that asks; such a split makes a request whatever is read along, and its batch would wait for
/// what is read along before it made it. The small segments read along are therefore those of the splits that read
/// nothing of their own, and that no such split asks for too: a split that reads takes its small segments, and so does
/// each split after it that shares one.
/// </para>
/// <para>
/// The context the scan rents to register the segments ahead in holds the walk, beside the segments a batch on that
/// context would read along, and keeps it for the scan that rents the context next; the scratch is the process's pool's
/// arrays, given back when the scan ends. A scan allocates no walk of its own once the pool's contexts have one, and
/// nothing for its scratch whichever context it draws.
/// </para>
/// </remarks>
internal sealed class AlongWalk
{
    /// <summary>
    /// The splits ahead a window walks at most while it finds nothing to read along: a file whose splits each read a
    /// segment of their own, or take every small one, as one of wide chunks a split, stops the walk after that many.
    /// </summary>
    internal const int ProbeSplits = 8;

    internal SplitCursor Cursor;

    /// <summary>The batch the cursor's next split is, -1 before the first.</summary>
    internal long Batch = -1;

    /// <summary>The context the walk's scan rented, which registers the segments ahead; null once the scan returned it.</summary>
    internal ScanContext? Context;

    /// <summary>Whether a window found nothing to read along: the walk then plans no other for the scan.</summary>
    internal bool Stopped;

    private (ulong Start, ulong End)[]? _own;
    private int _owned;
    private (SegmentSpec Spec, long Batch)[]? _candidates;
    private int _candidateCount;
    private Segments _previousLarge;
    private Segments _large;
    private Segments _previousTaken;
    private Segments _taken;

    /// <summary>The extents of the batch's own segments, while a window ahead is planned.</summary>
    internal ReadOnlySpan<(ulong Start, ulong End)> Own => _own.AsSpan(0, _owned);

    /// <summary>The small segments of the window's splits, each with the batch that asks for it; -1 for one withdrawn.</summary>
    internal Span<(SegmentSpec Spec, long Batch)> Candidates => _candidates.AsSpan(0, _candidateCount);

    /// <summary>The walk <paramref name="context"/> holds, made at its first, for a scan that just rented the context.</summary>
    internal static AlongWalk Begin(ScanContext context)
    {
        AlongWalk walk = (context.Along ??= new ScanContext.AlongSegments()).Walk ??= new AlongWalk();
        walk.Context = context;
        walk.Batch = -1;
        walk.Stopped = false;
        return walk;
    }

    /// <summary>Empties the scratch, for the next window ahead.</summary>
    internal void Restart()
    {
        _owned = 0;
        _candidateCount = 0;
        _previousLarge.Count = 0;
        _large.Count = 0;
        _previousTaken.Count = 0;
        _taken.Count = 0;
    }

    /// <summary>Adds the extent of a segment of the batch's own.</summary>
    internal void AddOwn(ulong start, ulong end)
    {
        if (_own is null || _owned == _own.Length)
        {
            Grow(ref _own, _owned);
        }

        _own![_owned++] = (start, end);
    }

    /// <summary>Adds a small segment of the split batch <paramref name="batch"/> will be.</summary>
    internal void AddCandidate(in SegmentSpec spec, long batch)
    {
        if (_candidates is null || _candidateCount == _candidates.Length)
        {
            Grow(ref _candidates, _candidateCount);
        }

        _candidates![_candidateCount++] = (spec, batch);
    }

    /// <summary>Moves on to the next split: the segments the last one, or the batch, read or took become the previous split's.</summary>
    internal void NextSplit()
    {
        (_previousLarge, _large) = (_large, _previousLarge);
        _large.Count = 0;
        (_previousTaken, _taken) = (_taken, _previousTaken);
        _taken.Count = 0;
    }

    /// <summary>
    /// Adds a large segment of the split, and says whether the split reads it itself: whether the split before it, or the
    /// batch for the first, does not ask for it too.
    /// </summary>
    internal bool AddLarge(in SegmentSpec spec)
    {
        bool shared = _previousLarge.Contains(in spec);
        _large.Add(in spec);
        return !shared;
    }

    /// <summary>Whether the split before, or the batch for the first, took the small segment <paramref name="spec"/>.</summary>
    internal bool TakenBefore(in SegmentSpec spec) => _previousTaken.Contains(in spec);

    /// <summary>
    /// Notes a small segment the split takes, one it reads with its own or one taken before it, and withdraws it from the
    /// segments read along, an earlier split of the window having given it: the split that reads would wait for it.
    /// </summary>
    internal void Take(in SegmentSpec spec)
    {
        _taken.Add(in spec);
        Span<(SegmentSpec Spec, long Batch)> candidates = Candidates;
        for (int c = candidates.Length - 1; c >= 0; c--)
        {
            if (candidates[c].Spec.Offset == spec.Offset && candidates[c].Spec.Length == spec.Length)
            {
                candidates[c].Batch = -1;
            }
        }
    }

    /// <summary>
    /// Forgets the scan, whose plan the cursor would otherwise keep alive in the pool, gives the scratch back, and returns
    /// the context.
    /// </summary>
    internal ScanContext? End()
    {
        ScanContext? context = Context;
        Cursor = default;
        Context = null;
        Restart();
        Return(ref _own);
        Return(ref _candidates);
        _previousLarge.Return();
        _large.Return();
        _previousTaken.Return();
        _taken.Return();
        return context;
    }

    /// <summary>Takes an array of the pool's twice as long as <paramref name="count"/>, or the first, and moves the entries into it.</summary>
    private static void Grow<T>(ref T[]? array, int count)
    {
        T[] grown = ArrayPool<T>.Shared.Rent(count == 0 ? 16 : count * 2);
        if (array is { } held)
        {
            held.AsSpan(0, count).CopyTo(grown);
            ArrayPool<T>.Shared.Return(held);
        }

        array = grown;
    }

    private static void Return<T>(ref T[]? array)
    {
        if (array is { } held)
        {
            array = null;
            ArrayPool<T>.Shared.Return(held);
        }
    }

    /// <summary>The segments of one split, by offset and length, in an array of the pool's.</summary>
    private struct Segments
    {
        private (ulong Offset, uint Length)[]? _items;

        internal int Count;

        internal readonly bool Contains(in SegmentSpec spec)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_items![i].Offset == spec.Offset && _items[i].Length == spec.Length)
                {
                    return true;
                }
            }

            return false;
        }

        internal void Add(in SegmentSpec spec)
        {
            if (_items is null || Count == _items.Length)
            {
                Grow(ref _items, Count);
            }

            _items![Count++] = (spec.Offset, spec.Length);
        }

        internal void Return()
        {
            Count = 0;
            AlongWalk.Return(ref _items);
        }
    }
}
