using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Dataset;

/// <summary>
/// What the answers of every object to one question share, worked out once for the question rather than once an
/// object: its filter's conjuncts and the columns each reads, its keys of time, and the filter on keys the last answer
/// built, which the next takes when the same conjuncts make it. An object's answer then builds its parts, its buckets
/// and its edges alone
/// (<see cref="SynopsisAnswer.Of(SynopsisQuestion, Indexes.SynopsisContents, Types.DType, ValueTuple{long, long}?, IReadOnlyDictionary{string, FilterLiteral}?)"/>).
/// </summary>
internal sealed class SynopsisQuestion
{
    /// <summary>The conjuncts past which an answer's filter on keys is built for each object, not kept.</summary>
    private const int KeptConjuncts = 64;

    // The filter on keys last built, and the conjuncts it joins, one bit each: published whole, a question's answers
    // being built on whichever thread reads the objects.
    private KeptFilter? _keyFilter;

    /// <summary>The question of <paramref name="request"/> under <paramref name="filter"/>, over a dataset whose instant is <paramref name="instant"/>.</summary>
    internal SynopsisQuestion(StateRequest request, VortexExpr? filter, string instant, IReadOnlyList<string>? identifiers)
    {
        Request = request;
        Filter = filter;
        Instant = instant;
        Identifiers = identifiers;
        List<VortexExpr> conjuncts = [];
        foreach (VortexExpr conjunct in SynopsisAnswer.Conjuncts(filter))
        {
            conjuncts.Add(conjunct);
        }

        Conjuncts = [.. conjuncts];
        Columns = new string[Conjuncts.Length][];
        ReadsPaths = new bool[Conjuncts.Length];
        for (int c = 0; c < Conjuncts.Length; c++)
        {
            HashSet<string> columns = new HashSet<string>(StringComparer.Ordinal);
            Conjuncts[c].CollectFields(columns);
            Columns[c] = [.. columns];
            ReadsPaths[c] = HoldsPath(Conjuncts[c]);
        }

        List<FunctionFieldExpr> times = [];
        for (int k = 0; k < request.Keys.Count; k++)
        {
            FieldExpr column = request.Keys[k];
            while (column is FunctionFieldExpr function)
            {
                column = function.Column;
            }

            if (request.Keys[k] is FunctionFieldExpr time && column.GetType() == typeof(FieldExpr) && string.Equals(column.Path, instant, StringComparison.Ordinal))
            {
                times.Add(time);
            }
        }

        Times = [.. times];
    }

    /// <summary>What the aggregation asks.</summary>
    internal StateRequest Request { get; }

    /// <summary>The scan's filter, null for every row.</summary>
    internal VortexExpr? Filter { get; }

    /// <summary>The dataset's instant, the first column of its clustering key.</summary>
    internal string Instant { get; }

    /// <summary>The dataset's identifiers, whose values a filter may leave out.</summary>
    internal IReadOnlyList<string>? Identifiers { get; }

    /// <summary>The filter's conjuncts: its operands under its conjunctions.</summary>
    internal VortexExpr[] Conjuncts { get; }

    /// <summary>The columns each conjunct reads.</summary>
    internal string[][] Columns { get; }

    /// <summary>Whether each conjunct reads a path of a variant behind a path's fence, which each object's paths name.</summary>
    internal bool[] ReadsPaths { get; }

    /// <summary>The keys that are functions of the instant, its keys of time.</summary>
    internal FunctionFieldExpr[] Times { get; }

    /// <summary>
    /// The filter on keys that joins <paramref name="conjuncts"/>, which <paramref name="joined"/> names a bit each: the
    /// last one built when made of the same, so that every object of a question takes one.
    /// </summary>
    internal VortexExpr KeyFilter(ulong joined, ReadOnlySpan<int> conjuncts)
    {
        if (Conjuncts.Length <= KeptConjuncts && System.Threading.Volatile.Read(ref _keyFilter) is { } kept && kept.Conjuncts == joined)
        {
            return kept.Filter;
        }

        VortexExpr built = Conjuncts[conjuncts[0]];
        for (int i = 1; i < conjuncts.Length; i++)
        {
            built = Expr.And(built, Conjuncts[conjuncts[i]]);
        }

        if (Conjuncts.Length <= KeptConjuncts)
        {
            System.Threading.Volatile.Write(ref _keyFilter, new KeptFilter(joined, built));
        }

        return built;
    }

    /// <summary>A filter on keys built, and the conjuncts it joins.</summary>
    private sealed record KeptFilter(ulong Conjuncts, VortexExpr Filter);

    /// <summary>Whether <paramref name="predicate"/> reads a path of a variant behind a path's fence, through <c>and</c>, <c>or</c> and <c>not</c>.</summary>
    private static bool HoldsPath(VortexExpr predicate) => predicate switch
    {
        LogicalExpr logical => HoldsPath(logical.Left) || HoldsPath(logical.Right),
        NotExpr not => HoldsPath(not.Operand),
        VariantFilterExpr => true,
        _ => false,
    };
}
