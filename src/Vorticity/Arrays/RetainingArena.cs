using System;
using System.Threading;

namespace Vorticity.Arrays;

/// <summary>
/// An arena that takes part in retention: a retained chunk's, which names what it holds once
/// published, or a batch's, which keeps the names of the retained nodes lent to it, so that a
/// consumer can tell two batches view the same retained values.
/// </summary>
/// <remarks>
/// <para>
/// Only arenas a scan keeps from one batch or scan to the next are made this way, so that the
/// arena of a batch owned by its caller or of a context thrown away after one read costs nothing
/// more.
/// </para>
/// <para>
/// A batch's arena also keeps what a consumer derived from retained values, a filter's answers
/// over a dictionary's values for one: the lane that reads the next batch of the chunk reads it
/// through the same arena, and finds them there. Kept under the values' origin, which is exact, so
/// the reset of every batch keeps them.
/// </para>
/// </remarks>
internal sealed class RetainingArena : CanonicalArena
{
    /// <summary>How many consumers' derivations the arena keeps at once, a filter's dictionary predicates.</summary>
    private const int KeptEntries = 4;

    private static long s_generations;

    // What consumers derived from retained values, kept for the batches after the one that derived
    // it, and the entry the next newcomer replaces.
    private Kept[]? _kept;
    private int _nextKept;

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

    /// <summary>
    /// The bytes <paramref name="owner"/> derived from the retained values <paramref name="origin"/>
    /// names, when a batch before this one kept them.
    /// </summary>
    /// <param name="owner">Whoever derived them, compared by reference.</param>
    /// <param name="origin">The values they were derived from.</param>
    /// <param name="bytes">What was kept.</param>
    internal bool TryKept(object owner, CanonicalOrigin origin, out ReadOnlySpan<byte> bytes)
    {
        if (_kept is { } kept && origin.IsKnown)
        {
            for (int i = 0; i < kept.Length; i++)
            {
                ref Kept entry = ref kept[i];
                if (ReferenceEquals(entry.Owner, owner) && entry.Derived && entry.Origin == origin)
                {
                    bytes = entry.Bytes!.AsSpan(0, entry.Length);
                    return true;
                }
            }
        }

        bytes = default;
        return false;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to what <paramref name="owner"/> has spent on the values
    /// <paramref name="origin"/> names without deriving anything from them, and returns the sum: a
    /// consumer derives once what it has spent enough to pay for, and not before.
    /// </summary>
    /// <param name="owner">Whoever spends, compared by reference.</param>
    /// <param name="origin">The values, a known origin.</param>
    /// <param name="amount">What this batch spent.</param>
    internal long Spend(object owner, CanonicalOrigin origin, long amount)
    {
        ref Kept entry = ref EntryOf(owner);
        if (entry.Derived || entry.Origin != origin)
        {
            entry.Derived = false;
            entry.Origin = origin;
            entry.Spent = 0;
        }

        entry.Spent += amount;
        return entry.Spent;
    }

    /// <summary>
    /// <paramref name="length"/> bytes for <paramref name="owner"/> to derive into, which answer for
    /// no values until <see cref="Keep"/> names them, so that a derivation that fails keeps nothing.
    /// </summary>
    /// <param name="owner">Whoever derives them, compared by reference.</param>
    /// <param name="length">How many bytes.</param>
    internal Span<byte> KeepFor(object owner, int length)
    {
        ref Kept entry = ref EntryOf(owner);
        entry.Derived = false;
        entry.Origin = default;
        entry.Length = length;
        if (entry.Bytes is null || entry.Bytes.Length < length)
        {
            entry.Bytes = new byte[length];
        }

        return entry.Bytes.AsSpan(0, length);
    }

    /// <summary>Names the values <paramref name="owner"/>'s bytes from <see cref="KeepFor"/> were derived from.</summary>
    /// <param name="owner">Whoever derived them.</param>
    /// <param name="origin">The values.</param>
    internal void Keep(object owner, CanonicalOrigin origin)
    {
        ref Kept entry = ref EntryOf(owner);
        entry.Origin = origin;
        entry.Derived = true;
    }

    /// <summary>Lets go of every consumer that derived something, keeping the bytes for the next.</summary>
    internal void ForgetKept()
    {
        if (_kept is { } kept)
        {
            for (int i = 0; i < kept.Length; i++)
            {
                kept[i].Owner = null;
                kept[i].Origin = default;
                kept[i].Derived = false;
            }
        }
    }

    /// <summary>The entry of <paramref name="owner"/>: its own, else a free one, else the next in turn.</summary>
    private ref Kept EntryOf(object owner)
    {
        Kept[] kept = _kept ??= new Kept[KeptEntries];
        for (int i = 0; i < kept.Length; i++)
        {
            if (ReferenceEquals(kept[i].Owner, owner))
            {
                return ref kept[i];
            }
        }

        int slot = -1;
        for (int i = 0; i < kept.Length && slot < 0; i++)
        {
            if (kept[i].Owner is null)
            {
                slot = i;
            }
        }

        if (slot < 0)
        {
            slot = _nextKept;
            _nextKept = (slot + 1) % kept.Length;
        }

        ref Kept entry = ref kept[slot];
        entry.Owner = owner;
        entry.Origin = default;
        entry.Derived = false;
        entry.Spent = 0;
        return ref entry;
    }

    /// <summary>
    /// One consumer's entry: its owner, the values it answers for, whether its bytes are derived
    /// from them or what it has spent on them so far, and its bytes.
    /// </summary>
    private struct Kept
    {
        internal object? Owner;
        internal CanonicalOrigin Origin;
        internal bool Derived;
        internal long Spent;
        internal byte[]? Bytes;
        internal int Length;
    }
}
