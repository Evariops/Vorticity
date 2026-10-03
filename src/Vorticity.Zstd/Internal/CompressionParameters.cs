using System;
using System.Numerics;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_strategy</c>: how a level searches for matches, from the fastest. The values are
/// libzstd's, which some of its heuristics compute with.
/// </summary>
internal enum Strategy
{
    Fast = 1,
    DoubleFast = 2,
    Greedy = 3,
    Lazy = 4,
    Lazy2 = 5,
    BinaryTreeLazy2 = 6,
    BinaryTreeOptimal = 7,
    BinaryTreeUltra = 8,
    BinaryTreeUltra2 = 9,
}

/// <summary>
/// libzstd's <c>ZSTD_compressionParameters</c>: a level's row of <c>ZSTD_defaultCParameters</c>,
/// fitted to the frame it compresses (<c>ZSTD_getCParams_internal</c>, then
/// <c>ZSTD_adjustCParams_internal</c>).
/// </summary>
/// <summary>libzstd's <c>ZSTD_CParamMode_e</c>: what the parameters are sized for.</summary>
internal enum ParameterMode
{
    /// <summary>A frame, with its dictionary loaded into its tables if any (<c>ZSTD_cpm_noAttachDict</c>).</summary>
    Frame,

    /// <summary>A frame searching a prepared dictionary in place (<c>ZSTD_cpm_attachDict</c>): the source alone.</summary>
    AttachedDictionary,

    /// <summary>A prepared dictionary (<c>ZSTD_cpm_createCDict</c>), for small sources.</summary>
    CreateDictionary,
}

internal struct CompressionParameters
{
    /// <summary>libzstd's <c>ZSTD_minCLevel()</c>: minus the largest target length.</summary>
    public const int MinLevel = -(1 << 17);

    public const int MaxLevel = 22;

    public const int DefaultLevel = 3;

    public int WindowLog;
    public int ChainLog;
    public int HashLog;
    public int SearchLog;
    public int MinMatch;
    public int TargetLength;
    public Strategy Strategy;

    /// <summary>
    /// The row-based match finder pinned on (1) or off (-1), as a prepared dictionary's tables decide
    /// it from its own window; 0 for the window of these parameters (<see cref="UsesRowMatchFinder"/>).
    /// </summary>
    public sbyte RowMatchFinder;

    /// <summary>
    /// <c>ZSTD_defaultCParameters</c>: for sources over 256 KiB, up to 256 KiB, up to 128 KiB and up
    /// to 16 KiB, a row per level from 0 (the base of the negative levels) to 22, each
    /// <c>W, C, H, S, L, TL, strategy</c>.
    /// </summary>
    private static ReadOnlySpan<short> Rows =>
    [
        // ---- over 256 KiB
        19, 12, 13, 1, 6, 1, 1,
        19, 13, 14, 1, 7, 0, 1,
        20, 15, 16, 1, 6, 0, 1,
        21, 16, 17, 1, 5, 0, 2,
        21, 18, 18, 1, 5, 0, 2,
        21, 18, 19, 3, 5, 2, 3,
        21, 18, 19, 3, 5, 4, 4,
        21, 19, 20, 4, 5, 8, 4,
        21, 19, 20, 4, 5, 16, 5,
        22, 20, 21, 4, 5, 16, 5,
        22, 21, 22, 5, 5, 16, 5,
        22, 21, 22, 6, 5, 16, 5,
        22, 22, 23, 6, 5, 32, 5,
        22, 22, 22, 4, 5, 32, 6,
        22, 22, 23, 5, 5, 32, 6,
        22, 23, 23, 6, 5, 32, 6,
        22, 22, 22, 5, 5, 48, 7,
        23, 23, 22, 5, 4, 64, 7,
        23, 23, 22, 6, 3, 64, 8,
        23, 24, 22, 7, 3, 256, 9,
        25, 25, 23, 7, 3, 256, 9,
        26, 26, 24, 7, 3, 512, 9,
        27, 27, 25, 9, 3, 999, 9,

        // ---- up to 256 KiB
        18, 12, 13, 1, 5, 1, 1,
        18, 13, 14, 1, 6, 0, 1,
        18, 14, 14, 1, 5, 0, 2,
        18, 16, 16, 1, 4, 0, 2,
        18, 16, 17, 3, 5, 2, 3,
        18, 17, 18, 5, 5, 2, 3,
        18, 18, 19, 3, 5, 4, 4,
        18, 18, 19, 4, 4, 4, 4,
        18, 18, 19, 4, 4, 8, 5,
        18, 18, 19, 5, 4, 8, 5,
        18, 18, 19, 6, 4, 8, 5,
        18, 18, 19, 5, 4, 12, 6,
        18, 19, 19, 7, 4, 12, 6,
        18, 18, 19, 4, 4, 16, 7,
        18, 18, 19, 4, 3, 32, 7,
        18, 18, 19, 6, 3, 128, 7,
        18, 19, 19, 6, 3, 128, 8,
        18, 19, 19, 8, 3, 256, 8,
        18, 19, 19, 6, 3, 128, 9,
        18, 19, 19, 8, 3, 256, 9,
        18, 19, 19, 10, 3, 512, 9,
        18, 19, 19, 12, 3, 512, 9,
        18, 19, 19, 13, 3, 999, 9,

        // ---- up to 128 KiB
        17, 12, 12, 1, 5, 1, 1,
        17, 12, 13, 1, 6, 0, 1,
        17, 13, 15, 1, 5, 0, 1,
        17, 15, 16, 2, 5, 0, 2,
        17, 17, 17, 2, 4, 0, 2,
        17, 16, 17, 3, 4, 2, 3,
        17, 16, 17, 3, 4, 4, 4,
        17, 16, 17, 3, 4, 8, 5,
        17, 16, 17, 4, 4, 8, 5,
        17, 16, 17, 5, 4, 8, 5,
        17, 16, 17, 6, 4, 8, 5,
        17, 17, 17, 5, 4, 8, 6,
        17, 18, 17, 7, 4, 12, 6,
        17, 18, 17, 3, 4, 12, 7,
        17, 18, 17, 4, 3, 32, 7,
        17, 18, 17, 6, 3, 256, 7,
        17, 18, 17, 6, 3, 128, 8,
        17, 18, 17, 8, 3, 256, 8,
        17, 18, 17, 10, 3, 512, 8,
        17, 18, 17, 5, 3, 256, 9,
        17, 18, 17, 7, 3, 512, 9,
        17, 18, 17, 9, 3, 512, 9,
        17, 18, 17, 11, 3, 999, 9,

        // ---- up to 16 KiB
        14, 12, 13, 1, 5, 1, 1,
        14, 14, 15, 1, 5, 0, 1,
        14, 14, 15, 1, 4, 0, 1,
        14, 14, 15, 2, 4, 0, 2,
        14, 14, 14, 4, 4, 2, 3,
        14, 14, 14, 3, 4, 4, 4,
        14, 14, 14, 4, 4, 8, 5,
        14, 14, 14, 6, 4, 8, 5,
        14, 14, 14, 8, 4, 8, 5,
        14, 15, 14, 5, 4, 8, 6,
        14, 15, 14, 9, 4, 8, 6,
        14, 15, 14, 3, 4, 12, 7,
        14, 15, 14, 4, 3, 24, 7,
        14, 15, 14, 5, 3, 32, 8,
        14, 15, 15, 6, 3, 64, 8,
        14, 15, 15, 7, 3, 256, 8,
        14, 15, 15, 5, 3, 48, 9,
        14, 15, 15, 6, 3, 128, 9,
        14, 15, 15, 7, 3, 256, 9,
        14, 15, 15, 8, 3, 256, 9,
        14, 15, 15, 8, 3, 512, 9,
        14, 15, 15, 9, 3, 512, 9,
        14, 15, 15, 10, 3, 999, 9,
    ];

    /// <summary>libzstd's <c>ZSTD_CONTENTSIZE_UNKNOWN</c>.</summary>
    public const ulong UnknownSize = ulong.MaxValue;

    /// <summary>
    /// libzstd's <c>ZSTD_getCParams_internal</c> for a frame of <paramref name="sourceSize"/> bytes
    /// compressed without a dictionary, as <c>ZSTD_compress</c> asks for it: the row of the source's
    /// size class, a negative level's acceleration as its target length, then the adjustments.
    /// </summary>
    /// <param name="level">A level from <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the default.</param>
    /// <param name="sourceSize">The size of the frame's content.</param>
    /// <param name="longDistanceMatching">
    /// Whether the long-distance matcher is asked for, as by <c>ZSTD_c_enableLongDistanceMatching</c>,
    /// rather than left to libzstd (<see cref="LongDistanceMatcher.EnabledFor"/>).
    /// </param>
    public static CompressionParameters ForFrame(int level, long sourceSize, bool longDistanceMatching = false)
    {
        CompressionParameters parameters = Select(level, (ulong)sourceSize, 0, ParameterMode.Frame);
        if (longDistanceMatching)
        {
            // libzstd's ZSTD_getCParamsFromCCtxParams: the matcher's window, then the adjustments again.
            parameters.WindowLog = LongDistanceMatcher.DefaultWindowLog;
            parameters.Adjust((ulong)sourceSize, 0, ParameterMode.Frame);
        }

        return parameters;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_getCParamsFromCCtxParams</c> for a frame compressed with a prepared dictionary
    /// (<c>ZSTD_CCtx_refCDict</c>) of <paramref name="dictionarySize"/> bytes, all of the buffer: sized
    /// for the source alone when the dictionary is attached, which has its own tables, else for both.
    /// </summary>
    /// <param name="level">The dictionary's level.</param>
    /// <param name="sourceSize">The size of the frame's content.</param>
    /// <param name="dictionarySize">The size of the dictionary's buffer.</param>
    /// <param name="attached">Whether the frame attaches the dictionary.</param>
    /// <param name="longDistanceMatching">Whether the long-distance matcher is asked for, as in <see cref="ForFrame(int, long, bool)"/>.</param>
    public static CompressionParameters ForFrame(int level, long sourceSize, long dictionarySize, bool attached, bool longDistanceMatching = false)
    {
        ParameterMode mode = attached ? ParameterMode.AttachedDictionary : ParameterMode.Frame;
        CompressionParameters parameters = Select(level, (ulong)sourceSize, (ulong)dictionarySize, mode);
        if (longDistanceMatching)
        {
            parameters.WindowLog = LongDistanceMatcher.DefaultWindowLog;
            parameters.Adjust((ulong)sourceSize, (ulong)dictionarySize, mode);
        }

        return parameters;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_createCDict_byReference</c>: the parameters of a prepared dictionary of
    /// <paramref name="dictionarySize"/> bytes, for sources of unknown size, which it assumes small.
    /// </summary>
    public static CompressionParameters ForDictionary(int level, long dictionarySize)
    {
        ulong size = (ulong)dictionarySize;
        CompressionParameters parameters = Select(level, UnknownSize, size, ParameterMode.CreateDictionary);

        // ZSTD_createCDict_advanced2: the default level's parameters, which these override but for a
        // target length of 0, adjusted again.
        if (parameters.TargetLength == 0)
        {
            parameters.TargetLength = Select(DefaultLevel, UnknownSize, size, ParameterMode.CreateDictionary).TargetLength;
        }

        parameters.Adjust(UnknownSize, size, ParameterMode.CreateDictionary);
        return parameters;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_resetCCtx_byAttachingCDict</c>: a prepared dictionary's parameters fitted to a
    /// source alone (the dictionary keeps its own tables), the window the frame's.
    /// </summary>
    public readonly CompressionParameters ForAttachedSource(long sourceSize, long dictionarySize, int windowLog)
    {
        CompressionParameters parameters = this;
        parameters.Adjust((ulong)sourceSize, (ulong)dictionarySize, ParameterMode.AttachedDictionary);
        return parameters.WithWindow(windowLog) with { RowMatchFinder = UsesRowMatchFinder ? (sbyte)1 : (sbyte)-1 };
    }

    /// <summary>libzstd's <c>ZSTD_getCParams_internal</c>: a level's row for the sizes, adjusted.</summary>
    private static CompressionParameters Select(int level, ulong sourceSize, ulong dictionarySize, ParameterMode mode)
    {
        ulong rowSize = RowSize(sourceSize, dictionarySize, mode);
        int table = (rowSize <= 256 << 10 ? 1 : 0) + (rowSize <= 128 << 10 ? 1 : 0) + (rowSize <= 16 << 10 ? 1 : 0);
        int row = level == 0 ? DefaultLevel : level < 0 ? 0 : Math.Min(level, MaxLevel);
        ReadOnlySpan<short> values = Rows.Slice(((table * (MaxLevel + 1)) + row) * 7, 7);
        var parameters = new CompressionParameters
        {
            WindowLog = values[0],
            ChainLog = values[1],
            HashLog = values[2],
            SearchLog = values[3],
            MinMatch = values[4],
            TargetLength = values[5],
            Strategy = (Strategy)values[6],
        };

        if (level < 0)
        {
            parameters.TargetLength = -Math.Max(MinLevel, level);
        }

        parameters.Adjust(sourceSize, dictionarySize, mode);
        return parameters;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_getCParamRowSize</c>: the size that picks the table. An unknown source with
    /// a dictionary counts as the dictionary and 499 bytes: libzstd adds 500 to its unknown size, -1.
    /// </summary>
    private static ulong RowSize(ulong sourceSize, ulong dictionarySize, ParameterMode mode)
    {
        if (mode == ParameterMode.AttachedDictionary)
        {
            dictionarySize = 0;
        }

        bool unknown = sourceSize == UnknownSize;
        ulong added = unknown && dictionarySize > 0 ? 500UL : 0;
        return unknown && dictionarySize == 0 ? UnknownSize : unchecked(sourceSize + dictionarySize + added);
    }

    /// <summary>The strongest strategy implemented so far: the stronger ones cascade down to it.</summary>
    public const Strategy StrongestImplemented = Strategy.BinaryTreeUltra2;

    /// <summary>
    /// The strategy libzstd uses for <paramref name="level"/> and a source of <paramref name="sourceSize"/>
    /// bytes, before any cascade: the frames are libzstd's, byte for byte, when it is implemented.
    /// </summary>
    public static Strategy LibzstdStrategy(int level, long sourceSize)
    {
        ulong size = (ulong)sourceSize;
        int table = (size <= 256 << 10 ? 1 : 0) + (size <= 128 << 10 ? 1 : 0) + (size <= 16 << 10 ? 1 : 0);
        int row = level == 0 ? DefaultLevel : level < 0 ? 0 : Math.Min(level, MaxLevel);
        return (Strategy)Rows[(((table * (MaxLevel + 1)) + row) * 7) + 6];
    }

    /// <summary>
    /// libzstd's <c>ZSTD_adjustCParams_internal</c>: the window shrunk to the source and dictionary,
    /// the tables to the window.
    /// </summary>
    private void Adjust(ulong sourceSize, ulong dictionarySize, ParameterMode mode)
    {
        const ulong MinSourceSize = 513;
        const ulong MaxWindowResize = 1UL << 30;

        // A strategy this build lacks cascades down to the strongest it has, as in a libzstd built
        // with ZSTD_EXCLUDE_..._BLOCK_COMPRESSOR.
        if (Strategy > StrongestImplemented)
        {
            Strategy = StrongestImplemented;
        }

        if (mode == ParameterMode.CreateDictionary && dictionarySize != 0 && sourceSize == UnknownSize)
        {
            // A small source assumed.
            sourceSize = MinSourceSize;
        }
        else if (mode == ParameterMode.AttachedDictionary)
        {
            // The dictionary has its own tables, and parameters.
            dictionarySize = 0;
        }

        if (sourceSize <= MaxWindowResize && dictionarySize <= MaxWindowResize)
        {
            uint size = (uint)(sourceSize + dictionarySize);
            int sourceLog = size < 1u << 6 ? 6 : BitOperations.Log2(size - 1) + 1;
            WindowLog = Math.Min(WindowLog, sourceLog);
        }

        if (sourceSize != UnknownSize)
        {
            // ZSTD_cycleLog: the binary trees hold two entries a position in their chain table.
            int windowLog = DictionaryAndWindowLog(WindowLog, sourceSize, dictionarySize);
            int cycleLog = ChainLog - (Strategy >= Strategy.BinaryTreeLazy2 ? 1 : 0);
            HashLog = Math.Min(HashLog, windowLog + 1);
            if (cycleLog > windowLog)
            {
                ChainLog -= cycleLog - windowLog;
            }
        }

        WindowLog = Math.Max(WindowLog, FrameFormat.WindowLogMin);

        // A prepared dictionary's fast tables keep an 8-bit tag beside each index (ZSTD_SHORT_CACHE).
        if (mode == ParameterMode.CreateDictionary && Strategy <= Strategy.DoubleFast)
        {
            HashLog = Math.Min(HashLog, 32 - DictionaryTagBits);
            ChainLog = Math.Min(ChainLog, 32 - DictionaryTagBits);
        }

        // The row-based match finder, which libzstd assumes in use, hashes at most 32 bits with its tags.
        if (Strategy is >= Strategy.Greedy and <= Strategy.Lazy2)
        {
            int rowLog = Math.Clamp(SearchLog, 4, 6);
            HashLog = Math.Min(HashLog, 24 + rowLog);
        }
    }

    /// <summary>libzstd's <c>ZSTD_SHORT_CACHE_TAG_BITS</c>: the tag beside the indices of a prepared dictionary's fast tables.</summary>
    public const int DictionaryTagBits = 8;

    /// <summary>
    /// libzstd's <c>ZSTD_dictAndWindowLog</c>: the window, grown to hold the dictionary with it when
    /// it would not hold the source and the dictionary.
    /// </summary>
    private static int DictionaryAndWindowLog(int windowLog, ulong sourceSize, ulong dictionarySize)
    {
        const ulong MaxWindowSize = 1UL << 31;
        if (dictionarySize == 0)
        {
            return windowLog;
        }

        ulong windowSize = 1UL << windowLog;
        ulong dictionaryAndWindowSize = dictionarySize + windowSize;
        if (windowSize >= dictionarySize + sourceSize)
        {
            return windowLog;
        }

        return dictionaryAndWindowSize >= MaxWindowSize ? 31 : BitOperations.Log2((uint)dictionaryAndWindowSize - 1) + 1;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_resolveRowMatchFinderMode</c> on a machine with 128-bit vectors (arm64, x64):
    /// the lazy strategies search rows once the window passes 16 KiB, hash chains below.
    /// </summary>
    public readonly bool UsesRowMatchFinder =>
        RowMatchFinder != 0 ? RowMatchFinder > 0 : Strategy is >= Strategy.Greedy and <= Strategy.Lazy2 && WindowLog > 14;

    /// <summary>These parameters with <paramref name="windowLog"/>, the row decision kept as these make it.</summary>
    public readonly CompressionParameters WithWindow(int windowLog)
    {
        CompressionParameters parameters = this;
        parameters.RowMatchFinder = UsesRowMatchFinder ? (sbyte)1 : (sbyte)-1;
        parameters.WindowLog = windowLog;
        return parameters;
    }

    /// <summary>The largest block of the frame: libzstd's <c>blockSizeMax</c>.</summary>
    public readonly int BlockSizeMax(long sourceSize) =>
        (int)Math.Min(FrameFormat.MaxBlockSize, Math.Max(1, Math.Min(1L << WindowLog, sourceSize)));
}
