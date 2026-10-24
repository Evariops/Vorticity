using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>
/// The predicates of a filter fenced over paths of variant columns (<see cref="VariantFilterExpr"/>), reached through its
/// conjunctions and disjunctions: the walk the structures that answer for a variant's paths share, its directory, its
/// block statistics and the keys its typed columns hold.
/// </summary>
internal static class VariantFilters
{
    /// <summary>The path a fenced predicate reads, a comparison's, a membership's, a null test's or a match's; null for another.</summary>
    internal static VariantFieldExpr? PathOf(VariantFilterExpr fence) => fence.Inner switch
    {
        ComparisonExpr { Field: VariantFieldExpr path } => path,
        InExpr { Field: VariantFieldExpr path } => path,
        NullCheckExpr { Field: VariantFieldExpr path } => path,
        StringMatchExpr { Field: VariantFieldExpr path } => path,
        _ => null,
    };

    /// <summary>
    /// Shows <paramref name="visitor"/> each fenced predicate of <paramref name="filter"/>'s conjunctions and disjunctions that
    /// reads a path, in their order, with its path; nothing is allocated, a filter of no path's predicate leaving it unshown.
    /// </summary>
    internal static void Visit<TVisitor>(VortexExpr filter, ref TVisitor visitor)
        where TVisitor : struct, IFencedVisitor
    {
        switch (filter)
        {
            case LogicalExpr logical:
                Visit(logical.Left, ref visitor);
                Visit(logical.Right, ref visitor);
                return;
            case VariantFilterExpr fence when PathOf(fence) is { } path:
                visitor.Visit(fence.Inner, path);
                return;
        }
    }

    /// <summary>
    /// Whether a row may satisfy <paramref name="filter"/>, each fenced predicate judged by <paramref name="judge"/> under its
    /// conjunctions and disjunctions; a negation, and anything that is no fenced predicate, may.
    /// </summary>
    internal static bool MayMatch<TJudge>(VortexExpr filter, in TJudge judge)
        where TJudge : struct, IFencedJudge => filter switch
    {
        LogicalExpr { IsAnd: true } both => MayMatch(both.Left, judge) && MayMatch(both.Right, judge),
        LogicalExpr either => MayMatch(either.Left, judge) || MayMatch(either.Right, judge),
        VariantFilterExpr fence => judge.MayMatch(fence.Inner),
        _ => true,
    };
}

/// <summary>What gathers the fenced predicates of a filter, one at a time (<see cref="VariantFilters.Visit"/>).</summary>
internal interface IFencedVisitor
{
    /// <summary>Takes <paramref name="predicate"/>, the predicate behind a fence, which reads <paramref name="path"/>.</summary>
    void Visit(VortexExpr predicate, VariantFieldExpr path);
}

/// <summary>What a structure that answers for a variant's paths says of a fenced predicate: whether a row it covers may satisfy it.</summary>
internal interface IFencedJudge
{
    /// <summary>Whether a row may satisfy <paramref name="predicate"/>, the predicate behind a fence; true when the structure cannot tell.</summary>
    bool MayMatch(VortexExpr predicate);
}
