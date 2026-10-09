using System;
using System.Collections.Generic;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Writing;

/// <summary>A column chunk written to the file, and where its offset index went.</summary>
internal sealed class WrittenChunk
{
    internal required WriteColumn Column { get; init; }

    internal required ChunkResult Chunk { get; init; }

    internal CompressionCodec Codec { get; init; }

    internal long OffsetIndexOffset { get; set; } = -1;

    internal int OffsetIndexLength { get; set; }
}

/// <summary>A row group written to the file.</summary>
internal sealed class WrittenRowGroup
{
    internal required WrittenChunk[] Chunks { get; init; }

    internal long Rows { get; init; }

    internal int Ordinal { get; init; }
}

/// <summary>
/// Serializes the footer: the <c>FileMetaData</c> of the schema, the row groups, the key-value
/// metadata, the writer and the column orders.
/// </summary>
internal static class FooterWriter
{
    /// <summary>Writes the <c>FileMetaData</c>.</summary>
    internal static void WriteFileMetaData(
        ref ThriftCompactWriter writer,
        SchemaElement[] schema,
        long rows,
        IReadOnlyList<WrittenRowGroup> rowGroups,
        IReadOnlyList<KeyValuePair<string, string>> keyValues,
        string createdBy,
        WriteColumn[] columns)
    {
        short saved = writer.BeginStruct();
        writer.WriteI32Field(1, 1);
        writer.WriteListField(2, ThriftType.Struct, schema.Length);
        foreach (SchemaElement element in schema)
        {
            element.Write(ref writer);
        }

        writer.WriteI64Field(3, rows);
        writer.WriteListField(4, ThriftType.Struct, rowGroups.Count);
        foreach (WrittenRowGroup rowGroup in rowGroups)
        {
            WriteRowGroup(ref writer, rowGroup);
        }

        if (keyValues.Count > 0)
        {
            writer.WriteListField(5, ThriftType.Struct, keyValues.Count);
            foreach (KeyValuePair<string, string> pair in keyValues)
            {
                short entry = writer.BeginStruct();
                writer.WriteStringField(1, pair.Key);
                writer.WriteStringField(2, pair.Value);
                writer.EndStruct(entry);
            }
        }

        writer.WriteStringField(6, createdBy);

        // The order each leaf's bounds are given in. Bounds are written only for the integer
        // domains, whose order is the type's.
        writer.WriteListField(7, ThriftType.Struct, columns.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            short order = writer.BeginStruct();
            short member = writer.BeginStructField((short)ColumnOrderKind.TypeDefined);
            writer.EndStruct(member);
            writer.EndStruct(order);
        }

        writer.EndStruct(saved);
    }

    private static void WriteRowGroup(ref ThriftCompactWriter writer, WrittenRowGroup rowGroup)
    {
        long uncompressed = 0;
        long compressed = 0;
        foreach (WrittenChunk chunk in rowGroup.Chunks)
        {
            uncompressed += chunk.Chunk.UncompressedSize;
            compressed += chunk.Chunk.CompressedSize;
        }

        short saved = writer.BeginStruct();
        writer.WriteListField(1, ThriftType.Struct, rowGroup.Chunks.Length);
        foreach (WrittenChunk chunk in rowGroup.Chunks)
        {
            WriteColumnChunk(ref writer, chunk);
        }

        writer.WriteI64Field(2, uncompressed);
        writer.WriteI64Field(3, rowGroup.Rows);
        if (rowGroup.Chunks.Length > 0)
        {
            writer.WriteI64Field(5, rowGroup.Chunks[0].Chunk.Offset);
        }

        writer.WriteI64Field(6, compressed);

        // An i16: a file of more row groups gives the rest none, which the field allows.
        if (rowGroup.Ordinal <= short.MaxValue)
        {
            writer.WriteI16Field(7, (short)rowGroup.Ordinal);
        }
        writer.EndStruct(saved);
    }

    private static void WriteColumnChunk(ref ThriftCompactWriter writer, WrittenChunk written)
    {
        ChunkResult chunk = written.Chunk;
        WriteColumn column = written.Column;
        short saved = writer.BeginStruct();

        // Required by the structure and deprecated by the standard: 0, no metadata outside the footer.
        writer.WriteI64Field(2, 0);
        short metadata = writer.BeginStructField(3);
        writer.WriteI32Field(1, (int)column.Physical);
        int encodings = System.Numerics.BitOperations.PopCount(chunk.Encodings);
        writer.WriteListField(2, ThriftType.I32, encodings);
        for (int encoding = 0; encoding < 32; encoding++)
        {
            if ((chunk.Encodings & (1u << encoding)) != 0)
            {
                writer.WriteI32Element(encoding);
            }
        }

        writer.WriteListField(3, ThriftType.Binary, column.Path.Length);
        foreach (string name in column.Path)
        {
            writer.WriteStringElement(name);
        }

        writer.WriteI32Field(4, (int)written.Codec);
        writer.WriteI64Field(5, chunk.Rows);
        writer.WriteI64Field(6, chunk.UncompressedSize);
        writer.WriteI64Field(7, chunk.CompressedSize);
        writer.WriteI64Field(9, chunk.DataPageOffset);
        if (chunk.DictionaryPageOffset >= 0)
        {
            writer.WriteI64Field(11, chunk.DictionaryPageOffset);
        }

        WriteStatistics(ref writer, chunk);

        // How many pages of each kind and encoding: what tells a reader every data page is codes.
        bool dictionary = chunk.DictionaryPageOffset >= 0;
        int kinds = (dictionary ? 1 : 0) + (chunk.DictionaryPages > 0 ? 1 : 0) + (chunk.PlainPages > 0 ? 1 : 0);
        writer.WriteListField(13, ThriftType.Struct, kinds);
        if (dictionary)
        {
            WriteEncodingStats(ref writer, PageType.DictionaryPage, ParquetEncoding.Plain, 1);
        }

        if (chunk.DictionaryPages > 0)
        {
            WriteEncodingStats(ref writer, PageType.DataPageV2, ParquetEncoding.RleDictionary, chunk.DictionaryPages);
        }

        if (chunk.PlainPages > 0)
        {
            WriteEncodingStats(ref writer, PageType.DataPageV2, ParquetEncoding.Plain, chunk.PlainPages);
        }

        writer.EndStruct(metadata);
        if (written.OffsetIndexOffset >= 0)
        {
            writer.WriteI64Field(4, written.OffsetIndexOffset);
            writer.WriteI32Field(5, written.OffsetIndexLength);
        }

        writer.EndStruct(saved);
    }

    private static void WriteEncodingStats(ref ThriftCompactWriter writer, PageType type, ParquetEncoding encoding, int count)
    {
        short saved = writer.BeginStruct();
        writer.WriteI32Field(1, (int)type);
        writer.WriteI32Field(2, (int)encoding);
        writer.WriteI32Field(3, count);
        writer.EndStruct(saved);
    }

    private static void WriteStatistics(ref ThriftCompactWriter writer, ChunkResult chunk)
    {
        short saved = writer.BeginStructField(12);
        writer.WriteI64Field(3, chunk.Nulls);
        if (chunk.Bounds.Present)
        {
            Span<byte> max = stackalloc byte[8];
            Span<byte> min = stackalloc byte[8];
            writer.WriteBinaryField(5, chunk.Bounds.Plain(chunk.Bounds.Max, max));
            writer.WriteBinaryField(6, chunk.Bounds.Plain(chunk.Bounds.Min, min));
            writer.WriteBooleanField(7, true);
            writer.WriteBooleanField(8, true);
        }

        writer.EndStruct(saved);
    }
}
