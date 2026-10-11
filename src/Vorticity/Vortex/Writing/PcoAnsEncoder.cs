using System;
using System.Numerics;

using Vorticity.Arrays.Decoders.Compressed.Pco;

namespace Vorticity.Writing;

/// <summary>
/// pco's tANS encoder, <c>ans::Encoder</c>: the states of a table of 2^size-log slots, spread over
/// the symbols by their weights exactly as the decoder spreads them, each symbol's states in slot
/// order; encoding a symbol from a state sheds the state's low bits until it falls in the
/// symbol's range, then moves to the state that range names.
/// </summary>
/// <remarks>
/// Refilled latent variable after latent variable, in arrays that grow to the largest table asked
/// of it, as the decoder's table is: a table runs to 2^14 slots.
/// </remarks>
internal sealed class PcoAnsEncoder
{
    private uint[] _symbols = [];
    private uint[] _nextStates = [];
    private int[] _starts = [];
    private uint[] _weights = [];
    private int[] _minimumBits = [];
    private uint[] _cutoffs = [];

    /// <summary>Log2 of the table size; zero for a single symbol, which encodes to nothing.</summary>
    internal int SizeLog { get; private set; }

    /// <summary>The state every lane starts from, and what a page's final states are stored relative to.</summary>
    internal uint DefaultState => 1u << SizeLog;

    /// <summary>Rebuilds the encoder for one latent variable's weights, which sum to 2^<paramref name="sizeLog"/>.</summary>
    internal void Refill(int sizeLog, ReadOnlySpan<uint> weights)
    {
        SizeLog = sizeLog;
        if (sizeLog == 0)
        {
            return;
        }

        int tableSize = 1 << sizeLog;
        int symbolCount = weights.Length;
        if (_symbols.Length < tableSize)
        {
            _symbols = new uint[tableSize];
            _nextStates = new uint[tableSize];
        }

        if (_starts.Length < symbolCount)
        {
            int capacity = Math.Max(symbolCount, 64);
            _starts = new int[capacity];
            _weights = new uint[capacity];
            _minimumBits = new int[capacity];
            _cutoffs = new uint[capacity];
        }

        Span<uint> symbols = _symbols.AsSpan(0, tableSize);
        PcoAnsTable.SpreadInto(sizeLog, weights, symbols);

        int start = 0;
        for (int s = 0; s < symbolCount; s++)
        {
            uint weight = weights[s];
            _starts[s] = start;
            _weights[s] = weight;

            // x_s ranges over [weight, 2 weight): the fewest bits shed take a state of the table's
            // top range under 2 weight, one more only for the states at or past the cutoff.
            int minimum = sizeLog - BitOperations.Log2((2 * weight) - 1);
            _minimumBits[s] = minimum;
            _cutoffs[s] = (2 * weight) << minimum;
            start += (int)weight;
        }

        // Each symbol's states in slot order, which is the order its range numbers them.
        Span<int> filled = _starts.AsSpan(0, symbolCount);
        Span<uint> nextStates = _nextStates.AsSpan(0, tableSize);
        for (int slot = 0; slot < tableSize; slot++)
        {
            int s = (int)symbols[slot];
            nextStates[filled[s]++] = (uint)(tableSize + slot);
        }

        // `filled` moved each start to its symbol's end; one weight back is its start again.
        for (int s = 0; s < symbolCount; s++)
        {
            _starts[s] -= (int)_weights[s];
        }
    }

    /// <summary>One symbol encoded from <paramref name="state"/>: the next state, and how many of the state's low bits were shed.</summary>
    internal uint Encode(uint state, int symbol, out int bits)
    {
        bits = state >= _cutoffs[symbol] ? _minimumBits[symbol] + 1 : _minimumBits[symbol];
        uint rangeIndex = (state >> bits) - _weights[symbol];
        return _nextStates[_starts[symbol] + (int)rangeIndex];
    }
}
