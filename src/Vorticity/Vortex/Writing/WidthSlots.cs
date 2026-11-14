using System;
using System.Runtime.InteropServices;
using Vorticity.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// A column's pairs of width histograms in one block of the pool, <see cref="BitPackWidths.Length"/>
/// counts a slot: a block takes a slot while it is open and frees it once its chunk is written, and a
/// freed slot is the next one taken, so the block grows to the most blocks in transit at once rather
/// than to the column's length.
/// </summary>
/// <remarks>
/// <para>
/// The block is held by its bytes alone, so that the column writer made for every column of every
/// write is no larger for it. Its first two counts are the header: the first free slot, then the
/// slots handed out. A free slot holds the next free one in its first count.
/// </para>
/// <para>
/// A mutable struct, held in one field of its column and only ever used through it: a copy would
/// share the block and lose the free list.
/// </para>
/// </remarks>
internal struct WidthSlots
{
    /// <summary>No slot: a block without histograms, and the end of the free list.</summary>
    internal const int None = -1;

    private const int Header = 2;

    /// <summary>The slots the block starts with.</summary>
    private const int FirstSlots = 4;

    private byte[]? _bytes;

    /// <summary>The counts of <paramref name="slot"/>; empty for <see cref="None"/>.</summary>
    internal readonly Span<int> this[int slot] =>
        slot == None ? default : All.Slice(Header + (slot * BitPackWidths.Length), BitPackWidths.Length);

    private readonly Span<int> All => MemoryMarshal.Cast<byte, int>(_bytes.AsSpan());

    /// <summary>A slot, its counts zeroed.</summary>
    internal int Take()
    {
        if (_bytes is null)
        {
            _bytes = Blocks.Rent<int>(Header + (FirstSlots * BitPackWidths.Length)).Bytes;
            All[0] = None;
            All[1] = 0;
        }

        Span<int> all = All;
        int slot = all[0];
        if (slot != None)
        {
            all[0] = this[slot][0];
        }
        else
        {
            slot = all[1];
            int needed = Header + ((slot + 1) * BitPackWidths.Length);
            if (needed > all.Length)
            {
                Block<int> grown = Blocks.Rent<int>(Header + (2 * slot * BitPackWidths.Length));
                all[..(Header + (slot * BitPackWidths.Length))].CopyTo(grown);
                Blocks.ReturnBytes(_bytes);
                _bytes = grown.Bytes;
                all = All;
            }

            all[1] = slot + 1;
        }

        this[slot].Clear();
        return slot;
    }

    /// <summary>Frees <paramref name="slot"/> for the next block to take; nothing for <see cref="None"/>.</summary>
    internal readonly void Free(int slot)
    {
        if (slot == None)
        {
            return;
        }

        Span<int> all = All;
        this[slot][0] = all[0];
        all[0] = slot;
    }

    /// <summary>Gives the block back to the pool, every slot with it.</summary>
    internal void Release()
    {
        Blocks.ReturnBytes(_bytes);
        _bytes = null;
    }
}
