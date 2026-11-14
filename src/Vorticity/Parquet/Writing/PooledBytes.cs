using System;
using System.Buffers;
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
/// page after. The blocks are the engine's pool's, so that a write of a million rows takes its buffers from the blocks the
/// last one gave back rather than allocating them again. Not thread-safe: each belongs to one column writer.
/// </remarks>
internal sealed class PooledBytes : IBufferWriter<byte>, IDisposable
{
    private const int MinimumCapacity = 4096;

    private readonly AlignedBufferPool _pool;
    private NativeSegmentOwner? _owner;

    // The block's bytes, a view of its array.
    private Memory<byte> _block;
    private int _capacity;
    private int _length;

    internal PooledBytes(AlignedBufferPool pool) => _pool = pool;

    /// <summary>The bytes written so far.</summary>
    internal int Length => _length;

    /// <summary>The bytes written so far, valid until the next write.</summary>
    internal ReadOnlyMemory<byte> Written => _block[.._length];

    /// <summary>The bytes written so far, writable in place.</summary>
    internal Span<byte> WrittenSpan => _block.Span[.._length];

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
        return _block[_length..];
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _block.Span[_length..];
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
        Memory<byte> block = owner.WritableMemory(0, owner.Length);
        if (_owner is { } previous)
        {
            _block.Span[.._length].CopyTo(block.Span);
            _pool.Return(previous);
        }

        _owner = owner;
        _block = block;
        _capacity = block.Length;
    }

    public void Dispose()
    {
        // Disposing a pooled block gives it back to its pool.
        _owner?.Dispose();
        _owner = null;
        _block = default;
        _capacity = 0;
        _length = 0;
    }
}
