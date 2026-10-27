using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How an object is read in two passes, the columns of its filter first, then the others for the rows that filter keeps
/// (<see cref="DatasetScanBuilder"/>, spec D, 5): decided after the first, by the rows it kept. When the blocks of
/// <see cref="BlockRows"/> rows that hold one are fewer than half the object's, the second pass reads the rows by their
/// places: the granules that hold one, where the object keeps their geometry (spec C, 3), and their blocks that do; otherwise
/// the granules that hold one, whole. With <c>k</c> rows kept among <c>b</c> blocks, about <c>min(k, b)</c> blocks are read
/// where every one was, at the price of a round trip more for those. The estimate of the rows kept, by the sheets or, for a
/// window on a number, by the object's summaries, rules an object out before any pass when it says half its blocks or more,
/// or rows so close in the widest column the second pass reads that the store's profile joins their reads into the column
/// whole (<see cref="Spares"/>): a first pass given up then would have read the filter's columns twice.
/// </summary>
internal static class TwoPhaseReads
{
    /// <summary>The granules under which an object is read in one pass: too few to spare.</summary>
    internal const long LeastGranules = 4;

    /// <summary>The rows of a block a second pass reads and decodes of a kept row: a FastLanes block (spec C, 3).</summary>
    internal const int BlockRows = 1_024;

    /// <summary>Whether a second pass reads the rows kept by their places: <paramref name="kept"/> blocks fewer than half of <paramref name="blocks"/>.</summary>
    internal static bool ByPlaces(long kept, long blocks) => kept * 2 < blocks;

    /// <summary>
    /// Whether a second pass over rows kept in <paramref name="kept"/> of an object's <paramref name="blocks"/> blocks spares
    /// reads at the store's profile: it reads them by their places (<see cref="ByPlaces"/>), and they lie so few in the
    /// widest column it reads, of <paramref name="widest"/> bytes, that two of them are more than the gap a read joins apart
    /// (<paramref name="joinedGap"/>). Closer, the reader joins their ranges into the column read whole, as one pass reads
    /// it, and the first pass is spent for nothing.
    /// </summary>
    internal static bool Spares(long kept, long blocks, long widest, int joinedGap) =>
        ByPlaces(kept, blocks) && (joinedGap <= 0 || kept <= 1 || widest / kept > joinedGap);

    /// <summary>
    /// The rows an object of <paramref name="rows"/> rows holds that <paramref name="filter"/> keeps, as the sheets of its
    /// columns, by place (<paramref name="sheetOf"/>), say: a value of the sheet's most frequent its rows; one its presence
    /// filter refutes, none; any other the rows its sheet leaves to the values past its most frequent, shared out evenly; a
    /// set their sum. An order comparison on a number, as its summary in <paramref name="summaries"/> says: its values spread
    /// evenly between its bounds (<see cref="Spread"/>). A conjunction the least of its sides', a disjunction their sum. Null
    /// where they say nothing: no fewer rows than the object's.
    /// </summary>
    internal static long? ExpectedRows(
        VortexExpr filter, DType schema, Func<int, ColumnSheet?> sheetOf, IReadOnlyList<ColumnSummary> summaries, long rows) => filter switch
    {
        ComparisonExpr { Op: ComparisonOp.Equal } equal when Column(equal.Field, schema) is int field && sheetOf(field) is { } sheet =>
            Values([equal.Value], sheet, schema.GetField(field)),
        ComparisonExpr { Op: ComparisonOp.Less or ComparisonOp.LessOrEqual or ComparisonOp.Greater or ComparisonOp.GreaterOrEqual } order
            when Column(order.Field, schema) is int => Spread(order, summaries, rows),
        InExpr set when Column(set.Field, schema) is int field && sheetOf(field) is { } sheet => Values(set.Literals, sheet, schema.GetField(field)),
        LogicalExpr { IsAnd: true } and =>
            Least(ExpectedRows(and.Left, schema, sheetOf, summaries, rows), ExpectedRows(and.Right, schema, sheetOf, summaries, rows)),
        LogicalExpr or => ExpectedRows(or.Left, schema, sheetOf, summaries, rows) is long left
            && ExpectedRows(or.Right, schema, sheetOf, summaries, rows) is long right
                ? left + right
                : null,
        _ => null,
    };

    /// <summary>
    /// The rows of an object of <paramref name="rows"/> rows that the order comparison <paramref name="order"/> keeps, the
    /// values of its column taken evenly spread between the bounds its summary in <paramref name="summaries"/> gives: the
    /// share of the span the comparison keeps, of the rows that are not null. Null for a column they do not bound, or not
    /// a number compared with a number.
    /// </summary>
    private static long? Spread(ComparisonExpr order, IReadOnlyList<ColumnSummary> summaries, long rows)
    {
        foreach (ColumnSummary summary in summaries)
        {
            if (!string.Equals(summary.Path, order.Field.Path, StringComparison.Ordinal))
            {
                continue;
            }

            if (!summary.HasMin || !summary.HasMax || Number(summary.Min) is not double low || Number(summary.Max) is not double high
                || Number(order.Value) is not double value)
            {
                return null;
            }

            bool below = order.Op is ComparisonOp.Less or ComparisonOp.LessOrEqual;
            double share = high > low ? (below ? value - low : high - value) / (high - low)
                : (below ? low <= value : low >= value) ? 1 : 0;
            long valid = rows - (summary.HasNullCount ? summary.NullCount : 0);
            return (long)Math.Ceiling(Math.Clamp(share, 0, 1) * Math.Max(valid, 0));
        }

        return null;
    }

    /// <summary>A number literal as a double; null for any other, or a NaN.</summary>
    private static double? Number(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Signed => literal.SignedValue,
        FilterLiteralKind.Unsigned => literal.UnsignedValue,
        FilterLiteralKind.Float when !double.IsNaN(literal.FloatValue) => literal.FloatValue,
        _ => null,
    };

    private static long? Least(long? left, long? right) => left is long l && right is long r ? Math.Min(l, r) : left ?? right;

    /// <summary>The rows of the values <paramref name="literals"/> of a column of <paramref name="dtype"/> by its sheet; null when one cannot be told.</summary>
    private static long? Values(FilterLiteral[] literals, ColumnSheet sheet, DType dtype)
    {
        long valid = sheet.Rows - sheet.Nulls;
        long topRows = 0;
        foreach (SheetValue top in sheet.Top)
        {
            topRows += top.Rows;
        }

        long distinct = sheet.IsExact ? sheet.Distinct : sheet.Estimate();
        long rest = Math.Max(0, valid - topRows);
        long others = Math.Max(1, distinct - sheet.Top.Count);
        long rows = 0;
        foreach (FilterLiteral literal in literals)
        {
            if (CountsAnswer.ValuesOf([literal], dtype) is not [byte[] value, ..])
            {
                return null;
            }

            SheetValue? frequent = null;
            foreach (SheetValue top in sheet.Top)
            {
                if (top.Value.AsSpan().SequenceEqual(value))
                {
                    frequent = top;
                    break;
                }
            }

            rows += frequent is { } held ? held.Rows : !sheet.MayContain(literal, dtype) ? 0 : (rest + others - 1) / others;
        }

        return rows;
    }

    /// <summary>The top-level column a field names, a sheet's: null for a nested field, a variant's path or a function of a column.</summary>
    private static int? Column(FieldExpr field, DType schema) =>
        field is ColumnFieldExpr && schema.Kind == DTypeKind.Struct && field.Path.IndexOf('.') < 0 && schema.IndexOfField(field.Path) is int index and >= 0
            ? index
            : null;
}
