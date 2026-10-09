using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's double-fast match finder with a dictionary: attached, or a segment of its own.</summary>
internal static unsafe partial class DoubleFastMatchFinder
{
    /// <summary>
    /// <see cref="MatchFinder.Count2Segments"/> out of line: inlined at each of the double-fast searches'
    /// matches, it made them 3 to 10% slower end to end (their time is in the probes, not the counts).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nuint CountAcross(byte* input, byte* match, byte* inputEnd, byte* matchEnd, byte* prefixStart) =>
        Count2Segments(input, match, inputEnd, matchEnd, prefixStart);

    /// <summary>
    /// libzstd's <c>ZSTD_fillDoubleHashTableForCDict</c> (<c>ZSTD_dtlm_full</c>): every third position
    /// into both tables, the two after it into the long one where its entry is empty. Each index is
    /// tagged with the 8 bits of its hash below the table's (libzstd's short cache), which the attached
    /// search compares before it reads the content.
    /// </summary>
    public static void FillTaggedHashTables(ref MatchState state, byte* end)
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
            int hashLogLarge = state.Parameters.HashLog + TagBits;
            uint* hashSmall = state.ChainTable;
            int hashLogSmall = state.Parameters.ChainLog + TagBits;
            byte* @base = state.Base;
            byte* ip = @base + state.NextToUpdate;
            byte* iend = end - HashReadSize;
            const int FillStep = 3;
            for (; ip + FillStep - 1 <= iend; ip += FillStep)
            {
                uint current = (uint)(ip - @base);
                for (int i = 0; i < FillStep; i++)
                {
                    ulong bytes = Read64(ip + i);
                    if (i == 0)
                    {
                        WriteTaggedIndex(hashSmall, THash.Hash(bytes, hashLogSmall), current);
                    }

                    nuint large = Hash8.Hash(bytes, hashLogLarge);
                    if (i == 0 || hashLarge[large >> TagBits] == 0)
                    {
                        WriteTaggedIndex(hashLarge, large, current + (uint)i);
                    }
                }
            }
        }
    }

    /// <summary>The block's sequences, a prepared dictionary attached, by minimum match length.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlockAttached(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size) =>
        state.Parameters.MinMatch switch
        {
            5 => CompressBlockAttached<Hash5>(ref state, store, rep, source, size),
            6 => CompressBlockAttached<Hash6>(ref state, store, rep, source, size),
            7 => CompressBlockAttached<Hash7>(ref state, store, rep, source, size),
            _ => CompressBlockAttached<Hash4>(ref state, store, rep, source, size),
        };

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_doubleFast_dictMatchState_generic</c>: the frame's tables first,
    /// the dictionary's (its own parameters, tagged indices) where they have nothing in the prefix;
    /// the dictionary's indices sit just below the prefix's. On x64, <see cref="CompressBlockAttachedX64{THash}"/>.
    /// </summary>
    private static nuint CompressBlockAttached<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        if (X86Base.IsSupported)
        {
            return CompressBlockAttachedX64<THash>(ref state, store, rep, source, size);
        }

        CompressionParameters parameters = state.Parameters;
        uint* hashLong = state.HashTable;
        int hashLogLong = parameters.HashLog;
        uint* hashSmall = state.ChainTable;
        int hashLogSmall = parameters.ChainLog;
        byte* @base = state.Base;
        byte* istart = source;
        byte* ip = istart;
        byte* anchor = istart;
        uint endIndex = (uint)(istart - @base) + (uint)size;
        uint prefixLowestIndex = state.LowestPrefixIndex(endIndex);
        byte* prefixLowest = @base + prefixLowestIndex;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;
        uint offset1 = rep[0];
        uint offset2 = rep[1];

        MatchState* dictionary = state.Dictionary;
        uint* dictHashLong = dictionary->HashTable;
        uint* dictHashSmall = dictionary->ChainTable;
        uint dictStartIndex = dictionary->DictLimit;
        byte* dictBase = dictionary->Base;
        byte* dictStart = dictBase + dictStartIndex;
        byte* dictEnd = dictBase + dictionary->End;
        uint dictIndexDelta = prefixLowestIndex - dictionary->End;
        int dictHashLogLong = dictionary->Parameters.HashLog + TagBits;
        int dictHashLogSmall = dictionary->Parameters.ChainLog + TagBits;
        uint dictAndPrefixLength = (uint)((ip - prefixLowest) + (dictEnd - dictStart));

        ip += dictAndPrefixLength == 0 ? 1 : 0;

        while (ip < ilimit)
        {
            nuint matchLength;
            uint offset;
            ulong bytes = Read64(ip);
            nuint h2 = Hash8.Hash(bytes, hashLogLong);
            nuint h = THash.Hash(bytes, hashLogSmall);
            nuint dictHashAndTagLong = Hash8.Hash(bytes, dictHashLogLong);
            nuint dictHashAndTagSmall = THash.Hash(bytes, dictHashLogSmall);
            uint dictMatchIndexAndTagLong = dictHashLong[dictHashAndTagLong >> TagBits];
            uint dictMatchIndexAndTagSmall = dictHashSmall[dictHashAndTagSmall >> TagBits];
            bool dictTagsMatchLong = (dictMatchIndexAndTagLong & TagMask) == ((uint)dictHashAndTagLong & TagMask);
            bool dictTagsMatchSmall = (dictMatchIndexAndTagSmall & TagMask) == ((uint)dictHashAndTagSmall & TagMask);
            uint current = (uint)(ip - @base);
            uint matchIndexLong = hashLong[h2];
            uint matchIndexSmall = hashSmall[h];
            byte* matchLong = @base + matchIndexLong;
            byte* match = @base + matchIndexSmall;
            uint repIndex = current + 1 - offset1;
            byte* repMatch = repIndex < prefixLowestIndex ? dictBase + (repIndex - dictIndexDelta) : @base + repIndex;
            hashLong[h2] = hashSmall[h] = current;

            // The first repeat offset at ip + 1.
            if (IndexOverlapCheck(prefixLowestIndex, repIndex) && Read32(repMatch) == Read32(ip + 1))
            {
                byte* repMatchEnd = repIndex < prefixLowestIndex ? dictEnd : iend;
                matchLength = CountAcross(ip + 1 + 4, repMatch + 4, iend, repMatchEnd, prefixLowest) + 4;
                ip++;
                store.Store((nuint)(ip - anchor), anchor, iend, RepeatCode1, matchLength);
                goto MatchStored;
            }

            if (matchIndexLong >= prefixLowestIndex && Read64(matchLong) == bytes)
            {
                // A long match in the prefix.
                matchLength = Count(ip + 8, matchLong + 8, iend) + 8;
                offset = (uint)(ip - matchLong);
                while (((ip > anchor) & (matchLong > prefixLowest)) && ip[-1] == matchLong[-1])
                {
                    ip--;
                    matchLong--;
                    matchLength++;
                }

                goto MatchFound;
            }

            if (dictTagsMatchLong)
            {
                // A long match in the dictionary.
                uint dictMatchIndexLong = dictMatchIndexAndTagLong >> TagBits;
                byte* dictMatchLong = dictBase + dictMatchIndexLong;
                if (dictMatchLong > dictStart && Read64(dictMatchLong) == bytes)
                {
                    matchLength = CountAcross(ip + 8, dictMatchLong + 8, iend, dictEnd, prefixLowest) + 8;
                    offset = current - dictMatchIndexLong - dictIndexDelta;
                    while (((ip > anchor) & (dictMatchLong > dictStart)) && ip[-1] == dictMatchLong[-1])
                    {
                        ip--;
                        dictMatchLong--;
                        matchLength++;
                    }

                    goto MatchFound;
                }
            }

            if (matchIndexSmall > prefixLowestIndex)
            {
                // A short match in the prefix.
                if (Read32(match) == (uint)bytes)
                {
                    goto SearchNextLong;
                }
            }
            else if (dictTagsMatchSmall)
            {
                // A short match in the dictionary.
                uint dictMatchIndexSmall = dictMatchIndexAndTagSmall >> TagBits;
                match = dictBase + dictMatchIndexSmall;
                matchIndexSmall = dictMatchIndexSmall + dictIndexDelta;
                if (match > dictStart && Read32(match) == (uint)bytes)
                {
                    goto SearchNextLong;
                }
            }

            ip += ((nuint)(ip - anchor) >> SearchStrength) + 1;
            continue;

        SearchNextLong:
            {
                ulong nextBytes = Read64(ip + 1);
                nuint hl3 = Hash8.Hash(nextBytes, hashLogLong);
                nuint dictHashAndTagLong3 = Hash8.Hash(nextBytes, dictHashLogLong);
                uint matchIndexLong3 = hashLong[hl3];
                uint dictMatchIndexAndTagLong3 = dictHashLong[dictHashAndTagLong3 >> TagBits];
                bool dictTagsMatchLong3 = (dictMatchIndexAndTagLong3 & TagMask) == ((uint)dictHashAndTagLong3 & TagMask);
                byte* matchLong3 = @base + matchIndexLong3;
                hashLong[hl3] = current + 1;

                if (matchIndexLong3 >= prefixLowestIndex && Read64(matchLong3) == nextBytes)
                {
                    // A long match at ip + 1 in the prefix.
                    matchLength = Count(ip + 9, matchLong3 + 8, iend) + 8;
                    ip++;
                    offset = (uint)(ip - matchLong3);
                    while (((ip > anchor) & (matchLong3 > prefixLowest)) && ip[-1] == matchLong3[-1])
                    {
                        ip--;
                        matchLong3--;
                        matchLength++;
                    }

                    goto MatchFound;
                }

                if (dictTagsMatchLong3)
                {
                    // A long match at ip + 1 in the dictionary.
                    uint dictMatchIndexLong3 = dictMatchIndexAndTagLong3 >> TagBits;
                    byte* dictMatchLong3 = dictBase + dictMatchIndexLong3;
                    if (dictMatchLong3 > dictStart && Read64(dictMatchLong3) == nextBytes)
                    {
                        matchLength = CountAcross(ip + 1 + 8, dictMatchLong3 + 8, iend, dictEnd, prefixLowest) + 8;
                        ip++;
                        offset = current + 1 - dictMatchIndexLong3 - dictIndexDelta;
                        while (((ip > anchor) & (dictMatchLong3 > dictStart)) && ip[-1] == dictMatchLong3[-1])
                        {
                            ip--;
                            dictMatchLong3--;
                            matchLength++;
                        }

                        goto MatchFound;
                    }
                }
            }

            // No long match at ip + 1: the short match found.
            if (matchIndexSmall < prefixLowestIndex)
            {
                matchLength = CountAcross(ip + 4, match + 4, iend, dictEnd, prefixLowest) + 4;
                offset = current - matchIndexSmall;
                while (((ip > anchor) & (match > dictStart)) && ip[-1] == match[-1])
                {
                    ip--;
                    match--;
                    matchLength++;
                }
            }
            else
            {
                matchLength = Count(ip + 4, match + 4, iend) + 4;
                offset = (uint)(ip - match);
                while (((ip > anchor) & (match > prefixLowest)) && ip[-1] == match[-1])
                {
                    ip--;
                    match--;
                    matchLength++;
                }
            }

        MatchFound:
            offset2 = offset1;
            offset1 = offset;
            store.Store((nuint)(ip - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);

        MatchStored:
            ip += matchLength;
            anchor = ip;

            if (ip <= ilimit)
            {
                // Complementary insertion, after the limit test: the positions could pass the end - 8.
                uint indexToInsert = current + 2;
                hashLong[Hash8.Hash(Read64(@base + indexToInsert), hashLogLong)] = indexToInsert;
                hashLong[Hash8.Hash(Read64(ip - 2), hashLogLong)] = (uint)(ip - 2 - @base);
                hashSmall[THash.Hash(Read64(@base + indexToInsert), hashLogSmall)] = indexToInsert;
                hashSmall[THash.Hash(Read64(ip - 1), hashLogSmall)] = (uint)(ip - 1 - @base);

                // The second repeat offset, at once.
                while (ip <= ilimit)
                {
                    uint current2 = (uint)(ip - @base);
                    uint repIndex2 = current2 - offset2;
                    byte* repMatch2 = repIndex2 < prefixLowestIndex ? dictBase + repIndex2 - dictIndexDelta : @base + repIndex2;
                    if (IndexOverlapCheck(prefixLowestIndex, repIndex2) && Read32(repMatch2) == Read32(ip))
                    {
                        byte* repEnd2 = repIndex2 < prefixLowestIndex ? dictEnd : iend;
                        nuint repLength2 = CountAcross(ip + 4, repMatch2 + 4, iend, repEnd2, prefixLowest) + 4;
                        (offset1, offset2) = (offset2, offset1);
                        store.Store(0, anchor, iend, RepeatCode1, repLength2);
                        ulong repBytes = Read64(ip);
                        hashSmall[THash.Hash(repBytes, hashLogSmall)] = current2;
                        hashLong[Hash8.Hash(repBytes, hashLogLong)] = current2;
                        ip += repLength2;
                        anchor = ip;
                        continue;
                    }

                    break;
                }
            }
        }

        rep[0] = offset1;
        rep[1] = offset2;
        return (nuint)(iend - anchor);
    }

    /// <summary>The block's sequences, with an extDict below the prefix, by minimum match length.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlockExtDict(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size) =>
        state.Parameters.MinMatch switch
        {
            5 => CompressBlockExtDict<Hash5>(ref state, store, rep, source, size),
            6 => CompressBlockExtDict<Hash6>(ref state, store, rep, source, size),
            7 => CompressBlockExtDict<Hash7>(ref state, store, rep, source, size),
            _ => CompressBlockExtDict<Hash4>(ref state, store, rep, source, size),
        };

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_doubleFast_extDict_generic</c>: one pair of tables over both
    /// segments, an index below the prefix's start read from the extDict's base.
    /// </summary>
    private static nuint CompressBlockExtDict<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        CompressionParameters parameters = state.Parameters;
        uint* hashLong = state.HashTable;
        int hashLogLong = parameters.HashLog;
        uint* hashSmall = state.ChainTable;
        int hashLogSmall = parameters.ChainLog;
        byte* istart = source;
        byte* ip = istart;
        byte* anchor = istart;
        byte* iend = istart + size;
        byte* ilimit = iend - 8;
        byte* @base = state.Base;
        uint endIndex = (uint)(istart - @base) + (uint)size;
        uint lowLimit = state.LowestMatchIndex(endIndex);
        uint dictStartIndex = lowLimit;
        uint dictLimit = state.DictLimit;
        uint prefixStartIndex = dictLimit > lowLimit ? dictLimit : lowLimit;
        byte* prefixStart = @base + prefixStartIndex;
        byte* dictBase = state.DictionaryBase;
        byte* dictStart = dictBase + dictStartIndex;
        byte* dictEnd = dictBase + prefixStartIndex;
        uint offset1 = rep[0];
        uint offset2 = rep[1];

        // The extDict out of the window: the search without one.
        if (prefixStartIndex == dictStartIndex)
        {
            return CompressBlock(ref state, store, rep, source, size);
        }

        while (ip < ilimit)
        {
            ulong bytes = Read64(ip);
            nuint hashSmallIndex = THash.Hash(bytes, hashLogSmall);
            uint matchIndex = hashSmall[hashSmallIndex];
            byte* match = (matchIndex < prefixStartIndex ? dictBase : @base) + matchIndex;

            nuint hashLongIndex = Hash8.Hash(bytes, hashLogLong);
            uint matchLongIndex = hashLong[hashLongIndex];
            byte* matchLong = (matchLongIndex < prefixStartIndex ? dictBase : @base) + matchLongIndex;

            uint current = (uint)(ip - @base);
            uint repIndex = current + 1 - offset1;
            byte* repMatch = (repIndex < prefixStartIndex ? dictBase : @base) + repIndex;
            nuint matchLength;
            hashSmall[hashSmallIndex] = hashLong[hashLongIndex] = current;

            if ((IndexOverlapCheck(prefixStartIndex, repIndex) & (offset1 <= current + 1 - dictStartIndex))
                && Read32(repMatch) == Read32(ip + 1))
            {
                byte* repMatchEnd = repIndex < prefixStartIndex ? dictEnd : iend;
                matchLength = CountAcross(ip + 1 + 4, repMatch + 4, iend, repMatchEnd, prefixStart) + 4;
                ip++;
                store.Store((nuint)(ip - anchor), anchor, iend, RepeatCode1, matchLength);
            }
            else if (matchLongIndex > dictStartIndex && Read64(matchLong) == bytes)
            {
                byte* matchEnd = matchLongIndex < prefixStartIndex ? dictEnd : iend;
                byte* lowMatchPointer = matchLongIndex < prefixStartIndex ? dictStart : prefixStart;
                matchLength = CountAcross(ip + 8, matchLong + 8, iend, matchEnd, prefixStart) + 8;
                uint offset = current - matchLongIndex;
                while (((ip > anchor) & (matchLong > lowMatchPointer)) && ip[-1] == matchLong[-1])
                {
                    ip--;
                    matchLong--;
                    matchLength++;
                }

                offset2 = offset1;
                offset1 = offset;
                store.Store((nuint)(ip - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);
            }
            else if (matchIndex > dictStartIndex && Read32(match) == (uint)bytes)
            {
                ulong nextBytes = Read64(ip + 1);
                nuint h3 = Hash8.Hash(nextBytes, hashLogLong);
                uint matchIndex3 = hashLong[h3];
                byte* match3 = (matchIndex3 < prefixStartIndex ? dictBase : @base) + matchIndex3;
                uint offset;
                hashLong[h3] = current + 1;
                if (matchIndex3 > dictStartIndex && Read64(match3) == nextBytes)
                {
                    byte* matchEnd = matchIndex3 < prefixStartIndex ? dictEnd : iend;
                    byte* lowMatchPointer = matchIndex3 < prefixStartIndex ? dictStart : prefixStart;
                    matchLength = CountAcross(ip + 9, match3 + 8, iend, matchEnd, prefixStart) + 8;
                    ip++;
                    offset = current + 1 - matchIndex3;
                    while (((ip > anchor) & (match3 > lowMatchPointer)) && ip[-1] == match3[-1])
                    {
                        ip--;
                        match3--;
                        matchLength++;
                    }
                }
                else
                {
                    byte* matchEnd = matchIndex < prefixStartIndex ? dictEnd : iend;
                    byte* lowMatchPointer = matchIndex < prefixStartIndex ? dictStart : prefixStart;
                    matchLength = CountAcross(ip + 4, match + 4, iend, matchEnd, prefixStart) + 4;
                    offset = current - matchIndex;
                    while (((ip > anchor) & (match > lowMatchPointer)) && ip[-1] == match[-1])
                    {
                        ip--;
                        match--;
                        matchLength++;
                    }
                }

                offset2 = offset1;
                offset1 = offset;
                store.Store((nuint)(ip - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);
            }
            else
            {
                ip += ((nuint)(ip - anchor) >> SearchStrength) + 1;
                continue;
            }

            ip += matchLength;
            anchor = ip;

            if (ip <= ilimit)
            {
                // Complementary insertion, after the limit test: the positions could pass the end - 8.
                uint indexToInsert = current + 2;
                hashLong[Hash8.Hash(Read64(@base + indexToInsert), hashLogLong)] = indexToInsert;
                hashLong[Hash8.Hash(Read64(ip - 2), hashLogLong)] = (uint)(ip - 2 - @base);
                hashSmall[THash.Hash(Read64(@base + indexToInsert), hashLogSmall)] = indexToInsert;
                hashSmall[THash.Hash(Read64(ip - 1), hashLogSmall)] = (uint)(ip - 1 - @base);

                // The second repeat offset, at once.
                while (ip <= ilimit)
                {
                    uint current2 = (uint)(ip - @base);
                    uint repIndex2 = current2 - offset2;
                    byte* repMatch2 = (repIndex2 < prefixStartIndex ? dictBase : @base) + repIndex2;
                    if ((IndexOverlapCheck(prefixStartIndex, repIndex2) & (offset2 <= current2 - dictStartIndex))
                        && Read32(repMatch2) == Read32(ip))
                    {
                        byte* repEnd2 = repIndex2 < prefixStartIndex ? dictEnd : iend;
                        nuint repLength2 = CountAcross(ip + 4, repMatch2 + 4, iend, repEnd2, prefixStart) + 4;
                        (offset1, offset2) = (offset2, offset1);
                        store.Store(0, anchor, iend, RepeatCode1, repLength2);
                        ulong repBytes = Read64(ip);
                        hashSmall[THash.Hash(repBytes, hashLogSmall)] = current2;
                        hashLong[Hash8.Hash(repBytes, hashLogLong)] = current2;
                        ip += repLength2;
                        anchor = ip;
                        continue;
                    }

                    break;
                }
            }
        }

        rep[0] = offset1;
        rep[1] = offset2;
        return (nuint)(iend - anchor);
    }
}
