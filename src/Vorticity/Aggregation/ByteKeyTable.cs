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
/// A slot holds what a probe compares (PLAN-HIGH-CARDINALITY, H15): the high half of the key's hash,
/// its number, where its bytes lie and how many. A probe reads its slot, then the bytes of a key whose
/// half and length match: two lines, where a slot holding the number alone sent the probe on to the
/// hash, the place and the length of the key, each in an array of its own.
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

    private byte[] _bytes = new byte[1024];
    private int _used;
    private int[] _offsets = new int[16];
    private int[] _lengths = new int[16];
    private ulong[] _hashes = new ulong[16];
    private Slot[] _slots = new Slot[32];
    private long _seed;

    /// <summary>The number of distinct keys.</summary>
    internal int Count { get; private set; }

    /// <summary>Whether a key landed far enough from its own slot for the table to take a seed.</summary>
    internal bool Reseeded => _seed != 0;

    /// <summary>The bytes the table holds at its capacity: the keys' bytes, where each lies, their hashes and the slots.</summary>
    internal long Footprint =>
        _bytes.Length + ((long)(_offsets.Length + _lengths.Length) * sizeof(int)) + ((long)_hashes.Length * sizeof(ulong)) + ((long)_slots.Length * Unsafe.SizeOf<Slot>());

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
            ref Slot slot = ref _slots[at];
            if (slot.Entry == 0)
            {
                break;
            }

            if (slot.Half == half && slot.Length == key.Length && _bytes.AsSpan(slot.Offset, slot.Length).SequenceEqual(key))
            {
                added = false;
                return slot.Entry - 1;
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
        _slots[at] = new Slot { Half = half, Entry = number + 1, Offset = _offsets[number], Length = key.Length };
        added = true;
        if (Count * 2 > _slots.Length)
        {
            Rehash(new Slot[_slots.Length * 2]);
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
        Append([], 0);
        _lengths[number] = -1;
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
    internal ReadOnlySpan<byte> KeyOf(int index) => _bytes.AsSpan(_offsets[index], _lengths[index]);

    /// <summary>
    /// Keeps keys <paramref name="entries"/> alone, key <c>entries[i]</c> becoming key <c>i</c>: their
    /// bytes moved to the front in place and the slots filled again from the hashes kept, so that no
    /// key is copied out or hashed again. <paramref name="entries"/> ascend.
    /// </summary>
    internal void Retain(ReadOnlySpan<int> entries)
    {
        int used = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            // Keys only move toward the front, into the room of those left out before them; a
            // detached entry has no bytes to move.
            int entry = entries[i];
            int length = _lengths[entry];
            if (length > 0)
            {
                _bytes.AsSpan(_offsets[entry], length).CopyTo(_bytes.AsSpan(used));
            }

            _offsets[i] = used;
            _lengths[i] = length;
            _hashes[i] = _hashes[entry];
            used += Math.Max(length, 0);
        }

        Count = entries.Length;
        _used = used;
        Array.Clear(_slots);
        Rehash(_slots);
    }

    private void Append(ReadOnlySpan<byte> key, ulong hash)
    {
        int index = Count;
        if (index == _offsets.Length)
        {
            int grown = index * 2;
            Array.Resize(ref _offsets, grown);
            Array.Resize(ref _lengths, grown);
            Array.Resize(ref _hashes, grown);
        }

        if (_used + key.Length > _bytes.Length)
        {
            Array.Resize(ref _bytes, Scratch.Capacity(_used + key.Length, _bytes.Length));
        }

        key.CopyTo(_bytes.AsSpan(_used));
        _offsets[index] = _used;
        _lengths[index] = key.Length;
        _hashes[index] = hash;
        _used += key.Length;
        Count = index + 1;
    }

    /// <summary>Hashes every key again under a seed of the table's own.</summary>
    private void Reseed()
    {
        _seed = Random.Shared.NextInt64(1, long.MaxValue);
        for (int index = 0; index < Count; index++)
        {
            if (_lengths[index] >= 0)
            {
                _hashes[index] = XxHash3.HashToUInt64(KeyOf(index), _seed);
            }
        }

        Rehash(new Slot[_slots.Length]);
    }

    /// <summary>Places every key in <paramref name="slots"/>, empty, by the hash it keeps, and makes them the table's.</summary>
    private void Rehash(Slot[] slots)
    {
        int mask = slots.Length - 1;
        for (int index = 0; index < Count; index++)
        {
            int length = _lengths[index];
            if (length < 0)
            {
                continue;
            }

            ulong hash = _hashes[index];
            int at = (int)hash & mask;
            while (slots[at].Entry != 0)
            {
                at = (at + 1) & mask;
            }

            slots[at] = new Slot { Half = (uint)(hash >> 32), Entry = index + 1, Offset = _offsets[index], Length = length };
        }

        _slots = slots;
    }

    /// <summary>What a probe compares: the high half of a key's hash, its number plus one (zero, an empty slot), where its bytes lie and how many.</summary>
    private struct Slot
    {
        public uint Half;
        public int Entry;
        public int Offset;
        public int Length;
    }
}
