using System;
using System.Buffers;

namespace Vorticity.Buffers;

/// <summary>
/// A growable run of contiguous bytes in one block of the pool (<see cref="Blocks"/>): a page being staged or
/// compressed, a footer being serialized, a partial result being spilled.
/// </summary>
/// <remarks>
/// It grows by doubling into a new block and gives the old one back, and keeps its block across <see cref="Clear"/>, so
/// a writer's buffers reach their working size once and are reused for every page after. A write of a million rows takes
/// its buffers from the blocks the last one gave back rather than allocating them again. Not thread-safe: each belongs
/// to one writer.
/// </remarks>
internal sealed class BlockBytes : IBufferWriter<byte>, IDisposable
{
    private const int MinimumCapacity = 4096;

    // Whom the block's bytes are counted for, or null for none: a writer's account, which takes each
    // block before it is rented and is given it back as it goes.
    private readonly MemoryAccount? _account;

    // The block's bytes, every one of them room: a block of bytes is its own array.
    private byte[] _block = [];
    private int _length;

    // The bytes the account took for the block, 0 when none did.
    private int _counted;

    /// <summary>A run whose block nothing counts.</summary>
    internal BlockBytes()
    {
    }

    /// <summary>A run whose block <paramref name="account"/> counts while it holds it.</summary>
    internal BlockBytes(MemoryAccount account)
    {
        _account = account;
    }

    /// <summary>The bytes written so far.</summary>
    internal int Length => _length;

    /// <summary>The bytes written so far, valid until the next write.</summary>
    internal ReadOnlyMemory<byte> Written => _block.AsMemory(0, _length);

    /// <summary>The bytes written so far, writable in place.</summary>
    internal Span<byte> WrittenSpan => _block.AsSpan(0, _length);

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
        if (count < 0 || _length + count > _block.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _block.AsMemory(_length);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _block.AsSpan(_length);
    }

    private void Ensure(int more)
    {
        long needed = (long)_length + more;
        if (needed <= _block.Length)
        {
            return;
        }

        if (needed > Array.MaxLength)
        {
            throw new InvalidOperationException($"A buffer of {needed} bytes passes what one block can hold.");
        }

        long capacity = Math.Max(MinimumCapacity, (long)_block.Length * 2);
        while (capacity < needed)
        {
            capacity *= 2;
        }

        int length = (int)Math.Min(capacity, Array.MaxLength);
        if (_account is { } account && !account.TryTake(length))
        {
            throw account.Refused(length);
        }

        byte[] grown;
        try
        {
            grown = Blocks.Rent<byte>(length).Array();
        }
        catch
        {
            _account?.Give(length);
            throw;
        }

        _block.AsSpan(0, _length).CopyTo(grown);
        Give();
        _block = grown;
        _counted = _account is null ? 0 : length;
    }

    /// <summary>Gives the block back to the pool, and the bytes taken for it to the account.</summary>
    private void Give()
    {
        if (_counted > 0)
        {
            _account!.Give(_counted);
            _counted = 0;
        }

        Blocks.ReturnBytes(_block);
    }

    /// <summary>Gives the block back to the pool.</summary>
    public void Dispose()
    {
        Give();
        _block = [];
        _length = 0;
    }
}
