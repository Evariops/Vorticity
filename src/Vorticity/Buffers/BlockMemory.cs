using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Buffers;

/// <summary>
/// A block's elements seen as a <see cref="Memory{T}"/>, for an API that takes one, a scan's take of positions: one
/// object over the block, which the caller keeps rented until the memory is read no more.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <param name="block">The block, which the caller gives back once the memory is read no more.</param>
internal sealed unsafe class BlockMemory<T>(Block<T> block) : MemoryManager<T>
    where T : unmanaged
{
    /// <summary>The block, for a reader that takes its elements where they lie, as it would an array's.</summary>
    internal Block<T> Block => block;

    public override Span<T> GetSpan() => block.AsSpan();

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)block.Length || block.Bytes is not { } bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // The block's bytes are an array the collector may move: pinned while the handle lives.
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new MemoryHandle(Unsafe.Add<T>((void*)handle.AddrOfPinnedObject(), elementIndex), handle);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
