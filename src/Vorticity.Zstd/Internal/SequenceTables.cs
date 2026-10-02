using System;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// One state of a sequence decoding table, libzstd's <c>ZSTD_seqSymbol</c>: the FSE transition and the
/// code's meaning folded together, so that a lookup yields the value's base and how many bits to add.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SeqSymbol
{
    public ushort NextState;
    public byte NbAdditionalBits;
    public byte NbBits;
    public uint BaseValue;

    public SeqSymbol(ushort nextState, byte nbAdditionalBits, byte nbBits, uint baseValue)
    {
        NextState = nextState;
        NbAdditionalBits = nbAdditionalBits;
        NbBits = nbBits;
        BaseValue = baseValue;
    }
}

/// <summary>A decoding table for one of the three sequence codes, and the accuracy it is read with.</summary>
internal sealed class SeqTable
{
    public readonly SeqSymbol[] Entries;
    public int TableLog;

    public SeqTable(int maxTableLog)
    {
        Entries = new SeqSymbol[1 << maxTableLog];
    }
}

/// <summary>The three sequence codes: their alphabets, their predefined distributions, their tables.</summary>
internal static class SequenceCodes
{
    public const int MaxLiteralLength = 35;
    public const int MaxMatchLength = 52;
    public const int MaxOffset = 31;
    public const int LiteralLengthMaxLog = 9;
    public const int MatchLengthMaxLog = 9;
    public const int OffsetMaxLog = 8;

    public static ReadOnlySpan<uint> LiteralLengthBase =>
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40,
        48, 64, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000,
        0x2000, 0x4000, 0x8000, 0x10000,
    ];

    public static ReadOnlySpan<byte> LiteralLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3,
        4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    public static ReadOnlySpan<uint> MatchLengthBase =>
    [
        3, 4, 5, 6, 7, 8, 9, 10,
        11, 12, 13, 14, 15, 16, 17, 18,
        19, 20, 21, 22, 23, 24, 25, 26,
        27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59,
        67, 83, 99, 0x83, 0x103, 0x203, 0x403, 0x803,
        0x1003, 0x2003, 0x4003, 0x8003, 0x10003,
    ];

    public static ReadOnlySpan<byte> MatchLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3,
        4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    /// <summary>
    /// An offset code c stands for the value (1 &lt;&lt; c) + c extra bits; the base here is that minus 3,
    /// which turns a value above 3 into the offset itself. Values 1 to 3 are the repeat codes.
    /// </summary>
    public static ReadOnlySpan<uint> OffsetBase =>
    [
        0, 1, 1, 5, 0xD, 0x1D, 0x3D, 0x7D,
        0xFD, 0x1FD, 0x3FD, 0x7FD, 0xFFD, 0x1FFD, 0x3FFD, 0x7FFD,
        0xFFFD, 0x1FFFD, 0x3FFFD, 0x7FFFD, 0xFFFFD, 0x1FFFFD, 0x3FFFFD, 0x7FFFFD,
        0xFFFFFD, 0x1FFFFFD, 0x3FFFFFD, 0x7FFFFFD, 0xFFFFFFD, 0x1FFFFFFD, 0x3FFFFFFD, 0x7FFFFFFD,
    ];

    public static ReadOnlySpan<byte> OffsetBits =>
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23,
        24, 25, 26, 27, 28, 29, 30, 31,
    ];

    public static ReadOnlySpan<short> LiteralLengthDefaultNorm =>
    [
        4, 3, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2,
        2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    ];

    public static ReadOnlySpan<short> MatchLengthDefaultNorm =>
    [
        1, 4, 3, 2, 2, 2, 2, 2,
        2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    ];

    public static ReadOnlySpan<short> OffsetDefaultNorm =>
    [
        1, 1, 1, 1, 1, 1, 2, 2,
        2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        -1, -1, -1, -1, -1,
    ];

    /// <summary>The predefined tables, built once from the default distributions.</summary>
    public static readonly SeqTable DefaultLiteralLengths = BuildDefault(LiteralLengthDefaultNorm, 6, LiteralLengthBase, LiteralLengthBits);
    public static readonly SeqTable DefaultMatchLengths = BuildDefault(MatchLengthDefaultNorm, 6, MatchLengthBase, MatchLengthBits);
    public static readonly SeqTable DefaultOffsets = BuildDefault(OffsetDefaultNorm, 5, OffsetBase, OffsetBits);

    private static SeqTable BuildDefault(ReadOnlySpan<short> norm, int tableLog, ReadOnlySpan<uint> baseValue, ReadOnlySpan<byte> bits)
    {
        var table = new SeqTable(tableLog);
        BuildTable(table, norm, tableLog, baseValue, bits);
        return table;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildFSETable</c>: spreads the symbols of a normalized distribution over the
    /// table and folds each symbol's base value and extra bits into its states.
    /// </summary>
    /// <remarks>The distribution comes from <see cref="Fse.ReadNCount"/>, which has validated it.</remarks>
    public static void BuildTable(SeqTable table, ReadOnlySpan<short> norm, int tableLog, ReadOnlySpan<uint> baseValue, ReadOnlySpan<byte> bits)
    {
        Span<SeqSymbol> entries = table.Entries;
        int tableSize = 1 << tableLog;
        int highThreshold = tableSize - 1;
        Span<ushort> symbolNext = stackalloc ushort[MaxMatchLength + 1];
        Span<byte> symbols = stackalloc byte[1 << LiteralLengthMaxLog];

        // Symbols below one unit of probability take one cell each, from the top.
        for (int s = 0; s < norm.Length; s++)
        {
            if (norm[s] == -1)
            {
                symbols[highThreshold--] = (byte)s;
                symbolNext[s] = 1;
            }
            else
            {
                symbolNext[s] = (ushort)norm[s];
            }
        }

        int mask = tableSize - 1;
        int step = Fse.TableStep(tableSize);
        int position = 0;
        for (int s = 0; s < norm.Length; s++)
        {
            for (int i = 0; i < norm[s]; i++)
            {
                symbols[position] = (byte)s;
                do
                {
                    position = (position + step) & mask;
                }
                while (position > highThreshold);
            }
        }

        for (int u = 0; u < tableSize; u++)
        {
            int symbol = symbols[u];
            int nextState = symbolNext[symbol]++;
            int nbBits = tableLog - BackwardBitReader.HighBit((uint)nextState);
            entries[u] = new SeqSymbol(
                (ushort)((nextState << nbBits) - tableSize),
                bits[symbol],
                (byte)nbBits,
                baseValue[symbol]);
        }

        table.TableLog = tableLog;
    }

    /// <summary>libzstd's <c>ZSTD_buildSeqTable_rle</c>: one state that every sequence takes.</summary>
    public static void BuildRle(SeqTable table, int symbol, ReadOnlySpan<uint> baseValue, ReadOnlySpan<byte> bits)
    {
        table.Entries[0] = new SeqSymbol(0, bits[symbol], 0, baseValue[symbol]);
        table.TableLog = 0;
    }
}
