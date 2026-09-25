using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class AlignedBufferPoolTests
{
    /// <summary>
    /// The block's base address. Pointer identity is the only honest proof that the pool reused a
    /// block rather than quietly allocating a fresh one that happens to look the same.
    /// </summary>
    private static unsafe nint AddressOf(NativeSegmentOwner owner)
    {
        fixed (byte* pointer = owner.Buffer.Span)
        {
            return (nint)pointer;
        }
    }

    [Fact]
    public void Rent_return_rent_hands_back_the_very_same_block()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 4);
        try
        {
            NativeSegmentOwner first = pool.Rent(1000, 64);
            nint address = AddressOf(first);
            Assert.NotEqual(0, address);

            pool.Return(first);
            Assert.Equal(1, pool.ParkedCount(1000));

            NativeSegmentOwner second = pool.Rent(1000, 64);

            Assert.Same(first, second);
            Assert.Equal(address, AddressOf(second));
            Assert.Equal(1000, second.Length);
            Assert.Equal(0, pool.ParkedCount(1000));

            pool.Return(second);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_recycled_block_comes_back_with_a_live_reference_count()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner first = pool.Rent(64, 64);
            pool.Return(first);

            NativeSegmentOwner second = pool.Rent(64, 64);
            Assert.Same(first, second);
            Assert.Equal(1, second.RefCount);

            // A recycled owner must accept a fresh Retain/Release cycle: its counter was reset,
            // not left at the zero that parked it.
            second.Retain();
            second.Release();
            Assert.Equal(1, second.RefCount);

            pool.Return(second);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_recycled_block_still_carries_the_previous_tenants_bytes()
    {
        // The other side of "rented memory is not zeroed": here it is observable rather than
        // merely unpromised. A caller that reads a block before filling it reads the last scan's
        // segment, and no assertion anywhere may come to depend on finding a zero.
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner first = pool.Rent(64, 64);
            first.WritableSpan.Fill(0xA7);
            pool.Return(first);

            NativeSegmentOwner second = pool.Rent(64, 64);
            Assert.Same(first, second);
            Assert.Equal(0xA7, second.Buffer.Span[0]);
            Assert.Equal(0xA7, second.Buffer.Span[63]);

            pool.Return(second);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Sizes_in_the_same_class_share_a_block()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            // Everything at or below 4 KiB is one size class, so a 4096-byte read and a 1-byte
            // read are interchangeable.
            NativeSegmentOwner big = pool.Rent(4096, 64);
            nint address = AddressOf(big);
            pool.Return(big);

            NativeSegmentOwner small = pool.Rent(1, 8);

            Assert.Equal(address, AddressOf(small));
            Assert.Equal(1, small.Length);
            Assert.Equal(4096, small.Capacity);

            pool.Return(small);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Different_size_classes_do_not_share()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner small = pool.Rent(4096, 64);
            pool.Return(small);

            // 4097 rounds to 8192: a different bucket, so the parked 4 KiB block is not eligible
            // and must not be handed out with a length beyond its capacity.
            NativeSegmentOwner large = pool.Rent(4097, 64);

            Assert.NotSame(small, large);
            Assert.Equal(8192, large.Capacity);
            Assert.Equal(4097, large.Length);
            Assert.Equal(1, pool.ParkedCount(4096));

            pool.Return(large);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public unsafe void Every_pooled_block_is_64_byte_aligned(int alignment)
    {
        // The pool allocates at the 64-byte cap whatever the caller
        // asked for, which is what makes blocks interchangeable across size classes and what
        // makes coalesced reads preserve each segment's own alignment.
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner owner = pool.Rent(500, alignment);

            Assert.Equal(VortexLimits.MaxAlignment, owner.Buffer.Alignment);
            Assert.True(owner.Buffer.IsAligned);
            Assert.True(Alignment.IsAligned((void*)AddressOf(owner), VortexLimits.MaxAlignment));

            pool.Return(owner);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Requests_above_the_ceiling_bypass_the_pool()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPooledLength: 4096, maxPerBucket: 4);
        try
        {
            NativeSegmentOwner huge = pool.Rent(1_000_000, 64);

            Assert.Equal(-1, huge.BucketIndex);
            Assert.Equal(1_000_000, huge.Length);
            Assert.Equal(1_000_000, huge.Capacity);
            Assert.True(huge.Buffer.IsAligned);

            pool.Return(huge);

            // Freed outright, not retained: one outsized read must not evict the working set.
            Assert.Equal(0, pool.ParkedCount(4096));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_full_bucket_frees_the_surplus_instead_of_growing()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 2);
        try
        {
            NativeSegmentOwner a = pool.Rent(1000, 64);
            NativeSegmentOwner b = pool.Rent(1000, 64);
            NativeSegmentOwner c = pool.Rent(1000, 64);

            nint addressA = AddressOf(a);
            nint addressB = AddressOf(b);
            Assert.NotEqual(addressA, addressB);
            Assert.NotEqual(addressA, AddressOf(c));

            pool.Return(a);
            pool.Return(b);
            pool.Return(c);

            Assert.Equal(2, pool.ParkedCount(1000));

            // Exactly the two parked blocks come back; c's memory was released.
            NativeSegmentOwner first = pool.Rent(1000, 64);
            NativeSegmentOwner second = pool.Rent(1000, 64);
            HashSet<nint> recycled = [AddressOf(first), AddressOf(second)];

            Assert.Equal(2, recycled.Count);
            Assert.Contains(addressA, recycled);
            Assert.Contains(addressB, recycled);

            pool.Return(first);
            pool.Return(second);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_retained_block_is_not_recycled_until_the_last_reference_goes()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner owner = pool.Rent(2048, 64);
            nint address = AddressOf(owner);

            owner.Retain();
            pool.Return(owner);

            // A second holder still has it: recycling here would hand live memory to another
            // batch.
            Assert.Equal(0, pool.ParkedCount(2048));
            Assert.Equal(1, owner.RefCount);

            owner.Release();
            Assert.Equal(1, pool.ParkedCount(2048));

            NativeSegmentOwner recycled = pool.Rent(2048, 64);
            Assert.Equal(address, AddressOf(recycled));
            pool.Return(recycled);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Releasing_to_zero_recycles_without_an_explicit_Return()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner owner = pool.Rent(1024, 64);
            nint address = AddressOf(owner);

            // This is the RecordBatch path: the batch releases every buffer it borrowed on
            // Dispose and never talks to the pool directly.
            owner.Dispose();

            Assert.Equal(1, pool.ParkedCount(1024));

            NativeSegmentOwner recycled = pool.Rent(1024, 64);
            Assert.Equal(address, AddressOf(recycled));
            pool.Return(recycled);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Returning_the_same_block_twice_throws()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner owner = pool.Rent(1024, 64);
            pool.Return(owner);

            Assert.Throws<ObjectDisposedException>(() => pool.Return(owner));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void Return_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => AlignedBufferPool.Shared.Return(null!));

    [Fact]
    public void Return_rejects_a_block_this_pool_does_not_own()
    {
        AlignedBufferPool mine = new AlignedBufferPool();
        AlignedBufferPool theirs = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner foreign = theirs.Rent(1024, 64);
            using NativeSegmentOwner unpooled = NativeSegmentOwner.Allocate(1024, 64);

            Assert.Throws<ArgumentException>(() => mine.Return(foreign));
            Assert.Throws<ArgumentException>(() => mine.Return(unpooled));

            theirs.Return(foreign);
        }
        finally
        {
            mine.Trim();
            theirs.Trim();
        }
    }

    [Theory]
    [InlineData(-1, 64)]
    [InlineData(int.MinValue, 64)]
    [InlineData(1024, 0)]
    [InlineData(1024, 3)]
    [InlineData(1024, 128)]
    [InlineData(1024, -64)]
    public void Rent_validates_its_arguments(int length, int alignment)
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            Assert.Throws<VortexFormatException>(() => pool.Rent(length, alignment));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_zero_length_rent_is_legal()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner owner = pool.Rent(0, 64);

            Assert.True(owner.Buffer.IsEmpty);
            Assert.Equal(4096, owner.Capacity);

            pool.Return(owner);
            Assert.Equal(1, pool.ParkedCount(0));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData((1 << 30) + 1)]
    [InlineData(int.MaxValue)]
    public void The_constructor_rejects_an_impossible_ceiling(int maxPooledLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlignedBufferPool(maxPooledLength));

    [Fact]
    public void The_constructor_rejects_a_negative_bucket_size() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AlignedBufferPool(maxPerBucket: -1));

    [Fact]
    public void The_ceiling_rounds_up_to_a_power_of_two()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPooledLength: 5000);

        Assert.Equal(8192, pool.MaxPooledLength);
        Assert.Equal(8, pool.MaxPerBucket);
    }

    [Fact]
    public void A_zero_sized_bucket_disables_retention()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 0);

        NativeSegmentOwner owner = pool.Rent(1024, 64);
        pool.Return(owner);

        Assert.Equal(0, pool.ParkedCount(1024));
        Assert.Throws<ObjectDisposedException>(() => owner.Retain());
    }

    [Fact]
    public void Trim_releases_every_parked_block()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 4);

        // Rent both before returning either: returning one and immediately renting again would
        // simply pop the block back out and park a single block twice.
        NativeSegmentOwner a = pool.Rent(1000, 64);
        NativeSegmentOwner b = pool.Rent(1000, 64);
        pool.Return(a);
        pool.Return(b);
        pool.Return(pool.Rent(100_000, 64));
        Assert.Equal(2, pool.ParkedCount(1000));
        Assert.Equal(1, pool.ParkedCount(100_000));

        pool.Trim();

        Assert.Equal(0, pool.ParkedCount(1000));
        Assert.Equal(0, pool.ParkedCount(100_000));

        // Still usable afterwards.
        NativeSegmentOwner owner = pool.Rent(1000, 64);
        Assert.Equal(1000, owner.Length);
        pool.Return(owner);
        pool.Trim();
    }

    [Fact]
    public void Trim_leaves_rented_blocks_alone()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        try
        {
            NativeSegmentOwner live = pool.Rent(1024, 64);
            live.WritableSpan.Fill(0x7E);

            pool.Trim();

            Assert.Equal(0x7E, live.Buffer.Span[1023]);
            pool.Return(live);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_sweep_frees_a_class_no_rent_asked_for_over_a_minute_and_keeps_one_in_use()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 4);
        try
        {
            pool.Return(pool.Rent(4096, 64));
            pool.Return(pool.Rent(65_536, 64));
            pool.TrimIdle(now: 1_000, everything: false);
            Assert.Equal(1, pool.ParkedCount(4096));
            Assert.Equal(1, pool.ParkedCount(65_536));

            pool.Return(pool.Rent(4096, 64));
            pool.TrimIdle(now: 60_999, everything: false);
            Assert.Equal(1, pool.ParkedCount(65_536));

            pool.TrimIdle(now: 61_000, everything: false);
            Assert.Equal(1, pool.ParkedCount(4096));
            Assert.Equal(0, pool.ParkedCount(65_536));

            pool.TrimIdle(now: 120_999, everything: false);
            Assert.Equal(0, pool.ParkedCount(4096));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_sweep_under_memory_pressure_frees_every_parked_block()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 4);
        try
        {
            pool.Return(pool.Rent(4096, 64));
            pool.TrimIdle(now: 0, everything: true);
            Assert.Equal(0, pool.ParkedCount(4096));

            // Still usable afterwards.
            pool.Return(pool.Rent(4096, 64));
            Assert.Equal(1, pool.ParkedCount(4096));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void The_pools_the_library_rents_from_are_swept_by_a_full_collection()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxRetainedBytes: 1 << 20).Swept();
        AlignedMemoryPool session = new AlignedMemoryPool();
        StrongBox<int> sweeps = AlignedBufferPool.WatchSweeps(pool);
        StrongBox<int> shared = AlignedBufferPool.WatchSweeps(AlignedMemoryPool.Shared.Inner);
        StrongBox<int> sessions = AlignedBufferPool.WatchSweeps(session.Inner);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        Assert.True(Volatile.Read(ref sweeps.Value) > 0, "a pool made swept");
        Assert.True(Volatile.Read(ref shared.Value) > 0, "the shared pool");
        Assert.True(Volatile.Read(ref sessions.Value) > 0, "a session's pool");
        GC.KeepAlive(pool);
        GC.KeepAlive(session);
    }

    [Fact]
    public void A_swept_pool_nothing_else_holds_is_collected()
    {
        WeakReference pool = SweptPool();
        for (int i = 0; i < 3 && pool.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(pool.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SweptPool() => new WeakReference(new AlignedBufferPool(maxRetainedBytes: 1 << 20).Swept());

    [Fact]
    public void The_shared_pool_rents_and_recycles()
    {
        NativeSegmentOwner first = AlignedBufferPool.Shared.Rent(64 * 1024, 64);
        nint address = AddressOf(first);
        AlignedBufferPool.Shared.Return(first);

        NativeSegmentOwner second = AlignedBufferPool.Shared.Rent(64 * 1024, 64);
        Assert.Equal(address, AddressOf(second));
        AlignedBufferPool.Shared.Return(second);
    }

    [Fact]
    public void The_shared_pool_parks_a_chunk_sized_block_and_keeps_sixty_four_mebibytes_of_them_at_most()
    {
        const int large = 32 * 1024 * 1024;
        Assert.True(AlignedBufferPool.Shared.MaxPooledLength >= large);

        NativeSegmentOwner[] rented = new NativeSegmentOwner[3];
        for (int i = 0; i < rented.Length; i++)
        {
            rented[i] = AlignedBufferPool.Shared.Rent(large, 64);
        }

        int before = AlignedBufferPool.Shared.ParkedCount(large);
        foreach (NativeSegmentOwner owner in rented)
        {
            AlignedBufferPool.Shared.Return(owner);
        }

        // Two blocks of 32 MiB is the budget; the third return is freed rather than parked.
        Assert.Equal(Math.Min(2, before + rented.Length), AlignedBufferPool.Shared.ParkedCount(large));
    }

    [Fact]
    public void Concurrent_renters_never_receive_the_same_block()
    {
        AlignedBufferPool pool = new AlignedBufferPool(maxPerBucket: 4);
        try
        {
            // If two threads were ever handed the same block, the fill/verify below would see a
            // neighbour's byte. 64 workers x 32 rentals across four size classes.
            Parallel.For(0, 64, worker =>
            {
                for (int i = 0; i < 32; i++)
                {
                    int length = 512 << (i & 3);
                    NativeSegmentOwner owner = pool.Rent(length, 64);
                    byte stamp = (byte)(worker + 1);

                    Span<byte> writable = owner.WritableSpan;
                    writable.Fill(stamp);
                    Assert.Equal(length, writable.Length);
                    Assert.False(writable.ContainsAnyExcept(stamp));

                    pool.Return(owner);
                }
            });
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_graded_class_keeps_as_many_blocks_as_were_rented_at_once()
    {
        AlignedBufferPool pool = AlignedBufferPool.Graded(8 * 1024 * 1024, 8, demandBudget: 64L * 1024 * 1024);
        const int block = 128 * 1024;
        try
        {
            NativeSegmentOwner[] held = RentAll(pool, block, 40);
            ReturnAll(pool, held);
            Assert.Equal(40, pool.ParkedCount(block));

            // The next batch of the same width finds every block parked: no owner is allocated.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < held.Length; i++)
            {
                held[i] = pool.Rent(block, 64);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            ReturnAll(pool, held);
            Assert.Equal(0, allocated);
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_graded_class_widens_within_the_pools_budget_and_its_own_ceiling()
    {
        const int block = 128 * 1024;

        // A budget of 2 MiB parks sixteen of these, the eight of the floor among them.
        AlignedBufferPool tight = AlignedBufferPool.Graded(8 * 1024 * 1024, 8, demandBudget: 2L * 1024 * 1024);

        // 4 MiB blocks keep 64 MiB of themselves at most: sixteen.
        AlignedBufferPool wide = AlignedBufferPool.Graded(8 * 1024 * 1024, 8, demandBudget: 1L << 40);
        const int big = 4 * 1024 * 1024;
        try
        {
            ReturnAll(tight, RentAll(tight, block, 40));
            Assert.Equal(16, tight.ParkedCount(block));

            ReturnAll(wide, RentAll(wide, big, 20));
            Assert.Equal(16, wide.ParkedCount(big));
        }
        finally
        {
            tight.Trim();
            wide.Trim();
        }
    }

    [Fact]
    public void A_graded_class_parks_no_more_than_was_rented_at_once()
    {
        AlignedBufferPool pool = AlignedBufferPool.Graded(8 * 1024 * 1024, 8, demandBudget: 64L * 1024 * 1024);
        const int block = 128 * 1024;
        try
        {
            ReturnAll(pool, RentAll(pool, block, 40));
            pool.Trim();
            Assert.Equal(0, pool.ParkedCount(block));

            // Rented one at a time, the class never has more than one out, and one is all it holds.
            for (int i = 0; i < 20; i++)
            {
                pool.Return(pool.Rent(block, 64));
            }

            Assert.Equal(1, pool.ParkedCount(block));
            ReturnAll(pool, RentAll(pool, block, 12));
            Assert.Equal(12, pool.ParkedCount(block));
        }
        finally
        {
            pool.Trim();
        }
    }

    private static NativeSegmentOwner[] RentAll(AlignedBufferPool pool, int length, int count)
    {
        NativeSegmentOwner[] owners = new NativeSegmentOwner[count];
        for (int i = 0; i < count; i++)
        {
            owners[i] = pool.Rent(length, 64);
        }

        return owners;
    }

    private static void ReturnAll(AlignedBufferPool pool, NativeSegmentOwner[] owners)
    {
        foreach (NativeSegmentOwner owner in owners)
        {
            pool.Return(owner);
        }
    }
}
