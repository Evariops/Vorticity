using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// A pool of 64-byte aligned native blocks: the memory every batch, segment and builder buffer of a
/// session comes from.
/// </summary>
/// <remarks>
/// Blocks are native, so a <see cref="ReadOnlySpan{T}"/> over one never moves and every
/// <c>Values</c> span a column hands out is aligned for the widest vector. A block rented through
/// <see cref="Rent"/> is reported to the GC as memory pressure until it is disposed, so a process
/// under load collects as if the bytes were managed; the blocks the pool keeps parked for reuse,
/// and those the engine rents from it directly, are not. What a size class keeps parked is freed
/// after a collection of the oldest generation once no rent has asked for the class over a minute,
/// and every parked block is freed when the machine's memory load is high. Requests round up to a
/// power of two from 4 KiB; a request above 8 MiB is allocated and freed directly.
/// </remarks>
public sealed class AlignedMemoryPool : MemoryPool<byte>
{
    /// <summary>The pressure declared for the blocks of each watched pool and not taken back; null until a pool is watched, so that a pool nobody watches does not look.</summary>
    private static ConditionalWeakTable<AlignedBufferPool, StrongBox<long>>? WatchedPressure;

    private readonly int _alignment = VortexLimits.MaxAlignment;

    internal AlignedMemoryPool(AlignedBufferPool inner)
    {
        Inner = inner;
    }

    /// <summary>A pool with its own retention budget.</summary>
    /// <param name="alignment">The alignment every block <see cref="Rent"/> hands out honours; a power of two up to 64.</param>
    /// <param name="maxRetainedBytes">The most bytes the pool keeps parked for reuse, across every size class.</param>
    /// <exception cref="ArgumentOutOfRangeException">The alignment is not a power of two up to 64, or the budget is negative.</exception>
    public AlignedMemoryPool(int alignment = 64, long maxRetainedBytes = 256L * 1024 * 1024)
    {
        if (alignment <= 0 || alignment > VortexLimits.MaxAlignment || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, $"A power of two up to {VortexLimits.MaxAlignment}.");
        }

        _alignment = alignment;
        Inner = new AlignedBufferPool(maxRetainedBytes).Swept();
    }

    /// <summary>The process-wide pool, which <see cref="VortexSession.Default"/> uses.</summary>
    public static new AlignedMemoryPool Shared { get; } = new AlignedMemoryPool(AlignedBufferPool.Shared);

    /// <summary>The engine's pool behind this one.</summary>
    internal AlignedBufferPool Inner { get; }

    /// <inheritdoc/>
    public override int MaxBufferSize => int.MaxValue - VortexLimits.MaxAlignment;

    /// <summary>Rents a block of at least <paramref name="minBufferSize"/> bytes, aligned as the pool was made and not zeroed.</summary>
    /// <param name="minBufferSize">The size wanted; -1 for 4 KiB.</param>
    /// <returns>The block; disposing it returns it to the pool.</returns>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        int length = minBufferSize < 0 ? 4096 : minBufferSize;
        NativeSegmentOwner block = Inner.Rent(length, _alignment);
        block.DeclaredPressure = Math.Max(length, 1);
        Declare(Inner, block.DeclaredPressure);
        return new Lease(this, block, length);
    }

    /// <summary>Counts, from now on, the pressure <paramref name="pool"/> declares to the GC and has not taken back.</summary>
    /// <param name="pool">The pool to watch.</param>
    /// <returns>The count, in bytes.</returns>
    internal static StrongBox<long> WatchPressure(AlignedMemoryPool pool) =>
        LazyInitializer.EnsureInitialized(ref WatchedPressure).GetValue(pool.Inner, static _ => new StrongBox<long>());

    /// <summary>
    /// Declares <paramref name="bytes"/> to the GC for a block of <paramref name="pool"/>, or takes
    /// them back when negative: with the lease, or with the block when a lease was dropped.
    /// </summary>
    /// <param name="pool">The engine's pool the block was rented from.</param>
    /// <param name="bytes">The bytes, negative to take them back.</param>
    internal static void Declare(AlignedBufferPool? pool, long bytes)
    {
        if (bytes > 0)
        {
            GC.AddMemoryPressure(bytes);
        }
        else
        {
            GC.RemoveMemoryPressure(-bytes);
        }

        if (WatchedPressure is { } watched && pool is not null && watched.TryGetValue(pool, out StrongBox<long>? declared))
        {
            Interlocked.Add(ref declared.Value, bytes);
        }
    }

    /// <summary>Frees every parked block.</summary>
    protected override void Dispose(bool disposing) => Inner.Trim();

    private sealed unsafe class Lease : MemoryManager<byte>
    {
        private readonly AlignedMemoryPool _pool;
        private readonly int _length;
        private NativeSegmentOwner? _block;

        internal Lease(AlignedMemoryPool pool, NativeSegmentOwner block, int length)
        {
            _pool = pool;
            _block = block;
            _length = length;
        }

        public override Span<byte> GetSpan()
        {
            NativeSegmentOwner block = _block ?? throw new ObjectDisposedException(nameof(AlignedMemoryPool), "The block has been returned to its pool.");
            return block.WritableSpan[.._length];
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            fixed (byte* pointer = GetSpan())
            {
                return new MemoryHandle(pointer + elementIndex);
            }
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            NativeSegmentOwner? block = Interlocked.Exchange(ref _block, null);
            if (block is null)
            {
                return;
            }

            block.DeclaredPressure = 0;
            _pool.Inner.Return(block);
            Declare(_pool.Inner, -Math.Max(_length, 1));
        }
    }
}
