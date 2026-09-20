using System;
using System.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// A pooled open-addressing set of 64-bit hashes. Zero marks an empty slot, so a zero hash is
/// tracked by a flag instead of being stored.
/// </summary>
internal sealed class HashSet64 : IDisposable
{
    private const int InitialSlots = 1 << 10;

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

    internal void Add(ulong hash)
    {
        if (hash == 0)
        {
            _hasZero = true;
            return;
        }

        ulong[] slots = _slots;
        int mask = _mask;
        int slot = (int)hash & mask;
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

    internal void AddAll(HashSet64 other)
    {
        foreach (ulong hash in other.Slots)
        {
            if (hash != 0)
            {
                Add(hash);
            }
        }

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

            int slot = (int)hash & mask;
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
