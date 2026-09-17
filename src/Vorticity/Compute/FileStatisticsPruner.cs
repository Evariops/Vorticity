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
//
// THE SAME QUESTION IS ASKED WITHOUT A FILE by a dataset's node, which holds the same three
// aggregates for an object it has not opened (docs/13-dataset.md §4.2). So the bounds and the
// asking both live in `Vorticity.Scan.ColumnSummaries`, and this file is the caller who happens
// to have a file open.
using System;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
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

        // ONE IMPLEMENTATION, and this is the caller who holds a file rather than kept bounds: the
        // summaries of the fields the filter names, asked of the same pruner a dataset asks
        // (Vorticity.Scan.ColumnSummaries). A non-struct root, a nested path, a field with no
        // statistic and a bound of a kind no literal holds all fall out of `Of` as absences, and an
        // absence licenses nothing.
        SummaryPruner pruner = new SummaryPruner(filter);
        return !pruner.ReadsColumns
            || pruner.MayMatch(ColumnSummaries.Of(file, pruner.Paths), file.RowCount);
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
