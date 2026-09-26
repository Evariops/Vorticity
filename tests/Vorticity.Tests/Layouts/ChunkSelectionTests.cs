// The chunked layout's binary-search chunk selection, in isolation.
//
// Transcribed from the reference chunked layout reader's `chunk_range`; every boundary
// gets its own case because "start exactly on a chunk start, one before, one after" is where an
// off-by-one hides and produces a plausible, wrong answer rather than a crash.
using System;
using System.Collections.Generic;

using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class ChunkSelectionTests
{
    private static readonly long[] Offsets = [0, 100, 200, 300];

    [Theory]
    // Whole range.
    [InlineData(0, 300, 0, 3)]
    // Exactly one chunk, on both boundaries.
    [InlineData(0, 100, 0, 1)]
    [InlineData(100, 200, 1, 2)]
    [InlineData(200, 300, 2, 3)]
    // One row before and after a boundary.
    [InlineData(99, 100, 0, 1)]
    [InlineData(99, 101, 0, 2)]
    [InlineData(100, 101, 1, 2)]
    [InlineData(101, 199, 1, 2)]
    // Spanning three chunks.
    [InlineData(50, 250, 0, 3)]
    // Empty ranges, on and off a boundary.
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 100, 1, 1)]
    // Not found at either end: insertion point 2, so first = 2 - 1 and last = 2. The chunk in
    // between contributes an empty local range and the reader skips it. This is upstream's
    // own answer, unwrapped from `unwrap_or_else(|x| x.saturating_sub(1))`.
    [InlineData(150, 150, 1, 2)]
    [InlineData(300, 300, 3, 3)]
    // The last row.
    [InlineData(299, 300, 2, 3)]
    public void ChunkRangeMatchesTheReference(long start, long end, int expectedFirst, int expectedLast)
    {
        ChunkedLayoutReader.ChunkRange(Offsets, new RowRange(start, end), out int first, out int last);
        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedLast, last);
    }

    [Fact]
    public void EveryRangeSelectsExactlyTheChunksItTouches()
    {
        // Exhaustive over the whole 300-row space: the selected chunks must cover every requested
        // row and no chunk outside the range may be selected.
        for (long start = 0; start <= 300; start++)
        {
            for (long end = start; end <= 300; end++)
            {
                ChunkedLayoutReader.ChunkRange(Offsets, new RowRange(start, end), out int first, out int last);

                long covered = 0;
                for (int c = first; c < last; c++)
                {
                    long lo = Math.Max(start, Offsets[c]);
                    long hi = Math.Min(end, Offsets[c + 1]);
                    if (hi > lo)
                    {
                        covered += hi - lo;
                    }
                }

                Assert.True(
                    covered == end - start,
                    $"[{start}, {end}) selected chunks [{first}, {last}) covering {covered} rows.");
            }
        }
    }

    [Fact]
    public void ANodeWithNoChunksSelectsNothing()
    {
        ChunkedLayoutReader.ChunkRange([0], RowRange.Empty, out int first, out int last);
        Assert.Equal(0, first);
        Assert.Equal(0, last);

        ChunkedLayoutReader.ChunkRange(default, RowRange.Empty, out first, out last);
        Assert.Equal(0, first);
        Assert.Equal(0, last);
    }

    [Fact]
    public void RepeatedOffsetsFromEmptyChunksStillCoverTheRange()
    {
        // A chunked layout may legally hold empty chunks, which repeat an offset. The corpus has
        // none at the layout level - the writer filters them - so this is the shape that would
        // otherwise never be exercised.
        long[] offsets = [0, 0, 10, 10, 20];
        for (long start = 0; start <= 20; start++)
        {
            for (long end = start; end <= 20; end++)
            {
                ChunkedLayoutReader.ChunkRange(offsets, new RowRange(start, end), out int first, out int last);

                long covered = 0;
                for (int c = first; c < last; c++)
                {
                    long lo = Math.Max(start, offsets[c]);
                    long hi = Math.Min(end, offsets[c + 1]);
                    if (hi > lo)
                    {
                        covered += hi - lo;
                    }
                }

                Assert.True(covered == end - start, $"[{start}, {end}) covered {covered} rows.");
            }
        }
    }

    [Fact]
    public void ASelectionInsideTheChunkMovesWholeIntoItsRows()
    {
        int[] selection = new int[37];
        for (int i = 0; i < selection.Length; i++)
        {
            selection[i] = 1_000 + (i * 7);
        }

        int taken = 0;
        ReadOnlySpan<int> run = ChunkedLayoutReader.NextRun(selection, ref taken, 1_000, 1_500);
        int[] into = new int[run.Length];
        ChunkedLayoutReader.Shift(run, 1_000, into);

        Assert.Equal(selection.Length, run.Length);
        Assert.Equal(selection.Length, taken);
        for (int i = 0; i < into.Length; i++)
        {
            Assert.Equal(i * 7, into[i]);
        }
    }

    [Fact]
    public void ASelectionAcrossChunksGivesEachChunkItsOwnRun()
    {
        // Rows before the first chunk, at both ends of each, and past the last: nine and more, so
        // the shift takes a vector and then single rows.
        int[] selection = [5, 999, 1_000, 1_001, 1_100, 1_200, 1_201, 1_202, 1_203, 1_499, 1_500, 2_999, 3_000];
        int taken = 0;

        ReadOnlySpan<int> first = ChunkedLayoutReader.NextRun(selection, ref taken, 1_000, 1_500);
        int[] into = new int[first.Length];
        ChunkedLayoutReader.Shift(first, 1_000, into);
        Assert.Equal([0, 1, 100, 200, 201, 202, 203, 499], into);

        ReadOnlySpan<int> second = ChunkedLayoutReader.NextRun(selection, ref taken, 1_500, 3_000);
        into = new int[second.Length];
        ChunkedLayoutReader.Shift(second, 1_500, into);
        Assert.Equal([0, 1_499], into);
        Assert.Equal(12, taken);

        // A chunk no int row reaches has no run.
        int past = 0;
        Assert.True(ChunkedLayoutReader.NextRun(selection, ref past, (long)int.MaxValue + 1, (long)int.MaxValue + 500).IsEmpty);
    }

    [Fact]
    public void EveryChunkOfARangeGetsTheSelectedRowsThatFallInIt()
    {
        Random random = new Random(11);
        for (int trial = 0; trial < 300; trial++)
        {
            // Chunks of 0 to 40 rows, empty ones included, and a selection that ascends over them.
            long[] offsets = new long[random.Next(1, 20) + 1];
            for (int i = 1; i < offsets.Length; i++)
            {
                offsets[i] = offsets[i - 1] + random.Next(0, 41);
            }

            List<int> rows = [];
            for (int row = 0; row < offsets[^1]; row++)
            {
                if (random.Next(3) == 0)
                {
                    rows.Add(row);
                }
            }

            int[] selection = [.. rows];
            int taken = 0;
            for (int chunk = 0; chunk + 1 < offsets.Length; chunk++)
            {
                ReadOnlySpan<int> run = ChunkedLayoutReader.NextRun(selection, ref taken, offsets[chunk], offsets[chunk + 1]);
                int[] into = new int[run.Length];
                ChunkedLayoutReader.Shift(run, offsets[chunk], into);

                int[] expected = [.. rows.FindAll(row => row >= offsets[chunk] && row < offsets[chunk + 1]).ConvertAll(row => (int)(row - offsets[chunk]))];
                Assert.Equal(expected, into);
            }

            Assert.Equal(selection.Length, taken);
        }
    }
}
