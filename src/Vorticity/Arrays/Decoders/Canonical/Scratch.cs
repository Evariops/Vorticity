// Phase 1 contract §1.3: no managed allocation on a decode path; `stackalloc` for small fixed
// bounds and ArrayPool<T>.Shared for transients, always returned in a finally. This is the one
// place that rule is implemented, so no decoder hand-rolls a rent/return pair and forgets the
// finally.
using System;
using System.Buffers;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// A transient span of <typeparamref name="T"/>: the caller's <c>stackalloc</c> when it fits, a
/// pooled array otherwise. Dispose returns the rental; <c>using</c> supplies the <c>finally</c>.
/// </summary>
/// <typeparam name="T">Element type; only value types are used here.</typeparam>
internal ref struct Scratch<T>
{
    private T[]? _rented;
    private readonly Span<T> _span;

    /// <summary>Takes <paramref name="count"/> elements from <paramref name="stack"/> or the pool.</summary>
    /// <param name="count">Element count; must be non-negative.</param>
    /// <param name="stack">A caller-owned buffer, typically <c>stackalloc</c>.</param>
    internal Scratch(int count, Span<T> stack)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count <= stack.Length)
        {
            _rented = null;
            _span = stack[..count];
        }
        else
        {
            _rented = ArrayPool<T>.Shared.Rent(count);
            _span = _rented.AsSpan(0, count);
        }
    }

    /// <summary>The transient elements.</summary>
    internal readonly Span<T> Span => _span;

    /// <summary>Returns the rental, if there was one.</summary>
    internal void Dispose()
    {
        T[]? rented = _rented;
        if (rented is not null)
        {
            _rented = null;

            // clearArray: the pool hands these to the next decode, and a VortexBuffer left behind
            // is a raw pointer into a segment this batch no longer owns.
            ArrayPool<T>.Shared.Return(rented, clearArray: true);
        }
    }
}
