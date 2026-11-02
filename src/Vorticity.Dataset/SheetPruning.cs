using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>
/// What a filter asks of the sheets of an object's columns (<see cref="ColumnSheet"/>): an equality or a set of constants
/// on a top-level column refuted when the column's filter lacks every constant, through <c>AND</c> and <c>OR</c>, so that
/// an object whose sheets prove no row matches is passed unopened. An object that keeps where each value of the columns
/// asked lies (<see cref="ColumnLocate"/>) is judged by those indexes in place of its sheets, or by the index of its
/// partition for the values asked (<see cref="PartitionLocates"/>): exactly, and to the granules where a selected row may lie
/// (<see cref="GranuleAlgebra"/>), which a scan reads alone.
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
    /// The shredded keys of variant columns of <paramref name="schema"/> whose values <paramref name="filter"/>'s
    /// equalities, sets and prefixes ask, through <c>AND</c> and <c>OR</c>, each once (spec E, 3.3); empty for none.
    /// </summary>
    internal static List<(int Field, string Key, PathClass Class)> Paths(VortexExpr filter, DType schema)
    {
        List<(int Field, string Key, PathClass Class)> paths = [];
        CollectPaths(filter, schema, paths);
        return paths;
    }

    private static void CollectPaths(VortexExpr filter, DType schema, List<(int Field, string Key, PathClass Class)> paths)
    {
        switch (filter)
        {
            case ComparisonExpr { Op: ComparisonOp.Equal } equal when GranuleAlgebra.PathOf(equal.Field, schema) is { } path:
                AddPath(paths, path);
                break;
            case InExpr set when GranuleAlgebra.PathOf(set.Field, schema) is { } path:
                AddPath(paths, path);
                break;
            case StringMatchExpr { Op: StringMatchOp.StartsWith } prefix when GranuleAlgebra.PathOf(prefix.Field, schema) is { } path:
                AddPath(paths, path);
                break;
            case LogicalExpr logical:
                CollectPaths(logical.Left, schema, paths);
                CollectPaths(logical.Right, schema, paths);
                break;
            default:
                break;
        }
    }

    private static void AddPath(List<(int Field, string Key, PathClass Class)> paths, (int Field, string Key, PathClass Class) path)
    {
        foreach ((int field, string key, PathClass @class) in paths)
        {
            if (field == path.Field && @class == path.Class && string.Equals(key, path.Key, StringComparison.Ordinal))
            {
                return;
            }
        }

        paths.Add(path);
    }

    /// <summary>Whether the object <paramref name="entry"/> names keeps where the values of one of <paramref name="paths"/> lie.</summary>
    internal static bool LocatesPaths(ObjectEntry entry, List<(int Field, string Key, PathClass Class)> paths)
    {
        foreach ((int field, string key, PathClass @class) in paths)
        {
            if (entry.Contents?.PathLocateOf(field, key, @class) is not null)
            {
                return true;
            }
        }

        return false;
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
    /// Whether the object <paramref name="entry"/> names keeps sheets a scan may read before it: its table of contents names
    /// sheets, and it holds the columns of <paramref name="schema"/> in their places (<see cref="DatasetSchema.Holds"/>).
    /// Whether reading them pays, the scan's rule of consultation says (<see cref="DatasetScanBuilder.ReadsSheets"/>).
    /// </summary>
    internal static bool Keeps(ObjectEntry entry, DatasetSchema schema) => entry.Contents is { Sheets.Count: > 0 } && schema.Holds(entry);

    /// <summary>
    /// What a scan reads of the object <paramref name="entry"/> names before it, under a filter that asks
    /// <paramref name="columns"/> for values: where their values lie when each keeps that index, else their sheets.
    /// </summary>
    internal static PathCosts.Cost Cost(ObjectEntry entry, List<int> columns, PathCosts costs, List<(int Field, string Key, PathClass Class)>? paths = null)
    {
        if (entry.Contents is null || (columns.Count == 0 && paths is not { Count: > 0 }))
        {
            return default;
        }

        ObjectContents contents = entry.Contents!;
        bool lies = Locates(entry, columns);
        int count = columns.Count + (paths?.Count ?? 0);
        Span<IndexSegment?> regions = count <= 16 ? stackalloc IndexSegment?[count] : new IndexSegment?[count];
        for (int i = 0; i < columns.Count; i++)
        {
            regions[i] = lies ? contents.LocateOf(columns[i]) : contents.SheetOf(columns[i]);
        }

        for (int i = 0; i < (paths?.Count ?? 0); i++)
        {
            regions[columns.Count + i] = contents.PathLocateOf(paths![i].Field, paths[i].Key, paths[i].Class);
        }

        return costs.Regions(entry.Uid, regions);
    }

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
    /// <paramref name="consulted"/> says the scan reads the sheets of have their sheets of <paramref name="columns"/> read
    /// side by side with no open of them; or, for an object that keeps where each value of every column of
    /// <paramref name="columns"/> lies, those indexes, or the index of its partition for the values asked
    /// (<paramref name="partitions"/>), which <paramref name="narrowed"/> is told the granules a selected row may lie in by, a
    /// bit a granule, when they leave some out.
    /// </summary>
    internal static async Task<List<PositionedObject>> KeptAsync(
        VortexDataset dataset, DatasetSchema schema, List<PositionedObject> batch, VortexExpr filter, List<int> columns, DatasetScanCounters? metrics,
        Func<PositionedObject, bool> consulted, CancellationToken cancellationToken, Action<PositionedObject, ulong[]>? narrowed = null,
        PartitionLocates? partitions = null, List<(int Field, string Key, PathClass Class)>? paths = null)
    {
        Task<ColumnSheet?[]>?[] reads = new Task<ColumnSheet?[]>?[batch.Count];
        Task<IValueGranules?[]>?[]? located = null;
        Task<ColumnLocate?[]>?[]? pathsLie = null;
        for (int i = 0; i < batch.Count; i++)
        {
            if (consulted(batch[i]))
            {
                if (Locates(batch[i].Entry, columns))
                {
                    (located ??= new Task<IValueGranules?[]>?[batch.Count])[i] = LocatesAsync(dataset, partitions, batch[i], columns, cancellationToken);
                }
                else if (columns.Count > 0)
                {
                    reads[i] = dataset.ReadSheetsAsync(batch[i].Entry, columns, cancellationToken).AsTask();
                }

                if (paths is { Count: > 0 } && LocatesPaths(batch[i].Entry, paths))
                {
                    (pathsLie ??= new Task<ColumnLocate?[]>?[batch.Count])[i] = dataset.ReadPathLocatesAsync(batch[i].Entry, paths, cancellationToken).AsTask();
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

            if (located?[i] is not null || pathsLie?[i] is not null)
            {
                IValueGranules?[] locates = located?[i] is { } lying ? await lying.ConfigureAwait(false) : [];
                ColumnLocate?[] pathLocates = pathsLie?[i] is { } pathLying ? await pathLying.ConfigureAwait(false) : [];
                ObjectEntry entry = batch[i].Entry;
                int granules = GranulesOf(locates, pathLocates);
                ulong[]? may = granules == 0 ? null : GranuleAlgebra.May(
                    filter, schema.DType, field => columns.IndexOf(field) is int at and >= 0 && at < locates.Length ? locates[at] : null, granules,
                    path => paths!.IndexOf(path) is int at and >= 0 && at < pathLocates.Length ? pathLocates[at] : null);
                if (may is not null && GranuleAlgebra.None(may))
                {
                    Refuted(metrics);
                    continue;
                }

                if (may is not null && GranuleAlgebra.Count(may) < granules)
                {
                    narrowed?.Invoke(batch[i], may);
                }
            }

            kept.Add(batch[i]);
        }

        return kept;
    }

    /// <summary>The granules of the object the first index of <paramref name="locates"/> or <paramref name="paths"/> describes; 0 for none.</summary>
    private static int GranulesOf(IValueGranules?[] locates, ColumnLocate?[] paths)
    {
        foreach (IValueGranules? locate in locates)
        {
            if (locate is not null)
            {
                return locate.Granules;
            }
        }

        foreach (ColumnLocate? path in paths)
        {
            if (path is not null)
            {
                return path.Granules;
            }
        }

        return 0;
    }

    /// <summary>
    /// Where the values of <paramref name="columns"/> lie in the object <paramref name="held"/>: by the index of its partition
    /// for the values asked when it covers the object for each, else by the object's own indexes.
    /// </summary>
    private static async Task<IValueGranules?[]> LocatesAsync(
        VortexDataset dataset, PartitionLocates? partitions, PositionedObject held, List<int> columns, CancellationToken cancellationToken)
    {
        if (partitions is not null && await partitions.LocatesAsync(held, cancellationToken).ConfigureAwait(false) is { } found)
        {
            return found;
        }

        return await dataset.ReadLocatesAsync(held.Entry, columns, cancellationToken).ConfigureAwait(false);
    }

    private static void Refuted(DatasetScanCounters? metrics)
    {
        if (metrics is not null)
        {
            metrics.ObjectsRefutedBySheets++;
        }
    }

    /// <summary>Whether the object <paramref name="entry"/> names keeps where the values of every column of <paramref name="columns"/> lie.</summary>
    internal static bool Locates(ObjectEntry entry, List<int> columns)
    {
        if (entry.Contents is not { Locates.Count: > 0 } contents || columns.Count == 0)
        {
            return false;
        }

        foreach (int field in columns)
        {
            if (contents.LocateOf(field) is null)
            {
                return false;
            }
        }

        return true;
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
