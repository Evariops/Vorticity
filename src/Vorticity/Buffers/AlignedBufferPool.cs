using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// <para>
/// A swept pool, as the ones the library rents from are, frees after a collection of the oldest
/// generation what a class no rent has asked for over a minute keeps parked, and everything
/// parked when the machine's memory load is high: what a process needed while it read some large
/// files goes back once it has moved on. A class in use keeps its blocks, and a rent pays one read
/// of a flag for it.
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
    /// flat count across classes spanning 4 kB to 32 MB means the same number buys 256 kB at the
    /// bottom and 2 GB at the top, so any count large enough to help the small classes is
    /// reckless in the large ones. This budget buys depth exactly where blocks are cheap.
    /// </remarks>
    private const int RetentionBudget = 256 * 1024;

    /// <summary>The block size above which a class keeps <see cref="LargeRetentionBudget"/> at most.</summary>
    private const int LargeBlockSize = 8 * 1024 * 1024;

    /// <summary>
    /// Bytes a class of blocks above <see cref="LargeBlockSize"/> may retain. A segment that size is
    /// a whole column chunk, which a scan reads once per column and gives back before the next, so
    /// a couple of parked blocks serve it; the floor that suits the small classes would park
    /// hundreds of megabytes here.
    /// </summary>
    private const long LargeRetentionBudget = 64L * 1024 * 1024;

    /// <summary>Largest permissible <c>maxPooledLength</c>: keeps every rounded size an int.</summary>
    private const int MaxPoolableLength = 1 << 30;

    /// <summary>
    /// Bytes one class of the graded pool may keep parked when the demand it meets asks for more
    /// than its base retention: a wide batch holds a block of each of its columns at once.
    /// </summary>
    private const long DemandClassBudget = 64L * 1024 * 1024;

    /// <summary>Blocks a class widened to its demand keeps at most, however small they are.</summary>
    private const int DemandBlocks = 4096;

    /// <summary>
    /// Bytes the pool keeps parked across every class past their base retention, whatever the
    /// demand: what the base retention of every class of the shared pool already comes to.
    /// </summary>
    private const long DemandBudget = 256L * 1024 * 1024;

    /// <summary>
    /// How long a class goes without a rent before a sweep frees what it keeps parked: the minute
    /// <c>ArrayPool&lt;T&gt;.Shared</c> gives its own buffers.
    /// </summary>
    private const long IdleMilliseconds = 60_000;

    /// <summary>The sweeps of each watched pool, counted; null until a pool is watched, so that a pool nobody watches does not look.</summary>
    private static ConditionalWeakTable<AlignedBufferPool, StrongBox<int>>? SweepCounts;

    private readonly Bucket[] _buckets;

    /// <summary>The bytes this pool keeps parked past the base retention of its classes.</summary>
    private readonly long _demandBudget;

    /// <summary>Bytes parked across every class.</summary>
    private long _parkedBytes;

    /// <summary>Blocks to retain in the class serving <paramref name="blockSize"/>.</summary>
    /// <param name="floor">The pool's base count, never reduced below <see cref="LargeBlockSize"/>.</param>
    /// <param name="blockSize">The class's block size in bytes.</param>
    /// <remarks>
    /// Past the retained set every return is dropped and the next rent has to allocate, so the
    /// cost of being a few blocks short is not the shortfall but the churn across it: a scattered
    /// read whose peak concurrent demand in the small classes exceeds the retained count allocates
    /// on nearly every segment, while a full scan that stays under it allocates nothing. Depth
    /// only helps where demand is bursty and blocks are small, which is what the budget expresses:
    /// deepest at 4 kB, halving each class up, and never below <paramref name="floor"/>. Above
    /// <see cref="LargeBlockSize"/> the count comes from <see cref="LargeRetentionBudget"/> instead:
    /// a block that large is not a burst, and allocating it fresh costs a page fault per page it
    /// is read into.
    /// </remarks>
    private static int RetainedFor(int floor, int blockSize)
        => blockSize > LargeBlockSize
            ? (int)Math.Max(1, LargeRetentionBudget / blockSize)
            : Math.Max(floor, Math.Min(64, RetentionBudget / blockSize));

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
    /// <param name="demandBudget">Bytes kept parked past the base retention of the classes.</param>
    /// <summary>Creates a pool, optionally grading retention by size class.</summary>
    private AlignedBufferPool(int maxPooledLength, int maxPerBucket, bool graded, long demandBudget = DemandBudget)
    {
        _demandBudget = demandBudget;
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
            int blockSize = MinBlockSize << i;
            int floor = graded ? RetainedFor(maxPerBucket, blockSize) : maxPerBucket;
            buckets[i] = new Bucket(floor, graded ? DemandFor(floor, blockSize) : floor);
        }

        _buckets = buckets;
    }

    /// <summary>The most blocks the class serving <paramref name="blockSize"/> keeps when its demand asks.</summary>
    /// <param name="floor">What it keeps whatever the demand.</param>
    /// <param name="blockSize">The class's block size in bytes.</param>
    /// <remarks>
    /// A class of blocks larger than <see cref="LargeBlockSize"/> keeps its floor: a block that
    /// large is a column chunk read once per column, not one of many held at once.
    /// </remarks>
    private static int DemandFor(int floor, int blockSize)
        => blockSize > LargeBlockSize
            ? floor
            : (int)Math.Max(floor, Math.Min(DemandBlocks, DemandClassBudget / blockSize));

    /// <summary>A pool whose retention is bounded by bytes: each size class keeps its share of <paramref name="maxRetainedBytes"/>.</summary>
    /// <param name="maxRetainedBytes">The most bytes the pool keeps parked across every class.</param>
    internal AlignedBufferPool(long maxRetainedBytes)
        : this(8 * 1024 * 1024, 0, graded: false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);
        long share = maxRetainedBytes / _buckets.Length;
        for (int i = 0; i < _buckets.Length; i++)
        {
            int blocks = (int)Math.Min(share / (MinBlockSize << i), 64);
            _buckets[i] = new Bucket(blocks, blocks);
        }
    }

    /// <summary>The process-wide pool used by the default segment sources.</summary>
    public static AlignedBufferPool Shared { get; }
        = new AlignedBufferPool(32 * 1024 * 1024, 8, graded: true).Swept();

    /// <summary>A pool graded as <see cref="Shared"/> is, with a budget of its own for the demand past the base retention.</summary>
    /// <param name="maxPooledLength">Requests above this length bypass the pool.</param>
    /// <param name="maxPerBucket">Blocks retained per size class, before grading.</param>
    /// <param name="demandBudget">Bytes the pool keeps parked past the base retention of its classes.</param>
    internal static AlignedBufferPool Graded(int maxPooledLength, int maxPerBucket, long demandBudget) =>
        new AlignedBufferPool(maxPooledLength, maxPerBucket, graded: true, demandBudget);

    /// <summary>Requests longer than this bypass the pool. A power of two.</summary>
    public int MaxPooledLength { get; }

    /// <summary>
    /// Blocks retained per size class. <see cref="Shared"/> keeps more of a class while the blocks
    /// of it rented at once outnumber that, within a budget in bytes per class and for the pool.
    /// </summary>
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
            Interlocked.Add(ref _parkedBytes, -capacity);
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
        for (int i = 0; i < _buckets.Length; i++)
        {
            Free(i);
        }
    }

    /// <summary>This pool, swept after each collection of the oldest generation for as long as it lives.</summary>
    /// <returns>This pool.</returns>
    /// <remarks>
    /// Asked once, where the pool is made: a pool is swept only once asked, so that its trimming can
    /// also be driven by hand (<see cref="TrimIdle"/>).
    /// </remarks>
    internal AlignedBufferPool Swept()
    {
        Sweeper.Register(this);
        return this;
    }

    /// <summary>Counts, from now on, the sweeps of <paramref name="pool"/>.</summary>
    /// <param name="pool">The pool to watch.</param>
    /// <returns>The count, raised by each sweep.</returns>
    internal static StrongBox<int> WatchSweeps(AlignedBufferPool pool) =>
        LazyInitializer.EnsureInitialized(ref SweepCounts).GetValue(pool, static _ => new StrongBox<int>());

    /// <summary>
    /// Frees what the classes keep parked: the blocks of every class no rent has asked for over
    /// <see cref="IdleMilliseconds"/> before <paramref name="now"/>, or of every class when
    /// <paramref name="everything"/>. A class asked for since the last sweep starts its idle time
    /// at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The time of the sweep, in the milliseconds of <see cref="Environment.TickCount64"/>.</param>
    /// <param name="everything">Whether every parked block goes: the machine's memory load is high.</param>
    internal void TrimIdle(long now, bool everything)
    {
        for (int i = 0; i < _buckets.Length; i++)
        {
            if (_buckets[i].Idle(now, IdleMilliseconds) || everything)
            {
                Free(i);
            }
        }
    }

    /// <summary>Retains a released block, or refuses it when its bucket is full.</summary>
    /// <param name="owner">The block whose reference count just reached zero.</param>
    /// <remarks>
    /// Past its base retention a class keeps a block only while the pool as a whole is under its
    /// demand budget; the budget is read before the push and counted after it, so
    /// racing returns may overrun it by a block each, and no more.
    /// </remarks>
    internal bool TryPark(NativeSegmentOwner owner)
    {
        int index = owner.BucketIndex;
        if ((uint)index >= (uint)_buckets.Length)
        {
            return false;
        }

        int blockSize = MinBlockSize << index;
        bool widen = Volatile.Read(ref _parkedBytes) + blockSize <= _demandBudget;
        if (!_buckets[index].TryPush(owner, widen))
        {
            return false;
        }

        Interlocked.Add(ref _parkedBytes, blockSize);
        return true;
    }

    /// <summary>The bytes <see cref="Rent"/> allocates for a block of <paramref name="length"/> bytes: its size class, or the length itself past the largest.</summary>
    /// <param name="length">Length in bytes, non-negative.</param>
    internal int CapacityFor(int length) => length > MaxPooledLength ? length : RoundedSize(length);

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

    /// <summary>Frees every block class <paramref name="index"/> keeps parked.</summary>
    private void Free(int index)
    {
        Bucket bucket = _buckets[index];
        while (bucket.TryTake() is { } owner)
        {
            Interlocked.Add(ref _parkedBytes, -(MinBlockSize << index));
            owner.FreeParked();
        }
    }

    /// <summary>A sweep after a collection: the idle classes, or every class when the machine's memory load is high.</summary>
    private void Sweep()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        TrimIdle(Environment.TickCount64, memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes);
        if (SweepCounts is { } watched && watched.TryGetValue(this, out StrongBox<int>? count))
        {
            Interlocked.Increment(ref count.Value);
        }
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

    /// <summary>
    /// An object nothing references, whose finalizer runs after a collection, and after collections
    /// of the oldest generation only once it has been promoted there: each run sweeps the pool and
    /// registers the sweeper again, for as long as the pool lives.
    /// </summary>
    private sealed class Sweeper
    {
        // A handle and not a WeakReference, whose own finalizer would run with this one and let
        // the pool go first.
        private WeakGCHandle<AlignedBufferPool> _pool;

        private Sweeper(AlignedBufferPool pool)
        {
            _pool = new WeakGCHandle<AlignedBufferPool>(pool);
        }

        ~Sweeper()
        {
            if (_pool.TryGetTarget(out AlignedBufferPool? pool))
            {
                pool.Sweep();
                GC.ReRegisterForFinalize(this);
            }
            else
            {
                _pool.Dispose();
            }
        }

        internal static void Register(AlignedBufferPool pool) => _ = new Sweeper(pool);
    }

    /// <summary>One size class: a bounded LIFO stack of parked blocks.</summary>
    private sealed class Bucket
    {
        private readonly Lock _gate = new Lock();
        private readonly int _floor;
        private readonly int _ceiling;
        private NativeSegmentOwner?[] _items;
        private int _count;

        /// <summary>Whether a rent has asked for the class since the last sweep.</summary>
        private bool _rented;

        /// <summary>When the sweep that last found the class asked for ran.</summary>
        private long _idleSince;

        /// <summary>
        /// A class keeping <paramref name="floor"/> blocks, and up to <paramref name="ceiling"/>
        /// while the pool's budget allows. What it parks is what was given back, so past its floor
        /// it holds at most as many blocks as were rented at once.
        /// </summary>
        /// <param name="floor">Blocks kept whatever the budget.</param>
        /// <param name="ceiling">Blocks kept at most.</param>
        internal Bucket(int floor, int ceiling)
        {
            _floor = floor;
            _ceiling = Math.Max(floor, ceiling);
            _items = new NativeSegmentOwner?[floor];
        }

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

        /// <summary>A parked block for a rent, or null when none is parked; either way the class is in use.</summary>
        internal NativeSegmentOwner? TryPop()
        {
            lock (_gate)
            {
                // Written once per sweep, so that concurrent renters do not keep taking the cache
                // line from one another.
                if (!_rented)
                {
                    _rented = true;
                }

                return Take();
            }
        }

        /// <summary>A parked block to free, or null when none is left.</summary>
        internal NativeSegmentOwner? TryTake()
        {
            lock (_gate)
            {
                return Take();
            }
        }

        /// <summary>
        /// Whether no rent has asked for the class over <paramref name="idleAfter"/> milliseconds
        /// before <paramref name="now"/>; a class asked for since the last sweep starts its idle
        /// time at <paramref name="now"/>.
        /// </summary>
        internal bool Idle(long now, long idleAfter)
        {
            lock (_gate)
            {
                if (_rented)
                {
                    _rented = false;
                    _idleSince = now;
                    return false;
                }

                return now - _idleSince >= idleAfter;
            }
        }

        /// <summary>Parks a block given back, while the class holds fewer than it keeps.</summary>
        /// <param name="owner">The block.</param>
        /// <param name="widen">Whether the pool's budget lets the class keep more than its floor.</param>
        /// <returns>Whether the block was parked.</returns>
        internal bool TryPush(NativeSegmentOwner owner, bool widen)
        {
            lock (_gate)
            {
                if (_count >= (widen ? _ceiling : _floor))
                {
                    return false;
                }

                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, Math.Min(Math.Max(_items.Length * 2, 8), _ceiling));
                }

                _items[_count++] = owner;
                return true;
            }
        }

        private NativeSegmentOwner? Take()
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
}
