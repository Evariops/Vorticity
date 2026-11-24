using System;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// The arenas the batches a caller owns copy their rows into (<see cref="RecordBatch.Own"/>), kept from one such batch to the
/// next: a lookup's read ahead copies each batch that keeps a row, and the arena each copy made, its records and their
/// children, outlived the collections the copy waited through, 5 % of what the warm mix at 32 clients kept past one
/// (2026-11-08). An arena comes back reset, holding no block, and is kept up to a bound; one past it is left to the collector.
/// </summary>
internal static class OwnedArenas
{
    // Kept as memory the registry asks back: those no batch took since its last turn go, and all of them when a reservation
    // needs room. Their bytes are the arrays each grew, which no reading tells: counted for nothing, seen in the rest.
    private static readonly KeptObjects<CanonicalArena> Kept = new KeptObjects<CanonicalArena>("arenas", Math.Max(16, 4 * Environment.ProcessorCount), bytesEach: 0);

    /// <summary>The arenas kept, for tests.</summary>
    internal static int Count => Kept.Count;

    /// <summary>
    /// An arena a batch gave back, or a new one of <paramref name="capacity"/> records, its blocks counted for
    /// <paramref name="account"/> from now on.
    /// </summary>
    internal static CanonicalArena Rent(int capacity, MemoryAccount account)
    {
        if (Kept.Take() is { } kept)
        {
            kept.Account = account;
            return kept;
        }

        return new CanonicalArena(capacity, account);
    }

    /// <summary>Keeps <paramref name="arena"/>, reset, for the next batch a caller owns, or leaves it past the bound.</summary>
    internal static void Return(CanonicalArena arena) => Kept.Give(arena);
}
