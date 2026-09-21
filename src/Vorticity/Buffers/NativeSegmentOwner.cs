using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A <see cref="SegmentOwner"/> over a block of aligned native memory. This is the destination
/// the I/O layer reads into: a random-access file read or a range response from a remote source
/// scatters straight into the aligned block.
/// </summary>
/// <remarks>
/// <para>
/// The block is obtained with <see cref="NativeMemory.AlignedAlloc"/> and returned with
/// <see cref="NativeMemory.AlignedFree"/>. A finalizer is present purely as a leak backstop: the
/// correct lifecycle is <see cref="SegmentOwner.Dispose"/> or a balanced
/// <see cref="SegmentOwner.Release"/>, and the finalizer only runs when a caller dropped the
/// owner on the floor.
/// </para>
/// <para>
/// An instance handed out by <see cref="AlignedBufferPool"/> returns itself to that pool when its
/// reference count reaches zero, instead of freeing.
/// </para>
/// </remarks>
internal sealed class NativeSegmentOwner : SegmentOwner
{
    // Held as nint rather than byte* so the free path can be a single Interlocked.Exchange: that
    // is what makes a finalizer racing an explicit Dispose incapable of double-freeing.
    private nint _pointer;

    private readonly AlignedBufferPool? _pool;
    private readonly int _bucketIndex;
    private readonly int _capacity;
    private readonly int _alignmentExponent;

    private static long s_finalizedBlocks;

    private unsafe NativeSegmentOwner(
        void* pointer,
        int capacity,
        int length,
        int alignmentExponent,
        AlignedBufferPool? pool,
        int bucketIndex)
    {
        _pointer = (nint)pointer;
        _capacity = capacity;
        _alignmentExponent = alignmentExponent;
        _pool = pool;
        _bucketIndex = bucketIndex;
        Buffer = VortexBuffer.FromPointer((byte*)pointer, length, alignmentExponent);
    }


    /// <summary>Frees the block if the owner was dropped without being released.</summary>
    ~NativeSegmentOwner()
    {
        // Never re-park into the pool from a finalizer: the pool may itself be unreachable, and a
        // resurrected owner would be handed to a caller with no live reference to it.
        if (FreeNative())
        {
            Interlocked.Increment(ref s_finalizedBlocks);
        }
    }

    /// <summary>
    /// How many blocks this process has freed from a finalizer rather than from an explicit
    /// release. A correctly used reader never increments it; the tests assert that it does move
    /// when an owner is deliberately dropped, which is the only way to prove the backstop works.
    /// </summary>
    internal static long FinalizedBlockCount => Interlocked.Read(ref s_finalizedBlocks);

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
    public static unsafe NativeSegmentOwner Allocate(int length, int alignment)
    {
        int exponent = CheckLengthAndAlignment(length, alignment);
        void* pointer = AllocRaw(length, alignment);
        return new NativeSegmentOwner(pointer, length, length, exponent, pool: null, bucketIndex: -1);
    }

    /// <summary>
    /// A writable view over the owned memory, used by the I/O layer to fill the block before the
    /// reader sees it. Valid until the memory is released.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The memory has already been released.</exception>
    public unsafe Span<byte> WritableSpan
    {
        get
        {
            nint pointer = Volatile.Read(ref _pointer);
            if (pointer == 0)
            {
                ThrowFreed();
            }

            return new Span<byte>((void*)pointer, Length);
        }
    }

    /// <summary>Bytes actually allocated, which for a pooled block exceeds <see cref="SegmentOwner.Length"/>.</summary>
    internal int Capacity => _capacity;

    /// <summary>The pool bucket this block belongs to, or -1 when it is not poolable.</summary>
    internal int BucketIndex => _bucketIndex;

    /// <summary><see langword="true"/> when this owner was rented from <paramref name="pool"/>.</summary>
    /// <param name="pool">The candidate pool.</param>
    internal bool BelongsTo(AlignedBufferPool pool) => ReferenceEquals(_pool, pool);

    /// <inheritdoc/>
    protected override void FreeCore()
    {
        AlignedBufferPool? pool = _pool;

        // A parked owner keeps both its memory and its finalizer registration: if the pool is
        // later dropped without Trim(), the finalizer is still the backstop that frees it.
        if (pool is not null && pool.TryPark(this))
        {
            return;
        }

        FreeNative();
    }

    /// <summary>Frees a block that the pool popped out of a bucket during <c>Trim</c>.</summary>
    internal void FreeParked() => FreeNative();

    /// <summary>Re-arms a parked owner for a new rental of <paramref name="length"/> bytes.</summary>
    /// <param name="length">The new segment length, at most <see cref="Capacity"/>.</param>
    internal unsafe void ResetForRent(int length)
    {
        nint pointer = Volatile.Read(ref _pointer);
        if (pointer == 0)
        {
            ThrowFreed();
        }

        if ((uint)length > (uint)_capacity)
        {
            ThrowCapacity(length, _capacity);
        }

        Buffer = VortexBuffer.FromPointer((byte*)pointer, length, _alignmentExponent);
        ResetRefCount();
    }

    /// <summary>Frees the block once. Returns whether this call is the one that freed it.</summary>
    private unsafe bool FreeNative()
    {
        // Exchange-to-zero: whichever of Dispose, Release and the finalizer arrives first is the
        // one that frees, and the losers see 0 and do nothing. This is what makes a finalizer
        // racing an explicit release incapable of double-freeing.
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer == 0)
        {
            return false;
        }

        NativeMemory.AlignedFree((void*)pointer);
        GC.SuppressFinalize(this);
        return true;
    }

    private static unsafe void* AllocRaw(int length, int alignment)
    {
        // AlignedAlloc(0, a) is implementation-defined and may return null, which the runtime
        // reports as OutOfMemory. One byte keeps a zero-length segment a real, freeable address
        // so Buffer.IsAligned and pointer identity stay meaningful.
        nuint byteCount = (nuint)Math.Max(length, 1);

        // posix_memalign rejects an alignment below sizeof(void*). Rounding up never weakens the
        // guarantee: a multiple of 8 is a multiple of 1, 2 and 4.
        nuint allocAlignment = (nuint)Math.Max(alignment, sizeof(void*));

        return NativeMemory.AlignedAlloc(byteCount, allocAlignment);
    }

    internal static unsafe NativeSegmentOwner AllocatePooled(
        AlignedBufferPool pool,
        int bucketIndex,
        int capacity,
        int length)
    {
        // The pool always allocates at the 64-byte cap, whatever the caller asked for: a block
        // aligned to 64 satisfies every legal segment alignment, so buckets stay
        // alignment-agnostic and blocks are genuinely interchangeable.
        void* pointer = AllocRaw(capacity, VortexLimits.MaxAlignment);
        return new NativeSegmentOwner(
            pointer,
            capacity,
            length,
            VortexLimits.MaxAlignmentExponent,
            pool,
            bucketIndex);
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
    private static void ThrowCapacity(int length, int capacity) =>
        throw new InvalidOperationException(
            $"Cannot re-rent {length} bytes from a pooled block of {capacity} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private void ThrowFreed() =>
        throw new ObjectDisposedException(
            nameof(NativeSegmentOwner),
            "The native block has been released; its memory is no longer addressable.");
}
