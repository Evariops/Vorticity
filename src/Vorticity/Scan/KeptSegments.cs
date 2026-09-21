using System;
using System.Buffers;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Scanning;

/// <summary>
/// The segments one lane's last batch read, held for its next one.
/// </summary>
/// <remarks>
/// <para>
/// A segment spans every block of its chunk, and a batch is one block, so consecutive batches ask
/// for the same segment over and over: a scan of a million rows in 123 blocks asked its source for
/// 247 specifications, of which five were distinct, and for 184 725 367 bytes of a 1 523 369-byte
/// file. Over a memory mapping that repetition is free, because a segment is a view; over anything
/// that copies -- a positional read, an object store -- it is the whole cost of the scan.
/// </para>
/// <para>
/// Holding them does not raise the peak. The source already rents the whole segment for one batch
/// and drops its own reference when the batch ends, so the bytes were live anyway; what changes is
/// that they are not rented, read and returned once per block. An entry the next batch does not ask
/// for is released before that batch reads, so what is held is bounded by one batch's registered
/// segments, as it was.
/// </para>
/// <para>
/// A struct held by the lane, over one pooled array: a scan's allocation is held to a ceiling
/// counted in bytes, and an object of its own would be most of what this costs. One per lane, and a
/// lane is read by one flow at a time, so nothing here locks.
/// </para>
/// </remarks>
internal struct KeptSegments
{
    private Entry[]? _entries;
    private int _count;

    /// <summary>
    /// Fills the slots this lane already holds, and releases what this batch does not ask for.
    /// Called on a registered, unpopulated set, before the source reads.
    /// </summary>
    internal void Prepare(SegmentRequestSet requests)
    {
        if (_count == 0)
        {
            return;
        }

        Entry[] entries = _entries!;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            int slot = Find(requests, entries[i].Offset, entries[i].Length);
            if (slot < 0)
            {
                entries[i].Owner.Release();
                entries[i] = default;
                continue;
            }

            requests.SetSharedResult(slot, entries[i].Owner, entries[i].View);
            entries[kept++] = entries[i];
        }

        for (int i = kept; i < _count; i++)
        {
            entries[i] = default;
        }

        _count = kept;
    }

    /// <summary>
    /// Takes a reference on every segment this batch read, so the next batch can reuse it. Called
    /// once the set is populated.
    /// </summary>
    internal void Adopt(SegmentRequestSet requests)
    {
        int registered = requests.Count;
        if (registered == 0)
        {
            return;
        }

        // Rented rather than allocated: a scan's allocation is held to a ceiling counted in bytes,
        // and this array is the same size on every batch of every scan.
        _entries ??= ArrayPool<Entry>.Shared.Rent(registered);
        for (int slot = 0; slot < registered; slot++)
        {
            SegmentSpec spec = requests.GetSpec(slot);
            if (Held(spec.Offset, spec.Length))
            {
                continue;
            }

            if (_count == _entries.Length)
            {
                Entry[] bigger = ArrayPool<Entry>.Shared.Rent(_entries.Length * 2);
                Array.Copy(_entries, bigger, _count);
                ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
                _entries = bigger;
            }

            _entries[_count++] = new Entry(
                spec.Offset, spec.Length, requests.GetOwner(slot).Retain(), requests.GetBuffer(slot));
        }
    }

    /// <summary>Releases everything held and gives the array back. Safe to call twice.</summary>
    internal void Clear()
    {
        if (_entries is not { } entries)
        {
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            entries[i].Owner.Release();
        }

        _count = 0;
        _entries = null;
        ArrayPool<Entry>.Shared.Return(entries, clearArray: true);
    }

    private bool Held(ulong offset, uint length)
    {
        Entry[] entries = _entries!;
        for (int i = 0; i < _count; i++)
        {
            if (entries[i].Offset == offset && entries[i].Length == length)
            {
                return true;
            }
        }

        return false;
    }

    private static int Find(SegmentRequestSet requests, ulong offset, uint length)
    {
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentSpec spec = requests.GetSpec(slot);
            if (spec.Offset == offset && spec.Length == length)
            {
                return slot;
            }
        }

        return -1;
    }

    private readonly record struct Entry(ulong Offset, uint Length, SegmentOwner Owner, VortexBuffer View);
}
