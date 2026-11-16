// The slabs the pools' blocks are cut from (Slabs): a slab holds every page of its span but the last, which passes the
// end of its array; a piece is aligned to 64 bytes and to its size, and overlaps no other; pieces given back in any order
// join again, until a slab holds a half whole and comes back empty.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class SlabsTests
{
    private const int Pages = 8_192;

    [Fact]
    public void ASlabHoldsEveryPageOfItsSpanButTheLast()
    {
        Slabs.Slab slab = new Slabs.Slab();
        HashSet<int> pages = [];
        while (slab.TryTake(0) is >= 0 and int page)
        {
            Assert.True(pages.Add(page), $"page {page} handed out twice");
        }

        Assert.Equal(Pages - 1, pages.Count);
        Assert.DoesNotContain(Pages - 1, pages);
        Assert.False(slab.IsEmpty);

        foreach (int page in pages)
        {
            slab.Give(page, 0);
        }

        Assert.True(slab.IsEmpty);

        // Joined again: one half whole, the other never, its last page past the array.
        Assert.Equal(0, slab.TryTake(12));
        Assert.Equal(-1, slab.TryTake(12));
    }

    /// <summary>A slab says in one test whether a piece of a size is left, and counts the pages it handed out.</summary>
    [Fact]
    public void ASlabSaysWhetherAPieceOfASizeIsLeft()
    {
        Slabs.Slab slab = new Slabs.Slab();
        Assert.True(slab.Holds(12));
        int half = slab.TryTake(12);

        // The other half lacks its last page: pieces of half its size at most.
        Assert.False(slab.Holds(12));
        Assert.True(slab.Holds(11));
        while (slab.TryTake(0) >= 0)
        {
        }

        Assert.False(slab.Holds(0));
        Assert.Equal(Pages - 1, slab.TakenPages);
        slab.Give(half, 12);
        Assert.True(slab.Holds(12));
        Assert.Equal(Pages - 1 - 4096, slab.TakenPages);
    }

    [Fact]
    public unsafe void APieceIsAlignedAndInsideTheArray()
    {
        Slabs.Slab slab = new Slabs.Slab();
        nint data = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(slab.Array));
        Assert.Equal(0, (data + slab.Base) % 64);
        Assert.Equal(Slabs.SlabBytes, slab.Array.Length);
        for (int order = 0; order < 13; order++)
        {
            int page = slab.TryTake(order);
            Assert.True(page >= 0, $"no piece of order {order}");
            Assert.Equal(0, page % (1 << order));
            Assert.True(slab.Base + ((long)(page + (1 << order)) << 12) <= slab.Array.Length, $"the piece of order {order} at page {page} passes the array");
        }
    }

    /// <summary>Pieces of every size, taken until the slab is full and given back in a random order: none overlaps, and the slab comes back empty and whole.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PiecesOfEverySizeNeverOverlapAndJoinAgain(int seed)
    {
        Random random = new Random(seed);
        Slabs.Slab slab = new Slabs.Slab();
        bool[] taken = new bool[Pages];
        List<(int Page, int Order)> pieces = [];
        for (int misses = 0; misses < 64;)
        {
            int order = random.Next(0, 13);
            int page = slab.TryTake(order);
            if (page < 0)
            {
                misses++;
                continue;
            }

            for (int p = page; p < page + (1 << order); p++)
            {
                Assert.False(taken[p], $"page {p} of a piece of order {order} was already handed out");
                taken[p] = true;
            }

            pieces.Add((page, order));
        }

        foreach ((int page, int order) in pieces.OrderBy(_ => random.Next()))
        {
            slab.Give(page, order);
        }

        Assert.True(slab.IsEmpty);
        Assert.Equal(0, slab.TryTake(12));
    }
}
