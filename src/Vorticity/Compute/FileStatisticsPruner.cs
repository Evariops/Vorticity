// File-level pruning - docs/11-write-strategy.md §6.3: "VortexFile.MayMatch(expr) answers from the
// footer's file statistics [...] without reading a data segment; an engine over many files calls
// it before opening a scan."
//
// THE FILE IS ONE ZONE. The statistics segment carries, per top-level field, the same aggregates a
// zone map carries per block -- min, max, their precision, the null count -- so the question is
// the zone pruner's question over a single zone as long as the file, and it is answered by the
// same code, under the same rule (docs/08-semantics.md §1: only a positive proof prunes). What has
// no file statistic answers "may match": a nested path, a non-struct root, a field whose bound is
// of a kind no filter literal can hold, a file with no statistics segment at all.
using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Whether a file may hold a matching row, from its statistics segment alone.</summary>
internal static class FileStatisticsPruner
{
    /// <summary>
    /// Whether <paramref name="file"/> may contain a row <paramref name="filter"/> selects.
    /// </summary>
    /// <param name="file">An open file; nothing of it is read.</param>
    /// <param name="filter">The predicate.</param>
    /// <returns><see langword="false"/> only when the file statistics prove no row can match.</returns>
    internal static bool MayMatch(VortexFile file, VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(filter);

        if (!file.HasFileStatistics || file.RowCount <= 0)
        {
            return true;
        }

        DType schema = file.Schema;
        if (schema.Kind != DTypeKind.Struct)
        {
            // A non-struct root's one statistic is typed against the whole file, and a filter names
            // fields: nothing to bind a bound to.
            return true;
        }

        List<string> paths = [];
        filter.CollectFields(paths);
        if (paths.Count == 0)
        {
            return true;
        }

        FileStatistics statistics = file.Statistics;
        List<ZoneColumn> columns = [];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (!seen.Add(path) || path.Contains('.', StringComparison.Ordinal))
            {
                // A nested field has no entry: the statistics are shallow, one per top-level field.
                continue;
            }

            int index = schema.IndexOfField(System.Text.Encoding.UTF8.GetBytes(path));
            if (index < 0 || index >= statistics.FieldCount)
            {
                continue;
            }

            FieldStatistics field = statistics.GetField(index);
            FilterLiteral min = default;
            FilterLiteral max = default;
            bool hasMin = field.HasMin && TryLiteral(field.Min, out min);
            bool hasMax = field.HasMax && TryLiteral(field.Max, out max);
            bool exact = (!field.HasMin || field.MinPrecision == StatPrecision.Exact)
                && (!field.HasMax || field.MaxPrecision == StatPrecision.Exact);
            bool hasNulls = field.TryGetNullCount(out ulong nulls) && nulls <= long.MaxValue;
            if (!hasMin && !hasMax && !hasNulls)
            {
                continue;
            }

            ZoneBounds bounds = ZoneBounds.Create(
                min, hasMin, max, hasMax, exact, hasNulls ? (long)nulls : 0, hasNulls);
            columns.Add(new ZoneColumn(Expr.Field(path), file.RowCount, file.RowCount, [bounds]));
        }

        if (columns.Count == 0)
        {
            return true;
        }

        ZonePruner pruner = new ZonePruner(filter, columns.ToArray());
        return pruner.MayMatch(new RowRange(0, file.RowCount));
    }

    /// <summary>
    /// A statistic's scalar as a filter literal, for the kinds a filter can name; the others carry
    /// no bound a comparison could use.
    /// </summary>
    /// <param name="value">The <c>min</c> or <c>max</c> statistic.</param>
    /// <param name="literal">Receives the literal.</param>
    /// <returns>Whether the kind converts.</returns>
    internal static bool TryLiteral(ScalarValue value, out FilterLiteral literal)
    {
        switch (value.Kind)
        {
            case ScalarValueKind.Bool:
                literal = FilterLiteral.From(value.AsBool);
                return true;
            case ScalarValueKind.Int64:
                literal = FilterLiteral.From(value.AsInt64);
                return true;
            case ScalarValueKind.UInt64:
                literal = FilterLiteral.From(value.AsUInt64);
                return true;
            case ScalarValueKind.F16:
                literal = FilterLiteral.From((double)value.AsF16);
                return true;
            case ScalarValueKind.F32:
                literal = FilterLiteral.From((double)value.AsF32);
                return true;
            case ScalarValueKind.F64:
                literal = FilterLiteral.From(value.AsF64);
                return true;
            case ScalarValueKind.String:
            case ScalarValueKind.Bytes:
                literal = FilterLiteral.From(value.AsBytes);
                return true;
            default:
                literal = default;
                return false;
        }
    }
}
