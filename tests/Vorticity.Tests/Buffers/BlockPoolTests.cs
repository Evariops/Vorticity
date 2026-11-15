using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class BlockPoolTests
{
    /// <summary>
    /// The block's base address. Pointer identity is the only honest proof that the pool reused a
    /// block rather than quietly allocating a fresh one that happens to look the same.
    /// </summary>
    internal static unsafe nint AddressOf(BlockOwner owner)
    {
        fixed (byte* pointer = owner.Buffer.Span)
        {
            return (nint)pointer;
        }
    }

    [Fact]
    public void Rent_return_rent_hands_back_the_very_same_block()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        try
        {
            BlockOwner first = pool.Rent(TestAccount.Shared,1000, 64);
            nint address = AddressOf(first);
            Assert.NotEqual(0, address);

            pool.Return(first);
            Assert.Equal(1, pool.ParkedCount(1000));

            BlockOwner second = pool.Rent(TestAccount.Shared,1000, 64);

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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner first = pool.Rent(TestAccount.Shared,64, 64);
            pool.Return(first);

            BlockOwner second = pool.Rent(TestAccount.Shared,64, 64);
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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner first = pool.Rent(TestAccount.Shared,64, 64);
            first.WritableSpan.Fill(0xA7);
            pool.Return(first);

            BlockOwner second = pool.Rent(TestAccount.Shared,64, 64);
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
        BlockPool pool = new BlockPool();
        try
        {
            // Everything at or below 4 KiB is one size class, so a 4096-byte read and a 1-byte
            // read are interchangeable.
            BlockOwner big = pool.Rent(TestAccount.Shared,4096, 64);
            nint address = AddressOf(big);
            pool.Return(big);

            BlockOwner small = pool.Rent(TestAccount.Shared,1, 8);

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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner small = pool.Rent(TestAccount.Shared,4096, 64);
            pool.Return(small);

            // 4097 rounds to 8192: a different bucket, so the parked 4 KiB block is not eligible
            // and must not be handed out with a length beyond its capacity.
            BlockOwner large = pool.Rent(TestAccount.Shared,4097, 64);

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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner owner = pool.Rent(TestAccount.Shared,500, alignment);

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
        BlockPool pool = new BlockPool(maxPooledLength: 4096, maxPerBucket: 4);
        try
        {
            BlockOwner huge = pool.Rent(TestAccount.Shared,1_000_000, 64);

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
        BlockPool pool = new BlockPool(maxPerBucket: 2);
        try
        {
            BlockOwner a = pool.Rent(TestAccount.Shared,1000, 64);
            BlockOwner b = pool.Rent(TestAccount.Shared,1000, 64);
            BlockOwner c = pool.Rent(TestAccount.Shared,1000, 64);

            nint addressA = AddressOf(a);
            nint addressB = AddressOf(b);
            Assert.NotEqual(addressA, addressB);
            Assert.NotEqual(addressA, AddressOf(c));

            pool.Return(a);
            pool.Return(b);
            pool.Return(c);

            Assert.Equal(2, pool.ParkedCount(1000));

            // Exactly the two parked blocks come back; c's memory was released.
            BlockOwner first = pool.Rent(TestAccount.Shared,1000, 64);
            BlockOwner second = pool.Rent(TestAccount.Shared,1000, 64);
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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner owner = pool.Rent(TestAccount.Shared,2048, 64);
            nint address = AddressOf(owner);

            owner.Retain();
            pool.Return(owner);

            // A second holder still has it: recycling here would hand live memory to another
            // batch.
            Assert.Equal(0, pool.ParkedCount(2048));
            Assert.Equal(1, owner.RefCount);

            owner.Release();
            Assert.Equal(1, pool.ParkedCount(2048));

            BlockOwner recycled = pool.Rent(TestAccount.Shared,2048, 64);
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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner owner = pool.Rent(TestAccount.Shared,1024, 64);
            nint address = AddressOf(owner);

            // This is the RecordBatch path: the batch releases every buffer it borrowed on
            // Dispose and never talks to the pool directly.
            owner.Dispose();

            Assert.Equal(1, pool.ParkedCount(1024));

            BlockOwner recycled = pool.Rent(TestAccount.Shared,1024, 64);
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
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner owner = pool.Rent(TestAccount.Shared,1024, 64);
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
        Assert.Throws<ArgumentNullException>(() => BlockPool.Shared.Return(null!));

    [Fact]
    public void Return_rejects_a_block_this_pool_does_not_own()
    {
        BlockPool mine = new BlockPool();
        BlockPool theirs = new BlockPool();
        try
        {
            BlockOwner foreign = theirs.Rent(TestAccount.Shared, 1024, 64);
            using BlockOwner unpooled = BlockOwner.Allocate(1024, 64);

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
        BlockPool pool = new BlockPool();
        try
        {
            Assert.Throws<VortexFormatException>(() => pool.Rent(TestAccount.Shared,length, alignment));
        }
        finally
        {
            pool.Trim();
        }
    }

    [Fact]
    public void A_zero_length_rent_is_legal()
    {
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner owner = pool.Rent(TestAccount.Shared,0, 64);

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
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPool(maxPooledLength));

    [Fact]
    public void The_constructor_rejects_a_negative_bucket_size() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlockPool(maxPerBucket: -1));

    [Fact]
    public void The_ceiling_rounds_up_to_a_power_of_two()
    {
        BlockPool pool = new BlockPool(maxPooledLength: 5000);

        Assert.Equal(8192, pool.MaxPooledLength);
        Assert.Equal(8, pool.MaxPerBucket);
    }

    [Fact]
    public void A_zero_sized_bucket_disables_retention()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 0);

        BlockOwner owner = pool.Rent(TestAccount.Shared,1024, 64);
        pool.Return(owner);

        Assert.Equal(0, pool.ParkedCount(1024));
        Assert.Throws<ObjectDisposedException>(() => owner.Retain());
    }

    [Fact]
    public void Trim_releases_every_parked_block()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 4);

        // Rent both before returning either: returning one and immediately renting again would
        // simply pop the block back out and park a single block twice.
        BlockOwner a = pool.Rent(TestAccount.Shared,1000, 64);
        BlockOwner b = pool.Rent(TestAccount.Shared,1000, 64);
        pool.Return(a);
        pool.Return(b);
        pool.Return(pool.Rent(TestAccount.Shared,100_000, 64));
        Assert.Equal(2, pool.ParkedCount(1000));
        Assert.Equal(1, pool.ParkedCount(100_000));

        pool.Trim();

        Assert.Equal(0, pool.ParkedCount(1000));
        Assert.Equal(0, pool.ParkedCount(100_000));

        // Still usable afterwards.
        BlockOwner owner = pool.Rent(TestAccount.Shared,1000, 64);
        Assert.Equal(1000, owner.Length);
        pool.Return(owner);
        pool.Trim();
    }

    [Fact]
    public void Trim_leaves_rented_blocks_alone()
    {
        BlockPool pool = new BlockPool();
        try
        {
            BlockOwner live = pool.Rent(TestAccount.Shared,1024, 64);
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
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        try
        {
            pool.Return(pool.Rent(TestAccount.Shared,4096, 64));
            pool.Return(pool.Rent(TestAccount.Shared,65_536, 64));
            pool.TrimIdle(now: 1_000, everything: false);
            Assert.Equal(1, pool.ParkedCount(4096));
            Assert.Equal(1, pool.ParkedCount(65_536));

            pool.Return(pool.Rent(TestAccount.Shared,4096, 64));
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
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        try
        {
            pool.Return(pool.Rent(TestAccount.Shared,4096, 64));
            pool.TrimIdle(now: 0, everything: true);
            Assert.Equal(0, pool.ParkedCount(4096));

            // Still usable afterwards.
            pool.Return(pool.Rent(TestAccount.Shared,4096, 64));
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
        BlockPool pool = new BlockPool(maxRetainedBytes: 1 << 20).Swept();
        AlignedMemoryPool session = new AlignedMemoryPool();
        StrongBox<int> sweeps = BlockPool.WatchSweeps(pool);
        StrongBox<int> shared = BlockPool.WatchSweeps(AlignedMemoryPool.Shared.Inner);
        StrongBox<int> sessions = BlockPool.WatchSweeps(session.Inner);
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
    private static WeakReference SweptPool() => new WeakReference(new BlockPool(maxRetainedBytes: 1 << 20).Swept());

    [Fact]
    public void A_pool_bounded_in_bytes_keeps_what_is_given_back_in_any_class_up_to_its_budget()
    {
        const int megabyte = 1024 * 1024;
        BlockPool large = new BlockPool(maxRetainedBytes: 16L * megabyte);
        BlockPool small = new BlockPool(maxRetainedBytes: megabyte);
        try
        {
            ReturnAll(large, RentAll(large, 4 * megabyte, 2));
            Assert.Equal(2, large.ParkedCount(4 * megabyte));

            // The two blocks above hold half the budget: eight blocks of a megabyte fill the rest.
            ReturnAll(large, RentAll(large, megabyte, 12));
            Assert.Equal(8, large.ParkedCount(megabyte));

            ReturnAll(small, RentAll(small, 4096, 100));
            Assert.Equal(100, small.ParkedCount(4096));
        }
        finally
        {
            large.Trim();
            small.Trim();
        }
    }

    [Fact]
    public void Concurrent_renters_never_receive_the_same_block()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        try
        {
            // If two threads were ever handed the same block, the fill/verify below would see a
            // neighbour's byte. 64 workers x 32 rentals across four size classes.
            Parallel.For(0, 64, worker =>
            {
                for (int i = 0; i < 32; i++)
                {
                    int length = 512 << (i & 3);
                    BlockOwner owner = pool.Rent(TestAccount.Shared,length, 64);
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
        BlockPool pool = BlockPool.Graded(8 * 1024 * 1024, 8, demandBudget: 64L * 1024 * 1024);
        const int block = 128 * 1024;
        try
        {
            BlockOwner[] held = RentAll(pool, block, 40);
            ReturnAll(pool, held);
            Assert.Equal(40, pool.ParkedCount(block));

            // The next batch of the same width finds every block parked: no owner is allocated.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < held.Length; i++)
            {
                held[i] = pool.Rent(TestAccount.Shared,block, 64);
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
    public void A_graded_pool_keeps_one_outsized_block_for_the_next_read_it_fits()
    {
        BlockPool pool = BlockPool.Graded(64 * 1024, 8, demandBudget: 1L << 20);
        try
        {
            // Past the classes, the block a read gets parks in the pool's one slot once given back.
            BlockOwner first = pool.Rent(TestAccount.Shared,1_000_000, 64);
            Assert.Equal(1_000_000, first.Capacity);
            pool.Return(first);
            Assert.True(pool.KeepsOutsized);

            // A shorter outsized read takes it, as long as it asked; a second block given back while
            // the slot holds one is freed.
            BlockOwner again = pool.Rent(TestAccount.Shared,900_000, 64);
            Assert.Equal(900_000, again.Length);
            Assert.Equal(1_000_000, again.Capacity);
            Assert.False(pool.KeepsOutsized);
            BlockOwner other = pool.Rent(TestAccount.Shared,500_000, 64);
            Assert.Equal(500_000, other.Capacity);
            pool.Return(again);
            pool.Return(other);
            Assert.True(pool.KeepsOutsized);

            // A longer read frees the block kept and gets one of its own length.
            BlockOwner longer = pool.Rent(TestAccount.Shared,2_000_000, 64);
            Assert.Equal(2_000_000, longer.Capacity);
            Assert.False(pool.KeepsOutsized);
            pool.Return(longer);
            Assert.True(pool.KeepsOutsized);

            // A sweep frees it once no outsized read has asked for a minute, or at once under a high
            // memory load.
            pool.TrimIdle(Environment.TickCount64, everything: false);
            Assert.True(pool.KeepsOutsized);
            pool.TrimIdle(Environment.TickCount64 + 60_000, everything: false);
            Assert.False(pool.KeepsOutsized);
            pool.Return(pool.Rent(TestAccount.Shared,1_000_000, 64));
            pool.TrimIdle(Environment.TickCount64, everything: true);
            Assert.False(pool.KeepsOutsized);
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
        BlockPool tight = BlockPool.Graded(8 * 1024 * 1024, 8, demandBudget: 2L * 1024 * 1024);

        // 4 MiB blocks keep their class's ceiling at most, 64 MiB a processor and 64 MiB at least: sixteen blocks a
        // processor, the blocks past it given back freed.
        BlockPool wide = BlockPool.Graded(8 * 1024 * 1024, 8, demandBudget: 1L << 40);
        const int big = 4 * 1024 * 1024;
        int ceiling = (int)(wide.ClassCeilingBytes(big) / big);
        Assert.Equal(16 * Environment.ProcessorCount, ceiling);
        try
        {
            ReturnAll(tight, RentAll(tight, block, 40));
            Assert.Equal(16, tight.ParkedCount(block));

            ReturnAll(wide, RentAll(wide, big, ceiling + 4));
            Assert.Equal(ceiling, wide.ParkedCount(big));
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
        BlockPool pool = BlockPool.Graded(8 * 1024 * 1024, 8, demandBudget: 64L * 1024 * 1024);
        const int block = 128 * 1024;
        try
        {
            ReturnAll(pool, RentAll(pool, block, 40));
            pool.Trim();
            Assert.Equal(0, pool.ParkedCount(block));

            // Rented one at a time, the class never has more than one out, and one is all it holds.
            for (int i = 0; i < 20; i++)
            {
                pool.Return(pool.Rent(TestAccount.Shared,block, 64));
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

    private static BlockOwner[] RentAll(BlockPool pool, int length, int count)
    {
        BlockOwner[] owners = new BlockOwner[count];
        for (int i = 0; i < count; i++)
        {
            owners[i] = pool.Rent(TestAccount.Shared,length, 64);
        }

        return owners;
    }

    private static void ReturnAll(BlockPool pool, BlockOwner[] owners)
    {
        foreach (BlockOwner owner in owners)
        {
            pool.Return(owner);
        }
    }
}

/// <summary>
/// The tests that rent from <see cref="BlockPool.Shared"/> and read back what it parks.
/// </summary>
/// <remarks>
/// The pool is the process's, and every test runs beside the others: a neighbour that rents or gives
/// back a block of the same class between a return and the rent after it hands this test another
/// block, and a parked count read around it moves by the neighbour's blocks. The tests hold only
/// while nothing else rents from the pool, so they run alone.
/// </remarks>
[CollectionDefinition(nameof(SharedPoolCollection), DisableParallelization = true)]
public sealed class SharedPoolCollection
{
}

[Collection(nameof(SharedPoolCollection))]
[Trait("Category", "RunsAlone")]
public sealed class SharedPoolTests
{
    /// <summary>
    /// Empties the pool before each test. A class already past its floor, in a pool parked past its
    /// budget, frees a block given back rather than parking it, and the next rent then takes another
    /// of the blocks the class holds: what the tests run earlier left parked would decide what these
    /// read, and xunit orders the collections differently from one run to the next.
    /// </summary>
    public SharedPoolTests() => BlockPool.Shared.Trim();

    [Fact]
    public void The_shared_pool_rents_and_recycles()
    {
        BlockOwner first = BlockPool.Shared.Rent(TestAccount.Shared,64 * 1024, 64);
        nint address = BlockPoolTests.AddressOf(first);
        BlockPool.Shared.Return(first);

        BlockOwner second = BlockPool.Shared.Rent(TestAccount.Shared,64 * 1024, 64);
        Assert.Equal(address, BlockPoolTests.AddressOf(second));
        BlockPool.Shared.Return(second);
    }

    [Fact]
    public void The_shared_pool_parks_chunk_sized_blocks_up_to_their_class_ceiling()
    {
        const int large = 32 * 1024 * 1024;
        Assert.True(BlockPool.Shared.MaxPooledLength >= large);

        // A class of blocks past 8 MiB keeps 16 MiB of them a processor, 64 MiB at least: two of these on four processors.
        int ceiling = (int)(BlockPool.Shared.ClassCeilingBytes(large) / large);
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), ceiling);
        BlockOwner[] rented = new BlockOwner[ceiling + 1];
        for (int i = 0; i < rented.Length; i++)
        {
            rented[i] = BlockPool.Shared.Rent(TestAccount.Shared,large, 64);
        }

        int before = BlockPool.Shared.ParkedCount(large);
        foreach (BlockOwner owner in rented)
        {
            BlockPool.Shared.Return(owner);
        }

        // The ceiling's blocks are the budget; a return past it is freed rather than parked.
        Assert.Equal(Math.Min(ceiling, before + rented.Length), BlockPool.Shared.ParkedCount(large));
    }

    /// <summary>
    /// A reservation a process's budget refuses first takes back the blocks the pool keeps parked for the next rents: memory
    /// a container's limit counts, and the room of the queries is what that limit leaves.
    /// </summary>
    [Fact]
    public void The_shared_pool_gives_back_what_it_parks_when_a_process_budget_refuses()
    {
        BlockOwner block = BlockPool.Shared.Rent(TestAccount.Shared,64 * 1024, 64);
        BlockPool.Shared.Return(block);
        Assert.True(BlockPool.Shared.ParkedBytes > 0);

        QueryMemoryBudget process = new QueryMemoryBudget(1 << 20, process: null);
        Assert.False(process.TryReserve(1L << 40));
        Assert.Equal(0, BlockPool.Shared.ParkedBytes);
    }
}
