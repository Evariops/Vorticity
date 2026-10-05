using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Aggregating;

/// <summary>One condition of a filtered group: the rows where a predicate is true, or, for <c>All</c>, where it is not.</summary>
/// <param name="Predicate">The predicate, in the language of <c>Where</c>.</param>
/// <param name="Text">Its text, what makes two predicates one.</param>
/// <param name="Holds">Whether the rows kept are those where it is true; those where it is false or unknown otherwise.</param>
internal readonly record struct RowCondition(VortexExpr Predicate, string Text, bool Holds)
{
    internal string Key => Holds ? Text : $"not true ({Text})";
}

/// <summary>
/// The rows an aggregate of a filtered group reads: those where each of its conditions holds. SQL's
/// <c>FILTER (WHERE …)</c>, which <c>g.Where(x => p)</c> writes, and <c>Count(p)</c>,
/// <c>Any(p)</c> and <c>All(p)</c> are made of.
/// </summary>
internal sealed class RowFilter
{
    /// <summary>A filter no row passes: a predicate that keeps none, or one asked to be true and not true.</summary>
    internal static readonly RowFilter Nothing = new RowFilter([], nothing: true);

    private RowFilter(RowCondition[] conditions, bool nothing)
    {
        Conditions = conditions;
        IsNothing = nothing;
        Key = nothing ? "nothing" : string.Join(" and ", Array.ConvertAll(conditions, c => c.Key));
    }

    /// <summary>The conditions, each once, in the order of their keys.</summary>
    internal RowCondition[] Conditions { get; }

    /// <summary>Whether no row passes.</summary>
    internal bool IsNothing { get; }

    /// <summary>What makes two filters one, and how a plan shows it.</summary>
    internal string Key { get; }

    /// <summary>
    /// <paramref name="filter"/> with <paramref name="predicate"/> true besides, or not true when
    /// <paramref name="holds"/> is false; null for no filter.
    /// </summary>
    /// <exception cref="InvalidOperationException">The predicate compares a result: a filtered group filters rows.</exception>
    internal static RowFilter? And(RowFilter? filter, Predicate predicate, bool holds)
    {
        if (predicate.IsAll)
        {
            // Every row is true: no condition, or, not true, no row.
            return holds ? filter : Nothing;
        }

        if (predicate.IsNone)
        {
            return holds ? Nothing : filter;
        }

        if (filter is { IsNothing: true })
        {
            return filter;
        }

        VortexExpr node = predicate.Node!;
        if (GroupPredicates.ReadsResults(node))
        {
            throw new InvalidOperationException("A filtered group keeps rows by their columns: an aggregate is a result of the group, compared by a Where after the GroupBy.");
        }

        RowCondition added = new RowCondition(node, ExprText.Format(node), holds);
        RowCondition[] known = filter?.Conditions ?? [];
        List<RowCondition> conditions = [.. known];
        foreach (RowCondition condition in known)
        {
            if (string.Equals(condition.Text, added.Text, StringComparison.Ordinal))
            {
                // The same predicate again is no new condition; true and not true at once, no row.
                return condition.Holds == holds ? filter : Nothing;
            }
        }

        conditions.Add(added);
        conditions.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new RowFilter([.. conditions], nothing: false);
    }

    public override string ToString() => Key;
}

/// <summary>
/// The selection of each filter of a plan over one batch: each condition evaluated once, however many
/// filters and aggregates read it, then each filter's conditions joined with the rows the scan kept.
/// Its buffers are grown and kept, so that a batch allocates nothing once warm.
/// </summary>
internal sealed class FilterMasks
{
    private readonly FilterPlan _plan;
    private readonly ulong[][] _true;
    private readonly ulong[][] _notTrue;
    private readonly ulong[][] _filters;
    private readonly int[] _selected;
    private byte[] _states = [];
    private int _rows;

    internal FilterMasks(FilterPlan plan)
    {
        _plan = plan;
        _true = new ulong[plan.Conditions.Length][];
        _notTrue = new ulong[plan.Conditions.Length][];
        _filters = new ulong[plan.Filters.Length][];
        _selected = new int[plan.Filters.Length];
        Array.Fill(_true, []);
        Array.Fill(_notTrue, []);
        Array.Fill(_filters, []);
    }

    /// <summary>Evaluates the conditions over a batch and works out each filter's selection.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="root">The batch's root.</param>
    /// <param name="rows">The batch's rows.</param>
    /// <param name="selection">The rows the scan kept; empty when it kept every one.</param>
    internal void Evaluate(CanonicalArena arena, int root, int rows, ReadOnlySpan<ulong> selection)
    {
        _rows = rows;
        int words = Math.Max((rows + 63) >> 6, 1);
        Scratch.Grow(ref _states, rows);
        Span<byte> states = _states.AsSpan(0, rows);
        for (int c = 0; c < _plan.Conditions.Length; c++)
        {
            FilterPlan.Condition condition = _plan.Conditions[c];
            condition.Evaluator.Evaluate(arena, root, rows, states);
            if (condition.True)
            {
                Scratch.Grow(ref _true[c], words);
                _true[c][0] = 0;
                Trilean.ToWords(states, Trilean.True, equal: true, _true[c]);
            }

            if (condition.NotTrue)
            {
                Scratch.Grow(ref _notTrue[c], words);
                _notTrue[c][0] = 0;
                Trilean.ToWords(states, Trilean.True, equal: false, _notTrue[c]);
            }
        }

        for (int f = 0; f < _plan.Filters.Length; f++)
        {
            Scratch.Grow(ref _filters[f], words);
            Span<ulong> into = _filters[f].AsSpan(0, words);
            RowFilter filter = _plan.Filters[f];
            if (filter.IsNothing)
            {
                into.Clear();
                _selected[f] = 0;
                continue;
            }

            if (selection.IsEmpty)
            {
                into.Fill(ulong.MaxValue);
            }
            else
            {
                selection[..words].CopyTo(into);
            }

            foreach ((int condition, bool holds) in _plan.Parts[f])
            {
                ReadOnlySpan<ulong> part = (holds ? _true[condition] : _notTrue[condition]).AsSpan(0, words);
                for (int w = 0; w < words; w++)
                {
                    into[w] &= part[w];
                }
            }

            // The conditions' words leave the bits past the rows clear, and every filter has one.
            int count = 0;
            foreach (ulong word in into)
            {
                count += System.Numerics.BitOperations.PopCount(word);
            }

            _selected[f] = count;
        }
    }

    /// <summary>The rows filter <paramref name="filter"/> keeps of the batch: empty for every row, as a batch's selection says it.</summary>
    internal ReadOnlySpan<ulong> Selection(int filter) =>
        _selected[filter] == _rows ? default : _filters[filter].AsSpan(0, Math.Max((_rows + 63) >> 6, 1));

    /// <summary>Whether filter <paramref name="filter"/> keeps no row of the batch, so that its aggregates have nothing to fold.</summary>
    internal bool KeepsNone(int filter) => _selected[filter] == 0;
}

/// <summary>The filters of a plan's aggregates, each once, and their conditions, each evaluated once a batch.</summary>
internal sealed class FilterPlan
{
    internal FilterPlan(IAggregateNode[] aggregates)
    {
        List<RowFilter> filters = [];
        List<Condition> conditions = [];
        List<(int, bool)[]> parts = [];
        FilterOf = new int[aggregates.Length];
        for (int i = 0; i < aggregates.Length; i++)
        {
            RowFilter? filter = aggregates[i].Filter;
            if (filter is null)
            {
                FilterOf[i] = -1;
                continue;
            }

            int found = filters.FindIndex(known => string.Equals(known.Key, filter.Key, StringComparison.Ordinal));
            if (found < 0)
            {
                found = filters.Count;
                filters.Add(filter);
                (int, bool)[] of = new (int, bool)[filter.Conditions.Length];
                for (int k = 0; k < of.Length; k++)
                {
                    RowCondition condition = filter.Conditions[k];
                    int at = conditions.FindIndex(known => string.Equals(known.Text, condition.Text, StringComparison.Ordinal));
                    if (at < 0)
                    {
                        at = conditions.Count;
                        conditions.Add(new Condition(condition.Predicate, condition.Text, new FilterEvaluator(condition.Predicate)));
                    }

                    if (condition.Holds)
                    {
                        conditions[at].True = true;
                    }
                    else
                    {
                        conditions[at].NotTrue = true;
                    }

                    of[k] = (at, condition.Holds);
                }

                parts.Add(of);
            }

            FilterOf[i] = found;
        }

        Filters = [.. filters];
        Conditions = [.. conditions];
        Parts = [.. parts];
    }

    /// <summary>The filters, each once.</summary>
    internal RowFilter[] Filters { get; }

    /// <summary>The predicates the filters read, each once.</summary>
    internal Condition[] Conditions { get; }

    /// <summary>Per filter, its conditions: their index, and whether the rows kept are those where it is true.</summary>
    internal (int Condition, bool Holds)[][] Parts { get; }

    /// <summary>Per aggregate, its filter's index, or -1.</summary>
    internal int[] FilterOf { get; }

    /// <summary>The columns the conditions read.</summary>
    internal List<FieldExpr> Fields()
    {
        List<FieldExpr> fields = [];
        foreach (Condition condition in Conditions)
        {
            GroupPredicates.Fields(condition.Predicate, fields);
        }

        return fields;
    }

    /// <summary>A predicate the filters read, the evaluator that answers it for the whole query, and which of its answers they read.</summary>
    internal sealed class Condition(VortexExpr predicate, string text, FilterEvaluator evaluator)
    {
        internal VortexExpr Predicate => predicate;

        internal string Text => text;

        internal FilterEvaluator Evaluator => evaluator;

        /// <summary>Whether a filter keeps the rows where it is true.</summary>
        internal bool True { get; set; }

        /// <summary>Whether a filter keeps the rows where it is not true.</summary>
        internal bool NotTrue { get; set; }
    }
}
