// The mask of live blocks, the read contract's one operation.
//
// Three things are held here and nowhere else. The arithmetic of a mask: a short last block, bits
// past the end that stay clear, a range that straddles two blocks. The refinement: the zone pruner
// kills exactly the blocks its own per-range question refuses, block by block, so the equivalence
// `ZonePruningTests` proves for splits is inherited by the mask rather than re-proven. And that
// pruning goes through the mask at all: a pruner whose every block is dead reads no split.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class BlockMaskTests
{
    /// <summary>65536 rows in 64 zones of 1024, over {monotone, banded, strs, nulls, nans}.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Theory]
    [InlineData(65_536, 1024, 64)]
    [InlineData(65_537, 1024, 65)]
    [InlineData(1023, 1024, 1)]
    [InlineData(0, 1024, 0)]
    [InlineData(1_000_000, 8192, 123)]
    public void AFreshMaskCountsItsBlocksAndLeavesThemAllLive(long rows, long blockRows, int blocks)
    {
        BlockMask mask = new BlockMask(rows, blockRows);

        Assert.Equal(blocks, mask.BlockCount);
        Assert.Equal(blocks, mask.LiveCount);
        Assert.Equal(blocks == 0, mask.IsEmpty);
        for (int block = 0; block < blocks; block++)
        {
            Assert.True(mask.IsLive(block));
        }

        // Past the end nothing is live, whatever the word holds.
        Assert.False(mask.IsLive(blocks));
        Assert.False(mask.IsLive(-1));
    }

    [Fact]
    public void NarrowingLeavesLiveTheBlocksOfTheLastRangeAloneWhateverCameBefore()
    {
        // A thousand blocks, the last one short; ranges drawn anywhere, across words, some empty,
        // and now and then a block killed after the narrowing.
        const long Rows = (999 * 64) + 17;
        BlockMask mask = new BlockMask(Rows, 64);
        Random random = new Random(33);
        for (int step = 0; step < 500; step++)
        {
            long start = random.Next((int)Rows);
            long end = Math.Min(Rows, start + random.Next(0, 64 * 150));
            mask.KeepOnly(new RowRange(start, end));
            long killed = step % 3 == 0 && end > start ? start / 64 : -1;
            if (killed >= 0)
            {
                mask.Kill((int)killed);
            }

            int live = 0;
            for (int block = 0; block < mask.BlockCount; block++)
            {
                bool expected = end > start && block >= start / 64 && block <= (end - 1) / 64 && block != killed;
                live += expected ? 1 : 0;
                Assert.True(expected == mask.IsLive(block), $"step {step}, block {block}");
            }

            Assert.Equal(live, mask.LiveCount);
        }
    }

    [Fact]
    public void TheLastBlockIsClippedToTheFile()
    {
        BlockMask mask = new BlockMask(65_537, 1024);

        Assert.Equal(new RowRange(0, 1024), mask.BlockRange(0));
        Assert.Equal(new RowRange(65_536, 65_537), mask.BlockRange(64));
    }

    [Fact]
    public void ARangeIsLiveWhenAnyBlockItStraddlesIs()
    {
        BlockMask mask = new BlockMask(65_536, 1024);
        RowRange straddling = new RowRange(1000, 1100);

        Assert.True(mask.AnyLive(straddling));

        mask.Kill(1);
        Assert.True(mask.AnyLive(straddling), "block 0 is still live");
        Assert.False(mask.AnyLive(new RowRange(1024, 2048)), "block 1 alone is dead");

        mask.Kill(0);
        Assert.False(mask.AnyLive(straddling));
        Assert.Equal(62, mask.LiveCount);

        // Killing a dead block again, or one past the end, changes nothing.
        mask.Kill(0);
        mask.Kill(64);
        Assert.Equal(62, mask.LiveCount);

        // Rows past the file are not blocks; a range wholly past it is dead.
        Assert.False(mask.AnyLive(new RowRange(65_536, 70_000)));
        Assert.True(mask.AnyLive(new RowRange(65_000, 70_000)), "the last block is live");
    }

    [Fact]
    public void ARangeHasDeadBlocksWhenAnyBlockItOverlapsIsDead()
    {
        BlockMask mask = new BlockMask(65_536, 1024);
        RowRange chunk = new RowRange(0, 16_384);

        Assert.False(mask.HasDeadBlocks(chunk));

        mask.Kill(3);
        Assert.True(mask.HasDeadBlocks(chunk));
        Assert.False(mask.HasDeadBlocks(new RowRange(16_384, 32_768)), "the dead block is in the first chunk");
        Assert.True(mask.HasDeadBlocks(new RowRange(3_000, 3_100)), "a range inside the dead block");
        Assert.False(mask.HasDeadBlocks(new RowRange(65_536, 70_000)), "past the file there are no blocks");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void TheRangeQuestionsAnswerAsTheBlocksOneByOneWould(int seed)
    {
        // Ranges that start and end inside a word, on its edges, and across several words, over
        // masks dense and sparse: the questions are asked a word at a time and must say what a walk
        // over the blocks says.
        Random random = new Random(seed);
        const long BlockRows = 100;
        BlockMask mask = new BlockMask(300 * BlockRows + 37, BlockRows);
        double deadShare = random.NextDouble();
        for (int block = 0; block < mask.BlockCount; block++)
        {
            if (random.NextDouble() < deadShare)
            {
                mask.Kill(block);
            }
        }

        for (int trial = 0; trial < 2_000; trial++)
        {
            long start = random.NextInt64(0, mask.RowCount + 200);
            long end = start + random.NextInt64(0, 20_000);
            RowRange rows = new RowRange(start, end);

            bool anyLive = false;
            bool anyDead = false;
            long clipped = Math.Min(end, mask.RowCount);
            for (long block = start / BlockRows; start < clipped && block * BlockRows < clipped; block++)
            {
                anyLive |= mask.IsLive((int)block);
                anyDead |= !mask.IsLive((int)block);
            }

            Assert.Equal(anyLive, mask.AnyLive(rows));
            Assert.Equal(anyDead, mask.HasDeadBlocks(rows));

            long first = random.NextInt64(-3, mask.BlockCount + 3);
            long past = first + random.NextInt64(0, 200);
            bool anyLiveBlock = false;
            for (long block = Math.Max(first, 0); block < Math.Min(past, mask.BlockCount); block++)
            {
                anyLiveBlock |= mask.IsLive((int)block);
            }

            Assert.Equal(anyLiveBlock, mask.AnyLiveBlocks(first, past));
        }
    }

    [Fact]
    public void EveryBlockCanBeKilledAndTheMaskIsThenEmpty()
    {
        BlockMask mask = new BlockMask(1_000_000, 8192);
        for (int block = 0; block < mask.BlockCount; block++)
        {
            mask.Kill(block);
        }

        Assert.True(mask.IsEmpty);
        Assert.False(mask.AnyLive(new RowRange(0, 1_000_000)));
    }

    [Fact]
    public async Task TheZonePrunerKillsExactlyTheBlocksItsRangeQuestionRefuses()
    {
        Decoders.EnsureRegistered();

        // The narrow band of ZonePruningTests: ~100 rows of a sorted column, so most of the 64
        // zones are dead and a few are live -- both answers are exercised.
        VortexExpr narrow = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        LayoutTree tree = file.LayoutTree;
        ZonePruner? pruner = await ZonePruningPlan.BuildAsync(file, tree, narrow, CancellationToken.None);
        Assert.NotNull(pruner);

        BlockMask mask = new BlockMask(tree.Root.RowCount, SplitPlan.NaturalBatchRows(tree));
        Assert.Equal(64, mask.BlockCount);
        pruner.Refine(mask);

        for (int block = 0; block < mask.BlockCount; block++)
        {
            Assert.Equal(pruner.MayMatch(mask.BlockRange(block)), mask.IsLive(block));
        }

        Assert.True(mask.LiveCount > 0, "the band matches rows, so some block is live");
        Assert.True(mask.LiveCount * 4 < mask.BlockCount, $"only {mask.LiveCount} of 64 blocks should survive a band of ~100 rows");

        // The same mask again, through the plan's own entry point.
        BlockMask? refined = await ZonePruningPlan.RefineAsync(file, tree, narrow, CancellationToken.None);
        Assert.NotNull(refined);
        Assert.Equal(mask.LiveCount, refined.LiveCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task TheMaskKillsWhatTheRangeQuestionRefusesWhateverTheFilter(int seed)
    {
        Decoders.EnsureRegistered();

        // The mask is answered sixty-four blocks at a time, from a table of the bounds for the
        // numeric comparisons it can answer and block by block for the rest; the range question is
        // asked block by block, one literal at a time. Filters of every shape over every column --
        // negations, both connectives, bounds at and past the edges, NaN, infinities, constants of
        // another kind, null, text, null tests and memberships -- over masks with dead blocks already,
        // must leave live exactly the blocks the question does not refuse. The same file serves every
        // filter, so the zone maps a filter decoded serve the next.
        Random random = new Random(seed);
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        LayoutTree tree = file.LayoutTree;
        long blockRows = SplitPlan.NaturalBatchRows(tree);
        int checkedFilters = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            VortexExpr filter = RandomFilter(random, depth: 3);
            ZonePruner? pruner = await ZonePruningPlan.BuildAsync(file, tree, filter, CancellationToken.None);
            if (pruner is null)
            {
                continue;
            }

            BlockMask mask = new BlockMask(tree.Root.RowCount, blockRows);
            bool[] wasLive = new bool[mask.BlockCount];
            for (int block = 0; block < mask.BlockCount; block++)
            {
                if (random.Next(4) == 0)
                {
                    mask.Kill(block);
                }

                wasLive[block] = mask.IsLive(block);
            }

            pruner.Refine(mask);
            for (int block = 0; block < mask.BlockCount; block++)
            {
                bool expected = wasLive[block] && pruner.MayMatch(mask.BlockRange(block));
                Assert.True(expected == mask.IsLive(block), $"block {block} of {filter}: the question says {expected}");
            }

            checkedFilters++;
        }

        Assert.True(checkedFilters > 300, $"only {checkedFilters} filters made a pruner");

        // And the tables did answer: every numeric column's zones are the blocks, and its bounds are
        // of one kind.
        VortexExpr every = Expr.And(
            Expr.And(
                Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(0L))),
                Expr.Ge(Expr.Field("banded"), Expr.Literal(FilterLiteral.From(0L)))),
            Expr.And(
                Expr.Ge(Expr.Field("nulls"), Expr.Literal(FilterLiteral.From(0L))),
                Expr.Ge(Expr.Field("nans"), Expr.Literal(FilterLiteral.From(0.0)))));
        ZonePruner? all = await ZonePruningPlan.BuildAsync(file, tree, every, CancellationToken.None);
        Assert.NotNull(all);
        foreach ((string column, FilterLiteralKind kind) in new[]
                 {
                     ("monotone", FilterLiteralKind.Signed), ("banded", FilterLiteralKind.Signed),
                     ("nulls", FilterLiteralKind.Signed), ("nans", FilterLiteralKind.Float),
                 })
        {
            ZoneColumn zones = all.Column(column)!;
            Assert.Equal(blockRows, zones.ZoneLength);
            Assert.Equal(kind, zones.Table.Kind);
        }
    }

    /// <summary>The numeric columns of <see cref="Zoned"/> and the range of their values.</summary>
    private static readonly (string Column, long Low, long High)[] Ranges =
    [
        ("monotone", 1_000_000, 1_196_605),
        ("banded", 0, 63),
        ("nulls", 0, 64_511),
        ("nans", 0, 32_767),
    ];

    private static VortexExpr RandomFilter(Random random, int depth) =>
        (depth == 0 ? 0 : random.Next(5)) switch
        {
            1 => Expr.Not(RandomFilter(random, depth - 1)),
            2 => Expr.And(RandomFilter(random, depth - 1), RandomFilter(random, depth - 1)),
            3 => Expr.Or(RandomFilter(random, depth - 1), RandomFilter(random, depth - 1)),
            _ => RandomLeaf(random),
        };

    private static VortexExpr RandomLeaf(Random random)
    {
        switch (random.Next(12))
        {
            case 0:
                return random.Next(2) == 0 ? Expr.IsNull(Expr.Field("nulls")) : Expr.IsNotNull(Expr.Field("nulls"));
            case 1:
                return Expr.In(
                    Expr.Field("banded"),
                    FilterLiteral.From(random.Next(-2, 66)),
                    FilterLiteral.From(random.Next(-2, 66)));
            case 2:
                string text = $"z{random.Next(0, 70):D4}-{random.Next(0, 1100):D4}";
                return Compare(random, Expr.Field("strs"), FilterLiteral.From(text));
            default:
                (string column, long low, long high) = Ranges[random.Next(Ranges.Length)];
                return Compare(random, Expr.Field(column), RandomLiteral(random, low, high));
        }
    }

    private static ComparisonExpr Compare(Random random, FieldExpr field, FilterLiteral value)
    {
        LiteralExpr literal = Expr.Literal(value);
        return random.Next(6) switch
        {
            0 => Expr.Eq(field, literal),
            1 => Expr.Ne(field, literal),
            2 => Expr.Lt(field, literal),
            3 => Expr.Le(field, literal),
            4 => Expr.Gt(field, literal),
            _ => Expr.Ge(field, literal),
        };
    }

    private static FilterLiteral RandomLiteral(Random random, long low, long high)
    {
        long near = random.Next(3) switch
        {
            0 => low + random.Next(-2, 3),
            1 => high + random.Next(-2, 3),
            _ => random.NextInt64(low, high + 1),
        };

        return random.Next(12) switch
        {
            0 => FilterLiteral.From((ulong)Math.Max(near, 0)),
            1 => FilterLiteral.From(near + 0.5),
            2 => FilterLiteral.From((double)near),
            3 => FilterLiteral.From(double.NaN),
            4 => FilterLiteral.From(random.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity),
            5 => FilterLiteral.Null,
            6 => FilterLiteral.From(random.Next(2) == 0 ? long.MinValue : long.MaxValue),
            7 => FilterLiteral.From(-0.0),
            _ => FilterLiteral.From(near),
        };
    }

    [Fact]
    public async Task AFilterNoZoneMapCanAnswerLeavesNoMaskAtAll()
    {
        Decoders.EnsureRegistered();

        // `nans` is a float column whose zone bounds exclude NaN; a comparison with NaN is never
        // provable either way, and the pruner declines. Still, a column with statistics makes a
        // pruner -- what makes NO mask is a file with no zone map to read.
        const string Flat = "containers/uncompressed_canonical";
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Flat), CancellationToken.None);
        string column = file.DType.GetFieldName(0);
        VortexExpr filter = Expr.IsNotNull(Expr.Field(column));

        BlockMask? refined = await ZonePruningPlan.RefineAsync(file, file.LayoutTree, filter, CancellationToken.None);
        Assert.Null(refined);
    }
}
