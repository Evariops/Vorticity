using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// A validated FSST symbol table, and the decode over it. The encoding replaces recurring byte
/// sequences with one-byte codes naming symbols of one to eight bytes; code 255 is the escape and
/// the byte after it is emitted literally, which is how a byte no symbol covers still round-trips.
/// Decoding is therefore a flat walk over the code stream with no state beyond the output cursor:
/// an escape copies the next input byte, any other code copies the first <c>width</c> bytes of its
/// symbol.
/// </summary>
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

    /// <summary>Bytes of scratch <see cref="Prepare"/> needs for the padded symbol table, an entry a code.</summary>
    internal const int SymbolScratchBytes = 256 * FsstEntry.Size;

    /// <summary>Bytes of scratch <see cref="Prepare"/> needs for the padded width table.</summary>
    internal const int WidthScratchBytes = 256;

    /// <summary>
    /// Builds the padded decode tables into caller-owned scratch, once per node.
    /// </summary>
    /// <param name="symbolScratch">At least <see cref="SymbolScratchBytes"/> writable bytes.</param>
    /// <param name="widthScratch">At least <see cref="WidthScratchBytes"/> writable bytes.</param>
    /// <remarks>
    /// The scratch is the caller's because the selective path calls <see cref="FsstDecodeTable.Decode"/>
    /// once per wanted row, and rebuilding the padded tables per row would cost more than the
    /// branches they remove.
    /// </remarks>
    internal FsstDecodeTable Prepare(Span<byte> symbolScratch, Span<byte> widthScratch)
    {
        symbolScratch = symbolScratch[..SymbolScratchBytes];
        widthScratch = widthScratch[..WidthScratchBytes];

        // Zero-filled past the real table: an absent code then reads a zero width.
        symbolScratch.Clear();
        widthScratch.Clear();
        Span<FsstEntry> entries = MemoryMarshal.Cast<byte, FsstEntry>(symbolScratch);
        for (int code = 0; code < _lengths.Length; code++)
        {
            entries[code] = new FsstEntry(SymbolBits(code), _lengths[code]);
        }

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
    /// A linear scan over at most 255 symbols, where the writer instead builds a hash of prefixes
    /// and a table of every two-byte pair. The writer amortizes that index over a whole column;
    /// here one needle is compressed once for a whole scan, so building the index would cost more
    /// than the scan it accelerates. Keeping the two implementations apart is also what lets a test
    /// compress the same input both ways and compare, which is the only honest check that they
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
/// A code's symbol and width side by side, so that the kernel reads both with one load.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Size)]
internal readonly struct FsstEntry(ulong symbol, ulong width)
{
    /// <summary>Bytes of an entry.</summary>
    internal const int Size = 16;

    /// <summary>The symbol as a little-endian <c>u64</c>, its first <see cref="Width"/> bytes the text.</summary>
    internal readonly ulong Symbol = symbol;

    /// <summary>The symbol's length, 1 to 8, or 0 for a code that names none.</summary>
    internal readonly ulong Width = width;
}

/// <summary>
/// The padded decode tables of one <c>vortex.fsst</c> node, and the kernel over them.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="FsstSymbolTable"/> because the padding is built once per node while
/// <see cref="Decode"/> runs once per node or once per wanted row, and the two lifetimes have to be
/// expressible apart.
/// </para>
/// <para>
/// The kernel tests sixteen codes at once against the table's size, reads each code's symbol and
/// width with one load, and writes each symbol as a single unaligned eight-byte store at the sum of
/// the widths before it, the slack past the symbol being overwritten by the next store. That shape
/// needs the tables padded to the whole code space, so an absent code reads a zero width instead of
/// indexing past the symbol buffer, and it needs the output's remaining room checked once per block
/// rather than once per code.
/// </para>
/// </remarks>
internal readonly ref struct FsstDecodeTable
{
    /// <summary>Codes consumed per block of the wide loop.</summary>
    private const int Block = 16;

    /// <summary>Bytes a block of symbols can advance the cursor by.</summary>
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
    /// Whether every byte this table can emit from a symbol is below 0x80.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a decoded heap earns its text validity from the table alone, instead of a sweep
    /// over the heap. Every byte of the heap comes from exactly one of two places: the first
    /// <c>width</c> bytes of some symbol, or a raw byte written by the escape path. Nothing else
    /// writes it -- the eight-byte store past a symbol's width is overwritten by the next symbol's
    /// store at <c>written + width</c>, and the final symbols take the exact-copy tail, so no byte
    /// beyond <c>written</c> survives. "Every symbol is ASCII and every escape byte is ASCII"
    /// therefore implies that the whole heap is ASCII, which is valid text.
    /// </para>
    /// <para>
    /// The guarantee is not weakened by this: a table with one high byte, or a stream with one high
    /// escape byte, falls back to validating the whole heap.
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
            ReadOnlySpan<byte> symbol = symbols.Slice(code * FsstEntry.Size, FsstSymbolTable.SymbolSize);
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
    /// the output is too small for what the stream decodes to.
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
        ref FsstEntry table = ref Unsafe.As<byte, FsstEntry>(ref MemoryMarshal.GetReference(_symbols));
        ref byte widths = ref MemoryMarshal.GetReference(_widths);
        ref byte input = ref MemoryMarshal.GetReference(codes);

        // Native-width cursors: an int would be widened before every address it forms.
        nint written = 0;
        nint i = 0;
        uint bad = 0;

        // Past this point a symbol lacks eight writable bytes behind it, so the wide store
        // would run off the end and the exact copy takes over. Negative for a destination under
        // eight bytes, which is right: the wide path is then never taken at all.
        nint wideLimit = destination.Length - FsstSymbolTable.SymbolSize;
        nint blockLimit = destination.Length - BlockSlack;
        nint blockEnd = codes.Length - Block;

        while (true)
        {
            i = Blocks(ref output, ref table, ref input, i, blockEnd, blockLimit, (byte)_count, ref written);
            if (i >= codes.Length)
            {
                break;
            }

            byte code = codes[(int)i];
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

                byte raw = codes[(int)++i];
                escapeBits |= raw;
                destination[(int)written++] = raw;
                i++;
                continue;
            }

            int width = Unsafe.Add(ref widths, code);
            bad |= (uint)(width - 1);
            if (written <= wideLimit)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, written), Unsafe.Add(ref table, code).Symbol);
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
            _symbols.Slice(code * FsstEntry.Size, width).CopyTo(destination.Slice((int)written, width));
            written += width;
            i++;
        }

        // One test for the whole stream. A width of zero means the code names no symbol; every real
        // width is 1..8, so `width - 1` stays in 0..7 for every legal code and wraps to 0xFFFFFFFF
        // for an absent one. The stores such a code made wrote into slack the next store overwrites
        // and advanced nothing, so nothing has escaped the destination by the time this fires.
        if (bad > FsstSymbolTable.SymbolSize - 1)
        {
            ThrowUnknownCode(codes, encodingId);
        }

        return (int)written;
    }

    /// <summary>
    /// Sixteen codes at a time while none of a block is past the table and the slack covers sixteen
    /// stores; returns where it stopped, and the bytes written so far in <paramref name="written"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code at or past the table's size is the escape, 255, or names no symbol: one unsigned
    /// compare over a block finds both, so the codes this loop takes need no check of their own, and
    /// a block holding either goes back to the caller, which handles the one and refuses the other.
    /// Nothing more is owed to the escape: a trained table barely produces one.
    /// </para>
    /// <para>
    /// A method of its own, with no call in it, so that its state stays in registers: the caller's
    /// escape and tail paths call out, and sharing a body with them spills this loop's state to the
    /// stack on every block.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Blocks(
        ref byte output, ref FsstEntry table, ref byte input, nint i, nint blockEnd, nint blockLimit,
        byte count, ref nint written)
    {
        Vector128<byte> size = Vector128.Create(count);
        nint at = written;
        while (i <= blockEnd && at <= blockLimit)
        {
            ref byte codes = ref Unsafe.Add(ref input, i);
            Vector128<byte> past = Vector128.GreaterThanOrEqual(Vector128.LoadUnsafe(ref codes), size);
            if (past != Vector128<byte>.Zero)
            {
                // The codes before the first one past the table are symbols like any other: they
                // go out here, and the caller starts at that one.
                int ahead = BitOperations.TrailingZeroCount(past.ExtractMostSignificantBits());
                for (int k = 0; k < ahead; k++)
                {
                    FsstEntry entry = Unsafe.Add(ref table, Unsafe.Add(ref codes, k));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, at), entry.Symbol);
                    at += (nint)entry.Width;
                }

                i += ahead;
                break;
            }

            at = Eight(ref output, ref table, ref codes, at);
            at = Eight(ref output, ref table, ref Unsafe.Add(ref codes, 8), at);
            i += Block;
        }

        written = at;
        return i;
    }

    /// <summary>
    /// Eight codes, each a symbol of the table: eight wide stores, each at the sum of the widths
    /// before it.
    /// </summary>
    /// <remarks>
    /// The sums are a tree rather than a running total: a running total makes every store wait for
    /// the width of the one before, eight additions in a row, where the tree is three deep and only
    /// the total carries to the next eight.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Eight(ref byte output, ref FsstEntry table, ref byte codes, nint written)
    {
        FsstEntry e0 = Unsafe.Add(ref table, codes);
        FsstEntry e1 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 1));
        FsstEntry e2 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 2));
        FsstEntry e3 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 3));
        FsstEntry e4 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 4));
        FsstEntry e5 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 5));
        FsstEntry e6 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 6));
        FsstEntry e7 = Unsafe.Add(ref table, Unsafe.Add(ref codes, 7));

        nint w0 = (nint)e0.Width;
        nint w2 = (nint)e2.Width;
        nint w4 = (nint)e4.Width;
        nint w6 = (nint)e6.Width;
        nint w01 = w0 + (nint)e1.Width;
        nint w23 = w2 + (nint)e3.Width;
        nint w45 = w4 + (nint)e5.Width;
        nint w67 = w6 + (nint)e7.Width;
        nint w03 = w01 + w23;
        nint w05 = w03 + w45;

        ref byte at = ref Unsafe.Add(ref output, written);
        Unsafe.WriteUnaligned(ref at, e0.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w0), e1.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w01), e2.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w01 + w2), e3.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w03), e4.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w03 + w4), e5.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w05), e6.Symbol);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, w05 + w6), e7.Symbol);
        return written + w03 + (w45 + w67);
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
