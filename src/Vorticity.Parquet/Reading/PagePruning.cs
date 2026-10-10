using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// The columns of a scan's filter that statistics may bound: those not under a list, whose bounds
/// are their rows' — a top-level column, or a struct's field. The pruner of a row group over the
/// chunk statistics of each.
/// </summary>
internal sealed class FilterColumns
{
    private FilterColumns(ParquetFile file, VortexExpr filter, FieldExpr[] fields, int[] columns)
    {
        File = file;
        Filter = filter;
        Fields = fields;
        Columns = columns;
    }

    internal ParquetFile File { get; }

    internal VortexExpr Filter { get; }

    /// <summary>Each column as the filter names it.</summary>
    internal FieldExpr[] Fields { get; }

    /// <summary>Each column's leaf.</summary>
    internal int[] Columns { get; }

    /// <summary>The columns of <paramref name="spec"/>'s filter statistics may bound; null without a filter, with pruning off, or when there is none.</summary>
    internal static FilterColumns? For(ParquetFile file, ScanSpec spec)
    {
        if (spec.Filter is not { } filter || !spec.Options.UseStatistics)
        {
            return null;
        }

        List<FieldExpr> paths = [];
        ScanBuilder.FieldsOf(filter, paths);
        List<FieldExpr> fields = [];
        List<int> columns = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (FieldExpr path in paths)
        {
            if (!seen.Add(path.Path))
            {
                continue;
            }

            // A column under a list has bounds over its elements, not its rows: none of a row's.
            foreach (ParquetColumn column in file.Compiled.Columns)
            {
                if (column.MaxRepetitionLevel == 0 && column.Form != LeafForm.Null && string.Equals(column.DottedPath, path.Path, StringComparison.Ordinal))
                {
                    fields.Add(new FieldExpr(path.Path));
                    columns.Add(column.Ordinal);
                    break;
                }
            }
        }

        return fields.Count == 0 ? null : new FilterColumns(file, filter, [.. fields], [.. columns]);
    }

    /// <summary>The pruner of row group <paramref name="group"/>, of <paramref name="rows"/> rows, one zone over its rows.</summary>
    internal ZonePruner Of(int group, long rows)
    {
        ParquetFooter footer = File.Footer;
        ZoneColumn[] zones = new ZoneColumn[Fields.Length];
        for (int i = 0; i < zones.Length; i++)
        {
            ParquetColumn column = File.Compiled.Columns[Columns[i]];
            ColumnChunkMetadata chunk = footer.Chunk(group, Columns[i]);
            ZoneBounds bounds = ColumnBounds.Of(column, chunk.Statistics, footer.Bytes);
            zones[i] = new ZoneColumn(Fields[i], rows, rows, [bounds], ColumnBounds.IsDecimal(column));
        }

        return new ZonePruner(Filter, zones);
    }
}

/// <summary>
/// The batches of a row group a scan's filter cannot select, proven by the page index of the
/// columns it reads: each batch bounded by the pages that cover its rows, their bounds from the
/// column index and their rows from the offset index.
/// </summary>
/// <remarks>
/// A batch one page covers takes that page's bounds; one that spans several takes the least lower
/// and the greatest upper of theirs, a page that holds no value adding none. Its null count is
/// known where the pages say: none, when none of them holds a null, and all of its rows, when they
/// hold nothing else. A page's bounds may have been cut, so they are never taken as exact.
/// </remarks>
internal static class PagePruning
{
    /// <summary>
    /// The live batches of <paramref name="rows"/> rows, of <paramref name="batchRows"/> each, of row
    /// group <paramref name="group"/>, the indexes of the filter's columns read in one request; null
    /// when no column has an index.
    /// </summary>
    internal static async ValueTask<BlockMask?> LiveAsync(
        FilterColumns filter, int group, long rows, int batchRows, SegmentRequestSet requests, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        ParquetFile file = filter.File;
        ParquetFooter footer = file.Footer;
        int columns = filter.Columns.Length;
        int[] columnSlots = new int[columns];
        int[] offsetSlots = new int[columns];
        int indexed = 0;
        long bytes = 0;
        requests.Release();
        for (int i = 0; i < columns; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(group, filter.Columns[i]);
            columnSlots[i] = -1;
            if (chunk.ColumnIndexOffset < 0 || chunk.OffsetIndexOffset < 0 || chunk.ColumnIndexLength <= 0 || chunk.OffsetIndexLength <= 0)
            {
                continue;
            }

            columnSlots[i] = requests.Add(new SegmentSpec((ulong)chunk.ColumnIndexOffset, (uint)chunk.ColumnIndexLength, 0, 0, 0));
            offsetSlots[i] = requests.Add(new SegmentSpec((ulong)chunk.OffsetIndexOffset, (uint)chunk.OffsetIndexLength, 0, 0, 0));
            bytes += chunk.ColumnIndexLength + (long)chunk.OffsetIndexLength;
            indexed++;
        }

        if (indexed == 0)
        {
            return null;
        }

        ScanCounters.Note(metrics, 2 * indexed, bytes);
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        int batches = (int)((rows + batchRows - 1) / batchRows);
        ZoneColumn[] zones = new ZoneColumn[columns];
        for (int i = 0; i < columns; i++)
        {
            ParquetColumn column = file.Compiled.Columns[filter.Columns[i]];
            ZoneBounds[] bounds = new ZoneBounds[batches];
            if (columnSlots[i] < 0)
            {
                Array.Fill(bounds, ZoneBounds.Unknown);
            }
            else
            {
                PageLocation[] pages = OffsetIndex.Read(requests.GetBuffer(offsetSlots[i]).Span, rows);
                ColumnIndex index = ColumnIndex.Read(requests.GetBuffer(columnSlots[i]).Span.ToArray(), pages.Length);
                if (Believable(column, pages, index, rows))
                {
                    Bound(column, pages, index, rows, batchRows, bounds);
                }
                else
                {
                    Array.Fill(bounds, ZoneBounds.Unknown);
                }
            }

            zones[i] = new ZoneColumn(filter.Fields[i], batchRows, rows, bounds, ColumnBounds.IsDecimal(column));
        }

        BlockMask live = new(rows, batchRows);
        ZonePruner pruner = new(filter.Filter, zones);
        if (pruner.IsUseful)
        {
            pruner.Refine(live);
        }

        return live;
    }

    /// <summary>
    /// Whether a column index says nothing its own fields contradict. A writer that built one
    /// without statistics marked every page null and counted -1 nulls in each: a negative count, a
    /// null page in a column that holds no null, or a null page that counts fewer nulls than rows
    /// disowns the whole index, which then bounds nothing.
    /// </summary>
    private static bool Believable(ParquetColumn column, PageLocation[] pages, ColumnIndex index, long rows)
    {
        long[]? counts = index.NullCounts;
        for (int page = 0; page < pages.Length; page++)
        {
            if (counts is not null && counts[page] < 0)
            {
                return false;
            }

            if (!index.NullPages[page])
            {
                continue;
            }

            long pageRows = (page + 1 < pages.Length ? pages[page + 1].FirstRow : rows) - pages[page].FirstRow;
            if (column.MaxDefinitionLevel == 0 || (counts is not null && counts[page] < pageRows))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Each batch's bounds from the pages that cover it.</summary>
    private static void Bound(ParquetColumn column, PageLocation[] pages, ColumnIndex index, long rows, int batchRows, ZoneBounds[] bounds)
    {
        bool decimals = ColumnBounds.IsDecimal(column);
        int page = 0;
        for (int batch = 0; batch < bounds.Length; batch++)
        {
            long start = (long)batch * batchRows;
            long end = Math.Min(start + batchRows, rows);
            while (page + 1 < pages.Length && pages[page + 1].FirstRow <= start)
            {
                page++;
            }

            FilterLiteral min = default;
            FilterLiteral max = default;
            bool hasMin = false;
            bool hasMax = false;
            bool usable = true;
            bool allNull = true;
            bool noNull = true;
            for (int p = page; p < pages.Length && pages[p].FirstRow < end; p++)
            {
                if (index.NullPages[p])
                {
                    noNull = false;
                    continue;
                }

                allNull = false;
                noNull &= index.NullCounts is { } counts && counts[p] == 0;
                if (!usable)
                {
                    continue;
                }

                if (!ColumnBounds.TryLiteral(column, index.Min(p), out FilterLiteral low) || !ColumnBounds.TryLiteral(column, index.Max(p), out FilterLiteral high))
                {
                    usable = false;
                    continue;
                }

                usable &= Merge(ref min, ref hasMin, low, lower: true, decimals) & Merge(ref max, ref hasMax, high, lower: false, decimals);
            }

            long batchLength = end - start;
            bool counted = allNull || noNull;
            bounds[batch] = ZoneBounds.Create(
                min, usable && hasMin, max, usable && hasMax, exact: false,
                allNull ? batchLength : 0, counted);
        }
    }

    /// <summary>Takes <paramref name="value"/> into the bound when it extends it; false when the two do not compare.</summary>
    private static bool Merge(ref FilterLiteral bound, ref bool has, FilterLiteral value, bool lower, bool decimals)
    {
        if (!has)
        {
            bound = value;
            has = true;
            return true;
        }

        // A wide decimal's literal is little-endian bytes, which compare as bytes no order of its.
        if (decimals && (bound.Kind != FilterLiteralKind.Signed || value.Kind != FilterLiteralKind.Signed))
        {
            return false;
        }

        if (!ZonePruner.TryCompare(value, bound, out int order))
        {
            return false;
        }

        if (lower ? order < 0 : order > 0)
        {
            bound = value;
        }

        return true;
    }
}
