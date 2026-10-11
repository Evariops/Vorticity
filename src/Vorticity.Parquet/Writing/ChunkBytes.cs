using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Writing;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A column chunk's pages while its row group is open: blocks of the engine's pool filled one after
/// the other, lent to the sink one by one when the row group closes, and never copied to grow: over a
/// file, the flush that closes the row group writes them where they lie.
/// </summary>
/// <remarks>
/// The blocks start at 64 KiB and double up to 1 MiB, so a narrow column holds little and a wide one
/// takes few blocks; they are kept across <see cref="Clear"/>, so the chunks of every row group after
/// the first take no block of their own. A write that does not fit the rest of a block starts the
/// next one, which leaves the rest unused rather than cut a span in two.
/// </remarks>
internal sealed unsafe class ChunkBytes : IBufferWriter<byte>, IDisposable
{
    private const int FirstBlock = 64 * 1024;
    private const int LargestBlock = 1024 * 1024;

    private readonly AlignedBufferPool _pool;
    private readonly List<Block> _blocks = [];

    /// <summary>The block being filled; -1 before the first write.</summary>
    private int _current = -1;

    private long _length;

    internal ChunkBytes(AlignedBufferPool pool) => _pool = pool;

    /// <summary>The bytes written since the last <see cref="Clear"/>.</summary>
    internal long Length => _length;

    /// <summary>Appends <paramref name="bytes"/>, across blocks when they do not fit one.</summary>
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            Span<byte> room = Room(1);
            int take = Math.Min(room.Length, bytes.Length);
            bytes[..take].CopyTo(room);
            Advance(take);
            bytes = bytes[take..];
        }
    }

    public void Advance(int count)
    {
        Block block = _blocks[_current];
        if (count < 0 || block.Used + count > block.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        block.Used += count;
        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Room(Math.Max(sizeHint, 1));
        Block block = _blocks[_current];
        return block.View.Memory[block.Used..];
    }

    public Span<byte> GetSpan(int sizeHint = 0) => Room(Math.Max(sizeHint, 1));

    /// <summary>
    /// Lends every block's bytes to <paramref name="sink"/>, in order: they stay as they are until the
    /// sink's next flush completes, which a caller awaits before <see cref="Clear"/>.
    /// </summary>
    internal async ValueTask LendToAsync(ISegmentSink sink, CancellationToken cancellationToken)
    {
        for (int i = 0; i <= _current; i++)
        {
            Block block = _blocks[i];
            if (block.Used > 0)
            {
                await sink.LendAsync(block.View.Memory[..block.Used], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Forgets what was written and keeps the blocks.</summary>
    internal void Clear()
    {
        for (int i = 0; i <= _current; i++)
        {
            _blocks[i].Used = 0;
        }

        _current = -1;
        _length = 0;
    }

    public void Dispose()
    {
        foreach (Block block in _blocks)
        {
            _pool.Return(block.Owner);
            block.View.Reset(null, 0);
        }

        _blocks.Clear();
        _current = -1;
        _length = 0;
    }

    /// <summary>The rest of the block being filled when it holds <paramref name="size"/> bytes, else the next block.</summary>
    private Span<byte> Room(int size)
    {
        if (_current >= 0)
        {
            Block block = _blocks[_current];
            if (block.Capacity - block.Used >= size)
            {
                return block.Span[block.Used..];
            }
        }

        _current++;
        int wanted = Math.Max(size, Math.Min(LargestBlock, FirstBlock << Math.Min(_current, 4)));
        if (_current == _blocks.Count)
        {
            _blocks.Add(Rent(wanted));
        }
        else if (_blocks[_current].Capacity < size)
        {
            _pool.Return(_blocks[_current].Owner);
            _blocks[_current] = Rent(wanted);
        }

        return _blocks[_current].Span;
    }

    private Block Rent(int length)
    {
        NativeSegmentOwner owner = _pool.Rent(length, VortexLimits.MaxAlignment);
        Span<byte> span = owner.WritableSpan;
        NativeMemoryManager view = new();
        view.Reset((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)), span.Length);
        return new Block(owner, view, span.Length);
    }

    private sealed class Block(NativeSegmentOwner owner, NativeMemoryManager view, int capacity)
    {
        internal NativeSegmentOwner Owner { get; } = owner;

        internal NativeMemoryManager View { get; } = view;

        internal int Capacity { get; } = capacity;

        internal int Used { get; set; }

        internal Span<byte> Span => View.GetSpan();
    }
}
