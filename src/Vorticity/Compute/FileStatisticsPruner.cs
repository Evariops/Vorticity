using System;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Whether a file may hold a matching row, from its statistics segment alone, without reading a
/// data segment.
/// </summary>
/// <remarks>
/// The file is treated as a single zone: its statistics segment carries, per top-level field, the
/// same min, max, precision and null count a zone map carries per block, so the question is the
/// zone pruner's question over one zone as long as the file, answered by the same code and under
/// the same rule -- only a positive proof prunes. Anything with no file statistic answers "may
/// match": a nested path, a non-struct root, a field whose bound is of a kind no filter literal can
/// hold, a file with no statistics segment at all.
/// </remarks>
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

        // A dataset node asks the same question without a file open, so the bounds and the asking
        // both live in ColumnSummaries and this is only the caller who holds a file. A non-struct
        // root, a nested path, a field with no statistic and a bound of a kind no literal holds all
        // fall out of `Of` as absences, and an absence licenses no pruning.
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
