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
                hashTable[THash.Hash(ip, hashLog)] = (uint)(ip - @base);
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
    /// libzstd's <c>ZSTD_compressBlock_fast_noDict_generic</c>: the search pipelined over four
    /// positions, two of them searched a round; a repeat offset tried at the third.
    /// </summary>
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
        byte* prefixStart = @base + prefixStartIndex;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;

        byte* anchor = istart;
        byte* ip0 = istart;
        byte* ip1;
        byte* ip2;
        byte* ip3;
        uint current0;

        uint repOffset1 = rep[0];
        uint repOffset2 = rep[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        nuint hash0;
        nuint hash1;
        uint matchIndex;
        uint offBase;
        byte* match0;
        nuint matchLength;

        // ip0 and ip1 are adjacent; the step separates the pairs, ip0 from ip2.
        nuint step;
        byte* nextStep;
        const nuint StepIncrement = 1 << (SearchStrength - 1);

        // A byte that matches no candidate stands for the candidates below the window.
        ulong dummy = 0x78563412;
        byte* dummyAddress = (byte*)&dummy;

        ip0 += ip0 == prefixStart ? 1 : 0;
        {
            uint current = (uint)(ip0 - @base);
            uint windowLow = state.LowestPrefixIndex(current);
            uint maxRep = current - windowLow;
            if (repOffset2 > maxRep)
            {
                offsetSaved2 = repOffset2;
                repOffset2 = 0;
            }

            if (repOffset1 > maxRep)
            {
                offsetSaved1 = repOffset1;
                repOffset1 = 0;
            }
        }

    Start:
        step = stepSize;
        nextStep = ip0 + StepIncrement;
        ip1 = ip0 + 1;
        ip2 = ip0 + step;
        ip3 = ip2 + 1;
        if (ip3 >= ilimit)
        {
            goto Cleanup;
        }

        hash0 = THash.Hash(ip0, hashLog);
        hash1 = THash.Hash(ip1, hashLog);
        matchIndex = hashTable[hash0];

        do
        {
            // The repeat offset at ip2.
            uint repValue = Read32(ip2 - repOffset1);
            current0 = (uint)(ip0 - @base);
            hashTable[hash0] = current0;
            if ((Read32(ip2) == repValue) & (repOffset1 > 0))
            {
                ip0 = ip2;
                match0 = ip0 - repOffset1;
                matchLength = ip0[-1] == match0[-1] ? 1u : 0u;
                ip0 -= matchLength;
                match0 -= matchLength;
                offBase = RepeatCode1;
                matchLength += 4;

                // ip1 is before the repeat match: its entry is safe to write.
                hashTable[hash1] = (uint)(ip1 - @base);
                goto Match;
            }

            if (MatchFound(ip0, @base, matchIndex, prefixStartIndex, dummyAddress))
            {
                // ip1 = ip0 + 1, where the search resumes at the earliest.
                hashTable[hash1] = (uint)(ip1 - @base);
                goto Offset;
            }

            matchIndex = hashTable[hash1];
            hash0 = hash1;
            hash1 = THash.Hash(ip2, hashLog);
            ip0 = ip1;
            ip1 = ip2;
            ip2 = ip3;

            current0 = (uint)(ip0 - @base);
            hashTable[hash0] = current0;
            if (MatchFound(ip0, @base, matchIndex, prefixStartIndex, dummyAddress))
            {
                // Not past where the search resumes: a match is four bytes at least.
                if (step <= 4)
                {
                    hashTable[hash1] = (uint)(ip1 - @base);
                }

                goto Offset;
            }

            matchIndex = hashTable[hash1];
            hash0 = hash1;
            hash1 = THash.Hash(ip2, hashLog);
            ip0 = ip1;
            ip1 = ip2;
            ip2 = ip0 + step;
            ip3 = ip1 + step;

            if (ip2 >= nextStep)
            {
                step++;
                nextStep += StepIncrement;
            }
        }
        while (ip3 < ilimit);

    Cleanup:
        // A repeat offset invalid at the start of the block, and never replaced, is restored for the
        // next one; when the first was replaced, the second becomes the old first.
        offsetSaved2 = offsetSaved1 != 0 && repOffset1 != 0 ? offsetSaved1 : offsetSaved2;
        rep[0] = repOffset1 != 0 ? repOffset1 : offsetSaved1;
        rep[1] = repOffset2 != 0 ? repOffset2 : offsetSaved2;
        return (nuint)(iend - anchor);

    Offset:
        match0 = @base + matchIndex;
        repOffset2 = repOffset1;
        repOffset1 = (uint)(ip0 - match0);
        offBase = OffsetToOffBase(repOffset1);
        matchLength = 4;

        // The match extended backward.
        while (((ip0 > anchor) & (match0 > prefixStart)) && ip0[-1] == match0[-1])
        {
            ip0--;
            match0--;
            matchLength++;
        }

    Match:
        matchLength += Count(ip0 + matchLength, match0 + matchLength, iend);
        store.Store((nuint)(ip0 - anchor), anchor, iend, offBase, matchLength);
        ip0 += matchLength;
        anchor = ip0;

        if (ip0 <= ilimit)
        {
            // Two positions of the match into the table, and the second repeat offset tried at once.
            hashTable[THash.Hash(@base + current0 + 2, hashLog)] = current0 + 2;
            hashTable[THash.Hash(ip0 - 2, hashLog)] = (uint)(ip0 - 2 - @base);

            if (repOffset2 > 0)
            {
                while (ip0 <= ilimit && Read32(ip0) == Read32(ip0 - repOffset2))
                {
                    nuint repLength = Count(ip0 + 4, ip0 + 4 - repOffset2, iend) + 4;
                    (repOffset1, repOffset2) = (repOffset2, repOffset1);
                    hashTable[THash.Hash(ip0, hashLog)] = (uint)(ip0 - @base);
                    ip0 += repLength;
                    store.Store(0, anchor, iend, RepeatCode1, repLength);
                    anchor = ip0;
                }
            }
        }

        goto Start;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_match4Found_cmov</c>: whether the candidate at <paramref name="matchIndex"/>,
    /// within the window, starts with the same four bytes. A candidate below the window reads from
    /// <paramref name="dummy"/> instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchFound(byte* current, byte* @base, uint matchIndex, uint lowLimit, byte* dummy)
    {
        byte* address = matchIndex >= lowLimit ? @base + matchIndex : dummy;
        return (Read32(current) == Read32(address)) & (matchIndex >= lowLimit);
    }
}
