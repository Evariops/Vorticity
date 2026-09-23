// The chunked layout's binary-search chunk selection, in isolation.
//
// Transcribed from the reference chunked layout reader's `chunk_range`; every boundary
// gets its own case because "start exactly on a chunk start, one before, one after" is where an
// off-by-one hides and produces a plausible, wrong answer rather than a crash.
using System;

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

        int[] into = new int[selection.Length];
        int count = ChunkedLayoutReader.Rebase(selection, 1_000, 500, into);

        Assert.Equal(selection.Length, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i * 7, into[i]);
        }
    }

    [Fact]
    public void ASelectionAcrossChunksKeepsTheRowsOfThisOneInTheirOrder()
    {
        // Rows before the chunk, at both of its ends, past it, and inside it out of order: nine
        // of them, so the vector pass meets a row outside and hands the rows to the row walk.
        int[] selection = [5, 1_499, 999, 1_000, 1_500, 1_200, 3_000, 1_100, 1_001];
        int[] into = new int[selection.Length];

        int count = ChunkedLayoutReader.Rebase(selection, 1_000, 500, into);

        Assert.Equal([499, 0, 200, 100, 1], into[..count]);
        Assert.Equal(0, ChunkedLayoutReader.Rebase(selection, (long)int.MaxValue + 1, 500, into));
    }
}
