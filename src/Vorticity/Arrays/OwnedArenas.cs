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
    private static readonly CanonicalArena?[] Kept = new CanonicalArena?[Math.Max(16, 4 * Environment.ProcessorCount)];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>The arenas kept, for tests.</summary>
    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// An arena a batch gave back, or a new one of <paramref name="capacity"/> records, its blocks counted for
    /// <paramref name="account"/> from now on.
    /// </summary>
    internal static CanonicalArena Rent(int capacity, MemoryAccount account)
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                CanonicalArena kept = Kept[--_count]!;
                Kept[_count] = null;
                kept.Account = account;
                return kept;
            }
        }

        return new CanonicalArena(capacity, account);
    }

    /// <summary>Keeps <paramref name="arena"/>, reset, for the next batch a caller owns, or leaves it past the bound.</summary>
    internal static void Return(CanonicalArena arena)
    {
        lock (Gate)
        {
            if (_count < Kept.Length)
            {
                Kept[_count++] = arena;
            }
        }
    }
}
