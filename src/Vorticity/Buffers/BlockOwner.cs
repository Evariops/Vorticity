using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A <see cref="SegmentOwner"/> over an aligned block of a managed array pinned for its whole life. This is the destination
/// the I/O layer reads into: a random-access file read or a range response from a remote source scatters straight into the
/// aligned block, and the engine reads it through a pointer.
/// </summary>
/// <remarks>
/// <para>
/// The array is allocated on the pinned object heap, which never moves an object, so a pointer into it stays valid for as
/// long as the owner is reachable. The collector counts its bytes, takes them back once nothing references the owner, and
/// is the only allocator of the library's blocks: there is nothing to free by hand and no finalizer.
/// </para>
/// <para>
/// An instance handed out by <see cref="BlockPool"/> returns itself to that pool when its reference count reaches
/// zero, instead of letting its array go.
/// </para>
/// </remarks>
internal sealed class BlockOwner : SegmentOwner
{
    // Null once the block let its array go: one exchange takes it, so that two releases cannot both drop it.
    private byte[]? _array;

    // Where the aligned block starts in the array: the array is allocated a cap's worth longer, and the block starts at the
    // first byte aligned to it.
    private readonly int _offset;

    private readonly BlockPool? _pool;
    private readonly int _bucketIndex;
    private readonly int _capacity;
    private readonly byte _alignmentExponent;

    // The account the block's bytes are counted in while it is rented, given its capacity back when the block returns.
    private MemoryAccount? _account;

    // The bytes of every block alive, rented or kept by a pool: what the gauge of rented bytes is worked out from.
    private static long s_liveBytes;

    /// <summary>
    /// The next block rented by the same arena, while an arena holds this one: the arena chains
    /// its blocks through them rather than listing them in an array it would have to grow.
    /// </summary>
    internal BlockOwner? NextOwned;

    /// <summary>
    /// Whether the arena whose chain holds this block kept it from the batch before, for an
    /// allocation of its size class, and has not handed it out again.
    /// </summary>
    internal bool Idle;

    private BlockOwner(byte[] array, int offset, int capacity, int length, int alignmentExponent, BlockPool? pool, int bucketIndex)
    {
        _array = array;
        _offset = offset;
        _capacity = capacity;
        _alignmentExponent = (byte)alignmentExponent;
        _pool = pool;
        _bucketIndex = bucketIndex;
        Buffer = BufferOf(array, offset, length, alignmentExponent);
        Interlocked.Add(ref s_liveBytes, Math.Max(capacity, 1));
    }

    /// <summary>
    /// The bytes of the blocks alive, rented or kept by a pool, from their allocation to the moment they let their array go.
    /// A block its holder drops without giving it back is taken by the collector, and stays counted here.
    /// </summary>
    internal static long LiveBytes => Interlocked.Read(ref s_liveBytes);

    /// <summary>
    /// Allocates <paramref name="length"/> bytes aligned to <paramref name="alignment"/>.
    /// </summary>
    /// <param name="length">Length in bytes, non-negative.</param>
    /// <param name="alignment">A power of two in
    /// <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.</param>
    /// <returns>An owner with a reference count of 1.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="length"/> is negative, or <paramref name="alignment"/> is not a power of
    /// two within the supported cap.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The allocation failed.</exception>
    public static BlockOwner Allocate(int length, int alignment)
    {
        int exponent = CheckLengthAndAlignment(length, alignment);
        (byte[] array, int offset) = AllocRaw(length, alignment);
        return new BlockOwner(array, offset, length, length, exponent, pool: null, bucketIndex: -1);
    }

    /// <summary>A block aligned to <paramref name="alignment"/> holding a copy of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to copy.</param>
    /// <param name="alignment">A power of two in <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.</param>
    /// <returns>An owner with a reference count of 1.</returns>
    /// <exception cref="VortexFormatException"><paramref name="alignment"/> is out of range.</exception>
    public static BlockOwner CopyOf(ReadOnlySpan<byte> data, int alignment)
    {
        BlockOwner owner = Allocate(data.Length, alignment);
        data.CopyTo(owner.WritableSpan);
        return owner;
    }

    /// <summary>
    /// The bytes <c>[offset, offset + length)</c> of the block as <see cref="Memory{T}"/>, for an asynchronous read to write
    /// into before the reader sees them: a view of the array, which allocates nothing. Valid until the memory is released.
    /// </summary>
    internal Memory<byte> WritableMemory(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length - offset);
        return new Memory<byte>(Array, _offset + offset, length);
    }

    /// <summary>
    /// A writable view over the owned memory, used by the I/O layer to fill the block before the
    /// reader sees it. Valid until the memory is released.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The memory has already been released.</exception>
    public Span<byte> WritableSpan => new Span<byte>(Array, _offset, Length);

    /// <summary>Bytes actually allocated, which for a pooled block exceeds <see cref="SegmentOwner.Length"/>.</summary>
    internal int Capacity => _capacity;

    /// <summary>The pool bucket this block belongs to, or -1 when it is not poolable.</summary>
    internal int BucketIndex => _bucketIndex;

    /// <summary><see langword="true"/> when this owner was rented from <paramref name="pool"/>.</summary>
    /// <param name="pool">The candidate pool.</param>
    internal bool BelongsTo(BlockPool pool) => ReferenceEquals(_pool, pool);

    /// <inheritdoc/>
    private protected override bool IsRecycled => _pool is not null;

    /// <summary>The array, or a throw once the block let it go.</summary>
    private byte[] Array => Volatile.Read(ref _array) ?? ThrowFreed();

    /// <summary>Counts the block's capacity in <paramref name="account"/>, which took it, until the block returns.</summary>
    internal void Charge(MemoryAccount account) => _account = account;

    /// <inheritdoc/>
    protected override void FreeCore()
    {
        if (_account is { } account)
        {
            _account = null;
            account.Give(_capacity);
        }

        BlockPool? pool = _pool;
        if (pool is not null)
        {
            BlockPool.Watch?.Released(this);
        }

        // A parked owner keeps its array for the next rent; any other lets it go to the collector, which the process's
        // budget counts as let go until a collection takes it. One the pool lets go later is the registry's to count.
        if (pool is not null && pool.TryPark(this))
        {
            return;
        }

        if (Drop())
        {
            QueryMemoryBudget.BlockFreed(_capacity);
        }
    }

    /// <summary>Lets go of the array of a block that the pool popped out of a bucket during <c>Trim</c>.</summary>
    internal void FreeParked() => _ = Drop();

    /// <summary>Re-arms a parked owner for a new rental of <paramref name="length"/> bytes.</summary>
    /// <param name="length">The new segment length, at most <see cref="Capacity"/>.</param>
    internal void ResetForRent(int length)
    {
        byte[] array = Array;
        if ((uint)length > (uint)_capacity)
        {
            ThrowCapacity(length, _capacity);
        }

        Buffer = BufferOf(array, _offset, length, _alignmentExponent);
        ResetRefCount();
    }

    /// <summary>Lets the array go, once: whichever of a release and a trim comes first, the other finds nothing to drop.</summary>
    /// <returns>Whether this call let it go.</returns>
    private bool Drop()
    {
        if (Interlocked.Exchange(ref _array, null) is not null)
        {
            Interlocked.Add(ref s_liveBytes, -Math.Max(_capacity, 1));
            return true;
        }

        return false;
    }

    /// <summary>The view of <paramref name="length"/> bytes of <paramref name="array"/> from <paramref name="offset"/>, which the pinned heap never moves.</summary>
    private static unsafe VortexBuffer BufferOf(byte[] array, int offset, int length, int alignmentExponent) =>
        VortexBuffer.FromPointer((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array)) + offset, length, alignmentExponent);

    /// <summary>
    /// A pinned array of <paramref name="length"/> bytes and the place in it aligned to <paramref name="alignment"/>: the
    /// pinned heap aligns an array to a pointer only, so the array is longer by the alignment and the block starts at its
    /// first aligned byte.
    /// </summary>
    private static unsafe (byte[] Array, int Offset) AllocRaw(int length, int alignment)
    {
        // A zero-length block keeps a byte of its own, so that its address is real and its alignment meaningful.
        long padded = (long)Math.Max(length, 1) + alignment - 1;
        if (padded > System.Array.MaxLength)
        {
            ThrowTooLarge(length, alignment);
        }

        byte[] array = GC.AllocateUninitializedArray<byte>((int)padded, pinned: true);
        nint address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        return (array, (int)(Alignment.AlignUp(address, alignment) - address));
    }

    internal static BlockOwner AllocatePooled(
        BlockPool pool,
        int bucketIndex,
        int capacity,
        int length)
    {
        // The pool always allocates at the 64-byte cap, whatever the caller asked for: a block
        // aligned to 64 satisfies every legal segment alignment, so buckets stay
        // alignment-agnostic and blocks are genuinely interchangeable.
        (byte[] array, int offset) = AllocRaw(capacity, VortexLimits.MaxAlignment);
        return new BlockOwner(array, offset, capacity, length, VortexLimits.MaxAlignmentExponent, pool, bucketIndex);
    }

    internal static int CheckLengthAndAlignment(int length, int alignment)
    {
        if (length < 0)
        {
            ThrowNegativeLength(length);
        }

        if (!Alignment.IsPowerOfTwo(alignment) || alignment > VortexLimits.MaxAlignment)
        {
            ThrowAlignment(alignment);
        }

        return BitOperations.TrailingZeroCount(alignment);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNegativeLength(int length) =>
        throw new VortexFormatException($"Segment length {length} is negative.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAlignment(int alignment) =>
        throw new VortexFormatException(
            $"Segment alignment {alignment} must be a power of two in [1, " +
            $"{VortexLimits.MaxAlignment}].");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowTooLarge(int length, int alignment) =>
        throw new VortexFormatException(
            $"A {length}-byte segment aligned to {alignment} does not fit in a single managed array.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowCapacity(int length, int capacity) =>
        throw new InvalidOperationException(
            $"Cannot re-rent {length} bytes from a pooled block of {capacity} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static byte[] ThrowFreed() =>
        throw new ObjectDisposedException(
            nameof(BlockOwner),
            "The block has been released; its memory is no longer addressable.");
}
