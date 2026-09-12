// The FSST decode kernel - fsst-rs-0.6.0/src/lib.rs `Decompressor::decompress_into`.
//
// FSST compresses text by replacing recurring byte sequences with one-byte codes drawn from a table
// of at most 255 symbols, each 1 to 8 bytes. Code 255 is the ESCAPE: the byte after it is emitted
// literally, which is how a byte that no symbol covers still round-trips. Decoding is therefore a
// flat walk over the code stream with no state at all beyond the output cursor:
//
//     code == 255  -> copy the next input byte
//     otherwise    -> copy the first lengths[code] bytes of symbols[code]
//
// The reference's kernel looks nothing like that, because it loads eight codes at a time, tests
// them for escapes with a SWAR mask, and writes every symbol as one unaligned 8-byte store that it
// then advances by the symbol's real length. That is a pure optimization of the loop above - the
// wide store writes garbage past the symbol and the next store overwrites it - and reproducing
// its shape rather than its semantics would buy nothing until the rest of the library is
// vectorized (docs/01-scope.md §3, SIMD is its own piece of work).
//
// What DOES transfer is the reason the reference's buffer is oversized: it needs 8 writable bytes
// to store a symbol whose real length may be 1. This implementation writes exactly the symbol's
// length, so it needs no slack -- stated here because the absence of FSST_DECODE_SLACK is otherwise
// an unexplained divergence.
//
// SO THE WIDE STORE IS NOW HERE TOO, and the story of getting there is the useful part. This file
// used to argue that reproducing the reference's shape "would buy nothing until the rest of the
// library is vectorized". Measuring per-encoding ratios against Rust (docs/05-benchmarks.md §1b)
// made FSST our slowest kernel, so the shape was tried - and a whole-file benchmark said it was 8%
// SLOWER, which I nearly wrote down as a finding. It was drift: three runs of effectively identical
// code came back 135, 146 and 202 us, and a 35 us open cost sat in front of the kernel in every one
// of them. Measured properly, both shapes in one process over one code stream
// (FsstKernelBenchmarks), the wide store is 7.9x FASTER - 307 us against 39 us on 64 KiB of codes.
//
// Two lessons, both already written down in docs/05 §5 and neither of which I applied first time: a
// microbenchmark of the thing being changed beats an end-to-end one diluted by a fixed cost, and
// two candidates have to be measured against ONE clock or thermal drift decides the winner.
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>A validated FSST symbol table, and the decode over it.</summary>
internal readonly ref struct FsstSymbolTable
{
    /// <summary>The escape code: the following input byte is a literal.</summary>
    internal const byte EscapeCode = 255;

    /// <summary>Codes 0..254 name symbols; 255 is the escape, so the table holds at most 255.</summary>
    internal const int MaxSymbols = 255;

    /// <summary>Bytes per symbol slot on the wire: a symbol is a <c>u64</c>.</summary>
    internal const int SymbolSize = 8;

    private readonly ReadOnlySpan<byte> _symbols;
    private readonly ReadOnlySpan<byte> _lengths;

    private FsstSymbolTable(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> lengths)
    {
        _symbols = symbols;
        _lengths = lengths;
    }

    /// <summary>How many symbols the table holds.</summary>
    internal int Count => _lengths.Length;

    /// <summary>
    /// Validates the two symbol-table buffers against each other and against the format's limits.
    /// </summary>
    /// <param name="symbols">Buffer 0: one <c>u64</c> per symbol.</param>
    /// <param name="lengths">Buffer 1: one <c>u8</c> per symbol, in <c>[1, 8]</c>.</param>
    /// <param name="encodingId">The encoding asking, for the messages.</param>
    /// <returns>The validated table.</returns>
    /// <exception cref="VortexFormatException">
    /// The buffers disagree, the table is larger than the code space, or a length is not a legal
    /// symbol width. Every one of these would otherwise be an out-of-bounds read or a silently
    /// wrong value during the decode.
    /// </exception>
    internal static FsstSymbolTable Create(
        ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> lengths, string encodingId)
    {
        if (symbols.Length % SymbolSize != 0)
        {
            CompressedThrow.Format(
                $"{encodingId}'s symbol buffer is {symbols.Length} bytes, not a multiple of {SymbolSize}.");
        }

        int count = symbols.Length / SymbolSize;
        if (count != lengths.Length)
        {
            CompressedThrow.Format(
                $"{encodingId} carries {count} symbols but {lengths.Length} symbol lengths.");
        }

        if (count > MaxSymbols)
        {
            CompressedThrow.Format(
                $"{encodingId} carries {count} symbols; code {EscapeCode} is the escape, so at " +
                $"most {MaxSymbols} are addressable.");
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            byte width = lengths[i];
            if (width is 0 or > SymbolSize)
            {
                CompressedThrow.Format(
                    $"{encodingId} symbol {i} declares a length of {width}; symbols are 1 to " +
                    $"{SymbolSize} bytes.");
            }
        }

        return new FsstSymbolTable(symbols, lengths);
    }

    /// <summary>
    /// Decodes the whole code stream into <paramref name="destination"/> and returns the bytes
    /// written.
    /// </summary>
    /// <param name="codes">The compressed stream.</param>
    /// <param name="destination">The output; must be large enough for the decoded bytes.</param>
    /// <param name="encodingId">The encoding asking, for the messages.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="VortexFormatException">
    /// A code names a symbol the table does not hold, an escape is the last byte of the stream, or
    /// the output is too small for what the stream decodes to. The reference asserts all three.
    /// </exception>
    internal int Decode(ReadOnlySpan<byte> codes, Span<byte> destination, string encodingId)
    {
        ReadOnlySpan<byte> symbols = _symbols;
        ReadOnlySpan<byte> lengths = _lengths;
        int count = lengths.Length;
        int written = 0;

        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte table = ref MemoryMarshal.GetReference(symbols);

        // Past this point a symbol no longer has eight writable bytes behind it, so the wide store
        // would run off the end and the exact copy takes over. Negative for a destination under
        // eight bytes, which is right: the wide path is then never taken at all.
        int wideLimit = destination.Length - SymbolSize;

        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];
            if (code == EscapeCode)
            {
                if (i + 1 >= codes.Length)
                {
                    CompressedThrow.Format(
                        $"{encodingId}: truncated compressed string, escape code at end of input.");
                }

                if (written >= destination.Length)
                {
                    return ThrowOverrun(encodingId, destination.Length);
                }

                destination[written++] = codes[++i];
                continue;
            }

            if (code >= count)
            {
                CompressedThrow.Format(
                    $"{encodingId}: code {code} names a symbol the {count}-entry table " +
                    "does not hold.");
            }

            int width = lengths[code];
            if (written <= wideLimit)
            {
                // ONE 8-BYTE STORE PER SYMBOL, then advance by the symbol's REAL length. The bytes
                // written past the symbol are garbage that the next store overwrites, which is both
                // what makes this legal and what makes it fast: a 1..8-byte Span.CopyTo carries a
                // length the CPU cannot see through, and this is a single unaligned move.
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref output, (uint)written),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref table, (uint)(code * SymbolSize))));
                written += width;
                continue;
            }

            if (written + width > destination.Length)
            {
                return ThrowOverrun(encodingId, destination.Length);
            }

            // The tail, where the wide store would run past the end of the destination. The symbol
            // is a little-endian u64 whose low `width` bytes are the text, which is exactly its
            // first `width` bytes in memory.
            symbols.Slice(code * SymbolSize, width).CopyTo(destination.Slice(written, width));
            written += width;
        }

        return written;
    }

    /// <summary>The <c>u64</c> of symbol <paramref name="code"/>, for diagnostics and tests.</summary>
    /// <param name="code">A code below <see cref="Count"/>.</param>
    internal ulong SymbolBits(int code) =>
        BinaryPrimitives.ReadUInt64LittleEndian(_symbols.Slice(code * SymbolSize, SymbolSize));

    private static int ThrowOverrun(string encodingId, int capacity) =>
        CompressedThrow.Format<int>(
            $"{encodingId}: the code stream decodes to more than the {capacity} bytes its " +
            "uncompressed lengths account for.");
}
