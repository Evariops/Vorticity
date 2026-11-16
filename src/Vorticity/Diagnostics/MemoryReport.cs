using System.Collections.Generic;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// The memory the library holds in this process, by sort, as it counts it at one instant: what the questions and the jobs
/// reserved, the blocks in use, what is kept for the next operations, the rest of the process, and the limit all of it is
/// held to.
/// </summary>
/// <remarks>
/// The same numbers are on the <see cref="VortexDiagnostics.MeterName"/> meter as gauges: <c>vortex.memory.limit</c>,
/// <c>vortex.memory.ceiling</c>, <c>vortex.memory.reserved</c>, <c>vortex.memory.rented</c>, <c>vortex.memory.retained</c>
/// tagged by <c>holder</c>, <c>vortex.memory.rest</c> and <c>vortex.dataset.handles</c>. Each number is read on its own, so
/// two of them can disagree by what moved between the reads.
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
        return new MemoryReport
        {
            LimitBytes = QueryMemoryBudget.ProcessLimit(),
            CeilingBytes = process.CeilingBytes,
            ReservedBytes = process.ReservedBytes,
            RentedBytes = RentedNow(),
            RetainedBytes = total,
            RetainedByHolder = retained,
            RestBytes = process.RestBytes,
            DatasetHandles = held.GetValueOrDefault("datasets").Holders,
        };
    }

    /// <summary>
    /// The bytes of the blocks in use: every block alive, less those the pools keep for the next rents. A block its holder
    /// drops without giving it back is taken by the collector, and stays counted.
    /// </summary>
    internal static long RentedNow() => System.Math.Max(0, BlockOwner.LiveBytes - HeldMemory.BytesOf<BlockPool>(static pool => pool.KeptBlockBytes));
}
