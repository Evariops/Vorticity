using System;

namespace Vorticity.Buffers;

/// <summary>
/// <see cref="Length"/> elements of a block from <c>start</c> on: what an asynchronous method holds across its awaits
/// where it held a <see cref="ReadOnlyMemory{T}"/> over an array, the block having no memory of its own type.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <param name="block">The block the elements lie in, which the caller holds until the method is done.</param>
/// <param name="start">The first element.</param>
/// <param name="length">How many elements.</param>
internal readonly struct BlockSlice<T>(Block<T> block, int start, int length)
    where T : unmanaged
{
    /// <summary>How many elements.</summary>
    internal int Length => length;

    /// <summary>The elements, read where they lie.</summary>
    internal ReadOnlySpan<T> Span => block.AsSpan(start, length);
}
