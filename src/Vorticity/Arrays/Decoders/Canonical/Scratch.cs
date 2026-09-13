// Phase 1 contract §1.3: no managed allocation on a decode path; `stackalloc` for small fixed
// bounds and ArrayPool<T>.Shared for transients, always returned in a finally. This is the one
// place that rule is implemented, so no decoder hand-rolls a rent/return pair and forgets the
// finally.
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// A transient span of <typeparamref name="T"/>: the caller's <c>stackalloc</c> when it fits, a
/// pooled array otherwise. Dispose returns the rental; <c>using</c> supplies the <c>finally</c>.
/// </summary>
/// <typeparam name="T">Element type; only value types are used here.</typeparam>
internal ref struct Scratch<T>
{
    /// <summary>
    /// Whether a returned rental must be wiped before the pool hands it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT IS THE POINTER THAT HAS TO GO, not the bytes. A <c>VortexBuffer</c> left in a pooled
    /// array is a raw pointer into a segment this batch no longer owns, and a <c>DType</c> holds an
    /// arena reference the next batch has no business keeping alive - so those are cleared. A span
    /// of <c>int</c> holds neither: the pool's own contract already says a renter reads only what
    /// it wrote, so wiping it buys nothing and costs a pass over the whole rental on the way out.
    /// </para>
    /// <para>
    /// Twenty of the thirty instantiations in this assembly are <c>Scratch&lt;int&gt;</c>, and the
    /// clear was showing up under <c>ArrayPool.Return</c> in the profile of a pco scan. The test is
    /// a static readonly <see cref="bool"/> over a type the JIT knows, so it folds to a constant in
    /// each instantiation and the branch disappears.
    /// </para>
    /// <para>
    /// <c>IsReferenceOrContainsReferences</c> alone would NOT be enough: it reports false for
    /// <c>VortexBuffer</c>, whose pointer is unmanaged. Requiring a primitive is the conservative
    /// side of that line - every non-primitive clears, whether or not it turns out to need to.
    /// </para>
    /// </remarks>
    private static readonly bool ClearOnReturn =
        RuntimeHelpers.IsReferenceOrContainsReferences<T>() || !typeof(T).IsPrimitive;

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

            ArrayPool<T>.Shared.Return(rented, ClearOnReturn);
        }
    }
}
