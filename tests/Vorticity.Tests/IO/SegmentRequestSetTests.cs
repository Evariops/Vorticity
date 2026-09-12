using System;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The request set is where "exactly one refcount per segment per batch" (PHASE1-CONTRACTS.md
/// §2.2 rule 3) is actually enforced, so the refcounts are asserted directly and not inferred.
/// </summary>
public sealed class SegmentRequestSetTests
{
    private static PinnedArraySegmentOwner OwnerOf(int length, int alignment = 1) =>
        PinnedArraySegmentOwner.CopyOf(Pattern(length), alignment);

    // ---- registration and deduplication -----------------------------------------------------

    [Fact]
    public void A_new_set_is_empty_and_unpopulated()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        Assert.Equal(0, set.Count);
        Assert.False(set.IsPopulated);
    }

    [Fact]
    public void Registering_the_same_offset_and_length_twice_returns_one_slot()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(4096, 128, 3));
        int b = set.Add(Spec(4096, 128, 3));

        Assert.Equal(a, b);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Deduplication_keeps_the_stronger_alignment_claim()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(4096, 128, 0));
        Assert.Equal(slot, set.Add(Spec(4096, 128, 6)));

        Assert.Equal(6, set.GetSpec(slot).AlignmentExponent);
    }

    [Fact]
    public void Segments_that_differ_only_in_length_are_distinct()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        Assert.NotEqual(set.Add(Spec(4096, 128)), set.Add(Spec(4096, 129)));
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void The_dedup_table_survives_growth()
    {
        using SegmentRequestSet set = new SegmentRequestSet(initialCapacity: 2);

        const int n = 500;
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i, set.Add(Spec((ulong)i * 64, 16, 4)));
        }

        Assert.Equal(n, set.Count);

        // Every one of them must still be found, in a different order than it was inserted.
        for (int i = n - 1; i >= 0; i--)
        {
            Assert.Equal(i, set.Add(Spec((ulong)i * 64, 16, 4)));
        }

        Assert.Equal(n, set.Count);
    }

    [Fact]
    public void Offsets_that_share_their_high_bits_do_not_degrade_into_one_probe_chain()
    {
        // Real segment offsets inside one chunk differ only in their low bits; a hash that only
        // masked them would still be correct but quadratic. Correctness is what is asserted.
        using SegmentRequestSet set = new SegmentRequestSet(initialCapacity: 4);

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(i, set.Add(Spec((1UL << 40) + ((ulong)i * 8), 8, 3)));
        }

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(i, set.Add(Spec((1UL << 40) + ((ulong)i * 8), 8, 3)));
        }
    }

    // ---- malformed specs are rejected at registration ---------------------------------------

    [Theory]
    [InlineData(0UL, 8U, (byte)7)]                       // exponent past the cap
    [InlineData(ulong.MaxValue, 16U, (byte)0)]           // offset + length overflows u64
    [InlineData((ulong)long.MaxValue + 1, 0U, (byte)0)]  // offset past the signed range
    [InlineData(0UL, uint.MaxValue, (byte)0)]            // length not addressable
    public void A_malformed_spec_is_rejected_at_registration(ulong offset, uint length, byte exponent)
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        Assert.Throws<VortexFormatException>(() => set.Add(Spec(offset, length, exponent)));
        Assert.Equal(0, set.Count);
    }

    // ---- zero-length segments ----------------------------------------------------------------

    [Fact]
    public void A_zero_length_segment_is_accepted_and_pre_filled()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(4096, 0, 6));

        Assert.True(set.IsFilled(slot));

        set.Complete();

        Assert.True(set.IsPopulated);
        Assert.True(set.GetBuffer(slot).IsEmpty);
        Assert.Equal(0, set.GetBuffer(slot).Length);
        Assert.NotNull(set.GetOwner(slot));
    }

    [Fact]
    public void A_zero_length_slot_survives_an_abandoned_read()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int empty = set.Add(Spec(0, 0));
        int real = set.Add(Spec(64, 16));

        set.SetResult(real, OwnerOf(16));
        set.AbandonPending();

        Assert.True(set.IsFilled(empty));
        Assert.False(set.IsFilled(real));
    }

    // ---- filling, completing, reading --------------------------------------------------------

    [Fact]
    public void A_completed_set_hands_back_its_buffers()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(64, 32, 5));
        PinnedArraySegmentOwner owner = OwnerOf(32, 32);
        set.SetResult(slot, owner);
        set.Complete();

        VortexBuffer buffer = set.GetBuffer(slot);

        Assert.Equal(32, buffer.Length);
        Assert.Equal(Pattern(32), buffer.Span.ToArray());
        Assert.Same(owner, set.GetOwner(slot));
        Assert.Equal(1, owner.RefCount);
    }

    [Fact]
    public void Reading_a_slot_before_the_read_completes_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(64, 32));

        Assert.Throws<InvalidOperationException>(() => set.GetBuffer(slot));
        Assert.Throws<InvalidOperationException>(() => set.GetOwner(slot));
    }

    [Fact]
    public void An_out_of_range_slot_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();
        set.Add(Spec(64, 32));

        Assert.Throws<ArgumentOutOfRangeException>(() => set.GetSpec(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.GetSpec(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.IsFilled(7));
    }

    [Fact]
    public void Completing_with_an_unfilled_slot_is_refused()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        set.Add(Spec(0, 16));
        set.Add(Spec(64, 16));
        set.SetResult(0, OwnerOf(16));

        Assert.Throws<InvalidOperationException>(set.Complete);
        Assert.False(set.IsPopulated);
    }

    [Fact]
    public void A_short_buffer_is_a_format_error_and_the_owner_is_still_reclaimed()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 32));
        PinnedArraySegmentOwner owner = OwnerOf(31);

        Assert.Throws<VortexFormatException>(() => set.SetResult(slot, owner));

        // SetResult takes ownership before it validates, so AbandonPending is what frees it - the
        // rule the two built-in sources and the HTTP double all rely on.
        set.AbandonPending();
        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void A_shared_result_is_rejected_before_it_retains_anything()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 32));
        PinnedArraySegmentOwner block = OwnerOf(128, 64);

        Assert.Throws<VortexFormatException>(
            () => set.SetSharedResult(slot, block, block.Buffer.Slice(0, 31)));

        // Nothing was taken: the caller's single reference is intact.
        Assert.Equal(1, block.RefCount);
        block.Dispose();
        Assert.Equal(0, block.RefCount);
    }

    [Fact]
    public void Filling_one_slot_twice_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 16));
        set.SetResult(slot, OwnerOf(16));

        Assert.Throws<InvalidOperationException>(() => set.SetResult(slot, OwnerOf(16)));
    }

    [Fact]
    public void Filling_a_populated_set_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 16));
        set.SetResult(slot, OwnerOf(16));
        set.Complete();

        Assert.Throws<InvalidOperationException>(() => set.SetResult(slot, OwnerOf(16)));
    }

    [Fact]
    public void Registering_a_new_segment_after_population_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 16));
        set.SetResult(slot, OwnerOf(16));
        set.Complete();

        // The already-known one is still fine: it resolves to an existing, filled slot.
        Assert.Equal(slot, set.Add(Spec(0, 16)));
        Assert.Throws<InvalidOperationException>(() => set.Add(Spec(64, 16)));
    }

    [Fact]
    public void A_null_owner_is_a_caller_error()
    {
        using SegmentRequestSet set = new SegmentRequestSet();
        int slot = set.Add(Spec(0, 16));

        Assert.Throws<ArgumentNullException>(() => set.SetResult(slot, null!));
        Assert.Throws<ArgumentNullException>(
            () => set.SetSharedResult(slot, null!, VortexBuffer.Empty));
    }

    [Fact]
    public void A_non_positive_initial_capacity_is_a_caller_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentRequestSet(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentRequestSet(-1));
    }

    // ---- refcounting ---------------------------------------------------------------------------

    [Fact]
    public void A_shared_block_is_retained_once_per_slot_and_released_once_per_slot()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        PinnedArraySegmentOwner block = OwnerOf(256, 64);

        int a = set.Add(Spec(0, 16, 4));
        int b = set.Add(Spec(64, 16, 6));
        int c = set.Add(Spec(128, 16, 6));

        set.SetSharedResult(a, block, block.Buffer.Slice(0, 16));
        set.SetSharedResult(b, block, block.Buffer.Slice(64, 16));
        set.SetSharedResult(c, block, block.Buffer.Slice(128, 16));
        set.Complete();

        // One creator reference plus three slot references.
        Assert.Equal(4, block.RefCount);

        block.Dispose();       // the source gives back its own
        Assert.Equal(3, block.RefCount);

        set.Release();
        Assert.Equal(0, block.RefCount);
    }

    [Fact]
    public void Release_frees_every_owner_and_clears_the_set_for_reuse()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        PinnedArraySegmentOwner one = OwnerOf(16);
        PinnedArraySegmentOwner two = OwnerOf(16);

        set.SetResult(set.Add(Spec(0, 16)), one);
        set.SetResult(set.Add(Spec(64, 16)), two);
        set.Complete();
        set.Release();

        Assert.Equal(0, one.RefCount);
        Assert.Equal(0, two.RefCount);
        Assert.Equal(0, set.Count);
        Assert.False(set.IsPopulated);

        // Reusable: the arrays stay, the state does not.
        Assert.Equal(0, set.Add(Spec(1024, 8)));
    }

    [Fact]
    public void Release_is_idempotent()
    {
        SegmentRequestSet set = new SegmentRequestSet();
        PinnedArraySegmentOwner owner = OwnerOf(16);

        set.SetResult(set.Add(Spec(0, 16)), owner);
        set.Complete();

        set.Release();
        set.Release();
        set.Dispose();

        Assert.Equal(0, owner.RefCount);
    }

    [Fact]
    public void AbandonPending_releases_what_the_failed_call_took_and_allows_a_retry()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(0, 16));
        int b = set.Add(Spec(64, 16));

        PinnedArraySegmentOwner first = OwnerOf(16);
        set.SetResult(a, first);

        // ... and then the second range fails.
        set.AbandonPending();

        Assert.Equal(0, first.RefCount);
        Assert.False(set.IsPopulated);
        Assert.False(set.IsFilled(a));
        Assert.Equal(2, set.Count);

        PinnedArraySegmentOwner retryA = OwnerOf(16);
        PinnedArraySegmentOwner retryB = OwnerOf(16);
        set.SetResult(a, retryA);
        set.SetResult(b, retryB);
        set.Complete();

        Assert.True(set.IsPopulated);
        Assert.Equal(1, retryA.RefCount);
    }

    [Fact]
    public void AbandonPending_after_completion_does_nothing()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        PinnedArraySegmentOwner owner = OwnerOf(16);
        set.SetResult(set.Add(Spec(0, 16)), owner);
        set.Complete();

        set.AbandonPending();

        Assert.True(set.IsPopulated);
        Assert.Equal(1, owner.RefCount);
    }

    [Fact]
    public void Complete_is_idempotent()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        set.SetResult(set.Add(Spec(0, 16)), OwnerOf(16));
        set.Complete();
        set.Complete();

        Assert.True(set.IsPopulated);
    }

    // ---- the representability boundary --------------------------------------------------------

    [Fact]
    public void The_longest_addressable_segment_is_accepted_and_one_past_it_is_not()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        // int.MaxValue - 64: a coalesced run rounds its start down by up to 63 bytes, so the run
        // length has to keep fitting an int after that. Registration allocates nothing, so this
        // costs no memory to assert.
        const uint longest = int.MaxValue - 64;

        Assert.Equal(0, set.Add(Spec(0, longest, 6)));
        Assert.Throws<VortexFormatException>(() => set.Add(Spec(0, longest + 1, 6)));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void An_offset_at_the_very_top_of_the_signed_range_is_accepted_when_the_end_still_fits()
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        // Exactly long.MaxValue as the end: legal arithmetically, and rejected only later by the
        // source's own end-of-file check.
        int slot = set.Add(Spec((ulong)long.MaxValue - 16, 16, 0));

        Assert.Equal(0, slot);
        Assert.Throws<VortexFormatException>(() => set.Add(Spec((ulong)long.MaxValue - 15, 16, 0)));
    }

    [Fact]
    public void Buffers_are_unreachable_once_the_set_is_released()
    {
        SegmentRequestSet set = new SegmentRequestSet();
        int slot = set.Add(Spec(0, 16));
        set.SetResult(slot, OwnerOf(16));
        set.Complete();

        set.Release();

        // docs/07-dotnet-mapping.md §4: spans borrowed from a batch are invalid after it is
        // disposed, and the set refuses to hand one out rather than returning a dangling view.
        Assert.Throws<ArgumentOutOfRangeException>(() => set.GetBuffer(slot));
        set.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public void The_boundary_lengths_round_trip_through_a_slot(int length)
    {
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(64, (uint)length, 3));

        if (length != 0)
        {
            set.SetResult(slot, OwnerOf(length, 8));
        }

        set.Complete();

        Assert.Equal(length, set.GetBuffer(slot).Length);
        Assert.Equal(Pattern(length), set.GetBuffer(slot).Span.ToArray());
    }
}
