using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's lazy parser without a dictionary (<c>ZSTD_compressBlock_lazy_generic</c>): at each
/// position the best match a search finds, kept (greedy) or weighed against those of the next one
/// (lazy) or two (lazy2) positions. The search is a hash chain (<c>ZSTD_HcFindBestMatch</c>) or the
/// row-based match finder (<c>ZSTD_RowFindBestMatch</c>), which libzstd uses on arm64 and x64 once the
/// window passes 16 KiB.
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
    public static nuint CompressBlock(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
    {
        int minMatch = Math.Clamp(state.Parameters.MinMatch, 4, 6);
        int depth = state.Parameters.Strategy - Strategy.Greedy;
        if (!state.Parameters.UsesRowMatchFinder)
        {
            return minMatch switch
            {
                5 => ByDepth<HashChainSearch<Hash5>>(ref state, store, rep, source, size, depth),
                6 => ByDepth<HashChainSearch<Hash6>>(ref state, store, rep, source, size, depth),
                _ => ByDepth<HashChainSearch<Hash4>>(ref state, store, rep, source, size, depth),
            };
        }

        return (minMatch, Math.Clamp(state.Parameters.SearchLog, 4, 6)) switch
        {
            (4, 4) => ByDepth<RowSearch<Hash4, Row16>>(ref state, store, rep, source, size, depth),
            (4, 5) => ByDepth<RowSearch<Hash4, Row32>>(ref state, store, rep, source, size, depth),
            (4, _) => ByDepth<RowSearch<Hash4, Row64>>(ref state, store, rep, source, size, depth),
            (5, 4) => ByDepth<RowSearch<Hash5, Row16>>(ref state, store, rep, source, size, depth),
            (5, 5) => ByDepth<RowSearch<Hash5, Row32>>(ref state, store, rep, source, size, depth),
            (5, _) => ByDepth<RowSearch<Hash5, Row64>>(ref state, store, rep, source, size, depth),
            (_, 4) => ByDepth<RowSearch<Hash6, Row16>>(ref state, store, rep, source, size, depth),
            (_, 5) => ByDepth<RowSearch<Hash6, Row32>>(ref state, store, rep, source, size, depth),
            _ => ByDepth<RowSearch<Hash6, Row64>>(ref state, store, rep, source, size, depth),
        };
    }

    private static nuint ByDepth<TSearch>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size, int depth)
        where TSearch : struct, ILazySearch =>
        depth switch
        {
            0 => CompressBlock<TSearch, Depth0>(ref state, store, rep, source, size),
            1 => CompressBlock<TSearch, Depth1>(ref state, store, rep, source, size),
            _ => CompressBlock<TSearch, Depth2>(ref state, store, rep, source, size),
        };

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
        byte* prefixLowest = @base + state.DictLimit;

        uint offset1 = rep[0];
        uint offset2 = rep[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        ip += ip == prefixLowest ? 1 : 0;
        {
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
            if ((offset1 > 0) & (Read32(ip + 1 - offset1) == Read32(ip + 1)))
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
                    if ((offset1 > 0) & (Read32(ip) == Read32(ip - offset1)))
                    {
                        nuint repLength = Count(ip + 4, ip + 4 - offset1, iend) + 4;
                        int gain2 = (int)(repLength * 3);
                        int gain1 = (int)(((nint)matchLength * 3) - HighBit(offBase) + 1);
                        if ((repLength >= 4) & (gain2 > gain1))
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
                        if ((length >= 4) & (gain2 > gain1))
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
                        if ((offset1 > 0) & (Read32(ip) == Read32(ip - offset1)))
                        {
                            nuint repLength = Count(ip + 4, ip + 4 - offset1, iend) + 4;
                            int gain2 = (int)(repLength * 4);
                            int gain1 = (int)(((nint)matchLength * 4) - HighBit(offBase) + 1);
                            if ((repLength >= 4) & (gain2 > gain1))
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
                            if ((length >= 4) & (gain2 > gain1))
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
                while (((start > anchor) & (start - offset > prefixLowest)) && start[-1] == (start - offset)[-1])
                {
                    start--;
                    matchLength++;
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
            while (((ip <= ilimit) & (offset2 > 0)) && Read32(ip) == Read32(ip - offset2))
            {
                matchLength = Count(ip + 4, ip + 4 - offset2, iend) + 4;
                (offset1, offset2) = (offset2, offset1);
                SequenceStore.StoreOnly(ref sequence, counts, 0, RepeatCode1, matchLength);
                ip += matchLength;
                anchor = ip;
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

    /// <summary>
    /// The longest match at <paramref name="ip"/>, the positions before it inserted first: its length
    /// (3 when there is none of 4 bytes), its offset code into <paramref name="offBase"/> when found.
    /// </summary>
    static abstract nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase);

    /// <summary>libzstd's <c>ZSTD_row_fillHashCache</c>: the hashes of the positions from <paramref name="index"/>.</summary>
    static abstract void FillHashCache(ref MatchState state, uint index, byte* limit);
}

/// <summary>
/// libzstd's hash chains (<c>ZSTD_HcFindBestMatch</c>): the last position of each hash in the hash
/// table, each position's predecessor of the same hash in the chain table, followed up to
/// 2^searchLog candidates.
/// </summary>
internal readonly unsafe struct HashChainSearch<THash> : ILazySearch
    where THash : IMatchHash
{
    public static bool UsesRows => false;

    public static void FillHashCache(ref MatchState state, uint index, byte* limit)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        uint* chainTable = state.ChainTable;
        uint chainSize = 1u << state.Parameters.ChainLog;
        uint chainMask = chainSize - 1;
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        uint lowLimit = state.LowestMatchIndex(current);
        uint minChain = current > chainSize ? current - chainSize : 0;
        uint attempts = 1u << state.Parameters.SearchLog;
        nuint bestLength = 4 - 1;

        uint matchIndex = InsertAndFindFirstIndex(ref state, ip);
        for (; (matchIndex >= lowLimit) & (attempts > 0); attempts--)
        {
            nuint length = 0;
            byte* match = @base + matchIndex;

            // The four bytes that end one past the best length: a longer match has them.
            if (Read32(match + bestLength - 3) == Read32(ip + bestLength - 3))
            {
                length = Count(ip, match, end);
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
    /// </summary>
    private static uint InsertAndFindFirstIndex(ref MatchState state, byte* ip)
    {
        uint* hashTable = state.HashTable;
        int hashLog = state.Parameters.HashLog;
        uint* chainTable = state.ChainTable;
        uint chainMask = (1u << state.Parameters.ChainLog) - 1;
        byte* @base = state.Base;
        uint target = (uint)(ip - @base);
        uint index = state.NextToUpdate;
        bool skipping = state.LazySkipping;
        while (index < target)
        {
            nuint hash = THash.Hash(Read64(@base + index), hashLog);
            chainTable[index & chainMask] = hashTable[hash];
            hashTable[hash] = index;
            index++;
            if (skipping)
            {
                break;
            }
        }

        state.NextToUpdate = target;
        return hashTable[THash.Hash(Read64(ip), hashLog)];
    }
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
/// libzstd salts its hash with a value that changes at every reset of a context, so that the entries
/// left by earlier frames seldom match. The salt is XORed into the hash before its shift: it relabels
/// the rows and the tags, without changing which positions share them, and an entry from an earlier
/// frame is older than any of the frame's own in its row, where the search stops at the first entry
/// below the window. The frames are the same without it, as libzstd's own are from a fresh context
/// and a reused one.
/// </para>
/// <para>
/// The first byte of each row of tags is the row's head, the newest entry's place: the entries fill
/// places rowEntries - 1 down to 1 and wrap, place 0 never holding one.
/// </para>
/// </remarks>
internal readonly unsafe struct RowSearch<THash, TRow> : ILazySearch
    where THash : IMatchHash
    where TRow : IRowLog
{
    public static bool UsesRows => true;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint FindBestMatch(ref MatchState state, byte* ip, byte* end, ref nuint offBase)
    {
        int rowLog = TRow.Log;
        uint rowMask = (1u << rowLog) - 1;
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        uint lowLimit = state.LowestMatchIndex(current);
        uint attempts = 1u << Math.Min(state.Parameters.SearchLog, rowLog);
        nuint bestLength = 4 - 1;

        // The positions before ip inserted, then ip's hash; while skipping, ip's alone.
        uint hash;
        if (!state.LazySkipping)
        {
            Update(ref state, ip);
            hash = NextCachedHash(ref state, current);
        }
        else
        {
            hash = Hash(ip, state.RowHashLog);
            state.NextToUpdate = current;
        }

        uint relativeRow = (hash >> LazyMatchFinder.RowHashTagBits) << rowLog;
        byte tag = (byte)hash;
        uint* row = state.HashTable + relativeRow;
        byte* tagRow = state.TagTable + relativeRow;
        uint head = tagRow[0] & rowMask;

        // The candidates, newest first, up to the attempts allowed or the first below the window.
        // libzstd gathers them, inserts ip, then compares them: ip's insertion changes neither, so
        // they are compared as they come.
        for (ulong matches = MatchMask(tagRow, tag, head); (matches != 0) & (attempts > 0); matches &= matches - 1)
        {
            uint matchPosition = (head + (uint)BitOperations.TrailingZeroCount(matches)) & rowMask;
            if (matchPosition == 0)
            {
                continue;
            }

            uint matchIndex = row[matchPosition];
            if (matchIndex < lowLimit)
            {
                break;
            }

            attempts--;
            byte* match = @base + matchIndex;
            nuint length = 0;
            if (Read32(match + bestLength - 3) == Read32(ip + bestLength - 3))
            {
                length = Count(ip, match, end);
            }

            if (length > bestLength)
            {
                bestLength = length;
                offBase = OffsetToOffBase(current - matchIndex);
                if (ip + length == end)
                {
                    // libzstd stops comparing here, its gathering already done: the remaining
                    // candidates only count against the attempts, which no longer matter.
                    break;
                }
            }
        }

        // ip itself, in its row.
        {
            uint position = NextIndex(tagRow, rowMask);
            tagRow[position] = tag;
            row[position] = state.NextToUpdate++;
        }

        return bestLength;
    }

    public static void FillHashCache(ref MatchState state, uint index, byte* limit)
    {
        byte* @base = state.Base;
        uint* cache = state.HashCache;
        int hashLog = state.RowHashLog;
        uint available = @base + index > limit ? 0 : (uint)(limit - (@base + index) + 1);
        uint end = index + Math.Min(LazyMatchFinder.RowHashCacheSize, available);
        for (; index < end; index++)
        {
            cache[index & (LazyMatchFinder.RowHashCacheSize - 1)] = Hash(@base + index, hashLog);
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_row_update_internal</c> with its cache: the positions up to <paramref name="ip"/>
    /// (excluded) into their rows; after a long match only its first 96 and its last 32.
    /// </summary>
    private static void Update(ref MatchState state, byte* ip)
    {
        const uint SkipThreshold = 384;
        const uint MaxMatchStartPositionsToUpdate = 96;
        const uint MaxMatchEndPositionsToUpdate = 32;
        uint index = state.NextToUpdate;
        uint target = (uint)(ip - state.Base);
        if (target - index > SkipThreshold)
        {
            UpdateRange(ref state, index, index + MaxMatchStartPositionsToUpdate);
            index = target - MaxMatchEndPositionsToUpdate;
            FillHashCache(ref state, index, ip + 1);
        }

        UpdateRange(ref state, index, target);
        state.NextToUpdate = target;
    }

    /// <summary>libzstd's <c>ZSTD_row_update_internalImpl</c> with its cache.</summary>
    private static void UpdateRange(ref MatchState state, uint index, uint end)
    {
        int rowLog = TRow.Log;
        uint rowMask = (1u << rowLog) - 1;
        uint* hashTable = state.HashTable;
        byte* tagTable = state.TagTable;
        for (; index < end; index++)
        {
            uint hash = NextCachedHash(ref state, index);
            uint relativeRow = (hash >> LazyMatchFinder.RowHashTagBits) << rowLog;
            byte* tagRow = tagTable + relativeRow;
            uint position = NextIndex(tagRow, rowMask);
            tagRow[position] = (byte)hash;
            hashTable[relativeRow + position] = index;
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_row_nextCachedHash</c>: the hash of <paramref name="index"/>, from the cache,
    /// which takes the hash of the position eight further in its place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NextCachedHash(ref MatchState state, uint index)
    {
        uint* slot = state.HashCache + (index & (LazyMatchFinder.RowHashCacheSize - 1));
        uint hash = *slot;
        *slot = Hash(state.Base + index + LazyMatchFinder.RowHashCacheSize, state.RowHashLog);
        return hash;
    }

    /// <summary>libzstd's <c>ZSTD_hashPtrSalted</c> unsalted (see the remarks): the row and the tag in one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(byte* p, int rowHashLog) =>
        (uint)THash.Hash(Read64(p), rowHashLog + LazyMatchFinder.RowHashTagBits);

    /// <summary>
    /// libzstd's <c>ZSTD_row_nextIndex</c>: the place of a new entry, one below the head, wrapping past
    /// place 0 (the head's own byte); it becomes the head.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NextIndex(byte* tagRow, uint rowMask)
    {
        uint next = (uint)(tagRow[0] - 1) & rowMask;
        next += next == 0 ? rowMask : 0;
        tagRow[0] = (byte)next;
        return next;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_row_getMatchMask</c>: a bit for each entry whose tag is <paramref name="tag"/>,
    /// rotated so that bit k is the entry k places from the head, the newest first.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MatchMask(byte* tagRow, byte tag, uint head)
    {
        Vector128<byte> tags = Vector128.Create(tag);
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
