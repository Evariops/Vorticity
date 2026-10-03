using System;
using System.Runtime.CompilerServices;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_compressBlock_doubleFast</c> without a dictionary: a table of eight-byte hashes
/// (<see cref="MatchState.HashTable"/>) for long matches, one of short hashes
/// (<see cref="MatchState.ChainTable"/>), the long one tried first.
/// </summary>
internal static unsafe class DoubleFastMatchFinder
{
    /// <summary>
    /// libzstd's <c>ZSTD_fillDoubleHashTableForCCtx</c> in its fast mode: every third position of a
    /// dictionary's content, into both tables.
    /// </summary>
    public static void FillHashTables(ref MatchState state, byte* end)
    {
        switch (state.Parameters.MinMatch)
        {
            case 5: Fill<Hash5>(ref state, end); break;
            case 6: Fill<Hash6>(ref state, end); break;
            case 7: Fill<Hash7>(ref state, end); break;
            default: Fill<Hash4>(ref state, end); break;
        }

        static void Fill<THash>(ref MatchState state, byte* end)
            where THash : IMatchHash
        {
            uint* hashLarge = state.HashTable;
            int hashLogLarge = state.Parameters.HashLog;
            uint* hashSmall = state.ChainTable;
            int hashLogSmall = state.Parameters.ChainLog;
            byte* @base = state.Base;
            byte* ip = @base + state.NextToUpdate;
            byte* iend = end - HashReadSize;
            const int FillStep = 3;
            for (; ip + FillStep - 1 <= iend; ip += FillStep)
            {
                uint current = (uint)(ip - @base);
                ulong bytes = Read64(ip);
                hashSmall[THash.Hash(bytes, hashLogSmall)] = current;
                hashLarge[Hash8.Hash(bytes, hashLogLarge)] = current;
            }
        }
    }

    /// <summary>The block's sequences into <paramref name="store"/>, by minimum match length.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlock(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size) =>
        state.Parameters.MinMatch switch
        {
            5 => CompressBlock<Hash5>(ref state, store, rep, source, size),
            6 => CompressBlock<Hash6>(ref state, store, rep, source, size),
            7 => CompressBlock<Hash7>(ref state, store, rep, source, size),
            _ => CompressBlock<Hash4>(ref state, store, rep, source, size),
        };

    /// <summary>
    /// What the search loop does not touch, in a local whose address is taken, which the JIT keeps in
    /// the frame: in registers, these values pushed the loop's own out, both hash multipliers among
    /// them, and through a pointer, the pointer took one.
    /// </summary>
    private struct Cold
    {
        public byte* Anchor;
        public byte* Lit;
        public SequenceRecord* Sequence;
        public uint* Counts;
        public byte* End;
        public uint Offset2;
        public uint Saved1;
        public uint Saved2;
        public uint Current;
    }

    /// <summary>libzstd's <c>ZSTD_compressBlock_doubleFast_noDict_generic</c>.</summary>
    /// <remarks>
    /// <para>
    /// libzstd tests a candidate as <c>read(safe) == read(ip) &amp;&amp; safe == candidate</c>, the
    /// safe address dummy bytes for a candidate below the window: the second test is the window test.
    /// Here a candidate below the window is read at the window's start instead, which is in the
    /// source, and the two tests are one condition, combined with <c>&amp;</c> into one chain of
    /// compares: no select, no branch on the window.
    /// </para>
    /// <para>
    /// The values the search loop uses are locals; those it does not are in <see cref="Cold"/>, read
    /// and written once a sequence, the end of the block among them.
    /// </para>
    /// </remarks>
    private static nuint CompressBlock<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        uint* hashLong = state.HashTable;
        int hashLogLong = state.Parameters.HashLog;
        uint* hashSmall = state.ChainTable;
        int hashLogSmall = state.Parameters.ChainLog;
        byte* @base = state.Base;
        byte* istart = source;
        uint endIndex = (uint)(istart - @base + (nint)size);
        nuint prefixLowestIndex = state.LowestPrefixIndex(endIndex);
        byte* ilimit = istart + size - HashReadSize;

        Cold cold = default;
        Expose(&cold);
        cold.Anchor = istart;
        cold.End = istart + size;
        cold.Lit = store.Literals;
        cold.Sequence = store.Sequences;
        cold.Counts = store.Counts;

        uint offset1 = rep[0];
        cold.Offset2 = rep[1];

        nuint matchLength;
        uint offset;

        // How many positions to search before the step grows, and where it grows next.
        const nuint StepIncrement = 1 << SearchStrength;
        byte* nextStep;
        nuint step;

        nuint hashLong0;
        nuint hashLong1;
        nuint indexLong0;
        nuint indexLong1;
        byte* match;

        byte* ip = istart;
        byte* ip1;

        ip += ip == @base + prefixLowestIndex ? 1 : 0;
        {
            uint start = (uint)(ip - @base);
            uint windowLow = state.LowestPrefixIndex(start);
            uint maxRep = start - windowLow;
            if (cold.Offset2 > maxRep)
            {
                cold.Saved2 = cold.Offset2;
                cold.Offset2 = 0;
            }

            if (offset1 > maxRep)
            {
                cold.Saved1 = offset1;
                offset1 = 0;
            }
        }

    // ---- one round per sequence stored
    Start:
        step = 1;
        nextStep = ip + StepIncrement;
        ip1 = ip + step;
        if (ip1 > ilimit)
        {
            goto Cleanup;
        }

        // The eight bytes at ip, read once for its two hashes and its two compares.
        ulong ipBytes = Read64(ip);
        hashLong0 = Hash8.Hash(ipBytes, hashLogLong);
        indexLong0 = hashLong[hashLong0];

        // ---- one round per position searched
        do
        {
            nuint hashShort0 = THash.Hash(ipBytes, hashLogSmall);
            nuint indexShort0 = hashSmall[hashShort0];
            uint current = (uint)(ip - @base);
            hashLong[hashLong0] = hashSmall[hashShort0] = current;

            // The repeat offset at ip + 1. It is 0 (invalid) only before the block's first match,
            // when the candidate is ip itself: compared with ip, it is not a test the JIT hoists and
            // keeps in a register.
            byte* repeat = ip - offset1;
            if ((Read32(repeat + 1) == (uint)(ipBytes >> 8)) & (repeat != ip))
            {
                cold.Current = current;
                matchLength = Count(ip + 1 + 4, repeat + 1 + 4, cold.End) + 4;
                ip++;
                byte* anchor = cold.Anchor;
                byte* lit = cold.Lit;
                SequenceRecord* sequence = cold.Sequence;
                SequenceStore.Store(ref lit, ref sequence, cold.Counts, (nuint)(ip - anchor), anchor, cold.End, RepeatCode1, matchLength);
                cold.Lit = lit;
                cold.Sequence = sequence;
                goto MatchStored;
            }

            ulong ip1Bytes = Read64(ip1);
            hashLong1 = Hash8.Hash(ip1Bytes, hashLogLong);

            // A long match at ip. A candidate below the window is read at its start: see the remarks.
            match = @base + ClampToWindow(indexLong0, prefixLowestIndex);
            if ((Read64(match) == ipBytes) & (indexLong0 >= prefixLowestIndex))
            {
                cold.Current = current;
                matchLength = Count(ip + 8, match + 8, cold.End) + 8;
                offset = (uint)(ip - match);
                goto ExtendBackward;
            }

            indexLong1 = hashLong[hashLong1];

            // A short match at ip.
            match = @base + ClampToWindow(indexShort0, prefixLowestIndex);
            if ((Read32(match) == (uint)ipBytes) & (indexShort0 >= prefixLowestIndex))
            {
                cold.Current = current;
                goto SearchNextLong;
            }

            if (ip1 >= nextStep)
            {
                step++;
                nextStep += StepIncrement;
            }

            ip = ip1;
            ip1 += step;
            ipBytes = ip1Bytes;
            hashLong0 = hashLong1;
            indexLong0 = indexLong1;
        }
        while (ip1 <= ilimit);

    Cleanup:
        {
            uint offsetSaved1 = cold.Saved1;
            uint offsetSaved2 = offsetSaved1 != 0 && offset1 != 0 ? offsetSaved1 : cold.Saved2;
            rep[0] = offset1 != 0 ? offset1 : offsetSaved1;
            rep[1] = cold.Offset2 != 0 ? cold.Offset2 : offsetSaved2;
            store.Literals = cold.Lit;
            store.Sequences = cold.Sequence;
            return (nuint)(cold.End - cold.Anchor);
        }

    SearchNextLong:
        // A short match: a long one at ip + 1 may be better.
        matchLength = Count(ip + 4, match + 4, cold.End) + 4;
        offset = (uint)(ip - match);
        {
            byte* matchLong1 = @base + indexLong1;
            if (indexLong1 > prefixLowestIndex && Read64(matchLong1) == Read64(ip1))
            {
                nuint length1 = Count(ip1 + 8, matchLong1 + 8, cold.End) + 8;
                if (length1 > matchLength)
                {
                    ip = ip1;
                    matchLength = length1;
                    offset = (uint)(ip - matchLong1);
                    match = matchLong1;
                }
            }
        }

    ExtendBackward:
        {
            byte* anchor = cold.Anchor;
            byte* prefixLowest = @base + prefixLowestIndex;
            while (((ip > anchor) & (match > prefixLowest)) && ip[-1] == match[-1])
            {
                ip--;
                match--;
                matchLength++;
            }

            cold.Offset2 = offset1;
            offset1 = offset;
            if (step < 4)
            {
                // ip1 is before where the search resumes: a match is four bytes at least.
                hashLong[hashLong1] = (uint)(ip1 - @base);
            }

            // The cursors in locals while they are used: in the frame, the JIT reloaded them after
            // every store the records take, which may alias them.
            byte* lit = cold.Lit;
            SequenceRecord* sequence = cold.Sequence;
            SequenceStore.Store(ref lit, ref sequence, cold.Counts, (nuint)(ip - anchor), anchor, cold.End, OffsetToOffBase(offset), matchLength);
            cold.Lit = lit;
            cold.Sequence = sequence;
        }

    MatchStored:
        ip += matchLength;
        cold.Anchor = ip;

        if (ip <= ilimit)
        {
            // Complementary insertions, then the second repeat offset tried at once.
            uint indexToInsert = cold.Current + 2;
            ulong insertBytes = Read64(@base + indexToInsert);
            hashLong[Hash8.Hash(insertBytes, hashLogLong)] = indexToInsert;
            hashLong[Hash8.Hash(Read64(ip - 2), hashLogLong)] = (uint)(ip - 2 - @base);
            hashSmall[THash.Hash(insertBytes, hashLogSmall)] = indexToInsert;
            hashSmall[THash.Hash(Read64(ip - 1), hashLogSmall)] = (uint)(ip - 1 - @base);

            uint offset2 = cold.Offset2;
            while (ip <= ilimit && ((offset2 > 0) & (Read32(ip) == Read32(ip - offset2))))
            {
                nuint repLength = Count(ip + 4, ip + 4 - offset2, cold.End) + 4;
                (offset1, offset2) = (offset2, offset1);
                ulong repBytes = Read64(ip);
                hashSmall[THash.Hash(repBytes, hashLogSmall)] = (uint)(ip - @base);
                hashLong[Hash8.Hash(repBytes, hashLogLong)] = (uint)(ip - @base);
                SequenceRecord* sequence = cold.Sequence;
                SequenceStore.StoreOnly(ref sequence, cold.Counts, 0, RepeatCode1, repLength);
                cold.Sequence = sequence;
                ip += repLength;
                cold.Anchor = ip;
            }

            cold.Offset2 = offset2;
        }

        goto Start;
    }

    /// <summary>
    /// Takes the address of <paramref name="cold"/>'s local, so that the JIT keeps it in the frame and
    /// reads its fields there, by offset, instead of in registers or through a pointer register.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Expose(Cold* cold)
    {
    }
}
