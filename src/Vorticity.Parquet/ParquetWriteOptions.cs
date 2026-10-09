using System;
using Vorticity.Zstd;

namespace Vorticity.Parquet;

/// <summary>The codec a Parquet writer compresses its pages with.</summary>
public enum ParquetCompression : byte
{
    /// <summary>No compression.</summary>
    Uncompressed = 0,

    /// <summary>Snappy, this library's own.</summary>
    Snappy = 1,

    /// <summary>GZIP, through the base class library.</summary>
    Gzip = 2,

    /// <summary>Brotli, through the base class library.</summary>
    Brotli = 4,

    /// <summary>Zstandard, through Vorticity.Zstd.</summary>
    Zstd = 6,

    /// <summary>The LZ4 block format, this library's own.</summary>
    Lz4Raw = 7,
}

/// <summary>What a Parquet file looks like: its row groups, its pages and its compression.</summary>
public sealed record ParquetWriteOptions
{
    /// <summary>The options a writer takes when it is given none.</summary>
    public static ParquetWriteOptions Default { get; } = new();

    /// <summary>
    /// The rows of a page, counted from the file's first row in every column, so that every column
    /// cuts its pages at the same rows. 8 192 by default.
    /// </summary>
    public int BlockRows { get; init; } = 8_192;

    /// <summary>The rows of a row group, a multiple of <see cref="BlockRows"/>. 1 048 576 by default.</summary>
    public int RowGroupRows { get; init; } = 1_048_576;

    /// <summary>
    /// The bytes a row group's pages may reach before it closes, at the block that reaches them:
    /// 256 MiB by default. A writer holds the pages of the row group it writes, so this bounds it.
    /// </summary>
    public long RowGroupBytes { get; init; } = 256L << 20;

    /// <summary>
    /// What the writer optimises for. <see cref="CompressionProfile.Auto"/>, the default, and
    /// <see cref="CompressionProfile.Fastest"/> and <see cref="CompressionProfile.Smallest"/> write a
    /// dictionary where it pays; <see cref="CompressionProfile.None"/> writes PLAIN pages alone.
    /// </summary>
    public CompressionProfile Profile { get; init; } = CompressionProfile.Auto;

    /// <summary>
    /// The codec of every page; null for the profile's: ZSTD under <see cref="CompressionProfile.Auto"/>
    /// and <see cref="CompressionProfile.Smallest"/>, LZ4_RAW under <see cref="CompressionProfile.Fastest"/>,
    /// none under <see cref="CompressionProfile.None"/>.
    /// </summary>
    public ParquetCompression? Compression { get; init; }

    /// <summary>
    /// The codec's level, for the codecs that take one; 0 for the codec's default: 3 for ZSTD, 19
    /// under <see cref="CompressionProfile.Smallest"/>, 4 for BROTLI and 6 for GZIP.
    /// </summary>
    public int CompressionLevel { get; init; }

    /// <summary>Whether completing the file puts it on the device before the call returns.</summary>
    public bool Durable { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockRows, 1, nameof(BlockRows));
        ArgumentOutOfRangeException.ThrowIfLessThan(RowGroupRows, BlockRows, nameof(RowGroupRows));
        if (RowGroupRows % BlockRows != 0)
        {
            throw new ArgumentException($"A row group of {RowGroupRows} rows is not a whole number of blocks of {BlockRows}.", nameof(RowGroupRows));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(RowGroupBytes, 1, nameof(RowGroupBytes));
        if (!Enum.IsDefined(Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(Profile), Profile, "Not a profile.");
        }

        if (Compression is { } codec && !Enum.IsDefined(codec))
        {
            throw new ArgumentOutOfRangeException(nameof(Compression), codec, "Not a codec this library writes.");
        }

        (int least, int most) = ResolvedCompression switch
        {
            ParquetCompression.Zstd => (ZstdCompressor.MinLevel, ZstdCompressor.MaxLevel),
            ParquetCompression.Brotli => (0, 11),
            ParquetCompression.Gzip => (0, 9),
            _ => (0, 0),
        };
        if (CompressionLevel < least || CompressionLevel > most)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                most == 0 ? $"{ResolvedCompression} takes no level." : $"{ResolvedCompression} takes a level from {least} to {most}.");
        }
    }

    /// <summary>The codec of the pages: the one asked for, or the profile's.</summary>
    internal ParquetCompression ResolvedCompression => Compression ?? Profile switch
    {
        CompressionProfile.Fastest => ParquetCompression.Lz4Raw,
        CompressionProfile.None => ParquetCompression.Uncompressed,
        _ => ParquetCompression.Zstd,
    };

    /// <summary>The level the pages are compressed at: the one asked for, or the codec's default.</summary>
    internal int ResolvedLevel => CompressionLevel != 0 ? CompressionLevel : ResolvedCompression switch
    {
        ParquetCompression.Zstd => Profile == CompressionProfile.Smallest ? 19 : ZstdCompressor.DefaultLevel,
        ParquetCompression.Brotli => 4,
        ParquetCompression.Gzip => 6,
        _ => 0,
    };

    /// <summary>Whether the profile writes a dictionary where it pays.</summary>
    internal bool Dictionaries => Profile != CompressionProfile.None;
}

/// <summary>What a Parquet writer wrote.</summary>
/// <param name="RowCount">The rows.</param>
/// <param name="RowGroups">The row groups.</param>
/// <param name="Bytes">The file's bytes.</param>
public sealed record ParquetWriteReport(long RowCount, int RowGroups, long Bytes);
