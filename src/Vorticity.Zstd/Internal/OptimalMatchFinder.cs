using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>A match the optimal parser weighs: libzstd's <c>ZSTD_match_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OptimalMatch
{
    public uint OffBase;
    public uint Length;
}

/// <summary>
/// A position of the optimal parser's forward pass: libzstd's <c>ZSTD_optimal_t</c>, the cheapest way
/// found to reach it, as a "stretch": a match, then literals.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OptimalNode
{
    public int Price;
    public uint OffBase;
    public uint MatchLength;
    public uint LiteralLength;
    public uint Rep0;
    public uint Rep1;
    public uint Rep2;
}

/// <summary>
/// libzstd's <c>optState_t</c>: the statistics the optimal parser prices symbols with, carried from
/// block to block of a frame, and its working tables.
/// </summary>
internal sealed unsafe class OptimalState
{
    /// <summary>libzstd's <c>ZSTD_OPT_NUM</c>: the longest stretch the forward pass prices.</summary>
    public const int OptNum = 1 << 12;

    /// <summary>libzstd's <c>ZSTD_OPT_SIZE</c>.</summary>
    public const int OptSize = OptNum + 3;

    private readonly uint[] _frequencies = GC.AllocateArray<uint>(256 + 36 + 53 + 32, pinned: true);
    private readonly OptimalMatch[] _matches = GC.AllocateArray<OptimalMatch>(OptSize, pinned: true);
    private readonly OptimalNode[] _nodes = GC.AllocateArray<OptimalNode>(OptSize, pinned: true);

    public OptimalState()
    {
        LiteralFrequencies = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_frequencies));
        LiteralLengthFrequencies = LiteralFrequencies + 256;
        MatchLengthFrequencies = LiteralLengthFrequencies + 36;
        OffCodeFrequencies = MatchLengthFrequencies + 53;
        Matches = (OptimalMatch*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_matches));
        Nodes = (OptimalNode*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_nodes));
    }

    public uint* LiteralFrequencies { get; }

    public uint* LiteralLengthFrequencies { get; }

    public uint* MatchLengthFrequencies { get; }

    public uint* OffCodeFrequencies { get; }

    /// <summary>libzstd's <c>matchTable</c>: the matches found at a position, by increasing length.</summary>
    public OptimalMatch* Matches { get; }

    /// <summary>libzstd's <c>priceTable</c>: the forward pass's positions.</summary>
    public OptimalNode* Nodes { get; }

    public uint LiteralSum;

    /// <summary>libzstd's <c>litLengthSum</c>: 0 until a frame's first block sets the statistics.</summary>
    public uint LiteralLengthSum;

    public uint MatchLengthSum;

    public uint OffCodeSum;

    public uint LiteralSumBasePrice;

    public uint LiteralLengthSumBasePrice;

    public uint MatchLengthSumBasePrice;

    public uint OffCodeSumBasePrice;

    /// <summary>libzstd's <c>zop_predef</c>: prices from fixed estimates, for a first block of 8 bytes or less.</summary>
    public bool Predefined;

    /// <summary>The block's long-distance matches (libzstd's <c>ldmSeqStore</c>), none unless the matcher is on.</summary>
    public LongDistanceCursor LongDistance;

    /// <summary>
    /// libzstd's <c>symbolCosts</c>: the tables of the block before, which a frame's first block,
    /// when a dictionary's, takes its statistics from.
    /// </summary>
    public BlockState? SymbolCosts;
}

/// <summary>The minimum length of the optimal parser's matches: libzstd's <c>mls</c>, 3 to 6.</summary>
internal interface IOptimalLength
{
    static abstract int MinLength { get; }

    /// <summary>The hash of the binary tree: 4 bytes for a minimum of 3 (libzstd's <c>ZSTD_hashPtr</c>).</summary>
    static abstract nuint Hash(ulong bytes, int hashLog);
}

internal readonly struct OptimalLength3 : IOptimalLength
{
    public static int MinLength => 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => Hash4.Hash(bytes, hashLog);
}

internal readonly struct OptimalLength4 : IOptimalLength
{
    public static int MinLength => 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => Hash4.Hash(bytes, hashLog);
}

internal readonly struct OptimalLength5 : IOptimalLength
{
    public static int MinLength => 5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => Hash5.Hash(bytes, hashLog);
}

internal readonly struct OptimalLength6 : IOptimalLength
{
    public static int MinLength => 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => Hash6.Hash(bytes, hashLog);
}

/// <summary>libzstd's <c>optLevel</c>: 0 for btopt, whole-bit prices; 2 for btultra, fractional ones.</summary>
internal interface IOptimalLevel
{
    static abstract int Level { get; }
}

internal readonly struct OptimalLevel0 : IOptimalLevel
{
    public static int Level => 0;
}

internal readonly struct OptimalLevel2 : IOptimalLevel
{
    public static int Level => 2;
}

/// <summary>Whether the optimal parser weighs the block's long-distance matches too.</summary>
internal interface IOptimalLongDistance
{
    static abstract bool Enabled { get; }
}

internal readonly struct WithoutLongDistance : IOptimalLongDistance
{
    public static bool Enabled => false;
}

internal readonly struct WithLongDistance : IOptimalLongDistance
{
    public static bool Enabled => true;
}

/// <summary>
/// libzstd's optimal parser (zstd_opt.c) without a dictionary: the btopt, btultra and btultra2
/// strategies, which weigh the long-distance matcher's matches too when it is on.
/// </summary>
internal static unsafe class OptimalMatchFinder
{
    /// <summary>libzstd's <c>ZSTD_HASHLOG3_MAX</c>: the 3-byte hash table of a minimum length of 3.</summary>
    public const int HashLog3Max = 17;

    /// <summary>The block's sequences into <paramref name="store"/>, for the strategy and minimum length of the parameters.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlock(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size) =>
        Dispatch<NoDictionary>(ref state, optimal, store, rep, source, size);

    /// <summary>
    /// The block's sequences with a dictionary: below the prefix as a segment of its own
    /// (<paramref name="extDict"/>), or attached.
    /// </summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlockWithDictionary(
        ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size, bool extDict) =>
        extDict
            ? Dispatch<ExtDictionary>(ref state, optimal, store, rep, source, size)
            : Dispatch<AttachedDictionary>(ref state, optimal, store, rep, source, size);

    private static nuint Dispatch<TDictionary>(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size)
        where TDictionary : IDictionaryMode =>
        Math.Clamp(state.Parameters.MinMatch, 3, 6) switch
        {
            3 => ByStrategy<OptimalLength3, TDictionary>(ref state, optimal, store, rep, source, size),
            4 => ByStrategy<OptimalLength4, TDictionary>(ref state, optimal, store, rep, source, size),
            5 => ByStrategy<OptimalLength5, TDictionary>(ref state, optimal, store, rep, source, size),
            _ => ByStrategy<OptimalLength6, TDictionary>(ref state, optimal, store, rep, source, size),
        };

    /// <summary>
    /// libzstd's <c>ZSTD_updateTree</c>, for a dictionary's content: every position up to
    /// <paramref name="ip"/> sorted into the binary tree, as the optimal parsers insert them (btlazy2
    /// loads its dictionaries this way too).
    /// </summary>
    public static void FillTree(ref MatchState state, byte* ip, byte* end)
    {
        switch (Math.Clamp(state.Parameters.MinMatch, 3, 6))
        {
            case 3: OptimalParser<OptimalLength3, OptimalLevel0, NoDictionary>.FillTree(ref state, ip, end); break;
            case 4: OptimalParser<OptimalLength4, OptimalLevel0, NoDictionary>.FillTree(ref state, ip, end); break;
            case 5: OptimalParser<OptimalLength5, OptimalLevel0, NoDictionary>.FillTree(ref state, ip, end); break;
            default: OptimalParser<OptimalLength6, OptimalLevel0, NoDictionary>.FillTree(ref state, ip, end); break;
        }
    }

    private static nuint ByStrategy<TLength, TDictionary>(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size)
        where TLength : struct, IOptimalLength
        where TDictionary : IDictionaryMode
    {
        switch (state.Parameters.Strategy)
        {
            case Strategy.BinaryTreeOptimal:
                return OptimalParser<TLength, OptimalLevel0, TDictionary>.CompressBlock(ref state, optimal, store, rep, source, size);
            case Strategy.BinaryTreeUltra:
                return OptimalParser<TLength, OptimalLevel2, TDictionary>.CompressBlock(ref state, optimal, store, rep, source, size);
            default:
            {
                // btultra2: a first pass over the frame's first block, to seed the statistics; with a
                // dictionary, libzstd runs btultra's parser instead (ZSTD_selectBlockCompressor).
                uint current = (uint)(source - state.Base);
                if (TDictionary.Mode == NoDictionary.Value
                    && (optimal.LiteralLengthSum == 0) && (state.DictLimit == state.LowLimit) && (current == state.DictLimit) && (size > 8))
                {
                    InitializeStatistics<TLength>(ref state, optimal, store, rep, source, size);
                }

                return OptimalParser<TLength, OptimalLevel2, TDictionary>.CompressBlock(ref state, optimal, store, rep, source, size);
            }
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_initStats_ultra</c>: the block compressed once for its statistics alone, then
    /// forgotten: its sequences dropped, the window moved past its indices, so that its entries in
    /// the tables lie below it.
    /// </summary>
    private static void InitializeStatistics<TLength>(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size)
        where TLength : struct, IOptimalLength
    {
        uint* repeats = stackalloc uint[3];
        repeats[0] = rep[0];
        repeats[1] = rep[1];
        repeats[2] = rep[2];
        OptimalParser<TLength, OptimalLevel2, NoDictionary>.CompressBlock(ref state, optimal, store, repeats, source, size);

        store.Reset();
        state.Base -= size;
        state.DictLimit += (uint)size;
        state.LowLimit = state.DictLimit;
        state.NextToUpdate = state.DictLimit;
    }
}

/// <summary>libzstd's <c>ZSTD_compressBlock_opt_generic</c> for one minimum length and one level.</summary>
internal static unsafe class OptimalParser<TLength, TLevel, TDictionary>
    where TLength : struct, IOptimalLength
    where TLevel : struct, IOptimalLevel
    where TDictionary : IDictionaryMode
{
    private const int BitCostAccuracy = 8;
    private const int BitCostMultiplier = 1 << BitCostAccuracy;
    private const int MaxPrice = 1 << 30;
    private const uint LiteralFrequencyAdd = 2;
    private const int PredefinedThreshold = 8;
    private const int OptNum = OptimalState.OptNum;

    private static ReadOnlySpan<byte> LiteralLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    private static ReadOnlySpan<byte> MatchLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    private static ReadOnlySpan<byte> LiteralLengthCodes =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 20, 20, 21, 21, 21, 21,
        22, 22, 22, 22, 22, 22, 22, 22, 23, 23, 23, 23, 23, 23, 23, 23,
        24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24, 24,
    ];

    private static ReadOnlySpan<byte> MatchLengthCodes =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 32, 33, 33, 34, 34, 35, 35, 36, 36, 36, 36, 37, 37, 37, 37,
        38, 38, 38, 38, 38, 38, 38, 38, 39, 39, 39, 39, 39, 39, 39, 39,
        40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40,
        41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41,
        42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42,
        42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42,
    ];

    private static ReadOnlySpan<uint> BaseLiteralLengthFrequencies =>
    [
        4, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1,
    ];

    private static ReadOnlySpan<uint> BaseOffCodeFrequencies =>
    [
        6, 2, 1, 1, 2, 3, 4, 4, 4, 3, 2, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_opt_generic</c> without a dictionary. A block without
    /// long-distance matches is parsed as if the matcher were off, which it then is to libzstd too.
    /// </summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlock(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size) =>
        optimal.LongDistance.Count == 0
            ? CompressBlock<WithoutLongDistance>(ref state, optimal, store, rep, source, size)
            : CompressBlock<WithLongDistance>(ref state, optimal, store, rep, source, size);

    private static nuint CompressBlock<TLongDistance>(ref MatchState state, OptimalState optimal, SequenceStore store, uint* rep, byte* source, nuint size)
        where TLongDistance : struct, IOptimalLongDistance
    {
        byte* istart = source;
        byte* ip = istart;
        byte* anchor = istart;
        byte* iend = istart + size;
        byte* ilimit = iend - 8;
        byte* @base = state.Base;
        byte* prefixStart = @base + state.DictLimit;
        uint sufficientLength = (uint)Math.Min(state.Parameters.TargetLength, OptNum - 1);
        uint minMatch = state.Parameters.MinMatch == 3 ? 3u : 4u;
        uint nextToUpdate3 = state.NextToUpdate;
        OptimalNode* opt = optimal.Nodes;
        OptimalMatch* matches = optimal.Matches;
        OptimalNode lastStretch = default;

        byte* lit = store.Literals;
        SequenceRecord* sequence = store.Sequences;
        uint* counts = store.Counts;

        if (TLongDistance.Enabled)
        {
            optimal.LongDistance.Begin((uint)size);
        }

        RescaleFrequencies(optimal, source, size);
        ip += ip == prefixStart ? 1 : 0;

        while (ip < ilimit)
        {
            uint cur;
            uint lastPosition;

            // The first match, at ip.
            {
                uint literalLength = (uint)(ip - anchor);
                uint ll0 = literalLength == 0 ? 1u : 0u;
                uint matchCount = GetAllMatches(matches, ref state, ref nextToUpdate3, ip, iend, rep, ll0, minMatch);
                if (TLongDistance.Enabled)
                {
                    matchCount = optimal.LongDistance.Process(matches, matchCount, (uint)(ip - istart), (uint)(iend - ip), minMatch);
                }

                if (matchCount == 0)
                {
                    ip++;
                    continue;
                }

                // The positions are "stretches", a match followed by literals: each literal position
                // of a run may have its own predecessor.
                opt[0].MatchLength = 0;
                opt[0].LiteralLength = literalLength;
                opt[0].Price = LiteralLengthPrice(optimal, literalLength);
                opt[0].Rep0 = rep[0];
                opt[0].Rep1 = rep[1];
                opt[0].Rep2 = rep[2];

                // A long match is taken at once.
                uint maxMatchLength = matches[matchCount - 1].Length;
                uint maxOffBase = matches[matchCount - 1].OffBase;
                if (maxMatchLength > sufficientLength)
                {
                    lastStretch.LiteralLength = 0;
                    lastStretch.MatchLength = maxMatchLength;
                    lastStretch.OffBase = maxOffBase;
                    cur = 0;
                    lastPosition = maxMatchLength;
                    goto ShortestPath;
                }

                // The prices of the first matches, from position 0.
                uint position;
                for (position = 1; position < minMatch; position++)
                {
                    opt[position].Price = MaxPrice;
                    opt[position].MatchLength = 0;
                    opt[position].LiteralLength = literalLength + position;
                }

                for (uint matchIndex = 0; matchIndex < matchCount; matchIndex++)
                {
                    uint offBase = matches[matchIndex].OffBase;
                    uint end = matches[matchIndex].Length;
                    uint offCode = (uint)BitOperations.Log2(offBase);
                    uint offsetPrice = OffsetPrice(optimal, offBase);
                    for (; position <= end; position++)
                    {
                        int matchPrice = (int)(offsetPrice + MatchLengthPrice(optimal, offCode, position));
                        int sequencePrice = opt[0].Price + matchPrice;
                        opt[position].MatchLength = position;
                        opt[position].OffBase = offBase;
                        opt[position].LiteralLength = 0;
                        opt[position].Price = sequencePrice + LiteralLengthPrice(optimal, 0);
                    }
                }

                lastPosition = position - 1;
                opt[position].Price = MaxPrice;
            }

            // The further positions.
            for (cur = 1; cur <= lastPosition; cur++)
            {
                byte* inr = ip + cur;

                // A literal from the previous position, if cheaper.
                {
                    uint literalLength = opt[cur - 1].LiteralLength + 1;
                    int price = opt[cur - 1].Price + LiteralPrice(optimal, ip + cur - 1) + LiteralLengthIncrementPrice(optimal, literalLength);
                    if (price <= opt[cur].Price)
                    {
                        OptimalNode previousMatch = opt[cur];
                        opt[cur] = opt[cur - 1];
                        opt[cur].LiteralLength = literalLength;
                        opt[cur].Price = price;
                        if ((TLevel.Level >= 1)
                            && (previousMatch.LiteralLength == 0)
                            && (LiteralLengthIncrementPrice(optimal, 1) < 0)
                            && (ip + cur < iend))
                        {
                            // The replaced match, then one literal, may be cheaper at the next position.
                            int withOneLiteral = previousMatch.Price + LiteralPrice(optimal, ip + cur) + LiteralLengthIncrementPrice(optimal, 1);
                            int withMoreLiterals = price + LiteralPrice(optimal, ip + cur) + LiteralLengthIncrementPrice(optimal, literalLength + 1);
                            if ((withOneLiteral < withMoreLiterals) && (withOneLiteral < opt[cur + 1].Price))
                            {
                                uint previous = cur - previousMatch.MatchLength;
                                opt[cur + 1] = previousMatch;
                                NewRep(&opt[cur + 1], &opt[previous], previousMatch.OffBase, opt[previous].LiteralLength == 0 ? 1u : 0u);
                                opt[cur + 1].LiteralLength = 1;
                                opt[cur + 1].Price = withOneLiteral;
                                if (lastPosition < cur + 1)
                                {
                                    lastPosition = cur + 1;
                                }
                            }
                        }
                    }
                }

                // A match just ended here: the offset history it leaves.
                if (opt[cur].LiteralLength == 0)
                {
                    uint previous = cur - opt[cur].MatchLength;
                    NewRep(&opt[cur], &opt[previous], opt[cur].OffBase, opt[previous].LiteralLength == 0 ? 1u : 0u);
                }

                // The last match must start 8 bytes or more before the end.
                if (inr > ilimit)
                {
                    continue;
                }

                if (cur == lastPosition)
                {
                    break;
                }

                if ((TLevel.Level == 0) && (opt[cur + 1].Price <= opt[cur].Price + (BitCostMultiplier / 2)))
                {
                    // Unpromising: the next position is already cheaper.
                    continue;
                }

                {
                    uint ll0 = opt[cur].LiteralLength == 0 ? 1u : 0u;
                    int previousPrice = opt[cur].Price;
                    int basePrice = previousPrice + LiteralLengthPrice(optimal, 0);
                    uint* curRep = &opt[cur].Rep0;
                    uint matchCount = GetAllMatches(matches, ref state, ref nextToUpdate3, inr, iend, curRep, ll0, minMatch);
                    if (TLongDistance.Enabled)
                    {
                        matchCount = optimal.LongDistance.Process(matches, matchCount, (uint)(inr - istart), (uint)(iend - inr), minMatch);
                    }

                    if (matchCount == 0)
                    {
                        continue;
                    }

                    uint longestLength = matches[matchCount - 1].Length;
                    if ((longestLength > sufficientLength) || (cur + longestLength >= OptNum) || (ip + cur + longestLength >= iend))
                    {
                        lastStretch.MatchLength = longestLength;
                        lastStretch.OffBase = matches[matchCount - 1].OffBase;
                        lastStretch.LiteralLength = 0;
                        lastPosition = cur + longestLength;
                        goto ShortestPath;
                    }

                    lastPosition = PriceMatches(optimal, opt, matches, matchCount, cur, basePrice, lastPosition, minMatch);
                }

                opt[lastPosition + 1].Price = MaxPrice;
            }

            lastStretch = opt[lastPosition];
            cur = lastPosition - lastStretch.MatchLength;

        ShortestPath:
            if (lastStretch.MatchLength == 0)
            {
                // No solution: only literals.
                ip += lastPosition;
                continue;
            }

            // The offset history after the segment.
            if (lastStretch.LiteralLength == 0)
            {
                OptimalNode reps = default;
                NewRep(&reps, &opt[cur], lastStretch.OffBase, opt[cur].LiteralLength == 0 ? 1u : 0u);
                rep[0] = reps.Rep0;
                rep[1] = reps.Rep1;
                rep[2] = reps.Rep2;
            }
            else
            {
                rep[0] = lastStretch.Rep0;
                rep[1] = lastStretch.Rep1;
                rep[2] = lastStretch.Rep2;
                cur -= lastStretch.LiteralLength;
            }

            // The shortest path, from its end, written back over the positions as sequences: literals
            // then a match. libzstd 1.5.7 first stores the last stretch's literals as a sequence of
            // their own, then overwrites it (a block that should have been an else): the last stretch
            // is always stored whole, its literals left for the next segment, which is what is done.
            {
                uint storeEnd = cur + 2;
                uint storeStart = storeEnd;
                uint stretchPosition = cur;
                opt[storeEnd] = lastStretch;
                while (true)
                {
                    OptimalNode nextStretch = opt[stretchPosition];
                    opt[storeStart].LiteralLength = nextStretch.LiteralLength;
                    if (nextStretch.MatchLength == 0)
                    {
                        break;
                    }

                    storeStart--;
                    opt[storeStart] = nextStretch;
                    stretchPosition -= nextStretch.LiteralLength + nextStretch.MatchLength;
                }

                for (uint storePosition = storeStart; storePosition <= storeEnd; storePosition++)
                {
                    uint literalLength = opt[storePosition].LiteralLength;
                    uint matchLength = opt[storePosition].MatchLength;
                    uint offBase = opt[storePosition].OffBase;
                    if (matchLength == 0)
                    {
                        // Only literals: the last of the segment, starting the next one.
                        ip = anchor + literalLength;
                        continue;
                    }

                    UpdateStatistics(optimal, literalLength, anchor, offBase, matchLength);
                    SequenceStore.Store(ref lit, ref sequence, counts, literalLength, anchor, iend, offBase, matchLength);
                    anchor += literalLength + matchLength;
                    ip = anchor;
                }
            }

            SetBasePrices(optimal);
        }

        store.Literals = lit;
        store.Sequences = sequence;
        return (nuint)(iend - anchor);
    }

    /// <summary>libzstd's <c>ZSTD_newRep</c>: the offset history of <paramref name="from"/>, after a match, into <paramref name="to"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NewRep(OptimalNode* to, OptimalNode* from, uint offBase, uint ll0)
    {
        uint rep0 = from->Rep0;
        uint rep1 = from->Rep1;
        uint rep2 = from->Rep2;
        if (offBase > RepeatCodeCount)
        {
            to->Rep2 = rep1;
            to->Rep1 = rep0;
            to->Rep0 = offBase - RepeatCodeCount;
            return;
        }

        uint repCode = offBase - 1 + ll0;
        if (repCode == 0)
        {
            to->Rep0 = rep0;
            to->Rep1 = rep1;
            to->Rep2 = rep2;
            return;
        }

        uint currentOffset = repCode == RepeatCodeCount ? rep0 - 1 : (repCode == 1 ? rep1 : rep2);
        to->Rep2 = repCode >= 2 ? rep1 : rep2;
        to->Rep1 = rep0;
        to->Rep0 = currentOffset;
    }

    // ---- prices

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Weight(uint stat) => TLevel.Level != 0 ? FractionalWeight(stat) : BitWeight(stat);

    /// <summary>libzstd's <c>ZSTD_bitWeight</c>: a stat's cost in whole bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitWeight(uint stat) => (uint)BitOperations.Log2(stat + 1) * BitCostMultiplier;

    /// <summary>libzstd's <c>ZSTD_fracWeight</c>: a stat's cost in fractional bits, interpolated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FractionalWeight(uint rawStat)
    {
        uint stat = rawStat + 1;
        int highBit = BitOperations.Log2(stat);
        return ((uint)highBit * BitCostMultiplier) + ((stat << BitCostAccuracy) >> highBit);
    }

    /// <summary>libzstd's <c>ZSTD_setBasePrices</c>.</summary>
    private static void SetBasePrices(OptimalState optimal)
    {
        optimal.LiteralSumBasePrice = Weight(optimal.LiteralSum);
        optimal.LiteralLengthSumBasePrice = Weight(optimal.LiteralLengthSum);
        optimal.MatchLengthSumBasePrice = Weight(optimal.MatchLengthSum);
        optimal.OffCodeSumBasePrice = Weight(optimal.OffCodeSum);
    }

    /// <summary>libzstd's <c>ZSTD_downscaleStats</c>.</summary>
    private static uint DownscaleStatistics(uint* table, uint lastIndex, int shift, bool baseOne)
    {
        uint sum = 0;
        for (uint s = 0; s < lastIndex + 1; s++)
        {
            uint @base = baseOne ? 1u : (table[s] > 0 ? 1u : 0u);
            uint stat = @base + (table[s] >> shift);
            sum += stat;
            table[s] = stat;
        }

        return sum;
    }

    /// <summary>libzstd's <c>ZSTD_scaleStats</c>: the frequencies reduced when their sum passes 2^logTarget.</summary>
    private static uint ScaleStatistics(uint* table, uint lastIndex, int logTarget)
    {
        uint previousSum = 0;
        for (uint s = 0; s < lastIndex + 1; s++)
        {
            previousSum += table[s];
        }

        uint factor = previousSum >> logTarget;
        return factor <= 1 ? previousSum : DownscaleStatistics(table, lastIndex, BitOperations.Log2(factor), baseOne: true);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_rescaleFreqs</c> without a dictionary: a frame's first block starts from the
    /// block's own literals and fixed baselines, the next ones from the last's statistics, reduced.
    /// </summary>
    private static void RescaleFrequencies(OptimalState optimal, byte* source, nuint size)
    {
        optimal.Predefined = false;
        if (optimal.LiteralLengthSum == 0)
        {
            if (size <= PredefinedThreshold)
            {
                optimal.Predefined = true;
            }

            if (optimal.SymbolCosts?.HuffmanRepeat == HuffmanRepeat.Valid)
            {
                // A Huffman table of every byte: a dictionary's, whose tables give the statistics.
                optimal.Predefined = false;
                SeedStatistics(optimal, optimal.SymbolCosts);
                SetBasePrices(optimal);
                return;
            }

            uint maxSymbol = 255;
            Histogram.CountSimple(optimal.LiteralFrequencies, ref maxSymbol, source, size);
            optimal.LiteralSum = DownscaleStatistics(optimal.LiteralFrequencies, 255, 8, baseOne: false);

            BaseLiteralLengthFrequencies.CopyTo(new Span<uint>(optimal.LiteralLengthFrequencies, 36));
            optimal.LiteralLengthSum = 0;
            for (int s = 0; s < 36; s++)
            {
                optimal.LiteralLengthSum += optimal.LiteralLengthFrequencies[s];
            }

            for (int s = 0; s < 53; s++)
            {
                optimal.MatchLengthFrequencies[s] = 1;
            }

            optimal.MatchLengthSum = 53;

            BaseOffCodeFrequencies.CopyTo(new Span<uint>(optimal.OffCodeFrequencies, 32));
            optimal.OffCodeSum = 0;
            for (int s = 0; s < 32; s++)
            {
                optimal.OffCodeSum += optimal.OffCodeFrequencies[s];
            }
        }
        else
        {
            optimal.LiteralSum = ScaleStatistics(optimal.LiteralFrequencies, 255, 12);
            optimal.LiteralLengthSum = ScaleStatistics(optimal.LiteralLengthFrequencies, 35, 11);
            optimal.MatchLengthSum = ScaleStatistics(optimal.MatchLengthFrequencies, 52, 11);
            optimal.OffCodeSum = ScaleStatistics(optimal.OffCodeFrequencies, 31, 11);
        }

        SetBasePrices(optimal);
    }

    /// <summary>
    /// The first block's statistics from a dictionary's tables (libzstd's <c>ZSTD_rescaleFreqs</c> with
    /// symbol costs): each symbol as frequent as its code is short, from 2^11 for the literals and
    /// 2^10 for the codes, 1 for a symbol without one.
    /// </summary>
    private static void SeedStatistics(OptimalState optimal, BlockState costs)
    {
        optimal.LiteralSum = 0;
        for (int literal = 0; literal <= 255; literal++)
        {
            int bitCost = HuffmanCTable.NbBits(costs.Huffman.Elements[literal]);
            optimal.LiteralFrequencies[literal] = bitCost != 0 ? 1u << (11 - bitCost) : 1;
            optimal.LiteralSum += optimal.LiteralFrequencies[literal];
        }

        optimal.LiteralLengthSum = Seed(optimal.LiteralLengthFrequencies, costs.LiteralLengths, SequenceCodes.MaxLiteralLength);
        optimal.MatchLengthSum = Seed(optimal.MatchLengthFrequencies, costs.MatchLengths, SequenceCodes.MaxMatchLength);
        optimal.OffCodeSum = Seed(optimal.OffCodeFrequencies, costs.Offsets, SequenceCodes.MaxOffset);

        // libzstd's FSE_getMaxNbBits: a symbol's longest code.
        static uint Seed(uint* frequencies, FseCTable table, int maxSymbol)
        {
            uint sum = 0;
            for (int symbol = 0; symbol <= maxSymbol; symbol++)
            {
                uint bitCost = (table.Symbols[symbol].DeltaNbBits + 0xFFFF) >> 16;
                frequencies[symbol] = bitCost != 0 ? 1u << (10 - (int)bitCost) : 1;
                sum += frequencies[symbol];
            }

            return sum;
        }
    }

    /// <summary>libzstd's <c>ZSTD_rawLiteralsCost</c> of one literal: libzstd's <c>LIT_PRICE</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LiteralPrice(OptimalState optimal, byte* literal)
    {
        if (optimal.Predefined)
        {
            return 6 * BitCostMultiplier;
        }

        uint literalPrice = Weight(optimal.LiteralFrequencies[*literal]);
        uint literalPriceMax = optimal.LiteralSumBasePrice - BitCostMultiplier;
        if (literalPrice > literalPriceMax)
        {
            literalPrice = literalPriceMax;
        }

        return (int)(optimal.LiteralSumBasePrice - literalPrice);
    }

    /// <summary>libzstd's <c>ZSTD_litLengthPrice</c>: libzstd's <c>LL_PRICE</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LiteralLengthPrice(OptimalState optimal, uint literalLength)
    {
        if (optimal.Predefined)
        {
            return (int)Weight(literalLength);
        }

        if (literalLength == FrameFormat.MaxBlockSize)
        {
            // Not representable: one bit more than the largest that is.
            return BitCostMultiplier + LiteralLengthPrice(optimal, FrameFormat.MaxBlockSize - 1);
        }

        uint code = LiteralLengthCode(literalLength);
        return (int)((LiteralLengthBits[(int)code] * (uint)BitCostMultiplier)
            + optimal.LiteralLengthSumBasePrice
            - Weight(optimal.LiteralLengthFrequencies[code]));
    }

    /// <summary>libzstd's <c>LL_INCPRICE</c>: what one more literal costs in the literal length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LiteralLengthIncrementPrice(OptimalState optimal, uint literalLength) =>
        LiteralLengthPrice(optimal, literalLength) - LiteralLengthPrice(optimal, literalLength - 1);

    /// <summary>libzstd's <c>ZSTD_getMatchPrice</c>: the offset and match length of a sequence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MatchPrice(OptimalState optimal, uint offBase, uint matchLength) =>
        OffsetPrice(optimal, offBase) + MatchLengthPrice(optimal, (uint)BitOperations.Log2(offBase), matchLength);

    /// <summary>
    /// The offset's part of <see cref="MatchPrice"/>, which a match's lengths share; with fixed prices,
    /// nothing (it takes the code with the length).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint OffsetPrice(OptimalState optimal, uint offBase)
    {
        if (optimal.Predefined)
        {
            return 0;
        }

        uint offCode = (uint)BitOperations.Log2(offBase);
        uint price = (offCode * BitCostMultiplier) + (optimal.OffCodeSumBasePrice - Weight(optimal.OffCodeFrequencies[offCode]));
        if ((TLevel.Level < 2) && (offCode >= 20))
        {
            // Long distances handicapped, for the decoder's caches.
            price += (offCode - 19) * 2 * BitCostMultiplier;
        }

        return price;
    }

    /// <summary>
    /// The match length's part of <see cref="MatchPrice"/>, and the bias against sequences; with fixed
    /// prices, the whole price. Sums of 32-bit prices wrap as libzstd's do: in any order, the same.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MatchLengthPrice(OptimalState optimal, uint offCode, uint matchLength)
    {
        uint matchLengthBase = matchLength - 3;
        if (optimal.Predefined)
        {
            return Weight(matchLengthBase) + ((16 + offCode) * BitCostMultiplier);
        }

        uint code = MatchLengthCode(matchLengthBase);
        uint price = (MatchLengthBits[(int)code] * (uint)BitCostMultiplier) + (optimal.MatchLengthSumBasePrice - Weight(optimal.MatchLengthFrequencies[code]));

        // Fewer sequences favored, for decompression speed.
        return price + (BitCostMultiplier / 5);
    }

    /// <summary>
    /// The prices of the matches found at <paramref name="cur"/>, each from its longest length down,
    /// into the positions they reach, cheaper ones replacing the stretches there. A method of its own:
    /// in the parser, its values spilled to the stack and its nodes' addresses were computed again for
    /// every field.
    /// </summary>
    /// <returns>The last position priced.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint PriceMatches(
        OptimalState optimal, OptimalNode* opt, OptimalMatch* matches, uint matchCount, uint cur, int basePrice, uint lastPosition, uint minMatch)
    {
        for (uint matchIndex = 0; matchIndex < matchCount; matchIndex++)
        {
            uint offBase = matches[matchIndex].OffBase;
            uint lastLength = matches[matchIndex].Length;
            uint startLength = matchIndex > 0 ? matches[matchIndex - 1].Length + 1 : minMatch;
            uint offCode = (uint)BitOperations.Log2(offBase);
            int offsetPrice = basePrice + (int)OffsetPrice(optimal, offBase);
            for (uint length = lastLength; length >= startLength; length--)
            {
                uint position = cur + length;
                int price = offsetPrice + (int)MatchLengthPrice(optimal, offCode, length);
                OptimalNode* node = opt + position;
                if ((position > lastPosition) || (price < node->Price))
                {
                    while (lastPosition < position)
                    {
                        // Empty positions filled, for the comparisons to come.
                        lastPosition++;
                        opt[lastPosition].Price = MaxPrice;
                        opt[lastPosition].LiteralLength = 1;
                    }

                    node->MatchLength = length;
                    node->OffBase = offBase;
                    node->LiteralLength = 0;
                    node->Price = price;
                }
                else if (TLevel.Level == 0)
                {
                    // Early abort: some ratio for speed.
                    break;
                }
            }
        }

        return lastPosition;
    }

    /// <summary>libzstd's <c>ZSTD_updateStats</c>: a sequence chosen, its symbols counted.</summary>
    private static void UpdateStatistics(OptimalState optimal, uint literalLength, byte* literals, uint offBase, uint matchLength)
    {
        for (uint u = 0; u < literalLength; u++)
        {
            optimal.LiteralFrequencies[literals[u]] += LiteralFrequencyAdd;
        }

        optimal.LiteralSum += literalLength * LiteralFrequencyAdd;
        optimal.LiteralLengthFrequencies[LiteralLengthCode(literalLength)]++;
        optimal.LiteralLengthSum++;
        optimal.OffCodeFrequencies[BitOperations.Log2(offBase)]++;
        optimal.OffCodeSum++;
        optimal.MatchLengthFrequencies[MatchLengthCode(matchLength - 3)]++;
        optimal.MatchLengthSum++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LiteralLengthCode(uint literalLength) =>
        literalLength > 63 ? (uint)BitOperations.Log2(literalLength) + 19 : LiteralLengthCodes[(int)literalLength];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MatchLengthCode(uint matchLengthBase) =>
        matchLengthBase > 127 ? (uint)BitOperations.Log2(matchLengthBase) + 36 : MatchLengthCodes[(int)matchLengthBase];

    // ---- matches

    /// <summary>libzstd's <c>ZSTD_readMINMATCH</c>: four bytes, or three in the top of a word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadMinMatch(byte* p, uint length) => length == 3 ? Read32(p) << 8 : Read32(p);

    /// <summary>libzstd's <c>ZSTD_hash3Ptr</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint Hash3(byte* p, int hashLog) => ((Read32(p) << 8) * 506832829u) >> (32 - hashLog);

    /// <summary>libzstd's <c>ZSTD_insertAndFindFirstIndexHash3</c>: the positions up to ip into the 3-byte table; ip's entry.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint InsertAndFindFirstIndexHash3(ref MatchState state, ref uint nextToUpdate3, byte* ip)
    {
        uint* hashTable3 = state.HashTable3;
        int hashLog3 = state.HashLog3;
        byte* @base = state.Base;
        uint index = nextToUpdate3;
        uint target = (uint)(ip - @base);
        nuint hash3 = Hash3(ip, hashLog3);
        while (index < target)
        {
            hashTable3[Hash3(@base + index, hashLog3)] = index;
            index++;
        }

        nextToUpdate3 = target;
        return hashTable3[hash3];
    }

    /// <summary>
    /// libzstd's <c>ZSTD_insertBt1</c>: one position into the binary tree.
    /// </summary>
    /// <returns>How many positions to advance: past a repetitive match, more than one.</returns>
    private static uint InsertIntoTree(ref MatchState state, byte* ip, byte* end, uint target)
    {
        uint* hashTable = state.HashTable;
        nuint hash = TLength.Hash(Read64(ip), state.Parameters.HashLog);
        uint* tree = state.ChainTable;
        uint treeMask = (1u << (state.Parameters.ChainLog - 1)) - 1;
        uint matchIndex = hashTable[hash];
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        uint treeLow = treeMask >= current ? 0 : current - treeMask;
        uint* smallerPtr = tree + (2 * (current & treeMask));
        uint* largerPtr = smallerPtr + 1;
        uint dummy;

        // The window at the end of the update: only positions still in it then are needed.
        uint windowLow = state.LowestMatchIndex(target);
        uint matchEndIndex = current + 8 + 1;
        nuint bestLength = 8;
        uint compares = 1u << state.Parameters.SearchLog;

        uint dictLimit = state.DictLimit;
        byte* dictBase = state.DictionaryBase;
        hashTable[hash] = current;
        for (; (compares != 0) && (matchIndex >= windowLow); compares--)
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
                matchLength += Count2Segments(ip + matchLength, match + matchLength, end, dictBase + dictLimit, @base + dictLimit);
                if (matchIndex + matchLength >= dictLimit)
                {
                    // The match runs into the prefix: its next byte is there.
                    match = @base + matchIndex;
                }
            }

            if (matchLength > bestLength)
            {
                bestLength = matchLength;
                if (matchLength > matchEndIndex - matchIndex)
                {
                    matchEndIndex = matchIndex + (uint)matchLength;
                }
            }

            if (ip + matchLength == end)
            {
                // Equal to the end: no way to know whether smaller or larger, so dropped.
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
        uint positions = bestLength > 384 ? (uint)Math.Min(192, bestLength - 384) : 0;
        return Math.Max(positions, matchEndIndex - (current + 8));
    }

    /// <summary>libzstd's <c>ZSTD_updateTree</c>: a dictionary's positions up to <paramref name="ip"/> sorted into the tree.</summary>
    internal static void FillTree(ref MatchState state, byte* ip, byte* end) => UpdateTree(ref state, ip, end);

    /// <summary>
    /// libzstd's <c>ZSTD_updateTree_internal</c>: the positions up to <paramref name="ip"/> into the tree.
    /// Seldom any: the parser searches most positions, inserting them; the insertion is not inlined.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateTree(ref MatchState state, byte* ip, byte* end)
    {
        uint target = (uint)(ip - state.Base);
        if (state.NextToUpdate < target)
        {
            InsertIntoTree(ref state, end, target);
        }

        state.NextToUpdate = target;
    }

    /// <summary>The positions from <see cref="MatchState.NextToUpdate"/> up to <paramref name="target"/> into the tree.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InsertIntoTree(ref MatchState state, byte* end, uint target)
    {
        byte* @base = state.Base;
        uint index = state.NextToUpdate;
        while (index < target)
        {
            index += InsertIntoTree(ref state, @base + index, end, target);
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_btGetAllMatches_internal</c>: the matches at <paramref name="ip"/>, each longer
    /// than the last: the repeat offsets', a 3-byte one, then the tree's, ip inserted as it is walked.
    /// </summary>
    /// <returns>The number of matches; 0 in a repetitive match's skipped positions.</returns>
    private static uint GetAllMatches(
        OptimalMatch* matches, ref MatchState state, ref uint nextToUpdate3, byte* ip, byte* end, uint* rep, uint ll0, uint lengthToBeat)
    {
        byte* @base = state.Base;
        if (ip < @base + state.NextToUpdate)
        {
            return 0;
        }

        UpdateTree(ref state, ip, end);

        uint sufficientLength = (uint)Math.Min(state.Parameters.TargetLength, OptNum - 1);
        uint current = (uint)(ip - @base);
        uint minMatch = TLength.MinLength == 3 ? 3u : 4u;
        uint* hashTable = state.HashTable;
        nuint hash = TLength.Hash(Read64(ip), state.Parameters.HashLog);
        uint matchIndex = hashTable[hash];
        uint dictLimit = state.DictLimit;
        uint windowLow = state.LowestMatchIndex(current);
        uint matchLow = windowLow != 0 ? windowLow : 1;
        uint count = 0;
        nuint bestLength = lengthToBeat - 1;

        // The repeat offsets: with literals before, the three; without, the last two and the first less one.
        uint lastRepeat = RepeatCodeCount + ll0;
        for (uint repCode = ll0; repCode < lastRepeat; repCode++)
        {
            uint repOffset = repCode == RepeatCodeCount ? rep[0] - 1 : rep[repCode];
            uint repIndex = current - repOffset;
            nuint repLength = 0;

            // current > repIndex >= dictLimit, by an intentional overflow that discards 0 and -1.
            if (repOffset - 1 < current - dictLimit)
            {
                if (X86Base.IsSupported
                    ? repIndex >= windowLow && ReadMinMatch(ip, minMatch) == ReadMinMatch(ip - repOffset, minMatch)
                    : (repIndex >= windowLow) & (ReadMinMatch(ip, minMatch) == ReadMinMatch(ip - repOffset, minMatch)))
                {
                    repLength = Count(ip + minMatch, ip + minMatch - repOffset, end) + minMatch;
                }
            }
            else if (TDictionary.Mode == ExtDictionary.Value)
            {
                // In the extDict, within the window and not straddling the segments.
                byte* repMatch = state.DictionaryBase + repIndex;
                if (((repOffset - 1 < current - windowLow) & IndexOverlapCheck(dictLimit, repIndex))
                    && ReadMinMatch(ip, minMatch) == ReadMinMatch(repMatch, minMatch))
                {
                    repLength = Count2Segments(ip + minMatch, repMatch + minMatch, end, state.DictionaryBase + dictLimit, @base + dictLimit) + minMatch;
                }
            }
            else if (TDictionary.Mode == AttachedDictionary.Value)
            {
                // In the attached dictionary, its indices just below the window's.
                MatchState* dictionary = state.Dictionary;
                uint indexDelta = windowLow - dictionary->End;
                byte* repMatch = dictionary->Base + repIndex - indexDelta;
                if (((repOffset - 1 < current - (dictionary->LowLimit + indexDelta)) & IndexOverlapCheck(dictLimit, repIndex))
                    && ReadMinMatch(ip, minMatch) == ReadMinMatch(repMatch, minMatch))
                {
                    repLength = Count2Segments(ip + minMatch, repMatch + minMatch, end, dictionary->Base + dictionary->End, @base + dictLimit) + minMatch;
                }
            }

            if (repLength > bestLength)
            {
                bestLength = repLength;
                matches[count].OffBase = repCode - ll0 + 1;
                matches[count].Length = (uint)repLength;
                count++;
                if (X86Base.IsSupported ? repLength > sufficientLength || ip + repLength == end : (repLength > sufficientLength) | (ip + repLength == end))
                {
                    return count;
                }
            }
        }

        // A 3-byte match, from its own table, when nothing longer is known.
        if ((TLength.MinLength == 3) && (bestLength < 3))
        {
            uint matchIndex3 = InsertAndFindFirstIndexHash3(ref state, ref nextToUpdate3, ip);
            if (X86Base.IsSupported
                ? matchIndex3 >= matchLow && current - matchIndex3 < (1 << 18)
                : (matchIndex3 >= matchLow) & (current - matchIndex3 < (1 << 18)))
            {
                nuint length = TDictionary.Mode != ExtDictionary.Value || matchIndex3 >= dictLimit
                    ? Count(ip, @base + matchIndex3, end)
                    : Count2Segments(ip, state.DictionaryBase + matchIndex3, end, state.DictionaryBase + dictLimit, @base + dictLimit);
                if (length >= 3)
                {
                    bestLength = length;
                    matches[0].OffBase = OffsetToOffBase(current - matchIndex3);
                    matches[0].Length = (uint)length;
                    count = 1;
                    if (X86Base.IsSupported ? length > sufficientLength || ip + length == end : (length > sufficientLength) | (ip + length == end))
                    {
                        // The longest possible: ip is not inserted.
                        state.NextToUpdate = current + 1;
                        return 1;
                    }
                }
            }
        }

        hashTable[hash] = current;
        return WalkTree(matches, count, bestLength, ref state, ip, end, matchIndex, matchLow);
    }

    /// <summary>
    /// The tree part of <see cref="GetAllMatches"/>: ip inserted into its hash's tree as the tree is
    /// walked, each longer match recorded.
    /// </summary>
    /// <remarks>
    /// A method of its own: inside the whole search, the JIT kept nearly every value of this loop on
    /// the stack. The indices it follows from node to node are native integers, so that each step's
    /// address is one instruction on the dependent chain.
    /// </remarks>
    /// <returns>The number of matches.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint WalkTree(
        OptimalMatch* matches, uint count, nuint bestLength, ref MatchState state, byte* ip, byte* end, nuint matchIndex, nuint matchLow)
    {
        byte* @base = state.Base;
        uint* tree = state.ChainTable;
        nuint treeMask = (1u << (state.Parameters.ChainLog - 1)) - 1;
        nuint current = (nuint)(ip - @base);
        nuint treeLow = treeMask >= current ? 0 : current - treeMask;
        uint* smallerPtr = tree + (2 * (current & treeMask));
        uint* largerPtr = smallerPtr + 1;
        nuint matchEndIndex = current + 8 + 1;
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        uint compares = 1u << state.Parameters.SearchLog;
        uint dummy;

        nuint dictLimit = state.DictLimit;
        byte* dictBase = state.DictionaryBase;
        for (; (compares != 0) && (matchIndex >= matchLow); compares--)
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
                matchLength += Count2Segments(ip + matchLength, match + matchLength, end, dictBase + dictLimit, @base + dictLimit);
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
                    matchEndIndex = matchIndex + matchLength;
                }

                bestLength = matchLength;
                matches[count].OffBase = OffsetToOffBase((uint)(current - matchIndex));
                matches[count].Length = (uint)matchLength;
                count++;
                if (X86Base.IsSupported ? matchLength > OptNum || ip + matchLength == end : (matchLength > OptNum) | (ip + matchLength == end))
                {
                    // Dropped, to keep the tree consistent; the attached dictionary is not searched.
                    if (TDictionary.Mode == AttachedDictionary.Value)
                    {
                        compares = 0;
                    }

                    break;
                }
            }

            if (match[matchLength] < ip[matchLength])
            {
                *smallerPtr = (uint)matchIndex;
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
                *largerPtr = (uint)matchIndex;
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
            count = WalkDictionaryTree(matches, count, bestLength, ref state, ip, end, (uint)matchLow, compares, ref matchEndIndex);
        }

        // Past a repetitive match, its positions are not inserted.
        state.NextToUpdate = (uint)(matchEndIndex - 8);
        return count;
    }

    /// <summary>
    /// The attached dictionary's tree, sorted when it was filled, with the compares the frame's left:
    /// each longer match recorded, its index translated to just below the window's.
    /// </summary>
    /// <returns>The number of matches.</returns>
    private static uint WalkDictionaryTree(
        OptimalMatch* matches, uint count, nuint bestLength, ref MatchState state, byte* ip, byte* end, uint windowLow, uint compares, ref nuint matchEndIndex)
    {
        MatchState* dictionary = state.Dictionary;
        byte* @base = state.Base;
        uint current = (uint)(ip - @base);
        uint highLimit = dictionary->End;
        uint lowLimit = dictionary->LowLimit;
        uint indexDelta = windowLow - highLimit;
        byte* dictionaryBase = dictionary->Base;
        byte* dictionaryEnd = dictionaryBase + highLimit;
        byte* prefixStart = @base + state.DictLimit;
        uint* tree = dictionary->ChainTable;
        uint treeMask = (1u << (dictionary->Parameters.ChainLog - 1)) - 1;
        uint treeLow = treeMask < highLimit - lowLimit ? highLimit - treeMask : lowLimit;
        uint dictMatchIndex = dictionary->HashTable[TLength.Hash(Read64(ip), dictionary->Parameters.HashLog)];
        nuint commonLengthSmaller = 0;
        nuint commonLengthLarger = 0;
        for (; (compares != 0) && (dictMatchIndex > lowLimit); compares--)
        {
            uint* nextPtr = tree + (2 * (dictMatchIndex & treeMask));
            nuint matchLength = Math.Min(commonLengthSmaller, commonLengthLarger);
            byte* match = dictionaryBase + dictMatchIndex;
            matchLength += Count2Segments(ip + matchLength, match + matchLength, end, dictionaryEnd, prefixStart);
            if (dictMatchIndex + matchLength >= highLimit)
            {
                // The match runs into the prefix: its next byte is there.
                match = @base + dictMatchIndex + indexDelta;
            }

            if (matchLength > bestLength)
            {
                uint matchIndex = dictMatchIndex + indexDelta;
                if (matchLength > matchEndIndex - matchIndex)
                {
                    matchEndIndex = matchIndex + (uint)matchLength;
                }

                bestLength = matchLength;
                matches[count].OffBase = OffsetToOffBase(current - matchIndex);
                matches[count].Length = (uint)matchLength;
                count++;
                if (X86Base.IsSupported ? matchLength > OptNum || ip + matchLength == end : (matchLength > OptNum) | (ip + matchLength == end))
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

        return count;
    }
}
