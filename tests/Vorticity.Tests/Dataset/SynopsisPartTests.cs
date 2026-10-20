// The buckets one table of an answer folds, held as runs of buckets folded one after another.
//
// WHAT IS HELD: a part holds every bucket it folded and none it did not, whether the buckets are slices or hours
// of several slices each; a bucket not folded between two folded ones ends a run, so a chunk of the table that
// holds only such buckets is not read; a bucket folded twice counts once, and one folded out of the order slices
// come in is held on its own; a bucket whose states carry another instant keeps it.
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class SynopsisPartTests
{
    private static SynopsisPart Part(SynopsisBucket bucket) =>
        new(new SynopsisTableInfo(SynopsisTableKind.Coarse, bucket, [1], [3], 100));

    private static TableChunk Chunk(long first, long last) => new(new IndexSegment(0, 64, 6, 0), first, last, 10);

    [Fact]
    public void ASliceNotFoldedEndsARunAndIsHeldByNone()
    {
        // Slices 0 to 5, starting every 10; slice 3, at 30, cut by the window and read as rows.
        SynopsisPart part = Part(SynopsisBucket.Slice);
        foreach (int slice in new[] { 0, 1, 2, 4, 5 })
        {
            part.Fold(10 * slice, 10 * slice, slice, slice);
        }

        Assert.Equal(5, part.KeptCount);
        foreach (long held in new long[] { 0, 10, 20, 40, 50 })
        {
            Assert.True(part.Holds(held), $"bucket {held}");
        }

        Assert.False(part.Holds(30));
        Assert.False(part.Holds(60));
        Assert.False(part.Holds(-10));

        Assert.False(part.Folds(Chunk(30, 30)));
        Assert.True(part.Folds(Chunk(20, 30)));
        Assert.True(part.Folds(Chunk(30, 40)));
        Assert.True(part.Folds(Chunk(long.MinValue, long.MaxValue)));
        Assert.False(part.Folds(Chunk(60, 70)));
        Assert.False(part.Folds(Chunk(-20, -10)));
    }

    [Fact]
    public void HoursOfSeveralSlicesEachRunOnAndAnHourNotFoldedEndsTheRun()
    {
        // Hours of four slices each: hours 0 and 1 kept, hour 2 cut, hour 3 kept.
        const long Hour = 3_600_000_000;
        SynopsisPart part = Part(SynopsisBucket.Hour);
        part.Fold(0, 0, 0, 3);
        part.Fold(Hour, Hour, 4, 7);
        part.Fold(3 * Hour, 3 * Hour, 12, 15);

        Assert.Equal(3, part.KeptCount);
        Assert.True(part.Holds(0));
        Assert.True(part.Holds(Hour));
        Assert.False(part.Holds(2 * Hour));
        Assert.True(part.Holds(3 * Hour));
        Assert.False(part.Folds(Chunk(2 * Hour, 2 * Hour)));
        Assert.True(part.Folds(Chunk(2 * Hour, 3 * Hour)));
    }

    [Fact]
    public void ABucketFoldedTwiceCountsOnceAndOneOutOfOrderIsHeldOnItsOwn()
    {
        SynopsisPart part = Part(SynopsisBucket.Slice);
        part.Fold(0, 0, 0, 0);
        part.Fold(10, 10, 1, 1);
        part.Fold(40, 41, 4, 4);
        part.Fold(10, 10, 1, 1);
        Assert.Equal(3, part.KeptCount);

        // Slice 3 at 30 folded late: held, while slice 2 at 20, never folded, is not.
        part.Fold(30, 30, 3, 3);
        Assert.Equal(4, part.KeptCount);
        Assert.True(part.Holds(30));
        Assert.False(part.Holds(20));
        Assert.True(part.Holds(40));
        Assert.False(part.Folds(Chunk(20, 20)));
        Assert.True(part.Folds(Chunk(20, 30)));

        // The instant bucket 40's states carry, the first the object holds in it.
        Assert.Equal(41, part.Instants![40]);
        Assert.False(part.Instants.ContainsKey(30));
    }
}
