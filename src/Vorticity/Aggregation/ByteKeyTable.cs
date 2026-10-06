using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// Byte strings numbered in the order they are first seen: an open-addressing table over XxHash3,
/// the keys packed in one buffer. It holds text keys, the row-encoded tuples of a composite key and
/// the (group, value) pairs of a distinct count, with no allocation per key.
/// </summary>
/// <remarks>
/// <para>
/// A probe reads two lines (PLAN-HIGH-CARDINALITY, H15): its slot, the high half of the key's hash
/// and where the key lies, then, when the half matches, the key itself, its number and its length
/// ahead of its bytes. A slot holding the number alone sent the probe on to the key's hash, place and
/// length, each in an array of its own: five lines. A slot of 8 bytes, where one of 16 held the number
/// and the length too, took 24 MiB more at a million keys.
/// </para>
/// <para>
/// A key comes hashed under <see cref="MergeHash.Seed"/>, the hash a merge cuts the parts by, so that
/// it is hashed once. Keys can be built to share the low bits of that hash, and so one run of the
/// probe sequence, by whoever learns the seed: a key that would land <see cref="MaxProbes"/> slots
/// past its own reseeds the hash with a value of the table's own and rehashes every key, which such
/// keys cannot have been built against. Until then every key sits closer than that to its own slot,
/// so no lookup probes further: a growth places the keys again in the order they came, and none
/// lands further than it was.
/// </para>
/// </remarks>
internal sealed class ByteKeyTable
{
    /// <summary>How far past its own slot a key lands before the table reseeds: linear probing at half load keeps its runs far shorter.</summary>
    private const int MaxProbes = 128;

    /// <summary>The most bytes ahead of a key's: its number, then its length in 7-bit groups.</summary>
    private const int MostAhead = sizeof(int) + 5;

    // Each key's number, its length and its bytes, one after the other, in the order keys came.
    private byte[] _bytes = new byte[1024];
    private int _used;

    // Where each key lies, -1 for a detached number; the hash it was placed by.
    private int[] _offsets = new int[16];
    private ulong[] _hashes = new ulong[16];
    private Slot[] _slots = new Slot[32];
    private long _seed;

    // The lane's shelf its arrays grow from, under the query's memory; null for a table nothing counts.
    private readonly ArrayShelf? _shelf;

    /// <summary>A table whose arrays grow from <paramref name="shelf"/>, which reserves each before it comes (PLAN-HIGH-CARDINALITY, H2); new ones when null.</summary>
    internal ByteKeyTable(ArrayShelf? shelf = null) => _shelf = shelf;

    /// <summary>The number of distinct keys.</summary>
    internal int Count { get; private set; }

    /// <summary>Whether a key landed far enough from its own slot for the table to take a seed.</summary>
    internal bool Reseeded => _seed != 0;

    /// <summary>The bytes the table holds at its capacity: the keys, where each lies, their hashes and the slots.</summary>
    internal long Footprint =>
        _bytes.Length + ((long)_offsets.Length * sizeof(int)) + ((long)_hashes.Length * sizeof(ulong)) + ((long)_slots.Length * Unsafe.SizeOf<Slot>());

    /// <summary>The number of <paramref name="key"/>, which is added when it is new.</summary>
    internal int GetOrAdd(ReadOnlySpan<byte> key, out bool added) => GetOrAdd(key, MergeHash.Of(key, MergeHash.Seed), out added);

    /// <summary>The number of <paramref name="key"/>, whose hash under <see cref="MergeHash.Seed"/> is <paramref name="hash"/>; added when it is new.</summary>
    internal int GetOrAdd(ReadOnlySpan<byte> key, ulong hash, out bool added)
    {
        if (_seed != 0)
        {
            hash = XxHash3.HashToUInt64(key, _seed);
        }

        uint half = (uint)(hash >> 32);
        int mask = _slots.Length - 1;
        int at = (int)hash & mask;
        while (true)
        {
            Slot slot = _slots[at];
            if (slot.Place == 0)
            {
                break;
            }

            if (slot.Half == half)
            {
                int offset = slot.Place - 1;
                ReadOnlySpan<byte> stored = Stored(offset);
                if (stored.SequenceEqual(key))
                {
                    added = false;
                    return NumberAt(offset);
                }
            }

            at = (at + 1) & mask;
        }

        if (((at - (int)hash) & mask) >= MaxProbes && _seed == 0)
        {
            Reseed();
            return GetOrAdd(key, hash, out added);
        }

        int number = Count;
        Append(key, hash);
        _slots[at] = new Slot { Half = half, Place = _offsets[number] + 1 };
        added = true;
        if (Count * 2 > _slots.Length)
        {
            Rehash(NewSlots(GroupKeys.Doubled(_slots.Length)));
        }

        return number;
    }

    /// <summary>The hash key <paramref name="index"/> was placed by: under <see cref="MergeHash.Seed"/> until the table is <see cref="Reseeded"/>.</summary>
    internal ulong HashOf(int index) => _hashes[index];

    /// <summary>
    /// A number no key finds: an entry without bytes that no lookup reaches, for a group with no key,
    /// the null of a text key, whose groups are then the table's entries (PLAN-HIGH-CARDINALITY, H1,
    /// reduction 6). Its bytes are never read.
    /// </summary>
    internal int AddDetached()
    {
        int number = Count;
        Grow(number + 1);
        _offsets[number] = -1;
        _hashes[number] = 0;
        Count = number + 1;
        return number;
    }

    /// <summary>Forgets every key, keeping the buffers and the seed.</summary>
    internal void Clear()
    {
        Count = 0;
        _used = 0;
        Array.Clear(_slots);
    }

    /// <summary>The bytes of key <paramref name="index"/>.</summary>
    internal ReadOnlySpan<byte> KeyOf(int index) => Stored(_offsets[index]);

    /// <summary>
    /// Keeps keys <paramref name="entries"/> alone, key <c>entries[i]</c> becoming key <c>i</c>: their
    /// bytes moved to the front in place, their numbers written again, and the slots filled again from
    /// the hashes kept, so that no key is copied out or hashed again. <paramref name="entries"/> ascend.
    /// </summary>
    internal void Retain(ReadOnlySpan<int> entries)
    {
        int used = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            // Keys only move toward the front, into the room of those left out before them; a
            // detached entry has no bytes to move.
            int entry = entries[i];
            int offset = _offsets[entry];
            _hashes[i] = _hashes[entry];
            if (offset < 0)
            {
                _offsets[i] = -1;
                continue;
            }

            int length = sizeof(int) + Ahead(offset, out int keyLength) + keyLength;
            _bytes.AsSpan(offset, length).CopyTo(_bytes.AsSpan(used));
            Unsafe.WriteUnaligned(ref _bytes[used], i);
            _offsets[i] = used;
            used += length;
        }

        Count = entries.Length;
        _used = used;
        Array.Clear(_slots);
        Rehash(_slots);
    }

    /// <summary>The key whose number lies at <paramref name="offset"/>: its bytes, past its number and length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Stored(int offset)
    {
        int ahead = Ahead(offset, out int length);
        return _bytes.AsSpan(offset + sizeof(int) + ahead, length);
    }

    /// <summary>The number of the key at <paramref name="offset"/>.</summary>
    private int NumberAt(int offset) => Unsafe.ReadUnaligned<int>(ref _bytes[offset]);

    /// <summary>The length of the key at <paramref name="offset"/>, in 7-bit groups past its number; the bytes they take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Ahead(int offset, out int length)
    {
        ReadOnlySpan<byte> bytes = _bytes.AsSpan(offset + sizeof(int));
        byte first = bytes[0];
        if (first < 0x80)
        {
            length = first;
            return 1;
        }

        uint value = 0;
        int shift = 0;
        int read = 0;
        byte part;
        do
        {
            part = bytes[read++];
            value |= (uint)(part & 0x7F) << shift;
            shift += 7;
        }
        while ((part & 0x80) != 0);

        length = (int)value;
        return read;
    }

    private void Append(ReadOnlySpan<byte> key, ulong hash)
    {
        int index = Count;
        Grow(index + 1);
        if (_used + MostAhead + key.Length > _bytes.Length)
        {
            ArrayShelf.Resize(_shelf, ref _bytes, Scratch.Capacity(_used + MostAhead + key.Length, _bytes.Length));
        }

        int at = _used;
        Unsafe.WriteUnaligned(ref _bytes[at], index);
        at += sizeof(int);
        uint length = (uint)key.Length;
        while (length >= 0x80)
        {
            _bytes[at++] = (byte)(length | 0x80);
            length >>= 7;
        }

        _bytes[at++] = (byte)length;
        key.CopyTo(_bytes.AsSpan(at));
        _offsets[index] = _used;
        _hashes[index] = hash;
        _used = at + key.Length;
        Count = index + 1;
    }

    /// <summary>Room for <paramref name="count"/> numbers.</summary>
    private void Grow(int count)
    {
        if (count > _offsets.Length)
        {
            int grown = Math.Max(count, GroupKeys.Doubled(_offsets.Length));
            ArrayShelf.Resize(_shelf, ref _offsets, grown);
            ArrayShelf.Resize(_shelf, ref _hashes, grown);
        }
    }

    /// <summary>Hashes every key again under a seed of the table's own.</summary>
    private void Reseed()
    {
        _seed = Random.Shared.NextInt64(1, long.MaxValue);
        for (int index = 0; index < Count; index++)
        {
            if (_offsets[index] >= 0)
            {
                _hashes[index] = XxHash3.HashToUInt64(KeyOf(index), _seed);
            }
        }

        Rehash(NewSlots(_slots.Length));
    }

    /// <summary>Empty slots, from the shelf when the table has one.</summary>
    private Slot[] NewSlots(int length) => _shelf is null ? new Slot[length] : _shelf.Take<Slot>(length, zeroed: true);

    /// <summary>Places every key in <paramref name="slots"/>, empty, by the hash it keeps, and makes them the table's; the slots it held given back.</summary>
    private void Rehash(Slot[] slots)
    {
        Slot[] old = _slots;
        int mask = slots.Length - 1;
        for (int index = 0; index < Count; index++)
        {
            int offset = _offsets[index];
            if (offset < 0)
            {
                continue;
            }

            ulong hash = _hashes[index];
            int at = (int)hash & mask;
            while (slots[at].Place != 0)
            {
                at = (at + 1) & mask;
            }

            slots[at] = new Slot { Half = (uint)(hash >> 32), Place = offset + 1 };
        }

        _slots = slots;
        if (!ReferenceEquals(old, slots))
        {
            _shelf?.Give(old);
        }
    }

    /// <summary>What a probe reads first: the high half of a key's hash, and where its number lies plus one (zero, an empty slot).</summary>
    private struct Slot
    {
        public uint Half;
        public int Place;
    }
}
