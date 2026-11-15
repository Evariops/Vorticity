using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// What a batch's reads are planned in: the pending slots and their offsets, the specs sorted by offset, and the runs they
/// coalesce into. Blocks of the pool, rented for one batch's read and given back after it: the read itself costs far more
/// than the rents, which a reader then needs no field of its own for, whichever lane reads through it.
/// </summary>
internal struct ReadScratch
{
    /// <summary>The pending slots, in the order <see cref="Keys"/> sorts them into.</summary>
    internal Block<int> Order;

    /// <summary>Each pending slot's offset, the sort's keys.</summary>
    internal Block<ulong> Keys;

    /// <summary>The pending specs, by offset.</summary>
    internal Block<SegmentSpec> Sorted;

    /// <summary>The runs the sorted specs coalesce into.</summary>
    internal Block<CoalescedRun> Runs;

    /// <summary>Room for <paramref name="registered"/> segments, the most a batch's runs can be.</summary>
    internal static ReadScratch Rent(int registered) => new ReadScratch
    {
        Order = Blocks.Rent<int>(registered),
        Keys = Blocks.Rent<ulong>(registered),
        Sorted = Blocks.Rent<SegmentSpec>(registered),
        Runs = Blocks.Rent<CoalescedRun>(registered),
    };

    /// <summary>Gives the blocks back.</summary>
    internal readonly void Return()
    {
        Blocks.Return(Runs);
        Blocks.Return(Sorted);
        Blocks.Return(Keys);
        Blocks.Return(Order);
    }
}
