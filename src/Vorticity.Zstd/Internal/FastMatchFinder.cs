using System.Runtime.CompilerServices;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_compressBlock_fast</c> without a dictionary: one hash table of the last
/// position of each hash, searched at every position, a step that grows while nothing matches.
/// </summary>
internal static unsafe class FastMatchFinder
{
    /// <summary>
    /// libzstd's <c>ZSTD_fillHashTableForCCtx</c> in its fast mode: every third position of a
    /// dictionary's content.
    /// </summary>
    public static void FillHashTable(ref MatchState state, byte* end)
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
            uint* hashTable = state.HashTable;
            int hashLog = state.Parameters.HashLog;
            byte* @base = state.Base;
            byte* ip = @base + state.NextToUpdate;
            byte* iend = end - HashReadSize;
            const int FillStep = 3;
            for (; ip + FillStep < iend + 2; ip += FillStep)
            {
                hashTable[THash.Hash(Read64(ip), hashLog)] = (uint)(ip - @base);
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
    /// What the search loop does not touch, in memory the JIT does not promote (stack allocated):
    /// in registers, these values pushed the loop's own out, the hash multiplier among them.
    /// </summary>
    private struct Cold
    {
        public byte* Anchor;
        public byte* Lit;
        public SequenceRecord* Sequence;
        public uint* Counts;
        public uint RepOffset2;
        public uint Saved1;
        public uint Saved2;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_fast_noDict_generic</c>: the search pipelined over four
    /// positions, two of them searched a round; a repeat offset tried at the third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// libzstd's four positions are two here: <c>ip1</c> is <c>ip0 + 1</c> and <c>ip3</c> is
    /// <c>ip2 + 1</c> at the top of every round, and its two halves are written out, the second at
    /// <c>ip0 + 1</c>. The table is read and written in libzstd's order, which the results depend
    /// on when two positions share a hash.
    /// </para>
    /// <para>
    /// The loop keeps fifteen values; those it does not use are in <see cref="Cold"/>, read and
    /// written once a sequence, and <c>current0</c> is set where the loop exits.
    /// </para>
    /// </remarks>
    private static nuint CompressBlock<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        uint* hashTable = state.HashTable;
        int hashLog = state.Parameters.HashLog;
        int targetLength = state.Parameters.TargetLength;
        nuint stepSize = (nuint)(targetLength + (targetLength == 0 ? 1 : 0) + 1);
        byte* @base = state.Base;
        byte* istart = source;
        uint endIndex = (uint)(istart - @base + (nint)size);
        uint prefixStartIndex = state.LowestPrefixIndex(endIndex);
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;

        byte* coldBytes = stackalloc byte[sizeof(Cold)];
        Cold* cold = (Cold*)coldBytes;
        cold->Anchor = istart;
        cold->Lit = store.Literals;
        cold->Sequence = store.Sequences;
        cold->Counts = store.Counts;
        cold->Saved1 = 0;
        cold->Saved2 = 0;

        byte* ip0 = istart;
        byte* ip2;
        uint current0;
        uint repOffset1 = rep[0];
        cold->RepOffset2 = rep[1];

        nuint hash0;
        nuint hash1;
        uint matchIndex;
        uint offBase;
        byte* match0;
        nuint matchLength;

        // ip0 + 1 is the second position of a round; the step separates the rounds.
        nuint step;
        byte* nextStep;
        const nuint StepIncrement = 1 << (SearchStrength - 1);

        ip0 += ip0 == @base + prefixStartIndex ? 1 : 0;
        {
            uint current = (uint)(ip0 - @base);
            uint windowLow = state.LowestPrefixIndex(current);
            uint maxRep = current - windowLow;
            if (cold->RepOffset2 > maxRep)
            {
                cold->Saved2 = cold->RepOffset2;
                cold->RepOffset2 = 0;
            }

            if (repOffset1 > maxRep)
            {
                cold->Saved1 = repOffset1;
                repOffset1 = 0;
            }
        }

    Start:
        step = stepSize;
        nextStep = ip0 + StepIncrement;
        ip2 = ip0 + step;
        if (ip2 + 1 >= ilimit)
        {
            goto Cleanup;
        }

        hash0 = THash.Hash(Read64(ip0), hashLog);
        hash1 = THash.Hash(Read64(ip0 + 1), hashLog);
        matchIndex = hashTable[hash0];

        do
        {
            // ---- ip0, and the repeat offset at ip2
            uint repValue = Read32(ip2 - repOffset1);
            hashTable[hash0] = (uint)(ip0 - @base);
            if ((Read32(ip2) == repValue) & (repOffset1 > 0))
            {
                current0 = (uint)(ip0 - @base);

                // ip0 + 1 is before the repeat match: its entry is safe to write.
                hashTable[hash1] = current0 + 1;
                ip0 = ip2;
                match0 = ip0 - repOffset1;
                matchLength = ip0[-1] == match0[-1] ? 1u : 0u;
                ip0 -= matchLength;
                match0 -= matchLength;
                offBase = RepeatCode1;
                matchLength += 4;
                goto Match;
            }

            if (MatchFound(ip0, @base, matchIndex, prefixStartIndex))
            {
                // ip0 + 1, where the search resumes at the earliest.
                current0 = (uint)(ip0 - @base);
                hashTable[hash1] = current0 + 1;
                goto Offset;
            }

            // ---- ip0 + 1
            matchIndex = hashTable[hash1];
            hash0 = THash.Hash(Read64(ip2), hashLog);
            hashTable[hash1] = (uint)(ip0 + 1 - @base);
            if (MatchFound(ip0 + 1, @base, matchIndex, prefixStartIndex))
            {
                // Not past where the search resumes: a match is four bytes at least.
                if (step <= 4)
                {
                    hashTable[hash0] = (uint)(ip2 - @base);
                }

                ip0++;
                current0 = (uint)(ip0 - @base);
                goto Offset;
            }

            // ---- the next round, from ip2
            matchIndex = hashTable[hash0];
            hash1 = THash.Hash(Read64(ip2 + 1), hashLog);
            ip0 = ip2;
            ip2 = ip0 + step;
            if (ip2 >= nextStep)
            {
                step++;
                nextStep += StepIncrement;
            }
        }
        while (ip2 + 1 < ilimit);

    Cleanup:
        {
            // A repeat offset invalid at the start of the block, and never replaced, is restored for
            // the next one; when the first was replaced, the second becomes the old first.
            uint offsetSaved1 = cold->Saved1;
            uint offsetSaved2 = offsetSaved1 != 0 && repOffset1 != 0 ? offsetSaved1 : cold->Saved2;
            rep[0] = repOffset1 != 0 ? repOffset1 : offsetSaved1;
            rep[1] = cold->RepOffset2 != 0 ? cold->RepOffset2 : offsetSaved2;
            store.Literals = cold->Lit;
            store.Sequences = cold->Sequence;
            return (nuint)(iend - cold->Anchor);
        }

    Offset:
        match0 = @base + matchIndex;
        cold->RepOffset2 = repOffset1;
        repOffset1 = (uint)(ip0 - match0);
        offBase = OffsetToOffBase(repOffset1);
        matchLength = 4;

        // The match extended backward.
        {
            byte* anchor = cold->Anchor;
            byte* prefixStart = @base + prefixStartIndex;
            while (((ip0 > anchor) & (match0 > prefixStart)) && ip0[-1] == match0[-1])
            {
                ip0--;
                match0--;
                matchLength++;
            }
        }

    Match:
        matchLength += Count(ip0 + matchLength, match0 + matchLength, iend);
        {
            byte* anchor = cold->Anchor;
            SequenceStore.Store(ref cold->Lit, ref cold->Sequence, cold->Counts, (nuint)(ip0 - anchor), anchor, iend, offBase, matchLength);
        }

        ip0 += matchLength;
        cold->Anchor = ip0;

        if (ip0 <= ilimit)
        {
            // Two positions of the match into the table, and the second repeat offset tried at once.
            hashTable[THash.Hash(Read64(@base + current0 + 2), hashLog)] = current0 + 2;
            hashTable[THash.Hash(Read64(ip0 - 2), hashLog)] = (uint)(ip0 - 2 - @base);

            uint repOffset2 = cold->RepOffset2;
            if (repOffset2 > 0)
            {
                while (ip0 <= ilimit && Read32(ip0) == Read32(ip0 - repOffset2))
                {
                    nuint repLength = Count(ip0 + 4, ip0 + 4 - repOffset2, iend) + 4;
                    (repOffset1, repOffset2) = (repOffset2, repOffset1);
                    hashTable[THash.Hash(Read64(ip0), hashLog)] = (uint)(ip0 - @base);
                    ip0 += repLength;
                    SequenceStore.StoreOnly(ref cold->Sequence, cold->Counts, 0, RepeatCode1, repLength);
                    cold->Anchor = ip0;
                }

                cold->RepOffset2 = repOffset2;
            }
        }

        goto Start;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_match4Found_cmov</c>: whether the candidate at <paramref name="matchIndex"/>,
    /// within the window, starts with the same four bytes. A candidate below the window is read at
    /// the window's start instead (see <see cref="MatchFinder.ClampToWindow"/>), and the window test
    /// joins the compare in one condition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchFound(byte* current, byte* @base, uint matchIndex, uint lowLimit) =>
        (Read32(current) == Read32(@base + ClampToWindow(matchIndex, lowLimit))) & (matchIndex >= lowLimit);
}
