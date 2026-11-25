using System.Collections.Generic;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// The memory the library holds in this process, by sort, as it counts it at one instant: what the questions and the jobs
/// reserved, the blocks in use, what is kept for the next operations, the rest of the process, and the limit all of it is
/// held to.
/// </summary>
/// <remarks>
/// The same numbers are on the <see cref="VortexDiagnostics.MeterName"/> meter: as gauges <c>vortex.memory.limit</c>,
/// <c>vortex.memory.ceiling</c>, <c>vortex.memory.reserved</c>, <c>vortex.memory.rented</c>, <c>vortex.memory.retained</c>
/// tagged by <c>holder</c>, <c>vortex.memory.rest</c>, <c>vortex.memory.rest.part</c> tagged by <c>part</c>,
/// <c>vortex.memory.pinned</c> and <c>vortex.dataset.handles</c>; as counters <c>vortex.memory.slabs_made</c>,
/// <c>vortex.memory.collections_asked</c> and <c>vortex.memory.refusals</c>. Each number is read on its own, so two of
/// them can disagree by what moved between the reads.
/// </remarks>
public sealed record MemoryReport
{
    /// <summary>The limit the process is held to: the collector's, the container's or the one the host set (<see cref="VortexMemory"/>), less a margin.</summary>
    public long LimitBytes { get; init; }

    /// <summary>What the reservations of every question and job may reach now: the limit, less the rest of the process.</summary>
    public long CeilingBytes { get; init; }

    /// <summary>What the questions and the jobs reserved, every budget's together.</summary>
    public long ReservedBytes { get; init; }

    /// <summary>The bytes of the blocks in use: rented out of the pool and not given back yet.</summary>
    public long RentedBytes { get; init; }

    /// <summary>The bytes kept for the next operations, every holder's together.</summary>
    public long RetainedBytes { get; init; }

    /// <summary>The bytes kept for the next operations, by holder: <c>pool</c>, <c>segments</c>, <c>datasets</c>.</summary>
    public IReadOnlyDictionary<string, long> RetainedByHolder { get; init; } = new Dictionary<string, long>();

    /// <summary>The rest of the process, as the library measures it: what is neither reserved nor its own.</summary>
    public long RestBytes { get; init; }

    /// <summary>
    /// The parts of the rest, as last read: <c>live</c>, what the heap held past the work at the last full collection;
    /// <c>uncertain</c>, what it grew by since; <c>reclaimable</c>, what the collector keeps committed and free, which a
    /// collection gives back; <c>pinned_free</c>, the free space between the arrays of the pinned heap, which none does;
    /// <c>pending</c>, what the work let go and no collection took yet; <c>container</c>, in a container, what it uses past
    /// what the library knows, which the rest is when it is more than the other parts together.
    /// </summary>
    public IReadOnlyDictionary<string, long> RestByPart { get; init; } = new Dictionary<string, long>();

    /// <summary>The pinned memory the library holds: its slabs, which its blocks are cut from, and its blocks past the pool's classes.</summary>
    public long PinnedBytes { get; init; }

    /// <summary>The slabs the library made since the process started: how fast it takes pinned memory from the collector.</summary>
    public long SlabsMade { get; init; }

    /// <summary>The full collections the library asked for since the process started, to give its work the room a collection gives back.</summary>
    public long CollectionsAsked { get; init; }

    /// <summary>The reservations refused to the work since the process started: a query failed, or a job's step gave way.</summary>
    public long Refusals { get; init; }

    /// <summary>The dataset handles open.</summary>
    public int DatasetHandles { get; init; }

    /// <summary>What the library holds now.</summary>
    internal static MemoryReport Now()
    {
        Dictionary<string, (long Bytes, int Holders)> held = HeldMemory.ByHolder();
        Dictionary<string, long> retained = new Dictionary<string, long>(held.Count, System.StringComparer.Ordinal);
        long total = 0;
        foreach (KeyValuePair<string, (long Bytes, int Holders)> holder in held)
        {
            retained[holder.Key] = holder.Value.Bytes;
            total += holder.Value.Bytes;
        }

        QueryMemoryBudget process = QueryMemoryBudget.Process;
        Dictionary<string, long> parts = new Dictionary<string, long>(6, System.StringComparer.Ordinal);
        foreach ((string part, long bytes) in RestParts())
        {
            parts[part] = bytes;
        }

        return new MemoryReport
        {
            LimitBytes = QueryMemoryBudget.ProcessLimit(),
            CeilingBytes = process.CeilingBytes,
            ReservedBytes = process.ReservedBytes,
            RentedBytes = RentedNow(),
            RetainedBytes = total,
            RetainedByHolder = retained,
            RestBytes = process.RestBytes,
            RestByPart = parts,
            PinnedBytes = PinnedNow(),
            SlabsMade = Slabs.Slab.Made,
            CollectionsAsked = QueryMemoryBudget.CollectionsAsked,
            Refusals = QueryMemoryBudget.Refusals,
            DatasetHandles = held.GetValueOrDefault("datasets").Holders,
        };
    }

    /// <summary>The parts of the process's rest, by the names the report and the gauge tag them with.</summary>
    internal static (string Part, long Bytes)[] RestParts()
    {
        (long live, long uncertain, long reclaimable, long pinnedFree, long pending, long container, _, _, _) = QueryMemoryBudget.Process.RestParts;
        return [("live", live), ("uncertain", uncertain), ("reclaimable", reclaimable), ("pinned_free", pinnedFree), ("pending", pending), ("container", container)];
    }

    /// <summary>The pinned bytes the library holds: its slabs, and its blocks that are an array of their own.</summary>
    internal static long PinnedNow() => Slabs.HeldBytes + BlockOwner.OwnBytes;

    /// <summary>
    /// The bytes of the blocks in use: every block alive, less those the pools keep for the next rents. A block its holder
    /// drops without giving it back is taken by the collector, and stays counted.
    /// </summary>
    internal static long RentedNow() => System.Math.Max(0, BlockOwner.LiveBytes - HeldMemory.BytesOf<BlockPool>(static pool => pool.KeptBlockBytes));
}
