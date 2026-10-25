using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// What a filter asks of the sheets of an object's columns (<see cref="ColumnSheet"/>): an equality or a set of constants
/// on a top-level column refuted when the column's filter lacks every constant, through <c>AND</c> and <c>OR</c>, so that
/// an object whose sheets prove no row matches is passed unopened. An object that keeps where each value of the columns
/// asked lives (<see cref="ColumnWhere"/>) is judged by those indexes in place of its sheets: exactly, and to the ranges of
/// its rows where a selected row may lie, which a scan reads alone.
/// </summary>
internal static class SheetPruning
{
    /// <summary>The top-level columns of <paramref name="schema"/> whose sheets may refute <paramref name="filter"/>: empty for none.</summary>
    internal static List<int> Columns(VortexExpr filter, DType schema)
    {
        List<int> columns = [];
        Collect(filter, schema, columns);
        return columns;
    }

    /// <summary>
    /// Whether the sheets of columns of <paramref name="schema"/> may refute <paramref name="filter"/>, with no allocation:
    /// a conjunction when either side may be, a disjunction when both may.
    /// </summary>
    internal static bool Asks(VortexExpr filter, DType schema) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal => Column(equal.Field, schema) is not null,
            InExpr set => Column(set.Field, schema) is not null,
            LogicalExpr { IsAnd: true } and => Asks(and.Left, schema) || Asks(and.Right, schema),
            LogicalExpr or => Asks(or.Left, schema) && Asks(or.Right, schema),
            _ => false,
        };

    /// <summary>
    /// Whether a scan reads the sheets of the object <paramref name="entry"/> names before it: an object of
    /// <see cref="DatasetOptions.SheetObjectBytes"/> or more whose table of contents names sheets, and which holds the
    /// columns of <paramref name="schema"/> in their places (<see cref="DatasetSchema.Holds"/>). A smaller one costs no
    /// more to read than its sheets, and keeps none.
    /// </summary>
    internal static bool Reads(ObjectEntry entry, DatasetOptions options, DatasetSchema schema) =>
        entry.Bytes >= options.SheetObjectBytes && entry.Contents is { Sheets.Count: > 0 } && schema.Holds(entry);

    /// <summary>
    /// Whether a row <paramref name="filter"/> selects may lie in an object whose sheets <paramref name="sheetOf"/> gives
    /// by column: false only when they prove none can.
    /// </summary>
    internal static bool MayMatch(VortexExpr filter, DType schema, System.Func<int, ColumnSheet?> sheetOf) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal when Column(equal.Field, schema) is int field && sheetOf(field) is { } sheet =>
                sheet.MayContain(equal.Value, schema.GetField(field)),
            InExpr set when Column(set.Field, schema) is int field && sheetOf(field) is { } sheet => Any(set.Literals, sheet, schema.GetField(field)),
            LogicalExpr { IsAnd: true } and => MayMatch(and.Left, schema, sheetOf) && MayMatch(and.Right, schema, sheetOf),
            LogicalExpr or => MayMatch(or.Left, schema, sheetOf) || MayMatch(or.Right, schema, sheetOf),
            _ => true,
        };

    /// <summary>
    /// The objects of <paramref name="batch"/> whose sheets do not refute <paramref name="filter"/>, over <paramref name="schema"/>, in its order: those
    /// whose sheets a scan reads (<see cref="Reads"/>), but those <paramref name="unread"/> leaves, have their sheets of
    /// <paramref name="columns"/> read side by side with no open of them; or, for an object that keeps where each value of
    /// every column of <paramref name="columns"/> lives, those indexes, which <paramref name="narrowed"/> is told the
    /// ranges of the object's rows a selected row may lie in by, when they leave some out.
    /// </summary>
    internal static async Task<List<PositionedObject>> KeptAsync(
        VortexDataset dataset, DatasetSchema schema, List<PositionedObject> batch, VortexExpr filter, List<int> columns, DatasetScanCounters? metrics,
        Func<PositionedObject, bool>? unread, CancellationToken cancellationToken, Action<PositionedObject, RowRange[]>? narrowed = null)
    {
        Task<ColumnSheet?[]>?[] reads = new Task<ColumnSheet?[]>?[batch.Count];
        Task<(ColumnWhere? Where, ColumnBlocks? Blocks)[]>?[]? placed = null;
        for (int i = 0; i < batch.Count; i++)
        {
            if (Reads(batch[i].Entry, dataset.Options, schema) && unread?.Invoke(batch[i]) != true)
            {
                if (Places(batch[i].Entry, columns))
                {
                    (placed ??= new Task<(ColumnWhere?, ColumnBlocks?)[]>?[batch.Count])[i] = dataset.ReadBlockColumnsAsync(batch[i].Entry, columns, stats: false, cancellationToken).AsTask();
                }
                else
                {
                    reads[i] = dataset.ReadSheetsAsync(batch[i].Entry, columns, cancellationToken).AsTask();
                }
            }
        }

        List<PositionedObject> kept = new List<PositionedObject>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            if (reads[i] is { } read)
            {
                ColumnSheet?[] sheets = await read.ConfigureAwait(false);
                if (!MayMatch(filter, schema.DType, field => columns.IndexOf(field) is int at and >= 0 ? sheets[at] : null))
                {
                    Refuted(metrics);
                    continue;
                }
            }

            if (placed?[i] is { } place)
            {
                (ColumnWhere? Where, ColumnBlocks? Blocks)[] wheres = await place.ConfigureAwait(false);
                RowRange[]? ranges = RangesOf(filter, schema.DType, field => columns.IndexOf(field) is int at and >= 0 ? wheres[at].Where : null);
                if (ranges is { Length: 0 })
                {
                    Refuted(metrics);
                    continue;
                }

                if (ranges is not null)
                {
                    narrowed?.Invoke(batch[i], ranges);
                }
            }

            kept.Add(batch[i]);
        }

        return kept;
    }

    private static void Refuted(DatasetScanCounters? metrics)
    {
        if (metrics is not null)
        {
            metrics.ObjectsRefutedBySheets++;
        }
    }

    /// <summary>Whether the object <paramref name="entry"/> names keeps where the values of every column of <paramref name="columns"/> live.</summary>
    private static bool Places(ObjectEntry entry, List<int> columns)
    {
        if (entry.Contents is not { Wheres.Count: > 0 } contents || columns.Count == 0)
        {
            return false;
        }

        foreach (int field in columns)
        {
            if (contents.WhereOf(field) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The ranges of an object's rows, ascending and apart, where a row <paramref name="filter"/> selects may lie, by where
    /// the values of its columns live (<paramref name="whereOf"/>): an equality or a set on a column the ranges of the
    /// values asked, none for a value the column lacks, through <c>AND</c> and <c>OR</c>; null for every row, where a
    /// conjunct asks no index or a value lives everywhere.
    /// </summary>
    internal static RowRange[]? RangesOf(VortexExpr filter, DType schema, Func<int, ColumnWhere?> whereOf) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal when Column(equal.Field, schema) is int field && whereOf(field) is { } where =>
                Placed([equal.Value], schema.GetField(field), where),
            InExpr set when Column(set.Field, schema) is int field && whereOf(field) is { } where => Placed(set.Literals, schema.GetField(field), where),
            LogicalExpr { IsAnd: true } and => Intersect(RangesOf(and.Left, schema, whereOf), RangesOf(and.Right, schema, whereOf)),
            LogicalExpr or => Union(RangesOf(or.Left, schema, whereOf), RangesOf(or.Right, schema, whereOf)),
            _ => null,
        };

    /// <summary>The ranges of the values <paramref name="literals"/> of a column of <paramref name="dtype"/>; null where one lives everywhere, or cannot be told.</summary>
    private static RowRange[]? Placed(FilterLiteral[] literals, DType dtype, ColumnWhere where)
    {
        if (CountsAnswer.ValuesOf(literals, dtype) is not { } values)
        {
            return null;
        }

        RowRange[] ranges = [];
        foreach (byte[] value in values)
        {
            int place = where.Find(value);
            if (place < 0)
            {
                continue;
            }

            if (where.Everywhere(place))
            {
                return null;
            }

            List<RowRange> own = [];
            foreach ((long start, long end) in where.RangesOf(place))
            {
                own.Add(new RowRange(start, end));
            }

            ranges = Union(ranges, [.. own])!;
        }

        return ranges;
    }

    /// <summary>The rows in both lists of ranges, null for every row.</summary>
    private static RowRange[]? Intersect(RowRange[]? left, RowRange[]? right)
    {
        if (left is null || right is null)
        {
            return left ?? right;
        }

        List<RowRange> both = [];
        for (int l = 0, r = 0; l < left.Length && r < right.Length;)
        {
            RowRange common = left[l].Intersect(right[r]);
            if (!common.IsEmpty)
            {
                both.Add(common);
            }

            if (left[l].End <= right[r].End)
            {
                l++;
            }
            else
            {
                r++;
            }
        }

        return [.. both];
    }

    /// <summary>The rows in either list of ranges, joined where they touch, null for every row.</summary>
    private static RowRange[]? Union(RowRange[]? left, RowRange[]? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        List<RowRange> either = [];
        for (int l = 0, r = 0; l < left.Length || r < right.Length;)
        {
            RowRange next = r == right.Length || (l < left.Length && left[l].Start <= right[r].Start) ? left[l++] : right[r++];
            if (either.Count > 0 && next.Start <= either[^1].End)
            {
                either[^1] = new RowRange(either[^1].Start, Math.Max(either[^1].End, next.End));
            }
            else
            {
                either.Add(next);
            }
        }

        return [.. either];
    }

    private static bool Any(FilterLiteral[] literals, ColumnSheet sheet, DType dtype)
    {
        foreach (FilterLiteral literal in literals)
        {
            if (sheet.MayContain(literal, dtype))
            {
                return true;
            }
        }

        return false;
    }

    private static void Collect(VortexExpr filter, DType schema, List<int> columns)
    {
        switch (filter)
        {
            case ComparisonExpr { Op: ComparisonOp.Equal } equal when Column(equal.Field, schema) is int field:
                Add(columns, field);
                break;
            case InExpr set when Column(set.Field, schema) is int field:
                Add(columns, field);
                break;
            case LogicalExpr logical when Asks(logical, schema):
                Collect(logical.Left, schema, columns);
                Collect(logical.Right, schema, columns);
                break;
            default:
                break;
        }
    }

    private static void Add(List<int> columns, int field)
    {
        if (!columns.Contains(field))
        {
            columns.Add(field);
        }
    }

    /// <summary>The top-level column a field names, a sheet's: null for a nested field, a variant's path or a function of a column.</summary>
    private static int? Column(FieldExpr field, DType schema) =>
        field is ColumnFieldExpr && schema.Kind == DTypeKind.Struct && field.Path.IndexOf('.') < 0 && schema.IndexOfField(field.Path) is int index and >= 0
            ? index
            : null;
}
