using System;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>
/// A column chunk's <c>ColumnIndex</c>: each data page's bounds, PLAIN as statistics hold them,
/// whether it holds no value, and its null count; and how the bounds run across the pages. With
/// the offset index, what lets a reader skip the pages a filter rules out.
/// </summary>
internal sealed class ColumnIndex
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly ByteRange[] _mins;
    private readonly ByteRange[] _maxes;

    private ColumnIndex(ReadOnlyMemory<byte> bytes, bool[] nullPages, ByteRange[] mins, ByteRange[] maxes, BoundaryOrder order, long[]? nullCounts)
    {
        _bytes = bytes;
        NullPages = nullPages;
        _mins = mins;
        _maxes = maxes;
        Order = order;
        NullCounts = nullCounts;
    }

    /// <summary>The pages.</summary>
    internal int Count => NullPages.Length;

    /// <summary>Per page, whether it holds no value: its bounds are then empty and mean nothing.</summary>
    internal bool[] NullPages { get; }

    internal BoundaryOrder Order { get; }

    /// <summary>Per page, its null count; null when the index carries none.</summary>
    internal long[]? NullCounts { get; }

    /// <summary>Page <paramref name="page"/>'s lower bound.</summary>
    internal ReadOnlySpan<byte> Min(int page) => _mins[page].Of(_bytes.Span);

    /// <summary>Page <paramref name="page"/>'s upper bound.</summary>
    internal ReadOnlySpan<byte> Max(int page) => _maxes[page].Of(_bytes.Span);

    /// <summary>Reads a column index of <paramref name="pages"/> pages, each list of it checked to hold one entry a page.</summary>
    internal static ColumnIndex Read(ReadOnlyMemory<byte> bytes, int pages)
    {
        ThriftCompactReader reader = new(bytes.Span);
        bool[]? nullPages = null;
        ByteRange[]? mins = null;
        ByteRange[]? maxes = null;
        long[]? nullCounts = null;
        BoundaryOrder order = BoundaryOrder.Unordered;
        bool hasOrder = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    ThriftCompactReader.ExpectBooleanElements(ReadCount(ref reader, pages));
                    nullPages = new bool[pages];
                    for (int i = 0; i < pages; i++)
                    {
                        nullPages[i] = reader.ReadBooleanElement();
                    }

                    break;
                case 2:
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    ThriftType element = ReadCount(ref reader, pages);
                    ThriftCompactReader.Expect(element, ThriftType.Binary);
                    ByteRange[] ranges = new ByteRange[pages];
                    for (int i = 0; i < pages; i++)
                    {
                        int length = reader.ReadBinary().Length;
                        ranges[i] = new ByteRange(reader.Position - length, length);
                    }

                    if (id == 2)
                    {
                        mins = ranges;
                    }
                    else
                    {
                        maxes = ranges;
                    }

                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    int value = reader.ReadI32();
                    order = value is >= 0 and <= 2 ? (BoundaryOrder)value : BoundaryOrder.Unordered;
                    hasOrder = true;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    ThriftCompactReader.Expect(ReadCount(ref reader, pages), ThriftType.I64);
                    nullCounts = new long[pages];
                    for (int i = 0; i < pages; i++)
                    {
                        nullCounts[i] = reader.ReadI64();
                    }

                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (nullPages is null || mins is null || maxes is null || !hasOrder)
        {
            return ParquetThrow.Format<ColumnIndex>("A column index lacks a field the standard requires of it.");
        }

        return new ColumnIndex(bytes, nullPages, mins, maxes, order, nullCounts);
    }

    /// <summary>Writes a column index of <paramref name="pages"/>, their bounds run in <paramref name="order"/>.</summary>
    internal static void Write(ref ThriftCompactWriter writer, ReadOnlySpan<Writing.PageStatistics> pages, BoundaryOrder order)
    {
        short saved = writer.BeginStruct();
        writer.WriteListField(1, ThriftType.BooleanTrue, pages.Length);
        foreach (Writing.PageStatistics page in pages)
        {
            writer.WriteBooleanElement(page.NullPage);
        }

        writer.WriteListField(2, ThriftType.Binary, pages.Length);
        foreach (Writing.PageStatistics page in pages)
        {
            writer.WriteBinaryElement(page.Min);
        }

        writer.WriteListField(3, ThriftType.Binary, pages.Length);
        foreach (Writing.PageStatistics page in pages)
        {
            writer.WriteBinaryElement(page.Max);
        }

        writer.WriteI32Field(4, (int)order);
        writer.WriteListField(5, ThriftType.I64, pages.Length);
        foreach (Writing.PageStatistics page in pages)
        {
            writer.WriteI64Element(page.Nulls);
        }

        writer.EndStruct(saved);
    }

    /// <summary>A list's header, its count required to be the pages'; its element type.</summary>
    private static ThriftType ReadCount(ref ThriftCompactReader reader, int pages)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        if (count != pages)
        {
            ParquetThrow.Format($"A column index lists {count} pages where its offset index has {pages}.");
        }

        return element;
    }
}
