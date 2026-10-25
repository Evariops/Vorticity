using System;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// When an object is read in two passes, the columns of its filter first, then the others for the rows that filter keeps
/// alone (<see cref="DatasetScanBuilder"/>): when its sheets say the filter keeps so few rows that most chunks of the
/// widest column read would hold none of them. Each pass reads whole chunks, so a chunk with no row kept is a chunk the
/// second pass spares; with <c>k</c> rows kept among <c>c</c> chunks, about <c>min(k, c)</c> are read where every one
/// was, at the price of a round trip more for those.
/// </summary>
internal static class TwoPhaseReads
{
    /// <summary>The chunks of the widest column read under which an object is read in one pass: too few to spare.</summary>
    internal const long LeastChunks = 4;

    /// <summary>Whether <paramref name="kept"/> rows among <paramref name="chunks"/> chunks leave half of them empty or more.</summary>
    internal static bool Pays(long kept, long chunks) => chunks >= LeastChunks && kept * 2 <= chunks;

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
