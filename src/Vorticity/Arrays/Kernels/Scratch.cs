using System;
using Vorticity.Buffers;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// A transient span of <typeparamref name="T"/>: the caller's <c>stackalloc</c> when it fits, a
/// block of the pool otherwise. Dispose gives the block back; <c>using</c> supplies the <c>finally</c>.
/// A decode path must not allocate on the managed heap, and this is the single implementation of
/// that rule, so that no decoder hand-rolls a rent and return pair and forgets to return.
/// </summary>
/// <typeparam name="T">
/// Element type, which holds no reference: a block holds bytes the collector never reads. A dtype is
/// gathered as its node in its arena (<see cref="Types.DType.NodeIndex"/>), and a buffer by its
/// pointer, which a block keeps as bytes and no later renter reads before it writes.
/// </typeparam>
internal ref struct Scratch<T>
    where T : unmanaged
{
    private Block<T> _rented;
    private readonly Span<T> _span;

    /// <summary>Takes <paramref name="count"/> elements from <paramref name="stack"/> or the pool.</summary>
    /// <param name="count">Element count; must be non-negative.</param>
    /// <param name="stack">A caller-owned buffer, typically <c>stackalloc</c>.</param>
    internal Scratch(int count, Span<T> stack)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count <= stack.Length)
        {
            _rented = default;
            _span = stack[..count];
        }
        else
        {
            _rented = Blocks.Rent<T>(count);
            _span = _rented;
        }
    }

    /// <summary>The transient elements.</summary>
    internal readonly Span<T> Span => _span;

    /// <summary>Gives the block back, if there was one.</summary>
    internal void Dispose()
    {
        Blocks.Return(_rented);
        _rented = default;
    }
}
