using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.Layouts;

namespace Vorticity;

/// <summary>What a question reads from (spec B, 3): a version of a dataset, a file, a stream.</summary>
internal enum LogicalSourceKind
{
    /// <summary>A file: a Vortex file, a Parquet file.</summary>
    File,

    /// <summary>A version of a dataset.</summary>
    Dataset,

    /// <summary>A stream of batches: a query's result.</summary>
    Stream,
}

/// <summary>The source a question reads: its kind, and a dataset's version.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Version">A dataset's version; 0 for another source.</param>
internal readonly record struct LogicalSource(LogicalSourceKind Kind, ulong Version = 0)
{
    public override string ToString() => Kind switch
    {
        LogicalSourceKind.Dataset => $"version {Version} of a dataset",
        LogicalSourceKind.File => "a file",
        _ => "a stream",
    };
}

/// <summary>
/// The shape of a question's answer (spec B, 3): its rows, an aggregate by keys, a count, whether any row is, a column's
/// extreme, a column's keys. A shape names what the answer is, never a structure that finds it.
/// </summary>
internal abstract record LogicalShape
{
    /// <summary>A count of the rows.</summary>
    internal static LogicalShape Count { get; } = new CountShape();

    /// <summary>Whether a row is.</summary>
    internal static LogicalShape Any { get; } = new AnyShape();

    /// <summary>Whether the answer reads the columns the question reads, rather than counting or proving its rows.</summary>
    internal virtual bool ReadsColumns => true;

    private sealed record CountShape : LogicalShape
    {
        internal override bool ReadsColumns => false;

        public override string ToString() => "count";
    }

    private sealed record AnyShape : LogicalShape
    {
        internal override bool ReadsColumns => false;

        public override string ToString() => "any";
    }
}

/// <summary>The rows, in the order of a column or as they lie, those of a window of them.</summary>
/// <param name="OrderPath">The column whose order they come in; null as they lie.</param>
/// <param name="Descending">Whether the largest comes first.</param>
/// <param name="Skip">The rows of the window passed over.</param>
/// <param name="Take">The rows of the window kept; <see cref="long.MaxValue"/> for every one.</param>
internal sealed record RowsShape(string? OrderPath = null, bool Descending = false, long Skip = 0, long Take = long.MaxValue) : LogicalShape
{
    /// <summary>The rows as they lie, every one.</summary>
    internal static RowsShape All { get; } = new RowsShape();

    public override string ToString()
    {
        StringBuilder text = new StringBuilder("rows");
        if (OrderPath is not null)
        {
            text.Append(" in the order of ").Append(OrderPath).Append(Descending ? " descending" : string.Empty);
        }

        if (Skip > 0)
        {
            text.Append(", past ").Append(Skip);
        }

        if (Take != long.MaxValue)
        {
            text.Append(", ").Append(Take).Append(" at most");
        }

        return text.ToString();
    }
}

/// <summary>The smallest or largest value of a column among the rows.</summary>
/// <param name="Path">The column.</param>
/// <param name="Min">Whether the smallest.</param>
internal sealed record ExtremeShape(string Path, bool Min) : LogicalShape
{
    internal override bool ReadsColumns => false;

    public override string ToString() => $"{(Min ? "min" : "max")}({Path})";
}

/// <summary>The keys of a column, in its order: every entry, or each value once.</summary>
/// <param name="Path">The column.</param>
/// <param name="Distinct">Whether each value once.</param>
internal sealed record KeysShape(string Path, bool Distinct) : LogicalShape
{
    internal override bool ReadsColumns => false;

    public override string ToString() => $"{(Distinct ? "distinct keys" : "keys")} of {Path}";
}

/// <summary>
/// An aggregate of the rows by keys: the groups of the keys' values, each the aggregates' answers, and what follows the group
/// by on the groups, a filter, an order, a window; no key for one group.
/// </summary>
/// <param name="Keys">The columns of the key, or the functions of them.</param>
/// <param name="Aggregates">The aggregates, each once.</param>
/// <param name="Operators">What follows the group by, in the order written.</param>
internal sealed record AggregateShape(ColumnShape[] Keys, IAggregateNode[] Aggregates, GroupOperator[] Operators) : LogicalShape
{
    internal override bool ReadsColumns => false;

    public override string ToString()
    {
        StringBuilder text = new StringBuilder("aggregate");
        if (Aggregates.Length > 0)
        {
            text.Append(' ');
            for (int a = 0; a < Aggregates.Length; a++)
            {
                AggregateIdentity identity = Aggregates[a].Identity;
                text.Append(a == 0 ? string.Empty : ", ").Append(identity.Kind.ToString().ToLowerInvariant());
                if (identity.Input is { } input)
                {
                    text.Append('(').Append(input).Append(')');
                }

                if (identity.Filter is { } filter)
                {
                    text.Append(" where ").Append(filter);
                }
            }
        }

        if (Keys.Length > 0)
        {
            text.Append(" by ");
            for (int k = 0; k < Keys.Length; k++)
            {
                text.Append(k == 0 ? string.Empty : ", ").Append(Keys[k].Key);
            }
        }

        foreach (GroupOperator op in Operators)
        {
            switch (op)
            {
                case GroupFilter filter:
                    text.Append(", groups where ").Append(filter.Predicate);
                    break;
                case GroupOrder order:
                    text.Append(", groups in the order of ");
                    for (int k = 0; k < order.Keys.Length; k++)
                    {
                        text.Append(k == 0 ? string.Empty : ", ").Append(order.Keys[k].Field.Path).Append(order.Keys[k].Descending ? " descending" : string.Empty);
                    }

                    break;
                case GroupWindow window:
                    text.Append(", groups past ").Append(window.Skip);
                    if (window.Take != long.MaxValue)
                    {
                        text.Append(", ").Append(window.Take).Append(" at most");
                    }

                    break;
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// A question as a planner takes it (spec B, 3): its source, its filter, the rows it selects by their place, the columns it
/// reads, the shape of its answer, its options; what it wants, never how. Every scan, aggregation, count and walk of keys
/// builds one; the spec its source runs derives from it (<see cref="Spec"/>), and its explanation shows it
/// (<see cref="ScanPlan.Logical"/>, <see cref="KeyPlan.Logical"/>).
/// </summary>
/// <param name="Source">What it reads from.</param>
/// <param name="Schema">The source's columns, which name those it reads.</param>
/// <param name="Shape">The shape of its answer.</param>
internal sealed record LogicalQuery(LogicalSource Source, VortexSchema Schema, LogicalShape Shape)
{
    /// <summary>The filter on the rows; null for every row.</summary>
    internal VortexExpr? Filter { get; init; }

    /// <summary>Whether the filter is known to keep no row.</summary>
    internal bool MatchesNothing { get; init; }

    /// <summary>The rows of a range, by their place in a file; null for every row.</summary>
    internal RowRange? Range { get; init; }

    /// <summary>The rows at these places in a file, sorted and distinct; null for every row.</summary>
    internal long[]? Positions { get; init; }

    /// <summary>The columns it reads; null for every column.</summary>
    internal FieldMask? Reads { get; init; }

    /// <summary>The variant columns, by name, it reads only through these top-level keys; null when it reads each whole.</summary>
    internal IReadOnlyDictionary<string, string[]>? VariantKeys { get; init; }

    /// <summary>The options: the structures it may consult, the synopses it may answer from, the window it may align.</summary>
    internal ScanOptions Options { get; init; } = ScanOptions.Default;

    /// <summary>Whether an estimate answers it, drawn from ranges of its rows, rather than every row.</summary>
    internal bool Estimated { get; init; }

    /// <summary>
    /// The spec a source runs for it: its columns, filter, rows, order and options; a pick of rows by their places with no
    /// filter delivers whole blocks with their selection, which copies nothing.
    /// </summary>
    internal ScanSpec Spec()
    {
        RowsShape? rows = Shape as RowsShape;
        return new ScanSpec
        {
            Query = this,
            Projection = Reads,
            Filter = Filter,
            MatchesNothing = MatchesNothing,
            Rows = Range,
            Take = Positions,
            OrderPath = rows?.OrderPath,
            Descending = rows?.Descending ?? false,
            Options = Positions is not null && Filter is null && !MatchesNothing ? Options with { Compact = false } : Options,
            VariantKeys = VariantKeys,
        };
    }

    /// <summary>The same question as an aggregate of <paramref name="plan"/>, then <paramref name="operators"/> on its groups.</summary>
    internal LogicalQuery Aggregating(AggregationPlan plan, GroupOperator[]? operators) =>
        this with { Shape = new AggregateShape(plan.Keys, plan.Aggregates, operators ?? []) };

    /// <summary>The question in words: its shape, its source, the columns it reads, its filter and rows, its options.</summary>
    internal string Text
    {
        get
        {
            StringBuilder text = new StringBuilder(Shape.ToString()).Append(" of ").Append(Source);
            if (Shape.ReadsColumns && Reads is { IsAll: false } reads)
            {
                text.Append(", reading ");
                int named = 0;
                for (int column = 0; column < Schema.Count; column++)
                {
                    if (reads.Includes(column))
                    {
                        text.Append(named++ == 0 ? string.Empty : ", ").Append(Schema[column].Name);
                    }
                }

                if (named == 0)
                {
                    text.Append("no column");
                }
            }

            if (MatchesNothing)
            {
                text.Append(", where nothing matches");
            }
            else if (Filter is { } filter)
            {
                text.Append(", where ").Append(filter);
            }

            if (Range is { } range)
            {
                text.Append(", rows ").Append(range.Start).Append(" to ").Append(range.End);
            }

            if (Positions is { } positions)
            {
                text.Append(", ").Append(positions.Length).Append(positions.Length == 1 ? " row by its place" : " rows by their places");
            }

            text.Append(Estimated ? "; estimated" : "; exact");
            text.Append(Options.UseSynopses ? ", synopses" : ", no synopsis");
            if (!Options.UseStatistics)
            {
                text.Append(", no statistics");
            }

            if (!Options.UseIndexes)
            {
                text.Append(", no index");
            }

            if (Options.AlignWindow)
            {
                text.Append(", window aligned");
            }

            return text.ToString();
        }
    }

    public override string ToString() => Text;
}
