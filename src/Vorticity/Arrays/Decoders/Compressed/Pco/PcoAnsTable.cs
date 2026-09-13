// pco's tANS table - pco-1.0.3/src/ans/{spec,decoding}.rs.
//
// TWO STEPS THAT MUST BOTH BE EXACT, because neither is self-checking: the table is built from the
// bin weights alone and any disagreement with the encoder silently decodes to different symbols.
//
// 1. SPREADING. Each symbol is scattered across the table by stepping with a stride of about three
//    fifths of the table size, forced odd so it is coprime with the power-of-two size and therefore
//    visits every slot. Upstream calls this out as "needs to remain backward compatible", which is
//    another way of saying a reimplementation has no freedom here at all.
// 2. THE NODE TABLE. For each table slot, how many bits the next state consumes and where that
//    state starts. `bits_to_read` is the difference in leading zeros between the symbol's running
//    occurrence count and the table size - the number of bits needed to distinguish the states that
//    symbol owns.
//
// Decoding is 4-WAY INTERLEAVED: four independent states advanced round-robin, so a value's state
// comes from four back, not one.
using System;
using System.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>One table slot: where the next state starts, and what it costs to get there.</summary>
/// <param name="NextStateIndexBase">The next state index before the read bits are added.</param>
/// <param name="OffsetBits">Offset bits the bin at this slot stores per value.</param>
/// <param name="BitsToRead">Bits the next state consumes.</param>
internal readonly record struct PcoAnsNode(int NextStateIndexBase, int OffsetBits, int BitsToRead);

/// <summary>pco's tANS decoding table, built from a latent variable's bin weights.</summary>
internal sealed class PcoAnsTable
{
    private PcoAnsTable(int sizeLog, uint[] stateSymbols, PcoAnsNode[] nodes, ulong[] stateLowers)
    {
        SizeLog = sizeLog;
        StateSymbols = stateSymbols;
        Nodes = nodes;
        StateLowers = stateLowers;
    }

    /// <summary>Log2 of the table size.</summary>
    internal int SizeLog { get; }

    /// <summary>The symbol owning each table slot, in slot order.</summary>
    internal uint[] StateSymbols { get; }

    /// <summary>One node per table slot.</summary>
    internal PcoAnsNode[] Nodes { get; }

    /// <summary>The bin lower bound at each table slot, hoisted out of the bin list.</summary>
    internal ulong[] StateLowers { get; }

    /// <summary>Table slots.</summary>
    internal int Size => 1 << SizeLog;

    /// <summary>Spreads the symbols across the table, upstream's order exactly.</summary>
    /// <param name="sizeLog">Log2 of the table size.</param>
    /// <param name="weights">Each symbol's weight; they must sum to the table size.</param>
    /// <returns>The symbol owning each slot.</returns>
    /// <exception cref="VortexFormatException">The weights do not sum to the table size.</exception>
    internal static uint[] Spread(int sizeLog, ReadOnlySpan<uint> weights)
    {
        int tableSize = 1 << sizeLog;
        long total = 0;
        foreach (uint weight in weights)
        {
            total += weight;
        }

        if (total != tableSize)
        {
            CompressedThrow.Format(
                $"A pco ANS table of size log {sizeLog} needs weights summing to {tableSize}; they sum to {total}.");
        }

        uint[] symbols = new uint[tableSize];
        int stride = ChooseStride(tableSize);
        int mask = tableSize - 1;
        int step = 0;

        for (int symbol = 0; symbol < weights.Length; symbol++)
        {
            for (uint i = 0; i < weights[symbol]; i++)
            {
                symbols[(stride * step) & mask] = (uint)symbol;
                step++;
            }
        }

        return symbols;
    }

    /// <summary>Builds the decoding table for one latent variable.</summary>
    /// <param name="sizeLog">The ANS size log from the chunk metadata.</param>
    /// <param name="bins">The latent variable's bins.</param>
    /// <returns>The table.</returns>
    internal static PcoAnsTable Build(int sizeLog, ReadOnlySpan<PcoBin> bins)
    {
        // A latent with no bins still gets a one-slot table: upstream's degenerate case, where the
        // single slot reads no bits and carries no offset.
        uint[] weights = new uint[Math.Max(1, bins.Length)];
        if (bins.Length == 0)
        {
            weights[0] = (uint)(1 << sizeLog);
        }
        else
        {
            for (int i = 0; i < bins.Length; i++)
            {
                weights[i] = bins[i].Weight;
            }
        }

        uint[] stateSymbols = Spread(sizeLog, weights);
        int tableSize = 1 << sizeLog;

        PcoAnsNode[] nodes = new PcoAnsNode[tableSize];
        ulong[] lowers = new ulong[tableSize];
        uint[] running = (uint[])weights.Clone();

        for (int slot = 0; slot < tableSize; slot++)
        {
            uint symbol = stateSymbols[slot];
            uint nextStateBase = running[symbol];

            // The bits that distinguish the states this symbol owns: how much `nextStateBase` must
            // be shifted to land inside [tableSize, 2 * tableSize).
            int bitsToRead = BitOperations.LeadingZeroCount(nextStateBase)
                - BitOperations.LeadingZeroCount((uint)tableSize);
            long shifted = (long)nextStateBase << bitsToRead;

            int offsetBits = symbol < (uint)bins.Length ? bins[(int)symbol].OffsetBits : 0;
            nodes[slot] = new PcoAnsNode((int)(shifted - tableSize), offsetBits, bitsToRead);
            lowers[slot] = symbol < (uint)bins.Length ? bins[(int)symbol].Lower : 0;
            running[symbol]++;
        }

        return new PcoAnsTable(sizeLog, stateSymbols, nodes, lowers);
    }

    /// <summary>About three fifths of the table size, forced odd so it is coprime with it.</summary>
    private static int ChooseStride(int tableSize)
    {
        int stride = (3 * tableSize) / 5;
        if ((stride & 1) == 0)
        {
            stride++;
        }

        return stride;
    }
}
