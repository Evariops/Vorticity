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
/// an object whose sheets prove no row matches is passed unopened.
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
    /// <paramref name="columns"/> read side by side with no open of them.
    /// </summary>
    internal static async Task<List<PositionedObject>> KeptAsync(
        VortexDataset dataset, DatasetSchema schema, List<PositionedObject> batch, VortexExpr filter, List<int> columns, DatasetScanCounters? metrics,
        System.Func<PositionedObject, bool>? unread, CancellationToken cancellationToken)
    {
        Task<ColumnSheet?[]>?[] reads = new Task<ColumnSheet?[]>?[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            if (Reads(batch[i].Entry, dataset.Options, schema) && unread?.Invoke(batch[i]) != true)
            {
                reads[i] = dataset.ReadSheetsAsync(batch[i].Entry, columns, cancellationToken).AsTask();
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
                    if (metrics is not null)
                    {
                        metrics.ObjectsRefutedBySheets++;
                    }

                    continue;
                }
            }

            kept.Add(batch[i]);
        }

        return kept;
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
