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
/// An arena no layout of the batch in flight has read is parsed over first; while every arena
/// holds a blob the batch reads, another is made, up to <see cref="Capacity"/>, past which the
/// oldest is parsed over and a batch reading that many layouts parses each blob again.
/// </para>
/// </remarks>
internal sealed class KeptBlobs
{
    /// <summary>The most blobs kept.</summary>
    internal const int Capacity = 64;

    /// <summary>The nodes a kept blob's arena starts with; it grows as the blob needs.</summary>
    private const int SlotNodes = 4;

    private Slot[] _slots = [];
    private int _count;
    private int _current = -1;
    private long _clock;
    private long _batchFrom;

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
        int oldest = -1;
        for (int i = 0; i < _count; i++)
        {
            ref Slot slot = ref _slots[i];
            if (slot.Layout == layout && slot.Source.SameAs(segment))
            {
                slot.LastUse = now;
                _current = i;
                return true;
            }

            if (oldest < 0 || slot.LastUse < _slots[oldest].LastUse)
            {
                oldest = i;
            }
        }

        int taken = oldest >= 0 && (_slots[oldest].LastUse < _batchFrom || _count == Capacity)
            ? oldest
            : Add(treeBytes);
        ref Slot target = ref _slots[taken];
        target.Layout = -1;
        target.Source = default;
        target.Ranged = null;
        target.LastUse = now;
        _current = taken;
        return false;
    }

    /// <summary>Records that the current arena holds flat layout <paramref name="layout"/>'s blob, parsed from <paramref name="segment"/>.</summary>
    /// <param name="layout">The flat layout's index in the layout tree.</param>
    /// <param name="segment">The segment the blob was parsed from.</param>
    internal void RememberCurrent(int layout, VortexBuffer segment)
    {
        if (_current >= 0)
        {
            _slots[_current].Layout = layout;
            _slots[_current].Source = segment;
        }
    }

    /// <summary>Forgets what the current arena holds, before a blob is loaded into it.</summary>
    internal void ForgetCurrent()
    {
        if (_current >= 0)
        {
            _slots[_current].Layout = -1;
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
            slot.Source = default;
            slot.Ranged = null;
        }

        _current = -1;
    }

    private int Add(int treeBytes)
    {
        if (_count == _slots.Length)
        {
            Array.Resize(ref _slots, Math.Max(4, _slots.Length * 2));
        }

        _slots[_count] = new Slot { Arena = new ArrayNodeArena(SlotNodes, treeBytes), Layout = -1 };
        return _count++;
    }

    /// <summary>A kept blob: its arena, the flat layout it is, or -1, and the segment it was parsed from.</summary>
    private struct Slot
    {
        internal ArrayNodeArena Arena;
        internal int Layout;
        internal VortexBuffer Source;
        internal long LastUse;
        internal bool? Ranged;
    }
}
