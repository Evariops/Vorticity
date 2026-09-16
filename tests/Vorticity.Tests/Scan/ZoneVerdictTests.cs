// The dual of pruning - docs/12-index-reads.md §5.2: MustMatch, and the count the zone maps prove.
//
// Pruning's invariant is one-sided ("never drop a row") and its test is a subset check. The dual's
// is exact: A WRONG PROOF IS A WRONG COUNT. So the property here is an equality, per filter and
// per range, on every count a verdict decides -- the rows selected, refused and left unknown --
// against what the decode finds. The decode is read twice, for the filter and for its negation,
// because a batch carries the true rows only and the unknown rows are what is left once the false
// ones are taken out as well. The ranges are the blocks (a block is a zone), their halves, and
// ranges that straddle two zones, so that the restriction to a partial zone is held too.
//
// A vacuous pass is the failure mode of a test like this -- a verdict that never decides anything
// is never wrong -- so every filter also states how many of the 64 blocks it must decide.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ZoneVerdictTests
{
    /// <summary>
    /// 65536 rows in 64 zones of 1024. <c>monotone</c> is <c>1 000 000 + 3·row</c>, exact bounds;
    /// <c>banded</c> is the zone index (min = max in every zone); <c>strs</c> is
    /// <c>zZZZZ-RRRR</c> under Inexact bounds; <c>nulls</c> is the row index with a null run at
    /// the start of each zone growing from none in zone 0 to the whole of zone 63; <c>nans</c> is
    /// <c>row / 2</c> with the first <c>z</c> rows of zone <c>z</c> NaN, and a nan_count per zone.
    /// </summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    private const long Rows = 65_536;
    private const long Block = 1024;

    /// <summary>Each filter, with the least number of blocks whose true count it must decide.</summary>
    public static TheoryData<string, int> Filters => new TheoryData<string, int>
    {
        // The six comparisons on an exact, sorted column: the zone holding the constant is open
        // to an ordering predicate and to an equality alike, the rest decided.
        { "monotone >= mid", 63 },
        { "monotone > mid", 63 },
        { "monotone < mid", 63 },
        { "monotone <= mid", 63 },
        { "monotone = mid", 63 },
        { "monotone != mid", 63 },

        // A constant zone: min = max, exact, so equality is proven whole and != proven empty.
        { "banded = 5", 64 },
        { "banded != 5", 64 },
        { "banded IN (3, 7, 100)", 64 },
        { "banded IN (3, NULL)", 64 },
        { "banded > NULL", 64 },

        // Nulls: the count is the statistic, and a comparison is unknown on exactly those rows.
        { "nulls IS NULL", 64 },
        { "nulls IS NOT NULL", 64 },
        { "nulls > 30000", 63 },
        { "NOT (nulls > 30000)", 63 },
        { "nulls != 30000", 63 },

        // NaN: false for every ordering predicate and true for !=, and counted per zone.
        { "nans > 1000", 62 },
        { "nans <= 1000", 62 },
        { "NOT (nans > 1000)", 62 },
        { "nans != 5", 63 },
        { "nans = 5", 63 },
        { "nans != -1", 64 },

        // Strings under Inexact bounds -- bounded_min(64), a plain scalar, and bounded_max(64), the
        // reference's {bound, unknown} struct: a prefix range proves whole, an equality never
        // does, and a LIKE only ever proves empty.
        { "strs StartsWith z0005-", 64 },
        { "strs StartsWith empty", 64 },
        { "strs LIKE z0005%", 63 },
        { "strs LIKE %5", 0 },
        { "strs Contains 5", 0 },
        { "strs Contains empty", 64 },
        { "strs = z0005-0000", 63 },

        // The algebra: same column, other column, the three connectives, nested. The disjunction
        // of a comparison with IS NULL on its own column is the shape the algebra does not see
        // (its unknowns are the other side's trues), decided only where the comparison is empty.
        { "monotone band", 63 },
        { "monotone >= a AND nulls IS NOT NULL", 62 },
        { "banded = 3 OR banded = 7", 64 },
        { "nulls > 30000 OR nulls IS NULL", 30 },
        { "NOT (monotone band)", 63 },
        { "NOT (banded = 3 OR nans > 1000)", 62 },
        { "monotone >= a AND NOT (nulls > 30000)", 62 },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task WhateverTheVerdictDecidesIsWhatTheDecodeFinds(string name, int atLeast)
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter(name);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        LayoutTree tree = file.LayoutTree;
        Assert.Equal(Rows, tree.Root.RowCount);
        Assert.Equal(Block, SplitPlan.NaturalBatchRows(tree));

        ZonePruner? pruner = await ZonePruningPlan.BuildAsync(file, tree, filter, CancellationToken.None);
        Assert.NotNull(pruner);

        // The truth, from the decode: which rows are true, and which are false; the rest unknown.
        long[] trues = Prefix(await Selected(file, filter));
        long[] falses = Prefix(await Selected(file, Expr.Not(filter)));

        int decided = 0;
        List<long> undecided = [];
        foreach (RowRange range in Ranges())
        {
            long trueRows = trues[range.End] - trues[range.Start];
            long falseRows = falses[range.End] - falses[range.Start];
            long unknownRows = range.Length - trueRows - falseRows;

            RangeVerdict verdict = pruner.Verdict(range);
            string where = name + " over " + range.ToString();
            Assert.Equal(range.Length, verdict.Rows);
            if (verdict.TrueKnown)
            {
                Assert.True(trueRows == verdict.TrueCount, where + ": claims " + verdict.TrueCount.ToString(CultureInfo.InvariantCulture) + " true rows, the decode finds " + trueRows.ToString(CultureInfo.InvariantCulture));
            }

            if (verdict.FalseKnown)
            {
                Assert.True(falseRows == verdict.FalseCount, where + ": claims " + verdict.FalseCount.ToString(CultureInfo.InvariantCulture) + " false rows, the decode finds " + falseRows.ToString(CultureInfo.InvariantCulture));
            }

            if (verdict.UnknownKnown)
            {
                Assert.True(unknownRows == verdict.UnknownCount, where + ": claims " + verdict.UnknownCount.ToString(CultureInfo.InvariantCulture) + " unknown rows, the decode finds " + unknownRows.ToString(CultureInfo.InvariantCulture));
            }

            // The two entry points are the verdict, and no more.
            Assert.Equal(verdict.TrueKnown, pruner.TryCount(range, out long count));
            Assert.Equal(verdict.TrueCount, count);
            Assert.Equal(verdict.IsAllTrue, pruner.MustMatch(range));
            if (pruner.MustMatch(range))
            {
                Assert.Equal(range.Length, trueRows);
            }

            // And the dual never contradicts the prune: a range that cannot match has no true row,
            // and the verdict knows it.
            if (!pruner.MayMatch(range))
            {
                Assert.Equal(0, trueRows);
                Assert.True(verdict.IsNoneTrue, where + ": pruned, so the verdict should know there is no true row");
            }

            if (range.Length == Block && range.Start % Block == 0)
            {
                if (verdict.TrueKnown)
                {
                    decided++;
                }
                else
                {
                    undecided.Add(range.Start / Block);
                }
            }
        }

        Assert.True(
            decided >= atLeast,
            name + ": decided the true count of " + decided.ToString(CultureInfo.InvariantCulture) +
            " blocks, expected at least " + atLeast.ToString(CultureInfo.InvariantCulture) +
            "; undecided: " + string.Join(", ", undecided));
    }

    [Fact]
    public async Task TheNanCountIsReadFromTheZoneMap()
    {
        // Zone z holds exactly z NaN rows. `nans > -1` is true of every value (min >= 0), and NOT
        // of it is therefore true of exactly the NaN rows: the count is the statistic itself.
        Decoders.EnsureRegistered();
        VortexExpr filter = Expr.Not(Expr.Gt(Expr.Field("nans"), Expr.Literal(FilterLiteral.From(-1.0))));

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ZonePruner? pruner = await ZonePruningPlan.BuildAsync(file, file.LayoutTree, filter, CancellationToken.None);
        Assert.NotNull(pruner);

        for (int zone = 0; zone < Rows / Block; zone++)
        {
            RangeVerdict verdict = pruner.Verdict(RowRange.FromLength(zone * Block, Block));
            Assert.True(verdict.IsExact, "zone " + zone.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(zone, verdict.TrueCount);
            Assert.Equal(Block - zone, verdict.FalseCount);
            Assert.Equal(0, verdict.UnknownCount);
        }
    }

    [Fact]
    public async Task ANegatedOrderingPredicateKeepsTheNaNRowsOfAPrunedZone()
    {
        // NOT (nans > 1000) is TRUE on a NaN row (docs/08-semantics.md §2), where the pushed-down
        // nans <= 1000 is false: a zone whose every value is above 1000 still holds matches, its
        // NaN rows, and pruning it drops them. The nan_count says whether it may.
        Decoders.EnsureRegistered();
        VortexExpr filter = Expr.Not(Expr.Gt(Expr.Field("nans"), Expr.Literal(FilterLiteral.From(1000.0))));

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        List<long> unpruned = await Selected(file, filter, prune: false);
        List<long> pruned = await Selected(file, filter, prune: true);

        Assert.Equal(unpruned, pruned);

        // From zone 2 on every value is above 1000, so the rows selected there are exactly the
        // NaN ones: 2 + 3 + … + 63 of them.
        int late = 0;
        foreach (long row in unpruned)
        {
            if (row >= 2 * Block)
            {
                late++;
            }
        }

        Assert.Equal((2 + 63) * 62 / 2, late);
    }

    [Fact]
    public void ABoundIsOrderedAgainstAConstantTheWayTheKernelsOrderAValue()
    {
        // Same kind.
        Assert.Equal(-1, Order(FilterLiteral.From(1L), FilterLiteral.From(2L)));
        Assert.Equal(0, Order(FilterLiteral.From(2UL), FilterLiteral.From(2UL)));
        Assert.Equal(1, Order(FilterLiteral.From(true), FilterLiteral.From(false)));
        Assert.Equal(1, Order(FilterLiteral.From("b"), FilterLiteral.From("a")));
        Assert.Equal(-1, Order(FilterLiteral.From("a"), FilterLiteral.From("ab")));
        Assert.Equal(0, Order(FilterLiteral.From(-0.0), FilterLiteral.From(0.0)));

        // Mixed integer signs, as the kernels settle them.
        Assert.Equal(-1, Order(FilterLiteral.From(-1L), FilterLiteral.From(0UL)));
        Assert.Equal(-1, Order(FilterLiteral.From(long.MaxValue), FilterLiteral.From(ulong.MaxValue)));
        Assert.Equal(1, Order(FilterLiteral.From(0UL), FilterLiteral.From(-1L)));
        Assert.Equal(0, Order(FilterLiteral.From(7UL), FilterLiteral.From(7L)));

        // An integer against a float goes through double, lossy above 2^53 -- for the bound and
        // for every row alike, which is what makes the proof follow the kernel.
        long above = (1L << 53) + 1;
        Assert.Equal(0, Order(FilterLiteral.From(above), FilterLiteral.From(9_007_199_254_740_992.0)));
        Assert.Equal(1, Order(FilterLiteral.From(3L), FilterLiteral.From(2.5)));
        Assert.Equal(-1, Order(FilterLiteral.From(2.5), FilterLiteral.From(3L)));
        Assert.Equal(1, Order(FilterLiteral.From(4.0), FilterLiteral.From(3UL)));

        // Not comparable: a NaN on either side, a string against a number, a null.
        Assert.False(ZonePruner.TryCompare(FilterLiteral.From(double.NaN), FilterLiteral.From(1.0), out _));
        Assert.False(ZonePruner.TryCompare(FilterLiteral.From(1L), FilterLiteral.From(double.NaN), out _));
        Assert.False(ZonePruner.TryCompare(FilterLiteral.From("a"), FilterLiteral.From(1L), out _));
        Assert.False(ZonePruner.TryCompare(FilterLiteral.From(1L), FilterLiteral.Null, out _));
        Assert.False(ZonePruner.TryCompare(FilterLiteral.From(true), FilterLiteral.From(1L), out _));
    }

    [Fact]
    public void TheAlgebraIsThreeValuedLogicOverCounts()
    {
        const long n = 100;
        RangeVerdict allTrue = RangeVerdict.AllTrue(n);
        RangeVerdict allFalse = RangeVerdict.AllFalse(n);
        RangeVerdict allUnknown = RangeVerdict.AllUnknown(n);
        RangeVerdict some = RangeVerdict.Of(n, 30, 60, 10);
        RangeVerdict open = RangeVerdict.Undecided(n);

        // Two counts decide the third; one decides nothing else.
        Assert.Equal((30L, 60L, 10L), Counts(RangeVerdict.Of(n, 30, null, 10)));
        Assert.Equal((30L, 60L, 10L), Counts(RangeVerdict.Of(n, null, 60, 10)));
        RangeVerdict trueOnly = RangeVerdict.Of(n, 30, null, null);
        Assert.True(trueOnly.TrueKnown);
        Assert.False(trueOnly.FalseKnown);
        Assert.False(trueOnly.UnknownKnown);

        // NOT swaps true and false and keeps unknown.
        Assert.Equal((60L, 30L, 10L), Counts(RangeVerdict.Not(some)));
        Assert.True(RangeVerdict.Not(allTrue).IsAllFalse);
        Assert.True(RangeVerdict.Not(allUnknown).IsAllUnknown);
        Assert.False(RangeVerdict.Not(trueOnly).TrueKnown);
        Assert.True(RangeVerdict.Not(trueOnly).FalseKnown);

        // AND: an identity, an annihilator, and the unknowns of a constant side.
        Assert.Equal((30L, 60L, 10L), Counts(RangeVerdict.And(allTrue, some)));
        Assert.Equal((30L, 60L, 10L), Counts(RangeVerdict.And(some, allTrue)));
        Assert.True(RangeVerdict.And(allFalse, open).IsAllFalse);
        Assert.True(RangeVerdict.And(open, allFalse).IsAllFalse);
        Assert.Equal((0L, 60L, 40L), Counts(RangeVerdict.And(allUnknown, some)));
        Assert.False(RangeVerdict.And(some, some).TrueKnown);
        Assert.False(RangeVerdict.And(some, some).FalseKnown);

        // OR, likewise.
        Assert.True(RangeVerdict.Or(allTrue, open).IsAllTrue);
        Assert.Equal((30L, 60L, 10L), Counts(RangeVerdict.Or(allFalse, some)));
        Assert.Equal((30L, 0L, 70L), Counts(RangeVerdict.Or(allUnknown, some)));
        Assert.False(RangeVerdict.Or(some, some).TrueKnown);
        Assert.False(RangeVerdict.Or(some, some).FalseKnown);
        Assert.False(RangeVerdict.Or(some, some).UnknownKnown);

        // A restriction keeps only what was uniform.
        Assert.Equal((0L, 40L, 0L), Counts(allFalse.Restrict(40)));
        Assert.Equal((40L, 0L, 0L), Counts(allTrue.Restrict(40)));
        Assert.Equal((0L, 0L, 40L), Counts(allUnknown.Restrict(40)));
        Assert.False(some.Restrict(40).TrueKnown);
        Assert.False(some.Restrict(40).FalseKnown);
        Assert.False(some.Restrict(40).UnknownKnown);
        RangeVerdict noFalse = RangeVerdict.Of(n, 30, 0, 70).Restrict(40);
        Assert.True(noFalse.IsNoneFalse);
        Assert.False(noFalse.TrueKnown);

        // Concatenation sums what both parts decided.
        Assert.Equal((70L, 60L, 10L), Counts(RangeVerdict.Concat(some, RangeVerdict.AllTrue(40))));
        Assert.False(RangeVerdict.Concat(some, trueOnly).FalseKnown);
        Assert.Equal(200, RangeVerdict.Concat(some, trueOnly).Rows);
    }

    private static (long True, long False, long Unknown) Counts(RangeVerdict verdict)
    {
        Assert.True(verdict.IsExact);
        return (verdict.TrueCount, verdict.FalseCount, verdict.UnknownCount);
    }

    private static int Order(FilterLiteral bound, FilterLiteral value)
    {
        Assert.True(ZonePruner.TryCompare(bound, value, out int order));
        return order;
    }

    /// <summary>Every block, both halves of every block, and a straddle across every pair.</summary>
    private static IEnumerable<RowRange> Ranges()
    {
        for (long start = 0; start < Rows; start += Block)
        {
            yield return RowRange.FromLength(start, Block);
            yield return RowRange.FromLength(start, Block / 2);
            yield return RowRange.FromLength(start + (Block / 2), Block / 2);
            if (start + Block < Rows)
            {
                yield return RowRange.FromLength(start + (Block / 2), Block);
            }
        }
    }

    private static long[] Prefix(List<long> rows)
    {
        long[] prefix = new long[Rows + 1];
        foreach (long row in rows)
        {
            prefix[row + 1]++;
        }

        for (long i = 1; i <= Rows; i++)
        {
            prefix[i] += prefix[i - 1];
        }

        return prefix;
    }

    /// <summary>The rows the filter selects, by row index, from the decode.</summary>
    private static async Task<List<long>> Selected(VortexFile file, VortexExpr filter, bool prune = false)
    {
        List<long> rows = [];
        IAsyncEnumerable<RecordBatch> scan = file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync();

        await foreach (RecordBatch batch in scan.WithCancellation(CancellationToken.None))
        {
            Collect(batch, rows);
        }

        return rows;
    }

    private static void Collect(RecordBatch batch, List<long> rows)
    {
        VortexColumn view = batch.Column(Encoding.UTF8.GetBytes("monotone"));
        ReadOnlySpan<long> values = view.AsPrimitive<long>().Values;
        for (int i = 0; i < batch.RowCount; i++)
        {
            rows.Add((values[i] - 1_000_000) / 3);
        }
    }

    private static VortexExpr Filter(string name)
    {
        FieldExpr monotone = Expr.Field("monotone");
        FieldExpr banded = Expr.Field("banded");
        FieldExpr nulls = Expr.Field("nulls");
        FieldExpr nans = Expr.Field("nans");
        FieldExpr strs = Expr.Field("strs");

        // Row 20 000 is inside zone 19; row 30 000 inside zone 29; row 2 000 inside zone 1.
        LiteralExpr mid = Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 20_000L)));
        LiteralExpr a = Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 10_240L)));
        LiteralExpr b = Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 20_480L)));
        LiteralExpr thirty = Expr.Literal(FilterLiteral.From(30_000L));
        LiteralExpr thousand = Expr.Literal(FilterLiteral.From(1000.0));
        VortexExpr band = Expr.And(Expr.Ge(monotone, a), Expr.Lt(monotone, b));

        return name switch
        {
            "monotone >= mid" => Expr.Ge(monotone, mid),
            "monotone > mid" => Expr.Gt(monotone, mid),
            "monotone < mid" => Expr.Lt(monotone, mid),
            "monotone <= mid" => Expr.Le(monotone, mid),
            "monotone = mid" => Expr.Eq(monotone, mid),
            "monotone != mid" => Expr.Ne(monotone, mid),
            "banded = 5" => Expr.Eq(banded, Expr.Literal(FilterLiteral.From(5L))),
            "banded != 5" => Expr.Ne(banded, Expr.Literal(FilterLiteral.From(5L))),
            "banded IN (3, 7, 100)" => Expr.In(banded, FilterLiteral.From(3L), FilterLiteral.From(7L), FilterLiteral.From(100L)),
            "banded IN (3, NULL)" => Expr.In(banded, FilterLiteral.From(3L), FilterLiteral.Null),
            "banded > NULL" => Expr.Gt(banded, Expr.Literal(FilterLiteral.Null)),
            "nulls IS NULL" => Expr.IsNull(nulls),
            "nulls IS NOT NULL" => Expr.IsNotNull(nulls),
            "nulls > 30000" => Expr.Gt(nulls, thirty),
            "NOT (nulls > 30000)" => Expr.Not(Expr.Gt(nulls, thirty)),
            "nulls != 30000" => Expr.Ne(nulls, thirty),
            "nans > 1000" => Expr.Gt(nans, thousand),
            "nans <= 1000" => Expr.Le(nans, thousand),
            "NOT (nans > 1000)" => Expr.Not(Expr.Gt(nans, thousand)),
            "nans != 5" => Expr.Ne(nans, Expr.Literal(FilterLiteral.From(5.0))),
            "nans = 5" => Expr.Eq(nans, Expr.Literal(FilterLiteral.From(5.0))),
            "nans != -1" => Expr.Ne(nans, Expr.Literal(FilterLiteral.From(-1.0))),
            "strs StartsWith z0005-" => Expr.StartsWith(strs, FilterLiteral.From("z0005-")),
            "strs StartsWith empty" => Expr.StartsWith(strs, FilterLiteral.From(string.Empty)),
            "strs LIKE z0005%" => Expr.Like(strs, FilterLiteral.From("z0005%")),
            "strs LIKE %5" => Expr.Like(strs, FilterLiteral.From("%5")),
            "strs Contains 5" => Expr.Contains(strs, FilterLiteral.From("5")),
            "strs Contains empty" => Expr.Contains(strs, FilterLiteral.From(string.Empty)),
            "strs = z0005-0000" => Expr.Eq(strs, Expr.Literal(FilterLiteral.From("z0005-0000"))),
            "monotone band" => band,
            "monotone >= a AND nulls IS NOT NULL" => Expr.And(Expr.Ge(monotone, a), Expr.IsNotNull(nulls)),
            "banded = 3 OR banded = 7" => Expr.Or(
                Expr.Eq(banded, Expr.Literal(FilterLiteral.From(3L))),
                Expr.Eq(banded, Expr.Literal(FilterLiteral.From(7L)))),
            "nulls > 30000 OR nulls IS NULL" => Expr.Or(Expr.Gt(nulls, thirty), Expr.IsNull(nulls)),
            "NOT (monotone band)" => Expr.Not(band),
            "NOT (banded = 3 OR nans > 1000)" => Expr.Not(Expr.Or(
                Expr.Eq(banded, Expr.Literal(FilterLiteral.From(3L))),
                Expr.Gt(nans, thousand))),
            "monotone >= a AND NOT (nulls > 30000)" => Expr.And(Expr.Ge(monotone, a), Expr.Not(Expr.Gt(nulls, thirty))),
            _ => throw new ArgumentException("unknown filter " + name, nameof(name)),
        };
    }
}
