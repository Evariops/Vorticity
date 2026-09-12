using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class SegmentOwnerTests
{
    /// <summary>A no-memory owner used to observe exactly when <c>FreeCore</c> runs.</summary>
    private sealed class CountingOwner : SegmentOwner
    {
        private int _frees;

        public int Frees => Volatile.Read(ref _frees);

        protected override void FreeCore() => Interlocked.Increment(ref _frees);
    }

    // ---- reference counting -------------------------------------------------------------

    [Fact]
    public void A_new_owner_starts_with_one_reference()
    {
        CountingOwner owner = new CountingOwner();

        Assert.Equal(1, owner.RefCount);
        Assert.Equal(0, owner.Frees);

        owner.Release();

        Assert.Equal(0, owner.RefCount);
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void Retain_returns_the_same_owner_and_defers_the_free()
    {
        CountingOwner owner = new CountingOwner();

        Assert.Same(owner, owner.Retain());
        Assert.Same(owner, owner.Retain());
        Assert.Equal(3, owner.RefCount);

        owner.Release();
        owner.Release();
        Assert.Equal(0, owner.Frees);

        owner.Release();
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void Releasing_past_zero_throws_and_leaves_the_count_at_zero()
    {
        CountingOwner owner = new CountingOwner();
        owner.Release();

        Assert.Throws<ObjectDisposedException>(owner.Release);

        // The counter must not have been driven negative: a second attempt behaves identically
        // rather than eventually wrapping back into "live" territory.
        Assert.Equal(0, owner.RefCount);
        Assert.Throws<ObjectDisposedException>(owner.Release);
        Assert.Equal(0, owner.RefCount);
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void Retaining_a_freed_owner_throws_rather_than_resurrecting_it()
    {
        CountingOwner owner = new CountingOwner();
        owner.Release();

        Assert.Throws<ObjectDisposedException>(() => owner.Retain());
        Assert.Equal(0, owner.RefCount);
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        CountingOwner owner = new CountingOwner();

        owner.Dispose();
        owner.Dispose();
        owner.Dispose();

        Assert.Equal(1, owner.Frees);
        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void Dispose_only_drops_the_creators_reference()
    {
        CountingOwner owner = new CountingOwner();
        owner.Retain();

        owner.Dispose();
        Assert.Equal(0, owner.Frees);
        Assert.Equal(1, owner.RefCount);

        owner.Release();
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void FreeCore_runs_exactly_once_under_concurrent_retain_and_release()
    {
        CountingOwner owner = new CountingOwner();

        // 16 workers x 500 balanced retain/release pairs. Any lost update in the Interlocked
        // pair shows up either as a premature free (Frees != 0 here) or a stuck count below.
        Parallel.For(0, 16, _ =>
        {
            for (int i = 0; i < 500; i++)
            {
                owner.Retain();
                owner.Release();
            }
        });

        Assert.Equal(0, owner.Frees);
        Assert.Equal(1, owner.RefCount);

        owner.Release();
        Assert.Equal(1, owner.Frees);
    }

    [Fact]
    public void Exactly_one_of_many_concurrent_releases_frees()
    {
        CountingOwner owner = new CountingOwner();
        for (int i = 0; i < 15; i++)
        {
            owner.Retain();
        }

        Parallel.For(0, 16, _ => owner.Release());

        Assert.Equal(1, owner.Frees);
        Assert.Equal(0, owner.RefCount);
    }

    // ---- NativeSegmentOwner -------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Native_allocation_honours_every_legal_alignment(int alignment)
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(100, alignment);

        Assert.Equal(100, owner.Length);
        Assert.Equal(100, owner.Buffer.Length);
        Assert.Equal(alignment, owner.Buffer.Alignment);
        Assert.True(owner.Buffer.IsAligned);
        Assert.Equal(100, owner.WritableSpan.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(128)]
    [InlineData(-64)]
    [InlineData(int.MinValue)]
    public void Native_allocation_rejects_an_alignment_outside_the_cap(int alignment) =>
        // docs/08-semantics.md §6 caps alignment at 64 bytes.
        Assert.Throws<VortexFormatException>(() => NativeSegmentOwner.Allocate(64, alignment));

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Native_allocation_rejects_a_negative_length(int length) =>
        Assert.Throws<VortexFormatException>(() => NativeSegmentOwner.Allocate(length, 64));

    [Fact]
    public void A_zero_length_native_segment_is_legal()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(0, 64);

        Assert.True(owner.Buffer.IsEmpty);
        Assert.True(owner.WritableSpan.IsEmpty);

        // Even an empty segment gets a real, 64-aligned base so pointer identity keeps working.
        Assert.True(owner.Buffer.IsAligned);
    }

    [Fact]
    public void Writes_through_WritableSpan_are_visible_through_Buffer()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(8, 8);
        owner.WritableSpan.Fill(0xAB);

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0xAB, owner.Buffer.Span[i]);
        }
    }

    [Fact]
    public void Native_double_dispose_is_safe()
    {
        NativeSegmentOwner owner = NativeSegmentOwner.Allocate(4096, 64);

        owner.Dispose();
        owner.Dispose();
        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => owner.WritableSpan.Length);
        Assert.Throws<ObjectDisposedException>(() => owner.Retain());
    }

    [Fact]
    public void Native_memory_is_only_freed_once_even_when_release_races_dispose()
    {
        NativeSegmentOwner owner = NativeSegmentOwner.Allocate(4096, 64);
        owner.Retain();

        // Real threads, not Parallel.For: two iterations can be inlined onto one thread, and a
        // Barrier would then deadlock rather than test anything.
        using Barrier barrier = new Barrier(2);
        Thread disposer = new Thread(() => { barrier.SignalAndWait(); owner.Dispose(); });
        Thread releaser = new Thread(() => { barrier.SignalAndWait(); owner.Release(); });

        disposer.Start();
        releaser.Start();
        disposer.Join();
        releaser.Join();

        // Two balanced releases: the block is gone and the counter sits at zero, whichever
        // thread won.
        Assert.Equal(0, owner.RefCount);
        Assert.Throws<ObjectDisposedException>(() => owner.WritableSpan.Length);
    }

    [Fact]
    public void The_finalizer_frees_a_block_that_was_dropped_on_the_floor()
    {
        const int Dropped = 16;

        Collect();
        long before = NativeSegmentOwner.FinalizedBlockCount;

        WeakReference weak = DropBlocks(Dropped);
        Collect();

        Assert.False(weak.IsAlive);
        Assert.True(
            NativeSegmentOwner.FinalizedBlockCount >= before + Dropped,
            $"expected at least {Dropped} finalized blocks, saw " +
            $"{NativeSegmentOwner.FinalizedBlockCount - before}");
    }

    [Fact]
    public void A_disposed_block_is_never_finalized()
    {
        Collect();
        long before = NativeSegmentOwner.FinalizedBlockCount;

        DisposeBlocks(16);
        Collect();

        // Dispose suppresses finalization, so the backstop counter must not move. This is what
        // makes the previous test's counter meaningful rather than incidental.
        Assert.Equal(before, NativeSegmentOwner.FinalizedBlockCount);
    }

    // ---- PinnedArraySegmentOwner --------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void A_pinned_array_is_over_allocated_until_it_is_aligned(int alignment)
    {
        // The pinned object heap only promises pointer-size alignment, so anything above 8 has
        // to come from over-allocating and offsetting inside the array.
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(101, alignment);

        Assert.Equal(101, owner.Length);
        Assert.Equal(alignment, owner.Buffer.Alignment);
        Assert.True(owner.Buffer.IsAligned);
        Assert.Equal(101, owner.WritableSpan.Length);
    }

    [Fact]
    public void A_pinned_segment_starts_zeroed_and_round_trips_its_bytes()
    {
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(32, 64);

        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(0, owner.Buffer.Span[i]);
        }

        owner.WritableSpan.Fill(0x5A);
        Assert.Equal(0x5A, owner.Buffer.Span[31]);
    }

    [Fact]
    public void CopyOf_copies_and_aligns()
    {
        byte[] source = new byte[37];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i * 3);
        }

        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(source, 64);

        Assert.Equal(37, owner.Length);
        Assert.True(owner.Buffer.IsAligned);
        Assert.True(owner.Buffer.Span.SequenceEqual(source));
    }

    [Fact]
    public void CopyOf_accepts_an_empty_source()
    {
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(default, 64);

        Assert.True(owner.Buffer.IsEmpty);
        Assert.True(owner.Buffer.IsAligned);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(128)]
    [InlineData(-1)]
    public void Pinned_allocation_rejects_an_alignment_outside_the_cap(int alignment) =>
        Assert.Throws<VortexFormatException>(() => PinnedArraySegmentOwner.Allocate(16, alignment));

    [Fact]
    public void Pinned_allocation_rejects_a_negative_length() =>
        Assert.Throws<VortexFormatException>(() => PinnedArraySegmentOwner.Allocate(-1, 64));

    [Fact]
    public void Pinned_allocation_rejects_a_length_that_cannot_be_padded_into_one_array() =>
        // length + alignment - 1 must not silently wrap to a small positive int.
        Assert.Throws<VortexFormatException>(() => PinnedArraySegmentOwner.Allocate(int.MaxValue, 64));

    [Fact]
    public void Pinned_double_dispose_is_safe()
    {
        PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(64, 64);

        owner.Dispose();
        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => owner.WritableSpan.Length);
        Assert.Throws<ObjectDisposedException>(() => owner.Retain());
    }

    // ---- helpers -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropBlocks(int count)
    {
        NativeSegmentOwner? last = null;
        for (int i = 0; i < count; i++)
        {
            // Deliberately neither disposed nor released: this is the leak the finalizer exists
            // to catch.
            last = NativeSegmentOwner.Allocate(4096, 64);
        }

        return new WeakReference(last);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeBlocks(int count)
    {
        for (int i = 0; i < count; i++)
        {
            NativeSegmentOwner.Allocate(4096, 64).Dispose();
        }
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}
