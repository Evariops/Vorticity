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

    /// <summary>An arena a batch gave back that rents from <paramref name="pool"/>, or a new one of <paramref name="capacity"/> records.</summary>
    internal static CanonicalArena Rent(int capacity, AlignedBufferPool pool)
    {
        lock (Gate)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                CanonicalArena kept = Kept[i]!;
                if (ReferenceEquals(kept.Pool, pool))
                {
                    _count--;
                    Kept[i] = Kept[_count];
                    Kept[_count] = null;
                    return kept;
                }
            }
        }

        return new CanonicalArena(capacity, pool);
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
