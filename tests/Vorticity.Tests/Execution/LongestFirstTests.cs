using System;
using System.Linq;
using Vorticity.Execution;
using Xunit;

namespace Vorticity.Tests.Execution;

/// <summary>
/// A batch's items go longest first by what each took the time before, ties in the order they had,
/// the order kept from one batch to the next.
/// </summary>
public sealed class LongestFirstTests
{
    [Fact]
    public void OrdersTheItemsLongestFirstEveryTime()
    {
        const int Items = 500;
        LongestFirst order = new(Items);
        Random random = new(7);
        long[] took = new long[Items];
        int[] into = new int[Items];
        for (int batch = 0; batch < 20; batch++)
        {
            for (int i = 0; i < Items; i++)
            {
                took[i] = batch == 0 ? random.Next(1_000) : Math.Max(0, took[i] + random.Next(-20, 21));
            }

            order.Order(took, into);
            Assert.Equal(Enumerable.Range(0, Items), into.Order());
            for (int i = 1; i < Items; i++)
            {
                Assert.True(took[into[i - 1]] >= took[into[i]], $"Batch {batch}: item {into[i - 1]} took {took[into[i - 1]]}, before {into[i]}'s {took[into[i]]}.");
            }
        }
    }

    [Fact]
    public void KeepsTiesInTheirFirstOrder()
    {
        LongestFirst order = new(4);
        int[] into = new int[4];
        order.Order([0, 0, 0, 0], into);
        Assert.Equal([0, 1, 2, 3], into);
        order.Order([1, 5, 5, 1], into);
        Assert.Equal([1, 2, 0, 3], into);
    }
}
