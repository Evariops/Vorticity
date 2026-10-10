using System;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>A range of the footer's bytes: where a structure, a binary or a list lies in it.</summary>
internal readonly record struct ByteRange(int Start, int Length)
{
    internal static readonly ByteRange None = new(-1, 0);

    internal bool IsPresent => Start >= 0;

    internal ReadOnlySpan<byte> Of(ReadOnlySpan<byte> footer) => IsPresent ? footer.Slice(Start, Length) : default;
}

/// <summary>
/// A column chunk's <c>Statistics</c>, its bounds left as ranges of the footer's bytes: they are
/// plain-encoded values whose meaning depends on the column's type and order, read when a plan
/// needs them.
/// </summary>
internal struct ColumnStatistics
{
    internal bool IsPresent;
    internal ByteRange LegacyMax;
    internal ByteRange LegacyMin;
    internal bool HasNullCount;
    internal long NullCount;
    internal bool HasDistinctCount;
    internal long DistinctCount;
    internal ByteRange MaxValue;
    internal ByteRange MinValue;
    internal bool IsMaxValueExact;
    internal bool IsMinValueExact;
    internal bool HasNanCount;
    internal long NanCount;

    /// <summary>Reads a <c>Statistics</c> whose struct starts at <paramref name="origin"/> in the footer.</summary>
    internal static ColumnStatistics Read(ref ThriftCompactReader reader, int origin)
    {
        ColumnStatistics statistics = default;
        statistics.IsPresent = true;
        statistics.LegacyMax = ByteRange.None;
        statistics.LegacyMin = ByteRange.None;
        statistics.MaxValue = ByteRange.None;
        statistics.MinValue = ByteRange.None;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    statistics.LegacyMax = ReadRange(ref reader, type, origin);
                    break;
                case 2:
                    statistics.LegacyMin = ReadRange(ref reader, type, origin);
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    statistics.NullCount = reader.ReadI64();
                    statistics.HasNullCount = statistics.NullCount >= 0;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    statistics.DistinctCount = reader.ReadI64();
                    statistics.HasDistinctCount = statistics.DistinctCount >= 0;
                    break;
                case 5:
                    statistics.MaxValue = ReadRange(ref reader, type, origin);
                    break;
                case 6:
                    statistics.MinValue = ReadRange(ref reader, type, origin);
                    break;
                case 7:
                    statistics.IsMaxValueExact = ThriftCompactReader.BooleanField(type);
                    break;
                case 8:
                    statistics.IsMinValueExact = ThriftCompactReader.BooleanField(type);
                    break;
                case 9:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    statistics.NanCount = reader.ReadI64();
                    statistics.HasNanCount = statistics.NanCount >= 0;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        return statistics;
    }

    private static ByteRange ReadRange(ref ThriftCompactReader reader, ThriftType type, int origin)
    {
        ThriftCompactReader.Expect(type, ThriftType.Binary);
        int length = reader.ReadBinary().Length;
        return new ByteRange(origin + reader.Position - length, length);
    }

    /// <summary>
    /// Writes these statistics: the counts as set, and <paramref name="min"/> and
    /// <paramref name="max"/> as the bounds where <see cref="MinValue"/> and <see cref="MaxValue"/>
    /// say there are bounds.
    /// </summary>
    internal readonly void Write(ref ThriftCompactWriter writer, ReadOnlySpan<byte> min, ReadOnlySpan<byte> max)
    {
        short saved = writer.BeginStruct();
        if (HasNullCount)
        {
            writer.WriteI64Field(3, NullCount);
        }

        if (HasDistinctCount)
        {
            writer.WriteI64Field(4, DistinctCount);
        }

        if (MaxValue.IsPresent)
        {
            writer.WriteBinaryField(5, max);
        }

        if (MinValue.IsPresent)
        {
            writer.WriteBinaryField(6, min);
        }

        if (MaxValue.IsPresent)
        {
            writer.WriteBooleanField(7, IsMaxValueExact);
        }

        if (MinValue.IsPresent)
        {
            writer.WriteBooleanField(8, IsMinValueExact);
        }

        if (HasNanCount)
        {
            writer.WriteI64Field(9, NanCount);
        }

        writer.EndStruct(saved);
    }
}

/// <summary>
/// A column chunk's <c>ColumnChunk</c> and <c>ColumnMetaData</c> flattened: where its pages and
/// indexes lie, how it is compressed, and where its statistics lie in the footer.
/// </summary>
internal struct ColumnChunkMetadata
{
    internal PhysicalType Type;

    /// <summary>The encodings the chunk declares, a bit per <see cref="ParquetEncoding"/> number.</summary>
    internal uint Encodings;

    /// <summary>Whether the chunk declares an encoding number above 31, which no bit holds.</summary>
    internal bool HasUnknownEncoding;

    internal ByteRange PathInSchema;
    internal int PathLength;
    internal CompressionCodec Codec;
    internal long ValueCount;
    internal long TotalUncompressedSize;
    internal long TotalCompressedSize;
    internal long DataPageOffset;

    /// <summary>The dictionary page's offset, or -1.</summary>
    internal long DictionaryPageOffset;

    internal ColumnStatistics Statistics;

    /// <summary>Whether <c>encoding_stats</c> shows every data page dictionary-encoded; false when absent.</summary>
    internal bool AllDataPagesDictionary;

    /// <summary>Whether the chunk carries <c>encoding_stats</c>.</summary>
    internal bool HasEncodingStats;

    /// <summary>The Bloom filter's offset, or -1.</summary>
    internal long BloomFilterOffset;

    /// <summary>The Bloom filter's length, header included, or -1.</summary>
    internal int BloomFilterLength;

    internal ByteRange SizeStatistics;

    /// <summary>The <c>GeospatialStatistics</c> of a GEOMETRY or GEOGRAPHY chunk, field 17, where it has them.</summary>
    internal ByteRange GeospatialStatistics;

    /// <summary>The offset index's offset, or -1.</summary>
    internal long OffsetIndexOffset;

    internal int OffsetIndexLength;

    /// <summary>The column index's offset, or -1.</summary>
    internal long ColumnIndexOffset;

    internal int ColumnIndexLength;

    /// <summary>Whether the chunk names a file of its own, which the standard does not ask a reader to follow.</summary>
    internal bool HasFilePath;

    /// <summary>Whether the chunk is encrypted.</summary>
    internal bool IsEncrypted;

    /// <summary>Where the chunk's first page lies: its dictionary page when it has one.</summary>
    internal readonly long Start => DictionaryPageOffset >= 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;

    /// <summary>Reads a <c>ColumnChunk</c> whose struct starts at <paramref name="origin"/> in the footer.</summary>
    internal static ColumnChunkMetadata Read(ReadOnlySpan<byte> footer, int origin)
    {
        ThriftCompactReader reader = new(footer.Slice(origin));
        ColumnChunkMetadata chunk = default;
        chunk.DictionaryPageOffset = -1;
        chunk.BloomFilterOffset = -1;
        chunk.BloomFilterLength = -1;
        chunk.OffsetIndexOffset = -1;
        chunk.ColumnIndexOffset = -1;
        chunk.SizeStatistics = ByteRange.None;
        chunk.GeospatialStatistics = ByteRange.None;
        chunk.PathInSchema = ByteRange.None;
        bool hasMetadata = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1 when type == ThriftType.Binary:
                    chunk.HasFilePath = reader.ReadBinary().Length > 0;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    ReadColumnMetaData(ref reader, ref chunk, origin);
                    hasMetadata = true;
                    break;
                case 4 when type == ThriftType.I64:
                    chunk.OffsetIndexOffset = reader.ReadI64();
                    break;
                case 5 when type == ThriftType.I32:
                    chunk.OffsetIndexLength = reader.ReadI32();
                    break;
                case 6 when type == ThriftType.I64:
                    chunk.ColumnIndexOffset = reader.ReadI64();
                    break;
                case 7 when type == ThriftType.I32:
                    chunk.ColumnIndexLength = reader.ReadI32();
                    break;
                case 8:
                case 9:
                    chunk.IsEncrypted = true;
                    reader.Skip(type);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (!hasMetadata && !chunk.IsEncrypted)
        {
            ParquetThrow.Format("A column chunk carries no metadata.");
        }

        return chunk;
    }

    private static void ReadColumnMetaData(ref ThriftCompactReader reader, ref ColumnChunkMetadata chunk, int origin)
    {
        int found = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    chunk.Type = (PhysicalType)reader.ReadI32();
                    found |= 1;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    ReadEncodings(ref reader, ref chunk);
                    found |= 2;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    int start = reader.Position;
                    int count = reader.ReadListHeader(out ThriftType element);
                    ThriftCompactReader.Expect(element, ThriftType.Binary);
                    for (int i = 0; i < count; i++)
                    {
                        reader.ReadBinary();
                    }

                    chunk.PathInSchema = new ByteRange(origin + start, reader.Position - start);
                    chunk.PathLength = count;
                    found |= 4;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    chunk.Codec = (CompressionCodec)reader.ReadI32();
                    found |= 8;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    chunk.ValueCount = reader.ReadI64();
                    found |= 16;
                    break;
                case 6:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    chunk.TotalUncompressedSize = reader.ReadI64();
                    found |= 32;
                    break;
                case 7:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    chunk.TotalCompressedSize = reader.ReadI64();
                    found |= 64;
                    break;
                case 9:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    chunk.DataPageOffset = reader.ReadI64();
                    found |= 128;
                    break;
                case 11 when type == ThriftType.I64:
                    chunk.DictionaryPageOffset = reader.ReadI64();
                    break;
                case 12 when type == ThriftType.Struct:
                    chunk.Statistics = ColumnStatistics.Read(ref reader, origin);
                    break;
                case 13 when type == ThriftType.List:
                    ReadEncodingStats(ref reader, ref chunk);
                    break;
                case 14 when type == ThriftType.I64:
                    chunk.BloomFilterOffset = reader.ReadI64();
                    break;
                case 15 when type == ThriftType.I32:
                    chunk.BloomFilterLength = reader.ReadI32();
                    break;
                case 16 when type == ThriftType.Struct:
                    int sizeStart = reader.Position;
                    reader.Skip(type);
                    chunk.SizeStatistics = new ByteRange(origin + sizeStart, reader.Position - sizeStart);
                    break;
                case 17 when type == ThriftType.Struct:
                    int geospatialStart = reader.Position;
                    reader.Skip(type);
                    chunk.GeospatialStatistics = new ByteRange(origin + geospatialStart, reader.Position - geospatialStart);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (found != 255)
        {
            ParquetThrow.Format("A column chunk's metadata lacks one of its required fields.");
        }

        if (chunk.ValueCount < 0 || chunk.TotalCompressedSize < 0 || chunk.TotalUncompressedSize < 0 || chunk.DataPageOffset < 0)
        {
            ParquetThrow.Format("A column chunk's metadata declares a negative count, size or offset.");
        }
    }

    private static void ReadEncodings(ref ThriftCompactReader reader, ref ColumnChunkMetadata chunk)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.I32);
        for (int i = 0; i < count; i++)
        {
            int encoding = reader.ReadI32();
            if ((uint)encoding < 32)
            {
                chunk.Encodings |= 1u << encoding;
            }
            else
            {
                chunk.HasUnknownEncoding = true;
            }
        }
    }

    private static void ReadEncodingStats(ref ThriftCompactReader reader, ref ColumnChunkMetadata chunk)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        bool allDictionary = true;
        for (int i = 0; i < count; i++)
        {
            int pageType = -1, encoding = -1, pages = 0;
            short saved = reader.EnterStruct();
            while (reader.ReadFieldHeader(out ThriftType type, out short id))
            {
                switch (id)
                {
                    case 1:
                        ThriftCompactReader.Expect(type, ThriftType.I32);
                        pageType = reader.ReadI32();
                        break;
                    case 2:
                        ThriftCompactReader.Expect(type, ThriftType.I32);
                        encoding = reader.ReadI32();
                        break;
                    case 3:
                        ThriftCompactReader.Expect(type, ThriftType.I32);
                        pages = reader.ReadI32();
                        break;
                    default:
                        reader.Skip(type);
                        break;
                }
            }

            reader.ExitStruct(saved);
            bool dataPage = pageType is (int)PageType.DataPage or (int)PageType.DataPageV2;
            if (dataPage && pages > 0 && encoding is not ((int)ParquetEncoding.RleDictionary or (int)ParquetEncoding.PlainDictionary))
            {
                allDictionary = false;
            }
        }

        chunk.HasEncodingStats = true;
        chunk.AllDataPagesDictionary = allDictionary;
    }
}
