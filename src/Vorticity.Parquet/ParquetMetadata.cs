using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Vorticity.Parquet.Schema;
using Vorticity.Types.Numerics;

namespace Vorticity.Parquet;

/// <summary>
/// What a Parquet file's footer says of it, for inspection: its writer, its columns with their types
/// as the standard spells them, its row groups and their column chunks, with their codecs, encodings,
/// statistics and the structures beside them, and its key-value pairs. Nothing a scan needs.
/// </summary>
public sealed class ParquetMetadata
{
    internal ParquetMetadata(ParquetFile file)
    {
        ParquetFooter footer = file.Footer;
        ReadOnlySpan<byte> bytes = footer.Bytes;
        CreatedBy = footer.CreatedBy.IsPresent ? Encoding.UTF8.GetString(footer.CreatedBy.Of(bytes)) : null;
        Version = footer.Version;
        RowCount = footer.RowCount;
        KeyValues = file.KeyValueMetadata;

        ParquetColumn[] leaves = file.Compiled.Columns;
        ParquetColumnInfo[] columns = new ParquetColumnInfo[leaves.Length];
        for (int c = 0; c < leaves.Length; c++)
        {
            ParquetColumn leaf = leaves[c];
            SchemaElement element = footer.Schema[leaf.Element];
            columns[c] = new ParquetColumnInfo(
                leaf.DottedPath,
                Physical(leaf.Physical),
                leaf.Physical == PhysicalType.FixedLenByteArray ? leaf.TypeLength : 0,
                Logical(leaf.Logical),
                leaf.MaxDefinitionLevel,
                leaf.MaxRepetitionLevel,
                element.HasFieldId ? element.FieldId : null,
                Order(leaf.Order),
                leaf.Type);
        }

        Columns = columns;
        ParquetRowGroupInfo[] groups = new ParquetRowGroupInfo[footer.RowGroups.Length];
        for (int g = 0; g < groups.Length; g++)
        {
            RowGroupEntry entry = footer.RowGroups[g];
            ParquetChunkInfo[] chunks = new ParquetChunkInfo[leaves.Length];
            for (int c = 0; c < leaves.Length; c++)
            {
                chunks[c] = Chunk(file, leaves[c], footer.Chunk(g, c));
            }

            groups[g] = new ParquetRowGroupInfo(g, entry.FirstRow, entry.RowCount, chunks);
        }

        RowGroups = groups;
    }

    /// <summary>The writer, as the footer names it; null when it does not.</summary>
    public string? CreatedBy { get; }

    /// <summary>The format version the footer declares.</summary>
    public int Version { get; }

    /// <summary>The rows the footer counts.</summary>
    public long RowCount { get; }

    /// <summary>The leaf columns, in the order a row group's column chunks follow.</summary>
    public IReadOnlyList<ParquetColumnInfo> Columns { get; }

    /// <summary>The row groups, in the file's order.</summary>
    public IReadOnlyList<ParquetRowGroupInfo> RowGroups { get; }

    /// <summary>The key-value pairs the footer carries, as <see cref="ParquetFile.KeyValueMetadata"/> gives them.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> KeyValues { get; }

    private static ParquetChunkInfo Chunk(ParquetFile file, ParquetColumn column, ColumnChunkMetadata chunk)
    {
        List<string> encodings = [];
        for (int encoding = 0; encoding < 32; encoding++)
        {
            if ((chunk.Encodings & (1u << encoding)) != 0)
            {
                encodings.Add(EncodingName((ParquetEncoding)encoding));
            }
        }

        return new ParquetChunkInfo(
            column.DottedPath,
            Codec(chunk.Codec),
            encodings,
            chunk.ValueCount,
            chunk.Start,
            chunk.TotalCompressedSize,
            chunk.TotalUncompressedSize,
            Statistics(file, column, chunk.Statistics),
            chunk.ColumnIndexOffset >= 0,
            chunk.OffsetIndexOffset >= 0,
            chunk.BloomFilterOffset >= 0,
            chunk.HasEncodingStats ? chunk.AllDataPagesDictionary : null);
    }

    private static ParquetStatisticsInfo? Statistics(ParquetFile file, ParquetColumn column, in ColumnStatistics statistics)
    {
        if (!statistics.IsPresent)
        {
            return null;
        }

        ZoneBounds bounds = ColumnBounds.Of(column, statistics, file.Footer.Bytes);
        bool decimals = ColumnBounds.IsDecimal(column);
        return new ParquetStatisticsInfo(
            statistics.HasNullCount ? statistics.NullCount : null,
            statistics.HasDistinctCount ? statistics.DistinctCount : null,
            bounds.HasMin ? Render(bounds.Min, column, decimals) : null,
            bounds.HasMax ? Render(bounds.Max, column, decimals) : null,
            statistics.IsMinValueExact,
            statistics.IsMaxValueExact,
            statistics.HasNanCount ? statistics.NanCount : null);
    }

    /// <summary>A bound as its value: a decimal at its scale, text in quotes, other bytes in hexadecimal.</summary>
    private static string Render(FilterLiteral value, ParquetColumn column, bool decimals)
    {
        if (decimals && ComparisonKernels.TryDecimal(value, out Int256 unscaled))
        {
            string digits = unscaled.ToString();
            int scale = column.Logical.Scale;
            if (scale <= 0)
            {
                return digits;
            }

            bool negative = digits.StartsWith('-');
            string magnitude = (negative ? digits[1..] : digits).PadLeft(scale + 1, '0');
            return (negative ? "-" : string.Empty) + magnitude[..^scale] + "." + magnitude[^scale..];
        }

        return value.Kind switch
        {
            FilterLiteralKind.Bool => value.BoolValue ? "true" : "false",
            FilterLiteralKind.Signed => value.SignedValue.ToString(CultureInfo.InvariantCulture),
            FilterLiteralKind.Unsigned => value.UnsignedValue.ToString(CultureInfo.InvariantCulture),
            FilterLiteralKind.Float => value.FloatValue.ToString("R", CultureInfo.InvariantCulture),
            FilterLiteralKind.Bytes when column.Form == LeafForm.Utf8 => "'" + System.Text.Encoding.UTF8.GetString(value.BytesValue) + "'",
            FilterLiteralKind.Bytes => "0x" + Convert.ToHexString(value.BytesValue),
            _ => value.ToString() ?? string.Empty,
        };
    }

    internal static string Physical(PhysicalType type) => type switch
    {
        PhysicalType.Boolean => "BOOLEAN",
        PhysicalType.Int32 => "INT32",
        PhysicalType.Int64 => "INT64",
        PhysicalType.Int96 => "INT96",
        PhysicalType.Float => "FLOAT",
        PhysicalType.Double => "DOUBLE",
        PhysicalType.ByteArray => "BYTE_ARRAY",
        PhysicalType.FixedLenByteArray => "FIXED_LEN_BYTE_ARRAY",
        _ => ((int)type).ToString(CultureInfo.InvariantCulture),
    };

    private static string Codec(CompressionCodec codec) => codec switch
    {
        CompressionCodec.Uncompressed => "UNCOMPRESSED",
        CompressionCodec.Snappy => "SNAPPY",
        CompressionCodec.Gzip => "GZIP",
        CompressionCodec.Lzo => "LZO",
        CompressionCodec.Brotli => "BROTLI",
        CompressionCodec.Lz4 => "LZ4",
        CompressionCodec.Zstd => "ZSTD",
        CompressionCodec.Lz4Raw => "LZ4_RAW",
        _ => ((int)codec).ToString(CultureInfo.InvariantCulture),
    };

    internal static string EncodingName(ParquetEncoding encoding) => encoding switch
    {
        ParquetEncoding.Plain => "PLAIN",
        ParquetEncoding.PlainDictionary => "PLAIN_DICTIONARY",
        ParquetEncoding.Rle => "RLE",
        ParquetEncoding.BitPacked => "BIT_PACKED",
        ParquetEncoding.DeltaBinaryPacked => "DELTA_BINARY_PACKED",
        ParquetEncoding.DeltaLengthByteArray => "DELTA_LENGTH_BYTE_ARRAY",
        ParquetEncoding.DeltaByteArray => "DELTA_BYTE_ARRAY",
        ParquetEncoding.RleDictionary => "RLE_DICTIONARY",
        ParquetEncoding.ByteStreamSplit => "BYTE_STREAM_SPLIT",
        ParquetEncoding.Alp => "ALP",
        _ => ((int)encoding).ToString(CultureInfo.InvariantCulture),
    };

    private static string Order(ColumnOrderKind order) => order switch
    {
        ColumnOrderKind.TypeDefined => "TYPE_ORDER",
        ColumnOrderKind.Ieee754TotalOrder => "IEEE_754_TOTAL_ORDER",
        ColumnOrderKind.Undefined => "undefined",
        _ => "unknown",
    };

    private static string? Logical(in LogicalTypeInfo logical) => logical.Kind switch
    {
        LogicalTypeKind.None => null,
        LogicalTypeKind.String => "STRING",
        LogicalTypeKind.Map => "MAP",
        LogicalTypeKind.List => "LIST",
        LogicalTypeKind.Enum => "ENUM",
        LogicalTypeKind.Decimal => $"DECIMAL({logical.Precision}, {logical.Scale})",
        LogicalTypeKind.Date => "DATE",
        LogicalTypeKind.Time => $"TIME({Unit(logical.Unit)}, {(logical.IsAdjustedToUtc ? "UTC" : "local")})",
        LogicalTypeKind.Timestamp => $"TIMESTAMP({Unit(logical.Unit)}, {(logical.IsAdjustedToUtc ? "UTC" : "local")})",
        LogicalTypeKind.Integer => $"INT({logical.BitWidth}, {(logical.IsSigned ? "signed" : "unsigned")})",
        LogicalTypeKind.Unknown => "UNKNOWN",
        LogicalTypeKind.Json => "JSON",
        LogicalTypeKind.Bson => "BSON",
        LogicalTypeKind.Uuid => "UUID",
        LogicalTypeKind.Float16 => "FLOAT16",
        LogicalTypeKind.Variant => $"VARIANT({logical.VariantVersion})",
        LogicalTypeKind.Geometry => logical.Crs is { } crs ? $"GEOMETRY({crs})" : "GEOMETRY",
        LogicalTypeKind.Geography => logical.Crs is { } crs ? $"GEOGRAPHY({crs})" : "GEOGRAPHY",
        LogicalTypeKind.File => "FILE",
        _ => "unrecognized",
    };

    private static string Unit(ParquetTimeUnit unit) => unit switch
    {
        ParquetTimeUnit.Millis => "MILLIS",
        ParquetTimeUnit.Micros => "MICROS",
        ParquetTimeUnit.Nanos => "NANOS",
        _ => "unknown",
    };
}

/// <summary>A leaf column, as the footer's schema describes it.</summary>
/// <param name="Path">Its path, its names joined by dots.</param>
/// <param name="PhysicalType">Its physical type as the standard spells it: <c>INT32</c>, <c>BYTE_ARRAY</c>…</param>
/// <param name="TypeLength">A <c>FIXED_LEN_BYTE_ARRAY</c>'s bytes; 0 for another type.</param>
/// <param name="LogicalType">Its logical type as the standard spells it, its parameters in parentheses; null for none.</param>
/// <param name="MaxDefinitionLevel">The greatest definition level of its entries.</param>
/// <param name="MaxRepetitionLevel">The greatest repetition level of its entries.</param>
/// <param name="FieldId">The field id its schema element carries, or null.</param>
/// <param name="ColumnOrder">The order its statistics are in: <c>TYPE_ORDER</c>, <c>IEEE_754_TOTAL_ORDER</c>, or <c>undefined</c>.</param>
/// <param name="Type">The type it reads as.</param>
public sealed record ParquetColumnInfo(
    string Path,
    string PhysicalType,
    int TypeLength,
    string? LogicalType,
    int MaxDefinitionLevel,
    int MaxRepetitionLevel,
    int? FieldId,
    string ColumnOrder,
    VortexType Type);

/// <summary>A row group: its rows and its column chunks.</summary>
/// <param name="Ordinal">Its place in the file.</param>
/// <param name="FirstRow">The file's row it starts at.</param>
/// <param name="RowCount">Its rows.</param>
/// <param name="Chunks">Its column chunks, one per leaf column, in their order.</param>
public sealed record ParquetRowGroupInfo(int Ordinal, long FirstRow, long RowCount, IReadOnlyList<ParquetChunkInfo> Chunks);

/// <summary>A column chunk, as the footer describes it.</summary>
/// <param name="Column">Its column's path.</param>
/// <param name="Codec">Its codec as the standard spells it: <c>ZSTD</c>, <c>SNAPPY</c>…</param>
/// <param name="Encodings">The encodings its pages use, as the standard spells them.</param>
/// <param name="Values">Its entries, nulls included.</param>
/// <param name="Offset">The byte its first page starts at.</param>
/// <param name="CompressedBytes">Its bytes in the file.</param>
/// <param name="UncompressedBytes">Its pages' bytes before compression.</param>
/// <param name="Statistics">Its statistics, or null when it has none.</param>
/// <param name="HasColumnIndex">Whether a column index bounds its pages.</param>
/// <param name="HasOffsetIndex">Whether an offset index places its pages.</param>
/// <param name="HasBloomFilter">Whether a Bloom filter holds its values.</param>
/// <param name="AllDataPagesDictionary">Whether its <c>encoding_stats</c> say every data page is dictionary codes; null without them.</param>
public sealed record ParquetChunkInfo(
    string Column,
    string Codec,
    IReadOnlyList<string> Encodings,
    long Values,
    long Offset,
    long CompressedBytes,
    long UncompressedBytes,
    ParquetStatisticsInfo? Statistics,
    bool HasColumnIndex,
    bool HasOffsetIndex,
    bool HasBloomFilter,
    bool? AllDataPagesDictionary);

/// <summary>A column chunk's statistics.</summary>
/// <param name="NullCount">Its nulls, or null when not counted.</param>
/// <param name="DistinctCount">Its distinct values, or null when not counted.</param>
/// <param name="Min">Its least value, rendered, where a column order this build knows makes it a bound; else null.</param>
/// <param name="Max">Its greatest value, likewise.</param>
/// <param name="MinExact">Whether <see cref="Min"/> is the least value itself rather than a bound below it.</param>
/// <param name="MaxExact">Whether <see cref="Max"/> is the greatest value itself.</param>
/// <param name="NanCount">Its NaN values, or null when not counted.</param>
public sealed record ParquetStatisticsInfo(long? NullCount, long? DistinctCount, string? Min, string? Max, bool MinExact, bool MaxExact, long? NanCount);

/// <summary>Something a file says of itself that its rows do not bear out.</summary>
/// <param name="RowGroup">The row group it is said of, or -1 for the file.</param>
/// <param name="Column">The column's path, or null for the file.</param>
/// <param name="Structure">What says it: <c>null_count</c>, <c>column index</c>, <c>bloom filter</c>…</param>
/// <param name="Message">What the rows hold instead.</param>
public sealed record ParquetFinding(int RowGroup, string? Column, string Structure, string Message)
{
    /// <summary>The finding as one line.</summary>
    public override string ToString() =>
        RowGroup < 0 ? $"{Structure}: {Message}" : $"row group {RowGroup}, {Column}: {Structure}: {Message}";
}
