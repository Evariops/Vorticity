using System;
using System.Collections.Generic;
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
        // docs/03-architecture.md §3.5: the pool allocates at the 64-byte cap whatever the caller
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
            // batch. docs/03-architecture.md §3.5, "Ownership".
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
}
