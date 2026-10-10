using System;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>Where a data page lies in the file, its bytes with its header, and its first row in the row group.</summary>
internal readonly record struct PageLocation(long Offset, int Size, long FirstRow);

/// <summary>
/// A column chunk's <c>OffsetIndex</c>: where each of its data pages lies and the row it starts at,
/// so that a reader goes to a page without reading the ones before it.
/// </summary>
internal static class OffsetIndex
{
    /// <summary>
    /// Reads the page locations of an offset index, checked as a reader relies on them: the pages in
    /// file order without overlap, their first rows rising from 0 within the row group's
    /// <paramref name="rows"/>.
    /// </summary>
    internal static PageLocation[] Read(ReadOnlySpan<byte> bytes, long rows)
    {
        ThriftCompactReader reader = new(bytes);
        PageLocation[]? pages = null;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id != 1)
            {
                reader.Skip(type);
                continue;
            }

            ThriftCompactReader.Expect(type, ThriftType.List);
            int count = reader.ReadListHeader(out ThriftType element);
            ThriftCompactReader.Expect(element, ThriftType.Struct);
            pages = new PageLocation[count];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = ReadLocation(ref reader);
            }
        }

        reader.ExitStruct(saved);
        if (pages is null)
        {
            return ParquetThrow.Format<PageLocation[]>("An offset index lacks its page locations.");
        }

        for (int i = 0; i < pages.Length; i++)
        {
            PageLocation page = pages[i];
            bool placed = i == 0
                ? page.FirstRow == 0
                : page.FirstRow > pages[i - 1].FirstRow && page.Offset >= pages[i - 1].Offset + pages[i - 1].Size;
            if (!placed || page.FirstRow >= rows)
            {
                ParquetThrow.Format($"Page {i} of an offset index is out of order, overlaps the page before it, or starts past the row group's {rows} rows.");
            }
        }

        return pages;
    }

    /// <summary>Writes an offset index of <paramref name="pages"/>.</summary>
    internal static void Write(ref ThriftCompactWriter writer, ReadOnlySpan<PageLocation> pages, Writing.ChunkSizes? sizes = null)
    {
        short saved = writer.BeginStruct();
        writer.WriteListField(1, ThriftType.Struct, pages.Length);
        foreach (PageLocation page in pages)
        {
            short location = writer.BeginStruct();
            writer.WriteI64Field(1, page.Offset);
            writer.WriteI32Field(2, page.Size);
            writer.WriteI64Field(3, page.FirstRow);
            writer.EndStruct(location);
        }

        sizes?.WriteOffsetIndex(ref writer);
        writer.EndStruct(saved);
    }

    private static PageLocation ReadLocation(ref ThriftCompactReader reader)
    {
        long offset = -1;
        int size = -1;
        long firstRow = -1;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    offset = reader.ReadI64();
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    size = reader.ReadI32();
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    firstRow = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (offset < 0 || size <= 0 || firstRow < 0)
        {
            ParquetThrow.Format("A page location lacks its offset, its size or its first row, or holds a negative one.");
        }

        return new PageLocation(offset, size, firstRow);
    }
}
