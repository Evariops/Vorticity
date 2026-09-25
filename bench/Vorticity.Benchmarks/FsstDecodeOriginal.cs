using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Benchmarks;

/// <summary>
/// The eight-codes-a-word FSST decode, kept here unchanged as the baseline every change to
/// <c>FsstDecodeTable.Decode</c> is measured against in the same process: the padded tables built
/// for the node, the ASCII proof over the symbols, and the walk over the whole code stream.
/// </summary>
/// <remarks>
/// Self-contained on purpose: nothing here calls into the library, so editing the library's kernel
/// can never move this arm.
/// </remarks>
internal static class FsstDecodeOriginal
{
    private const byte EscapeCode = 255;
    private const int SymbolSize = 8;
    private const ulong Ones = 0x0101010101010101UL;
    private const ulong Highs = 0x8080808080808080UL;
    private const int Block = 8;
    private const int BlockSlack = Block * SymbolSize;

    /// <summary>Decodes <paramref name="codes"/> with the table, as one node's whole stream.</summary>
    /// <returns>The bytes written; the ASCII proof goes to <paramref name="ascii"/>.</returns>
    internal static int Decode(
        ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> lengths, ReadOnlySpan<byte> codes, Span<byte> destination,
        out bool ascii)
    {
        Span<byte> symbolScratch = stackalloc byte[256 * SymbolSize];
        Span<byte> widthScratch = stackalloc byte[256];
        symbolScratch.Clear();
        widthScratch.Clear();
        symbols.CopyTo(symbolScratch);
        lengths.CopyTo(widthScratch);
        bool symbolsAreAscii = EveryEmittedByteIsAscii(symbolScratch, widthScratch, lengths.Length);

        uint escapeBits = 0;
        int written = Walk(symbolScratch, widthScratch, codes, destination, ref escapeBits);
        ascii = symbolsAreAscii && (escapeBits & 0x80) == 0;
        return written;
    }

    private static bool EveryEmittedByteIsAscii(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> widths, int count)
    {
        ulong high = 0;
        for (int code = 0; code < count; code++)
        {
            int width = widths[code];
            ReadOnlySpan<byte> symbol = symbols.Slice(code * SymbolSize, SymbolSize);
            for (int i = 0; i < width; i++)
            {
                high |= symbol[i];
            }
        }

        return (high & 0x80) == 0;
    }

    private static int Walk(
        ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> widthTable, ReadOnlySpan<byte> codes, Span<byte> destination,
        ref uint escapeBits)
    {
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte table = ref MemoryMarshal.GetReference(symbols);
        ref byte widths = ref MemoryMarshal.GetReference(widthTable);
        ref byte input = ref MemoryMarshal.GetReference(codes);

        int written = 0;
        int i = 0;
        uint bad = 0;
        int wideLimit = destination.Length - SymbolSize;
        int blockLimit = destination.Length - BlockSlack;
        int blockEnd = codes.Length - Block;

        while (true)
        {
            while (i <= blockEnd && written <= blockLimit)
            {
                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, (uint)i));
                ulong inverted = ~word;
                if (((inverted - Ones) & ~inverted & Highs) != 0)
                {
                    break;
                }

                written = Step(ref output, ref table, ref widths, (byte)word, written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 8), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 16), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 24), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 32), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 40), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 48), written, ref bad);
                written = Step(ref output, ref table, ref widths, (byte)(word >> 56), written, ref bad);
                i += Block;
            }

            if (i >= codes.Length)
            {
                break;
            }

            byte code = codes[i];
            if (code == EscapeCode)
            {
                if (i + 1 >= codes.Length)
                {
                    throw new InvalidOperationException("truncated compressed string, escape code at end of input");
                }

                if (written >= destination.Length)
                {
                    throw new InvalidOperationException("the code stream decodes past the destination");
                }

                byte raw = codes[++i];
                escapeBits |= raw;
                destination[written++] = raw;
                i++;
                continue;
            }

            int width = Unsafe.Add(ref widths, (uint)code);
            bad |= (uint)(width - 1);
            if (written <= wideLimit)
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref output, (uint)written),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref table, (uint)(code * SymbolSize))));
                written += width;
                i++;
                continue;
            }

            if (written + width > destination.Length)
            {
                throw new InvalidOperationException("the code stream decodes past the destination");
            }

            symbols.Slice(code * SymbolSize, width).CopyTo(destination.Slice(written, width));
            written += width;
            i++;
        }

        if (bad > SymbolSize - 1)
        {
            throw new InvalidOperationException("a code names a symbol the table does not hold");
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step(ref byte output, ref byte table, ref byte widths, byte code, int written, ref uint bad)
    {
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref output, (uint)written),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref table, (uint)(code * SymbolSize))));
        uint width = Unsafe.Add(ref widths, (uint)code);
        bad |= width - 1;
        return written + (int)width;
    }
}
