using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// DEFLATE data, RFC 1951, decoded into a destination of exactly the length the caller expects: the
/// stored, fixed and dynamic blocks, up to the final one.
/// </summary>
/// <remarks>
/// <para>
/// A symbol is read from a table indexed by the next bits of the stream, its codeword's length, its
/// extra bits' count and its value in one entry, so that a literal is one lookup, and a match, a
/// length and a distance with their extra bits, two. A table holds 2^11 entries for the literals and
/// lengths and 2^8 for the distances, a longer codeword reaching a subtable of the entry its first
/// bits index; a code's tables are built once a block, from its codeword lengths.
/// </para>
/// <para>
/// The bits are kept in a 64-bit word, refilled with no branch eight bytes at a time while the input
/// holds sixteen more, to 56 bits at least: as many as a literal or a length and a distance with
/// their extra bits take, 48 at most. A match is copied sixteen bytes at a time where its source is
/// that far behind, while the output holds a match and its slack ahead; near either end, a symbol is
/// read with the refills and the copies checked. The code lengths a block declares are refused where
/// zlib refuses them: an over-subscribed code, an incomplete one but for a single codeword of one bit,
/// a precode that is not complete, and literals and lengths with no end of block.
/// </para>
/// </remarks>
internal static class Inflate
{
    private const int LitlenTableBits = 11;
    private const int OffsetTableBits = 8;
    private const int PrecodeTableBits = 7;
    private const int MaxCodewordLength = 15;
    private const int LitlenSymbols = 288;
    private const int OffsetSymbols = 32;
    private const int PrecodeSymbols = 19;

    /// <summary>Entries a literal and length table may take: its primary table, and a subtable of 16 entries at most for each codeword longer than 11 bits.</summary>
    private const int LitlenEntries = (1 << LitlenTableBits) + (LitlenSymbols << (MaxCodewordLength - LitlenTableBits));

    /// <summary>Entries a distance table may take: its primary table, and a subtable of 128 entries at most for each of its codewords.</summary>
    private const int OffsetEntries = (1 << OffsetTableBits) + (OffsetSymbols << (MaxCodewordLength - OffsetTableBits));

    /// <summary>The entries of the tables a decode builds its dynamic blocks' codes into.</summary>
    internal const int TableEntries = LitlenEntries + OffsetEntries + (1 << PrecodeTableBits);

    /// <summary>The output ahead of a symbol read without the checks: a match of 258, and the fifteen bytes past it a copy may write, with room to spare.</summary>
    private const int FastOutput = 258 + 32;

    /// <summary>The input ahead of a symbol read without the checks: the eight bytes a refill loads, and as many again.</summary>
    private const int FastInput = 16;

    // An entry: the bits a symbol takes, its codeword and its extra bits, in the low byte; its
    // codeword's length, or a subtable's index bits, in the next four; its kind in the four after;
    // its value above: a literal's byte, a length's or a distance's base, a subtable's first entry.
    private const uint Exceptional = 1u << 12;
    private const uint Subtable = 1u << 13;
    private const uint EndOfBlock = 1u << 14;
    private const uint Literal = 1u << 15;
    private const uint Invalid = Exceptional;

    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] OffsetBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] OffsetExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] PrecodeOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>The fixed code's two tables, the distances' after the literals and lengths', built once.</summary>
    private static readonly uint[] FixedTables = BuildFixedTables();

    private enum Kind
    {
        Litlen,
        Offset,
        Precode,
    }

    /// <summary>
    /// Decodes the DEFLATE data at the start of <paramref name="source"/> into the start of
    /// <paramref name="destination"/>, with <paramref name="tables"/> of <see cref="TableEntries"/> at
    /// least for its dynamic codes; the bytes of the source the data takes, up to its final block's
    /// last bit, and in <paramref name="produced"/> the bytes it decodes to.
    /// </summary>
    internal static unsafe int Decode(ReadOnlySpan<byte> source, Span<byte> destination, Span<uint> tables, out int produced)
    {
        if (tables.Length < TableEntries)
        {
            throw new ArgumentException("The tables are shorter than a decode builds.", nameof(tables));
        }

        fixed (byte* input = source)
        fixed (byte* output = destination)
        fixed (uint* dynamic = tables)
        fixed (uint* fixedTables = FixedTables)
        {
            byte* inNext = input;
            byte* inEnd = input + source.Length;
            byte* outNext = output;
            byte* outEnd = output + destination.Length;
            ulong bits = 0;
            int count = 0;
            int overread = 0;
            bool final;
            do
            {
                Ensure(ref bits, ref count, ref inNext, inEnd, ref overread, 3);
                final = (bits & 1) != 0;
                int type = (int)(bits >> 1) & 3;
                bits >>= 3;
                count -= 3;
                uint* litlen;
                uint* offsets;
                switch (type)
                {
                    case 0:
                    {
                        // A stored block starts on a byte: the whole bytes the word holds go back to
                        // the input, those it was padded with past the end included.
                        if (overread > count >> 3)
                        {
                            throw Corrupt();
                        }

                        inNext -= (count >> 3) - overread;
                        bits = 0;
                        count = 0;
                        overread = 0;
                        if (inEnd - inNext < 4)
                        {
                            throw Corrupt();
                        }

                        int length = inNext[0] | (inNext[1] << 8);
                        int complement = inNext[2] | (inNext[3] << 8);
                        inNext += 4;
                        if (length != (~complement & 0xFFFF) || length > inEnd - inNext || length > outEnd - outNext)
                        {
                            throw Corrupt();
                        }

                        Buffer.MemoryCopy(inNext, outNext, length, length);
                        inNext += length;
                        outNext += length;
                        continue;
                    }

                    case 1:
                        litlen = fixedTables;
                        offsets = fixedTables + LitlenEntries;
                        break;
                    case 2:
                        litlen = dynamic;
                        offsets = dynamic + LitlenEntries;
                        ReadCodes(ref bits, ref count, ref inNext, inEnd, ref overread, litlen, offsets, dynamic + LitlenEntries + OffsetEntries);
                        break;
                    default:
                        throw Corrupt();
                }

                if (!Fast(ref bits, ref count, ref inNext, inEnd - FastInput, output, ref outNext, outEnd - FastOutput, litlen, offsets))
                {
                    Slow(ref bits, ref count, ref inNext, inEnd, ref overread, output, ref outNext, outEnd, litlen, offsets);
                }
            }
            while (!final);

            // The whole bytes the word still holds are past the data, those it was padded with first.
            if (overread > count >> 3)
            {
                throw Corrupt();
            }

            produced = (int)(outNext - output);
            return (int)(inNext - input) - ((count >> 3) - overread);
        }
    }

    /// <summary>
    /// A block's symbols, from <paramref name="outNextRef"/>, while the input holds the bytes a refill
    /// loads before <paramref name="inLimit"/> and the output a match and its slack before
    /// <paramref name="outLimit"/>, with no check but the codes' and the distances': true at the
    /// block's end, false where either limit stops them first.
    /// </summary>
    /// <remarks>
    /// A method apart from the checked symbols, so that its values stay in registers: a refill a
    /// symbol, to 56 bits at least, which a length and a distance with their extra bits take 48 of at
    /// most, and up to three literals of 15 bits at most.
    /// </remarks>
    private static unsafe bool Fast(
        ref ulong bitsRef, ref int countRef, ref byte* inNextRef, byte* inLimit,
        byte* output, ref byte* outNextRef, byte* outLimit, uint* litlen, uint* offsets)
    {
        ulong bits = bitsRef;
        nint count = countRef;
        byte* inNext = inNextRef;
        byte* outNext = outNextRef;
        bool ended = false;
        while (inNext <= inLimit && outNext <= outLimit)
        {
            // Eight bytes loaded, as many as the word has room for kept: the bits above the count
            // are the stream's next, which the next load writes again.
            bits |= Unsafe.ReadUnaligned<ulong>(inNext) << (int)count;
            inNext += (63 - count) >> 3;
            count |= 56;

            uint entry = litlen[(uint)bits & ((1u << LitlenTableBits) - 1)];
            if ((entry & Exceptional) != 0)
            {
                if ((entry & Subtable) != 0)
                {
                    bits >>= LitlenTableBits;
                    count -= LitlenTableBits;
                    entry = litlen[(entry >> 16) + ((uint)bits & ((1u << (int)((entry >> 8) & 0xF)) - 1))];
                }

                if ((entry & Exceptional) != 0)
                {
                    if ((entry & EndOfBlock) == 0)
                    {
                        throw Corrupt();
                    }

                    bits >>= (int)entry;
                    count -= (byte)entry;
                    ended = true;
                    break;
                }
            }

            // A shift by the entry itself: a shift takes its count's low six bits, an entry's the bits
            // it takes, and the byte its count would be read from is one step more on each literal's chain.
            if ((entry & Literal) != 0)
            {
                bits >>= (int)entry;
                count -= (byte)entry;
                *outNext++ = (byte)(entry >> 16);
                entry = litlen[(uint)bits & ((1u << LitlenTableBits) - 1)];
                if ((entry & Literal) == 0)
                {
                    continue;
                }

                bits >>= (int)entry;
                count -= (byte)entry;
                *outNext++ = (byte)(entry >> 16);
                entry = litlen[(uint)bits & ((1u << LitlenTableBits) - 1)];
                if ((entry & Literal) == 0)
                {
                    continue;
                }

                bits >>= (int)entry;
                count -= (byte)entry;
                *outNext++ = (byte)(entry >> 16);
                continue;
            }

            // A length, its extra bits past its codeword, then its distance's.
            nint length = (nint)(entry >> 16) + (nint)((bits & ((1UL << (byte)entry) - 1)) >> (int)((entry >> 8) & 0xF));
            bits >>= (int)entry;
            count -= (byte)entry;
            entry = offsets[(uint)bits & ((1u << OffsetTableBits) - 1)];
            if ((entry & Exceptional) != 0)
            {
                if ((entry & Subtable) == 0)
                {
                    throw Corrupt();
                }

                bits >>= OffsetTableBits;
                count -= OffsetTableBits;
                entry = offsets[(entry >> 16) + ((uint)bits & ((1u << (int)((entry >> 8) & 0xF)) - 1))];
                if ((entry & Exceptional) != 0)
                {
                    throw Corrupt();
                }
            }

            nint distance = (nint)(entry >> 16) + (nint)((bits & ((1UL << (byte)entry) - 1)) >> (int)((entry >> 8) & 0xF));
            bits >>= (int)entry;
            count -= (byte)entry;
            if (distance > outNext - output)
            {
                throw Corrupt();
            }

            outNext = CopyFast(outNext, distance, length);
        }

        bitsRef = bits;
        countRef = (int)count;
        inNextRef = inNext;
        outNextRef = outNext;
        return ended;
    }

    /// <summary>
    /// A block's symbols near either end, up to the block's end: a refill a byte at a time, the input
    /// padded with zeros it counts, each symbol held to the bits the input holds and to the room the
    /// output has.
    /// </summary>
    private static unsafe void Slow(
        ref ulong bits, ref int count, ref byte* inNext, byte* inEnd, ref int overread,
        byte* output, ref byte* outNext, byte* outEnd, uint* litlen, uint* offsets)
    {
        while (true)
        {
            Ensure(ref bits, ref count, ref inNext, inEnd, ref overread, 48);
            uint entry = litlen[(uint)bits & ((1u << LitlenTableBits) - 1)];
            if ((entry & Subtable) != 0)
            {
                bits >>= LitlenTableBits;
                count -= LitlenTableBits;
                entry = litlen[(entry >> 16) + ((uint)bits & ((1u << (int)((entry >> 8) & 0xF)) - 1))];
            }

            if ((entry & Exceptional) != 0)
            {
                if ((entry & EndOfBlock) == 0)
                {
                    throw Corrupt();
                }

                bits >>= (byte)entry;
                count -= (byte)entry;
                Past(count, overread);
                return;
            }

            if ((entry & Literal) != 0)
            {
                bits >>= (byte)entry;
                count -= (byte)entry;
                Past(count, overread);
                if (outNext == outEnd)
                {
                    throw Corrupt();
                }

                *outNext++ = (byte)(entry >> 16);
                continue;
            }

            nint length = (nint)(entry >> 16) + (nint)((bits & ((1UL << (byte)entry) - 1)) >> (int)((entry >> 8) & 0xF));
            bits >>= (byte)entry;
            count -= (byte)entry;
            entry = offsets[(uint)bits & ((1u << OffsetTableBits) - 1)];
            if ((entry & Subtable) != 0)
            {
                bits >>= OffsetTableBits;
                count -= OffsetTableBits;
                entry = offsets[(entry >> 16) + ((uint)bits & ((1u << (int)((entry >> 8) & 0xF)) - 1))];
            }

            if ((entry & Exceptional) != 0)
            {
                throw Corrupt();
            }

            nint distance = (nint)(entry >> 16) + (nint)((bits & ((1UL << (byte)entry) - 1)) >> (int)((entry >> 8) & 0xF));
            bits >>= (byte)entry;
            count -= (byte)entry;
            Past(count, overread);
            if (distance > outNext - output || length > outEnd - outNext)
            {
                throw Corrupt();
            }

            byte* from = outNext - distance;
            for (nint i = 0; i < length; i++)
            {
                outNext[i] = from[i];
            }

            outNext += length;
        }
    }

    /// <summary>
    /// Copies a match of <paramref name="length"/> bytes from <paramref name="distance"/> back, which may
    /// overlap what it writes, writing up to sixteen bytes past its end: sixteen at a time where the
    /// source is that far behind, one byte repeated a vector at a time, and a shorter pattern doubled
    /// until it is a word, then a word at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* CopyFast(byte* outNext, nint distance, nint length)
    {
        byte* from = outNext - distance;
        byte* end = outNext + length;
        if (distance >= 16)
        {
            // Each sixteen bytes read lie before those written: the match's own, already written,
            // where it repeats itself.
            do
            {
                Vector128.Store(Vector128.Load(from), outNext);
                from += 16;
                outNext += 16;
            }
            while (outNext < end);

            return end;
        }

        if (distance == 1)
        {
            Vector128<byte> repeated = Vector128.Create(*from);
            do
            {
                repeated.Store(outNext);
                outNext += 16;
            }
            while (outNext < end);

            return end;
        }

        while (outNext - from < 8)
        {
            Unsafe.WriteUnaligned(outNext, Unsafe.ReadUnaligned<ulong>(from));
            outNext += outNext - from;
        }

        while (outNext < end)
        {
            Unsafe.WriteUnaligned(outNext, Unsafe.ReadUnaligned<ulong>(from));
            from += 8;
            outNext += 8;
        }

        return end;
    }

    /// <summary>
    /// Refills the word a byte at a time until it holds <paramref name="need"/> bits, the input
    /// padded with zeros past its end, which <paramref name="overread"/> counts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Ensure(ref ulong bits, ref int count, ref byte* inNext, byte* inEnd, ref int overread, int need)
    {
        while (count < need)
        {
            if (inNext < inEnd)
            {
                bits |= (ulong)*inNext++ << count;
            }
            else if (++overread > 8)
            {
                // A symbol takes 48 bits at most: eight bytes of padding are past any the data needs.
                throw Corrupt();
            }

            count += 8;
        }
    }

    /// <summary>Refuses a symbol that took bits of the padding past the input's end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Past(int count, int overread)
    {
        if (count < overread << 3)
        {
            throw Corrupt();
        }
    }

    /// <summary>
    /// A dynamic block's header: the precode, then the codeword lengths of the literals and lengths
    /// and of the distances it codes, built into their tables.
    /// </summary>
    private static unsafe void ReadCodes(ref ulong bits, ref int count, ref byte* inNext, byte* inEnd, ref int overread, uint* litlen, uint* offsets, uint* precode)
    {
        Ensure(ref bits, ref count, ref inNext, inEnd, ref overread, 14);
        int litlenCount = (int)(bits & 0x1F) + 257;
        int offsetCount = (int)((bits >> 5) & 0x1F) + 1;
        int precodeCount = (int)((bits >> 10) & 0xF) + 4;
        bits >>= 14;
        count -= 14;
        if (litlenCount > 286 || offsetCount > 30)
        {
            throw Corrupt();
        }

        Span<byte> lengths = stackalloc byte[LitlenSymbols + OffsetSymbols];
        lengths.Clear();
        for (int i = 0; i < precodeCount; i++)
        {
            Ensure(ref bits, ref count, ref inNext, inEnd, ref overread, 3);
            lengths[PrecodeOrder[i]] = (byte)(bits & 7);
            bits >>= 3;
            count -= 3;
        }

        Build(lengths[..PrecodeSymbols], precode, PrecodeTableBits, Kind.Precode);
        lengths[..PrecodeSymbols].Clear();

        // The lengths of both codes in one run, which a repeat may cross.
        int total = litlenCount + offsetCount;
        Span<byte> declared = stackalloc byte[LitlenSymbols + OffsetSymbols];
        int at = 0;
        while (at < total)
        {
            Ensure(ref bits, ref count, ref inNext, inEnd, ref overread, 14);
            uint entry = precode[bits & ((1u << PrecodeTableBits) - 1)];
            if ((entry & Exceptional) != 0)
            {
                throw Corrupt();
            }

            bits >>= (int)(entry & 0xFF);
            count -= (int)(entry & 0xFF);
            int symbol = (int)(entry >> 16);
            if (symbol < 16)
            {
                declared[at++] = (byte)symbol;
                continue;
            }

            int repeat;
            byte value = 0;
            switch (symbol)
            {
                case 16:
                    if (at == 0)
                    {
                        throw Corrupt();
                    }

                    value = declared[at - 1];
                    repeat = 3 + (int)(bits & 3);
                    bits >>= 2;
                    count -= 2;
                    break;
                case 17:
                    repeat = 3 + (int)(bits & 7);
                    bits >>= 3;
                    count -= 3;
                    break;
                default:
                    repeat = 11 + (int)(bits & 0x7F);
                    bits >>= 7;
                    count -= 7;
                    break;
            }

            if (repeat > total - at)
            {
                throw Corrupt();
            }

            declared.Slice(at, repeat).Fill(value);
            at += repeat;
        }

        Past(count, overread);
        if (declared[256] == 0)
        {
            throw Corrupt();
        }

        declared[..litlenCount].CopyTo(lengths);
        lengths[litlenCount..LitlenSymbols].Clear();
        Build(lengths[..LitlenSymbols], litlen, LitlenTableBits, Kind.Litlen);
        lengths[..OffsetSymbols].Clear();
        declared.Slice(litlenCount, offsetCount).CopyTo(lengths);
        Build(lengths[..OffsetSymbols], offsets, OffsetTableBits, Kind.Offset);
    }

    /// <summary>
    /// Builds the decode table of a canonical code from its codeword <paramref name="lengths"/>, a
    /// symbol each, 0 for a symbol it does not code: its primary table of 2^<paramref name="tableBits"/>
    /// entries, then a subtable for each run of longer codewords that share their first bits, every
    /// entry no codeword reaches invalid. A code that is over-subscribed, or incomplete but for one
    /// codeword of one bit, is refused, a precode incomplete at all.
    /// </summary>
    private static unsafe void Build(ReadOnlySpan<byte> lengths, uint* table, int tableBits, Kind kind)
    {
        Span<int> counts = stackalloc int[MaxCodewordLength + 1];
        counts.Clear();
        int longest = 0;
        foreach (byte length in lengths)
        {
            counts[length]++;
            longest = Math.Max(longest, length);
        }

        counts[0] = 0;
        int left = 1;
        for (int length = 1; length <= MaxCodewordLength; length++)
        {
            left = (left << 1) - counts[length];
            if (left < 0)
            {
                throw Corrupt();
            }
        }

        if (left > 0 && longest > 0 && (kind == Kind.Precode || longest != 1))
        {
            throw Corrupt();
        }

        int primary = 1 << tableBits;
        new Span<uint>(table, primary).Fill(Invalid);
        if (longest == 0)
        {
            // No codeword at all: a distance code a block of literals alone may declare, any use refused.
            return;
        }

        // The first codeword of each length, canonically: shorter codewords first, then by symbol.
        Span<int> next = stackalloc int[MaxCodewordLength + 2];
        int code = 0;
        for (int length = 1; length <= MaxCodewordLength; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        // Each symbol's codeword, its bits in the stream's order.
        Span<int> codes = stackalloc int[lengths.Length];
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            codes[symbol] = length == 0 ? 0 : Reverse(next[length]++, length);
        }

        // A subtable a run of longer codewords that share their first bits: its size the longest
        // of them.
        Span<byte> deepest = stackalloc byte[primary];
        deepest.Clear();
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length > tableBits)
            {
                int prefix = codes[symbol] & (primary - 1);
                deepest[prefix] = (byte)Math.Max(deepest[prefix], length - tableBits);
            }
        }

        int free = primary;
        for (int prefix = 0; prefix < primary; prefix++)
        {
            if (deepest[prefix] != 0)
            {
                int bits = deepest[prefix];
                table[prefix] = Exceptional | Subtable | ((uint)bits << 8) | (uint)tableBits | ((uint)free << 16);
                new Span<uint>(table + free, 1 << bits).Fill(Invalid);
                free += 1 << bits;
            }
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            int reversed = codes[symbol];
            if (length <= tableBits)
            {
                uint entry = Entry(kind, symbol, length);
                for (int i = reversed; i < primary; i += 1 << length)
                {
                    table[i] = entry;
                }
            }
            else
            {
                uint pointer = table[reversed & (primary - 1)];
                int start = (int)(pointer >> 16);
                int bits = (int)((pointer >> 8) & 0xF);
                int rest = length - tableBits;
                uint entry = Entry(kind, symbol, rest);
                for (int i = reversed >> tableBits; i < 1 << bits; i += 1 << rest)
                {
                    table[start + i] = entry;
                }
            }
        }
    }

    /// <summary>
    /// The entry of <paramref name="symbol"/> under a codeword of <paramref name="length"/> bits past its
    /// table's index: the bits it takes with its extra bits, its codeword's length, its kind and its value.
    /// </summary>
    private static uint Entry(Kind kind, int symbol, int length)
    {
        switch (kind)
        {
            case Kind.Precode:
                return (uint)length | ((uint)length << 8) | ((uint)symbol << 16);
            case Kind.Litlen when symbol < 256:
                return (uint)length | ((uint)length << 8) | Literal | ((uint)symbol << 16);
            case Kind.Litlen when symbol == 256:
                return (uint)length | ((uint)length << 8) | Exceptional | EndOfBlock;
            case Kind.Litlen when symbol <= 285:
            {
                int extra = LengthExtra[symbol - 257];
                return (uint)(length + extra) | ((uint)length << 8) | ((uint)LengthBase[symbol - 257] << 16);
            }

            case Kind.Offset when symbol < 30:
            {
                int extra = OffsetExtra[symbol];
                return (uint)(length + extra) | ((uint)length << 8) | ((uint)OffsetBase[symbol] << 16);
            }

            default:
                // A literal and length code's 286 and 287, a distance code's 30 and 31: in the fixed
                // code, never in the data.
                return Invalid;
        }
    }

    /// <summary>The low <paramref name="length"/> bits of <paramref name="code"/> in the reverse order: DEFLATE packs a codeword from its first bit, the stream's lowest.</summary>
    private static int Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return reversed;
    }

    private static unsafe uint[] BuildFixedTables()
    {
        uint[] tables = new uint[LitlenEntries + OffsetEntries];
        byte[] lengths = new byte[LitlenSymbols];
        lengths.AsSpan(0, 144).Fill(8);
        lengths.AsSpan(144, 112).Fill(9);
        lengths.AsSpan(256, 24).Fill(7);
        lengths.AsSpan(280, 8).Fill(8);
        byte[] distances = new byte[OffsetSymbols];
        distances.AsSpan().Fill(5);
        fixed (uint* table = tables)
        {
            Build(lengths, table, LitlenTableBits, Kind.Litlen);
            Build(distances, table + LitlenEntries, OffsetTableBits, Kind.Offset);
        }

        return tables;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParquetFormatException Corrupt() => new("A GZIP page is corrupt: its DEFLATE data breaks RFC 1951 or runs past its bytes.");
}
