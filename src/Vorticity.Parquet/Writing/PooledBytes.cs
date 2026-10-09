using System;
using System.Buffers;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A growable run of bytes over blocks of the session's memory pool: a page being staged, a column
/// chunk waiting for its row group to close, a footer being serialized.
/// </summary>
/// <remarks>
/// It grows by doubling into a new block and gives the old one back, and keeps its block across
/// <see cref="Clear"/>, so a writer's buffers reach their working size once and are reused for every
/// page and row group after. Not thread-safe: each belongs to one column writer.
/// </remarks>
internal sealed class PooledBytes : IBufferWriter<byte>, IDisposable
{
    private const int MinimumCapacity = 4096;

    private readonly MemoryPool<byte> _pool;
    private IMemoryOwner<byte>? _owner;
    private Memory<byte> _memory;
    private int _length;

    internal PooledBytes(MemoryPool<byte> pool) => _pool = pool;

    /// <summary>The bytes written so far.</summary>
    internal int Length => _length;

    /// <summary>The bytes written so far.</summary>
    internal ReadOnlyMemory<byte> Written => _memory[.._length];

    /// <summary>The bytes written so far, writable in place.</summary>
    internal Span<byte> WrittenSpan => _memory.Span[.._length];

    /// <summary>Forgets what was written and keeps the block.</summary>
    internal void Clear() => _length = 0;

    /// <summary>Appends <paramref name="bytes"/>.</summary>
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _length += bytes.Length;
    }

    /// <summary>Makes room for <paramref name="count"/> more bytes and returns them, zeroed or not as the pool left them.</summary>
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
        if (count < 0 || _length + count > _memory.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _memory[_length..];
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    private void Ensure(int more)
    {
        long needed = (long)_length + more;
        if (needed <= _memory.Length)
        {
            return;
        }

        if (needed > Array.MaxLength)
        {
            throw new InvalidOperationException($"A buffer of {needed} bytes passes what one block can hold.");
        }

        long capacity = Math.Max(MinimumCapacity, (long)_memory.Length * 2);
        while (capacity < needed)
        {
            capacity *= 2;
        }

        IMemoryOwner<byte> owner = _pool.Rent((int)Math.Min(capacity, Array.MaxLength));
        Memory<byte> memory = owner.Memory;
        _memory[.._length].CopyTo(memory);
        _owner?.Dispose();
        _owner = owner;
        _memory = memory;
    }

    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
        _memory = default;
        _length = 0;
    }
}
