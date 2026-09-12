// Failure localization, which at 616 files and 1.8 million values is the difference between a
// usable oracle and a useless one. Every mismatch carries (file, column path, row) and both
// renderings, and the log stops collecting after a cap so one broken decoder does not print a
// million lines and hide the second broken decoder underneath.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vorticity.Conformance.Comparison;

/// <summary>One disagreement between the sidecar and what the reader produced.</summary>
internal readonly record struct Mismatch(
    string Column,
    long Row,
    string What,
    string Expected,
    string Actual)
{
    /// <summary>Renders the mismatch on one line, with the row and column that locate it.</summary>
    public override string ToString()
    {
        string where = Row >= 0
            ? $"column {Display(Column)} row {Row.ToString(CultureInfo.InvariantCulture)}"
            : $"column {Display(Column)}";

        return $"  {where}: {What}\n      expected {Expected}\n      actual   {Actual}";
    }

    private static string Display(string column) => column.Length == 0 ? "<root>" : column;
}

/// <summary>
/// The mismatches found while comparing one file. Capped: after <see cref="Limit"/> the log only
/// counts, because a decoder that is wrong is wrong on every row and the first few say everything.
/// </summary>
internal sealed class MismatchLog
{
    internal const int Limit = 12;

    private readonly List<Mismatch> _mismatches = new List<Mismatch>();

    internal MismatchLog(string entryId) => EntryId = entryId;

    /// <summary>The corpus entry being compared.</summary>
    internal string EntryId { get; }

    /// <summary>Every mismatch seen, including the ones past <see cref="Limit"/>.</summary>
    internal long Total { get; private set; }

    /// <summary>
    /// How many leaf values were actually compared. Reported because "0 mismatches" over 0
    /// comparisons is the failure mode a conformance harness dies of.
    /// </summary>
    internal long ValuesCompared { get; private set; }

    /// <summary>
    /// Paths whose null_count could not be checked because the file's own field names make the
    /// `.`-joined path ambiguous (SIDECAR.md, "null_counts paths"). Named, never silent.
    /// </summary>
    internal List<string> SkippedNullCountPaths { get; } = new List<string>();

    /// <summary>Counts one leaf comparison.</summary>
    internal void CountValue() => ValuesCompared++;

    /// <summary><see langword="true"/> when nothing disagreed.</summary>
    internal bool IsClean => Total == 0;

    /// <summary>The kinds of thing that disagreed, in first-seen order - the failure's shape.</summary>
    internal List<string> Kinds { get; } = new List<string>();

    internal void Add(string column, long row, string what, string expected, string actual)
    {
        Total++;
        if (_mismatches.Count < Limit)
        {
            _mismatches.Add(new Mismatch(column, row, what, expected, actual));
        }

        if (!Kinds.Contains(what))
        {
            Kinds.Add(what);
        }
    }

    /// <summary>The report for this file: every localized mismatch, then the count.</summary>
    internal string Render()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(EntryId).Append(": ").Append(Total.ToString(CultureInfo.InvariantCulture))
            .Append(Total == 1 ? " mismatch" : " mismatches");

        if (Total > _mismatches.Count)
        {
            builder.Append(" (first ").Append(_mismatches.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" shown)");
        }

        builder.Append('\n');
        foreach (Mismatch mismatch in _mismatches)
        {
            builder.Append(mismatch.ToString()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>A one-line summary of what differed, for the run-level report.</summary>
    internal string Summary()
    {
        if (IsClean)
        {
            return "clean";
        }

        string kinds = string.Join("; ", Kinds);
        Mismatch first = _mismatches[0];
        return $"{Total.ToString(CultureInfo.InvariantCulture)}x [{kinds}] first at " +
               $"column {(first.Column.Length == 0 ? "<root>" : first.Column)} row " +
               $"{first.Row.ToString(CultureInfo.InvariantCulture)}: expected {first.Expected}, " +
               $"actual {first.Actual}";
    }
}
