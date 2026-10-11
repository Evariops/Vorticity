using System;
using System.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// A pooled open-addressing set of 64-bit hashes. Zero marks an empty slot, so a zero hash is
/// tracked by a flag instead of being stored.
/// </summary>
/// <remarks>
/// <para>
/// The hashes are the format's, unseeded, and a value can be worked back from the one it hashes to:
/// a column could be written whose hashes share their low bits. A hash is stored as it is, but its
/// slot is read from it through the process's seed, so no column can know which ones share a slot.
/// </para>
/// <para>
/// That slot is a mix to wait for, and a probe whose branch was guessed wrong would wait for the
/// next one's all over again. <see cref="AddRange"/> and <see cref="AddAll"/> take the mixes of a
/// batch before they probe any of it, so that the probes wait on nothing but their slots.
/// </para>
/// </remarks>
internal sealed class HashSet64 : IDisposable
{
    private const int InitialSlots = 1 << 10;

    /// <summary>Hashes whose slots are mixed ahead of their probes, on the stack.</summary>
    private const int Batch = 64;

    private ulong[] _slots;
    private int _mask;
    private int _count;
    private bool _hasZero;

    internal HashSet64()
    {
        _slots = ArrayPool<ulong>.Shared.Rent(InitialSlots);
        _mask = InitialSlots - 1;
        _slots.AsSpan(0, InitialSlots).Clear();
    }

    internal int Count => _count + (_hasZero ? 1 : 0);

    internal void Add(ulong hash) => Insert(hash, KeyHash.Mix(hash));

    /// <summary>Adds every hash of <paramref name="hashes"/>, a batch's slots mixed before its probes.</summary>
    internal void AddRange(ReadOnlySpan<ulong> hashes)
    {
        Span<ulong> mixed = stackalloc ulong[Batch];
        for (int from = 0; from < hashes.Length; from += Batch)
        {
            ReadOnlySpan<ulong> batch = hashes.Slice(from, Math.Min(Batch, hashes.Length - from));
            for (int i = 0; i < batch.Length; i++)
            {
                mixed[i] = KeyHash.Mix(batch[i]);
            }

            for (int i = 0; i < batch.Length; i++)
            {
                Insert(batch[i], mixed[i]);
            }
        }
    }

    /// <param name="hash">The hash, stored as it is.</param>
    /// <param name="mixed">The hash's <see cref="KeyHash.Mix"/>, which picks its slot.</param>
    private void Insert(ulong hash, ulong mixed)
    {
        if (hash == 0)
        {
            _hasZero = true;
            return;
        }

        ulong[] slots = _slots;
        int mask = _mask;
        int slot = (int)mixed & mask;
        while (true)
        {
            ulong held = slots[slot];
            if (held == hash)
            {
                return;
            }

            if (held == 0)
            {
                slots[slot] = hash;
                if (++_count * 2 > mask + 1)
                {
                    Grow();
                }

                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Adds every hash <paramref name="other"/> holds.</summary>
    /// <remarks>
    /// A cleared set keeps the slots it grew to, most of them empty, so the held hashes are packed
    /// a batch at a time, without a branch, and only those are mixed.
    /// </remarks>
    internal void AddAll(HashSet64 other)
    {
        ReadOnlySpan<ulong> held = other.Slots;
        Span<ulong> batch = stackalloc ulong[Batch];
        int count = 0;
        foreach (ulong hash in held)
        {
            batch[count] = hash;
            count += (int)((hash | (0 - hash)) >> 63);
            if (count == Batch)
            {
                AddRange(batch);
                count = 0;
            }
        }

        AddRange(batch[..count]);
        if (other._hasZero)
        {
            _hasZero = true;
        }
    }

    internal void InsertInto(Span<uint> filter)
    {
        foreach (ulong hash in Slots)
        {
            if (hash != 0)
            {
                Indexes.SplitBlockBloom.Insert(filter, hash);
            }
        }

        if (_hasZero)
        {
            Indexes.SplitBlockBloom.Insert(filter, 0);
        }
    }

    /// <summary>Empties the set, keeping its pooled table.</summary>
    internal void Clear()
    {
        Slots.Clear();
        _count = 0;
        _hasZero = false;
    }

    private Span<ulong> Slots => _slots.AsSpan(0, _mask + 1);

    private void Grow()
    {
        ulong[] old = _slots;
        int oldLength = _mask + 1;
        int length = oldLength * 2;
        ulong[] grown = ArrayPool<ulong>.Shared.Rent(length);
        grown.AsSpan(0, length).Clear();
        int mask = length - 1;
        foreach (ulong hash in old.AsSpan(0, oldLength))
        {
            if (hash == 0)
            {
                continue;
            }

            int slot = (int)KeyHash.Mix(hash) & mask;
            while (grown[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            grown[slot] = hash;
        }

        ArrayPool<ulong>.Shared.Return(old);
        _slots = grown;
        _mask = mask;
    }

    public void Dispose()
    {
        if (_slots.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_slots);
            _slots = [];
            _mask = -1;
        }
    }
}
