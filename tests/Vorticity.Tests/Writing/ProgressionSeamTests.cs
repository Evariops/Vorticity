// The seam between two blocks, as the statistics pass records it and as a merge reads it.
//
// A block is stepped from the last row of the block before it, so that a chunk made of several
// blocks can be told a progression without a walk. That step -- the SEAM -- used to be folded into
// the block's own steps, which made the first block of a chunk carry the jump between it and the
// chunk before as a break of its own; `PlanMemoryTests` has the file it cost 30 KB on. These are
// the four seams that matter, on eight rows each, against `BlockStats` directly.
using System;

using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>The seam belongs to the merge, and only when the block before it is in the range.</summary>
public sealed class ProgressionSeamTests
{
    /// <summary>A jump at the seam breaks the two blocks together and neither alone.</summary>
    [Fact]
    public void AJumpAtTheSeamIsNotABreakInsideEitherBlock()
    {
        using ColumnFixture f = new ColumnFixture();
        ColumnWriter column = TwoBlocks(f, [0, 1, 2, 3, 100, 101, 102, 103]);

        AssertProgression(column.Chunk(0, 1), step: 1);
        AssertProgression(column.Chunk(1, 1), step: 1);
        Assert.True(column.Chunk(0, 2).DeltaBroken, "a jump between the blocks breaks the chunk");
        Assert.Equal(8, column.Chunk(0, 2).RunCount);
        Assert.Equal(4, column.Chunk(1, 1).RunCount);
    }

    /// <summary>A step that continues across the seam makes the two blocks one progression.</summary>
    [Fact]
    public void AStepThatContinuesAcrossTheSeamMergesIntoOneProgression()
    {
        using ColumnFixture f = new ColumnFixture();
        ColumnWriter column = TwoBlocks(f, [10, 13, 16, 19, 22, 25, 28, 31]);

        AssertProgression(column.Chunk(0, 2), step: 3);
        Assert.Equal(8, column.Chunk(0, 2).RunCount);
    }

    /// <summary>
    /// A constant block followed by a climbing one that starts on the same value: the seam is a
    /// step of zero, so the second block's first row starts no run, and the steps disagree, so the
    /// chunk is no progression.
    /// </summary>
    [Fact]
    public void ASeamOfZeroBeforeAClimbingBlockStartsNoRun()
    {
        using ColumnFixture f = new ColumnFixture();
        ColumnWriter column = TwoBlocks(f, [5, 5, 5, 5, 5, 6, 7, 8]);

        AssertProgression(column.Chunk(0, 1), step: 0);
        AssertProgression(column.Chunk(1, 1), step: 1);
        Assert.True(column.Chunk(0, 2).DeltaBroken, "a step of zero then a step of one is no progression");
        // One run of fives, then 6, 7, 8: the 5 that opens the second block continues the first run.
        Assert.Equal(4, column.Chunk(0, 2).RunCount);
        Assert.Equal(4, column.Chunk(1, 1).RunCount);
    }

    /// <summary>A null before the block is a seam that is no step, and a run boundary all the same.</summary>
    [Fact]
    public void ANullBeforeTheBlockBreaksTheSeamAndNotTheBlock()
    {
        using ColumnFixture f = new ColumnFixture();
        long[] values = [0, 0, 0, 0, 5, 6, 7, 8];
        bool[] valid = [false, false, false, false, true, true, true, true];
        int node = f.Int64Node(values, f.BitmapValidity(valid), Nullability.Nullable);
        ColumnWriter column = new ColumnWriter();
        column.Accumulate(f.Arena, node, 0, 4);
        column.CloseBlock();
        column.Accumulate(f.Arena, node, 4, 4);
        column.CloseBlock();

        AssertProgression(column.Chunk(1, 1), step: 1);
        Assert.True(column.Chunk(0, 2).DeltaBroken, "nulls are no progression");
        // The four nulls are one run, and 5 opens the next one against a null.
        Assert.Equal(5, column.Chunk(0, 2).RunCount);
        Assert.Equal(4, column.Chunk(1, 1).RunCount);
    }

    private static ColumnWriter TwoBlocks(ColumnFixture f, ReadOnlySpan<long> values)
    {
        int node = f.Int64Node(values, Validity.NonNullable);
        ColumnWriter column = new ColumnWriter();
        column.Accumulate(f.Arena, node, 0, 4);
        column.CloseBlock();
        column.Accumulate(f.Arena, node, 4, 4);
        column.CloseBlock();
        return column;
    }

    private static void AssertProgression(BlockStats stats, long step)
    {
        Assert.True(stats.DeltaKnown, "the steps should be known");
        Assert.False(stats.DeltaBroken, "the steps should agree");
        Assert.Equal(step, stats.Delta);
    }
}
