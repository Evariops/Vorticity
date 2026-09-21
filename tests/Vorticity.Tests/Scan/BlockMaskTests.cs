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

    [Fact]
    public async Task AFilterNoZoneMapCanAnswerLeavesNoMaskAtAll()
    {
        Decoders.EnsureRegistered();

        // `nans` is a float column whose zone bounds exclude NaN; a comparison with NaN is never
        // provable either way, and the pruner declines. Still, a column with statistics makes a
        // pruner -- what makes NO mask is a file with no zone map to read.
        const string Flat = "containers/uncompressed_canonical";
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Flat), CancellationToken.None);
        string column = file.Schema.GetFieldName(0);
        VortexExpr filter = Expr.IsNotNull(Expr.Field(column));

        BlockMask? refined = await ZonePruningPlan.RefineAsync(file, file.LayoutTree, filter, CancellationToken.None);
        Assert.Null(refined);
    }
}
