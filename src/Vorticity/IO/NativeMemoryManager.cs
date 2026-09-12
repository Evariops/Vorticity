// RandomAccess.ReadAsync needs a Memory<byte>, and a Memory<byte> can only come from an array, a
// string, or a MemoryManager<T>. The destination here is aligned native memory
// (docs/03-architecture.md §3.1), so a manager is the only bridge that does not introduce a copy.
using System;
using System.Buffers;

namespace Vorticity.IO;

/// <summary>
/// A <see cref="MemoryManager{T}"/> projecting a block of native memory as <see cref="Memory{T}"/>.
/// </summary>
/// <remarks>
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

    /// <inheritdoc/>
    public override Span<byte> GetSpan() => new Span<byte>(_pointer, _length);

    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex), elementIndex, null);
        }

        // Native memory does not move, so there is nothing to pin and nothing to hand the GC.
        return new MemoryHandle(_pointer + elementIndex);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) => Clear();
}
