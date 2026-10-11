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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(300)]
    [InlineData(5_000)]
    public void EqualKeysEndWhereListSortLeavesThem(int keys)
    {
        // Pairs of a key and where it began, compared by the key alone: equal keys are told apart by
        // where they end, which only the same steps as List.Sort's introsort reproduce.
        Random random = new Random(keys);
        for (int round = 0; round < 200; round++)
        {
            int length = random.Next(0, keys + 1);
            (int Key, int Origin)[] pairs = new (int Key, int Origin)[length];
            for (int i = 0; i < length; i++)
            {
                pairs[i] = (random.Next(0, 1 + (length / 4)), i);
            }

            List<(int Key, int Origin)> expected = [.. pairs];
            expected.Sort(static (a, b) => b.Key.CompareTo(a.Key));
            SpanSort.Sort(pairs.AsSpan(), default(ByKeyDescending));
            Assert.Equal(expected, pairs);
        }
    }

    [Fact]
    public void EqualKeysPastTheDepthLimitEndWhereListSortLeavesThem()
    {
        // The organ pipe drives both into the heap, each pair keeping where it began.
        const int Half = 20_000;
        (int Key, int Origin)[] pairs = new (int Key, int Origin)[2 * Half];
        for (int i = 0; i < Half; i++)
        {
            pairs[i] = (i / 3, i);
            pairs[(2 * Half) - 1 - i] = (i / 3, (2 * Half) - 1 - i);
        }

        List<(int Key, int Origin)> expected = [.. pairs];
        expected.Sort(static (a, b) => b.Key.CompareTo(a.Key));
        SpanSort.Sort(pairs.AsSpan(), default(ByKeyDescending));
        Assert.Equal(expected, pairs);
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

    private readonly struct ByKeyDescending : IComparer<(int Key, int Origin)>
    {
        public int Compare((int Key, int Origin) x, (int Key, int Origin) y) => y.Key.CompareTo(x.Key);
    }
}
