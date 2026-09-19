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
//
// AND THEN THE OTHER HALF OF THE REFERENCE'S SHAPE, which the first pass took the store from and
// left the loop behind. `dotnet-trace` over a 1M-row `vortex.fsst` scan puts 76.7% of the whole
// scan inside this one method -- it is not a hot path, it IS the path -- and per code it was doing
// a bounds-checked load of the code, a compare against the escape, a compare against the table
// size, a bounds-checked load of the width and a compare against the destination's slack, to
// perform one store.
//
// `fsst-rs` loads EIGHT codes as one u64 and tests all eight for the escape with a SWAR has-zero-
// byte mask. When none of them is an escape -- a trained table produces one escape in dozens of
// codes -- it runs eight unconditional stores with no branch between them. That needs two things
// this file did not have:
//
//   * THE TABLES PADDED TO THE FULL CODE SPACE. Dropping the `code >= count` branch means an
//     out-of-table code would index past the symbol buffer, which is the out-of-bounds read this
//     library promises never to perform. So the 256 widths and the 2 KiB of symbols are built once
//     per node into caller scratch, zero-filled, and an absent code reads a zero width. The check
//     does not disappear, it becomes BRANCHLESS: `bad |= width - 1` is 0..7 for a real symbol and
//     0xFFFFFFFF for an absent one, tested once at the end of the stream.
//   * THE SLACK CHECKED PER BLOCK RATHER THAN PER CODE. Eight symbols advance the cursor by at
//     most 64 bytes, so one compare before the block covers all eight stores.
//
// Preparing the tables costs a 2.3 KiB clear and copy per NODE, against 65 536 branches saved per
// 64 KiB of codes; and it is why `Prepare` takes the scratch from its caller -- the selective path
// decodes row by row and must not rebuild the table per row.
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

    /// <summary>Bytes of scratch <see cref="Prepare"/> needs for the padded symbol table.</summary>
    internal const int SymbolScratchBytes = 256 * SymbolSize;

    /// <summary>Bytes of scratch <see cref="Prepare"/> needs for the padded width table.</summary>
    internal const int WidthScratchBytes = 256;

    /// <summary>
    /// Builds the padded decode tables into caller-owned scratch, ONCE per node.
    /// </summary>
    /// <param name="symbolScratch">At least <see cref="SymbolScratchBytes"/> writable bytes.</param>
    /// <param name="widthScratch">At least <see cref="WidthScratchBytes"/> writable bytes.</param>
    /// <remarks>
    /// The scratch is the caller's because the selective path calls <see cref="FsstDecodeTable.Decode"/>
    /// once per wanted row, and rebuilding a 2.3 KiB table per row would cost more than the branches
    /// it removes.
    /// </remarks>
    internal FsstDecodeTable Prepare(Span<byte> symbolScratch, Span<byte> widthScratch)
    {
        symbolScratch = symbolScratch[..SymbolScratchBytes];
        widthScratch = widthScratch[..WidthScratchBytes];

        // Zero-filled past the real table: an absent code then reads a zero width, which is what
        // makes the range check branchless rather than absent.
        symbolScratch.Clear();
        widthScratch.Clear();
        _symbols.CopyTo(symbolScratch);
        _lengths.CopyTo(widthScratch);
        return new FsstDecodeTable(symbolScratch, widthScratch, _lengths.Length);
    }

    /// <summary>The <c>u64</c> of symbol <paramref name="code"/>, for diagnostics and tests.</summary>
    /// <param name="code">A code below <see cref="Count"/>.</param>
    internal ulong SymbolBits(int code) =>
        BinaryPrimitives.ReadUInt64LittleEndian(_symbols.Slice(code * SymbolSize, SymbolSize));

    /// <summary>The most code bytes <paramref name="length"/> input bytes can turn into.</summary>
    /// <param name="length">The value's length in bytes.</param>
    internal static int MaxCompressedLength(int length) => length * 2;

    /// <summary>
    /// Compresses <paramref name="value"/> with this table, the way the writer compressed the rows.
    /// </summary>
    /// <param name="value">The bytes to compress.</param>
    /// <param name="destination">At least <see cref="MaxCompressedLength"/> bytes.</param>
    /// <param name="written">How many code bytes were produced.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too short.</returns>
    /// <remarks>
    /// <para>
    /// The same rule the writer applies -- take the longest symbol that matches at this position,
    /// and escape the byte when none does -- and therefore the same output, because that rule does
    /// not depend on the order symbols are examined in: two symbols of equal width that both match
    /// the same bytes are the same symbol. Equal values compress to equal code sequences, which is
    /// what lets an equality be answered on the codes.
    /// </para>
    /// <para>
    /// A LINEAR SCAN OVER AT MOST 255 SYMBOLS, where the writer builds a hash of prefixes and a
    /// table of every two-byte pair. That table is 128 KiB and the writer amortizes it over a whole
    /// column; here one needle is compressed once for a whole scan, so the index would cost more to
    /// build than the scan it accelerates. Keeping the two implementations apart is also what lets a
    /// test compress the corpus both ways and compare, which is the only honest check that they
    /// agree.
    /// </para>
    /// </remarks>
    internal bool TryCompress(ReadOnlySpan<byte> value, Span<byte> destination, out int written)
    {
        written = 0;
        int read = 0;
        int count = _lengths.Length;
        while (read < value.Length)
        {
            ReadOnlySpan<byte> rest = value[read..];
            int best = -1;
            int bestLength = 0;
            for (int code = 0; code < count; code++)
            {
                int width = _lengths[code];
                if (width > rest.Length || width <= bestLength)
                {
                    continue;
                }

                if (rest[..width].SequenceEqual(_symbols.Slice(code * SymbolSize, width)))
                {
                    best = code;
                    bestLength = width;
                }
            }

            if (best >= 0)
            {
                if (written >= destination.Length)
                {
                    return false;
                }

                destination[written++] = (byte)best;
                read += bestLength;
                continue;
            }

            // No symbol covers this byte, so it costs two: the escape and the byte itself.
            if (written + 1 >= destination.Length)
            {
                return false;
            }

            destination[written++] = EscapeCode;
            destination[written++] = value[read++];
        }

        return true;
    }
}

/// <summary>
/// The padded decode tables of one <c>vortex.fsst</c> node, and the kernel over them.
/// </summary>
/// <remarks>
/// Separate from <see cref="FsstSymbolTable"/> because the padding is built once per node while
/// <see cref="Decode"/> runs once per node OR once per wanted row, and the two lifetimes have to be
/// expressible apart.
/// </remarks>
internal readonly ref struct FsstDecodeTable
{
    /// <summary>Every byte of a <c>u64</c> that equals 0xFF is an escape.</summary>
    private const ulong Ones = 0x0101010101010101UL;

    /// <summary>The high bit of every byte.</summary>
    private const ulong Highs = 0x8080808080808080UL;

    /// <summary>Codes consumed per block of the wide loop.</summary>
    private const int Block = 8;

    /// <summary>Bytes a block of eight symbols can advance the cursor by.</summary>
    private const int BlockSlack = Block * FsstSymbolTable.SymbolSize;

    private readonly ReadOnlySpan<byte> _symbols;
    private readonly ReadOnlySpan<byte> _widths;
    private readonly int _count;
    private readonly bool _symbolsAreAscii;

    internal FsstDecodeTable(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> widths, int count)
    {
        _symbols = symbols;
        _widths = widths;
        _count = count;
        _symbolsAreAscii = EveryEmittedByteIsAscii(symbols, widths, count);
    }

    /// <summary>
    /// Whether every byte this table can emit from a SYMBOL is below 0x80.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE UTF-8 GUARANTEE, PRICED AT TWO KILOBYTES INSTEAD OF FIFTY-TWO MEGABYTES. Every byte of a
    /// decoded heap comes from exactly one of two places: the first <c>width</c> bytes of some
    /// symbol, or a raw byte written by the escape path. Nothing else writes it -- the eight-byte
    /// store past a symbol's width is overwritten by the next symbol's store at
    /// <c>written + width</c>, and the final symbols take the exact-copy tail, so no byte beyond
    /// <c>written</c> survives. So "every symbol is ASCII and every escape byte is ASCII" implies
    /// "the whole heap is ASCII", which implies valid UTF-8, with no sweep of the heap at all.
    /// </para>
    /// <para>
    /// This is the same argument `ZstdDecoder` makes frame by frame, and it is worth as much: the
    /// whole-heap `Utf8.IsValid` is <b>763 us of a 7 400 us `fsst` scan, 9.4 %</b>, measured by
    /// short-circuiting it (bench/ab.sh, 2026-09-18). The guarantee is NOT weakened -- a table with
    /// one high byte, or a stream with one high escape byte, falls back to the full sweep -- so
    /// this is not the question PERF-GAPS.md D6 asks.
    /// </para>
    /// </remarks>
    internal bool SymbolsAreAscii => _symbolsAreAscii;

    private static bool EveryEmittedByteIsAscii(
        ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> widths, int count)
    {
        ulong high = 0;
        for (int code = 0; code < count; code++)
        {
            // Only the first `width` bytes of a symbol are ever emitted; the rest of its u64 is
            // padding that the next store covers, so it may hold anything.
            int width = widths[code];
            ReadOnlySpan<byte> symbol =
                symbols.Slice(code * FsstSymbolTable.SymbolSize, FsstSymbolTable.SymbolSize);
            for (int i = 0; i < width; i++)
            {
                high |= symbol[i];
            }
        }

        return (high & 0x80) == 0;
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
    /// <param name="escapeBits">
    /// Accumulates, by OR, every raw byte the escape path writes. With
    /// <see cref="SymbolsAreAscii"/> it decides whether the heap needs a UTF-8 sweep at all: the
    /// two together cover every byte the decode can produce.
    /// </param>
    internal int Decode(
        ReadOnlySpan<byte> codes, Span<byte> destination, string encodingId, ref uint escapeBits)
    {
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte table = ref MemoryMarshal.GetReference(_symbols);
        ref byte widths = ref MemoryMarshal.GetReference(_widths);
        ref byte input = ref MemoryMarshal.GetReference(codes);

        int written = 0;
        int i = 0;
        uint bad = 0;

        // Past this point a symbol no longer has eight writable bytes behind it, so the wide store
        // would run off the end and the exact copy takes over. Negative for a destination under
        // eight bytes, which is right: the wide path is then never taken at all.
        int wideLimit = destination.Length - FsstSymbolTable.SymbolSize;
        int blockLimit = destination.Length - BlockSlack;
        int blockEnd = codes.Length - Block;

        while (true)
        {
            // EIGHT AT A TIME while the block is escape-free and the slack covers eight stores.
            // One compare for the slack and one SWAR test for the escapes, against eight of each.
            //
            // Nothing is owed to the escape here, because trained tables barely produce one. Counted
            // over every file of the corpus: 622 escapes in 264 038 codes, a quarter of a per cent,
            // and 1 640 breaks out of this loop for them -- two and a half per escape, because the
            // 0xFF stays in the eight-byte window until the scalar path has consumed it and the
            // literal that follows an escape can be 0xFF itself. On the million-row `fsst` file the
            // benchmarks read there is not one escape in 9 877 674 codes. Upstream's other shape,
            // which keeps the escape inside the block of eight, would therefore be answering a
            // question this data does not ask.
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
            if (code == FsstSymbolTable.EscapeCode)
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

                byte raw = codes[++i];
                escapeBits |= raw;
                destination[written++] = raw;
                i++;
                continue;
            }

            int width = widthsAt(ref widths, code);
            bad |= (uint)(width - 1);
            if (written <= wideLimit)
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref output, (uint)written),
                    Unsafe.ReadUnaligned<ulong>(
                        ref Unsafe.Add(ref table, (uint)(code * FsstSymbolTable.SymbolSize))));
                written += width;
                i++;
                continue;
            }

            if (written + width > destination.Length)
            {
                return ThrowOverrun(encodingId, destination.Length);
            }

            // The tail, where the wide store would run past the end of the destination. The symbol
            // is a little-endian u64 whose low `width` bytes are the text, which is exactly its
            // first `width` bytes in memory.
            _symbols.Slice(code * FsstSymbolTable.SymbolSize, width)
                .CopyTo(destination.Slice(written, width));
            written += width;
            i++;
        }

        // ONE TEST FOR THE WHOLE STREAM. A width of zero means the code names no symbol; every real
        // width is 1..8, so `width - 1` stays in 0..7 for every legal code and wraps to 0xFFFFFFFF
        // for an absent one. The stores such a code made wrote into slack the next store overwrites
        // and advanced nothing, so nothing has escaped the destination by the time this fires.
        if (bad > FsstSymbolTable.SymbolSize - 1)
        {
            ThrowUnknownCode(codes, encodingId);
        }

        return written;

        static int widthsAt(ref byte widths, byte code) => Unsafe.Add(ref widths, (uint)code);
    }

    /// <summary>One symbol: the wide store, the branchless range accumulation, the advance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step(
        ref byte output, ref byte table, ref byte widths, byte code, int written, ref uint bad)
    {
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref output, (uint)written),
            Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref table, (uint)(code * FsstSymbolTable.SymbolSize))));
        uint width = Unsafe.Add(ref widths, (uint)code);

        // THE PER-SYMBOL CODE CHECK STAYS, and it was priced: removing it entirely reads 0.904
        // against 0.919-0.926 for the same tree with the check in (bench/ab.sh on `fsst` fullscan,
        // 2026-09-18), so it is worth about 2 % of the axis -- inside the 3-5 % this measurement can
        // see. It is a subtract and an or in the shadow of an eight-byte store, and upstream's
        // `get_unchecked` buys that 2 % by decoding a corrupt code into a wrong value in silence.
        // A generic flag could drop it for a full 255-symbol table, where no code CAN be absent;
        // that is a second shape of the hottest loop in the library for a gain this bench cannot
        // resolve, so it is written down rather than written.
        bad |= width - 1;
        return written + (int)width;
    }

    /// <summary>Finds the offending code, on the error path, where cost does not matter.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowUnknownCode(ReadOnlySpan<byte> codes, string encodingId)
    {
        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];
            if (code == FsstSymbolTable.EscapeCode)
            {
                i++;
                continue;
            }

            if (code >= _count)
            {
                CompressedThrow.Format(
                    $"{encodingId}: code {code} names a symbol the {_count}-entry table " +
                    "does not hold.");
            }
        }

        CompressedThrow.Format(
            $"{encodingId}: the code stream names a symbol the {_count}-entry table does not hold.");
    }

    private static int ThrowOverrun(string encodingId, int capacity) =>
        CompressedThrow.Format<int>(
            $"{encodingId}: the code stream decodes to more than the {capacity} bytes its " +
            "uncompressed lengths account for.");
}
