using System;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// The spare instances of a scratch the writer reuses, an FSST trainer's tables or a zone map's
/// buffers: a few slots, each taken by an exchange that leaves null behind and given back by one
/// that fills a null, so that threads taking and giving at once never share an instance and need no
/// lock.
/// </summary>
/// <typeparam name="T">The scratch.</typeparam>
/// <remarks>
/// The columns of a chunk are written on several threads at once, so a single slot hands its one
/// spare to the first and leaves every other thread to allocate its own, every chunk: with as many
/// slots as columns written at once, each thread finds the instance it gave back the chunk before.
/// The slots are scanned from the first, which costs a few exchanges for a scratch taken once a
/// column chunk. What the slots hold is bounded by their count; past it an instance is not kept,
/// and its giver releases it or leaves it to the collector.
/// </remarks>
internal static class SpareSlots<T>
    where T : class
{
    /// <summary>
    /// The instances kept: one a processor, since a chunk's columns are written on at most as many
    /// threads, and sixteen at least.
    /// </summary>
    private static readonly T?[] Kept = new T?[Math.Max(16, Environment.ProcessorCount)];

    /// <summary>A spare instance, which the caller owns outright until it gives it back, or null when none is kept.</summary>
    internal static T? Take()
    {
        T?[] kept = Kept;
        for (int i = 0; i < kept.Length; i++)
        {
            if (Volatile.Read(ref kept[i]) is not null && Interlocked.Exchange(ref kept[i], null) is { } spare)
            {
                return spare;
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps <paramref name="spare"/> for a later taker; false when every slot holds one, and the
    /// caller is left to release what the instance holds.
    /// </summary>
    internal static bool Give(T spare)
    {
        T?[] kept = Kept;
        for (int i = 0; i < kept.Length; i++)
        {
            if (Volatile.Read(ref kept[i]) is null && Interlocked.CompareExchange(ref kept[i], spare, null) is null)
            {
                return true;
            }
        }

        return false;
    }
}
