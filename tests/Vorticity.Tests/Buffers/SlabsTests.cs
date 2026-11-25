// The slabs the pools' blocks are cut from (Slabs): a slab fills a region of the collector's pinned heap but for its last
// page and some bytes, and holds every page of its span its array holds whole; a piece is aligned to 64 bytes and to its
// size, and overlaps no other; pieces given back in any order join again, until a slab holds a half whole and comes back
// empty. A block of more than half a region takes the first half of a slab twice its size.
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
    /// <summary>The pages of a slab's span.</summary>
    private static int Pages => Slabs.SpanBytes >> Slabs.PageShift;

    /// <summary>The order of half a slab's span: its largest piece.</summary>
    private static int Half => BitOperations(Slabs.SpanBytes) - Slabs.PageShift - 1;

    /// <summary>The pages a slab's array holds whole, from its first byte aligned to 64.</summary>
    private static int Whole(Slabs.Slab slab) => (slab.Array.Length - slab.Base) >> Slabs.PageShift;

    private static int BitOperations(int value) => System.Numerics.BitOperations.Log2((uint)value);

    [Fact]
    public void ASlabHoldsEveryPageItsArrayHoldsWhole()
    {
        Slabs.Slab slab = new Slabs.Slab();
        int whole = Whole(slab);
        Assert.InRange(whole, Pages - 6, Pages - 1);
        HashSet<int> pages = [];
        while (slab.TryTake(0) is >= 0 and int page)
        {
            Assert.True(pages.Add(page), $"page {page} handed out twice");
        }

        Assert.Equal(whole, pages.Count);
        Assert.All(pages, page => Assert.True(page < whole, $"page {page} passes the array's {whole} whole pages"));
        Assert.False(slab.IsEmpty);

        foreach (int page in pages)
        {
            slab.Give(page, 0);
        }

        Assert.True(slab.IsEmpty);

        // Joined again: one half whole, the other never, its last pages past the array.
        Assert.Equal(0, slab.TryTake(Half));
        Assert.Equal(-1, slab.TryTake(Half));
    }

    /// <summary>A slab says in one test whether a piece of a size is left, and counts the pages it handed out.</summary>
    [Fact]
    public void ASlabSaysWhetherAPieceOfASizeIsLeft()
    {
        Slabs.Slab slab = new Slabs.Slab();
        Assert.True(slab.Holds(Half));
        Assert.False(slab.Holds(Half + 1));
        int half = slab.TryTake(Half);

        // The other half lacks its last pages: pieces of half its size at most.
        Assert.False(slab.Holds(Half));
        Assert.True(slab.Holds(Half - 1));
        while (slab.TryTake(0) >= 0)
        {
        }

        Assert.False(slab.Holds(0));
        Assert.Equal(Whole(slab), slab.TakenPages);
        slab.Give(half, Half);
        Assert.True(slab.Holds(Half));
        Assert.Equal(Whole(slab) - (1 << Half), slab.TakenPages);
    }

    [Fact]
    public unsafe void APieceIsAlignedAndInsideTheArray()
    {
        Slabs.Slab slab = new Slabs.Slab();
        nint data = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(slab.Array));
        Assert.Equal(0, (data + slab.Base) % 64);
        Assert.Equal(Regions.ArrayBytes(Slabs.SpanBytes), slab.Array.Length);
        Assert.Equal(slab.Array.Length, slab.Bytes);

        // Each size alone in the slab, given back before the next: the pages missing at the end of the second half leave some
        // sizes there no piece of their own, which a piece of a larger size then gives.
        for (int order = 0; order <= Half; order++)
        {
            int page = slab.TryTake(order);
            Assert.True(page >= 0, $"no piece of order {order}");
            Assert.Equal(0, page % (1 << order));
            Assert.True(slab.Base + ((long)(page + (1 << order)) << 12) <= slab.Array.Length, $"the piece of order {order} at page {page} passes the array");
            slab.Give(page, order);
        }

        Assert.True(slab.IsEmpty);
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
            int order = random.Next(0, Half + 1);
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
        Assert.Equal(0, slab.TryTake(Half));
    }

    /// <summary>
    /// A slab twice a region fills two regions but for a page, gives its first half whole, a block of a region, and serves
    /// pieces of half a region at most from its second, whose last pages pass the array; a piece larger than its span is
    /// none of its.
    /// </summary>
    [Fact]
    public void ASlabOfTwoRegionsGivesARegionWholeAndSmallerPiecesBeside()
    {
        Slabs.Slab slab = new Slabs.Slab(2 * Slabs.SpanBytes);
        Assert.Equal(Regions.ArrayBytes(2L * Slabs.SpanBytes), slab.Bytes);
        Assert.Equal(-1, slab.TryTake(Half + 2));
        Assert.Equal(0, slab.TryTake(Half + 1));
        Assert.False(slab.Holds(Half + 1));
        Assert.True(slab.Holds(Half));
        Assert.Equal(1 << (Half + 1), slab.TryTake(Half));
        Assert.False(slab.Holds(Half));
        Assert.True(slab.Holds(Half - 1));
    }

    /// <summary>A block of the largest class is cut from a slab twice its size, made for it, and its slab goes with it.</summary>
    [Fact]
    public void ABlockOfTheLargestClassIsTheFirstHalfOfASlab()
    {
        Assert.Equal(Slabs.LargestBytes <= Slabs.SpanBytes / 2 ? Slabs.SpanBytes : 2 * Slabs.LargestBytes, Slabs.SpanFor(Slabs.LargestBytes));
        Assert.Equal(Slabs.SpanBytes, Slabs.SpanFor(Slabs.SpanBytes / 2));
        (Slabs.Slab slab, byte[] array, int offset, bool _) = Slabs.TryTake(Slabs.LargestBytes) ?? throw new InvalidOperationException("No slab could be made.");
        try
        {
            Assert.Same(slab.Array, array);
            Assert.True(slab.Bytes >= Slabs.LargestBytes + offset);
            Assert.Equal(0, (offset - slab.Base) % Slabs.LargestBytes);
        }
        finally
        {
            Slabs.Give(slab, offset, Slabs.LargestBytes);
        }
    }

    /// <summary>The regions the collector gives pinned arrays: a large region a power of two, and an array that fills one but for a page and 80 bytes.</summary>
    [Fact]
    public void AnArrayIsSizedToFillTheRegionItTakes()
    {
        int region = Regions.LargeBytes;
        Assert.True(System.Numerics.BitOperations.IsPow2(region) && region >= 8 << 20, $"a large region of {region:N0} bytes");
        Assert.Equal(region - Environment.SystemPageSize - 80, Regions.ArrayBytes(region));
        Assert.Equal(region, Regions.SpanFor(1));
        Assert.Equal(region, Regions.SpanFor(Regions.ArrayBytes(region)));
        Assert.Equal(2L * region, Regions.SpanFor(Regions.ArrayBytes(region) + 1));
    }
}
