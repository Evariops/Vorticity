using System;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// The blobs of the flat layouts a scan context's batches read, kept parsed from one batch to the
/// next: a node arena a blob, with the layout and the segment bytes it was parsed from.
/// </summary>
/// <remarks>
/// <para>
/// A blob's parse is a function of its bytes, and the bytes at an address a batch holds are the
/// bytes the parse read: batch after batch of one chunk finds its root parsed, where each would copy
/// the array flatbuffer again, walk its node tree and resolve every buffer spec against the segment.
/// A kept blob's views name the segment's bytes, and are read again only once a batch holds those
/// bytes at the address they name.
/// </para>
/// <para>
/// An arena no layout of the batch in flight has read is parsed over first, the one read longest
/// ago; while every arena holds a blob the batch reads, another is made, up to
/// <see cref="Capacity"/>, past which the one read longest ago is parsed over. A batch of a wide
/// table reads a blob of every column, so the arenas are found by their layout, through buckets,
/// and kept in the order they were read, through a list, and neither a lookup nor a choice of the
/// arena to parse over walks the others.
/// </para>
/// </remarks>
internal sealed class KeptBlobs
{
    /// <summary>The most blobs kept: more than the columns of all but the widest tables.</summary>
    internal const int Capacity = 256;

    /// <summary>The nodes a kept blob's arena starts with; it grows as the blob needs.</summary>
    private const int SlotNodes = 4;

    /// <summary>The buckets the arenas are found in by layout, a power of two, twice the capacity.</summary>
    private const int BucketCount = 2 * Capacity;

    private Slot[] _slots = [];
    private int _count;
    private int _current = -1;
    private long _clock;
    private long _batchFrom;

    // Per bucket, the first arena holding a blob of a layout of it, or -1; made with the first arena.
    private int[] _buckets = [];

    // The arenas in the order they were read, the latest first.
    private int _latest = -1;
    private int _oldest = -1;

    /// <summary>The arena of the blob made current in the batch in flight, or null.</summary>
    internal ArrayNodeArena? Current => _current >= 0 ? _slots[_current].Arena : null;

    /// <summary>
    /// Whether the current blob's root decodes a range of its rows and materializes them, once a
    /// flat reader has asked; null before, or when no blob is current.
    /// </summary>
    internal bool? CurrentRanged
    {
        get => _current >= 0 ? _slots[_current].Ranged : null;
        set
        {
            if (_current >= 0)
            {
                _slots[_current].Ranged = value;
            }
        }
    }

    /// <summary>
    /// Makes current the arena holding flat layout <paramref name="layout"/>'s blob as parsed from
    /// <paramref name="segment"/>; or, when none does, the one to parse it into, holding nothing
    /// until <see cref="RememberCurrent"/>.
    /// </summary>
    /// <param name="layout">The flat layout's index in the layout tree.</param>
    /// <param name="segment">Its segment, as the batch in flight holds it.</param>
    /// <param name="treeBytes">The tree buffer a new arena starts with.</param>
    /// <returns>Whether the current arena holds the blob parsed.</returns>
    internal bool Take(int layout, VortexBuffer segment, int treeBytes)
    {
        long now = ++_clock;
        int found = Find(layout);
        if (found >= 0 && _slots[found].Source.SameAs(segment))
        {
            Read(found, now);
            _current = found;
            return true;
        }

        // The layout's blob from other bytes is parsed over where it lies; another layout takes
        // the arena read longest ago when no layout of this batch read it, or the arenas are all
        // there may be, and a new arena otherwise.
        int taken = found >= 0
            ? found
            : _oldest >= 0 && (_slots[_oldest].LastUse < _batchFrom || _count == Capacity)
                ? _oldest
                : Add(treeBytes);
        Unhold(taken);
        ref Slot target = ref _slots[taken];
        target.Source = default;
        target.Ranged = null;
        Read(taken, now);
        _current = taken;
        return false;
    }

    /// <summary>Records that the current arena holds flat layout <paramref name="layout"/>'s blob, parsed from <paramref name="segment"/>.</summary>
    /// <param name="layout">The flat layout's index in the layout tree.</param>
    /// <param name="segment">The segment the blob was parsed from.</param>
    internal void RememberCurrent(int layout, VortexBuffer segment)
    {
        if (_current < 0)
        {
            return;
        }

        Unhold(_current);
        ref Slot slot = ref _slots[_current];
        slot.Layout = layout;
        slot.Source = segment;
        int bucket = Bucket(layout);
        slot.Chain = _buckets[bucket];
        _buckets[bucket] = _current;
    }

    /// <summary>Forgets what the current arena holds, before a blob is loaded into it.</summary>
    internal void ForgetCurrent()
    {
        if (_current >= 0)
        {
            Unhold(_current);
            _slots[_current].Source = default;
            _slots[_current].Ranged = null;
        }
    }

    /// <summary>Starts a batch: no blob is current, and an arena last used before now holds none the batch reads yet.</summary>
    internal void NextBatch()
    {
        _current = -1;
        _batchFrom = _clock + 1;
    }

    /// <summary>Forgets every blob, facts about a file's bytes, keeping the arenas for the next file.</summary>
    internal void Forget()
    {
        for (int i = 0; i < _count; i++)
        {
            ref Slot slot = ref _slots[i];
            slot.Arena.Reset();
            slot.Layout = -1;
            slot.Chain = -1;
            slot.Source = default;
            slot.Ranged = null;
        }

        if (_buckets.Length > 0)
        {
            _buckets.AsSpan().Fill(-1);
        }

        _current = -1;
    }

    /// <summary>The arena holding a blob of <paramref name="layout"/>, or -1.</summary>
    private int Find(int layout)
    {
        if (_count == 0)
        {
            return -1;
        }

        for (int at = _buckets[Bucket(layout)]; at >= 0; at = _slots[at].Chain)
        {
            if (_slots[at].Layout == layout)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Takes arena <paramref name="at"/> out of its layout's bucket, holding no layout's blob.</summary>
    private void Unhold(int at)
    {
        int layout = _slots[at].Layout;
        if (layout < 0)
        {
            return;
        }

        int bucket = Bucket(layout);
        if (_buckets[bucket] == at)
        {
            _buckets[bucket] = _slots[at].Chain;
        }
        else
        {
            int before = _buckets[bucket];
            while (_slots[before].Chain != at)
            {
                before = _slots[before].Chain;
            }

            _slots[before].Chain = _slots[at].Chain;
        }

        _slots[at].Layout = -1;
        _slots[at].Chain = -1;
    }

    /// <summary>Marks arena <paramref name="at"/> read at <paramref name="now"/>: the latest read.</summary>
    private void Read(int at, long now)
    {
        ref Slot slot = ref _slots[at];
        slot.LastUse = now;
        if (_latest == at)
        {
            return;
        }

        // Out of the order where it was, then in at the front.
        if (slot.Later >= 0)
        {
            _slots[slot.Later].Earlier = slot.Earlier;
        }

        if (slot.Earlier >= 0)
        {
            _slots[slot.Earlier].Later = slot.Later;
        }

        if (_oldest == at)
        {
            _oldest = slot.Later;
        }

        slot.Earlier = _latest;
        slot.Later = -1;
        if (_latest >= 0)
        {
            _slots[_latest].Later = at;
        }

        _latest = at;
        if (_oldest < 0)
        {
            _oldest = at;
        }
    }

    private static int Bucket(int layout) => (int)((uint)layout * 0x9E3779B1u >> 23) & (BucketCount - 1);

    private int Add(int treeBytes)
    {
        if (_count == _slots.Length)
        {
            Array.Resize(ref _slots, Math.Max(4, Math.Min(_slots.Length * 2, Capacity)));
        }

        if (_buckets.Length == 0)
        {
            _buckets = new int[BucketCount];
            _buckets.AsSpan().Fill(-1);
        }

        _slots[_count] = new Slot
        {
            Arena = new ArrayNodeArena(SlotNodes, treeBytes),
            Layout = -1,
            Chain = -1,
            Earlier = -1,
            Later = -1,
        };
        return _count++;
    }

    /// <summary>
    /// A kept blob: its arena, the flat layout it is, or -1, the segment it was parsed from, the next
    /// arena of its layout's bucket, and its neighbours in the order the arenas were read.
    /// </summary>
    private struct Slot
    {
        internal ArrayNodeArena Arena;
        internal int Layout;
        internal int Chain;
        internal int Earlier;
        internal int Later;
        internal VortexBuffer Source;
        internal long LastUse;
        internal bool? Ranged;
    }
}
