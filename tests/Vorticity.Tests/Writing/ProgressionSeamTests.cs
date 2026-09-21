// The seam between two blocks, as the statistics pass records it and as a merge reads it.
//
// A block is stepped from the last row of the block before it, so that a chunk made of several
// blocks can be told a progression without a walk. That step -- the SEAM -- is kept apart from the
// block's own steps: folded into them, it would make the first block of a chunk carry the jump
// between it and the chunk before as a break of its own; `PlanMemoryTests` has a file where that
// packs every progression. These are the four seams that matter, on eight rows each, against
// `BlockStats` directly.
using System;

using Vorticity.Arrays;
using Vorticity.Buffers;
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

    /// <summary>
    /// A column that climbs by one past its type's maximum is no progression, though every
    /// difference the lanes of the pass's register walk take in the type's own width is one.
    /// </summary>
    [Theory]
    [InlineData(0, 200)]
    [InlineData(80, 120)]
    public void AClimbThatWrapsIsNoProgression(int cut, int rows)
    {
        int[] values = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            values[i] = unchecked(int.MaxValue - 99 + i);
        }

        Assert.True(Steps(values, PType.I32, cut).DeltaBroken, "the column wraps at row 100");

        // The same climb stopping on the maximum is one.
        int[] limit = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            limit[i] = int.MaxValue - (rows - 1) + i;
        }

        BlockStats stats = Steps(limit, PType.I32, cut);
        Assert.False(stats.DeltaBroken);
        Assert.Equal(1, stats.Delta);
    }

    /// <summary>The same on bytes, sixteen lanes, climbing and descending.</summary>
    [Fact]
    public void ByteLanesTellAWrapFromADescent()
    {
        byte[] wraps = new byte[200];
        byte[] descends = new byte[200];
        for (int i = 0; i < 200; i++)
        {
            wraps[i] = unchecked((byte)(100 + i));
            descends[i] = (byte)(255 - i);
        }

        Assert.True(Steps(wraps, PType.U8, 0).DeltaBroken);
        BlockStats down = Steps(descends, PType.U8, 0);
        Assert.False(down.DeltaBroken);
        Assert.Equal(-1, down.Delta);

        // A step that breaks deep inside a register.
        descends[137] = 7;
        Assert.True(Steps(descends, PType.U8, 0).DeltaBroken);
    }

    /// <summary>
    /// Rows of <paramref name="values"/> through the pass, in two ranges cut at
    /// <paramref name="cut"/> (none when 0), the second continuing the first.
    /// </summary>
    private static BlockStats Steps<T>(T[] values, PType ptype, int cut)
        where T : unmanaged
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int width = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        VortexBuffer buffer = arena.Allocate(values.Length * width, width, out Span<byte> bytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        int node = arena.AddPrimitive(
            types.Primitive(ptype, Nullability.NonNullable), values.Length, Validity.NonNullable, ptype, buffer);

        BlockStats stats = default;
        PreviousRow previous = new PreviousRow();
        if (cut > 0)
        {
            BlockStatsPass.Accumulate(arena, node, 0, cut, ref stats, previous);
        }

        BlockStatsPass.Accumulate(arena, node, cut, values.Length - cut, ref stats, previous);
        return stats;
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
