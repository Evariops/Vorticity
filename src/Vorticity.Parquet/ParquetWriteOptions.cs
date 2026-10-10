using System;
using System.Collections.Generic;
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

/// <summary>A codec and its level, which a column may name in place of the file's.</summary>
/// <param name="Compression">The codec.</param>
/// <param name="Level">
/// The codec's level, for the codecs that take one; 0 for the codec's default: 3 for ZSTD, 19 under
/// <see cref="CompressionProfile.Smallest"/>, 4 for BROTLI and 6 for GZIP.
/// </param>
public readonly record struct ParquetCodec(ParquetCompression Compression, int Level = 0);

/// <summary>
/// The encoding a column's data pages are written with, when the caller pins one, named as the
/// standard spells it.
/// </summary>
public enum ParquetEncodingHint : byte
{
    /// <summary>No hint: the writer prices the candidates, which is the default.</summary>
    Auto = 0,

    /// <summary>PLAIN: the values as they are, for every type.</summary>
    Plain = 1,

    /// <summary>
    /// RLE_DICTIONARY: a dictionary page and each value's code, while the dictionary stays within
    /// its bound, and PLAIN past it; for every type but BOOLEAN and a fixed-size list's bytes.
    /// </summary>
    Dictionary = 2,

    /// <summary>DELTA_BINARY_PACKED: INT32 and INT64.</summary>
    DeltaBinaryPacked = 3,

    /// <summary>DELTA_LENGTH_BYTE_ARRAY: BYTE_ARRAY.</summary>
    DeltaLengthByteArray = 4,

    /// <summary>DELTA_BYTE_ARRAY: BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY.</summary>
    DeltaByteArray = 5,

    /// <summary>BYTE_STREAM_SPLIT: INT32, INT64, FLOAT, DOUBLE and FIXED_LEN_BYTE_ARRAY.</summary>
    ByteStreamSplit = 6,

    /// <summary>RLE: BOOLEAN.</summary>
    Rle = 7,

    /// <summary>
    /// ALP: FLOAT and DOUBLE, decimals of few digits as integers, the standard's Preview encoding,
    /// which a reader that predates it does not read.
    /// </summary>
    Alp = 8,
}

/// <summary>The form of the data pages a Parquet writer writes.</summary>
public enum DataPageVersion : byte
{
    /// <summary>
    /// Data pages v1, for readers that read no other: the levels inside the compressed bytes, each
    /// kind behind its length, and every page compressed whatever it saves.
    /// </summary>
    V1 = 1,

    /// <summary>
    /// Data pages v2, the default: the levels ahead of the compressed values, which a reader then
    /// decompresses straight into place, and a page that does not shrink stored as it is.
    /// </summary>
    V2 = 2,
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
    /// The bytes a block's values may reach in one page: a block past them is written as pages of
    /// power-of-two fractions of its rows, halved while they pass, down to 1 024 rows, and pages of
    /// those that still pass are cut by bytes. 1 MiB by default. A page the dictionary codes is written
    /// whole, its codes far smaller than its values.
    /// </summary>
    public int PageBytes { get; init; } = 1 << 20;

    /// <summary>
    /// What the writer optimises for. <see cref="CompressionProfile.Auto"/>, the default, and
    /// <see cref="CompressionProfile.Smallest"/> write a dictionary where it pays, and otherwise the
    /// encoding of each page that saves the most: DELTA_BINARY_PACKED, the byte array deltas,
    /// BYTE_STREAM_SPLIT by trial, RLE for booleans. <see cref="CompressionProfile.Fastest"/> writes
    /// dictionaries and PLAIN; <see cref="CompressionProfile.None"/> writes PLAIN pages alone.
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

    /// <summary>
    /// The codecs of the columns that name their own, by name — a nested column by its dotted path —
    /// in place of <see cref="Compression"/> and <see cref="CompressionLevel"/>. None by default. A
    /// name no column has throws when the writer is created.
    /// </summary>
    public IReadOnlyDictionary<string, ParquetCodec>? ColumnCompression { get; init; }

    /// <summary>
    /// The encoding to write a column's data pages with, by name — a nested column by its dotted path —
    /// for a caller who knows, as the core's hints pin a scheme. A pinned encoding is written on every
    /// page under every profile, unpriced: a dictionary while it stays within its bound and PLAIN past
    /// it, any other always. None by default. An encoding the column's type does not take, or a name
    /// no column has, throws when the writer is created.
    /// </summary>
    public IReadOnlyDictionary<string, ParquetEncodingHint>? Hints { get; init; }

    /// <summary>
    /// The form of the data pages: <see cref="Parquet.DataPageVersion.V2"/> by default, which a reader
    /// decompresses into place; <see cref="Parquet.DataPageVersion.V1"/> for a reader that reads no other.
    /// Rows are never split across pages in either.
    /// </summary>
    public DataPageVersion DataPageVersion { get; init; } = DataPageVersion.V2;

    /// <summary>
    /// Whether a page whose values are stored as they are, uncompressed, starts them on a 64-byte
    /// boundary of the file, behind an extension of its header that every reader skips: 21 to 84
    /// bytes a page, for a reader that maps the file to take the values where they lie, aligned as a
    /// batch's values are. On by default.
    /// </summary>
    public bool AlignUncompressedPages { get; init; } = true;

    /// <summary>
    /// Whether FLOAT and DOUBLE pages may be written ALP, decimals of few digits as small integers,
    /// which a trial of each chunk's first page then weighs against PLAIN and BYTE_STREAM_SPLIT. Off
    /// by default: the standard marks ALP Preview, and a reader that predates it cannot read such a
    /// page.
    /// </summary>
    public bool Alp { get; init; }

    /// <summary>Whether completing the file puts it on the device before the call returns.</summary>
    public bool Durable { get; init; }

    /// <summary>
    /// How many threads the columns close their pages on, encoding and compressing them side by side;
    /// 0, the default, for the session's <see cref="VortexSessionOptions.MaxDegreeOfParallelism"/>. The file
    /// is the same bytes whatever the degree.
    /// </summary>
    public int DegreeOfParallelism { get; init; }

    /// <summary>
    /// The columns that get a Bloom filter in every row group, by name — a nested column by its
    /// dotted path — each with the false-positive rate it is sized for, above 0 and below 1. None by
    /// default. A filter is the standard's split-block filter of xxHash64, which a reader probes for
    /// an equality or an IN; a boolean column gets none.
    /// </summary>
    public IReadOnlyDictionary<string, double>? BloomFilters { get; init; }

    /// <summary>
    /// Whether each page carries a checksum, the CRC-32 of its bytes as stored past its header, which
    /// a reader may hold it to. Off by default: a reader that does not check one pays for its bytes.
    /// </summary>
    public bool WriteChecksums { get; init; }

    /// <summary>
    /// Key-value pairs the footer carries, written in the ordinal order of their keys after the one this
    /// package writes itself, <c>vorticity.schema</c>: the Vortex schema the file is written from, which
    /// no pair may name. None by default.
    /// </summary>
    public IReadOnlyDictionary<string, string>? KeyValueMetadata { get; init; }

    internal void Validate()
    {
        if (KeyValueMetadata is { } pairs)
        {
            foreach ((string key, string value) in pairs)
            {
                if (key == Schema.ParquetSchema.VortexSchemaKey)
                {
                    throw new ArgumentException($"The key '{key}' is this package's own: it carries the Vortex schema the file is written from.", nameof(KeyValueMetadata));
                }

                ArgumentNullException.ThrowIfNull(value, nameof(KeyValueMetadata));
            }
        }

        if (BloomFilters is { } blooms)
        {
            foreach ((string column, double rate) in blooms)
            {
                if (!(rate > 0 && rate < 1))
                {
                    throw new ArgumentOutOfRangeException(nameof(BloomFilters), rate, $"The Bloom filter of '{column}' asks a false-positive rate outside (0, 1).");
                }
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(BlockRows, 1, nameof(BlockRows));
        ArgumentOutOfRangeException.ThrowIfLessThan(RowGroupRows, BlockRows, nameof(RowGroupRows));
        if (RowGroupRows % BlockRows != 0)
        {
            throw new ArgumentException($"A row group of {RowGroupRows} rows is not a whole number of blocks of {BlockRows}.", nameof(RowGroupRows));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(RowGroupBytes, 1, nameof(RowGroupBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(PageBytes, 1, nameof(PageBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(DegreeOfParallelism, nameof(DegreeOfParallelism));
        if (!Enum.IsDefined(Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(Profile), Profile, "Not a profile.");
        }

        if (!Enum.IsDefined(DataPageVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(DataPageVersion), DataPageVersion, "Not a data page version.");
        }

        if (Compression is { } codec && !Enum.IsDefined(codec))
        {
            throw new ArgumentOutOfRangeException(nameof(Compression), codec, "Not a codec this library writes.");
        }

        RequireLevel(ResolvedCompression, CompressionLevel, nameof(CompressionLevel));
        if (ColumnCompression is { } columns)
        {
            foreach ((string column, ParquetCodec own) in columns)
            {
                if (!Enum.IsDefined(own.Compression))
                {
                    throw new ArgumentOutOfRangeException(nameof(ColumnCompression), own.Compression, $"The column '{column}' names no codec this library writes.");
                }

                RequireLevel(own.Compression, own.Level, nameof(ColumnCompression));
            }
        }

        if (Hints is { } hints)
        {
            foreach ((string column, ParquetEncodingHint hint) in hints)
            {
                if (!Enum.IsDefined(hint))
                {
                    throw new ArgumentOutOfRangeException(nameof(Hints), hint, $"The column '{column}' names no encoding.");
                }
            }
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
    internal int ResolvedLevel => LevelOf(ResolvedCompression, CompressionLevel);

    /// <summary>The codec of the column at <paramref name="path"/>: its own, or the file's.</summary>
    internal ParquetCodec CodecOf(string path) =>
        ColumnCompression is { } columns && columns.TryGetValue(path, out ParquetCodec own)
            ? own with { Level = LevelOf(own.Compression, own.Level) }
            : new ParquetCodec(ResolvedCompression, ResolvedLevel);

    /// <summary><paramref name="level"/>, or the default of <paramref name="codec"/> when it is 0.</summary>
    private int LevelOf(ParquetCompression codec, int level) => level != 0 ? level : codec switch
    {
        ParquetCompression.Zstd => Profile == CompressionProfile.Smallest ? 19 : ZstdCompressor.DefaultLevel,
        ParquetCompression.Brotli => 4,
        ParquetCompression.Gzip => 6,
        _ => 0,
    };

    /// <summary>Throws unless <paramref name="codec"/> takes <paramref name="level"/>, 0 meaning its default.</summary>
    private static void RequireLevel(ParquetCompression codec, int level, string name)
    {
        (int least, int most) = codec switch
        {
            ParquetCompression.Zstd => (ZstdCompressor.MinLevel, ZstdCompressor.MaxLevel),
            ParquetCompression.Brotli => (0, 11),
            ParquetCompression.Gzip => (0, 9),
            _ => (0, 0),
        };
        if (level < least || level > most)
        {
            throw new ArgumentOutOfRangeException(
                name,
                level,
                most == 0 ? $"{codec} takes no level." : $"{codec} takes a level from {least} to {most}.");
        }
    }
}

/// <summary>What a Parquet writer wrote.</summary>
/// <param name="RowCount">The rows.</param>
/// <param name="RowGroups">The row groups.</param>
/// <param name="Bytes">The file's bytes.</param>
public sealed record ParquetWriteReport(long RowCount, int RowGroups, long Bytes);
