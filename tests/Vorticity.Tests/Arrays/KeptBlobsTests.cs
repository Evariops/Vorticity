// The blobs a scan context keeps parsed from one batch to the next. A batch of a wide table reads a
// blob of every column, more than the old sixty-four arenas: each must still be found parsed on the
// next batch. A layout's blob read from other bytes is parsed over in its own arena, past the
// capacity the blob read longest ago gives its arena up, and forgetting drops every blob.
using System;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class KeptBlobsTests
{
    private const int SegmentBytes = 8;

    [Fact]
    public void AWideBatchFindsEveryBlobItKeptOnTheNext()
    {
        const int columns = 200;
        byte[] bytes = GC.AllocateArray<byte>(columns * SegmentBytes, pinned: true);
        KeptBlobs blobs = new KeptBlobs();
        ArrayNodeArena[] arenas = new ArrayNodeArena[columns];

        blobs.NextBatch();
        for (int layout = 0; layout < columns; layout++)
        {
            Assert.False(blobs.Take(layout, Segment(bytes, layout), treeBytes: 0));
            arenas[layout] = blobs.Current!;
            blobs.RememberCurrent(layout, Segment(bytes, layout));
        }

        // Read in another order, as a batch starting at another column would.
        blobs.NextBatch();
        for (int i = 0; i < columns; i++)
        {
            int layout = (i * 7 + 3) % columns;
            Assert.True(blobs.Take(layout, Segment(bytes, layout), treeBytes: 0), $"layout {layout} kept");
            Assert.Same(arenas[layout], blobs.Current);
        }
    }

    [Fact]
    public void ABlobFromOtherBytesIsParsedOverInItsOwnArena()
    {
        byte[] bytes = GC.AllocateArray<byte>(4 * SegmentBytes, pinned: true);
        KeptBlobs blobs = new KeptBlobs();

        blobs.NextBatch();
        Assert.False(blobs.Take(5, Segment(bytes, 0), treeBytes: 0));
        ArrayNodeArena arena = blobs.Current!;
        blobs.RememberCurrent(5, Segment(bytes, 0));
        Assert.False(blobs.Take(6, Segment(bytes, 1), treeBytes: 0));
        blobs.RememberCurrent(6, Segment(bytes, 1));

        blobs.NextBatch();
        Assert.False(blobs.Take(5, Segment(bytes, 2), treeBytes: 0));
        Assert.Same(arena, blobs.Current);
        blobs.RememberCurrent(5, Segment(bytes, 2));

        blobs.NextBatch();
        Assert.True(blobs.Take(5, Segment(bytes, 2), treeBytes: 0));
        Assert.Same(arena, blobs.Current);
        Assert.True(blobs.Take(6, Segment(bytes, 1), treeBytes: 0));
        Assert.False(blobs.Take(7, Segment(bytes, 0), treeBytes: 0));
    }

    [Fact]
    public void PastItsCapacityTheBlobReadLongestAgoGivesItsArenaUp()
    {
        int columns = KeptBlobs.Capacity + 44;
        byte[] bytes = GC.AllocateArray<byte>(columns * SegmentBytes, pinned: true);
        KeptBlobs blobs = new KeptBlobs();

        blobs.NextBatch();
        for (int layout = 0; layout < columns; layout++)
        {
            Assert.False(blobs.Take(layout, Segment(bytes, layout), treeBytes: 0));
            blobs.RememberCurrent(layout, Segment(bytes, layout));
        }

        blobs.NextBatch();
        for (int layout = columns - 1; layout >= columns - KeptBlobs.Capacity; layout--)
        {
            Assert.True(blobs.Take(layout, Segment(bytes, layout), treeBytes: 0), $"layout {layout} kept");
        }

        Assert.False(blobs.Take(0, Segment(bytes, 0), treeBytes: 0));
        blobs.RememberCurrent(0, Segment(bytes, 0));
        Assert.True(blobs.Take(0, Segment(bytes, 0), treeBytes: 0));
    }

    [Fact]
    public void ForgettingDropsEveryBlobAndKeepsTheArenas()
    {
        byte[] bytes = GC.AllocateArray<byte>(3 * SegmentBytes, pinned: true);
        KeptBlobs blobs = new KeptBlobs();

        blobs.NextBatch();
        Assert.False(blobs.Take(1, Segment(bytes, 0), treeBytes: 0));
        ArrayNodeArena arena = blobs.Current!;
        blobs.RememberCurrent(1, Segment(bytes, 0));

        blobs.Forget();
        Assert.Null(blobs.Current);
        blobs.NextBatch();
        Assert.False(blobs.Take(1, Segment(bytes, 0), treeBytes: 0));
        Assert.Same(arena, blobs.Current);
        blobs.RememberCurrent(1, Segment(bytes, 0));
        Assert.True(blobs.Take(1, Segment(bytes, 0), treeBytes: 0));
    }

    private static VortexBuffer Segment(byte[] bytes, int index) =>
        VortexBuffer.FromPinned(bytes.AsSpan(index * SegmentBytes, SegmentBytes), alignmentExponent: 0);
}
