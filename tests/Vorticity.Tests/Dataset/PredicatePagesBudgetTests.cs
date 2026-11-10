// A handle keeps its filters' pages within a budget, least used first out: a filter whose literals change at every
// question, a sliding window of time, made a table that grew for the handle's life. Without a disk cache the pages kept
// are found again; past the budget the oldest are let go.
using System;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class PredicatePagesBudgetTests
{
    [Fact]
    public void ThePagesKeptStayWithinTheBudgetTheLastFoundAgain()
    {
        // Pages of 64 granules, a word each: some 104 bytes counted, a dozen within the budget.
        PredicatePages pages = new PredicatePages(budget: 1_280);
        UInt128 uid = 0xABCDEF;
        for (int filter = 0; filter < 200; filter++)
        {
            ulong[] kept = [1UL << (filter % 64)];
            pages.Keep(uid, (UInt128)filter, kept, granules: 64, byPlaces: false, cache: null);
        }

        int found = 0;
        for (int filter = 0; filter < 200; filter++)
        {
            found += pages.Of(uid, (UInt128)filter, granules: 64, cache: null) is { } held && held[0] == 1UL << (filter % 64) ? 1 : 0;
        }

        Assert.InRange(found, 1, 12);
        Assert.NotNull(pages.Of(uid, (UInt128)199, granules: 64, cache: null));
        Assert.Null(pages.Of(uid, (UInt128)0, granules: 64, cache: null));

        // A budget of nothing keeps nothing.
        PredicatePages none = new PredicatePages(budget: 0);
        none.Keep(uid, 7, [1UL], granules: 64, byPlaces: false, cache: null);
        Assert.Null(none.Of(uid, 7, granules: 64, cache: null));
    }
}
