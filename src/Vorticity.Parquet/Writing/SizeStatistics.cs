using System;
using System.Collections.Generic;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// Gathers a column chunk's size statistics, as the standard's <c>SizeStatistics</c> holds them, page
/// by page: the bytes of a byte array column's values, their lengths left out, and the histograms of
/// the levels that say more than the null count, the repetition levels of a maximum above 0 and the
/// definition levels of one above 1, which the standard lets a writer leave out otherwise.
/// </summary>
internal sealed class SizeCollector
{
    private readonly List<long>? _bytes;
    private readonly List<long>? _repetition;
    private readonly List<long>? _definition;
    private readonly int _repetitionLevels;
    private readonly int _definitionLevels;

    internal SizeCollector(WriteColumn column)
    {
        if (column.Physical == Metadata.PhysicalType.ByteArray)
        {
            _bytes = [];
        }

        if (column.MaxRepetitionLevel > 0)
        {
            _repetition = [];
            _repetitionLevels = column.MaxRepetitionLevel + 1;
        }

        if (column.MaxDefinitionLevel > 1)
        {
            _definition = [];
            _definitionLevels = column.MaxDefinitionLevel + 1;
        }
    }

    /// <summary>
    /// Adds a page: its <paramref name="values"/> PLAIN values, a byte array's behind its length, and
    /// its levels, a byte an entry.
    /// </summary>
    internal void AddPage(ReadOnlySpan<byte> plain, int values, ReadOnlySpan<byte> repetition, ReadOnlySpan<byte> definition)
    {
        _bytes?.Add(plain.Length - ((long)sizeof(int) * values));
        if (_repetition is not null)
        {
            Histogram(repetition, _repetitionLevels, _repetition);
        }

        if (_definition is not null)
        {
            Histogram(definition, _definitionLevels, _definition);
        }
    }

    /// <summary>The chunk's statistics, or null for a column that has none; the gathering starts over.</summary>
    internal ChunkSizes? Close()
    {
        if (_bytes is null && _repetition is null && _definition is null)
        {
            return null;
        }

        ChunkSizes sizes = new ChunkSizes(_bytes?.ToArray(), _repetition?.ToArray(), _repetitionLevels, _definition?.ToArray(), _definitionLevels);
        _bytes?.Clear();
        _repetition?.Clear();
        _definition?.Clear();
        return sizes;
    }

    private static void Histogram(ReadOnlySpan<byte> levels, int count, List<long> into)
    {
        Span<long> counts = stackalloc long[count];
        counts.Clear();
        foreach (byte level in levels)
        {
            counts[level]++;
        }

        foreach (long found in counts)
        {
            into.Add(found);
        }
    }
}

/// <summary>A closed chunk's size statistics, its pages' one after another.</summary>
/// <param name="Bytes">Per page, its byte arrays' bytes; null but for a byte array column.</param>
/// <param name="Repetition">Per page, a count per repetition level; null where the histogram says nothing.</param>
/// <param name="RepetitionLevels">The repetition levels a page counts.</param>
/// <param name="Definition">Per page, a count per definition level; null where the histogram says nothing.</param>
/// <param name="DefinitionLevels">The definition levels a page counts.</param>
internal sealed record ChunkSizes(long[]? Bytes, long[]? Repetition, int RepetitionLevels, long[]? Definition, int DefinitionLevels)
{
    /// <summary>The chunk's <c>SizeStatistics</c>, field 16 of its metadata.</summary>
    internal void WriteChunk(ref ThriftCompactWriter writer)
    {
        short saved = writer.BeginStructField(16);
        if (Bytes is not null)
        {
            long total = 0;
            foreach (long bytes in Bytes)
            {
                total += bytes;
            }

            writer.WriteI64Field(1, total);
        }

        WriteTotals(ref writer, 2, Repetition, RepetitionLevels);
        WriteTotals(ref writer, 3, Definition, DefinitionLevels);
        writer.EndStruct(saved);
    }

    /// <summary>The pages' histograms, fields 6 and 7 of the column index, where the column has them.</summary>
    internal void WriteColumnIndex(ref ThriftCompactWriter writer)
    {
        WriteAll(ref writer, 6, Repetition);
        WriteAll(ref writer, 7, Definition);
    }

    /// <summary>The pages' byte array bytes, field 2 of the offset index, where the column has them.</summary>
    internal void WriteOffsetIndex(ref ThriftCompactWriter writer) => WriteAll(ref writer, 2, Bytes);

    private static void WriteTotals(ref ThriftCompactWriter writer, short id, long[]? pages, int levels)
    {
        if (pages is null)
        {
            return;
        }

        Span<long> totals = stackalloc long[levels];
        totals.Clear();
        for (int i = 0; i < pages.Length; i++)
        {
            totals[i % levels] += pages[i];
        }

        writer.WriteListField(id, ThriftType.I64, levels);
        foreach (long total in totals)
        {
            writer.WriteI64Element(total);
        }
    }

    private static void WriteAll(ref ThriftCompactWriter writer, short id, long[]? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WriteListField(id, ThriftType.I64, values.Length);
        foreach (long value in values)
        {
            writer.WriteI64Element(value);
        }
    }
}
