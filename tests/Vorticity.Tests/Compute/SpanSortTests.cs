using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// The sort that allocates nothing gives what <c>Array.Sort</c> gives, on every shape its paths
/// meet: short spans, runs already in order or reversed, duplicates, and inputs that drive the
/// quicksort past its depth into the heapsort.
/// </summary>
public sealed class SpanSortTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(500)]
    [InlineData(100_000)]
    public void RandomKeysComeOutInOrder(int length)
    {
        Random random = new Random(length);
        long[] keys = new long[length];
        for (int i = 0; i < length; i++)
        {
            keys[i] = random.NextInt64(-1_000, 1_000);
        }

        AssertSortsLikeArraySort(keys);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(65_536)]
    public void OrderedReversedAndConstantKeysComeOutInOrder(int length)
    {
        long[] ascending = new long[length];
        long[] descending = new long[length];
        long[] constant = new long[length];
        for (int i = 0; i < length; i++)
        {
            ascending[i] = i;
            descending[i] = length - i;
            constant[i] = 7;
        }

        AssertSortsLikeArraySort(ascending);
        AssertSortsLikeArraySort(descending);
        AssertSortsLikeArraySort(constant);
    }

    [Fact]
    public void AnOrganPipeFallsBackToTheHeapAndStillSorts()
    {
        // Ascending then descending: the median of three keeps landing on an extreme, which is the
        // shape that exhausts the depth limit.
        const int Half = 50_000;
        long[] keys = new long[2 * Half];
        for (int i = 0; i < Half; i++)
        {
            keys[i] = i;
            keys[(2 * Half) - 1 - i] = i;
        }

        AssertSortsLikeArraySort(keys);
    }

    [Fact]
    public void AStructComparerOrdersByIt()
    {
        int[] keys = [5, -3, 9, 0, 9, -3, 12, 1];
        SpanSort.Sort(keys.AsSpan(), default(Descending));
        Assert.Equal([12, 9, 9, 5, 1, 0, -3, -3], keys);
    }

    private static void AssertSortsLikeArraySort(long[] keys)
    {
        long[] expected = (long[])keys.Clone();
        Array.Sort(expected);
        SpanSort.Sort(keys.AsSpan());
        Assert.Equal(expected, keys);
    }

    private readonly struct Descending : IComparer<int>
    {
        public int Compare(int x, int y) => y.CompareTo(x);
    }
}
