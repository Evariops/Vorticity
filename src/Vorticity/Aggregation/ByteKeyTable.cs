using System;
using System.IO.Hashing;

namespace Vorticity.Aggregating;

/// <summary>
/// Byte strings numbered in the order they are first seen: an open-addressing table over XxHash3,
/// the keys packed in one buffer. It holds text keys, the row-encoded tuples of a composite key and
/// the (group, value) pairs of a distinct count, with no allocation per key.
/// </summary>
/// <remarks>
/// Keys can be built offline to share the low bits of their unseeded hash, and so one run of the
/// probe sequence: a key that would land <see cref="MaxProbes"/> slots past its own reseeds the hash
/// with a value of the table's own and rehashes every key, which such keys cannot have been built
/// against. Until then every key sits closer than that to its own slot, so no lookup probes further:
/// a growth places the keys again in the order they came, and none lands further than it was.
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
    private int[] _slots = new int[32];
    private long _seed;

    /// <summary>The number of distinct keys.</summary>
    internal int Count { get; private set; }

    /// <summary>Whether a key landed far enough from its own slot for the table to take a seed.</summary>
    internal bool Reseeded => _seed != 0;

    /// <summary>The number of <paramref name="key"/>, which is added when it is new.</summary>
    internal int GetOrAdd(ReadOnlySpan<byte> key, out bool added)
    {
        ulong hash = XxHash3.HashToUInt64(key, _seed);
        int mask = _slots.Length - 1;
        int slot = (int)hash & mask;
        while (true)
        {
            int entry = _slots[slot];
            if (entry == 0)
            {
                break;
            }

            int index = entry - 1;
            if (_hashes[index] == hash && _bytes.AsSpan(_offsets[index], _lengths[index]).SequenceEqual(key))
            {
                added = false;
                return index;
            }

            slot = (slot + 1) & mask;
        }

        if (((slot - (int)hash) & mask) >= MaxProbes && _seed == 0)
        {
            Reseed();
            return GetOrAdd(key, out added);
        }

        int number = Count;
        Append(key, hash);
        _slots[slot] = number + 1;
        added = true;
        if (Count * 2 > _slots.Length)
        {
            Rehash(_slots.Length * 2);
        }

        return number;
    }

    /// <summary>The bytes of key <paramref name="index"/>.</summary>
    internal ReadOnlySpan<byte> KeyOf(int index) => _bytes.AsSpan(_offsets[index], _lengths[index]);

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
            Array.Resize(ref _bytes, Math.Max(_used + key.Length, _bytes.Length * 2));
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
            _hashes[index] = XxHash3.HashToUInt64(KeyOf(index), _seed);
        }

        Rehash(_slots.Length);
    }

    private void Rehash(int size)
    {
        int[] slots = new int[size];
        int mask = slots.Length - 1;
        for (int index = 0; index < Count; index++)
        {
            int slot = (int)_hashes[index] & mask;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            slots[slot] = index + 1;
        }

        _slots = slots;
    }
}
