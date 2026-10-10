using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A Parquet file put together by hand: schema elements, and per column pages of levels and PLAIN
/// values, uncompressed, in one row group. What this package's writer never lays out — v1 pages, a
/// row cut across two pages, the deprecated BIT_PACKED levels — and what no writer should.
/// </summary>
internal sealed class HandBuiltFile
{
    private readonly List<Element> _schema;
    private readonly List<Column> _columns = [];

    /// <summary>A file whose root holds <paramref name="fields"/> fields: the elements added next, depth first.</summary>
    internal HandBuiltFile(int fields)
    {
        _schema = [new Element("schema", null, null, fields, null)];
    }

    /// <summary>The rows the footer and its row group declare.</summary>
    internal long Rows { get; set; }

    /// <summary>Adds a group of <paramref name="children"/> elements, which the next elements are.</summary>
    internal HandBuiltFile Group(string name, FieldRepetition repetition, int children, ConvertedType? converted = null)
    {
        _schema.Add(new Element(name, null, repetition, children, converted));
        return this;
    }

    /// <summary>Adds a leaf.</summary>
    internal HandBuiltFile Leaf(string name, FieldRepetition repetition, PhysicalType type, ConvertedType? converted = null)
    {
        _schema.Add(new Element(name, type, repetition, -1, converted));
        return this;
    }

    /// <summary>The chunk of the next leaf, in the schema's order: its pages are added to it.</summary>
    internal Column Chunk(PhysicalType type, params string[] path)
    {
        Column column = new(type, path);
        _columns.Add(column);
        return column;
    }

    /// <summary>The file's bytes.</summary>
    internal byte[] ToBytes()
    {
        ArrayBufferWriter<byte> output = new();
        output.Write("PAR1"u8);
        long[] offsets = new long[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            offsets[i] = output.WrittenCount;
            foreach (byte[] page in _columns[i].Pages)
            {
                output.Write(page);
            }
        }

        int footerStart = output.WrittenCount;
        ThriftCompactWriter writer = new(output);
        short file = writer.BeginStruct();
        writer.WriteI32Field(1, 1);
        writer.WriteListField(2, ThriftType.Struct, _schema.Count);
        foreach (Element element in _schema)
        {
            short saved = writer.BeginStruct();
            if (element.Type is { } type)
            {
                writer.WriteI32Field(1, (int)type);
            }

            if (element.Repetition is { } repetition)
            {
                writer.WriteI32Field(3, (int)repetition);
            }

            writer.WriteStringField(4, element.Name);
            if (element.Children >= 0)
            {
                writer.WriteI32Field(5, element.Children);
            }

            if (element.Converted is { } converted)
            {
                writer.WriteI32Field(6, (int)converted);
            }

            writer.EndStruct(saved);
        }

        writer.WriteI64Field(3, Rows);
        writer.WriteListField(4, ThriftType.Struct, 1);
        short group = writer.BeginStruct();
        writer.WriteListField(1, ThriftType.Struct, _columns.Count);
        long total = 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            Column column = _columns[i];
            short chunk = writer.BeginStruct();
            writer.WriteI64Field(2, offsets[i]);
            short meta = writer.BeginStructField(3);
            writer.WriteI32Field(1, (int)column.Type);
            writer.WriteListField(2, ThriftType.I32, 2);
            writer.WriteI32Element((int)ParquetEncoding.Plain);
            writer.WriteI32Element((int)ParquetEncoding.Rle);
            writer.WriteListField(3, ThriftType.Binary, column.Path.Length);
            foreach (string name in column.Path)
            {
                writer.WriteStringElement(name);
            }

            writer.WriteI32Field(4, 0);
            writer.WriteI64Field(5, column.Values);
            writer.WriteI64Field(6, column.Bytes);
            writer.WriteI64Field(7, column.Bytes);
            writer.WriteI64Field(9, offsets[i]);
            if (column.BloomFilterOffset >= 0)
            {
                writer.WriteI64Field(14, column.BloomFilterOffset);
                if (column.BloomFilterLength > 0)
                {
                    writer.WriteI32Field(15, column.BloomFilterLength);
                }
            }

            writer.EndStruct(meta);
            writer.EndStruct(chunk);
            total += column.Bytes;
        }

        writer.WriteI64Field(2, total);
        writer.WriteI64Field(3, Rows);
        writer.EndStruct(group);
        writer.WriteStringField(6, "hand");
        writer.EndStruct(file);
        writer.Flush();
        Span<byte> tail = output.GetSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(tail, output.WrittenCount - footerStart);
        "PAR1"u8.CopyTo(tail[4..]);
        output.Advance(8);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>INT64 values, PLAIN.</summary>
    internal static byte[] Longs(params long[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(long)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(long)), values[i]);
        }

        return bytes;
    }

    /// <summary>BYTE_ARRAY values, PLAIN: each behind its length.</summary>
    internal static byte[] Strings(params string[] values)
    {
        List<byte> bytes = [];
        byte[] length = new byte[4];
        foreach (string value in values)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(length, utf8.Length);
            bytes.AddRange(length);
            bytes.AddRange(utf8);
        }

        return bytes.ToArray();
    }

    private sealed record Element(string Name, PhysicalType? Type, FieldRepetition? Repetition, int Children, ConvertedType? Converted);

    /// <summary>A column's chunk, its pages in order.</summary>
    internal sealed class Column(PhysicalType type, string[] path)
    {
        internal PhysicalType Type { get; } = type;

        internal string[] Path { get; } = path;

        internal List<byte[]> Pages { get; } = [];

        internal long Values { get; private set; }

        internal long Bytes { get; private set; }

        /// <summary>Where the footer says the chunk's Bloom filter is, or -1 for none.</summary>
        internal long BloomFilterOffset { get; set; } = -1;

        /// <summary>The Bloom filter's length the footer gives, or 0 for none.</summary>
        internal int BloomFilterLength { get; set; }

        /// <summary>
        /// Adds a v1 page: its levels inside its bytes, RLE behind their lengths or, when
        /// <paramref name="bitPacked"/>, the deprecated MSB-first packing; levels of a maximum of zero
        /// are left out, as the standard asks.
        /// </summary>
        internal Column V1(byte[]? repetition, int maxRepetition, byte[] definition, int maxDefinition, byte[] values, bool bitPacked = false)
        {
            List<byte> body = [];
            if (maxRepetition > 0)
            {
                body.AddRange(LevelsV1(repetition!, maxRepetition, bitPacked));
            }

            if (maxDefinition > 0)
            {
                body.AddRange(LevelsV1(definition, maxDefinition, bitPacked));
            }

            body.AddRange(values);
            ArrayBufferWriter<byte> page = new();
            ThriftCompactWriter writer = new(page);
            short header = writer.BeginStruct();
            writer.WriteI32Field(1, (int)PageType.DataPage);
            writer.WriteI32Field(2, body.Count);
            writer.WriteI32Field(3, body.Count);
            short data = writer.BeginStructField(5);
            writer.WriteI32Field(1, definition.Length);
            writer.WriteI32Field(2, (int)ParquetEncoding.Plain);
            writer.WriteI32Field(3, (int)(bitPacked ? ParquetEncoding.BitPacked : ParquetEncoding.Rle));
            writer.WriteI32Field(4, (int)(bitPacked ? ParquetEncoding.BitPacked : ParquetEncoding.Rle));
            writer.EndStruct(data);
            writer.EndStruct(header);
            writer.Flush();
            page.Write(body.ToArray());
            return Add(page.WrittenSpan.ToArray(), definition.Length);
        }

        /// <summary>Adds a v2 page: its levels ahead of its values, RLE without lengths, which its header holds.</summary>
        internal Column V2(byte[]? repetition, int maxRepetition, byte[] definition, int maxDefinition, int rows, byte[] values)
        {
            byte[] repetitionBytes = maxRepetition > 0 ? Rle(repetition!, maxRepetition) : [];
            byte[] definitionBytes = maxDefinition > 0 ? Rle(definition, maxDefinition) : [];
            int nulls = 0;
            foreach (byte level in definition)
            {
                nulls += level < maxDefinition ? 1 : 0;
            }

            int size = repetitionBytes.Length + definitionBytes.Length + values.Length;
            ArrayBufferWriter<byte> page = new();
            ThriftCompactWriter writer = new(page);
            short header = writer.BeginStruct();
            writer.WriteI32Field(1, (int)PageType.DataPageV2);
            writer.WriteI32Field(2, size);
            writer.WriteI32Field(3, size);
            short data = writer.BeginStructField(8);
            writer.WriteI32Field(1, definition.Length);
            writer.WriteI32Field(2, nulls);
            writer.WriteI32Field(3, rows);
            writer.WriteI32Field(4, (int)ParquetEncoding.Plain);
            writer.WriteI32Field(5, definitionBytes.Length);
            writer.WriteI32Field(6, repetitionBytes.Length);
            writer.WriteBooleanField(7, false);
            writer.EndStruct(data);
            writer.EndStruct(header);
            writer.Flush();
            page.Write(repetitionBytes);
            page.Write(definitionBytes);
            page.Write(values);
            return Add(page.WrittenSpan.ToArray(), definition.Length);
        }

        private Column Add(byte[] page, int values)
        {
            Pages.Add(page);
            Values += values;
            Bytes += page.Length;
            return this;
        }

        private static byte[] LevelsV1(byte[] levels, int max, bool bitPacked)
        {
            if (!bitPacked)
            {
                byte[] runs = Rle(levels, max);
                byte[] withLength = new byte[4 + runs.Length];
                BinaryPrimitives.WriteInt32LittleEndian(withLength, runs.Length);
                runs.CopyTo(withLength, 4);
                return withLength;
            }

            // Each level from its most significant bit, back to back from each byte's top.
            int width = Width(max);
            byte[] packed = new byte[(levels.Length * width + 7) / 8];
            int bit = 0;
            foreach (byte level in levels)
            {
                for (int b = width - 1; b >= 0; b--, bit++)
                {
                    if (((level >> b) & 1) != 0)
                    {
                        packed[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    }
                }
            }

            return packed;
        }

        private static byte[] Rle(byte[] levels, int max)
        {
            int width = Width(max);
            byte[] runs = new byte[RleHybridEncoder.Size(levels, width)];
            RleHybridEncoder.Encode(levels, width, runs);
            return runs;
        }

        private static int Width(int max) => 32 - BitOperations.LeadingZeroCount((uint)max);
    }
}
