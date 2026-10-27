using System;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How an object is read in two passes, the columns of its filter first, then the others for the rows that filter keeps
/// (<see cref="DatasetScanBuilder"/>, spec D, 5): decided after the first, by the rows it kept. When the blocks of
/// <see cref="BlockRows"/> rows that hold one are fewer than half the object's, the second pass reads the rows by their
/// places: the granules that hold one, where the object keeps their geometry (spec C, 3), and their blocks that do; otherwise
/// the granules that hold one, whole. With <c>k</c> rows kept among <c>b</c> blocks, about <c>min(k, b)</c> blocks are read
/// where every one was, at the price of a round trip more for those. The sheets' estimate of the rows kept rules an object
/// out before any pass, when it says half its blocks or more.
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
    /// The rows an object holds that <paramref name="filter"/> keeps, as the sheets of its columns, by place
    /// (<paramref name="sheetOf"/>), say: a value of the sheet's most frequent its rows; one its presence filter refutes,
    /// none; any other the rows its sheet leaves to the values past its most frequent, shared out evenly; a set their sum;
    /// a conjunction the least of its sides', a disjunction their sum. Null where they say nothing, a comparison or a column
    /// with no sheet: no fewer rows than the object's.
    /// </summary>
    internal static long? ExpectedRows(VortexExpr filter, DType schema, Func<int, ColumnSheet?> sheetOf) => filter switch
    {
        ComparisonExpr { Op: ComparisonOp.Equal } equal when Column(equal.Field, schema) is int field && sheetOf(field) is { } sheet =>
            Values([equal.Value], sheet, schema.GetField(field)),
        InExpr set when Column(set.Field, schema) is int field && sheetOf(field) is { } sheet => Values(set.Literals, sheet, schema.GetField(field)),
        LogicalExpr { IsAnd: true } and => Least(ExpectedRows(and.Left, schema, sheetOf), ExpectedRows(and.Right, schema, sheetOf)),
        LogicalExpr or => ExpectedRows(or.Left, schema, sheetOf) is long left && ExpectedRows(or.Right, schema, sheetOf) is long right ? left + right : null,
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
