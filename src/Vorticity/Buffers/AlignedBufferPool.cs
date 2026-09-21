using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A thread-safe size-class pool of 64-byte aligned native blocks.
/// </summary>
/// <remarks>
/// <para>
/// Requests round up to the next power of two with a floor of 4 KiB, so a scan that reads
/// segments of slightly different sizes still hits the same bucket. Requests above
/// <see cref="MaxPooledLength"/> bypass the pool: they are allocated and freed directly, which
/// keeps a single outsized read from evicting the whole steady-state working set.
/// </para>
/// <para>
/// A rented block returns to the pool when its reference count reaches zero, so a
/// <c>RecordBatch</c> that releases everything it borrowed on disposal recycles automatically.
/// <see cref="Return"/> is the explicit form of the same thing.
/// </para>
/// <para>
/// Rented memory is <b>not</b> zeroed. Callers fill a block before reading it, exactly as with
/// <c>ArrayPool&lt;byte&gt;</c>.
/// </para>
/// <para>
/// A coalesced read is served from a block whose start is the coalesced start rounded down to
/// 64, so every block is allocated at that 64-byte cap; blocks are therefore interchangeable
/// between callers that asked for different segment alignments.
/// </para>
/// </remarks>
internal sealed class AlignedBufferPool
{
    /// <summary>Smallest size class. Below this, pooling costs more than it saves.</summary>
    private const int MinBlockSize = 4096;

    private const int MinBlockShift = 12; // log2(MinBlockSize)

    /// <summary>
    /// Bytes a single size class may retain before <see cref="RetainedFor"/> stops widening it.
    /// </summary>
    /// <remarks>
    /// Retention is bounded in bytes rather than in blocks, because bytes are what it costs. A
    /// flat count across classes spanning 4 kB to 8 MB means the same number buys 256 kB at the
    /// bottom and 512 MB at the top, so any count large enough to help the small classes is
    /// reckless in the large ones. This budget buys depth exactly where blocks are cheap.
    /// </remarks>
    private const int RetentionBudget = 256 * 1024;

    /// <summary>Largest permissible <c>maxPooledLength</c>: keeps every rounded size an int.</summary>
    private const int MaxPoolableLength = 1 << 30;

    private readonly Bucket[] _buckets;

    /// <summary>Blocks to retain in the class serving <paramref name="blockSize"/>.</summary>
    /// <param name="floor">The pool's base count, never reduced.</param>
    /// <param name="blockSize">The class's block size in bytes.</param>
    /// <remarks>
    /// Past the retained set every return is dropped and the next rent has to allocate, so the
    /// cost of being a few blocks short is not the shortfall but the churn across it: a scattered
    /// read whose peak concurrent demand in the small classes exceeds the retained count allocates
    /// on nearly every segment, while a full scan that stays under it allocates nothing. Depth
    /// only helps where demand is bursty and blocks are small, which is what the budget expresses:
    /// deepest at 4 kB, halving each class up, and never below <paramref name="floor"/>.
    /// </remarks>
    private static int RetainedFor(int floor, int blockSize)
        => Math.Max(floor, Math.Min(64, RetentionBudget / blockSize));

    /// <summary>Creates a pool.</summary>
    /// <param name="maxPooledLength">
    /// Requests above this length bypass the pool. Rounded up to a power of two; must be in
    /// <c>[4096, 2^30]</c>.
    /// </param>
    /// <param name="maxPerBucket">Blocks retained per size class. Zero disables retention.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    public AlignedBufferPool(int maxPooledLength = 8 * 1024 * 1024, int maxPerBucket = 8)
        : this(maxPooledLength, maxPerBucket, graded: false)
    {
    }

    /// <param name="graded">
    /// When set, the small classes retain more than <paramref name="maxPerBucket"/> under
    /// <see cref="RetentionBudget"/>. It stays private because the public constructor's count
    /// means exactly what it says: a pool asked for zero retains nothing, and one asked for two is
    /// never quietly handed more. Grading is a policy for <see cref="Shared"/>, not a
    /// reinterpretation of a caller's number.
    /// </param>
    /// <param name="maxPooledLength">Requests above this length bypass the pool.</param>
    /// <param name="maxPerBucket">Blocks retained per size class, before grading.</param>
    /// <summary>Creates a pool, optionally grading retention by size class.</summary>
    private AlignedBufferPool(int maxPooledLength, int maxPerBucket, bool graded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPooledLength, MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPooledLength, MaxPoolableLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPerBucket);

        int top = (int)BitOperations.RoundUpToPowerOf2((uint)maxPooledLength);
        MaxPooledLength = top;
        MaxPerBucket = maxPerBucket;

        int bucketCount = BitOperations.Log2((uint)top) - MinBlockShift + 1;
        Bucket[] buckets = new Bucket[bucketCount];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new Bucket(
                graded ? RetainedFor(maxPerBucket, MinBlockSize << i) : maxPerBucket);
        }

        _buckets = buckets;
    }

    /// <summary>A pool whose retention is bounded by bytes: each size class keeps its share of <paramref name="maxRetainedBytes"/>.</summary>
    /// <param name="maxRetainedBytes">The most bytes the pool keeps parked across every class.</param>
    internal AlignedBufferPool(long maxRetainedBytes)
        : this(8 * 1024 * 1024, 0, graded: false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);
        long share = maxRetainedBytes / _buckets.Length;
        for (int i = 0; i < _buckets.Length; i++)
        {
            long blocks = share / (MinBlockSize << i);
            _buckets[i] = new Bucket((int)Math.Min(blocks, 64));
        }
    }

    /// <summary>The process-wide pool used by the default segment sources.</summary>
    public static AlignedBufferPool Shared { get; }
        = new AlignedBufferPool(8 * 1024 * 1024, 8, graded: true);

    /// <summary>Requests longer than this bypass the pool. A power of two.</summary>
    public int MaxPooledLength { get; }

    /// <summary>Blocks retained per size class.</summary>
    public int MaxPerBucket { get; }

    /// <summary>
    /// Rents a block of at least <paramref name="length"/> bytes, whose
    /// <see cref="SegmentOwner.Buffer"/> is exactly <paramref name="length"/> bytes long.
    /// </summary>
    /// <param name="length">Length in bytes, non-negative.</param>
    /// <param name="alignment">
    /// The alignment the caller needs: a power of two in
    /// <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>. It is validated, and the returned
    /// block always satisfies it because every pooled block is allocated at the 64-byte cap.
    /// </param>
    /// <returns>An owner with a reference count of 1.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="length"/> is negative or <paramref name="alignment"/> is out of range.
    /// </exception>
    public NativeSegmentOwner Rent(int length, int alignment)
    {
        NativeSegmentOwner.CheckLengthAndAlignment(length, alignment);

        if (length > MaxPooledLength)
        {
            // Tagged with this pool but bucket -1, so Return() still accepts it and the block is
            // freed rather than retained.
            return NativeSegmentOwner.AllocatePooled(this, bucketIndex: -1, capacity: length, length: length);
        }

        int capacity = RoundedSize(length);
        int index = BucketIndexOf(capacity);
        Bucket bucket = _buckets[index];

        NativeSegmentOwner? parked = bucket.TryPop();
        if (parked is not null)
        {
            parked.ResetForRent(length);
            return parked;
        }

        return NativeSegmentOwner.AllocatePooled(this, index, capacity, length);
    }

    /// <summary>
    /// Gives back a rented block. Equivalent to a single <see cref="SegmentOwner.Release"/>: if
    /// another holder still has a <see cref="SegmentOwner.Retain"/> outstanding, the block stays
    /// live and is recycled when that holder releases.
    /// </summary>
    /// <param name="owner">A block obtained from <see cref="Rent"/> on this same pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> was not rented from this pool.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// <paramref name="owner"/> has already been fully released.
    /// </exception>
    public void Return(NativeSegmentOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!owner.BelongsTo(this))
        {
            ThrowForeign();
        }

        owner.Release();
    }

    /// <summary>Frees every retained block. Blocks currently rented out are untouched.</summary>
    public void Trim()
    {
        Bucket[] buckets = _buckets;
        for (int i = 0; i < buckets.Length; i++)
        {
            Bucket bucket = buckets[i];
            while (true)
            {
                NativeSegmentOwner? owner = bucket.TryPop();
                if (owner is null)
                {
                    break;
                }

                owner.FreeParked();
            }
        }
    }

    /// <summary>Retains a released block, or refuses it when its bucket is full.</summary>
    /// <param name="owner">The block whose reference count just reached zero.</param>
    internal bool TryPark(NativeSegmentOwner owner)
    {
        int index = owner.BucketIndex;
        if ((uint)index >= (uint)_buckets.Length)
        {
            return false;
        }

        return _buckets[index].TryPush(owner);
    }

    /// <summary>Number of blocks currently retained in the bucket serving <paramref name="length"/>.</summary>
    /// <param name="length">A length whose size class is being inspected.</param>
    /// <remarks>
    /// Nothing in the library asks a bucket how full it is; the count exists for tests. A pool
    /// that is being used answers from under the caller's feet, so the number describes an instant
    /// that has already passed — what a test on a quiesced pool wants, and what nothing else
    /// should build on.
    /// </remarks>
    internal int ParkedCount(int length)
    {
        if (length > MaxPooledLength || length < 0)
        {
            return 0;
        }

        return _buckets[BucketIndexOf(RoundedSize(length))].Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundedSize(int length) =>
        length <= MinBlockSize ? MinBlockSize : (int)BitOperations.RoundUpToPowerOf2((uint)length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketIndexOf(int roundedSize) =>
        BitOperations.Log2((uint)roundedSize) - MinBlockShift;

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowForeign() =>
        throw new ArgumentException(
            "The segment owner was not rented from this pool; returning it here would free or " +
            "recycle memory this pool does not own.",
            "owner");

    /// <summary>One size class: a bounded LIFO stack of parked blocks.</summary>
    private sealed class Bucket
    {
        private readonly Lock _gate = new Lock();
        private readonly NativeSegmentOwner?[] _items;
        private int _count;

        internal Bucket(int capacity) => _items = new NativeSegmentOwner?[capacity];

        /// <summary>
        /// How many blocks the bucket holds, read without taking the gate.
        /// </summary>
        /// <remarks>
        /// Taking the gate would buy nothing a volatile read does not: an <c>int</c> is read
        /// atomically either way, and a pusher or a popper may still run between the read and the
        /// caller looking at the value. It would only serialise a diagnostic against the pool's
        /// real traffic.
        /// </remarks>
        internal int Count => Volatile.Read(ref _count);

        internal NativeSegmentOwner? TryPop()
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return null;
                }

                int index = --_count;
                NativeSegmentOwner? owner = _items[index];
                _items[index] = null;
                return owner;
            }
        }

        internal bool TryPush(NativeSegmentOwner owner)
        {
            lock (_gate)
            {
                if (_count == _items.Length)
                {
                    return false;
                }

                _items[_count++] = owner;
                return true;
            }
        }
    }
}
