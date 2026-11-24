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

    /// <summary>
    /// An object let go is released, through the release the stack was given, whether it sat unused or a reservation needed
    /// the room; one taken is not.
    /// </summary>
    [Fact]
    public void WhatIsLetGoIsReleasedAndWhatIsTakenIsNot()
    {
        System.Collections.Generic.List<object> released = [];
        KeptObjects<object> kept = new KeptObjects<object>("kept-test", capacity: 8, bytesEach: 0, item => released.Add(item));
        try
        {
            object a = new object();
            object b = new object();
            object c = new object();
            Assert.True(kept.Give(a));
            Assert.True(kept.Give(b));
            kept.LetGoUnused(collection: false);
            Assert.Empty(released);

            // Nothing taken since: both go, released.
            kept.LetGoUnused(collection: false);
            Assert.Equal([a, b], released);

            Assert.True(kept.Give(c));
            Assert.Same(c, kept.Take());
            Assert.True(kept.Give(c));
            kept.GiveBack(KeptKind.Blocks, 1);
            Assert.Equal([a, b, c], released);
            Assert.Null(kept.Take());
        }
        finally
        {
            HeldMemory.Unregister(kept);
        }
    }

    /// <summary>
    /// A reserve let go whole keeps all it holds while something takes from it, lets all of it go once nothing took since
    /// the registry last asked, and all of it when a reservation needs the room.
    /// </summary>
    [Fact]
    public void AReserveLetGoWholeGoesWhenUnusedOrForRoom()
    {
        int held = 3;
        int letGo = 0;
        KeptWhole whole = new KeptWhole("kept-test", () => held, () => (held, letGo) = (0, letGo + 1));
        try
        {
            Assert.Equal(3, whole.HeldBytes);

            // Taken from since the last asking: kept.
            lock (whole.Gate)
            {
                whole.Took();
            }

            whole.LetGoUnused(collection: false);
            Assert.Equal(0, letGo);

            // Nothing taken since: it goes.
            whole.LetGoUnused(collection: true);
            Assert.Equal(1, letGo);
            Assert.Equal(0, whole.HeldBytes);

            held = 5;
            lock (whole.Gate)
            {
                whole.Took();
            }

            whole.GiveBack(KeptKind.Blocks, 1);
            Assert.Equal(2, letGo);
            Assert.Equal(0, whole.HeldBytes);
        }
        finally
        {
            HeldMemory.Unregister(whole);
        }
    }

    /// <summary>
    /// A compressor kept is rented again at its level, and goes when a reservation needs the room: rented after, the level
    /// makes a new one. Level 21 is one no other test rents.
    /// </summary>
    [Fact]
    public void AKeptCompressorGoesWhenAReservationNeedsTheRoom()
    {
        ManagedCompressions.Zstd.ZstdCompressor given = new ManagedCompressions.Zstd.ZstdCompressor(21);
        Vorticity.Writing.ZstdEncoders.Return(given);
        Assert.Same(given, Vorticity.Writing.ZstdEncoders.Rent(21));
        Vorticity.Writing.ZstdEncoders.Return(given);
        Vorticity.Writing.ZstdEncoders.Held.GiveBack(KeptKind.Blocks, 1);
        Assert.NotSame(given, Vorticity.Writing.ZstdEncoders.Rent(21));
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
