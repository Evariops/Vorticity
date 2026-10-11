using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A growable run of contiguous bytes over a block of the engine's pool: a page being staged or
/// compressed, a footer being serialized.
/// </summary>
/// <remarks>
/// It grows by doubling into a new block and gives the old one back, and keeps its block across
/// <see cref="Clear"/>, so a writer's buffers reach their working size once and are reused for every
/// page after. The blocks are the engine's, which declares nothing to the GC: a writer's buffers are
/// bounded by its row group, and declaring each one as it grew made a write of a million rows collect
/// its oldest generation four times. Not thread-safe: each belongs to one column writer.
/// </remarks>
internal sealed unsafe class PooledBytes : IBufferWriter<byte>, IDisposable
{
    private const int MinimumCapacity = 4096;

    private readonly AlignedBufferPool _pool;
    private readonly NativeMemoryManager _view = new();
    private NativeSegmentOwner? _owner;
    private int _capacity;
    private int _length;

    internal PooledBytes(AlignedBufferPool pool) => _pool = pool;

    /// <summary>The bytes written so far.</summary>
    internal int Length => _length;

    /// <summary>The bytes written so far, valid until the next write.</summary>
    internal ReadOnlyMemory<byte> Written => _view.Memory[.._length];

    /// <summary>The bytes written so far, writable in place.</summary>
    internal Span<byte> WrittenSpan => _view.GetSpan()[.._length];

    /// <summary>Forgets what was written and keeps the block.</summary>
    internal void Clear() => _length = 0;

    /// <summary>Appends <paramref name="bytes"/>.</summary>
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _length += bytes.Length;
    }

    /// <summary>Makes room for <paramref name="count"/> more bytes and returns them, as the pool left them.</summary>
    internal Span<byte> Reserve(int count)
    {
        Span<byte> span = GetSpan(count)[..count];
        _length += count;
        return span;
    }

    /// <summary>Drops the bytes past <paramref name="length"/>.</summary>
    internal void Truncate(int length) => _length = Math.Min(_length, length);

    public void Advance(int count)
    {
        if (count < 0 || _length + count > _capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _view.Memory[_length..];
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _view.GetSpan()[_length..];
    }

    private void Ensure(int more)
    {
        long needed = (long)_length + more;
        if (needed <= _capacity)
        {
            return;
        }

        if (needed > Array.MaxLength)
        {
            throw new InvalidOperationException($"A buffer of {needed} bytes passes what one block can hold.");
        }

        long capacity = Math.Max(MinimumCapacity, (long)_capacity * 2);
        while (capacity < needed)
        {
            capacity *= 2;
        }

        NativeSegmentOwner owner = _pool.Rent((int)Math.Min(capacity, Array.MaxLength), VortexLimits.MaxAlignment);
        Span<byte> block = owner.WritableSpan;
        if (_owner is { } previous)
        {
            _view.GetSpan()[.._length].CopyTo(block);
            _pool.Return(previous);
        }

        _owner = owner;
        _capacity = block.Length;
        _view.Reset((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(block)), block.Length);
    }

    public void Dispose()
    {
        // Disposing a pooled block gives it back to its pool; the view only forgets its pointer.
        _owner?.Dispose();
        _owner = null;
        ((IDisposable)_view).Dispose();
        _capacity = 0;
        _length = 0;
    }
}
