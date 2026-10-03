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

    /// <summary>
    /// libzstd's <c>ZSTD_getCParams_internal</c> for a frame of <paramref name="sourceSize"/> bytes
    /// compressed without a dictionary, as <c>ZSTD_compress</c> asks for it: the row of the source's
    /// size class, a negative level's acceleration as its target length, then the adjustments.
    /// </summary>
    /// <param name="level">A level from <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the default.</param>
    /// <param name="sourceSize">The size of the frame's content.</param>
    public static CompressionParameters ForFrame(int level, long sourceSize)
    {
        ulong size = (ulong)sourceSize;
        int table = (size <= 256 << 10 ? 1 : 0) + (size <= 128 << 10 ? 1 : 0) + (size <= 16 << 10 ? 1 : 0);
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

        parameters.Adjust(sourceSize);
        return parameters;
    }

    /// <summary>The strongest strategy implemented so far: the stronger ones cascade down to it.</summary>
    public const Strategy StrongestImplemented = Strategy.DoubleFast;

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
    /// libzstd's <c>ZSTD_adjustCParams_internal</c> for a known source size and no dictionary: the
    /// window shrunk to the source, the tables to the window.
    /// </summary>
    private void Adjust(long sourceSize)
    {
        // A strategy this build lacks cascades down to the strongest it has, as in a libzstd built
        // with ZSTD_EXCLUDE_..._BLOCK_COMPRESSOR.
        if (Strategy > StrongestImplemented)
        {
            Strategy = StrongestImplemented;
        }

        if (sourceSize <= 1L << 30)
        {
            uint size = (uint)sourceSize;
            int sourceLog = size < 1u << 6 ? 6 : BitOperations.Log2(size - 1) + 1;
            WindowLog = Math.Min(WindowLog, sourceLog);
        }

        // ZSTD_cycleLog: the binary trees hold two entries a position in their chain table.
        int cycleLog = ChainLog - (Strategy >= Strategy.BinaryTreeLazy2 ? 1 : 0);
        HashLog = Math.Min(HashLog, WindowLog + 1);
        if (cycleLog > WindowLog)
        {
            ChainLog -= cycleLog - WindowLog;
        }

        WindowLog = Math.Max(WindowLog, FrameFormat.WindowLogMin);

        // The row-based match finder, which libzstd assumes in use, hashes at most 32 bits with its tags.
        if (Strategy is >= Strategy.Greedy and <= Strategy.Lazy2)
        {
            int rowLog = Math.Clamp(SearchLog, 4, 6);
            HashLog = Math.Min(HashLog, 24 + rowLog);
        }
    }

    /// <summary>The largest block of the frame: libzstd's <c>blockSizeMax</c>.</summary>
    public readonly int BlockSizeMax(long sourceSize) =>
        (int)Math.Min(FrameFormat.MaxBlockSize, Math.Max(1, Math.Min(1L << WindowLog, sourceSize)));
}
