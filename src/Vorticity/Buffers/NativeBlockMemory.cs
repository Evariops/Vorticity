using System;
using System.Buffers;

namespace Vorticity.Buffers;

/// <summary>
/// A native block as <see cref="Memory{T}"/>, so that an asynchronous read, a store's or a socket's,
/// writes straight into the block a scan decodes from.
/// </summary>
/// <remarks>
/// It owns nothing: the block's owner keeps the block alive, and makes this wrapper once, on its first
/// such read, so that a pooled block's later reads allocate nothing. The block is native and never
/// moves, so a pin is its address and an unpin does nothing.
/// </remarks>
internal sealed unsafe class NativeBlockMemory : MemoryManager<byte>
{
    private readonly NativeSegmentOwner _block;

    /// <summary>The bytes of <paramref name="block"/>, as long as its current length.</summary>
    internal NativeBlockMemory(NativeSegmentOwner block) => _block = block;

    /// <summary>The bytes <c>[offset, offset + length)</c> of the block.</summary>
    internal Memory<byte> Slice(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _block.Length - offset);
        return CreateMemory(offset, length);
    }

    public override Span<byte> GetSpan() => _block.WritableSpan;

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _block.Length);
        fixed (byte* at = GetSpan())
        {
            // The block is native: the address outlives this scope, and nothing moves it.
            return new MemoryHandle(at + elementIndex);
        }
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
