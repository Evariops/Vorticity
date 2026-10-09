using System;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>
/// A page's header, <c>PageHeader</c> and the header of its kind flattened into one value: what a
/// page reader needs, decoded from the Thrift bytes that precede the page.
/// </summary>
/// <remarks>
/// Encodings are kept as the numbers the file wrote: a number this build does not know is refused by
/// the decode that needs it, not by the header. Page statistics are not decoded; their place in the
/// header is kept for a reader that wants them.
/// </remarks>
internal struct PageHeader
{
    internal PageType Type;
    internal int UncompressedPageSize;
    internal int CompressedPageSize;
    internal bool HasCrc;
    internal int Crc;

    /// <summary>The values of a data page, v1 or v2, nulls included; the entries of a dictionary page.</summary>
    internal int ValueCount;

    /// <summary>The encoding of a data page's values, or of a dictionary page's entries.</summary>
    internal ParquetEncoding Encoding;

    /// <summary>A v1 data page's definition levels' encoding.</summary>
    internal ParquetEncoding DefinitionLevelEncoding;

    /// <summary>A v1 data page's repetition levels' encoding.</summary>
    internal ParquetEncoding RepetitionLevelEncoding;

    /// <summary>A v2 data page's nulls.</summary>
    internal int NullCount;

    /// <summary>A v2 data page's rows.</summary>
    internal int RowCount;

    /// <summary>A v2 data page's definition levels, in bytes, before its values.</summary>
    internal int DefinitionLevelsLength;

    /// <summary>A v2 data page's repetition levels, in bytes, first in the page.</summary>
    internal int RepetitionLevelsLength;

    /// <summary>Whether a v2 data page's values are compressed; true unless the header says otherwise.</summary>
    internal bool IsCompressed;

    /// <summary>Whether a dictionary page's entries ascend.</summary>
    internal bool IsSorted;

    /// <summary>Where the page's statistics start in its header, or -1.</summary>
    internal int StatisticsStart;

    /// <summary>The length of the page's statistics in its header.</summary>
    internal int StatisticsLength;

    /// <summary>The header's own length, in bytes: where the page's data starts after it.</summary>
    internal int HeaderLength;

    /// <summary>Whether this is a data page, of either version.</summary>
    internal readonly bool IsDataPage => Type is PageType.DataPage or PageType.DataPageV2;

    /// <summary>Reads a page header from the start of <paramref name="bytes"/>.</summary>
    internal static PageHeader Read(ReadOnlySpan<byte> bytes)
    {
        ThriftCompactReader reader = new(bytes);
        PageHeader header = default;
        header.StatisticsStart = -1;
        header.IsCompressed = true;
        bool hasType = false, hasUncompressed = false, hasCompressed = false, hasKind = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.Type = (PageType)reader.ReadI32();
                    hasType = true;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.UncompressedPageSize = reader.ReadI32();
                    hasUncompressed = true;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.CompressedPageSize = reader.ReadI32();
                    hasCompressed = true;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.Crc = reader.ReadI32();
                    header.HasCrc = true;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    ReadDataPageHeader(ref reader, ref header);
                    hasKind |= header.Type == PageType.DataPage;
                    break;
                case 7:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    ReadDictionaryPageHeader(ref reader, ref header);
                    hasKind |= header.Type == PageType.DictionaryPage;
                    break;
                case 8:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    ReadDataPageHeaderV2(ref reader, ref header);
                    hasKind |= header.Type == PageType.DataPageV2;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (!hasType || !hasUncompressed || !hasCompressed)
        {
            ParquetThrow.Format("A page header lacks its type or one of its sizes.");
        }

        if (header.UncompressedPageSize < 0 || header.CompressedPageSize < 0)
        {
            ParquetThrow.Format("A page header declares a negative size.");
        }

        if (!hasKind && header.Type is PageType.DataPage or PageType.DataPageV2 or PageType.DictionaryPage)
        {
            ParquetThrow.Format($"A {header.Type} page lacks the header of its kind.");
        }

        header.HeaderLength = reader.Position;
        return header;
    }

    private static void ReadDataPageHeader(ref ThriftCompactReader reader, ref PageHeader header)
    {
        bool hasCount = false, hasEncoding = false, hasDefinition = false, hasRepetition = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.ValueCount = reader.ReadI32();
                    hasCount = true;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.Encoding = (ParquetEncoding)reader.ReadI32();
                    hasEncoding = true;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.DefinitionLevelEncoding = (ParquetEncoding)reader.ReadI32();
                    hasDefinition = true;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.RepetitionLevelEncoding = (ParquetEncoding)reader.ReadI32();
                    hasRepetition = true;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    header.StatisticsStart = reader.Position;
                    reader.Skip(type);
                    header.StatisticsLength = reader.Position - header.StatisticsStart;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (!hasCount || !hasEncoding || !hasDefinition || !hasRepetition)
        {
            ParquetThrow.Format("A data page header lacks one of its required fields.");
        }

        if (header.ValueCount < 0)
        {
            ParquetThrow.Format("A data page declares a negative value count.");
        }
    }

    private static void ReadDictionaryPageHeader(ref ThriftCompactReader reader, ref PageHeader header)
    {
        bool hasCount = false, hasEncoding = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.ValueCount = reader.ReadI32();
                    hasCount = true;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.Encoding = (ParquetEncoding)reader.ReadI32();
                    hasEncoding = true;
                    break;
                case 3:
                    header.IsSorted = ThriftCompactReader.BooleanField(type);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (!hasCount || !hasEncoding)
        {
            ParquetThrow.Format("A dictionary page header lacks one of its required fields.");
        }

        if (header.ValueCount < 0)
        {
            ParquetThrow.Format("A dictionary page declares a negative entry count.");
        }
    }

    private static void ReadDataPageHeaderV2(ref ThriftCompactReader reader, ref PageHeader header)
    {
        int found = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.ValueCount = reader.ReadI32();
                    found |= 1;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.NullCount = reader.ReadI32();
                    found |= 2;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.RowCount = reader.ReadI32();
                    found |= 4;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.Encoding = (ParquetEncoding)reader.ReadI32();
                    found |= 8;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.DefinitionLevelsLength = reader.ReadI32();
                    found |= 16;
                    break;
                case 6:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    header.RepetitionLevelsLength = reader.ReadI32();
                    found |= 32;
                    break;
                case 7:
                    header.IsCompressed = ThriftCompactReader.BooleanField(type);
                    break;
                case 8:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    header.StatisticsStart = reader.Position;
                    reader.Skip(type);
                    header.StatisticsLength = reader.Position - header.StatisticsStart;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (found != 63)
        {
            ParquetThrow.Format("A v2 data page header lacks one of its required fields.");
        }

        if (header.ValueCount < 0 || header.NullCount < 0 || header.RowCount < 0
            || header.NullCount > header.ValueCount
            || header.DefinitionLevelsLength < 0 || header.RepetitionLevelsLength < 0)
        {
            ParquetThrow.Format("A v2 data page header declares a negative or inconsistent count.");
        }
    }

    /// <summary>
    /// Writes this header: a v2 data page or a dictionary page, the only kinds this library writes,
    /// with <paramref name="extension"/>, when not empty, as the binary field 32767 the standard
    /// reserves in every structure.
    /// </summary>
    internal readonly void Write(ref ThriftCompactWriter writer, ReadOnlySpan<byte> extension)
    {
        short saved = writer.BeginStruct();
        writer.WriteI32Field(1, (int)Type);
        writer.WriteI32Field(2, UncompressedPageSize);
        writer.WriteI32Field(3, CompressedPageSize);
        if (HasCrc)
        {
            writer.WriteI32Field(4, Crc);
        }

        switch (Type)
        {
            case PageType.DataPageV2:
                short v2 = writer.BeginStructField(8);
                writer.WriteI32Field(1, ValueCount);
                writer.WriteI32Field(2, NullCount);
                writer.WriteI32Field(3, RowCount);
                writer.WriteI32Field(4, (int)Encoding);
                writer.WriteI32Field(5, DefinitionLevelsLength);
                writer.WriteI32Field(6, RepetitionLevelsLength);
                if (!IsCompressed)
                {
                    writer.WriteBooleanField(7, false);
                }

                writer.EndStruct(v2);
                break;
            case PageType.DictionaryPage:
                short dictionary = writer.BeginStructField(7);
                writer.WriteI32Field(1, ValueCount);
                writer.WriteI32Field(2, (int)Encoding);
                if (IsSorted)
                {
                    writer.WriteBooleanField(3, true);
                }

                writer.EndStruct(dictionary);
                break;
            case PageType.DataPage:
                short v1 = writer.BeginStructField(5);
                writer.WriteI32Field(1, ValueCount);
                writer.WriteI32Field(2, (int)Encoding);
                writer.WriteI32Field(3, (int)DefinitionLevelEncoding);
                writer.WriteI32Field(4, (int)RepetitionLevelEncoding);
                writer.EndStruct(v1);
                break;
            default:
                throw new InvalidOperationException($"This library writes no {Type} page.");
        }

        if (!extension.IsEmpty)
        {
            writer.WriteBinaryField(ExtensionFieldId, extension);
        }

        writer.EndStruct(saved);
    }

    /// <summary>The field id the standard reserves in every structure for an extension's bytes.</summary>
    internal const short ExtensionFieldId = 32767;
}
