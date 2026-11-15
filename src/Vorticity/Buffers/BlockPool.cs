using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A thread-safe size-class pool of 64-byte aligned blocks, each a managed array the runtime keeps pinned.
/// </summary>
/// <remarks>
/// <para>
/// Requests round up to the next power of two with a floor of 4 KiB, so a scan that reads
/// segments of slightly different sizes still hits the same bucket. Requests above
/// <see cref="MaxPooledLength"/> bypass the classes: they are allocated and freed directly, which
/// keeps a single outsized read from evicting the whole steady-state working set. The process's
/// pool, <see cref="MemoryAccount.Pool"/>, keeps one of them up to 256 MiB in a slot of its own, which
/// the next outsized read it is long enough for takes.
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
/// The process's pool keeps every block given back, with no count or budget of its own: it never keeps more than
/// was rented at once, and the registry of kept memory (<see cref="HeldMemory"/>) takes back what it keeps above its
/// floor. It lets go of the blocks that sat parked since it was last asked, after each collection of the oldest
/// generation and at each turn of the registry's slow timer; everything above its floor when a reservation needs the
/// room or the machine's memory load is high. A pool made with a count per class keeps that count, and never goes
/// below it unless trimmed.
/// </para>
/// </remarks>
internal sealed class BlockPool : IHeldMemory
{
    /// <summary>Smallest size class. Below this, pooling costs more than it saves.</summary>
    private const int MinBlockSize = 4096;

    private const int MinBlockShift = 12; // log2(MinBlockSize)

    /// <summary>
    /// 🧪 The bytes each class of the process's pool keeps whatever the registry asks: 64 blocks of 4 KiB, one of
    /// 256 KiB, none larger. Provisional, calibrated by measurement with the other floors: what a question pays when the
    /// blocks it rents were let go since the one before.
    /// </summary>
    internal const int FloorBytesPerClass = 256 * 1024;

    /// <summary>Largest permissible <c>maxPooledLength</c>: keeps every rounded size an int.</summary>
    private const int MaxPoolableLength = 1 << 30;

    /// <summary>The longest block past <see cref="MaxPooledLength"/> the process's pool keeps parked, in its one slot for them.</summary>
    /// <remarks>
    /// A segment that large is a whole column of a file another writer chunked in millions of rows,
    /// read positionally on Windows when the session keeps no mapping: freed after each scan, the next
    /// one asked the system for it again, which hands out every page anew, zeroed, as the read copies
    /// into it, and a scan of an 82 MB segment took 22 ms where it takes 17 into a block kept.
    /// </remarks>
    private const int MaxOutsizedLength = 256 * 1024 * 1024;

    /// <summary>The bucket index of a block of the outsized slot, which <see cref="TryPark"/> parks there rather than in a class.</summary>
    private const int OutsizedBucket = -2;

    private readonly Bucket[] _buckets;

    /// <summary>Whether blocks past <see cref="MaxPooledLength"/> are kept, one at a time: the process's pool's policy.</summary>
    private readonly bool _keepsOutsized;

    /// <summary>The block past <see cref="MaxPooledLength"/> kept parked, or null.</summary>
    private BlockOwner? _outsized;

    /// <summary>Whether a block past <see cref="MaxPooledLength"/> was asked for since the pool last let go of what it did not use.</summary>
    private bool _outsizedAsked;

    /// <summary>Bytes parked across every class.</summary>
    private long _parkedBytes;

    /// <summary>Creates a pool.</summary>
    /// <param name="maxPooledLength">
    /// Requests above this length bypass the pool. Rounded up to a power of two; must be in
    /// <c>[4096, 2^30]</c>.
    /// </param>
    /// <param name="maxPerBucket">Blocks retained per size class, whatever the registry asks. Zero disables retention.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    public BlockPool(int maxPooledLength = 8 * 1024 * 1024, int maxPerBucket = 8)
        : this(maxPooledLength, maxPerBucket, keepsAll: false)
    {
    }

    /// <summary>Creates a pool that keeps a count per class, or every block given back above a floor.</summary>
    /// <param name="maxPooledLength">Requests above this length bypass the pool.</param>
    /// <param name="maxPerBucket">Blocks retained per size class, whatever the registry asks; ignored when <paramref name="keepsAll"/>.</param>
    /// <param name="keepsAll">
    /// Whether the pool keeps every block given back, and one past its classes, above a floor of
    /// <see cref="FloorBytesPerClass"/> a class, for the registry to take back: the process's pool's policy. It stays
    /// private because the public constructor's count means exactly what it says: a pool asked for zero retains nothing,
    /// and one asked for two is never quietly handed more.
    /// </param>
    private BlockPool(int maxPooledLength, int maxPerBucket, bool keepsAll)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPooledLength, MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPooledLength, MaxPoolableLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPerBucket);

        int top = (int)BitOperations.RoundUpToPowerOf2((uint)maxPooledLength);
        MaxPooledLength = top;
        MaxPerBucket = keepsAll ? int.MaxValue : maxPerBucket;
        _keepsOutsized = keepsAll;

        int bucketCount = BitOperations.Log2((uint)top) - MinBlockShift + 1;
        Bucket[] buckets = new Bucket[bucketCount];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = keepsAll
                ? new Bucket(FloorBytesPerClass / (MinBlockSize << i), int.MaxValue)
                : new Bucket(maxPerBucket, maxPerBucket);
        }

        _buckets = buckets;
        HeldMemory.Register(this);
    }

    /// <inheritdoc/>
    public string Holder => "pool";

    /// <summary>The bytes the pool keeps for the next rents: its classes' and the block its outsized slot keeps.</summary>
    public long HeldBytes => ParkedBytes + (Volatile.Read(ref _outsized)?.Capacity ?? 0);

    /// <summary>
    /// The process's pool, keeping every block given back above its floors, blocks up to 32 MiB pooled: built once, by
    /// <see cref="MemoryAccount.Pool"/>, the only way to it, so that no block leaves it without an account.
    /// </summary>
    internal static BlockPool ForProcess() => KeepingAll(32 * 1024 * 1024);

    /// <summary>A pool that keeps, as the process's does, every block given back above its floors, pooling blocks up to <paramref name="maxPooledLength"/>.</summary>
    /// <param name="maxPooledLength">Requests above this length bypass the classes.</param>
    internal static BlockPool KeepingAll(int maxPooledLength) => new BlockPool(maxPooledLength, 0, keepsAll: true);

    /// <summary>Requests longer than this bypass the pool. A power of two.</summary>
    public int MaxPooledLength { get; }

    /// <summary>Blocks retained per size class: <see cref="int.MaxValue"/> for a pool that keeps every block given back.</summary>
    public int MaxPerBucket { get; }

    /// <summary>
    /// Rents a block of at least <paramref name="length"/> bytes, whose
    /// <see cref="SegmentOwner.Buffer"/> is exactly <paramref name="length"/> bytes long, its capacity counted in
    /// <paramref name="account"/> until its last holder gives it back.
    /// </summary>
    /// <param name="account">Whom the block is counted for: no rent without one.</param>
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
    /// <exception cref="VortexMemoryException">The account refuses the block's capacity: the block goes back to the pool.</exception>
    public BlockOwner Rent(MemoryAccount account, int length, int alignment)
    {
        BlockOwner owner = RentCore(length, alignment);
        if (!account.TryTake(owner.Capacity))
        {
            // Counted nowhere yet: the release parks it, or lets it go, as any block given back.
            owner.Release();
            throw account.Refused(owner.Capacity);
        }

        owner.Charge(account);
        Watch?.Rented(owner);
        return owner;
    }

    /// <summary>
    /// What is told of each block the pools rent out and of each one its last holder gives back, for a tool that counts
    /// the blocks held by the site that rented them (<c>vortex-queries --block-sites</c>); null, and nothing is.
    /// </summary>
    internal static IBlockWatch? Watch;

    /// <summary>A tool told of the blocks the pools rent out and of those given back (<see cref="Watch"/>).</summary>
    internal interface IBlockWatch
    {
        /// <summary>Block <paramref name="owner"/> rented out, on the thread that rented it.</summary>
        void Rented(BlockOwner owner);

        /// <summary>Block <paramref name="owner"/> given back by its last holder, before the pool parks or frees it.</summary>
        void Released(BlockOwner owner);
    }

    private BlockOwner RentCore(int length, int alignment)
    {
        BlockOwner.CheckLengthAndAlignment(length, alignment);

        if (length > MaxPooledLength)
        {
            if (_keepsOutsized && length <= MaxOutsizedLength)
            {
                // The one block kept past the classes, when it is long enough; a shorter one is freed,
                // and the block the read gets parks in its place once given back. Written once between two
                // lettings go, so that the reads of large segments do not keep writing the line.
                if (!Volatile.Read(ref _outsizedAsked))
                {
                    Volatile.Write(ref _outsizedAsked, true);
                }

                if (Interlocked.Exchange(ref _outsized, null) is { } kept)
                {
                    if (kept.Capacity >= length)
                    {
                        kept.ResetForRent(length);
                        return kept;
                    }

                    kept.FreeParked();
                }

                return BlockOwner.AllocatePooled(this, OutsizedBucket, capacity: length, length: length);
            }

            // Tagged with this pool but bucket -1, so Return() still accepts it and the block is
            // freed rather than retained.
            return BlockOwner.AllocatePooled(this, bucketIndex: -1, capacity: length, length: length);
        }

        int capacity = RoundedSize(length);
        int index = BucketIndexOf(capacity);
        Bucket bucket = _buckets[index];

        BlockOwner? parked = bucket.TryPop();
        if (parked is not null)
        {
            Interlocked.Add(ref _parkedBytes, -capacity);
            parked.ResetForRent(length);
            return parked;
        }

        return BlockOwner.AllocatePooled(this, index, capacity, length);
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
    public void Return(BlockOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!owner.BelongsTo(this))
        {
            ThrowForeign();
        }

        owner.Release();
    }

    /// <summary>Frees every retained block, floors included. Blocks currently rented out are untouched.</summary>
    public void Trim()
    {
        for (int i = 0; i < _buckets.Length; i++)
        {
            Free(i, int.MaxValue, aboveFloor: false);
        }

        FreeOutsized();
    }

    /// <summary>
    /// Lets go of up to <paramref name="bytes"/> of the blocks the pool keeps above its floors: the outsized slot's
    /// first, then the classes' from the largest down, whose blocks cost the least to make again for what they hold.
    /// </summary>
    /// <returns>The bytes let go.</returns>
    public long GiveBack(KeptKind kind, long bytes)
    {
        if (kind != KeptKind.Blocks || bytes <= 0)
        {
            return 0;
        }

        long given = 0;
        if (Interlocked.Exchange(ref _outsized, null) is { } kept)
        {
            given += kept.Capacity;
            kept.FreeParked();
        }

        for (int i = _buckets.Length - 1; i >= 0 && given < bytes; i--)
        {
            int size = MinBlockSize << i;
            given += (long)Free(i, BlocksFor(bytes - given, size), aboveFloor: true) * size;
        }

        return given;
    }

    /// <summary>
    /// Lets go of the blocks that sat parked since the pool was last asked, above its floors: in each class, as many as
    /// the fewest it held parked in between, which no rent took; and the outsized slot's block when no read asked for one.
    /// </summary>
    public void LetGoUnused(bool collection)
    {
        for (int i = 0; i < _buckets.Length; i++)
        {
            Free(i, _buckets[i].Unused(), aboveFloor: true);
        }

        if (!Volatile.Read(ref _outsizedAsked))
        {
            FreeOutsized();
        }

        Volatile.Write(ref _outsizedAsked, false);
    }

    /// <summary>Retains a released block, or refuses it when its bucket is full.</summary>
    /// <param name="owner">The block whose reference count just reached zero.</param>
    internal bool TryPark(BlockOwner owner)
    {
        int index = owner.BucketIndex;
        if (index == OutsizedBucket)
        {
            // The slot keeps one block; a second given back while it is taken is freed.
            return Interlocked.CompareExchange(ref _outsized, owner, null) is null;
        }

        if ((uint)index >= (uint)_buckets.Length || !_buckets[index].TryPush(owner))
        {
            return false;
        }

        Interlocked.Add(ref _parkedBytes, MinBlockSize << index);
        return true;
    }

    /// <summary>
    /// The bytes of blocks of <paramref name="capacity"/>'s class the pool keeps parked at most: what
    /// a reader may hold of the class at once and find again in the pool once it gives it back.
    /// </summary>
    /// <param name="capacity">A block's capacity, as <see cref="CapacityFor"/> gives it.</param>
    /// <remarks>
    /// A pool that keeps every block given back takes back whatever a reader holds: no bound. A block past
    /// <see cref="MaxPooledLength"/> is never parked in a class, so its class holds no more than one.
    /// </remarks>
    internal long ClassCeilingBytes(int capacity) =>
        capacity > MaxPooledLength ? capacity
            : MaxPerBucket == int.MaxValue ? long.MaxValue
            : (long)Math.Max(1, MaxPerBucket) * capacity;

    /// <summary>The bytes parked across every class: what a bench tells apart from the blocks in use.</summary>
    internal long ParkedBytes => Interlocked.Read(ref _parkedBytes);

    /// <summary>The bytes <see cref="Rent"/> allocates for a block of <paramref name="length"/> bytes: its size class, or the length itself past the largest.</summary>
    /// <param name="length">Length in bytes, non-negative.</param>
    internal int CapacityFor(int length) => length > MaxPooledLength ? length : RoundedSize(length);

    /// <summary>Number of blocks currently retained in the bucket serving <paramref name="length"/>.</summary>
    /// <param name="length">A length whose size class is being inspected.</param>
    /// <remarks>
    /// Nothing in the library asks a bucket how full it is; the count exists for tests. A pool
    /// that is being used answers from under the caller's feet, so the number describes an instant
    /// that has already passed: what a test on a quiesced pool wants, and what nothing else
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

    /// <summary>Frees the block the outsized slot keeps, if any.</summary>
    private void FreeOutsized() => Interlocked.Exchange(ref _outsized, null)?.FreeParked();

    /// <summary>Whether the outsized slot keeps a block: for tests, as <see cref="ParkedCount"/> is.</summary>
    internal bool KeepsOutsized => Volatile.Read(ref _outsized) is not null;

    /// <summary>Frees up to <paramref name="count"/> blocks class <paramref name="index"/> keeps parked, above its floor or all of them: how many.</summary>
    private int Free(int index, int count, bool aboveFloor)
    {
        Bucket bucket = _buckets[index];
        int freed = 0;
        while (freed < count && bucket.TryTake(aboveFloor) is { } owner)
        {
            Interlocked.Add(ref _parkedBytes, -(MinBlockSize << index));
            owner.FreeParked();
            freed++;
        }

        return freed;
    }

    /// <summary>The blocks of <paramref name="size"/> bytes that hold <paramref name="bytes"/>, rounded up, <see cref="int.MaxValue"/> at most.</summary>
    internal static int BlocksFor(long bytes, long size) => (int)Math.Min(int.MaxValue, (bytes / size) + (bytes % size == 0 ? 0 : 1));

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

    /// <summary>One size class: a LIFO stack of parked blocks, bounded or not, above a floor the registry leaves it.</summary>
    private sealed class Bucket
    {
        private readonly Lock _gate = new Lock();
        private readonly int _floor;
        private readonly int _ceiling;
        private BlockOwner?[] _items;
        private int _count;

        /// <summary>The fewest blocks the class held parked since it was last asked what it did not use: none of them was taken.</summary>
        private int _fewest;

        /// <summary>A class keeping up to <paramref name="ceiling"/> blocks, and <paramref name="floor"/> whatever the registry asks.</summary>
        /// <param name="floor">Blocks kept whatever the registry asks.</param>
        /// <param name="ceiling">Blocks kept at most.</param>
        internal Bucket(int floor, int ceiling)
        {
            _floor = floor;
            _ceiling = Math.Max(floor, ceiling);
            _items = new BlockOwner?[Math.Min(floor, 64)];
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

        /// <summary>A parked block for a rent, or null when none is parked.</summary>
        internal BlockOwner? TryPop()
        {
            lock (_gate)
            {
                BlockOwner? owner = Take();
                if (_count < _fewest)
                {
                    _fewest = _count;
                }

                return owner;
            }
        }

        /// <summary>A parked block to free, above the floor when <paramref name="aboveFloor"/>, or null when none is left.</summary>
        internal BlockOwner? TryTake(bool aboveFloor)
        {
            lock (_gate)
            {
                if (aboveFloor && _count <= _floor)
                {
                    return null;
                }

                BlockOwner? owner = Take();
                _fewest = Math.Min(_fewest, _count);
                return owner;
            }
        }

        /// <summary>
        /// How many blocks sat parked since the class was last asked, the fewest it held in between, and from now on
        /// counted again from what it holds.
        /// </summary>
        internal int Unused()
        {
            lock (_gate)
            {
                int unused = _fewest;
                _fewest = _count;
                return unused;
            }
        }

        /// <summary>Parks a block given back, while the class holds fewer than it keeps.</summary>
        /// <param name="owner">The block.</param>
        /// <returns>Whether the block was parked.</returns>
        internal bool TryPush(BlockOwner owner)
        {
            lock (_gate)
            {
                if (_count >= _ceiling)
                {
                    return false;
                }

                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, (int)Math.Min(Math.Max((long)_items.Length * 2, 8), _ceiling));
                }

                _items[_count++] = owner;
                return true;
            }
        }

        private BlockOwner? Take()
        {
            if (_count == 0)
            {
                return null;
            }

            int index = --_count;
            BlockOwner? owner = _items[index];
            _items[index] = null;
            return owner;
        }
    }
}
