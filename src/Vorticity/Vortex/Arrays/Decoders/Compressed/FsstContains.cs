using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// A pattern found anywhere in a row, matched on the row's codes: the Knuth–Morris–Pratt automaton of the pattern's bytes,
/// carried over to the table's symbols, so that a row is walked a code a step and none is decoded.
/// </summary>
/// <remarks>
/// <para>
/// A state is how many of the pattern's bytes end the bytes seen so far, the pattern's length once all of them did, which
/// no byte leaves. A byte's step is the automaton's; a symbol's is its bytes' steps one after the other, worked out once
/// for every state and symbol of the chunk's table, and an escape's the step of the literal byte after it. A code no
/// symbol has decodes to nothing, as the decoder reads it, and leaves the state as it is.
/// </para>
/// <para>
/// The tables are a byte a state and code, two of 256 codes a state: a pattern of 20 bytes takes 10.5 KiB, which stays
/// in the first-level cache while the rows are walked. A pattern of more than 254 bytes has no state in a byte and is not
/// matched here.
/// </para>
/// </remarks>
internal sealed class FsstContains : IDisposable
{
    /// <summary>The longest pattern matched on codes: its states fit a byte.</summary>
    internal const int MostBytes = 254;

    // Per state, the next state after each byte, and after each code; rented, 256 a state.
    private readonly byte[] _bytes;
    private readonly byte[] _codes;
    private readonly int _length;

    private FsstContains(byte[] bytes, byte[] codes, int length)
    {
        _bytes = bytes;
        _codes = codes;
        _length = length;
    }

    /// <summary>The automaton of <paramref name="pattern"/> over <paramref name="table"/>'s symbols; null for a pattern past <see cref="MostBytes"/>.</summary>
    internal static FsstContains? Create(FsstSymbolTable table, ReadOnlySpan<byte> pattern)
    {
        int length = pattern.Length;
        if (length > MostBytes)
        {
            return null;
        }

        int states = length + 1;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(states * 256);
        byte[] codes = ArrayPool<byte>.Shared.Rent(states * 256);

        // The automaton over bytes: from a state, the pattern's next byte moves on, and any other falls back as far as the
        // pattern's longest border allows, the state it reaches from there.
        Span<int> border = stackalloc int[MostBytes];
        for (int i = 1, k = 0; i < length; i++)
        {
            while (k > 0 && pattern[i] != pattern[k])
            {
                k = border[k - 1];
            }

            k += pattern[i] == pattern[k] ? 1 : 0;
            border[i] = k;
        }

        for (int state = 0; state < states; state++)
        {
            Span<byte> row = bytes.AsSpan(state * 256, 256);
            if (state == length)
            {
                row.Fill((byte)length);
                continue;
            }

            for (int b = 0; b < 256; b++)
            {
                row[b] = pattern[state] == b
                    ? (byte)(state + 1)
                    : state == 0 ? (byte)0 : bytes[(border[state - 1] * 256) + b];
            }
        }

        // A symbol's step: its bytes' steps, the last state of the pattern holding once reached.
        for (int state = 0; state < states; state++)
        {
            Span<byte> row = codes.AsSpan(state * 256, 256);
            for (int code = 0; code < 255; code++)
            {
                int at = state;
                if (code < table.Count)
                {
                    ulong symbol = table.SymbolBits(code);
                    for (int b = 0; b < table.Width(code); b++)
                    {
                        at = bytes[(at * 256) + (byte)(symbol >> (8 * b))];
                    }
                }

                row[code] = (byte)at;
            }

            row[FsstSymbolTable.EscapeCode] = (byte)state;
        }

        return new FsstContains(bytes, codes, length);
    }

    /// <summary>Whether the bytes <paramref name="codes"/> stand for hold the pattern.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Match(ReadOnlySpan<byte> codes)
    {
        ref byte bytes = ref _bytes[0];
        ref byte steps = ref _codes[0];
        int state = 0;
        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];
            if (code != FsstSymbolTable.EscapeCode)
            {
                state = Unsafe.Add(ref steps, (state << 8) | code);
                continue;
            }

            // An escape at the stream's end stands for no byte, as the decoder reads it.
            if (++i < codes.Length)
            {
                state = Unsafe.Add(ref bytes, (state << 8) | codes[i]);
            }
        }

        return state == _length;
    }

    /// <summary>Gives the tables back.</summary>
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_bytes);
        ArrayPool<byte>.Shared.Return(_codes);
    }
}
