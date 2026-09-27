using System;
using System.Threading;

namespace Vorticity.Arrays;

/// <summary>
/// An arena that takes part in retention: a retained chunk's, which names what it holds once
/// published, or a batch's, which keeps the names of the retained nodes lent to it, so that a
/// consumer can tell two batches view the same retained values.
/// </summary>
/// <remarks>
/// Only arenas a scan keeps from one batch or scan to the next are made this way, so that the
/// arena of a batch owned by its caller or of a context thrown away after one read costs nothing
/// more.
/// </remarks>
internal sealed class RetainingArena : CanonicalArena
{
    private static long s_generations;

    // Which publication of a retained chunk this arena holds, from a count the process never
    // repeats, and 0 until it is published and again from its next reset. A published arena changes
    // no node until it is reset, so this and a node's index name what the node holds.
    private long _generation;

    // The retained node each node views, by node index, for the nodes lent from another arena, until
    // the arena is reset.
    private CanonicalOrigin[]? _origins;

    /// <summary>Creates an arena backed by <see cref="Buffers.AlignedBufferPool.Shared"/>.</summary>
    /// <param name="initialCapacity">Hint for the record array's initial size. Must be positive.</param>
    internal RetainingArena(int initialCapacity = 64)
        : base(initialCapacity)
    {
    }

    /// <summary>
    /// Names what the arena holds for the arenas its nodes are lent to, until it is reset: what a
    /// retained chunk is once published, after which it changes no node.
    /// </summary>
    internal void Seal() => _generation = Interlocked.Increment(ref s_generations);

    internal override CanonicalOrigin OriginOf(int index)
    {
        if (_origins is { } origins && (uint)index < (uint)origins.Length && origins[index].IsKnown)
        {
            return origins[index];
        }

        return _generation != 0 ? new CanonicalOrigin(_generation, index) : default;
    }

    internal override void NoteOrigin(int index, CanonicalOrigin origin)
    {
        if (!origin.IsKnown)
        {
            return;
        }

        if (_origins is null || index >= _origins.Length)
        {
            Array.Resize(ref _origins, Math.Max(index + 1, 2 * (_origins?.Length ?? 8)));
        }

        _origins[index] = origin;
    }

    private protected override void Forget(int records)
    {
        if (_origins is not null)
        {
            Array.Clear(_origins, 0, Math.Min(records, _origins.Length));
        }

        _generation = 0;
    }
}
