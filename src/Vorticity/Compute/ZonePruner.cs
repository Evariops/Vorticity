using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// Zone-map pruning for one scan's filter. The rule nothing here may break is that pruning never
/// eliminates a row full materialization would have returned; pruning too little only costs time,
/// so anything unknown, absent, unresolvable or merely awkward answers "may match" and only a
/// positive proof of impossibility prunes, which is why missing bounds, unknown aggregates and
/// unsupported operators all take the same branch.
/// </summary>
/// <remarks>
/// The question is asked of a row range rather than of a single zone, since a split rarely lines
/// up with a zone: the range may match when any zone overlapping it may match, and a conjunction
/// may match the range when both sides may match it somewhere in it. That is weaker than the truth
/// — the two sides could match on different rows — and safe for exactly that reason; reading it the
/// other way round would need per-row evidence a zone map does not carry. The dual,
/// <see cref="MustMatch(RowRange)"/> and <c>TryCount</c>, errs the other way and proves only what
/// the statistics settle, in three-valued logic with counts.
/// </remarks>
internal sealed class ZonePruner : IBlockPruner
{
    /// <summary>Stack bytes a pattern's prefix and its successor each get before the heap.</summary>
    private const int Scratch = 256;

    private readonly VortexExpr _filter;
    private readonly ZoneColumn[] _columns;

    /// <summary>
    /// The ordered candidates of each <c>IN</c> the filter has been asked about, or a null entry
    /// for one whose candidates do not order. Grown by replacement and published by a volatile
    /// write, because a scan of several lanes prunes from several threads and what is published is
    /// never written again; a filter holds a handful of <c>IN</c> nodes at most, so the lookup is a
    /// walk.
    /// </summary>
    private volatile Ordered[]? _ordered;

    /// <summary>
    /// The ordered candidates of every <c>IN</c> a filter still in use holds, made once for the
    /// expression rather than once per pruner: a dataset plans one filter over each of its
    /// objects, and each would sort the same list again. They go with the expression.
    /// </summary>
    private static readonly ConditionalWeakTable<InExpr, Ordered> Sorted = new ConditionalWeakTable<InExpr, Ordered>();

    internal ZonePruner(VortexExpr filter, ZoneColumn[] columns)
    {
        _filter = filter;
        _columns = columns;
    }

    /// <summary>Whether any column carries usable statistics; a pruner with none never prunes.</summary>
    internal bool IsUseful
    {
        get
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                if (_columns[i].HasStatistics)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether <paramref name="rows"/> may contain a row the filter selects.</summary>
    /// <param name="rows">The candidate range, in file row coordinates.</param>
    /// <remarks>
    /// A negation is pushed into the comparison rather than applied to a bound: negating "this zone
    /// may hold a match" gives "this zone may hold a non-match", which is a different question and
    /// is almost always true.
    /// </remarks>
    internal bool MayMatch(RowRange rows) => MayMatch(_filter, rows, negated: false);

    /// <summary>
    /// Whether the filter selects every one of <paramref name="rows"/>, from the zone maps alone --
    /// the dual of <see cref="MayMatch(RowRange)"/>.
    /// </summary>
    /// <param name="rows">The candidate range, in file row coordinates.</param>
    /// <remarks>
    /// Strict: a null or a NaN row is not selected by a comparison, so a range that holds one is
    /// not proven whole. What the statistics prove of the rows either side of them is
    /// <see cref="TryCount"/>.
    /// </remarks>
    internal bool MustMatch(RowRange rows) =>
        rows.IsEmpty || Verdict(_filter, rows).IsAllTrue;

    /// <summary>
    /// How many rows of <paramref name="rows"/> the filter selects, when the zone maps decide
    /// it: the whole-block proof a count answers from without decoding.
    /// </summary>
    /// <param name="rows">The candidate range, in file row coordinates.</param>
    /// <param name="count">The exact count, when decided.</param>
    /// <returns>Whether the statistics decided it.</returns>
    internal bool TryCount(RowRange rows, out long count)
    {
        RangeVerdict verdict = Verdict(_filter, rows);
        count = verdict.TrueCount;
        return verdict.TrueKnown;
    }

    /// <summary>The filter's verdict over <paramref name="rows"/>, both counts.</summary>
    /// <param name="rows">The candidate range, in file row coordinates.</param>
    internal RangeVerdict Verdict(RowRange rows) => Verdict(_filter, rows);

    /// <summary>
    /// The zone map of <paramref name="path"/>, when the filter reads it and the column has a
    /// usable one -- the bounds a <c>Min</c> or <c>Max</c> answers from without decoding.
    /// </summary>
    /// <param name="path">The column, as the filter names it.</param>
    internal ZoneColumn? Column(string path)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            if (string.Equals(_columns[i].Field.Path, path, StringComparison.Ordinal))
            {
                return _columns[i].HasStatistics ? _columns[i] : null;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The mask says of every block exactly what the range question says of the rows it covers,
    /// sixty-four blocks at a time: a comparison on a numeric column whose zones are the blocks is
    /// answered from its <see cref="ZoneTable"/>, any other leaf block by block, and the filter's
    /// AND, OR and NOT combine the words as the question combines its answers. A block that is
    /// already dead is not asked again: another structure may have killed it, and the zones cannot
    /// revive it.
    /// </remarks>
    public void Refine(BlockMask live)
    {
        ReadOnlySpan<ulong> scope = live.Words;
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(scope.Length);
        try
        {
            Span<ulong> may = rented.AsSpan(0, scope.Length);
            MayWords(_filter, negated: false, live, scope, may);
            live.Keep(may);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Sets in <paramref name="words"/> the blocks of <paramref name="scope"/> where
    /// <paramref name="expr"/> may match; a bit outside the scope says nothing.
    /// </summary>
    /// <param name="expr">The predicate.</param>
    /// <param name="negated">Whether a negation above it is pushed into its comparisons.</param>
    /// <param name="live">The mask, for its blocks' rows.</param>
    /// <param name="scope">
    /// The blocks whose answer matters: live, and not already settled by the other side of an AND or
    /// an OR, which is what the range question's short circuit skips.
    /// </param>
    /// <param name="words">The answer, one bit per block.</param>
    private void MayWords(VortexExpr expr, bool negated, BlockMask live, ReadOnlySpan<ulong> scope, Span<ulong> words)
    {
        switch (expr.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)expr;
                ComparisonOp op = negated ? Negate(comparison.Op) : comparison.Op;
                bool nanMatches = negated && IsOrdering(comparison.Op);
                if (comparison.Value.Kind != FilterLiteralKind.Null &&
                    Find(comparison.Field) is { HasStatistics: true } column &&
                    column.ZoneLength == live.BlockRows && column.RowCount == live.RowCount &&
                    column.Table.TryMayMatch(op, comparison.Value, nanMatches, words))
                {
                    return;
                }

                break;
            }

            case ExprKind.Not:
                MayWords(((NotExpr)expr).Operand, !negated, live, scope, words);
                return;

            case ExprKind.Logical:
            {
                // De Morgan, as the range question applies it: under a negation an AND behaves as
                // an OR and the other way round.
                LogicalExpr logical = (LogicalExpr)expr;
                bool isAnd = logical.IsAnd != negated;
                MayWords(logical.Left, negated, live, scope, words);
                ulong[] rented = ArrayPool<ulong>.Shared.Rent(2 * words.Length);
                try
                {
                    Span<ulong> rightScope = rented.AsSpan(0, words.Length);
                    Span<ulong> right = rented.AsSpan(words.Length, words.Length);
                    ulong flip = isAnd ? 0 : ulong.MaxValue;
                    for (int i = 0; i < words.Length; i++)
                    {
                        rightScope[i] = scope[i] & (words[i] ^ flip);
                    }

                    MayWords(logical.Right, negated, live, rightScope, right);
                    for (int i = 0; i < words.Length; i++)
                    {
                        words[i] = isAnd ? words[i] & right[i] : words[i] | (right[i] & rightScope[i]);
                    }
                }
                finally
                {
                    ArrayPool<ulong>.Shared.Return(rented);
                }

                return;
            }
        }

        // Every other shape, and a comparison the table cannot answer, is asked block by block.
        for (int w = 0; w < words.Length; w++)
        {
            ulong pending = scope[w];
            ulong answer = 0;
            while (pending != 0)
            {
                int bit = BitOperations.TrailingZeroCount(pending);
                pending &= pending - 1;
                if (MayMatch(expr, live.BlockRange((w << 6) + bit), negated))
                {
                    answer |= 1UL << bit;
                }
            }

            words[w] = answer;
        }
    }

    private bool MayMatch(VortexExpr expr, RowRange rows, bool negated)
    {
        switch (expr.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)expr;
                ComparisonOp op = negated ? Negate(comparison.Op) : comparison.Op;

                // NOT (x < v) is true on a NaN row, where the pushed-down x >= v is false: under
                // a negation an ordering predicate matches wherever a NaN may be. Equality is not
                // concerned -- NOT (x = v) becomes x != v, which never prunes, and NOT (x != v)
                // is false on NaN exactly as x = v is.
                bool nanMatches = negated && IsOrdering(comparison.Op);
                return MayMatchComparison(comparison.Field, op, comparison.Value, rows, nanMatches);
            }

            case ExprKind.ColumnComparison:
            {
                // The same push-down as a comparison with a constant, NaN rule included: the
                // negation of an ordering holds on a NaN row, and so does !=.
                ColumnComparisonExpr columns = (ColumnComparisonExpr)expr;
                ComparisonOp op = negated ? Negate(columns.Op) : columns.Op;
                bool nanMatches = (negated && IsOrdering(columns.Op)) || op == ComparisonOp.NotEqual;
                return MayMatchColumns(columns, op, rows, nanMatches);
            }

            case ExprKind.NullCheck:
            {
                NullCheckExpr check = (NullCheckExpr)expr;
                return MayMatchNullCheck(check.Field, check.IsNull != negated, rows);
            }

            case ExprKind.In:
            {
                InExpr membership = (InExpr)expr;
                if (negated)
                {
                    // NOT IN prunes nothing a zone map can prove; a zone almost always holds some
                    // value outside a small candidate set.
                    return true;
                }

                if (OrderedFor(membership) is OrderedCandidates candidates)
                {
                    return MayMatchMembership(membership.Field, candidates, rows);
                }

                FilterLiteral[] literals = membership.Literals;
                for (int i = 0; i < literals.Length; i++)
                {
                    if (MayMatchComparison(membership.Field, ComparisonOp.Equal, literals[i], rows))
                    {
                        return true;
                    }
                }

                return false;
            }

            case ExprKind.StringMatch:
                return MayMatchStringMatch((StringMatchExpr)expr, rows, negated);

            case ExprKind.ListContains:
                return MayMatchListContains((ListContainsExpr)expr, rows);

            case ExprKind.Not:
                return MayMatch(((NotExpr)expr).Operand, rows, !negated);

            case ExprKind.Logical:
            {
                LogicalExpr logical = (LogicalExpr)expr;

                // De Morgan: under a negation an AND behaves as an OR and vice versa.
                bool isAnd = logical.IsAnd != negated;
                bool left = MayMatch(logical.Left, rows, negated);
                if (isAnd)
                {
                    return left && MayMatch(logical.Right, rows, negated);
                }

                return left || MayMatch(logical.Right, rows, negated);
            }

            default:
                return true;
        }
    }

    /// <summary>
    /// <c>list_contains</c>, pruned by the list's null count alone: a null list is unknown, and so
    /// is its negation, so a zone of nothing but null lists holds no row either way.
    /// </summary>
    /// <remarks>
    /// A list column's zone map summarizes the list values themselves and not their elements, so
    /// its bounds say nothing about what a list contains and are never read here; the elements are
    /// the Bloom filter's business. A null literal is left alone, as a comparison's is: pruning on
    /// it would rest a correctness claim on a constant.
    /// </remarks>
    private bool MayMatchListContains(ListContainsExpr contains, RowRange rows)
    {
        if (contains.Value.Kind == FilterLiteralKind.Null)
        {
            return true;
        }

        ZoneColumn? column = Find(contains.Field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);
            if (!bounds.HasNullCount || bounds.NullCount < column.RowsInZone(zone))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A byte-pattern predicate, pruned through whichever of the three shapes is a range.
    /// </summary>
    /// <remarks>
    /// <c>StartsWith(p)</c> is exactly <c>x ≥ p</c> together with <c>x &lt; succ(p)</c> over the
    /// bytewise order a zone map's string bounds already use, so it prunes with no machinery of
    /// its own, and a <c>Like</c> pattern that does not open with a wildcard claims that same
    /// prefix. <c>Contains</c> claims nothing, having no range to stand on.
    /// <para>
    /// A negation claims nothing either, which is the answer a negated comparison gets too: "this
    /// zone may hold a value that does not begin with p" is almost always true and is not the
    /// question the bounds answer.
    /// </para>
    /// </remarks>
    private bool MayMatchStringMatch(StringMatchExpr match, RowRange rows, bool negated)
    {
        if (negated || match.Op == StringMatchOp.Contains)
        {
            return true;
        }

        ReadOnlySpan<byte> pattern = match.Pattern.BytesValue;
        if (match.Op != StringMatchOp.Like)
        {
            return pattern.IsEmpty || PrefixMayMatch(match.Field, pattern, rows);
        }

        // A pattern claims only what it says before its first wildcard, and the escapes have to come
        // out of it first, so it needs a buffer of its own. The prefix never leaves this frame: it
        // goes straight into the comparison below.
        Span<byte> literal = pattern.Length <= Scratch
            ? stackalloc byte[Scratch]
            : new byte[pattern.Length];
        int taken = BytePattern.LeadingLiteral(pattern, match.Escape, literal);
        return taken == 0 || PrefixMayMatch(match.Field, literal[..taken], rows);
    }

    /// <summary>
    /// Whether any zone of <paramref name="rows"/> may hold a value beginning with
    /// <paramref name="prefix"/>, through the range that prefix is.
    /// </summary>
    private bool PrefixMayMatch(FieldExpr field, ReadOnlySpan<byte> prefix, RowRange rows)
    {
        // A decimal's bounds are numbers, and a byte prefix is no range over them.
        if (Find(field) is { IsDecimal: true })
        {
            return true;
        }

        if (!MayMatchComparison(
                field, ComparisonOp.GreaterOrEqual, FilterLiteral.From(prefix), rows))
        {
            return false;
        }

        Span<byte> upper = prefix.Length <= Scratch ? stackalloc byte[Scratch] : new byte[prefix.Length];
        int length = BytePattern.Successor(prefix, upper);

        // An all-0xFF prefix has no successor: nothing sorts above every string that begins with
        // it, so the lower bound is the whole claim.
        return length == 0
            || MayMatchComparison(field, ComparisonOp.Less, FilterLiteral.From(upper[..length]), rows);
    }

    /// <summary>
    /// Whether any zone of <paramref name="field"/> may hold one of <paramref name="candidates"/>,
    /// asked once per zone rather than once per candidate.
    /// </summary>
    /// <remarks>
    /// The same question as the loop it replaces, and the same answer: a zone may match an
    /// <c>IN</c> exactly when one candidate falls inside its bounds, which ordered candidates
    /// answer with a search rather than a walk. A zone whose bounds do not compare against them
    /// decides nothing, which is the "may match" every unresolvable case takes.
    /// </remarks>
    private bool MayMatchMembership(FieldExpr field, OrderedCandidates candidates, RowRange rows)
    {
        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);

            // An all-null zone satisfies no equality, exactly as ZoneMayMatch rules it out.
            if (bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone))
            {
                continue;
            }

            if (!candidates.TryAnswer(bounds, out bool inside, out _) || inside)
            {
                return true;
            }
        }

        return false;
    }

    private bool MayMatchComparison(
        FieldExpr field, ComparisonOp op, FilterLiteral value, RowRange rows, bool nanMatches = false)
    {
        if (value.Kind == FilterLiteralKind.Null)
        {
            // Unknown for every row, so nothing is selected anywhere - but proving that is the
            // evaluator's job, and pruning on it would be a correctness claim resting on a
            // constant. Left alone.
            return true;
        }

        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            if (ZoneMayMatch(column, zone, op, value) || (nanMatches && ZoneMayHoldNaN(column, zone)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether zone <paramref name="zone"/> may hold a NaN: its <c>nan_count</c> when it has one,
    /// else whatever its bounds' type allows -- and a zone with no bounds allows everything.
    /// </summary>
    private static bool ZoneMayHoldNaN(ZoneColumn column, int zone)
    {
        ZoneBounds bounds = column.Bounds(zone);
        if (bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone))
        {
            // No values at all, NaN included.
            return false;
        }

        if (bounds.HasNanCount)
        {
            return bounds.NanCount > 0;
        }

        return BoundsKind(bounds) is not FilterLiteralKind kind || kind == FilterLiteralKind.Float;
    }

    /// <summary>The type the bounds are in, or null when the zone has none.</summary>
    private static FilterLiteralKind? BoundsKind(ZoneBounds bounds) =>
        bounds.HasMin ? bounds.Min.Kind : bounds.HasMax ? bounds.Max.Kind : null;

    /// <summary>
    /// How many of the zone's rows are NaN: <c>nan_count</c> when the zone has one, none when
    /// its bounds are of a type that has none, undecided otherwise.
    /// </summary>
    private static long? NaNs(ZoneBounds bounds, long rows)
    {
        if (bounds.HasNanCount)
        {
            return Math.Min(bounds.NanCount, rows);
        }

        return BoundsKind(bounds) is FilterLiteralKind kind && kind != FilterLiteralKind.Float ? 0 : null;
    }

    private static bool IsOrdering(ComparisonOp op) =>
        op is ComparisonOp.Less or ComparisonOp.LessOrEqual
            or ComparisonOp.Greater or ComparisonOp.GreaterOrEqual;

    // ------------------------------------------------------------------------------- the dual

    /// <summary>
    /// The filter's verdict over <paramref name="rows"/>: how many rows it selects and how many
    /// it leaves unknown, in three-valued logic, from the zone maps alone.
    /// </summary>
    /// <remarks>
    /// The leaves are decided per zone from the bounds and the counts, and NOT, AND and OR are
    /// the exact operations of <see cref="RangeVerdict"/> -- no push-down, because a count of
    /// trues negates exactly only with the unknowns beside it. A leaf the maps say nothing about
    /// is <see cref="RangeVerdict.Undecided"/>, and the algebra lets it decide what it still can:
    /// <c>false AND undecided</c> is false.
    /// </remarks>
    private RangeVerdict Verdict(VortexExpr expr, RowRange rows)
    {
        long length = rows.Length;
        switch (expr.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)expr;
                if (comparison.Value.Kind == FilterLiteralKind.Null)
                {
                    // "A comparison with a null operand yields unknown" -- for every row, which
                    // is what ComparisonKernels.Compare fills.
                    return RangeVerdict.AllUnknown(length);
                }

                return Decide(comparison.Field, rows, ZoneQuestion.Compare(comparison.Op, comparison.Value));
            }

            case ExprKind.ColumnComparison:
                return ColumnsVerdict((ColumnComparisonExpr)expr, rows);

            case ExprKind.NullCheck:
            {
                NullCheckExpr check = (NullCheckExpr)expr;
                return Decide(check.Field, rows, ZoneQuestion.NullCheck(check.IsNull));
            }

            case ExprKind.In:
            {
                InExpr membership = (InExpr)expr;
                return Decide(
                    membership.Field, rows, ZoneQuestion.In(membership.Literals, OrderedFor(membership)));
            }

            case ExprKind.StringMatch:
                return StringMatchVerdict((StringMatchExpr)expr, rows);

            case ExprKind.ListContains:
            {
                // The nulls are the unknown rows, and nothing a zone holds decides the others.
                ListContainsExpr contains = (ListContainsExpr)expr;
                return contains.Value.Kind == FilterLiteralKind.Null
                    ? RangeVerdict.AllUnknown(length)
                    : Decide(contains.Field, rows, ZoneQuestion.Open);
            }

            case ExprKind.Not:
                return RangeVerdict.Not(Verdict(((NotExpr)expr).Operand, rows));

            case ExprKind.Logical:
            {
                LogicalExpr logical = (LogicalExpr)expr;
                RangeVerdict left = Verdict(logical.Left, rows);
                RangeVerdict right = Verdict(logical.Right, rows);
                return logical.IsAnd
                    ? RangeVerdict.And(left, right)
                    : RangeVerdict.Or(left, right);
            }

            default:
                return RangeVerdict.Undecided(length);
        }
    }

    /// <summary>
    /// A byte-pattern predicate, decided through the range a prefix is, exactly as
    /// <see cref="MayMatchStringMatch"/> prunes it: <c>StartsWith(p)</c> is
    /// <c>x ≥ p AND x &lt; succ(p)</c>, so its bounds prove it whole as well as impossible; a
    /// <c>Like</c> claims only impossibility through its leading literal, since what follows the
    /// prefix is not a range; <c>Contains</c> claims nothing but the empty pattern.
    /// </summary>
    private RangeVerdict StringMatchVerdict(StringMatchExpr match, RowRange rows)
    {
        ReadOnlySpan<byte> pattern = match.Pattern.BytesValue;
        if (match.Op == StringMatchOp.Contains)
        {
            return Decide(match.Field, rows, ZoneQuestion.Contains(pattern.IsEmpty));
        }

        if (match.Op == StringMatchOp.StartsWith)
        {
            return Decide(match.Field, rows, Prefix(pattern, whole: true));
        }

        Span<byte> literal = pattern.Length <= Scratch
            ? stackalloc byte[Scratch]
            : new byte[pattern.Length];
        int taken = BytePattern.LeadingLiteral(pattern, match.Escape, literal);
        if (taken == 0)
        {
            // A pattern that opens with a wildcard says nothing a bound can check; the nulls are
            // still the unknown rows.
            return Decide(match.Field, rows, ZoneQuestion.Open);
        }

        return Decide(match.Field, rows, Prefix(literal[..taken], whole: false));
    }

    /// <summary>The prefix question: the range <c>[p, succ(p))</c>, both ends as literals.</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="whole">Whether the predicate is the prefix test itself, so the range proves it whole.</param>
    private static ZoneQuestion Prefix(ReadOnlySpan<byte> prefix, bool whole)
    {
        if (prefix.IsEmpty)
        {
            // Every string begins with the empty prefix.
            return whole ? ZoneQuestion.Everything : ZoneQuestion.Open;
        }

        Span<byte> upper = prefix.Length <= Scratch ? stackalloc byte[Scratch] : new byte[prefix.Length];
        int length = BytePattern.Successor(prefix, upper);
        return ZoneQuestion.Prefix(
            FilterLiteral.From(prefix),
            length == 0 ? default : FilterLiteral.From(upper[..length]),
            hasUpper: length > 0,
            whole);
    }

    /// <summary>
    /// Asks <paramref name="question"/> of every zone of <paramref name="field"/> that
    /// <paramref name="rows"/> touches and sums the answers.
    /// </summary>
    /// <remarks>
    /// A zone the range covers whole contributes its counts. A zone it covers in part is a
    /// count over rows the map cannot tell apart, so it contributes only what is uniform over
    /// the zone: nothing true, everything true, nothing unknown, everything unknown. A split that
    /// straddles two blocks is the case that meets this; a block that is itself one zone never
    /// does.
    /// </remarks>
    private RangeVerdict Decide(FieldExpr field, RowRange rows, in ZoneQuestion question)
    {
        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics || rows.IsEmpty)
        {
            return RangeVerdict.Undecided(rows.Length);
        }

        ZoneRange zones = column.Zones(rows);
        long covered = Math.Min((long)zones.End * column.ZoneLength, column.RowCount);
        if (zones.End <= zones.Start || rows.Start < (long)zones.Start * column.ZoneLength || rows.End > covered)
        {
            // Rows the map does not describe decide nothing.
            return RangeVerdict.Undecided(rows.Length);
        }

        RangeVerdict total = RangeVerdict.Of(0, 0, 0, 0, column);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            long zoneRows = column.RowsInZone(zone);
            RowRange whole = RowRange.FromLength((long)zone * column.ZoneLength, zoneRows);
            RowRange part = whole.Intersect(rows);
            RangeVerdict answer = question.OfZone(column, column.Bounds(zone), zoneRows);
            if (part.Length < zoneRows)
            {
                answer = answer.Restrict(part.Length);
            }

            total = RangeVerdict.Concat(total, answer);
            if (!total.TrueKnown && !total.FalseKnown && !total.UnknownKnown)
            {
                return RangeVerdict.Undecided(rows.Length);
            }
        }

        return total;
    }

    /// <summary>What the bounds of one zone prove of a comparison over its values.</summary>
    private enum Proof : byte
    {
        /// <summary>The bounds decide nothing.</summary>
        Open,

        /// <summary>Every non-null, non-NaN value satisfies it.</summary>
        All,

        /// <summary>No value satisfies it.</summary>
        None,
    }

    /// <summary>
    /// What the bounds prove of <c>x op value</c>, over the values the bounds describe: the
    /// non-null, non-NaN ones. <see cref="Proof.All"/> may rest on an inexact bound, since the true
    /// minimum is at or above the stated one and <c>min ≥ v</c> therefore proves <c>x ≥ v</c>;
    /// equality is proven only from exact bounds.
    /// </summary>
    private static Proof Prove(ZoneColumn column, ZoneBounds bounds, ComparisonOp op, FilterLiteral value)
    {
        int low = 0;
        int high = 0;
        bool hasLow = bounds.HasMin && TryOrder(column, bounds.Min, value, out low);
        bool hasHigh = bounds.HasMax && TryOrder(column, bounds.Max, value, out high);
        switch (op)
        {
            case ComparisonOp.GreaterOrEqual:
                return hasLow && low >= 0 ? Proof.All : hasHigh && high < 0 ? Proof.None : Proof.Open;
            case ComparisonOp.Greater:
                return hasLow && low > 0 ? Proof.All : hasHigh && high <= 0 ? Proof.None : Proof.Open;
            case ComparisonOp.LessOrEqual:
                return hasHigh && high <= 0 ? Proof.All : hasLow && low > 0 ? Proof.None : Proof.Open;
            case ComparisonOp.Less:
                return hasHigh && high < 0 ? Proof.All : hasLow && low >= 0 ? Proof.None : Proof.Open;
            case ComparisonOp.Equal:
                if ((hasLow && low > 0) || (hasHigh && high < 0))
                {
                    return Proof.None;
                }

                return hasLow && hasHigh && low == 0 && high == 0 && bounds.IsExact ? Proof.All : Proof.Open;
            default:
                // NotEqual: the mirror of Equal, over the values the bounds describe. The NaN
                // rows, which lie outside them and do satisfy !=, are the caller's to count.
                if ((hasLow && low > 0) || (hasHigh && high < 0))
                {
                    return Proof.All;
                }

                return hasLow && hasHigh && low == 0 && high == 0 && bounds.IsExact ? Proof.None : Proof.Open;
        }
    }

    /// <summary>One predicate, asked zone by zone.</summary>
    private readonly struct ZoneQuestion
    {
        private readonly Shape _shape;
        private readonly ComparisonOp _op;
        private readonly bool _isNull;
        private readonly bool _whole;
        private readonly bool _hasUpper;
        private readonly FilterLiteral _value;
        private readonly FilterLiteral _upper;
        private readonly FilterLiteral[]? _literals;
        private readonly OrderedCandidates? _ordered;

        private ZoneQuestion(
            Shape shape, ComparisonOp op, bool isNull, bool whole, bool hasUpper,
            FilterLiteral value, FilterLiteral upper, FilterLiteral[]? literals,
            OrderedCandidates? ordered = null)
        {
            _shape = shape;
            _op = op;
            _isNull = isNull;
            _whole = whole;
            _hasUpper = hasUpper;
            _value = value;
            _upper = upper;
            _literals = literals;
            _ordered = ordered;
        }

        private enum Shape : byte
        {
            /// <summary>Nothing provable beyond the nulls being unknown.</summary>
            Open,

            /// <summary>True of every non-null row.</summary>
            Everything,

            /// <summary><c>x op v</c>.</summary>
            Comparison,

            /// <summary>A null test, either way round.</summary>
            NullCheck,

            /// <summary><c>x IN (…)</c>.</summary>
            Membership,

            /// <summary>A byte prefix: the range <c>[p, succ(p))</c>.</summary>
            Prefix,
        }

        /// <summary>A predicate the bounds cannot check, unknown on the nulls.</summary>
        internal static ZoneQuestion Open => new ZoneQuestion(Shape.Open, default, false, false, false, default, default, null);

        /// <summary>A predicate true of every value, unknown on the nulls.</summary>
        internal static ZoneQuestion Everything => new ZoneQuestion(Shape.Everything, default, false, false, false, default, default, null);

        internal static ZoneQuestion Compare(ComparisonOp op, FilterLiteral value) =>
            new ZoneQuestion(Shape.Comparison, op, false, false, false, value, default, null);

        internal static ZoneQuestion NullCheck(bool isNull) =>
            new ZoneQuestion(Shape.NullCheck, default, isNull, false, false, default, default, null);

        internal static ZoneQuestion In(FilterLiteral[] literals, OrderedCandidates? ordered) =>
            new ZoneQuestion(
                Shape.Membership, default, false, false, false, default, default, literals, ordered);

        /// <summary><c>Contains(p)</c>: everything when the pattern is empty, else unchecked.</summary>
        internal static ZoneQuestion Contains(bool empty) => empty ? Everything : Open;

        internal static ZoneQuestion Prefix(FilterLiteral lower, FilterLiteral upper, bool hasUpper, bool whole) =>
            new ZoneQuestion(Shape.Prefix, default, false, whole, hasUpper, lower, upper, null);

        /// <summary>The verdict over one whole zone of <paramref name="rows"/> rows.</summary>
        internal RangeVerdict OfZone(ZoneColumn column, ZoneBounds bounds, long rows)
        {
            long? nulls = bounds.HasNullCount ? Math.Min(bounds.NullCount, rows) : null;

            if (_shape == Shape.NullCheck)
            {
                // A null test is never unknown, and its count is the statistic itself.
                return nulls is long known
                    ? RangeVerdict.Of(rows, _isNull ? known : rows - known, _isNull ? rows - known : known, 0)
                    : RangeVerdict.Undecided(rows);
            }

            if (nulls == rows)
            {
                // No values: unknown everywhere, and those unknowns are this column's nulls.
                return RangeVerdict.AllUnknown(rows, column);
            }

            // Every remaining shape is unknown on exactly the nulls and decided elsewhere.
            switch (_shape)
            {
                case Shape.Everything:
                    return RangeVerdict.Of(rows, rows - nulls, 0, nulls, column);

                case Shape.Comparison:
                    return Comparison(column, bounds, rows, nulls);

                case Shape.Membership:
                    return Membership(column, bounds, rows, nulls);

                case Shape.Prefix:
                    return PrefixRange(column, bounds, rows, nulls);

                default:
                    return RangeVerdict.Of(rows, null, null, nulls, column);
            }
        }

        private RangeVerdict Comparison(ZoneColumn column, ZoneBounds bounds, long rows, long? nulls)
        {
            Proof proof = Prove(column, bounds, _op, _value);
            long? nans = NaNs(bounds, rows);
            if (_op == ComparisonOp.NotEqual)
            {
                // A NaN row does satisfy !=: the trues are every non-null row when the
                // bounds prove the values, and exactly the NaN rows when they prove the values
                // all equal -- the values themselves are then the falses.
                return proof switch
                {
                    Proof.All => RangeVerdict.Of(rows, rows - nulls, 0, nulls, column),
                    Proof.None => RangeVerdict.Of(rows, nans, rows - nulls - nans, nulls, column),
                    _ => RangeVerdict.Of(rows, null, null, nulls, column),
                };
            }

            // An ordering predicate or an equality: false on a NaN row, so the bounds
            // proving every value leave exactly the NaN rows false, and proving none leaves no
            // true row at all.
            return proof switch
            {
                Proof.All => RangeVerdict.Of(rows, rows - nulls - nans, nans, nulls, column),
                Proof.None => RangeVerdict.Of(rows, 0, rows - nulls, nulls, column),
                _ => RangeVerdict.Of(rows, null, null, nulls, column),
            };
        }

        /// <summary>
        /// <c>x IN (…)</c> as the OR of equalities it is (ComparisonKernels.In): true wherever
        /// one candidate is proven for every value -- two cannot be, unless they are the same
        /// candidate -- and false only when every candidate is proven impossible. A null
        /// candidate is unknown on every row, so the OR is unknown wherever it is not true.
        /// </summary>
        private RangeVerdict Membership(ZoneColumn column, ZoneBounds bounds, long rows, long? nulls)
        {
            bool anyNull = false;
            bool anyAll;
            bool allNone;

            // Ordered candidates hold no null, so the OR cannot be unknown on that account.
            if (_ordered is OrderedCandidates ordered &&
                ordered.TryAnswer(bounds, out bool inside, out bool whole))
            {
                anyAll = whole;
                allNone = !inside;
            }
            else
            {
                FilterLiteral[] literals = _literals!;
                anyAll = false;
                allNone = true;
                for (int i = 0; i < literals.Length; i++)
                {
                    if (literals[i].Kind == FilterLiteralKind.Null)
                    {
                        anyNull = true;
                        continue;
                    }

                    Proof proof = Prove(column, bounds, ComparisonOp.Equal, literals[i]);
                    anyAll |= proof == Proof.All;
                    allNone &= proof == Proof.None;
                }
            }

            long? nans = NaNs(bounds, rows);
            long? trueCount = anyAll ? rows - nulls - nans : allNone ? 0 : null;
            if (anyNull)
            {
                // Every row that is not true is unknown, the nulls of the column among them.
                return RangeVerdict.Of(rows, trueCount, 0, rows - trueCount);
            }

            long? falseCount = anyAll ? nans : allNone ? rows - nulls : null;
            return RangeVerdict.Of(rows, trueCount, falseCount, nulls, column);
        }

        /// <summary>
        /// The range <c>[p, succ(p))</c> against string bounds: whole when the bounds sit inside
        /// it (and the predicate is the prefix test itself), impossible when they sit entirely
        /// outside it. An all-0xFF prefix has no successor and its lower bound is the whole claim.
        /// </summary>
        private RangeVerdict PrefixRange(ZoneColumn column, ZoneBounds bounds, long rows, long? nulls)
        {
            if (column.IsDecimal || BoundsKind(bounds) != FilterLiteralKind.Bytes)
            {
                return RangeVerdict.Of(rows, null, null, nulls, column);
            }

            int low = 0;
            int high = 0;
            bool hasLow = bounds.HasMin && TryCompare(bounds.Min, _value, out low);
            bool hasHigh = bounds.HasMax && TryCompare(bounds.Max, _value, out high);
            bool none = hasHigh && high < 0;
            bool all = hasLow && low >= 0;
            if (_hasUpper)
            {
                int minVsUpper = 0;
                int maxVsUpper = 0;
                bool lowAboveUpper = bounds.HasMin && TryCompare(bounds.Min, _upper, out minVsUpper) && minVsUpper >= 0;
                bool highBelowUpper = bounds.HasMax && TryCompare(bounds.Max, _upper, out maxVsUpper) && maxVsUpper < 0;
                none |= lowAboveUpper;
                all &= highBelowUpper;
            }

            if (none)
            {
                return RangeVerdict.Of(rows, 0, rows - nulls, nulls, column);
            }

            if (all && _whole)
            {
                return RangeVerdict.Of(rows, rows - nulls, 0, nulls, column);
            }

            return RangeVerdict.Of(rows, null, null, nulls, column);
        }
    }

    private bool MayMatchNullCheck(FieldExpr field, bool isNull, RowRange rows)
    {
        ZoneColumn? column = Find(field);
        if (column is null || !column.HasStatistics)
        {
            return true;
        }

        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);
            if (!bounds.HasNullCount)
            {
                return true;
            }

            long zoneRows = column.RowsInZone(zone);
            bool possible = isNull ? bounds.NullCount > 0 : bounds.NullCount < zoneRows;
            if (possible)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ZoneMayMatch(ZoneColumn column, int zone, ComparisonOp op, FilterLiteral value)
    {
        ZoneBounds bounds = column.Bounds(zone);

        // A zone whose null count fills it can be skipped for any predicate a null cannot satisfy,
        // and a comparison is never satisfied by a null.
        if (bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone))
        {
            return false;
        }

        switch (op)
        {
            case ComparisonOp.Greater:
            case ComparisonOp.GreaterOrEqual:
                // x > k is impossible when every value is <= max < k. A bound that cannot be
                // ordered against k settles nothing, and reading the 0 that says so as "equal"
                // would rule the zone out -- every zone, for a constant of another kind, which is
                // an empty answer where an error is owed.
                return !bounds.HasMax || !TryOrder(column, bounds.Max, value, out int over) ||
                       Satisfiable(op, over, upper: true);

            case ComparisonOp.Less:
            case ComparisonOp.LessOrEqual:
                return !bounds.HasMin || !TryOrder(column, bounds.Min, value, out int under) ||
                       Satisfiable(op, under, upper: false);

            case ComparisonOp.Equal:
                // k has to sit inside [min, max]. Inexact bounds only widen that interval, so the
                // containment test stays sound; what they forbid is the opposite shortcut,
                // "min == max means the zone is constant", which is not used here.
                if (bounds.HasMin && TryOrder(column, bounds.Min, value, out int low) && low > 0)
                {
                    return false;
                }

                if (bounds.HasMax && TryOrder(column, bounds.Max, value, out int high) && high < 0)
                {
                    return false;
                }

                return true;

            default:
                // NotEqual: a zone can only be ruled out when every value equals k, which needs
                // min == max AND both exact. Rare enough that the check earns nothing; kept as
                // "may match".
                return true;
        }
    }

    /// <summary>
    /// Whether the predicate can hold somewhere in a zone whose relevant bound compares
    /// <paramref name="order"/> against the constant.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="order">The sign of <c>bound - constant</c>.</param>
    /// <param name="upper">Whether the bound is the zone's maximum.</param>
    private static bool Satisfiable(ComparisonOp op, int order, bool upper) => op switch
    {
        // Some value may exceed k only if the largest one does.
        ComparisonOp.Greater => upper && order > 0,
        ComparisonOp.GreaterOrEqual => upper && order >= 0,

        // Some value may fall below k only if the smallest one does.
        ComparisonOp.Less => !upper && order < 0,
        ComparisonOp.LessOrEqual => !upper && order <= 0,
        _ => true,
    };

    /// <summary>
    /// Orders a bound of <paramref name="column"/> against a constant the way the kernels order
    /// that column's values: numerically for a decimal, whose bounds and constants are unscaled
    /// integers at one scale, and by <see cref="TryCompare"/> otherwise.
    /// </summary>
    /// <returns>Whether the two are comparable; a caller that cannot order them claims nothing.</returns>
    private static bool TryOrder(ZoneColumn column, FilterLiteral bound, FilterLiteral value, out int order)
    {
        if (!column.IsDecimal)
        {
            return TryCompare(bound, value, out order);
        }

        if (ComparisonKernels.TryDecimal(bound, out Int256 left) &&
            ComparisonKernels.TryDecimal(value, out Int256 right))
        {
            order = left.CompareTo(right);
            return true;
        }

        order = 0;
        return false;
    }

    /// <summary>
    /// Orders a bound against a constant the way the comparison kernels order a value against
    /// it, and says when it cannot -- which a proof has to know, where a prune only has to fall
    /// back to "maybe".
    /// </summary>
    /// <param name="bound">The bound.</param>
    /// <param name="value">The constant.</param>
    /// <param name="order">The sign of <c>bound - value</c>, when the two are comparable.</param>
    /// <returns>Whether they are.</returns>
    /// <remarks>
    /// An integer against a float goes through <c>double</c> exactly as
    /// <c>ComparisonKernels.CompareSignedAgainstFloat</c> takes each value there, and that
    /// widening is monotone, so what holds of the widened bound holds of every widened value on
    /// its side of it -- lossy above 2^53 in the same way for the bound and for the rows. A NaN on
    /// either side is not comparable: a zone's bounds exclude NaN rows, and a NaN constant compares
    /// false against everything.
    /// </remarks>
    internal static bool TryCompare(FilterLiteral bound, FilterLiteral value, out int order)
    {
        order = 0;
        if (bound.Kind == value.Kind)
        {
            switch (bound.Kind)
            {
                case FilterLiteralKind.Bool:
                    order = bound.BoolValue.CompareTo(value.BoolValue);
                    return true;
                case FilterLiteralKind.Signed:
                    order = bound.SignedValue.CompareTo(value.SignedValue);
                    return true;
                case FilterLiteralKind.Unsigned:
                    order = bound.UnsignedValue.CompareTo(value.UnsignedValue);
                    return true;
                case FilterLiteralKind.Float:
                    return TryCompareFloat(bound.FloatValue, value.FloatValue, out order);
                case FilterLiteralKind.Bytes:
                    order = Math.Sign(bound.BytesValue.SequenceCompareTo(value.BytesValue));
                    return true;
                default:
                    return false;
            }
        }

        // Mixed integer signs, as ComparisonKernels.CompareSigned and CompareUnsigned settle
        // them: a negative constant is below every unsigned value, a constant above i64::Max
        // above every signed one.
        if (bound.Kind == FilterLiteralKind.Signed && value.Kind == FilterLiteralKind.Unsigned)
        {
            order = bound.SignedValue < 0
                ? -1
                : ((ulong)bound.SignedValue).CompareTo(value.UnsignedValue);
            return true;
        }

        if (bound.Kind == FilterLiteralKind.Unsigned && value.Kind == FilterLiteralKind.Signed)
        {
            order = value.SignedValue < 0
                ? 1
                : bound.UnsignedValue.CompareTo((ulong)value.SignedValue);
            return true;
        }

        if (bound.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned &&
            value.Kind == FilterLiteralKind.Float)
        {
            double left = bound.Kind == FilterLiteralKind.Signed
                ? bound.SignedValue
                : bound.UnsignedValue;
            return TryCompareFloat(left, value.FloatValue, out order);
        }

        if (bound.Kind == FilterLiteralKind.Float &&
            value.Kind is FilterLiteralKind.Signed or FilterLiteralKind.Unsigned)
        {
            // A float column against an integer constant: the constant widened, as
            // ComparisonKernels.ToDouble widens it for every row.
            double right = value.Kind == FilterLiteralKind.Signed
                ? value.SignedValue
                : value.UnsignedValue;
            return TryCompareFloat(bound.FloatValue, right, out order);
        }

        return false;
    }

    private static bool TryCompareFloat(double bound, double value, out int order)
    {
        if (double.IsNaN(bound) || double.IsNaN(value))
        {
            order = 0;
            return false;
        }

        order = bound.CompareTo(value);
        return true;
    }

    // ------------------------------------------------------------------------- two columns

    /// <summary>
    /// Whether a row of <paramref name="rows"/> may satisfy <c>left op right</c>, from the bounds
    /// of the two columns.
    /// </summary>
    /// <param name="columns">The comparison.</param>
    /// <param name="op">Its operator, negation already pushed in.</param>
    /// <param name="rows">The candidate range.</param>
    /// <param name="nanMatches">Whether a NaN on either side satisfies the pushed-down predicate.</param>
    /// <remarks>
    /// A null on either side is unknown, so a range where one column holds nothing but nulls holds
    /// no match, and the two intervals settle the rest: <c>a &gt; b</c> is impossible where
    /// <c>max(a) &lt;= min(b)</c>. Two maps cut into the same zones are asked zone by zone, which is
    /// what a block is; two maps cut differently are asked once over the range, each side's bounds
    /// widened over every zone it touches.
    /// </remarks>
    private bool MayMatchColumns(ColumnComparisonExpr columns, ComparisonOp op, RowRange rows, bool nanMatches)
    {
        ZoneColumn? left = Usable(columns.Left);
        ZoneColumn? right = Usable(columns.Right);
        if (HoldsNoValue(left, rows) || HoldsNoValue(right, rows))
        {
            return false;
        }

        if (left is null || right is null || !Covers(left, rows) || !Covers(right, rows))
        {
            return true;
        }

        if (Aligned(left, right))
        {
            ZoneRange zones = left.Zones(rows);
            for (int zone = zones.Start; zone < zones.End; zone++)
            {
                ZoneBounds a = left.Bounds(zone);
                ZoneBounds b = right.Bounds(zone);
                if (AllNull(left, a, zone) || AllNull(right, b, zone))
                {
                    continue;
                }

                if (ProvePair(left, a, op, right, b) != Proof.None ||
                    (nanMatches && (ZoneMayHoldNaN(left, zone) || ZoneMayHoldNaN(right, zone))))
                {
                    return true;
                }
            }

            return false;
        }

        ZoneBounds widenedLeft = Widen(left, rows, out bool leftNaN);
        ZoneBounds widenedRight = Widen(right, rows, out bool rightNaN);
        return ProvePair(left, widenedLeft, op, right, widenedRight) != Proof.None ||
               (nanMatches && (leftNaN || rightNaN));
    }

    /// <summary>
    /// The verdict of <c>left op right</c> over <paramref name="rows"/>, in three-valued logic with
    /// counts.
    /// </summary>
    /// <remarks>
    /// The unknown rows are the union of the two columns' nulls, and the counts give that union
    /// only when one side has none, so the other counts are decided per zone as far as that goes:
    /// nothing true where the bounds rule the comparison out, everything decided true where they
    /// prove it and one side holds no null. Maps cut differently prove only that nothing is true.
    /// </remarks>
    private RangeVerdict ColumnsVerdict(ColumnComparisonExpr columns, RowRange rows)
    {
        ZoneColumn? left = Usable(columns.Left);
        ZoneColumn? right = Usable(columns.Right);

        // A column null on every row of the range makes every row unknown, whatever the other holds.
        RangeVerdict leftNulls = Decide(columns.Left, rows, ZoneQuestion.Open);
        if (leftNulls.IsAllUnknown)
        {
            return leftNulls;
        }

        RangeVerdict rightNulls = Decide(columns.Right, rows, ZoneQuestion.Open);
        if (rightNulls.IsAllUnknown)
        {
            return rightNulls;
        }

        if (left is null || right is null || rows.IsEmpty)
        {
            return RangeVerdict.Undecided(rows.Length);
        }

        if (Aligned(left, right))
        {
            return DecideColumns(left, columns.Op, right, rows);
        }

        if (!Covers(left, rows) || !Covers(right, rows) || columns.Op == ComparisonOp.NotEqual)
        {
            return RangeVerdict.Undecided(rows.Length);
        }

        ZoneBounds widenedLeft = Widen(left, rows, out _);
        ZoneBounds widenedRight = Widen(right, rows, out _);
        return ProvePair(left, widenedLeft, columns.Op, right, widenedRight) == Proof.None
            ? RangeVerdict.Of(rows.Length, 0, null, null)
            : RangeVerdict.Undecided(rows.Length);
    }

    /// <summary>
    /// <see cref="Decide"/> for two maps cut into the same zones: each zone pair answered, a zone
    /// the range covers in part keeping only what is uniform over it, the answers summed.
    /// </summary>
    private static RangeVerdict DecideColumns(ZoneColumn left, ComparisonOp op, ZoneColumn right, RowRange rows)
    {
        if (!Covers(left, rows))
        {
            return RangeVerdict.Undecided(rows.Length);
        }

        ZoneRange zones = left.Zones(rows);
        RangeVerdict total = default;
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            long zoneRows = left.RowsInZone(zone);
            RowRange whole = RowRange.FromLength((long)zone * left.ZoneLength, zoneRows);
            RowRange part = whole.Intersect(rows);
            RangeVerdict answer = PairOfZone(left, left.Bounds(zone), op, right, right.Bounds(zone), zoneRows);
            if (part.Length < zoneRows)
            {
                answer = answer.Restrict(part.Length);
            }

            total = zone == zones.Start ? answer : RangeVerdict.Concat(total, answer);
            if (!total.TrueKnown && !total.FalseKnown && !total.UnknownKnown)
            {
                return RangeVerdict.Undecided(rows.Length);
            }
        }

        return total;
    }

    /// <summary>What one pair of zones, of <paramref name="rows"/> rows each, proves of <c>a op b</c>.</summary>
    private static RangeVerdict PairOfZone(
        ZoneColumn left, ZoneBounds a, ComparisonOp op, ZoneColumn right, ZoneBounds b, long rows)
    {
        long? leftNulls = a.HasNullCount ? Math.Min(a.NullCount, rows) : null;
        long? rightNulls = b.HasNullCount ? Math.Min(b.NullCount, rows) : null;
        if (leftNulls == rows)
        {
            return RangeVerdict.AllUnknown(rows, left);
        }

        if (rightNulls == rows)
        {
            return RangeVerdict.AllUnknown(rows, right);
        }

        long? unknown = null;
        ZoneColumn? nullsOf = null;
        if (leftNulls == 0 && rightNulls is long onlyRight)
        {
            unknown = onlyRight;
            nullsOf = right;
        }
        else if (rightNulls == 0 && leftNulls is long onlyLeft)
        {
            unknown = onlyLeft;
            nullsOf = left;
        }

        // A NaN row is false for every operator but !=, where it is true, and the bounds say
        // nothing of it: a proof over the values holds of the whole zone only where the zone
        // holds no NaN, or where the NaN rows fall on the proof's side.
        Proof proof = ProvePair(left, a, op, right, b);
        bool noNaN = NaNs(a, rows) == 0 && NaNs(b, rows) == 0;
        bool notEqual = op == ComparisonOp.NotEqual;
        if (proof == Proof.None && (!notEqual || noNaN))
        {
            return RangeVerdict.Of(rows, 0, rows - unknown, unknown, nullsOf);
        }

        if (proof == Proof.All && (notEqual || noNaN))
        {
            return RangeVerdict.Of(rows, rows - unknown, 0, unknown, nullsOf);
        }

        return RangeVerdict.Of(rows, null, null, unknown, nullsOf);
    }

    /// <summary>
    /// What the bounds of two zones prove of <c>a op b</c> over their non-null, non-NaN values.
    /// </summary>
    /// <remarks>
    /// Every ordering is settled by <c>min(a)</c> against <c>max(b)</c> and <c>max(a)</c> against
    /// <c>min(b)</c>, and an inexact bound only widens its interval, so these proofs hold on it
    /// too. An equality proven whole, or an inequality proven impossible, needs both zones to hold
    /// one and the same value, which only exact bounds can say.
    /// </remarks>
    private static Proof ProvePair(ZoneColumn left, ZoneBounds a, ComparisonOp op, ZoneColumn right, ZoneBounds b)
    {
        if (left.IsDecimal != right.IsDecimal)
        {
            return Proof.Open;
        }

        int minMax = 0;
        int maxMin = 0;
        bool hasMinMax = a.HasMin && b.HasMax && TryOrder(left, a.Min, b.Max, out minMax);
        bool hasMaxMin = a.HasMax && b.HasMin && TryOrder(left, a.Max, b.Min, out maxMin);
        bool disjoint = (hasMaxMin && maxMin < 0) || (hasMinMax && minMax > 0);
        switch (op)
        {
            case ComparisonOp.Greater:
                return hasMinMax && minMax > 0 ? Proof.All : hasMaxMin && maxMin <= 0 ? Proof.None : Proof.Open;
            case ComparisonOp.GreaterOrEqual:
                return hasMinMax && minMax >= 0 ? Proof.All : hasMaxMin && maxMin < 0 ? Proof.None : Proof.Open;
            case ComparisonOp.Less:
                return hasMaxMin && maxMin < 0 ? Proof.All : hasMinMax && minMax >= 0 ? Proof.None : Proof.Open;
            case ComparisonOp.LessOrEqual:
                return hasMaxMin && maxMin <= 0 ? Proof.All : hasMinMax && minMax > 0 ? Proof.None : Proof.Open;
            case ComparisonOp.Equal:
                return disjoint ? Proof.None : OneValue(left, a, right, b) ? Proof.All : Proof.Open;
            default:
                return disjoint ? Proof.All : OneValue(left, a, right, b) ? Proof.None : Proof.Open;
        }
    }

    /// <summary>Whether both zones hold one value, the same, by exact bounds.</summary>
    private static bool OneValue(ZoneColumn left, ZoneBounds a, ZoneColumn right, ZoneBounds b) =>
        a.IsExact && b.IsExact && a.HasMin && a.HasMax && b.HasMin && b.HasMax &&
        TryOrder(left, a.Min, a.Max, out int ownLeft) && ownLeft == 0 &&
        TryOrder(right, b.Min, b.Max, out int ownRight) && ownRight == 0 &&
        TryOrder(left, a.Min, b.Min, out int across) && across == 0;

    /// <summary>
    /// The bounds of the values <paramref name="column"/> holds in <paramref name="rows"/>: the
    /// widest of the bounds of every zone it touches that is not all null, and none as soon as one
    /// such zone has none or two bounds do not order.
    /// </summary>
    /// <param name="column">The column's map.</param>
    /// <param name="rows">The range.</param>
    /// <param name="mayHoldNaN">Whether one of those zones may hold a NaN.</param>
    private static ZoneBounds Widen(ZoneColumn column, RowRange rows, out bool mayHoldNaN)
    {
        FilterLiteral min = default;
        FilterLiteral max = default;
        bool hasMin = true;
        bool hasMax = true;
        bool any = false;
        mayHoldNaN = false;
        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);
            if (AllNull(column, bounds, zone))
            {
                continue;
            }

            mayHoldNaN |= ZoneMayHoldNaN(column, zone);
            if (!any)
            {
                any = true;
                min = bounds.Min;
                hasMin = bounds.HasMin;
                max = bounds.Max;
                hasMax = bounds.HasMax;
                continue;
            }

            int order = 0;
            hasMin = hasMin && bounds.HasMin && TryOrder(column, bounds.Min, min, out order);
            if (hasMin && order < 0)
            {
                min = bounds.Min;
            }

            hasMax = hasMax && bounds.HasMax && TryOrder(column, bounds.Max, max, out order);
            if (hasMax && order > 0)
            {
                max = bounds.Max;
            }
        }

        return ZoneBounds.Create(min, any && hasMin, max, any && hasMax, exact: false, 0, hasNullCount: false);
    }

    /// <summary>The column's map, when it has one it can prune with.</summary>
    private ZoneColumn? Usable(FieldExpr field) => Find(field) is { HasStatistics: true } column ? column : null;

    /// <summary>Whether two maps cut the rows into the same zones, so that zone <c>z</c> of one is zone <c>z</c> of the other.</summary>
    private static bool Aligned(ZoneColumn left, ZoneColumn right) =>
        left.ZoneLength == right.ZoneLength && left.RowCount == right.RowCount;

    /// <summary>Whether the map describes every row of <paramref name="rows"/>.</summary>
    private static bool Covers(ZoneColumn column, RowRange rows)
    {
        ZoneRange zones = column.Zones(rows);
        long covered = Math.Min((long)zones.End * column.ZoneLength, column.RowCount);
        return zones.End > zones.Start && rows.Start >= (long)zones.Start * column.ZoneLength && rows.End <= covered;
    }

    /// <summary>Whether a zone's null count fills it.</summary>
    private static bool AllNull(ZoneColumn column, ZoneBounds bounds, int zone) =>
        bounds.HasNullCount && bounds.NullCount >= column.RowsInZone(zone);

    /// <summary>
    /// Whether <paramref name="column"/> is null on every row of <paramref name="rows"/>; false
    /// when there is no map or it does not describe every row of the range.
    /// </summary>
    private static bool HoldsNoValue(ZoneColumn? column, RowRange rows)
    {
        if (column is null || !Covers(column, rows))
        {
            return false;
        }

        ZoneRange zones = column.Zones(rows);
        for (int zone = zones.Start; zone < zones.End; zone++)
        {
            if (!AllNull(column, column.Bounds(zone), zone))
            {
                return false;
            }
        }

        return true;
    }

    private ZoneColumn? Find(FieldExpr field)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            if (ReferenceEquals(_columns[i].Field, field) ||
                string.Equals(_columns[i].Field.Path, field.Path, StringComparison.Ordinal))
            {
                return _columns[i];
            }
        }

        return null;
    }

    private static ComparisonOp Negate(ComparisonOp op) => op switch
    {
        ComparisonOp.Equal => ComparisonOp.NotEqual,
        ComparisonOp.NotEqual => ComparisonOp.Equal,
        ComparisonOp.Less => ComparisonOp.GreaterOrEqual,
        ComparisonOp.LessOrEqual => ComparisonOp.Greater,
        ComparisonOp.Greater => ComparisonOp.LessOrEqual,
        _ => ComparisonOp.Less,
    };

    /// <summary>
    /// The ordered candidates of <paramref name="membership"/>, built on first use and kept for
    /// the pruner's life, or <see langword="null"/> when they do not order or the column is a
    /// decimal.
    /// </summary>
    private OrderedCandidates? OrderedFor(InExpr membership)
    {
        // The candidates are sorted in the generic order, bytes bytewise, which is not a decimal's.
        if (Find(membership.Field) is { IsDecimal: true })
        {
            return null;
        }

        Ordered[]? known = _ordered;
        if (known is not null)
        {
            for (int i = 0; i < known.Length; i++)
            {
                if (ReferenceEquals(known[i].Node, membership))
                {
                    return known[i].Candidates;
                }
            }
        }

        Ordered sorted = Sorted.GetValue(membership, static node => new Ordered(node, OrderedCandidates.TryBuild(node.Literals)));

        // Two lanes reaching the same unseen node publish it twice and one array wins. Both are
        // equal and neither is mutated after publication, so the loser is only work.
        int length = known?.Length ?? 0;
        Ordered[] grown = new Ordered[length + 1];
        for (int i = 0; i < length; i++)
        {
            grown[i] = known![i];
        }

        grown[length] = sorted;
        _ordered = grown;
        return sorted.Candidates;
    }

    /// <summary>What one <c>IN</c> node of the filter was prepared into.</summary>
    private sealed record Ordered(InExpr Node, OrderedCandidates? Candidates);

    /// <summary>
    /// The candidates of one <c>IN</c>, sorted so that a zone's bounds are answered by a search.
    /// </summary>
    /// <remarks>
    /// A zone rules an <c>IN</c> out exactly when no candidate falls inside <c>[min, max]</c>, and
    /// the candidate that decides it is the smallest one at or above <c>min</c>: if that one is
    /// above <c>max</c>, none is inside. So the walk over candidates becomes one binary search, and
    /// the cost per zone falls from the candidate count to its logarithm.
    ///
    /// Only candidates of one order are taken, compared with bounds of that order: bytes, integers
    /// of either sign, or floats. Widening an integer to a double is lossy above 2^53, which would
    /// let the sort and the per-zone comparison disagree about two candidates that differ, so
    /// integers and floats are never mixed. Floats compare as IEEE does, the two zeros one value,
    /// as the proof of an equality compares them; a NaN, which that proof leaves open on every
    /// zone, keeps the caller on the walk, and so do a boolean and a null.
    /// </remarks>
    private sealed class OrderedCandidates
    {
        /// <summary>
        /// Below this many candidates the walk wins: the search costs a bounds-kind test and a
        /// handful of unpredictable branches, where the walk is a straight line over values
        /// already in cache, and the file-level pruner asks a single zone.
        /// </summary>
        private const int LeastCandidates = 32;

        private readonly FilterLiteral[] _sorted;

        /// <summary>The candidates' order, as <see cref="OrderOf"/> names it.</summary>
        private readonly FilterLiteralKind _order;

        private OrderedCandidates(FilterLiteral[] sorted, FilterLiteralKind order)
        {
            _sorted = sorted;
            _order = order;
        }

        /// <summary>
        /// Orders <paramref name="literals"/>, or reports that they are not worth ordering.
        /// </summary>
        /// <param name="literals">The candidates of the <c>IN</c>.</param>
        /// <returns><see langword="null"/> when the caller must keep to one pass per candidate.</returns>
        internal static OrderedCandidates? TryBuild(FilterLiteral[] literals)
        {
            if (literals.Length < LeastCandidates)
            {
                return null;
            }

            FilterLiteralKind order = OrderOf(literals[0].Kind);
            if (order == FilterLiteralKind.Null)
            {
                return null;
            }

            for (int i = 0; i < literals.Length; i++)
            {
                if (OrderOf(literals[i].Kind) != order
                    || (order == FilterLiteralKind.Float && double.IsNaN(literals[i].FloatValue)))
                {
                    return null;
                }
            }

            FilterLiteral[] sorted = (FilterLiteral[])literals.Clone();
            Array.Sort(sorted, order switch
            {
                FilterLiteralKind.Bytes => CompareBytes,
                FilterLiteralKind.Float => CompareFloats,
                _ => CompareIntegers,
            });
            return new OrderedCandidates(sorted, order);
        }

        /// <summary>
        /// The order a value of <paramref name="kind"/> sorts in: bytes, integers of either sign
        /// on one line, or floats; <see cref="FilterLiteralKind.Null"/> for any other.
        /// </summary>
        private static FilterLiteralKind OrderOf(FilterLiteralKind kind) => kind switch
        {
            FilterLiteralKind.Bytes => FilterLiteralKind.Bytes,
            FilterLiteralKind.Signed or FilterLiteralKind.Unsigned => FilterLiteralKind.Signed,
            FilterLiteralKind.Float => FilterLiteralKind.Float,
            _ => FilterLiteralKind.Null,
        };

        /// <summary>
        /// What the bounds of one zone prove of the membership, when they compare at all.
        /// </summary>
        /// <param name="bounds">The zone's bounds.</param>
        /// <param name="inside">Whether some candidate falls within them.</param>
        /// <param name="whole">
        /// Whether some candidate is the zone's every value: exact bounds that meet, on a candidate.
        /// </param>
        /// <returns>Whether the bounds are of a kind these candidates compare against.</returns>
        internal bool TryAnswer(ZoneBounds bounds, out bool inside, out bool whole)
        {
            inside = false;
            whole = false;
            if (BoundsKind(bounds) is not FilterLiteralKind kind)
            {
                // No bounds at all: every candidate is possible and none is proven.
                inside = true;
                return true;
            }

            // A bound's own NaN is not screened here: the comparisons below refuse it, and the
            // zone then stays live, as the walk leaves it.
            if (OrderOf(kind) != _order)
            {
                return false;
            }

            int at = bounds.HasMin ? LowerBound(bounds.Min) : 0;
            if (at == _sorted.Length)
            {
                // Every candidate sorts below the zone's minimum.
                return true;
            }

            if (bounds.HasMax && TryCompare(bounds.Max, _sorted[at], out int high) && high < 0)
            {
                // The smallest candidate the minimum allows is already above the maximum.
                return true;
            }

            inside = true;
            whole = bounds.IsExact && bounds.HasMin && bounds.HasMax &&
                TryCompare(bounds.Min, _sorted[at], out int low) && low == 0 &&
                TryCompare(bounds.Max, _sorted[at], out int top) && top == 0;
            return true;
        }

        /// <summary>The first candidate at or above <paramref name="bound"/>, else the count.</summary>
        private int LowerBound(FilterLiteral bound)
        {
            int low = 0;
            int high = _sorted.Length;
            while (low < high)
            {
                int middle = (int)(((uint)low + (uint)high) >> 1);

                // The order is the sign of bound - candidate, so a positive one puts the candidate
                // below the bound and the answer to its right.
                if (TryCompare(bound, _sorted[middle], out int order) && order > 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static int CompareBytes(FilterLiteral left, FilterLiteral right) =>
            left.BytesValue.SequenceCompareTo(right.BytesValue);

        /// <summary>Two floats, neither a NaN, as IEEE orders them: the two zeros are one value.</summary>
        private static int CompareFloats(FilterLiteral left, FilterLiteral right) =>
            left.FloatValue.CompareTo(right.FloatValue);

        /// <summary>
        /// The signed and unsigned candidates on one line, which is what
        /// <see cref="TryCompare"/> also does with them: every negative sits below every value a
        /// <c>ulong</c> can hold, and the rest compare as the numbers they are.
        /// </summary>
        private static int CompareIntegers(FilterLiteral left, FilterLiteral right)
        {
            Int128 first = left.Kind == FilterLiteralKind.Signed ? left.SignedValue : left.UnsignedValue;
            Int128 second = right.Kind == FilterLiteralKind.Signed ? right.SignedValue : right.UnsignedValue;
            return first.CompareTo(second);
        }
    }
}
