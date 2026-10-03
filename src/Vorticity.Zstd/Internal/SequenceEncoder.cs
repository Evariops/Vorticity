using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>FSE_repeat</c>: whether the previous block's table of a code may serve again.</summary>
internal enum FseRepeat
{
    /// <summary>No previous table, or one that cannot be used.</summary>
    None,

    /// <summary>A previous table that may lack some symbols: its cost tells.</summary>
    Check,

    /// <summary>A previous table known to encode every symbol (a dictionary's).</summary>
    Valid,
}

/// <summary>libzstd's <c>SymbolEncodingType_e</c>: the mode of a code's table, as the format numbers it.</summary>
internal enum SymbolEncodingType
{
    Basic = 0,
    Rle = 1,
    Compressed = 2,
    Repeat = 3,
}

/// <summary>
/// libzstd's <c>ZSTD_compressedBlockState_t</c>: what a block hands the next, once it is emitted
/// compressed: its entropy tables, their repeat modes, and the repeat offsets.
/// </summary>
internal sealed unsafe class BlockState
{
    private readonly uint[] _rep = GC.AllocateArray<uint>(SequenceEncoder.RepeatOffsetCount, pinned: true);

    public BlockState()
    {
        Rep = (uint*)Unsafe.AsPointer(ref _rep[0]);
        Reset();
    }

    public readonly HuffmanCTable Huffman = new();
    public HuffmanRepeat HuffmanRepeat;
    public readonly FseCTable LiteralLengths = new(SequenceEncoder.LiteralLengthFseLog, SequenceCodes.MaxLiteralLength);
    public readonly FseCTable Offsets = new(SequenceEncoder.OffsetFseLog, SequenceCodes.MaxOffset);
    public readonly FseCTable MatchLengths = new(SequenceEncoder.MatchLengthFseLog, SequenceCodes.MaxMatchLength);
    public FseRepeat LiteralLengthRepeat;
    public FseRepeat OffsetRepeat;
    public FseRepeat MatchLengthRepeat;

    /// <summary>The three repeat offsets, pinned: the match finders update them in place.</summary>
    public uint* Rep { get; }

    /// <summary>libzstd's <c>ZSTD_reset_compressedBlockState</c>: the state every frame starts from.</summary>
    public void Reset()
    {
        Rep[0] = 1;
        Rep[1] = 4;
        Rep[2] = 8;
        HuffmanRepeat = HuffmanRepeat.None;
        LiteralLengthRepeat = FseRepeat.None;
        OffsetRepeat = FseRepeat.None;
        MatchLengthRepeat = FseRepeat.None;
    }

    /// <summary>libzstd's copy of <c>prevEntropy->fse</c> into <c>nextEntropy->fse</c>, when a block has no sequence.</summary>
    public void CopySequenceTablesFrom(BlockState other)
    {
        LiteralLengths.CopyFrom(other.LiteralLengths);
        Offsets.CopyFrom(other.Offsets);
        MatchLengths.CopyFrom(other.MatchLengths);
        LiteralLengthRepeat = other.LiteralLengthRepeat;
        OffsetRepeat = other.OffsetRepeat;
        MatchLengthRepeat = other.MatchLengthRepeat;
    }
}

/// <summary>
/// libzstd's <c>zstd_compress_sequences.c</c> and the sequence half of
/// <c>ZSTD_entropyCompressSeqStore_internal</c>: the codes of a block's sequences, the mode of each
/// code's table, the tables, and the bitstream.
/// </summary>
internal static unsafe class SequenceEncoder
{
    public const int RepeatOffsetCount = 3;
    public const int LiteralLengthFseLog = 9;
    public const int MatchLengthFseLog = 9;
    public const int OffsetFseLog = 8;

    /// <summary>libzstd's <c>DefaultMaxOff</c>: the largest offset code the predefined table has.</summary>
    public const int DefaultMaxOffset = 28;

    private const int LiteralLengthDefaultNormLog = 6;
    private const int MatchLengthDefaultNormLog = 6;
    private const int OffsetDefaultNormLog = 5;

    /// <summary>libzstd's <c>LONGNBSEQ</c>.</summary>
    private const int LongNumberOfSequences = 0x7F00;

    /// <summary>The predefined tables, built once.</summary>
    private static readonly FseCTable DefaultLiteralLengths = BuildDefault(SequenceCodes.LiteralLengthDefaultNorm, SequenceCodes.MaxLiteralLength, LiteralLengthDefaultNormLog);
    private static readonly FseCTable DefaultOffsets = BuildDefault(SequenceCodes.OffsetDefaultNorm, DefaultMaxOffset, OffsetDefaultNormLog);
    private static readonly FseCTable DefaultMatchLengths = BuildDefault(SequenceCodes.MatchLengthDefaultNorm, SequenceCodes.MaxMatchLength, MatchLengthDefaultNormLog);

    private static FseCTable BuildDefault(ReadOnlySpan<short> norm, int maxSymbolValue, int tableLog)
    {
        var table = new FseCTable(tableLog, maxSymbolValue);
        fixed (short* normalized = norm)
        {
            FseEncoder.BuildCTable(table, normalized, (uint)maxSymbolValue, (uint)tableLog);
        }

        return table;
    }

    /// <summary>libzstd's <c>LL_Code</c>: the code of a literal length below 64.</summary>
    private static ReadOnlySpan<byte> LiteralLengthCodes =>
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 16, 17, 17, 18, 18, 19, 19,
        20, 20, 20, 20, 21, 21, 21, 21,
        22, 22, 22, 22, 22, 22, 22, 22,
        23, 23, 23, 23, 23, 23, 23, 23,
        24, 24, 24, 24, 24, 24, 24, 24,
        24, 24, 24, 24, 24, 24, 24, 24,
    ];

    /// <summary>libzstd's <c>ML_Code</c>: the code of a match length minus 3, below 128.</summary>
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

    /// <summary>libzstd's <c>kInverseProbabilityLog256</c>: -log2(x / 256) in 1/256 of a bit, 0 for 0.</summary>
    private static ReadOnlySpan<ushort> InverseProbabilityLog256 =>
    [
        0, 2048, 1792, 1642, 1536, 1453, 1386, 1329, 1280, 1236, 1197, 1162,
        1130, 1100, 1073, 1047, 1024, 1001, 980, 960, 941, 923, 906, 889,
        874, 859, 844, 830, 817, 804, 791, 779, 768, 756, 745, 734,
        724, 714, 704, 694, 685, 676, 667, 658, 650, 642, 633, 626,
        618, 610, 603, 595, 588, 581, 574, 567, 561, 554, 548, 542,
        535, 529, 523, 517, 512, 506, 500, 495, 489, 484, 478, 473,
        468, 463, 458, 453, 448, 443, 438, 434, 429, 424, 420, 415,
        411, 407, 402, 398, 394, 390, 386, 382, 377, 373, 370, 366,
        362, 358, 354, 350, 347, 343, 339, 336, 332, 329, 325, 322,
        318, 315, 311, 308, 305, 302, 298, 295, 292, 289, 286, 282,
        279, 276, 273, 270, 267, 264, 261, 258, 256, 253, 250, 247,
        244, 241, 239, 236, 233, 230, 228, 225, 222, 220, 217, 215,
        212, 209, 207, 204, 202, 199, 197, 194, 192, 190, 187, 185,
        182, 180, 178, 175, 173, 171, 168, 166, 164, 162, 159, 157,
        155, 153, 151, 149, 146, 144, 142, 140, 138, 136, 134, 132,
        130, 128, 126, 123, 121, 119, 117, 115, 114, 112, 110, 108,
        106, 104, 102, 100, 98, 96, 94, 93, 91, 89, 87, 85,
        83, 82, 80, 78, 76, 74, 73, 71, 69, 67, 66, 64,
        62, 61, 59, 57, 55, 54, 52, 50, 49, 47, 46, 44,
        42, 41, 39, 37, 36, 34, 33, 31, 30, 28, 26, 25,
        23, 22, 20, 19, 17, 16, 14, 13, 11, 10, 8, 7,
        5, 4, 2, 1,
    ];

    /// <summary>libzstd's <c>ZSTD_LLcode</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LiteralLengthCode(uint litLength) =>
        litLength > 63 ? (uint)BitOperations.Log2(litLength) + 19 : LiteralLengthCodes[(int)litLength];

    /// <summary>libzstd's <c>ZSTD_MLcode</c>, from the match length minus 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MatchLengthCode(uint matchLengthBase) =>
        matchLengthBase > 127 ? (uint)BitOperations.Log2(matchLengthBase) + 36 : MatchLengthCodes[(int)matchLengthBase];

    /// <summary>libzstd's <c>ZSTD_seqToCodes</c>: the three codes of every sequence.</summary>
    public static void SequencesToCodes(SequenceStore store)
    {
        SequenceRecord* sequences = store.SequencesStart;
        byte* llCodes = store.LiteralLengthCodes;
        byte* ofCodes = store.OffsetCodes;
        byte* mlCodes = store.MatchLengthCodes;
        nuint count = store.SequenceCount;
        for (nuint u = 0; u < count; u++)
        {
            llCodes[u] = (byte)LiteralLengthCode(sequences[u].LitLength);
            ofCodes[u] = (byte)BitOperations.Log2(sequences[u].OffBase);
            mlCodes[u] = (byte)MatchLengthCode(sequences[u].MatchLengthBase);
        }

        if (store.LongLengthType == LongLengthType.LiteralLength)
        {
            llCodes[store.LongLengthPosition] = SequenceCodes.MaxLiteralLength;
        }

        if (store.LongLengthType == LongLengthType.MatchLength)
        {
            mlCodes[store.LongLengthPosition] = SequenceCodes.MaxMatchLength;
        }
    }

    /// <summary>libzstd's <c>ZSTD_useLowProbCount</c>: -1 for the rare symbols of the larger blocks only.</summary>
    private static bool UseLowProbCount(nuint sequenceCount) => sequenceCount >= 2048;

    /// <summary>libzstd's <c>ZSTD_NCountCost</c>: the size of the description a new table would take.</summary>
    private static nuint NCountCost(uint* count, uint max, nuint sequenceCount, uint fseLog)
    {
        byte* header = stackalloc byte[FseEncoder.NCountBound];
        short* normalized = stackalloc short[SequenceCodes.MaxMatchLength + 1];
        uint tableLog = FseEncoder.OptimalTableLog(fseLog, sequenceCount, max);
        FseEncoder.NormalizeCount(normalized, tableLog, count, sequenceCount, max, UseLowProbCount(sequenceCount));
        return FseEncoder.WriteNCount(header, normalized, max, tableLog);
    }

    /// <summary>libzstd's <c>ZSTD_entropyCost</c>: the bits the distribution itself would take.</summary>
    private static nuint EntropyCost(uint* count, uint max, nuint total)
    {
        uint cost = 0;
        ReadOnlySpan<ushort> inverse = InverseProbabilityLog256;
        for (uint s = 0; s <= max; s++)
        {
            uint norm = (uint)(256 * count[s] / total);
            if (count[s] != 0 && norm == 0)
            {
                norm = 1;
            }

            cost += count[s] * inverse[(int)norm];
        }

        return cost >> 8;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_fseBitCost</c>: the bits the distribution takes with <paramref name="table"/>,
    /// or <see cref="nuint.MaxValue"/> (libzstd's error) when the table lacks a symbol.
    /// </summary>
    private static nuint FseBitCost(FseCTable table, uint* count, uint max)
    {
        const int AccuracyLog = 8;
        if (table.MaxSymbolValue < max)
        {
            return nuint.MaxValue;
        }

        nuint cost = 0;
        uint tableLog = (uint)table.TableLog;
        for (uint s = 0; s <= max; s++)
        {
            uint badCost = (tableLog + 1) << AccuracyLog;
            uint bitCost = FseEncoder.BitCost(table.Symbols, tableLog, s, AccuracyLog);
            if (count[s] == 0)
            {
                continue;
            }

            if (bitCost >= badCost)
            {
                return nuint.MaxValue;
            }

            cost += (nuint)count[s] * bitCost;
        }

        return cost >> AccuracyLog;
    }

    /// <summary>libzstd's <c>ZSTD_crossEntropyCost</c>: the bits the distribution takes with a normalized one.</summary>
    private static nuint CrossEntropyCost(ReadOnlySpan<short> norm, uint accuracyLog, uint* count, uint max)
    {
        int shift = 8 - (int)accuracyLog;
        nuint cost = 0;
        ReadOnlySpan<ushort> inverse = InverseProbabilityLog256;
        for (uint s = 0; s <= max; s++)
        {
            uint normAccuracy = norm[(int)s] != -1 ? (uint)norm[(int)s] : 1;
            uint norm256 = normAccuracy << shift;
            cost += count[s] * (nuint)inverse[(int)norm256];
        }

        return cost >> 8;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_selectEncodingType</c>: one symbol is RLE; below the lazy strategies the
    /// predefined table for few sequences or a flat distribution, otherwise a new one; from the lazy
    /// strategies, whichever of the predefined, the previous and a new table costs least.
    /// </summary>
    public static SymbolEncodingType SelectEncodingType(
        ref FseRepeat repeatMode, uint* count, uint max, nuint mostFrequent, nuint sequenceCount, uint fseLog, FseCTable previous,
        ReadOnlySpan<short> defaultNorm, uint defaultNormLog, bool isDefaultAllowed, Strategy strategy)
    {
        if (mostFrequent == sequenceCount)
        {
            repeatMode = FseRepeat.None;
            if (isDefaultAllowed && sequenceCount <= 2)
            {
                // Two symbols or fewer cost less with the predefined table than an RLE byte.
                return SymbolEncodingType.Basic;
            }

            return SymbolEncodingType.Rle;
        }

        if (strategy < Strategy.Lazy)
        {
            if (isDefaultAllowed)
            {
                const nuint StaticFseMaxSequences = 1000;
                nuint multiplier = (nuint)(10 - (int)strategy);
                const int BaseLog = 3;
                nuint dynamicFseMinSequences = (((nuint)1 << (int)defaultNormLog) * multiplier) >> BaseLog;
                if (repeatMode == FseRepeat.Valid && sequenceCount < StaticFseMaxSequences)
                {
                    return SymbolEncodingType.Repeat;
                }

                if (sequenceCount < dynamicFseMinSequences || mostFrequent < (sequenceCount >> (int)(defaultNormLog - 1)))
                {
                    repeatMode = FseRepeat.None;
                    return SymbolEncodingType.Basic;
                }
            }
        }
        else
        {
            nuint basicCost = isDefaultAllowed ? CrossEntropyCost(defaultNorm, defaultNormLog, count, max) : nuint.MaxValue;
            nuint repeatCost = repeatMode != FseRepeat.None ? FseBitCost(previous, count, max) : nuint.MaxValue;
            nuint nCountCost = NCountCost(count, max, sequenceCount, fseLog);
            nuint compressedCost = (nCountCost << 3) + EntropyCost(count, max, sequenceCount);
            if (basicCost <= repeatCost && basicCost <= compressedCost)
            {
                repeatMode = FseRepeat.None;
                return SymbolEncodingType.Basic;
            }

            if (repeatCost <= compressedCost)
            {
                return SymbolEncodingType.Repeat;
            }
        }

        repeatMode = FseRepeat.Check;
        return SymbolEncodingType.Compressed;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildCTable</c>: the table of a code in its mode, and its description when
    /// it has one. A new table leaves out the last sequence's symbol once, which the state that
    /// starts the bitstream encodes for free.
    /// </summary>
    /// <returns>The size of the description.</returns>
    public static nuint BuildCTable(
        byte* destination, FseCTable next, uint fseLog, SymbolEncodingType type, uint* count, uint max, byte* codeTable, nuint sequenceCount,
        FseCTable defaultTable, FseCTable previous)
    {
        switch (type)
        {
            case SymbolEncodingType.Rle:
                FseEncoder.BuildCTableRle(next, (byte)max);
                *destination = codeTable[0];
                return 1;

            case SymbolEncodingType.Repeat:
                next.CopyFrom(previous);
                return 0;

            case SymbolEncodingType.Basic:
                next.CopyFrom(defaultTable);
                return 0;

            default:
            {
                short* normalized = stackalloc short[SequenceCodes.MaxMatchLength + 1];
                nuint total = sequenceCount;
                uint tableLog = FseEncoder.OptimalTableLog(fseLog, sequenceCount, max);
                if (count[codeTable[sequenceCount - 1]] > 1)
                {
                    count[codeTable[sequenceCount - 1]]--;
                    total--;
                }

                FseEncoder.NormalizeCount(normalized, tableLog, count, total, max, UseLowProbCount(total));
                nuint size = FseEncoder.WriteNCount(destination, normalized, max, tableLog);
                FseEncoder.BuildCTable(next, normalized, max, tableLog);
                return size;
            }
        }
    }

    /// <summary>The modes of a block's three tables and the size of their descriptions.</summary>
    public struct Statistics
    {
        public SymbolEncodingType LiteralLengths;
        public SymbolEncodingType Offsets;
        public SymbolEncodingType MatchLengths;
        public nuint Size;

        /// <summary>The size of the last new table's description: see <see cref="ZstdCompressor"/>'s 1.3.4 guard.</summary>
        public nuint LastCountSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildSequencesStatistics</c>: the codes, then for each code its histogram (in
    /// <paramref name="count"/>, 256 counters), its mode, its table in <paramref name="next"/> and its
    /// description at <paramref name="destination"/>.
    /// </summary>
    public static Statistics BuildStatistics(SequenceStore store, BlockState previous, BlockState next, byte* destination, Strategy strategy, uint* count)
    {
        nuint sequenceCount = store.SequenceCount;
        byte* op = destination;
        Statistics stats = default;
        SequencesToCodes(store);

        // ---- literal lengths
        {
            uint max = SequenceCodes.MaxLiteralLength;
            nuint mostFrequent = Histogram.CountCodes(count, ref max, store.LiteralLengthCodes, sequenceCount);
            next.LiteralLengthRepeat = previous.LiteralLengthRepeat;
            stats.LiteralLengths = SelectEncodingType(
                ref next.LiteralLengthRepeat, count, max, mostFrequent, sequenceCount, LiteralLengthFseLog, previous.LiteralLengths,
                SequenceCodes.LiteralLengthDefaultNorm, LiteralLengthDefaultNormLog, isDefaultAllowed: true, strategy);
            nuint countSize = BuildCTable(
                op, next.LiteralLengths, LiteralLengthFseLog, stats.LiteralLengths, count, max, store.LiteralLengthCodes, sequenceCount,
                DefaultLiteralLengths, previous.LiteralLengths);
            if (stats.LiteralLengths == SymbolEncodingType.Compressed)
            {
                stats.LastCountSize = countSize;
            }

            op += countSize;
        }

        // ---- offsets: the predefined table only has codes up to 28
        {
            uint max = SequenceCodes.MaxOffset;
            nuint mostFrequent = Histogram.CountCodes(count, ref max, store.OffsetCodes, sequenceCount);
            bool defaultAllowed = max <= DefaultMaxOffset;
            next.OffsetRepeat = previous.OffsetRepeat;
            stats.Offsets = SelectEncodingType(
                ref next.OffsetRepeat, count, max, mostFrequent, sequenceCount, OffsetFseLog, previous.Offsets,
                SequenceCodes.OffsetDefaultNorm, OffsetDefaultNormLog, defaultAllowed, strategy);
            nuint countSize = BuildCTable(
                op, next.Offsets, OffsetFseLog, stats.Offsets, count, max, store.OffsetCodes, sequenceCount,
                DefaultOffsets, previous.Offsets);
            if (stats.Offsets == SymbolEncodingType.Compressed)
            {
                stats.LastCountSize = countSize;
            }

            op += countSize;
        }

        // ---- match lengths
        {
            uint max = SequenceCodes.MaxMatchLength;
            nuint mostFrequent = Histogram.CountCodes(count, ref max, store.MatchLengthCodes, sequenceCount);
            next.MatchLengthRepeat = previous.MatchLengthRepeat;
            stats.MatchLengths = SelectEncodingType(
                ref next.MatchLengthRepeat, count, max, mostFrequent, sequenceCount, MatchLengthFseLog, previous.MatchLengths,
                SequenceCodes.MatchLengthDefaultNorm, MatchLengthDefaultNormLog, isDefaultAllowed: true, strategy);
            nuint countSize = BuildCTable(
                op, next.MatchLengths, MatchLengthFseLog, stats.MatchLengths, count, max, store.MatchLengthCodes, sequenceCount,
                DefaultMatchLengths, previous.MatchLengths);
            if (stats.MatchLengths == SymbolEncodingType.Compressed)
            {
                stats.LastCountSize = countSize;
            }

            op += countSize;
        }

        stats.Size = (nuint)(op - destination);
        return stats;
    }

    /// <summary>libzstd's sequences section header: the number of sequences in one to three bytes.</summary>
    public static nuint WriteSequenceCount(byte* op, nuint sequenceCount)
    {
        if (sequenceCount < 128)
        {
            op[0] = (byte)sequenceCount;
            return 1;
        }

        if (sequenceCount < LongNumberOfSequences)
        {
            op[0] = (byte)((sequenceCount >> 8) + 0x80);
            op[1] = (byte)sequenceCount;
            return 2;
        }

        op[0] = 0xFF;
        Unsafe.WriteUnaligned(op + 1, (ushort)(sequenceCount - LongNumberOfSequences));
        return 3;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_encodeSequences_body</c> on a 64-bit container: from the last sequence to the
    /// first, its three states then its extra bits, so that the decoder reads them first to last.
    /// </summary>
    /// <returns>The size of the bitstream, or 0 when it does not fit.</returns>
    public static nuint EncodeSequences(
        byte* destination, nuint capacity, FseCTable matchLengthTable, byte* mlCodes, FseCTable offsetTable, byte* ofCodes,
        FseCTable literalLengthTable, byte* llCodes, SequenceRecord* sequences, nuint sequenceCount)
    {
        var bits = new BitWriter(destination, capacity);
        if (!bits.IsValid)
        {
            return 0;
        }

        ReadOnlySpan<byte> llBitsTable = SequenceCodes.LiteralLengthBits;
        ReadOnlySpan<byte> mlBitsTable = SequenceCodes.MatchLengthBits;

        // ---- the last sequence: its states start the stream
        nuint last = sequenceCount - 1;
        var stateMatchLength = new FseState(matchLengthTable, mlCodes[last]);
        var stateOffset = new FseState(offsetTable, ofCodes[last]);
        var stateLitLength = new FseState(literalLengthTable, llCodes[last]);
        bits.AddBits(sequences[last].LitLength, llBitsTable[llCodes[last]]);
        bits.AddBits(sequences[last].MatchLengthBase, mlBitsTable[mlCodes[last]]);
        bits.AddBits(sequences[last].OffBase, ofCodes[last]);
        bits.Flush();

        for (nuint n = sequenceCount - 2; n < sequenceCount; n--)
        {
            byte llCode = llCodes[n];
            byte ofCode = ofCodes[n];
            byte mlCode = mlCodes[n];
            int llBits = llBitsTable[llCode];
            int ofBits = ofCode;
            int mlBits = mlBitsTable[mlCode];
            stateOffset.Encode(ref bits, ofCode);
            stateMatchLength.Encode(ref bits, mlCode);
            stateLitLength.Encode(ref bits, llCode);
            if (ofBits + mlBits + llBits >= 64 - 7 - (LiteralLengthFseLog + MatchLengthFseLog + OffsetFseLog))
            {
                bits.Flush();
            }

            bits.AddBits(sequences[n].LitLength, llBits);
            bits.AddBits(sequences[n].MatchLengthBase, mlBits);
            if (ofBits + mlBits + llBits > 56)
            {
                bits.Flush();
            }

            bits.AddBits(sequences[n].OffBase, ofBits);
            bits.Flush();
        }

        stateMatchLength.Flush(ref bits);
        stateOffset.Flush(ref bits);
        stateLitLength.Flush(ref bits);
        return bits.Close();
    }
}
