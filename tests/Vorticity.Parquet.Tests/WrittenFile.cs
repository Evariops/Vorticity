using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Zstd;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>A file this package wrote, read back through its own metadata readers.</summary>
internal sealed class WrittenFile
{
    private readonly ZstdDecompressor _zstd = new();

    internal WrittenFile(byte[] bytes)
    {
        Bytes = bytes;
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("PAR1"u8));
        Assert.True(bytes.AsSpan(bytes.Length - 4).SequenceEqual("PAR1"u8));
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        Footer = ParquetFooter.Read(bytes.AsMemory(bytes.Length - 8 - length, length));
        Schema = ParquetSchema.Compile(Footer.Schema, Footer.ColumnOrders);
    }

    internal byte[] Bytes { get; }

    internal ParquetFooter Footer { get; }

    internal ParquetSchema Schema { get; }

    internal string CreatedBy => Encoding.UTF8.GetString(Range(Footer.CreatedBy, Footer.Bytes));

    internal ReadOnlySpan<byte> Range(ByteRange range) => Range(range, Footer.Bytes);

    internal PageLocation[] Pages(int rowGroup, int column)
    {
        ColumnChunkMetadata chunk = Footer.Chunk(rowGroup, column);
        Assert.True(chunk.OffsetIndexOffset > 0);
        return OffsetIndex.Read(Bytes.AsSpan(checked((int)chunk.OffsetIndexOffset), chunk.OffsetIndexLength), Footer.RowGroups[rowGroup].RowCount);
    }

    internal PageHeader Header(PageLocation page) => PageHeader.Read(Bytes.AsSpan(checked((int)page.Offset), page.Size));

    /// <summary>Per row of a v2 page, whether it holds a value: its definition levels, or every row when it has none.</summary>
    internal bool[] Validity(PageLocation page, PageHeader header, int rows)
    {
        bool[] valid = new bool[rows];
        if (header.DefinitionLevelsLength == 0)
        {
            Array.Fill(valid, true);
            return valid;
        }

        ReadOnlySpan<byte> levels = Bytes.AsSpan(checked((int)page.Offset) + header.HeaderLength + header.RepetitionLevelsLength, header.DefinitionLevelsLength);
        byte[] decoded = new byte[rows];
        new RleHybridDecoder(1).Read(levels, decoded);
        for (int r = 0; r < rows; r++)
        {
            valid[r] = decoded[r] == 1;
        }

        return valid;
    }

    /// <summary>A v2 page's values, decompressed when the page says they are compressed.</summary>
    internal byte[] Values(PageLocation page, PageHeader header)
    {
        int levels = header.RepetitionLevelsLength + header.DefinitionLevelsLength;
        ReadOnlySpan<byte> stored = Bytes.AsSpan(checked((int)page.Offset) + header.HeaderLength + levels, header.CompressedPageSize - levels);
        byte[] values = new byte[header.UncompressedPageSize - levels];
        CompressionCodec codec = header.IsCompressed ? CodecOf(page) : CompressionCodec.Uncompressed;
        PageCodecs.Decompress(codec, stored, values, _zstd);
        return values;
    }

    private CompressionCodec CodecOf(PageLocation page)
    {
        for (int group = 0; group < Footer.RowGroups.Length; group++)
        {
            for (int column = 0; column < Schema.Columns.Length; column++)
            {
                ColumnChunkMetadata chunk = Footer.Chunk(group, column);
                if (page.Offset >= chunk.DataPageOffset && page.Offset < chunk.DataPageOffset + chunk.TotalCompressedSize)
                {
                    return chunk.Codec;
                }
            }
        }

        throw new InvalidOperationException("The page is in no column chunk.");
    }

    private static ReadOnlySpan<byte> Range(ByteRange range, ReadOnlySpan<byte> bytes) => bytes.Slice(range.Start, range.Length);
}
