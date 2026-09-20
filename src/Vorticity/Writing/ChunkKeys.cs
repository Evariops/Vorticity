using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>
/// Distinct key bytes and the positions they were seen at, for one open chunk: a value's bytes are
/// interned once in a heap under an id, and every occurrence a locating index cares about is
/// appended to a log of (id, position) pairs. The log is in ingest order, so positions ascend and a
/// key's positions ascend with them.
/// </summary>
/// <remarks>
/// This table is the index's own and not the writer's running distinct table, which lives and dies
/// by plan memory and forgets at every chunk; an index cannot follow those rules, so it keeps its
/// own table and pays a second hash per row on the columns that ask for one.
/// </remarks>
internal sealed class ChunkKeys : IDisposable
{
    private byte[] _heap = ArrayPool<byte>.Shared.Rent(1 << 12);
    private int _heapUsed;
    private List<(int Key, long Position)> _log = [];
    private List<(int Offset, int Length)> _keys = [];
    private int[] _slots = NewSlots(1 << 10);

    /// <summary>The pairs, in ingest order.</summary>
    internal List<(int Key, long Position)> Log => _log;

    /// <summary>Bytes the table holds, for the budget and the report.</summary>
    internal long Bytes => _heapUsed + ((long)_keys.Count * 8) + ((long)_log.Count * 16);

    /// <summary>A key's bytes.</summary>
    /// <param name="key">The id.</param>
    internal ReadOnlySpan<byte> KeyBytes(int key)
    {
        (int offset, int length) = _keys[key];
        return _heap.AsSpan(offset, length);
    }

    /// <summary>The id of <paramref name="bytes"/>, interning it on first sight.</summary>
    /// <param name="bytes">The key.</param>
    /// <remarks>
    /// The slot comes from the write path's short hash, <see cref="KeyHash.Bytes"/>, which folds a
    /// small key in a multiply where a general-purpose hash costs a call: the table only needs a
    /// bucket, since an exact comparison settles every collision.
    /// </remarks>
    internal int Intern(ReadOnlySpan<byte> bytes)
    {
        int[] slots = _slots;
        int mask = slots.Length - 1;
        int slot = (int)KeyHash.Bytes(bytes) & mask;
        while (true)
        {
            int held = slots[slot];
            if (held < 0)
            {
                int id = Append(bytes);
                slots[slot] = id;
                if (_keys.Count * 2 > slots.Length)
                {
                    Rehash();
                }

                return id;
            }

            if (KeyBytes(held).SequenceEqual(bytes))
            {
                return held;
            }

            slot = (slot + 1) & mask;
        }
    }

    private int Append(ReadOnlySpan<byte> bytes)
    {
        if (_heapUsed + bytes.Length > _heap.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_heap.Length * 2, _heapUsed + bytes.Length));
            _heap.AsSpan(0, _heapUsed).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_heap);
            _heap = grown;
        }

        bytes.CopyTo(_heap.AsSpan(_heapUsed));
        _keys.Add((_heapUsed, bytes.Length));
        _heapUsed += bytes.Length;
        return _keys.Count - 1;
    }

    private void Rehash()
    {
        int[] slots = NewSlots(_slots.Length * 2);
        int mask = slots.Length - 1;
        for (int id = 0; id < _keys.Count; id++)
        {
            int slot = (int)KeyHash.Bytes(KeyBytes(id)) & mask;
            while (slots[slot] >= 0)
            {
                slot = (slot + 1) & mask;
            }

            slots[slot] = id;
        }

        _slots = slots;
    }

    private static int[] NewSlots(int length)
    {
        int[] slots = new int[length];
        slots.AsSpan().Fill(-1);
        return slots;
    }

    /// <summary>Records one occurrence.</summary>
    /// <param name="key">The id.</param>
    /// <param name="position">A block or a row, as the index counts.</param>
    internal void Note(int key, long position) => _log.Add((key, position));

    /// <summary>
    /// Takes the pairs whose position is below <paramref name="end"/> as a table of their own, and
    /// keeps the rest.
    /// </summary>
    /// <param name="end">The first position the chunk does not cover.</param>
    /// <param name="taken">
    /// An empty table that becomes the chunk's. Its key list may hold keys only the kept suffix
    /// uses; <see cref="Ranked"/> leaves those out.
    /// </param>
    /// <remarks>
    /// The two tables trade places here and trade back in <see cref="Reclaim"/>, so the buffers that
    /// grew to a chunk's size stay with the open table and the spare only ever holds a carried
    /// suffix: a builder that keeps both allocates nothing per chunk once the first has grown.
    /// </remarks>
    internal void Cut(long end, ChunkKeys taken)
    {
        int split = 0;
        while (split < _log.Count && _log[split].Position < end)
        {
            split++;
        }

        taken.Exchange(this);
        if (split == taken._log.Count)
        {
            return;
        }

        // Carried rows: re-intern the suffix here, then drop it from the chunk's table.
        taken.CopyInto(this, split);
        taken._log.RemoveRange(split, taken._log.Count - split);
    }

    /// <summary>
    /// Takes back the buffers of the table <see cref="Cut"/> filled, once its run is built: the
    /// carried pairs move into it and the two trade places again, <paramref name="spent"/> ending
    /// empty.
    /// </summary>
    /// <param name="spent">The chunk's table, already read and free to reuse.</param>
    internal void Reclaim(ChunkKeys spent)
    {
        spent.Reset();
        CopyInto(spent, 0);
        Exchange(spent);
        spent.Reset();
    }

    /// <summary>Re-interns the pairs from <paramref name="from"/> on into <paramref name="target"/>.</summary>
    private void CopyInto(ChunkKeys target, int from)
    {
        if (from == _log.Count)
        {
            return;
        }

        int keys = _keys.Count;
        int[] ids = ArrayPool<int>.Shared.Rent(keys);
        ids.AsSpan(0, keys).Fill(-1);
        for (int i = from; i < _log.Count; i++)
        {
            (int key, long position) = _log[i];
            if (ids[key] < 0)
            {
                ids[key] = target.Intern(KeyBytes(key));
            }

            target.Note(ids[key], position);
        }

        ArrayPool<int>.Shared.Return(ids);
    }

    private void Reset()
    {
        _heapUsed = 0;
        _keys.Clear();
        _log.Clear();
        _slots.AsSpan().Fill(-1);
    }

    private void Exchange(ChunkKeys other)
    {
        (_heap, other._heap) = (other._heap, _heap);
        (_heapUsed, other._heapUsed) = (other._heapUsed, _heapUsed);
        (_slots, other._slots) = (other._slots, _slots);
        (_keys, other._keys) = (other._keys, _keys);
        (_log, other._log) = (other._log, _log);
    }

    /// <summary>The ids the log uses, in the layout's total order.</summary>
    /// <param name="layout">How the bytes compare.</param>
    /// <param name="ranked">
    /// The ids, in an array rented from <see cref="ArrayPool{T}.Shared"/> that the caller returns.
    /// </param>
    /// <returns>How many ids <paramref name="ranked"/> holds.</returns>
    internal int Ranked(KeyLayout layout, out int[] ranked)
    {
        int keys = _keys.Count;
        bool[] used = ArrayPool<bool>.Shared.Rent(keys);
        used.AsSpan(0, keys).Clear();
        int count = 0;
        foreach ((int key, _) in _log)
        {
            if (!used[key])
            {
                used[key] = true;
                count++;
            }
        }

        ranked = ArrayPool<int>.Shared.Rent(count);
        int next = 0;
        for (int id = 0; id < keys; id++)
        {
            if (used[id])
            {
                ranked[next++] = id;
            }
        }

        ArrayPool<bool>.Shared.Return(used);

        // The sort keys are the table's own buffer, grown once to the largest chunk, as the log is.
        if (_order.Length < count)
        {
            _order = new ulong[BitOperations.RoundUpToPowerOf2((uint)count)];
        }

        if (layout.Shape == KeyShape.Bytes)
        {
            SortBytes(ranked.AsSpan(0, count), _order.AsSpan(0, count), 0, layout);
            return count;
        }

        // A fixed width sorts as integers: the layout maps each key one-to-one onto an unsigned
        // integer in its total order, so the primitive sort -- no comparer, no byte reads per
        // comparison -- gives the order the comparer would, ties being impossible. Sorting such a
        // column through the comparer instead dominates the cost of writing a sorted run.
        for (int i = 0; i < count; i++)
        {
            _order[i] = layout.SortKey(KeyBytes(ranked[i]));
        }

        _order.AsSpan(0, count).Sort(ranked.AsSpan(0, count));
        return count;
    }

    private ulong[] _order = [];

    /// <summary>Below this many ids, a run is sorted by the comparer.</summary>
    private const int SmallRun = 16;

    /// <summary>
    /// Sorts <paramref name="ids"/>, whose keys agree on their first <paramref name="offset"/>
    /// bytes, bytewise: eight bytes at a time as big-endian integers, the runs that tie sorted again
    /// on the next eight.
    /// </summary>
    /// <remarks>
    /// This gives the comparer's order without its cost. A window is the key's bytes from
    /// <paramref name="offset"/>, zero-padded past its end, so a smaller window is a smaller key and
    /// only equal windows need more: keys that go on past the window are sorted on the next one, and
    /// keys that all end inside it differ only by trailing zeros, which the comparer orders by
    /// length. Keys are distinct, so the order is total and the same the comparer would give, while
    /// a comparer sort re-reads the bytes at every comparison -- ruinous on a composite key, whose
    /// row encoding repeats its leading column's bytes across whole chunks.
    /// <paramref name="windows"/> is indexed like <paramref name="ids"/>, so a nested sort
    /// overwrites only the entries of its own run.
    /// </remarks>
    private void SortBytes(Span<int> ids, Span<ulong> windows, int offset, KeyLayout layout)
    {
        if (ids.Length <= SmallRun)
        {
            ids.Sort(new ByKey(this, layout));
            return;
        }

        // A window every key shares -- a composite key's padding, its leading column's value in a
        // run of it -- is skipped rather than sorted: the loop moves to the next one.
        int next;
        bool longer;
        while (true)
        {
            longer = false;
            next = offset + sizeof(ulong);
            bool shared = true;
            for (int i = 0; i < ids.Length; i++)
            {
                ReadOnlySpan<byte> key = KeyBytes(ids[i]);
                windows[i] = Window(key, offset);
                shared &= windows[i] == windows[0];
                longer |= key.Length > next;
            }

            if (!shared)
            {
                break;
            }

            if (!longer)
            {
                ids.Sort(new ByKey(this, layout));
                return;
            }

            offset = next;
        }

        windows.Sort(ids);
        for (int start = 0; start < ids.Length;)
        {
            int end = start + 1;
            bool runLonger = KeyBytes(ids[start]).Length > next;
            while (end < ids.Length && windows[end] == windows[start])
            {
                runLonger |= KeyBytes(ids[end]).Length > next;
                end++;
            }

            if (end - start > 1)
            {
                if (runLonger)
                {
                    SortBytes(ids[start..end], windows[start..end], next, layout);
                }
                else
                {
                    ids[start..end].Sort(new ByKey(this, layout));
                }
            }

            start = end;
        }
    }

    /// <summary>Bytes <c>[offset, offset + 8)</c> of a key, zero-padded past its end, as a big-endian integer.</summary>
    private static ulong Window(ReadOnlySpan<byte> key, int offset)
    {
        if (key.Length >= offset + sizeof(ulong))
        {
            return BinaryPrimitives.ReadUInt64BigEndian(key.Slice(offset, sizeof(ulong)));
        }

        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        padded.Clear();
        if (offset < key.Length)
        {
            key[offset..].CopyTo(padded);
        }

        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    private readonly struct ByKey(ChunkKeys table, KeyLayout layout) : IComparer<int>
    {
        public int Compare(int x, int y) => layout.Compare(table.KeyBytes(x), table.KeyBytes(y));
    }

    public void Dispose()
    {
        if (_heap.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_heap);
            _heap = [];
        }
    }
}
