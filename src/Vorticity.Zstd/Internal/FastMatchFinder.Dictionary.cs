using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's fast match finder with a dictionary: attached, or a segment of its own.</summary>
internal static unsafe partial class FastMatchFinder
{
    /// <summary>
    /// libzstd's <c>ZSTD_fillHashTableForCDict</c> (<c>ZSTD_dtlm_full</c>): every third position, and
    /// the two after it where their entries are empty, each index tagged with the 8 bits of its hash
    /// below the table's (libzstd's short cache).
    /// </summary>
    public static void FillTaggedHashTable(ref MatchState state, byte* end)
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
            int hashLog = state.Parameters.HashLog + TagBits;
            byte* @base = state.Base;
            byte* ip = @base + state.NextToUpdate;
            byte* iend = end - HashReadSize;
            const int FillStep = 3;
            for (; ip + FillStep < iend + 2; ip += FillStep)
            {
                uint current = (uint)(ip - @base);
                WriteTaggedIndex(hashTable, THash.Hash(Read64(ip), hashLog), current);
                for (int p = 1; p < FillStep; p++)
                {
                    nuint hashAndTag = THash.Hash(Read64(ip + p), hashLog);
                    if (hashTable[hashAndTag >> TagBits] == 0)
                    {
                        WriteTaggedIndex(hashTable, hashAndTag, current + (uint)p);
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
    /// libzstd's <c>ZSTD_compressBlock_fast_dictMatchState_generic</c>: the frame's table and the
    /// dictionary's (tagged indices); a dictionary match taken only where the frame's table has
    /// nothing in the prefix, as an extDict search would.
    /// </summary>
    private static nuint CompressBlockAttached<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        const nuint StepIncrement = 1 << SearchStrength;
        CompressionParameters parameters = state.Parameters;
        uint* hashTable = state.HashTable;
        int hashLog = parameters.HashLog;
        nuint stepSize = (nuint)parameters.TargetLength + (parameters.TargetLength == 0 ? 1u : 0u);
        byte* @base = state.Base;
        byte* istart = source;
        byte* ip0 = istart;
        byte* ip1 = ip0 + stepSize;
        byte* anchor = istart;
        uint prefixStartIndex = state.DictLimit;
        byte* prefixStart = @base + prefixStartIndex;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;
        uint offset1 = rep[0];
        uint offset2 = rep[1];

        MatchState* dictionary = state.Dictionary;
        uint* dictHashTable = dictionary->HashTable;
        uint dictStartIndex = dictionary->DictLimit;
        byte* dictBase = dictionary->Base;
        byte* dictStart = dictBase + dictStartIndex;
        byte* dictEnd = dictBase + dictionary->End;
        uint dictIndexDelta = prefixStartIndex - dictionary->End;
        uint dictAndPrefixLength = (uint)((istart - prefixStart) + (dictEnd - dictStart));
        int dictHashLog = dictionary->Parameters.HashLog + TagBits;

        ip0 += dictAndPrefixLength == 0 ? 1 : 0;

        while (ip1 <= ilimit)
        {
            nuint matchLength;
            ulong bytes0 = Read64(ip0);
            nuint hash0 = THash.Hash(bytes0, hashLog);
            nuint dictHashAndTag0 = THash.Hash(bytes0, dictHashLog);
            uint dictMatchIndexAndTag = dictHashTable[dictHashAndTag0 >> TagBits];
            bool dictTagsMatch = (dictMatchIndexAndTag & TagMask) == ((uint)dictHashAndTag0 & TagMask);
            uint matchIndex = hashTable[hash0];
            uint current = (uint)(ip0 - @base);
            nuint step = stepSize;
            byte* nextStep = ip0 + StepIncrement;

            while (true)
            {
                byte* match = @base + matchIndex;
                uint repIndex = current + 1 - offset1;
                byte* repMatch = repIndex < prefixStartIndex ? dictBase + (repIndex - dictIndexDelta) : @base + repIndex;
                ulong bytes1 = Read64(ip1);
                nuint hash1 = THash.Hash(bytes1, hashLog);
                nuint dictHashAndTag1 = THash.Hash(bytes1, dictHashLog);
                hashTable[hash0] = current;

                if (IndexOverlapCheck(prefixStartIndex, repIndex) && Read32(repMatch) == Read32(ip0 + 1))
                {
                    byte* repMatchEnd = repIndex < prefixStartIndex ? dictEnd : iend;
                    matchLength = Count2Segments(ip0 + 1 + 4, repMatch + 4, iend, repMatchEnd, prefixStart) + 4;
                    ip0++;
                    store.Store((nuint)(ip0 - anchor), anchor, iend, RepeatCode1, matchLength);
                    break;
                }

                if (dictTagsMatch)
                {
                    uint dictMatchIndex = dictMatchIndexAndTag >> TagBits;
                    byte* dictMatch = dictBase + dictMatchIndex;
                    if (dictMatchIndex > dictStartIndex && Read32(dictMatch) == Read32(ip0) && matchIndex <= prefixStartIndex)
                    {
                        uint offset = current - dictMatchIndex - dictIndexDelta;
                        matchLength = Count2Segments(ip0 + 4, dictMatch + 4, iend, dictEnd, prefixStart) + 4;
                        while (((ip0 > anchor) & (dictMatch > dictStart)) && ip0[-1] == dictMatch[-1])
                        {
                            ip0--;
                            dictMatch--;
                            matchLength++;
                        }

                        offset2 = offset1;
                        offset1 = offset;
                        store.Store((nuint)(ip0 - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);
                        break;
                    }
                }

                if (matchIndex >= prefixStartIndex && Read32(ip0) == Read32(match))
                {
                    uint offset = (uint)(ip0 - match);
                    matchLength = Count(ip0 + 4, match + 4, iend) + 4;
                    while (((ip0 > anchor) & (match > prefixStart)) && ip0[-1] == match[-1])
                    {
                        ip0--;
                        match--;
                        matchLength++;
                    }

                    offset2 = offset1;
                    offset1 = offset;
                    store.Store((nuint)(ip0 - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);
                    break;
                }

                // The next position.
                dictMatchIndexAndTag = dictHashTable[dictHashAndTag1 >> TagBits];
                dictTagsMatch = (dictMatchIndexAndTag & TagMask) == ((uint)dictHashAndTag1 & TagMask);
                matchIndex = hashTable[hash1];
                if (ip1 >= nextStep)
                {
                    step++;
                    nextStep += StepIncrement;
                }

                ip0 = ip1;
                ip1 += step;
                if (ip1 > ilimit)
                {
                    goto Cleanup;
                }

                current = (uint)(ip0 - @base);
                hash0 = hash1;
            }

            ip0 += matchLength;
            anchor = ip0;
            if (ip0 <= ilimit)
            {
                // Complementary insertion: current + 2 could be past the end - 8.
                hashTable[THash.Hash(Read64(@base + current + 2), hashLog)] = current + 2;
                hashTable[THash.Hash(Read64(ip0 - 2), hashLog)] = (uint)(ip0 - 2 - @base);

                // The second repeat offset, at once.
                while (ip0 <= ilimit)
                {
                    uint current2 = (uint)(ip0 - @base);
                    uint repIndex2 = current2 - offset2;
                    byte* repMatch2 = repIndex2 < prefixStartIndex ? dictBase - dictIndexDelta + repIndex2 : @base + repIndex2;
                    if (IndexOverlapCheck(prefixStartIndex, repIndex2) && Read32(repMatch2) == Read32(ip0))
                    {
                        byte* repEnd2 = repIndex2 < prefixStartIndex ? dictEnd : iend;
                        nuint repLength2 = Count2Segments(ip0 + 4, repMatch2 + 4, iend, repEnd2, prefixStart) + 4;
                        (offset1, offset2) = (offset2, offset1);
                        store.Store(0, anchor, iend, RepeatCode1, repLength2);
                        hashTable[THash.Hash(Read64(ip0), hashLog)] = current2;
                        ip0 += repLength2;
                        anchor = ip0;
                        continue;
                    }

                    break;
                }
            }

            ip1 = ip0 + stepSize;
        }

    Cleanup:
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
    /// libzstd's <c>ZSTD_compressBlock_fast_extDict_generic</c>: the search of four positions at a
    /// time, the repeat offset tried two ahead, one table over both segments.
    /// </summary>
    private static nuint CompressBlockExtDict<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        const nuint StepIncrement = 1 << (SearchStrength - 1);
        CompressionParameters parameters = state.Parameters;
        uint* hashTable = state.HashTable;
        int hashLog = parameters.HashLog;
        nuint stepSize = (nuint)parameters.TargetLength + (parameters.TargetLength == 0 ? 1u : 0u) + 1;
        byte* @base = state.Base;
        byte* dictBase = state.DictionaryBase;
        byte* istart = source;
        byte* anchor = istart;
        uint endIndex = (uint)(istart - @base) + (uint)size;
        uint lowLimit = state.LowestMatchIndex(endIndex);
        uint dictStartIndex = lowLimit;
        byte* dictStart = dictBase + dictStartIndex;
        uint dictLimit = state.DictLimit;
        uint prefixStartIndex = dictLimit < lowLimit ? lowLimit : dictLimit;
        byte* prefixStart = @base + prefixStartIndex;
        byte* dictEnd = dictBase + prefixStartIndex;
        byte* iend = istart + size;
        byte* ilimit = iend - 8;
        uint offset1 = rep[0];
        uint offset2 = rep[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        byte* ip0 = istart;
        byte* ip1;
        byte* ip2;
        byte* ip3;
        uint current0;
        nuint hash0;
        nuint hash1;
        uint index;
        byte* indexBase;
        uint offBase;
        byte* match0;
        nuint matchLength;
        byte* matchEnd;
        nuint step;
        byte* nextStep;

        // The extDict out of the window: the search without one.
        if (prefixStartIndex == dictStartIndex)
        {
            return CompressBlock(ref state, store, rep, source, size);
        }

        {
            uint current = (uint)(ip0 - @base);
            uint maxRep = current - dictStartIndex;
            if (offset2 >= maxRep)
            {
                offsetSaved2 = offset2;
                offset2 = 0;
            }

            if (offset1 >= maxRep)
            {
                offsetSaved1 = offset1;
                offset1 = 0;
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

        hash0 = THash.Hash(Read64(ip0), hashLog);
        hash1 = THash.Hash(Read64(ip1), hashLog);
        index = hashTable[hash0];
        indexBase = index < prefixStartIndex ? dictBase : @base;

        do
        {
            {
                // The repeat offset at ip2.
                uint current2 = (uint)(ip2 - @base);
                uint repIndex = current2 - offset1;
                byte* repBase = repIndex < prefixStartIndex ? dictBase : @base;
                uint repValue = ((prefixStartIndex - repIndex >= 4) & (offset1 > 0)) ? Read32(repBase + repIndex) : Read32(ip2) ^ 1;
                current0 = (uint)(ip0 - @base);
                hashTable[hash0] = current0;
                if (Read32(ip2) == repValue)
                {
                    ip0 = ip2;
                    match0 = repBase + repIndex;
                    matchEnd = repIndex < prefixStartIndex ? dictEnd : iend;
                    matchLength = ip0[-1] == match0[-1] ? 1u : 0u;
                    ip0 -= matchLength;
                    match0 -= matchLength;
                    offBase = RepeatCode1;
                    matchLength += 4;
                    goto Match;
                }
            }

            if (Read32(ip0) == (index >= dictStartIndex ? Read32(indexBase + index) : Read32(ip0) ^ 1))
            {
                goto Offset;
            }

            index = hashTable[hash1];
            indexBase = index < prefixStartIndex ? dictBase : @base;
            hash0 = hash1;
            hash1 = THash.Hash(Read64(ip2), hashLog);
            ip0 = ip1;
            ip1 = ip2;
            ip2 = ip3;
            current0 = (uint)(ip0 - @base);
            hashTable[hash0] = current0;

            if (Read32(ip0) == (index >= dictStartIndex ? Read32(indexBase + index) : Read32(ip0) ^ 1))
            {
                goto Offset;
            }

            index = hashTable[hash1];
            indexBase = index < prefixStartIndex ? dictBase : @base;
            hash0 = hash1;
            hash1 = THash.Hash(Read64(ip2), hashLog);
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
        // An offset invalid at the start that became valid rotates the saved ones.
        offsetSaved2 = offsetSaved1 != 0 && offset1 != 0 ? offsetSaved1 : offsetSaved2;
        rep[0] = offset1 != 0 ? offset1 : offsetSaved1;
        rep[1] = offset2 != 0 ? offset2 : offsetSaved2;
        return (nuint)(iend - anchor);

    Offset:
        {
            uint offset = current0 - index;
            byte* lowMatchPointer = index < prefixStartIndex ? dictStart : prefixStart;
            matchEnd = index < prefixStartIndex ? dictEnd : iend;
            match0 = indexBase + index;
            offset2 = offset1;
            offset1 = offset;
            offBase = OffsetToOffBase(offset);
            matchLength = 4;
            while (((ip0 > anchor) & (match0 > lowMatchPointer)) && ip0[-1] == match0[-1])
            {
                ip0--;
                match0--;
                matchLength++;
            }
        }

    Match:
        matchLength += Count2Segments(ip0 + matchLength, match0 + matchLength, iend, matchEnd, prefixStart);
        store.Store((nuint)(ip0 - anchor), anchor, iend, offBase, matchLength);
        ip0 += matchLength;
        anchor = ip0;
        if (ip1 < ip0)
        {
            hashTable[hash1] = (uint)(ip1 - @base);
        }

        if (ip0 <= ilimit)
        {
            // Complementary insertion: current0 + 2 could be past the end - 8.
            hashTable[THash.Hash(Read64(@base + current0 + 2), hashLog)] = current0 + 2;
            hashTable[THash.Hash(Read64(ip0 - 2), hashLog)] = (uint)(ip0 - 2 - @base);

            // The second repeat offset, at once.
            while (ip0 <= ilimit)
            {
                uint repIndex2 = (uint)(ip0 - @base) - offset2;
                byte* repMatch2 = (repIndex2 < prefixStartIndex ? dictBase : @base) + repIndex2;
                if ((IndexOverlapCheck(prefixStartIndex, repIndex2) & (offset2 > 0)) && Read32(repMatch2) == Read32(ip0))
                {
                    byte* repEnd2 = repIndex2 < prefixStartIndex ? dictEnd : iend;
                    nuint repLength2 = Count2Segments(ip0 + 4, repMatch2 + 4, iend, repEnd2, prefixStart) + 4;
                    (offset1, offset2) = (offset2, offset1);
                    store.Store(0, anchor, iend, RepeatCode1, repLength2);
                    hashTable[THash.Hash(Read64(ip0), hashLog)] = (uint)(ip0 - @base);
                    ip0 += repLength2;
                    anchor = ip0;
                    continue;
                }

                break;
            }
        }

        goto Start;
    }
}
