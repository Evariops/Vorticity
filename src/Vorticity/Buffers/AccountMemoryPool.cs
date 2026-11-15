using System;
using System.Buffers;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The library's pool lent as a <see cref="MemoryPool{T}"/> of bytes, every block counted for one account: for what a pipe
/// writes through, a file writer's or a segment sink's, whose segments the pipe rents as it fills and gives back as it
/// flushes.
/// </summary>
/// <remarks>
/// Each block is a managed array the runtime pins for its whole life and aligned to 64 bytes, as every block of the pool is.
/// Requests round up to a power of two from 4 KiB.
/// </remarks>
internal sealed class AccountMemoryPool : MemoryPool<byte>
{
    private readonly MemoryAccount _account;
    private long _rented;

    /// <summary>A pool whose blocks are counted for <paramref name="account"/>.</summary>
    internal AccountMemoryPool(MemoryAccount account) => _account = account;

    /// <summary>The bytes the pool lent over its life: what a pipe needed of its own beside the bytes it was lent.</summary>
    internal long Rented => Interlocked.Read(ref _rented);

    /// <inheritdoc/>
    public override int MaxBufferSize => int.MaxValue - VortexLimits.MaxAlignment;

    /// <summary>Rents a block of at least <paramref name="minBufferSize"/> bytes, aligned to 64 bytes and not zeroed.</summary>
    /// <param name="minBufferSize">The size wanted; -1 for 4 KiB.</param>
    /// <returns>The block; disposing it gives it back to the pool and its bytes to the account.</returns>
    /// <exception cref="VortexMemoryException">The account refuses the block.</exception>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        int length = minBufferSize < 0 ? 4096 : minBufferSize;
        Interlocked.Add(ref _rented, length);
        return new Lease(_account.Rent(length, VortexLimits.MaxAlignment), length);
    }

    /// <summary>Nothing to free: the blocks are the library's pool's.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private sealed unsafe class Lease : MemoryManager<byte>
    {
        private readonly int _length;
        private BlockOwner? _block;

        internal Lease(BlockOwner block, int length)
        {
            _block = block;
            _length = length;
        }

        public override Span<byte> GetSpan()
        {
            BlockOwner block = _block ?? throw new ObjectDisposedException(nameof(AccountMemoryPool), "The block has been returned to its pool.");
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

        protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _block, null)?.Release();
    }
}
