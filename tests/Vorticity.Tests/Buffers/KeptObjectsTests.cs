using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

/// <summary>Objects kept from one operation to the next, which the registry of kept memory asks back.</summary>
public sealed class KeptObjectsTests
{
    /// <summary>
    /// The objects no taking reached since the registry last asked go at its next asking, the longest kept first, and those
    /// taken and given back stay; a reservation short of room has every one of them go.
    /// </summary>
    [Fact]
    public void WhatSatUnusedGoesAndEverythingGoesForRoom()
    {
        KeptObjects<object> kept = new KeptObjects<object>("kept-test", capacity: 8, bytesEach: 0);
        try
        {
            object a = new object();
            object b = new object();
            object c = new object();
            Assert.True(kept.Give(a));
            Assert.True(kept.Give(b));
            Assert.True(kept.Give(c));

            // The first asking starts the watch: nothing was watched yet.
            kept.LetGoUnused(collection: false);
            Assert.Equal(3, kept.Count);

            // c taken and given back; a and b sat unused, and go.
            Assert.Same(c, kept.Take());
            Assert.True(kept.Give(c));
            kept.LetGoUnused(collection: false);
            Assert.Equal(1, kept.Count);
            Assert.Same(c, kept.Take());
            Assert.True(kept.Give(c));

            kept.GiveBack(KeptKind.Blocks, 1);
            Assert.Equal(0, kept.Count);
            Assert.Null(kept.Take());
        }
        finally
        {
            HeldMemory.Unregister(kept);
        }
    }

    /// <summary>Past its bound, an object given back is left to the collector.</summary>
    [Fact]
    public void PastItsBoundAnObjectIsLeft()
    {
        KeptObjects<object> kept = new KeptObjects<object>("kept-test", capacity: 1, bytesEach: 100);
        try
        {
            Assert.True(kept.Give(new object()));
            Assert.False(kept.Give(new object()));
            Assert.Equal(100, kept.HeldBytes);
        }
        finally
        {
            HeldMemory.Unregister(kept);
        }
    }
}
