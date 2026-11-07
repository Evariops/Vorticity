// A take whose owner holds its positions unchanged until the scan ends, as the engine's own takes do:
// a list that ascends, each row in the file, is read where it lies, and any other is sorted into a
// copy of its own as a caller's list is. Both select the same rows.
using System;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class RowSelectionHeldTests
{
    private const long FileRows = 1_000;

    [Fact]
    public void AListThatAscendsIsReadWhereItLies()
    {
        // A rented array is longer than the rows it holds: the selection takes the rows, not the array.
        // Ten thousand rows, which a copy would take 80 KB for.
        long[] rented = new long[16_384];
        for (int i = 0; i < 10_000; i++)
        {
            rented[i] = 7 + (3L * i);
        }

        RowSelection.Held(rented.AsMemory(0, 10), 1_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RowSelection held = RowSelection.Held(rented.AsMemory(0, 10_000), 1_000_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_024, $"the selection allocated {allocated} bytes, a copy of the rows");
        Assert.Equal(10_000, held.Count);
        Assert.Equal(new RowRange(7, 7 + (3L * 9_999) + 1), held.Bounds);

        long[] rows = [3, 17, 18, 400, 999];
        Same(RowSelection.Create(rows, FileRows), RowSelection.Held(rows, FileRows));
    }

    [Theory]
    [InlineData(new long[] { 400, 3, 17, 3, 999 })]
    [InlineData(new long[] { 3, 3, 17 })]
    [InlineData(new long[] { 17, 3 })]
    public void AnyOtherListIsSortedAsACallersIs(long[] rows) =>
        Same(RowSelection.Create(rows, FileRows), RowSelection.Held(rows, FileRows));

    [Fact]
    public void AListThatStartsInsideAnArrayIsCopied()
    {
        long[] array = [1, 3, 17, 400];
        Same(RowSelection.Create(array.AsSpan(1), FileRows), RowSelection.Held(array.AsMemory(1), FileRows));
    }

    [Theory]
    [InlineData(new long[] { 3, 1_000 })]
    [InlineData(new long[] { -1, 3 })]
    public void ARowOutsideTheFileIsRefused(long[] rows) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RowSelection.Held(rows, FileRows));

    [Fact]
    public void NoRowSelectsNothing() => Assert.Equal(0, RowSelection.Held(ReadOnlyMemory<long>.Empty, FileRows).Count);

    private static void Same(RowSelection expected, RowSelection actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Bounds, actual.Bounds);
        int[] want = new int[expected.Count];
        int[] got = new int[actual.Count];
        foreach (RowRange split in (ReadOnlySpan<RowRange>)[new RowRange(0, 10), new RowRange(10, 400), new RowRange(400, FileRows)])
        {
            Assert.Equal(want.AsSpan(0, expected.LocalIndices(split, want)).ToArray(), got.AsSpan(0, actual.LocalIndices(split, got)).ToArray());
        }
    }
}
