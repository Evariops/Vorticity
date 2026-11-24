using System;
using Vorticity.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// The spare instances of a scratch the writer reuses, an FSST trainer's tables, an OnPair trainer, a
/// chunk's columns or a compression lane: one stack a type, under one lock, that the registry of kept memory empties of those no
/// writer took since its last turn, and of every one when a reservation needs the room
/// (<see cref="KeptObjects{T}"/>). An instance that is <see cref="IDisposable"/> is disposed as it goes.
/// </summary>
/// <typeparam name="T">The scratch.</typeparam>
/// <remarks>
/// The columns of a chunk are written on several threads at once, so a single spare goes to the
/// first and leaves every other thread to allocate its own, every chunk: with as many spares as
/// columns written at once, each thread finds one the chunk before gave back. A scratch is taken
/// once a column chunk. What is kept is bounded by the count; past it an instance is not kept, and
/// its giver releases it or leaves it to the collector.
/// </remarks>
internal static class SpareSlots<T>
    where T : class
{
    /// <summary>
    /// The instances kept: one a processor, since a chunk's columns are written on at most as many
    /// threads, and sixteen at least. Their tables are not read for the registry's report: they count
    /// for nothing there, as the arenas kept do.
    /// </summary>
    private static readonly KeptObjects<T> Kept = new KeptObjects<T>(
        "writer spares: " + typeof(T).Name,
        Math.Max(16, Environment.ProcessorCount),
        bytesEach: 0,
        typeof(IDisposable).IsAssignableFrom(typeof(T)) ? static spare => ((IDisposable)spare).Dispose() : null);

    /// <summary>A spare instance, which the caller owns outright until it gives it back, or null when none is kept.</summary>
    internal static T? Take() => Kept.Take();

    /// <summary>
    /// Keeps <paramref name="spare"/> for a later taker; false when as many are kept as the bound, and the
    /// caller is left to release what the instance holds.
    /// </summary>
    internal static bool Give(T spare) => Kept.Give(spare);
}
