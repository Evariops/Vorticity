// The keys a locating index sees between two chunk closes (docs/10-indexes.md §6.1, §6.2).
//
// INTERNED BYTES AND A LOG OF (key, position). A value is interned once -- its bytes in one heap,
// an id per distinct value -- and every occurrence the index cares about is appended as a pair: for
// postings the first time a key is seen in a block, for sorted runs every row. The log is in
// ingest order, so positions ascend, and a key's positions ascend with them.
//
// WHY ITS OWN TABLE AND NOT THE DISTINCT TABLE. The writer's running table (11 §3.2.2) lives and
// dies by plan memory: it is off when the last chunk was not a dictionary, and it forgets at every
// chunk. An index cannot share those rules, so it keeps its own, and pays a second hash per row on
// the columns that ask for one -- which the budget and the report account for.
//
// A CHUNK CLOSE CUTS THE LOG. Ingest runs ahead of emission by the rows the writer carries, so when
// a chunk of rows [r, r + n) goes out the log may hold the carried rows' pairs after its own. When
// it does not -- the common case -- the whole table becomes the run and a fresh one starts; when it
// does, the table keeps its ids for the run and only the carried suffix is re-interned.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>Distinct key bytes and the positions they were seen at, for one open chunk.</summary>
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
    /// THE SLOT HASH IS THE WRITE PATH'S SHORT ONE (<see cref="KeyHash.Bytes"/>): a key up to
    /// sixteen bytes folds in one multiply, where XxHash3 would cost a call and three times the
    /// time -- and the table only needs a bucket, since an exact comparison settles every collision.
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
    /// <returns>
    /// The chunk's table. Its key list may hold keys only the kept suffix uses; <see cref="Ranked"/>
    /// leaves those out.
    /// </returns>
    internal ChunkKeys Cut(long end)
    {
        int split = 0;
        while (split < _log.Count && _log[split].Position < end)
        {
            split++;
        }

        ChunkKeys taken = new ChunkKeys();
        taken.Exchange(this);
        if (split == taken._log.Count)
        {
            return taken;
        }

        // Carried rows: re-intern the suffix here, then drop it from the chunk's table.
        List<(int Key, long Position)> log = taken._log;
        Dictionary<int, int> ids = [];
        for (int i = split; i < log.Count; i++)
        {
            (int key, long position) = log[i];
            if (!ids.TryGetValue(key, out int id))
            {
                id = Intern(taken.KeyBytes(key));
                ids[key] = id;
            }

            Note(id, position);
        }

        log.RemoveRange(split, log.Count - split);
        return taken;
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
    internal int[] Ranked(KeyLayout layout)
    {
        bool[] used = new bool[_keys.Count];
        int count = 0;
        foreach ((int key, _) in _log)
        {
            if (!used[key])
            {
                used[key] = true;
                count++;
            }
        }

        int[] order = new int[count];
        int next = 0;
        for (int id = 0; id < used.Length; id++)
        {
            if (used[id])
            {
                order[next++] = id;
            }
        }

        Array.Sort(order, (a, b) => layout.Compare(KeyBytes(a), KeyBytes(b)));
        return order;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_heap.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_heap);
            _heap = [];
        }
    }
}
