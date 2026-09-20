using System;
using System.Buffers;

namespace Vorticity.IO;

/// <summary>
/// A <see cref="MemoryManager{T}"/> projecting a block of native memory as <see cref="Memory{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// A positional read wants a <see cref="Memory{T}"/>, and one can only come from an array, a
/// string or a <see cref="MemoryManager{T}"/>; the destination here is aligned native memory, so
/// this is the only bridge to it that adds no copy.
/// </para>
/// <para>
/// One instance is created per <c>ReadManyAsync</c> call and re-pointed at each coalesced run in
/// turn (<see cref="Reset"/>). That is safe because runs are read one at a time and each read is
/// fully awaited before the next begins: no <see cref="Memory{T}"/> handed to the runtime is ever
/// live across a <see cref="Reset"/>.
/// </para>
/// <para>
/// The memory is owned by a <see cref="Buffers.SegmentOwner"/>, never by this manager;
/// <see cref="Dispose(bool)"/> only forgets the pointer.
/// </para>
/// </remarks>
internal sealed unsafe class NativeMemoryManager : MemoryManager<byte>
{
    private byte* _pointer;
    private int _length;

    /// <summary>Points the manager at <paramref name="length"/> bytes at <paramref name="pointer"/>.</summary>
    /// <param name="pointer">Base address of memory that outlives every use of the projection.</param>
    /// <param name="length">Length in bytes, non-negative.</param>
    internal void Reset(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan() => new Span<byte>(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex), elementIndex, null);
        }

        // Native memory does not move, so there is nothing to pin and nothing to hand the GC.
        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    /// <summary>
    /// Forgets the pointer. <see cref="MemoryManager{T}"/> implements <see cref="IDisposable"/>
    /// explicitly, so this is the accessible spelling of the same thing and needs no cast.
    /// </summary>
    internal void Clear()
    {
        _pointer = null;
        _length = 0;
    }

    protected override void Dispose(bool disposing) => Clear();
}
