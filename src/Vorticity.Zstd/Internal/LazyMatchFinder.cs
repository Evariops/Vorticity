using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's lazy parser without a dictionary (<c>ZSTD_compressBlock_lazy_generic</c>): at each
/// position the best match a search finds, kept (greedy) or weighed against those of the next one
/// (lazy) or two (lazy2, btlazy2) positions. The search is a hash chain (<c>ZSTD_HcFindBestMatch</c>),
/// the row-based match finder (<c>ZSTD_RowFindBestMatch</c>), which libzstd uses on arm64 and x64 once
/// the window passes 16 KiB, or the binary tree of btlazy2 (<c>ZSTD_BtFindBestMatch</c>).
/// </summary>
internal static unsafe class LazyMatchFinder
{
    /// <summary>libzstd's <c>kLazySkippingStep</c>: a step past which positions are no longer all inserted.</summary>
    private const nuint LazySkippingStep = 8;

    /// <summary>libzstd's <c>ZSTD_ROW_HASH_CACHE_SIZE</c>: hashes computed that many positions ahead.</summary>
    public const int RowHashCacheSize = 8;

    /// <summary>libzstd's <c>ZSTD_ROW_HASH_TAG_BITS</c>: the low bits of a row hash, kept as a tag.</summary>
    public const int RowHashTagBits = 8;

    /// <summary>The block's sequences into <paramref name="store"/>, for the strategy and search of the parameters.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlock(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size) =>
        Dispatch<NoDictionary>(ref state, store, rep, source, size);

    /// <summary>
    /// libzstd's loading of a dictionary into the lazy strategies' tables: every position up to
    /// <paramref name="ip"/>, into the rows (<c>ZSTD_row_update</c>, the tags cleared first) or the
    /// hash chains (<c>ZSTD_insertAndFindFirstIndex</c>).
    /// </summary>
    public static void FillDictionaryTables(ref MatchState state, byte* ip)
    {
        CompressionParameters parameters = state.Parameters;
        int minMatch = Math.Clamp(parameters.MinMatch, 4, 6);
        if (!parameters.UsesRowMatchFinder)
        {
            switch (parameters.MinMatch)
            {
                case 5: HashChainSearch<Hash5, NoDictionary>.Fill(ref state, ip); break;
                case 6: HashChainSearch<Hash6, NoDictionary>.Fill(ref state, ip); break;
                default: HashChainSearch<Hash4, NoDictionary>.Fill(ref state, ip); break;
            }

            return;
        }

        new Span<byte>(state.TagTable, 1 << parameters.HashLog).Clear();
        switch (minMatch, Math.Clamp(parameters.SearchLog, 4, 6))
        {
            case (4, 4): RowSearch<Hash4, Row16, NoDictionary>.Fill(ref state, ip); break;
            case (4, 5): RowSearch<Hash4, Row32, NoDictionary>.Fill(ref state, ip); break;
            case (4, _): RowSearch<Hash4, Row64, NoDictionary>.Fill(ref state, ip); break;
            case (5, 4): RowSearch<Hash5, Row16, NoDictionary>.Fill(ref state, ip); break;
            case (5, 5): RowSearch<Hash5, Row32, NoDictionary>.Fill(ref state, ip); break;
            case (5, _): RowSearch<Hash5, Row64, NoDictionary>.Fill(ref state, ip); break;
            case (_, 4): RowSearch<Hash6, Row16, NoDictionary>.Fill(ref state, ip); break;
            case (_, 5): RowSearch<Hash6, Row32, NoDictionary>.Fill(ref state, ip); break;
            default: RowSearch<Hash6, Row64, NoDictionary>.Fill(ref state, ip); break;
        }
    }

    /// <summary>
    /// The block's sequences with a dictionary: below the prefix as a segment of its own
    /// (<paramref name="extDict"/>), or attached.
    /// </summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlockWithDictionary(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size, bool extDict) =>
        extDict
            ? Dispatch<ExtDictionary>(ref state, store, rep, source, size)
            : Dispatch<AttachedDictionary>(ref state, store, rep, source, size);

    /// <summary>
    /// libzstd's <c>ZSTD_selectBlockCompressor</c> for the lazy strategies: the search and its
    /// specializations, then the parser of the dictionary mode.
    /// </summary>
    private static nuint Dispatch<TDictionary>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where TDictionary : IDictionaryMode
    {
        int minMatch = Math.Clamp(state.Parameters.MinMatch, 4, 6);
        if (state.Parameters.Strategy == Strategy.BinaryTreeLazy2)
        {
            return minMatch switch
            {
                5 => ByDepth<BinaryTreeSearch<Hash5, TDictionary>>(ref state, store, rep, source, size, 2),
                6 => ByDepth<BinaryTreeSearch<Hash6, TDictionary>>(ref state, store, rep, source, size, 2),
                _ => ByDepth<BinaryTreeSearch<Hash4, TDictionary>>(ref state, store, rep, source, size, 2),
            };
        }

        int depth = state.Parameters.Strategy - Strategy.Greedy;
        if (!state.Parameters.UsesRowMatchFinder)
        {
            return minMatch switch
            {
                5 => ByDepth<HashChainSearch<Hash5, TDictionary>>(ref state, store, rep, source, size, depth),
                6 => ByDepth<HashChainSearch<Hash6, TDictionary>>(ref state, store, rep, source, size, depth),
                _ => ByDepth<HashChainSearch<Hash4, TDictionary>>(ref state, store, rep, source, size, depth),
            };
        }

        return (minMatch, Math.Clamp(state.Parameters.SearchLog, 4, 6)) switch
        {
            (4, 4) => ByDepth<RowSearch<Hash4, Row16, TDictionary>>(ref state, store, rep, source, size, depth),
            (4, 5) => ByDepth<RowSearch<Hash4, Row32, TDictionary>>(ref state, store, rep, source, size, depth),
            (4, _) => ByDepth<RowSearch<Hash4, Row64, TDictionary>>(ref state, store, rep, source, size, depth),
            (5, 4) => ByDepth<RowSearch<Hash5, Row16, TDictionary>>(ref state, store, rep, source, size, depth),
            (5, 5) => ByDepth<RowSearch<Hash5, Row32, TDictionary>>(ref state, store, rep, source, size, depth),
            (5, _) => ByDepth<RowSearch<Hash5, Row64, TDictionary>>(ref state, store, rep, source, size, depth),
            (_, 4) => ByDepth<RowSearch<Hash6, Row16, TDictionary>>(ref state, store, rep, source, size, depth),
            (_, 5) => ByDepth<RowSearch<Hash6, Row32, TDictionary>>(ref state, store, rep, source, size, depth),
            _ => ByDepth<RowSearch<Hash6, Row64, TDictionary>>(ref state, store, rep, source, size, depth),
        };
    }

    private static nuint ByDepth<TSearch>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size, int depth)
        where TSearch : struct, ILazySearch =>
        TSearch.DictionaryMode == ExtDictionary.Value
            ? depth switch
            {
                0 => CompressBlockExtDict<TSearch, Depth0>(ref state, store, rep, source, size),
                1 => CompressBlockExtDict<TSearch, Depth1>(ref state, store, rep, source, size),
                _ => CompressBlockExtDict<TSearch, Depth2>(ref state, store, rep, source, size),
            }
            : depth switch
            {
                0 => CompressBlock<TSearch, Depth0>(ref state, store, rep, source, size),
                1 => CompressBlock<TSearch, Depth1>(ref state, store, rep, source, size),
                _ => CompressBlock<TSearch, Depth2>(ref state, store, rep, source, size),
            };

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_lazy_extDict_generic</c>: the parser over an extDict below the
    /// prefix, whose repeat offsets reach into it within the window.
    /// </summary>
    private static nuint CompressBlockExtDict<TSearch, TDepth>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where TSearch : struct, ILazySearch
        where TDepth : struct, ILazyDepth
    {
        byte* istart = source;
        byte* ip = istart;
        byte* anchor = istart;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize - (TSearch.UsesRows ? RowHashCacheSize : 0);
        byte* @base = state.Base;
        uint dictLimit = state.DictLimit;
        byte* prefixStart = @base + dictLimit;
        byte* dictBase = state.DictionaryBase;
        byte* dictEnd = dictBase + dictLimit;
        byte* dictStart = dictBase + state.LowLimit;
        uint offset1 = rep[0];
        uint offset2 = rep[1];

        state.LazySkipping = false;
        ip += ip == prefixStart ? 1 : 0;
        TSearch.Prepare(ref state);
        if (TSearch.UsesRows)
        {
            TSearch.FillHashCache(ref state, state.NextToUpdate, ilimit);
        }

        while (ip < ilimit)
        {
            nuint matchLength = 0;
            nuint offBase = RepeatCode1;
            byte* start = ip + 1;
            uint current = (uint)(ip - @base);

            // The first repeat offset at ip + 1.
            {
                nuint repLength = ExtDictRepeatLength(ref state, ip + 1, current + 1, offset1, dictBase, dictEnd, iend);
                if (repLength != 0)
                {
                    matchLength = repLength;
                    if (TDepth.Depth == 0)
                    {
                        goto StoreSequence;
                    }
                }
            }

            // The first search, at ip.
            {
                nuint candidate = 999999999;
                nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref candidate);
                if (length > matchLength)
                {
                    matchLength = length;
                    start = ip;
                    offBase = candidate;
                }
            }

            if (matchLength < 4)
            {
                nuint step = (nuint)(ip - anchor) >> SearchStrength;
                ip += step + 1;
                state.LazySkipping = step > LazySkippingStep;
                continue;
            }

            if (TDepth.Depth >= 1)
            {
                while (ip < ilimit)
                {
                    ip++;
                    current++;
                    {
                        nuint repLength = ExtDictRepeatLength(ref state, ip, current, offset1, dictBase, dictEnd, iend);
                        int gain2 = (int)(repLength * 3);
                        int gain1 = (int)(((nint)matchLength * 3) - HighBit(offBase) + 1);
                        if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                        {
                            matchLength = repLength;
                            offBase = RepeatCode1;
                            start = ip;
                        }
                    }

                    {
                        nuint candidate = 999999999;
                        nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref candidate);
                        int gain2 = (int)(((nint)length * 4) - HighBit(candidate));
                        int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 4);
                        if (X86Base.IsSupported ? length >= 4 && gain2 > gain1 : (length >= 4) & (gain2 > gain1))
                        {
                            matchLength = length;
                            offBase = candidate;
                            start = ip;
                            continue;
                        }
                    }

                    if (TDepth.Depth == 2 && ip < ilimit)
                    {
                        ip++;
                        current++;
                        {
                            nuint repLength = ExtDictRepeatLength(ref state, ip, current, offset1, dictBase, dictEnd, iend);
                            int gain2 = (int)(repLength * 4);
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 1);
                            if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                            {
                                matchLength = repLength;
                                offBase = RepeatCode1;
                                start = ip;
                            }
                        }

                        {
                            nuint candidate = 999999999;
                            nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref candidate);
                            int gain2 = (int)(((nint)length * 4) - HighBit(candidate));
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 7);
                            if (X86Base.IsSupported ? length >= 4 && gain2 > gain1 : (length >= 4) & (gain2 > gain1))
                            {
                                matchLength = length;
                                offBase = candidate;
                                start = ip;
                                continue;
                            }
                        }
                    }

                    break;
                }
            }

            // An offset (not a repeat code): the match extended backward, in its segment.
            if (offBase > RepeatCodeCount)
            {
                nuint offset = offBase - RepeatCodeCount;
                uint matchIndex = (uint)((nuint)(start - @base) - offset);
                byte* match = matchIndex < dictLimit ? dictBase + matchIndex : @base + matchIndex;
                byte* matchStart = matchIndex < dictLimit ? dictStart : prefixStart;
                while ((start > anchor) && (match > matchStart) && start[-1] == match[-1])
                {
                    start--;
                    match--;
                    matchLength++;
                }

                offset2 = offset1;
                offset1 = (uint)offset;
            }

        StoreSequence:
            store.Store((nuint)(start - anchor), anchor, iend, (uint)offBase, matchLength);
            anchor = ip = start + matchLength;
            if (state.LazySkipping)
            {
                // A match ends the skipping: the hash cache is filled again.
                if (TSearch.UsesRows)
                {
                    TSearch.FillHashCache(ref state, state.NextToUpdate, ilimit);
                }

                state.LazySkipping = false;
            }

            // The second repeat offset, at once.
            while (ip <= ilimit)
            {
                matchLength = ExtDictRepeatLength(ref state, ip, (uint)(ip - @base), offset2, dictBase, dictEnd, iend);
                if (matchLength == 0)
                {
                    break;
                }

                (offset1, offset2) = (offset2, offset1);
                store.Store(0, anchor, iend, RepeatCode1, matchLength);
                ip += matchLength;
                anchor = ip;
            }
        }

        rep[0] = offset1;
        rep[1] = offset2;
        return (nuint)(iend - anchor);
    }

    /// <summary>
    /// The match of a repeat offset at <paramref name="ip"/> (index <paramref name="current"/>) over an
    /// extDict: within the window, not straddling the segments; its length, 0 when there is none.
    /// </summary>
    private static nuint ExtDictRepeatLength(ref MatchState state, byte* ip, uint current, uint offset, byte* dictBase, byte* dictEnd, byte* iend)
    {
        uint dictLimit = state.DictLimit;
        uint windowLow = state.LowestMatchIndex(current);
        uint repIndex = current - offset;
        byte* repMatch = (repIndex < dictLimit ? dictBase : state.Base) + repIndex;
        if (!(IndexOverlapCheck(dictLimit, repIndex) & (offset <= current - windowLow)) || Read32(ip) != Read32(repMatch))
        {
            return 0;
        }

        byte* repEnd = repIndex < dictLimit ? dictEnd : iend;
        return Count2Segments(ip + 4, repMatch + 4, iend, repEnd, state.Base + dictLimit) + 4;
    }

    /// <summary>libzstd's <c>ZSTD_compressBlock_lazy_generic</c> for one search and one depth, without a dictionary.</summary>
    private static nuint CompressBlock<TSearch, TDepth>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where TSearch : struct, ILazySearch
        where TDepth : struct, ILazyDepth
    {
        byte* istart = source;
        byte* ip = istart;
        byte* anchor = istart;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize - (TSearch.UsesRows ? RowHashCacheSize : 0);
        byte* @base = state.Base;
        uint prefixLowestIndex = state.DictLimit;
        byte* prefixLowest = @base + prefixLowestIndex;

        uint offset1 = rep[0];
        uint offset2 = rep[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        // An attached dictionary: its indices just below the prefix's.
        bool attached = TSearch.DictionaryMode == AttachedDictionary.Value;
        byte* dictBase = null;
        byte* dictLowest = null;
        byte* dictEnd = null;
        uint dictIndexDelta = 0;
        if (attached)
        {
            MatchState* dictionary = state.Dictionary;
            dictBase = dictionary->Base;
            dictLowest = dictBase + dictionary->DictLimit;
            dictEnd = dictBase + dictionary->End;
            dictIndexDelta = prefixLowestIndex - dictionary->End;
            ip += (ip - prefixLowest) + (dictEnd - dictLowest) == 0 ? 1 : 0;
        }
        else
        {
            ip += ip == prefixLowest ? 1 : 0;
            uint current = (uint)(ip - @base);
            uint maxRep = current - state.LowestPrefixIndex(current);
            if (offset2 > maxRep)
            {
                offsetSaved2 = offset2;
                offset2 = 0;
            }

            if (offset1 > maxRep)
            {
                offsetSaved1 = offset1;
                offset1 = 0;
            }
        }

        state.LazySkipping = false;
        TSearch.Prepare(ref state);
        if (TSearch.UsesRows)
        {
            TSearch.FillHashCache(ref state, state.NextToUpdate, ilimit);
        }

        byte* lit = store.Literals;
        SequenceRecord* sequence = store.Sequences;
        uint* counts = store.Counts;

        while (ip < ilimit)
        {
            nuint matchLength = 0;
            nuint offBase = RepeatCode1;
            byte* start = ip + 1;

            // The first repeat offset at ip + 1.
            if (attached)
            {
                uint repIndex = (uint)(ip - @base) + 1 - offset1;
                byte* repMatch = repIndex < prefixLowestIndex ? dictBase + (repIndex - dictIndexDelta) : @base + repIndex;
                if (IndexOverlapCheck(prefixLowestIndex, repIndex) && Read32(repMatch) == Read32(ip + 1))
                {
                    byte* repMatchEnd = repIndex < prefixLowestIndex ? dictEnd : iend;
                    matchLength = Count2Segments(ip + 1 + 4, repMatch + 4, iend, repMatchEnd, prefixLowest) + 4;
                    if (TDepth.Depth == 0)
                    {
                        goto StoreSequence;
                    }
                }
            }
            else if (X86Base.IsSupported ? offset1 > 0 && Read32(ip + 1 - offset1) == Read32(ip + 1) : (offset1 > 0) & (Read32(ip + 1 - offset1) == Read32(ip + 1)))
            {
                matchLength = Count(ip + 1 + 4, ip + 1 + 4 - offset1, iend) + 4;
                if (TDepth.Depth == 0)
                {
                    goto StoreSequence;
                }
            }

            // The first search, at ip.
            {
                nuint found = 999999999;
                nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref found);
                if (length > matchLength)
                {
                    matchLength = length;
                    start = ip;
                    offBase = found;
                }
            }

            if (matchLength < 4)
            {
                // Faster over what does not compress; past a step of 8, only the positions searched
                // are inserted.
                nuint step = ((nuint)(ip - anchor) >> SearchStrength) + 1;
                ip += step;
                state.LazySkipping = step > LazySkippingStep;
                continue;
            }

            // A better match at the next positions, the offset's cost weighed against the length.
            if (TDepth.Depth >= 1)
            {
                while (ip < ilimit)
                {
                    ip++;
                    if (attached)
                    {
                        nuint repLength = AttachedRepeatLength(ip, offset1, @base, prefixLowestIndex, dictBase, dictIndexDelta, dictEnd, iend);
                        int gain2 = (int)(repLength * 3);
                        int gain1 = (int)(((nint)matchLength * 3) - HighBit(offBase) + 1);
                        if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                        {
                            matchLength = repLength;
                            offBase = RepeatCode1;
                            start = ip;
                        }
                    }
                    else if (X86Base.IsSupported ? offset1 > 0 && Read32(ip) == Read32(ip - offset1) : (offset1 > 0) & (Read32(ip) == Read32(ip - offset1)))
                    {
                        nuint repLength = Count(ip + 4, ip + 4 - offset1, iend) + 4;
                        int gain2 = (int)(repLength * 3);
                        int gain1 = (int)(((nint)matchLength * 3) - HighBit(offBase) + 1);
                        if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                        {
                            matchLength = repLength;
                            offBase = RepeatCode1;
                            start = ip;
                        }
                    }

                    {
                        nuint candidate = 999999999;
                        nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref candidate);
                        int gain2 = (int)(((nint)length * 4) - HighBit(candidate));
                        int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 4);
                        if (X86Base.IsSupported ? length >= 4 && gain2 > gain1 : (length >= 4) & (gain2 > gain1))
                        {
                            matchLength = length;
                            offBase = candidate;
                            start = ip;
                            continue;
                        }
                    }

                    if (TDepth.Depth == 2 && ip < ilimit)
                    {
                        ip++;
                        if (attached)
                        {
                            nuint repLength = AttachedRepeatLength(ip, offset1, @base, prefixLowestIndex, dictBase, dictIndexDelta, dictEnd, iend);
                            int gain2 = (int)(repLength * 4);
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 1);
                            if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                            {
                                matchLength = repLength;
                                offBase = RepeatCode1;
                                start = ip;
                            }
                        }
                        else if (X86Base.IsSupported ? offset1 > 0 && Read32(ip) == Read32(ip - offset1) : (offset1 > 0) & (Read32(ip) == Read32(ip - offset1)))
                        {
                            nuint repLength = Count(ip + 4, ip + 4 - offset1, iend) + 4;
                            int gain2 = (int)(repLength * 4);
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 1);
                            if (X86Base.IsSupported ? repLength >= 4 && gain2 > gain1 : (repLength >= 4) & (gain2 > gain1))
                            {
                                matchLength = repLength;
                                offBase = RepeatCode1;
                                start = ip;
                            }
                        }

                        {
                            nuint candidate = 999999999;
                            nuint length = TSearch.FindBestMatch(ref state, ip, iend, ref candidate);
                            int gain2 = (int)(((nint)length * 4) - HighBit(candidate));
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 7);
                            if (X86Base.IsSupported ? length >= 4 && gain2 > gain1 : (length >= 4) & (gain2 > gain1))
                            {
                                matchLength = length;
                                offBase = candidate;
                                start = ip;
                                continue;
                            }
                        }
                    }

                    break;
                }
            }

            // An offset (not a repeat code): the match extended backward, the offsets rotated.
            if (offBase > RepeatCodeCount)
            {
                nuint offset = offBase - RepeatCodeCount;
                if (attached)
                {
                    // The match may start in the dictionary.
                    uint matchIndex = (uint)((nuint)(start - @base) - offset);
                    byte* match = matchIndex < prefixLowestIndex ? dictBase + matchIndex - dictIndexDelta : @base + matchIndex;
                    byte* matchStart = matchIndex < prefixLowestIndex ? dictLowest : prefixLowest;
                    while ((start > anchor) && (match > matchStart) && start[-1] == match[-1])
                    {
                        start--;
                        match--;
                        matchLength++;
                    }
                }
                else
                {
                    while ((X86Base.IsSupported ? start > anchor && start - offset > prefixLowest : (start > anchor) & (start - offset > prefixLowest)) && start[-1] == (start - offset)[-1])
                    {
                        start--;
                        matchLength++;
                    }
                }

                offset2 = offset1;
                offset1 = (uint)offset;
            }

        StoreSequence:
            SequenceStore.Store(ref lit, ref sequence, counts, (nuint)(start - anchor), anchor, iend, (uint)offBase, matchLength);
            anchor = ip = start + matchLength;
            if (state.LazySkipping)
            {
                // A match ends the skipping: the hash cache is filled again.
                if (TSearch.UsesRows)
                {
                    TSearch.FillHashCache(ref state, state.NextToUpdate, ilimit);
                }

                state.LazySkipping = false;
            }

            // The second repeat offset, at once.
            if (attached)
            {
                while (ip <= ilimit)
                {
                    matchLength = AttachedRepeatLength(ip, offset2, @base, prefixLowestIndex, dictBase, dictIndexDelta, dictEnd, iend);
                    if (matchLength == 0)
                    {
                        break;
                    }

                    (offset1, offset2) = (offset2, offset1);
                    SequenceStore.StoreOnly(ref sequence, counts, 0, RepeatCode1, matchLength);
                    ip += matchLength;
                    anchor = ip;
                }
            }
            else
            {
                while ((X86Base.IsSupported ? ip <= ilimit && offset2 > 0 : (ip <= ilimit) & (offset2 > 0)) && Read32(ip) == Read32(ip - offset2))
                {
                    matchLength = Count(ip + 4, ip + 4 - offset2, iend) + 4;
                    (offset1, offset2) = (offset2, offset1);
                    SequenceStore.StoreOnly(ref sequence, counts, 0, RepeatCode1, matchLength);
                    ip += matchLength;
                    anchor = ip;
                }
            }
        }

        // A first offset invalid at the start, made valid since, hands the saved one down.
        offsetSaved2 = offsetSaved1 != 0 && offset1 != 0 ? offsetSaved1 : offsetSaved2;
        rep[0] = offset1 != 0 ? offset1 : offsetSaved1;
        rep[1] = offset2 != 0 ? offset2 : offsetSaved2;
        store.Literals = lit;
        store.Sequences = sequence;
        return (nuint)(iend - anchor);
    }

    /// <summary>
    /// The match of a repeat offset at <paramref name="ip"/> with a dictionary attached, which it may
    /// start in: its length, 0 when its four bytes differ or straddle the segments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AttachedRepeatLength(
        byte* ip, uint offset, byte* @base, uint prefixLowestIndex, byte* dictBase, uint dictIndexDelta, byte* dictEnd, byte* iend)
    {
        uint repIndex = (uint)(ip - @base) - offset;
        byte* repMatch = repIndex < prefixLowestIndex ? dictBase + (repIndex - dictIndexDelta) : @base + repIndex;
        if (!IndexOverlapCheck(prefixLowestIndex, repIndex) || Read32(repMatch) != Read32(ip))
        {
            return 0;
        }

        byte* repMatchEnd = repIndex < prefixLowestIndex ? dictEnd : iend;
        return Count2Segments(ip + 4, repMatch + 4, iend, repMatchEnd, @base + prefixLowestIndex) + 4;
    }

    /// <summary>libzstd's <c>ZSTD_highbit32</c> of an offset code, as the lazy parser weighs it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HighBit(nuint offBase) => BitOperations.Log2((uint)offBase);
}

/// <summary>How far the lazy parser looks ahead: libzstd's <c>depth</c>.</summary>
internal interface ILazyDepth
{
    static abstract int Depth { get; }
}

internal readonly struct Depth0 : ILazyDepth
{
    public static int Depth => 0;
}

internal readonly struct Depth1 : ILazyDepth
{
    public static int Depth => 1;
}

internal readonly struct Depth2 : ILazyDepth
{
    public static int Depth => 2;
}

/// <summary>A search of the lazy parser: libzstd's <c>searchMax</c> for one method and one minimum length.</summary>
internal unsafe interface ILazySearch
{
    /// <summary>Whether this is the row-based match finder, whose hash cache the parser fills.</summary>
    static abstract bool UsesRows { get; }

    /// <summary>What the search needs in the match state before a block.</summary>
    static abstract void Prepare(ref MatchState state);

    /// <summary>
    /// The longest match at <paramref name="ip"/>, the positions before it inserted first: its length
    /// (3 when there is none of 4 bytes), its offset code into <paramref name="offBase"/> when found.
    /// </summary>
    static abstract nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase);

    /// <summary>libzstd's <c>ZSTD_row_fillHashCache</c>: the hashes of the positions from <paramref name="index"/>.</summary>
    static abstract void FillHashCache(ref MatchState state, uint index, byte* limit);

    /// <summary>The dictionary mode of the search: <see cref="IDictionaryMode.Mode"/>.</summary>
    static abstract int DictionaryMode { get; }

    /// <summary>
    /// A dictionary's positions into the tables, up to <paramref name="ip"/> excluded (libzstd's
    /// <c>ZSTD_insertAndFindFirstIndex</c>, <c>ZSTD_row_update</c>): all of them, without the cache.
    /// </summary>
    static abstract void Fill(ref MatchState state, byte* ip);
}

/// <summary>
/// libzstd's hash chains (<c>ZSTD_HcFindBestMatch</c>): the last position of each hash in the hash
/// table, each position's predecessor of the same hash in the chain table, followed up to
/// 2^searchLog candidates.
/// </summary>
internal readonly unsafe struct HashChainSearch<THash, TDictionary> : ILazySearch
    where THash : IMatchHash
    where TDictionary : IDictionaryMode
{
    public static bool UsesRows => false;

    public static int DictionaryMode => TDictionary.Mode;

    public static void Fill(ref MatchState state, byte* ip) => InsertAndFindFirstIndex(ref state, ip);

    public static void Prepare(ref MatchState state)
    {
    }

    public static void FillHashCache(ref MatchState state, uint index, byte* limit)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        // The insertion first, then the search's values: computed before it, they were live across
        // its loop, which x64 has not the registers for, and spilled, the loop's own with them.
        nuint matchIndex = InsertAndFindFirstIndex(ref state, ip);

        uint* chainTable = state.ChainTable;
        uint chainSize = 1u << state.Parameters.ChainLog;
        nuint chainMask = chainSize - 1;
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        nuint lowLimit = state.LowestMatchIndex(current);
        nuint minChain = current > chainSize ? current - chainSize : 0;
        uint attempts = 1u << state.Parameters.SearchLog;

        // The chain's indices are native integers, read from the table as such: as 32-bit ones, the
        // JIT zero-extended and scaled each in an instruction of its own, on the walk's dependent chain.
        // An extDict's indices, below the prefix, read from its own base.
        uint dictLimit = state.DictLimit;
        byte* prefixStart = @base + dictLimit;
        byte* dictBase = state.DictionaryBase;
        byte* dictEnd = dictBase + dictLimit;

        nuint bestLength = 4 - 1;
        for (; X86Base.IsSupported ? matchIndex >= lowLimit && attempts > 0 : (matchIndex >= lowLimit) & (attempts > 0); attempts--)
        {
            if (TDictionary.Mode != ExtDictionary.Value || matchIndex >= dictLimit)
            {
                byte* match = @base + matchIndex;

                // The four bytes that end one past the best length: a longer match has them.
                if (Read32(match + bestLength - 3) == Read32(ip + bestLength - 3))
                {
                    nuint length = Count(ip, match, end);
                    if (length > bestLength)
                    {
                        bestLength = length;
                        offBase = OffsetToOffBase(current - (uint)matchIndex);
                        if (ip + length == end)
                        {
                            // The longest possible, and reading further would pass the end.
                            break;
                        }
                    }
                }
            }
            else
            {
                byte* match = dictBase + matchIndex;
                if (Read32(match) == Read32(ip))
                {
                    nuint length = Count2Segments(ip + 4, match + 4, end, dictEnd, prefixStart) + 4;
                    if (length > bestLength)
                    {
                        bestLength = length;
                        offBase = OffsetToOffBase(current - (uint)matchIndex);
                        if (ip + length == end)
                        {
                            break;
                        }
                    }
                }
            }

            if (matchIndex <= minChain)
            {
                break;
            }

            matchIndex = chainTable[matchIndex & chainMask];
        }

        if (TDictionary.Mode == AttachedDictionary.Value)
        {
            bestLength = SearchDictionary(ref state, ip, end, ref offBase, bestLength, attempts);
        }

        return bestLength;
    }

    /// <summary>
    /// The attached dictionary's chain, with the attempts the frame's left: its own tables and
    /// parameters, its indices just below the prefix's.
    /// </summary>
    private static nuint SearchDictionary(ref MatchState state, byte* ip, byte* end, ref nuint offBase, nuint bestLength, uint attempts)
    {
        MatchState* dictionary = state.Dictionary;
        uint* chainTable = dictionary->ChainTable;
        uint chainSize = 1u << dictionary->Parameters.ChainLog;
        uint chainMask = chainSize - 1;
        uint lowestIndex = dictionary->DictLimit;
        byte* dictionaryBase = dictionary->Base;
        byte* dictionaryEnd = dictionaryBase + dictionary->End;
        uint dictionarySize = dictionary->End;
        uint indexDelta = state.DictLimit - dictionarySize;
        uint minChain = dictionarySize > chainSize ? dictionarySize - chainSize : 0;
        byte* prefixStart = state.Base + state.DictLimit;
        uint current = (uint)(ip - state.Base);
        uint matchIndex = dictionary->HashTable[THash.Hash(Read64(ip), dictionary->Parameters.HashLog)];
        for (; (matchIndex >= lowestIndex) & (attempts > 0); attempts--)
        {
            byte* match = dictionaryBase + matchIndex;
            if (Read32(match) == Read32(ip))
            {
                nuint length = Count2Segments(ip + 4, match + 4, end, dictionaryEnd, prefixStart) + 4;
                if (length > bestLength)
                {
                    bestLength = length;
                    offBase = OffsetToOffBase(current - (matchIndex + indexDelta));
                    if (ip + length == end)
                    {
                        break;
                    }
                }
            }

            if (matchIndex <= minChain)
            {
                break;
            }

            matchIndex = chainTable[matchIndex & chainMask];
        }

        return bestLength;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_insertAndFindFirstIndex_internal</c>: the positions up to <paramref name="ip"/>
    /// (excluded) into the chains, only one when skipping; the head of <paramref name="ip"/>'s chain.
    /// Inlined: across a call, the search's best length lived on the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint InsertAndFindFirstIndex(ref MatchState state, byte* ip)
    {
        uint* hashTable = state.HashTable;
        int hashLog = state.Parameters.HashLog;
        uint* chainTable = state.ChainTable;
        uint chainMask = (1u << state.Parameters.ChainLog) - 1;
        byte* @base = state.Base;
        uint target = (uint)(ip - @base);
        uint index = state.NextToUpdate;
        if (state.LazySkipping)
        {
            // Only the first position while skipping.
            if (index < target)
            {
                nuint hash = THash.Hash(Read64(@base + index), hashLog);
                chainTable[index & chainMask] = hashTable[hash];
                hashTable[hash] = index;
            }
        }
        else
        {
            // The skipping test out of the loop, which then holds one value less: on x64, short of
            // registers in the search it is inlined into, the tables and the mask came from memory.
            for (; index < target; index++)
            {
                nuint hash = THash.Hash(Read64(@base + index), hashLog);
                chainTable[index & chainMask] = hashTable[hash];
                hashTable[hash] = index;
            }
        }

        state.NextToUpdate = target;
        return hashTable[THash.Hash(Read64(ip), hashLog)];
    }
}

/// <summary>
/// libzstd's binary tree of the btlazy2 strategy (<c>ZSTD_BtFindBestMatch</c>, a "dual unsorted binary
/// tree"): positions are first chained by hash, unsorted, and sorted into the tree of their hash
/// only when a search reaches them; the tree orders the positions by their bytes, and a search
/// walks it from the hash's root, inserting the position as it goes.
/// </summary>
/// <remarks>
/// The chain table holds two entries a position, its children in the tree (smaller, larger), at the
/// position modulo the tree's size: while it is unsorted, the first is the next position of its
/// hash and the second <see cref="UnsortedMark"/>, an index below every window.
/// </remarks>
internal readonly unsafe struct BinaryTreeSearch<THash, TDictionary> : ILazySearch
    where THash : IMatchHash
    where TDictionary : IDictionaryMode
{
    /// <summary>libzstd's <c>ZSTD_DUBT_UNSORTED_MARK</c>.</summary>
    private const uint UnsortedMark = 1;

    public static bool UsesRows => false;

    public static int DictionaryMode => TDictionary.Mode;

    /// <summary>Not used: a dictionary goes into the tree sorted, as the optimal parsers insert (<see cref="OptimalMatchFinder.FillTree"/>).</summary>
    public static void Fill(ref MatchState state, byte* ip) => throw new NotSupportedException();

    public static void Prepare(ref MatchState state)
    {
    }

    public static void FillHashCache(ref MatchState state, uint index, byte* limit)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        if (ip < state.Base + state.NextToUpdate)
        {
            // Skipped: inside a repetitive match the last search went past.
            return 0;
        }

        Update(ref state, ip);
        return FindBestMatchInTree(ref state, ip, end, ref offBase);
    }

    /// <summary>libzstd's <c>ZSTD_updateDUBT</c>: the positions up to <paramref name="ip"/> (excluded) chained, unsorted.</summary>
    private static void Update(ref MatchState state, byte* ip)
    {
        uint* hashTable = state.HashTable;
        int hashLog = state.Parameters.HashLog;
        uint* tree = state.ChainTable;
        uint treeMask = (1u << (state.Parameters.ChainLog - 1)) - 1;
        byte* @base = state.Base;
        uint target = (uint)(ip - @base);
        for (uint index = state.NextToUpdate; index < target; index++)
        {
            nuint hash = THash.Hash(Read64(@base + index), hashLog);
            uint matchIndex = hashTable[hash];
            uint* next = tree + (2 * (index & treeMask));
            hashTable[hash] = index;
            next[0] = matchIndex;
            next[1] = UnsortedMark;
        }

        state.NextToUpdate = target;
    }

    /// <summary>libzstd's <c>ZSTD_DUBT_findBestMatch</c> without a dictionary.</summary>
    private static nuint FindBestMatchInTree(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        uint* hashTable = state.HashTable;
        nuint hash = THash.Hash(Read64(ip), state.Parameters.HashLog);
        uint matchIndex = hashTable[hash];
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        uint windowLow = state.LowestMatchIndex(current);
        uint* tree = state.ChainTable;
        uint treeMask = (1u << (state.Parameters.ChainLog - 1)) - 1;
        uint treeLow = treeMask >= current ? 0 : current - treeMask;
        uint unsortLimit = Math.Max(treeLow, windowLow);
        uint* nextCandidate = tree + (2 * (matchIndex & treeMask));
        uint* unsortedMark = nextCandidate + 1;
        uint compares = 1u << state.Parameters.SearchLog;
        uint candidates = compares;
        uint previousCandidate = 0;

        // The unsorted positions of the hash, newest first, their marks turned into a chain back.
        while ((matchIndex > unsortLimit) && (*unsortedMark == UnsortedMark) && (candidates > 1))
        {
            *unsortedMark = previousCandidate;
            previousCandidate = matchIndex;
            matchIndex = *nextCandidate;
            nextCandidate = tree + (2 * (matchIndex & treeMask));
            unsortedMark = nextCandidate + 1;
            candidates--;
        }

        // The last, still unsorted, dropped: libzstd's speed over ratio.
        if ((matchIndex > unsortLimit) && (*unsortedMark == UnsortedMark))
        {
            *nextCandidate = *unsortedMark = 0;
        }

        // Each sorted into the tree, the oldest first.
        matchIndex = previousCandidate;
        while (matchIndex != 0)
        {
            uint* nextCandidateIndex = tree + (2 * (matchIndex & treeMask)) + 1;
            uint next = *nextCandidateIndex;
            Insert(ref state, matchIndex, end, candidates, unsortLimit);
            matchIndex = next;
            candidates++;
        }

        // The longest match, ip inserted on the way.
        uint dictLimit = state.DictLimit;
        byte* dictBase = state.DictionaryBase;
        byte* dictEnd = dictBase + dictLimit;
        byte* prefixStart = @base + dictLimit;
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        uint* smallerPtr = tree + (2 * (current & treeMask));
        uint* largerPtr = smallerPtr + 1;
        uint matchEndIndex = current + 8 + 1;
        uint dummy;
        nuint bestLength = 0;

        matchIndex = hashTable[hash];
        hashTable[hash] = current;
        for (; (compares != 0) && (matchIndex > windowLow); compares--)
        {
            uint* nextPtr = tree + (2 * (matchIndex & treeMask));
            nuint matchLength = Math.Min(commonLengthSmaller, commonLengthLarger);
            byte* match;
            if (TDictionary.Mode != ExtDictionary.Value || matchIndex + matchLength >= dictLimit)
            {
                match = @base + matchIndex;
                matchLength += Count(ip + matchLength, match + matchLength, end);
            }
            else
            {
                match = dictBase + matchIndex;
                matchLength += Count2Segments(ip + matchLength, match + matchLength, end, dictEnd, prefixStart);
                if (matchIndex + matchLength >= dictLimit)
                {
                    // The match runs into the prefix: its next byte is there.
                    match = @base + matchIndex;
                }
            }

            if (matchLength > bestLength)
            {
                if (matchLength > matchEndIndex - matchIndex)
                {
                    matchEndIndex = matchIndex + (uint)matchLength;
                }

                // A longer match is taken when its length outweighs its offset's cost.
                if (4 * (int)(matchLength - bestLength) > HighBit(current - matchIndex + 1) - HighBit((uint)offBase))
                {
                    bestLength = matchLength;
                    offBase = OffsetToOffBase(current - matchIndex);
                }

                if (ip + matchLength == end)
                {
                    // Equal to the end: no way to know whether smaller or larger, so dropped, the
                    // attached dictionary's search with it.
                    if (TDictionary.Mode == AttachedDictionary.Value)
                    {
                        compares = 0;
                    }

                    break;
                }
            }

            if (match[matchLength] < ip[matchLength])
            {
                // The match is smaller than ip.
                *smallerPtr = matchIndex;
                commonLengthSmaller = matchLength;
                if (matchIndex <= treeLow)
                {
                    smallerPtr = &dummy;
                    break;
                }

                smallerPtr = nextPtr + 1;
                matchIndex = nextPtr[1];
            }
            else
            {
                // The match is larger.
                *largerPtr = matchIndex;
                commonLengthLarger = matchLength;
                if (matchIndex <= treeLow)
                {
                    largerPtr = &dummy;
                    break;
                }

                largerPtr = nextPtr;
                matchIndex = nextPtr[0];
            }
        }

        *smallerPtr = *largerPtr = 0;

        if (TDictionary.Mode == AttachedDictionary.Value && compares != 0)
        {
            bestLength = FindBetterDictionaryMatch(ref state, ip, end, ref offBase, bestLength, compares);
        }

        // Past a repetitive match, its positions are not inserted.
        state.NextToUpdate = matchEndIndex - 8;
        return bestLength;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_DUBT_findBetterDictMatch</c>: the attached dictionary's tree (sorted when it
    /// was filled), with the compares the frame's left; a longer match taken as the frame's search
    /// takes one, but against the best offset plus 1.
    /// </summary>
    private static nuint FindBetterDictionaryMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase, nuint bestLength, uint compares)
    {
        MatchState* dictionary = state.Dictionary;
        uint dictMatchIndex = dictionary->HashTable[THash.Hash(Read64(ip), dictionary->Parameters.HashLog)];
        byte* @base = state.Base;
        byte* prefixStart = @base + state.DictLimit;
        uint current = (uint)(ip - @base);
        byte* dictBase = dictionary->Base;
        byte* dictEnd = dictBase + dictionary->End;
        uint dictHighLimit = dictionary->End;
        uint dictLowLimit = dictionary->LowLimit;
        uint dictIndexDelta = state.LowLimit - dictHighLimit;
        uint* dictTree = dictionary->ChainTable;
        uint treeMask = (1u << (dictionary->Parameters.ChainLog - 1)) - 1;
        uint treeLow = treeMask >= dictHighLimit - dictLowLimit ? dictLowLimit : dictHighLimit - treeMask;
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        for (; (compares != 0) && (dictMatchIndex > dictLowLimit); compares--)
        {
            uint* nextPtr = dictTree + (2 * (dictMatchIndex & treeMask));
            nuint matchLength = Math.Min(commonLengthSmaller, commonLengthLarger);
            byte* match = dictBase + dictMatchIndex;
            matchLength += Count2Segments(ip + matchLength, match + matchLength, end, dictEnd, prefixStart);
            if (dictMatchIndex + matchLength >= dictHighLimit)
            {
                // The match runs into the prefix: its next byte is there.
                match = @base + dictMatchIndex + dictIndexDelta;
            }

            if (matchLength > bestLength)
            {
                uint matchIndex = dictMatchIndex + dictIndexDelta;
                if (4 * (int)(matchLength - bestLength) > HighBit(current - matchIndex + 1) - HighBit((uint)offBase + 1))
                {
                    bestLength = matchLength;
                    offBase = OffsetToOffBase(current - matchIndex);
                }

                if (ip + matchLength == end)
                {
                    break;
                }
            }

            if (dictMatchIndex <= treeLow)
            {
                break;
            }

            if (match[matchLength] < ip[matchLength])
            {
                commonLengthSmaller = matchLength;
                dictMatchIndex = nextPtr[1];
            }
            else
            {
                commonLengthLarger = matchLength;
                dictMatchIndex = nextPtr[0];
            }
        }

        return bestLength;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_insertDUBT1</c> without a dictionary: an unsorted position sorted into its
    /// tree, comparing it with up to <paramref name="compares"/> positions.
    /// </summary>
    private static void Insert(ref MatchState state, uint current, byte* end, uint compares, uint treeLow)
    {
        uint* tree = state.ChainTable;
        uint treeMask = (1u << (state.Parameters.ChainLog - 1)) - 1;
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        byte* @base = state.Base;
        uint dictLimit = state.DictLimit;
        byte* dictBase = state.DictionaryBase;
        byte* dictEnd = dictBase + dictLimit;
        byte* prefixStart = @base + dictLimit;
        bool inExtDict = TDictionary.Mode == ExtDictionary.Value && current < dictLimit;
        byte* ip = inExtDict ? dictBase + current : @base + current;
        byte* inputEnd = inExtDict ? dictEnd : end;
        uint* smallerPtr = tree + (2 * (current & treeMask));
        uint* largerPtr = smallerPtr + 1;

        // This position is unsorted: the next sorted one is reached through its first entry, while
        // the second holds the previous unsorted one, already saved, which may be overwritten.
        uint matchIndex = *smallerPtr;
        uint dummy;
        uint windowLow = state.LowestIndexInWindow(current);

        for (; (compares != 0) && (matchIndex > windowLow); compares--)
        {
            uint* nextPtr = tree + (2 * (matchIndex & treeMask));
            nuint matchLength = Math.Min(commonLengthSmaller, commonLengthLarger);
            byte* match;
            if (TDictionary.Mode != ExtDictionary.Value || matchIndex + matchLength >= dictLimit || current < dictLimit)
            {
                // Both in the same segment.
                match = (TDictionary.Mode != ExtDictionary.Value || matchIndex + matchLength >= dictLimit ? @base : dictBase) + matchIndex;
                matchLength += Count(ip + matchLength, match + matchLength, inputEnd);
            }
            else
            {
                match = dictBase + matchIndex;
                matchLength += Count2Segments(ip + matchLength, match + matchLength, inputEnd, dictEnd, prefixStart);
                if (matchIndex + matchLength >= dictLimit)
                {
                    match = @base + matchIndex;
                }
            }

            if (ip + matchLength == inputEnd)
            {
                // Equal to the end: dropped, to keep the tree consistent.
                break;
            }

            if (match[matchLength] < ip[matchLength])
            {
                *smallerPtr = matchIndex;
                commonLengthSmaller = matchLength;
                if (matchIndex <= treeLow)
                {
                    smallerPtr = &dummy;
                    break;
                }

                smallerPtr = nextPtr + 1;
                matchIndex = nextPtr[1];
            }
            else
            {
                *largerPtr = matchIndex;
                commonLengthLarger = matchLength;
                if (matchIndex <= treeLow)
                {
                    largerPtr = &dummy;
                    break;
                }

                largerPtr = nextPtr;
                matchIndex = nextPtr[0];
            }
        }

        *smallerPtr = *largerPtr = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HighBit(uint value) => BitOperations.Log2(value);
}

/// <summary>The entries of a row of the row-based match finder: libzstd's <c>1 &lt;&lt; rowLog</c>.</summary>
internal interface IRowLog
{
    static abstract int Log { get; }
}

internal readonly struct Row16 : IRowLog
{
    public static int Log => 4;
}

internal readonly struct Row32 : IRowLog
{
    public static int Log => 5;
}

internal readonly struct Row64 : IRowLog
{
    public static int Log => 6;
}

/// <summary>
/// libzstd's row-based match finder (<c>ZSTD_RowFindBestMatch</c>): a hash picks a row of 16, 32 or
/// 64 entries, a circular buffer of the last positions of its hashes, and an 8-bit tag, kept beside
/// each entry in the tag table; the entries whose tags match are the candidates, the newest first.
/// </summary>
/// <remarks>
/// <para>
/// The hash is salted, as libzstd salts it (<c>ZSTD_hashPtrSalted</c>), with a value that changes at
/// every frame (<see cref="MatchState.RowHashSalt"/>), XORed in before its shift: it relabels the rows
/// and the tags, without changing which positions share them, so the frames are the same whatever
/// the salt. What it changes is where the entries of earlier frames lie: without it, a frame of the
/// same content as the last finds the last's entry of each position in its row, under its tag, and
/// reads it before it stops there (it is below the window); with it, they are scattered over other
/// rows and tags.
/// </para>
/// <para>
/// The first byte of each row of tags is the row's head, the newest entry's place: the entries fill
/// places rowEntries - 1 down to 1 and wrap, place 0 never holding one.
/// </para>
/// <para>
/// libzstd prefetches the rows of the position eight ahead, whose hash its cache computes, and the
/// candidates before it compares them. So does x64 (see <c>PrefetchRow</c>); .NET has no prefetch on
/// Arm64. There, the row of entries is brought in by a store instead: of a 0 to its place 0, which no
/// entry takes and nothing reads, in the line the position writes once inserted. A store retires as a prefetch does, before its line arrives,
/// where a load holds up the retirement of everything after it until its line is there. The row of
/// tags has no such place (its first byte is the head, its others are tags, all read by the search,
/// which a store still on its way would hold up): it is read, as are the candidates, summed into
/// <see cref="MatchState.Touched"/>, which keeps the loads alive and the lines on their way.
/// </para>
/// </remarks>
internal readonly unsafe struct RowSearch<THash, TRow, TDictionary> : ILazySearch
    where THash : IMatchHash
    where TRow : IRowLog
    where TDictionary : IDictionaryMode
{
    /// <summary>libzstd's <c>kSkipThreshold</c>: past this many positions to insert, most are skipped.</summary>
    private const uint SkipThreshold = 384;

    public static bool UsesRows => true;

    public static int DictionaryMode => TDictionary.Mode;

    /// <summary>libzstd's <c>ZSTD_row_update</c>: every position up to <paramref name="ip"/>, hashed without the cache.</summary>
    public static void Fill(ref MatchState state, byte* ip)
    {
        Prepare(ref state);
        byte* @base = state.Base;
        ulong multiplier = state.RowHashMultiplier;
        ulong salt = state.RowHashSalt;
        int shift = state.RowHashShift;
        uint target = (uint)(ip - @base);
        for (uint index = state.NextToUpdate; index < target; index++)
        {
            Insert(state.TagTable, state.HashTable, (nuint)(((Read64(@base + index) * multiplier) ^ salt) >> shift), index);
        }

        state.NextToUpdate = target;
    }

    public static void Prepare(ref MatchState state)
    {
        state.RowHashMultiplier = THash.Multiplier;
        state.RowHashShift = 64 - (state.RowHashLog + LazyMatchFinder.RowHashTagBits);
        state.RowAttempts = 1u << Math.Min(state.Parameters.SearchLog, TRow.Log);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        int rowLog = TRow.Log;
        nuint rowMask = ((nuint)1 << rowLog) - 1;
        byte* @base = state.Base;
        uint* hashTable = state.HashTable;
        byte* tagTable = state.TagTable;
        uint current = (uint)(ip - @base);
        uint read = 0;

        // An attached dictionary's row, which libzstd prefetches first (ZSTD_row_prefetch): read
        // here, its misses hidden behind the frame's own search.
        nuint dictionaryHash = 0;
        if (TDictionary.Mode == AttachedDictionary.Value)
        {
            MatchState* dictionary = state.Dictionary;
            dictionaryHash = (nuint)((Read64(ip) * state.RowHashMultiplier) >> (64 - (dictionary->RowHashLog + LazyMatchFinder.RowHashTagBits)));
            nuint dictionaryRow = RowOf(dictionaryHash);
            PrefetchRow(dictionary->TagTable, dictionary->HashTable, dictionaryRow, ref read, write: false);
        }

        // libzstd's ZSTD_row_update_internal: the positions before ip into their rows, then ip's
        // hash, from the cache; while skipping, ip's hash alone.
        nuint hash;
        if (!state.LazySkipping)
        {
            uint index = state.NextToUpdate;
            if (current - index > SkipThreshold)
            {
                index = UpdateAfterLongMatch(ref state, index, ip);
            }

            uint* cache = state.HashCache;
            ulong multiplier = state.RowHashMultiplier;
            ulong salt = state.RowHashSalt;
            int shift = state.RowHashShift;
            for (; index < current; index++)
            {
                nuint indexHash = NextCachedHash(cache, @base, index, multiplier, salt, shift, tagTable, hashTable, ref read);
                Insert(tagTable, hashTable, indexHash, index);
            }

            state.NextToUpdate = current;
            hash = NextCachedHash(cache, @base, current, multiplier, salt, shift, tagTable, hashTable, ref read);
        }
        else
        {
            hash = (nuint)(((Read64(ip) * state.RowHashMultiplier) ^ state.RowHashSalt) >> state.RowHashShift);
            state.NextToUpdate = current;
        }

        nuint relativeRow = RowOf(hash);
        byte tag = (byte)hash;
        uint* row = hashTable + relativeRow;
        byte* tagRow = tagTable + relativeRow;
        uint head = tagRow[0] & (uint)rowMask;
        uint lowLimit = state.LowestMatchIndex(current);
        uint attempts = state.RowAttempts;

        // The candidates, newest first, up to the attempts allowed or the first below the window,
        // gathered before they are compared, as libzstd does, so that their misses overlap. Into
        // the compressor's buffer: one on the stack brought a probe and a cookie to every search.
        uint* candidates = state.Candidates;
        nuint count = 0;
        nuint headGrouped = (nuint)head << MaskGroupShift;
        ulong matches = MatchMask(tagRow, tag, head);
        while (matches != 0)
        {
            nuint matchPosition = ((headGrouped + (nuint)(uint)BitOperations.TrailingZeroCount(matches)) >> MaskGroupShift) & rowMask;
            matches &= matches - 1;
            if (matchPosition == 0)
            {
                continue;
            }

            uint matchIndex = row[matchPosition];
            if (matchIndex < lowLimit)
            {
                break;
            }

            candidates[count] = matchIndex;
            count++;
            PrefetchCandidate((TDictionary.Mode == ExtDictionary.Value && matchIndex < state.DictLimit ? state.DictionaryBase : @base) + matchIndex, ref read);
            if (--attempts == 0)
            {
                break;
            }
        }

        state.Touched += read;

        // ip itself, in its row.
        {
            nuint position = NextIndex(tagRow);
            tagRow[position] = tag;
            row[position] = state.NextToUpdate++;
        }

        nuint bestLength = 4 - 1;
        for (nuint k = 0; k < count; k++)
        {
            uint matchIndex = candidates[k];
            nuint length;
            if (TDictionary.Mode != ExtDictionary.Value || matchIndex >= state.DictLimit)
            {
                byte* match = @base + matchIndex;

                // The four bytes that end one past the best length: a longer match has them.
                if (Read32(match + bestLength - 3) != Read32(ip + bestLength - 3))
                {
                    continue;
                }

                length = Count(ip, match, end);
            }
            else
            {
                // An extDict's index, below the prefix.
                byte* match = state.DictionaryBase + matchIndex;
                if (Read32(match) != Read32(ip))
                {
                    continue;
                }

                length = Count2Segments(ip + 4, match + 4, end, state.DictionaryBase + state.DictLimit, @base + state.DictLimit) + 4;
            }

            if (length > bestLength)
            {
                bestLength = length;
                offBase = OffsetToOffBase(current - matchIndex);
                if (ip + length == end)
                {
                    // The longest possible, and reading further would pass the end.
                    break;
                }
            }
        }

        if (TDictionary.Mode == AttachedDictionary.Value)
        {
            bestLength = SearchDictionary(ref state, ip, end, ref offBase, bestLength, attempts, dictionaryHash);
        }

        return bestLength;
    }

    /// <summary>
    /// The attached dictionary's row, with the attempts the frame's left: its own tables, of the same
    /// row size, hashed without salt (<paramref name="hash"/>); its indices just below the prefix's.
    /// </summary>
    private static nuint SearchDictionary(ref MatchState state, byte* ip, byte* end, ref nuint offBase, nuint bestLength, uint attempts, nuint hash)
    {
        nuint rowMask = ((nuint)1 << TRow.Log) - 1;
        MatchState* dictionary = state.Dictionary;
        nuint relativeRow = RowOf(hash);
        byte* tagRow = dictionary->TagTable + relativeRow;
        uint* row = dictionary->HashTable + relativeRow;
        uint lowestIndex = dictionary->DictLimit;
        byte* dictionaryBase = dictionary->Base;
        byte* dictionaryEnd = dictionaryBase + dictionary->End;
        uint indexDelta = state.DictLimit - dictionary->End;
        byte* prefixStart = state.Base + state.DictLimit;
        uint current = (uint)(ip - state.Base);
        uint head = tagRow[0] & (uint)rowMask;
        nuint headGrouped = (nuint)head << MaskGroupShift;
        ulong matches = MatchMask(tagRow, (byte)hash, head);
        uint* candidates = state.Candidates;
        nuint count = 0;
        uint read = 0;
        for (; (matches != 0) & (attempts > 0); matches &= matches - 1)
        {
            nuint matchPosition = ((headGrouped + (nuint)(uint)BitOperations.TrailingZeroCount(matches)) >> MaskGroupShift) & rowMask;
            if (matchPosition == 0)
            {
                continue;
            }

            uint matchIndex = row[matchPosition];
            if (matchIndex < lowestIndex)
            {
                break;
            }

            PrefetchCandidate(dictionaryBase + matchIndex, ref read);
            candidates[count++] = matchIndex;
            attempts--;
        }

        state.Touched += read;
        for (nuint k = 0; k < count; k++)
        {
            uint matchIndex = candidates[k];
            byte* match = dictionaryBase + matchIndex;
            if (Read32(match) != Read32(ip))
            {
                continue;
            }

            nuint length = Count2Segments(ip + 4, match + 4, end, dictionaryEnd, prefixStart) + 4;
            if (length > bestLength)
            {
                bestLength = length;
                offBase = OffsetToOffBase(current - (matchIndex + indexDelta));
                if (ip + length == end)
                {
                    break;
                }
            }
        }

        return bestLength;
    }

    public static void FillHashCache(ref MatchState state, uint index, byte* limit)
    {
        byte* @base = state.Base;
        uint* cache = state.HashCache;
        ulong multiplier = state.RowHashMultiplier;
        ulong salt = state.RowHashSalt;
        int shift = state.RowHashShift;
        uint read = 0;
        uint available = @base + index > limit ? 0 : (uint)(limit - (@base + index) + 1);
        uint end = index + Math.Min(LazyMatchFinder.RowHashCacheSize, available);
        for (; index < end; index++)
        {
            nuint hash = (nuint)(((Read64(@base + index) * multiplier) ^ salt) >> shift);
            cache[index & (LazyMatchFinder.RowHashCacheSize - 1)] = (uint)hash;
            nuint relativeRow = RowOf(hash);
            PrefetchRow(state.TagTable, state.HashTable, relativeRow, ref read, write: true);
        }

        state.Touched += read;
    }

    /// <summary>
    /// The rare part of libzstd's <c>ZSTD_row_update_internal</c>, after a match so long that most of
    /// its positions are skipped: its first 96 inserted, the cache filled again at its last 32.
    /// </summary>
    /// <returns>The first of the last 32, where the insertion resumes.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint UpdateAfterLongMatch(ref MatchState state, uint index, byte* ip)
    {
        const uint MaxMatchStartPositionsToUpdate = 96;
        const uint MaxMatchEndPositionsToUpdate = 32;
        byte* @base = state.Base;
        uint* cache = state.HashCache;
        ulong multiplier = state.RowHashMultiplier;
        ulong salt = state.RowHashSalt;
        int shift = state.RowHashShift;
        uint read = 0;
        for (uint end = index + MaxMatchStartPositionsToUpdate; index < end; index++)
        {
            nuint hash = NextCachedHash(cache, @base, index, multiplier, salt, shift, state.TagTable, state.HashTable, ref read);
            Insert(state.TagTable, state.HashTable, hash, index);
        }

        state.Touched += read;
        uint resume = (uint)(ip - @base) - MaxMatchEndPositionsToUpdate;
        FillHashCache(ref state, resume, ip + 1);
        return resume;
    }

    /// <summary>libzstd's <c>ZSTD_row_update_internalImpl</c> for one position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Insert(byte* tagTable, uint* hashTable, nuint hash, uint index)
    {
        nuint relativeRow = RowOf(hash);
        byte* tagRow = tagTable + relativeRow;
        nuint position = NextIndex(tagRow);
        tagRow[position] = (byte)hash;
        hashTable[relativeRow + position] = index;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_row_nextCachedHash</c>: the hash of <paramref name="index"/>, from the cache,
    /// which takes the hash of the position eight further in its place, and brings its rows in
    /// ahead (see the remarks).
    /// </summary>
    /// <remarks>Hashes are native integers, the shift leaving them 32 bits: no extension before their rows.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint NextCachedHash(
        uint* cache, byte* @base, uint index, ulong multiplier, ulong salt, int shift, byte* tagTable, uint* hashTable, ref uint read)
    {
        uint* slot = cache + (index & (LazyMatchFinder.RowHashCacheSize - 1));
        nuint hash = *slot;
        nuint next = (nuint)(((Read64(@base + index + LazyMatchFinder.RowHashCacheSize) * multiplier) ^ salt) >> shift);
        *slot = (uint)next;
        nuint nextRow = RowOf(next);
        PrefetchRow(tagTable, hashTable, nextRow, ref read, write: true);
        return hash;
    }

    /// <summary>
    /// Brings a row's entries and tags on their way. On x64, prefetches, as libzstd's
    /// <c>ZSTD_row_prefetch</c>: they retire at once and wait on nothing. Without them (Arm64, see the
    /// remarks), the row of tags is read, summed into <paramref name="read"/>, and the row of entries
    /// written at its place 0 when it is the frame's own (<paramref name="write"/>), read otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchRow(byte* tagTable, uint* hashTable, nuint relativeRow, ref uint read, bool write)
    {
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(hashTable + relativeRow);
            if (TRow.Log >= 5)
            {
                Sse.Prefetch0(hashTable + relativeRow + 16);
            }

            Sse.Prefetch0(tagTable + relativeRow);
            if (TRow.Log == 6)
            {
                Sse.Prefetch0(tagTable + relativeRow + 32);
            }

            return;
        }

        read += tagTable[relativeRow];
        if (write)
        {
            hashTable[relativeRow] = 0;
        }
        else
        {
            read += hashTable[relativeRow];
        }
    }

    /// <summary>
    /// Brings a candidate's first bytes on their way before it is compared: a prefetch on x64, as
    /// libzstd's; a load summed into <paramref name="read"/> elsewhere.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchCandidate(byte* candidate, ref uint read)
    {
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(candidate);
            return;
        }

        read += *candidate;
    }

    /// <summary>
    /// The first entry of a hash's row: its bits above the tag, times the entries of a row, as one
    /// shift and one mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint RowOf(nuint hash) =>
        (hash >> (LazyMatchFinder.RowHashTagBits - TRow.Log)) & ~(((nuint)1 << TRow.Log) - 1);

    /// <summary>
    /// libzstd's <c>ZSTD_row_nextIndex</c>: the place of a new entry, one below the head, wrapping past
    /// place 0 (the head's own byte); it becomes the head.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint NextIndex(byte* tagRow)
    {
        // Past place 0 by the sign of next - 1: as a select, a branch in a loop.
        nuint rowMask = ((nuint)1 << TRow.Log) - 1;
        nuint next = ((nuint)tagRow[0] - 1) & rowMask;
        next += rowMask & (nuint)((nint)(next - 1) >> 63);
        tagRow[0] = (byte)next;
        return next;
    }

    /// <summary>
    /// The bits of <see cref="MatchMask"/> per entry, as a shift: libzstd's
    /// <c>ZSTD_row_matchMaskGroupWidth</c>, 4 for rows of 16 entries on arm64.
    /// </summary>
    private static int MaskGroupShift
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TRow.Log == 4 && AdvSimd.IsSupported ? 2 : 0;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_row_getMatchMask</c>: a group of bits for each entry whose tag is
    /// <paramref name="tag"/>, its lowest set, rotated so that group k is the entry k places from
    /// the head, the newest first. A row of 16 on arm64 is libzstd's NEON one: the compare narrowed
    /// to four bits an entry, 5 instructions where extracting a bit per byte takes 9. Rows of 32
    /// and 64 take a bit an entry, from pairwise sums.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MatchMask(byte* tagRow, byte tag, uint head)
    {
        Vector128<byte> tags = Vector128.Create(tag);
        if (TRow.Log == 4 && AdvSimd.IsSupported)
        {
            Vector128<byte> equal = AdvSimd.CompareEqual(AdvSimd.LoadVector128(tagRow), tags);
            ulong nibbles = AdvSimd.ShiftRightLogicalNarrowingLower(equal.AsUInt16(), 4).AsUInt64().ToScalar();
            return BitOperations.RotateRight(nibbles, (int)(head << 2)) & 0x1111111111111111;
        }

        if (TRow.Log >= 5 && AdvSimd.Arm64.IsSupported)
        {
            // A bit an entry: each compare weighed by its bit within its group of eight, then
            // pairwise sums, which carry nothing, fold the groups into bytes of the mask.
            Vector128<byte> weights = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            Vector128<byte> first = AdvSimd.And(AdvSimd.CompareEqual(AdvSimd.LoadVector128(tagRow), tags), weights);
            Vector128<byte> second = AdvSimd.And(AdvSimd.CompareEqual(AdvSimd.LoadVector128(tagRow + 16), tags), weights);
            Vector128<byte> pairs = AdvSimd.Arm64.AddPairwise(first, second);
            if (TRow.Log == 5)
            {
                pairs = AdvSimd.Arm64.AddPairwise(pairs, pairs);
                pairs = AdvSimd.Arm64.AddPairwise(pairs, pairs);
                return BitOperations.RotateRight(pairs.AsUInt32().ToScalar(), (int)head);
            }

            Vector128<byte> third = AdvSimd.And(AdvSimd.CompareEqual(AdvSimd.LoadVector128(tagRow + 32), tags), weights);
            Vector128<byte> fourth = AdvSimd.And(AdvSimd.CompareEqual(AdvSimd.LoadVector128(tagRow + 48), tags), weights);
            Vector128<byte> quads = AdvSimd.Arm64.AddPairwise(pairs, AdvSimd.Arm64.AddPairwise(third, fourth));
            quads = AdvSimd.Arm64.AddPairwise(quads, quads);
            return BitOperations.RotateRight(quads.AsUInt64().ToScalar(), (int)head);
        }

        if (TRow.Log == 4)
        {
            uint mask = Vector128.Equals(Vector128.Load(tagRow), tags).ExtractMostSignificantBits();
            return ((mask >> (int)head) | (mask << (16 - (int)head))) & 0xFFFF;
        }

        if (TRow.Log == 5)
        {
            uint mask = Vector128.Equals(Vector128.Load(tagRow), tags).ExtractMostSignificantBits()
                | (Vector128.Equals(Vector128.Load(tagRow + 16), tags).ExtractMostSignificantBits() << 16);
            return BitOperations.RotateRight(mask, (int)head);
        }

        ulong wide = Vector128.Equals(Vector128.Load(tagRow), tags).ExtractMostSignificantBits()
            | ((ulong)Vector128.Equals(Vector128.Load(tagRow + 16), tags).ExtractMostSignificantBits() << 16)
            | ((ulong)Vector128.Equals(Vector128.Load(tagRow + 32), tags).ExtractMostSignificantBits() << 32)
            | ((ulong)Vector128.Equals(Vector128.Load(tagRow + 48), tags).ExtractMostSignificantBits() << 48);
        return BitOperations.RotateRight(wide, (int)head);
    }
}
