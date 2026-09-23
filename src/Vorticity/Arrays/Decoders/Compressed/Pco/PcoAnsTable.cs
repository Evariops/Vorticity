using System;
using System.Numerics;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>One table slot: where the next state starts, and what it costs to get there.</summary>
/// <param name="NextStateIndexBase">The next state index before the read bits are added.</param>
/// <param name="OffsetBits">Offset bits the bin at this slot stores per value.</param>
/// <param name="BitsToRead">Bits the next state consumes.</param>
internal readonly record struct PcoAnsNode(int NextStateIndexBase, int OffsetBits, int BitsToRead);

/// <summary>
/// pco's tANS decoding table, built from a latent variable's bin weights. Both the spreading order
/// and the node table have to match the encoder exactly and neither is self-checking, so any
/// departure from the published order decodes silently to different symbols rather than failing.
/// </summary>
/// <remarks>
/// A table is refilled chunk after chunk rather than built for each: it runs to 2^14 slots, a few
/// hundred kilobytes of arrays past the large-object threshold, which a scan of a column of many
/// chunks would otherwise allocate once a chunk. Its arrays grow to the largest table asked of it
/// and are read to its current size only.
/// </remarks>
internal sealed class PcoAnsTable
{
    private uint[] _stateSymbols = [];
    private PcoAnsNode[] _nodes = [];
    private ulong[] _stateLowers = [];

    /// <summary>Log2 of the table size.</summary>
    internal int SizeLog { get; private set; }

    /// <summary>The symbol owning each table slot, in slot order.</summary>
    internal ReadOnlySpan<uint> StateSymbols => _stateSymbols.AsSpan(0, Size);

    /// <summary>One node per table slot.</summary>
    internal ReadOnlySpan<PcoAnsNode> Nodes => _nodes.AsSpan(0, Size);

    /// <summary>The bin lower bound at each table slot, hoisted out of the bin list.</summary>
    internal ReadOnlySpan<ulong> StateLowers => _stateLowers.AsSpan(0, Size);

    /// <summary>Table slots.</summary>
    internal int Size => 1 << SizeLog;

    /// <summary>Spreads the symbols across the table, upstream's order exactly.</summary>
    /// <param name="sizeLog">Log2 of the table size.</param>
    /// <param name="weights">Each symbol's weight; they must sum to the table size.</param>
    /// <returns>The symbol owning each slot.</returns>
    /// <exception cref="VortexFormatException">The weights do not sum to the table size.</exception>
    internal static uint[] Spread(int sizeLog, ReadOnlySpan<uint> weights)
    {
        uint[] symbols = new uint[1 << sizeLog];
        SpreadInto(sizeLog, weights, symbols);
        return symbols;
    }

    /// <summary><see cref="Spread"/> into the first 2^<paramref name="sizeLog"/> slots of <paramref name="symbols"/>.</summary>
    private static void SpreadInto(int sizeLog, ReadOnlySpan<uint> weights, Span<uint> symbols)
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
    }

    /// <summary>Builds the decoding table for one latent variable.</summary>
    /// <param name="sizeLog">The ANS size log from the chunk metadata.</param>
    /// <param name="bins">The latent variable's bins.</param>
    /// <returns>The table.</returns>
    internal static PcoAnsTable Build(int sizeLog, ReadOnlySpan<PcoBin> bins) =>
        new PcoAnsTable().Refill(sizeLog, bins);

    /// <summary>Rebuilds this table for one latent variable, in the arrays it already has where they are large enough.</summary>
    /// <param name="sizeLog">The ANS size log from the chunk metadata.</param>
    /// <param name="bins">The latent variable's bins.</param>
    /// <returns>This table.</returns>
    internal PcoAnsTable Refill(int sizeLog, ReadOnlySpan<PcoBin> bins)
    {
        int tableSize = 1 << sizeLog;
        if (_nodes.Length < tableSize)
        {
            _stateSymbols = new uint[tableSize];
            _nodes = new PcoAnsNode[tableSize];
            _stateLowers = new ulong[tableSize];
        }

        // A latent with no bins still gets a one-slot table: upstream's degenerate case, where the
        // single slot reads no bits and carries no offset.
        int symbolCount = Math.Max(1, bins.Length);
        Span<uint> stackWeights = stackalloc uint[64];
        using Scratch<uint> weightScratch = new Scratch<uint>(symbolCount, stackWeights);
        Span<uint> stackRunning = stackalloc uint[64];
        using Scratch<uint> runningScratch = new Scratch<uint>(symbolCount, stackRunning);
        Span<uint> weights = weightScratch.Span[..symbolCount];
        Span<uint> running = runningScratch.Span[..symbolCount];
        if (bins.Length == 0)
        {
            weights[0] = (uint)tableSize;
        }
        else
        {
            for (int i = 0; i < bins.Length; i++)
            {
                weights[i] = bins[i].Weight;
            }
        }

        Span<uint> stateSymbols = _stateSymbols.AsSpan(0, tableSize);
        SpreadInto(sizeLog, weights, stateSymbols);
        weights.CopyTo(running);
        SizeLog = sizeLog;

        Span<PcoAnsNode> nodes = _nodes.AsSpan(0, tableSize);
        Span<ulong> lowers = _stateLowers.AsSpan(0, tableSize);
        for (int slot = 0; slot < tableSize; slot++)
        {
            uint symbol = stateSymbols[slot];
            uint nextStateBase = running[(int)symbol];

            // The bits that distinguish the states this symbol owns: how much `nextStateBase` must
            // be shifted to land inside [tableSize, 2 * tableSize).
            int bitsToRead = BitOperations.LeadingZeroCount(nextStateBase)
                - BitOperations.LeadingZeroCount((uint)tableSize);
            long shifted = (long)nextStateBase << bitsToRead;

            int offsetBits = symbol < (uint)bins.Length ? bins[(int)symbol].OffsetBits : 0;
            nodes[slot] = new PcoAnsNode((int)(shifted - tableSize), offsetBits, bitsToRead);
            lowers[slot] = symbol < (uint)bins.Length ? bins[(int)symbol].Lower : 0;
            running[(int)symbol]++;
        }

        return this;
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
