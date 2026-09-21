using System;
using Vorticity;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The planner in isolation. Its rounding rule is the single most likely thing to be simplified
/// into <c>Start = specs[first].Offset</c>, so it is tested here directly rather than only through
/// a source.
/// </summary>
public sealed class SegmentCoalescerTests
{
    private static int Plan(ReadOnlySpan<SegmentSpec> specs, Span<CoalescedRun> runs, SegmentReadOptions options) =>
        SegmentCoalescer.Plan(specs, runs, options);

    // ---- the alignment rule ---------------------------------------------------------------

    [Fact]
    public void A_run_start_is_always_rounded_down_to_64()
    {
        SegmentSpec[] specs = [Spec(65, 4), Spec(127, 1), Spec(128, 64, 6), Spec(4096, 32, 4)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        int count = Plan(specs, runs, SegmentReadOptions.Default);

        Assert.Equal(1, count);
        Assert.Equal(64, runs[0].Start);
        Assert.Equal(0, runs[0].Start % 64);

        // The whole point: with Start = 65 the segment at 128 would land at buffer offset 63.
        Assert.Equal(4128 - 64, runs[0].Length);
        Assert.Equal(0, runs[0].FirstIndex);
        Assert.Equal(4, runs[0].Count);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(63UL)]
    [InlineData(64UL)]
    [InlineData(65UL)]
    [InlineData(4095UL)]
    [InlineData(1UL << 40)]
    [InlineData((1UL << 40) + 37)]
    public void Every_run_start_is_64_aligned_and_at_or_below_the_first_offset(ulong offset)
    {
        SegmentSpec[] specs = [Spec(offset, 8)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Equal(1, Plan(specs, runs, SegmentReadOptions.Default));
        Assert.Equal(0, runs[0].Start % 64);
        Assert.True(runs[0].Start <= (long)offset);
        Assert.True((long)offset - runs[0].Start < 64);
        Assert.Equal((long)offset + 8, runs[0].End);
    }

    [Fact]
    public void Alignment_survives_coalescing_for_every_segment_in_the_run()
    {
        // Segments of mixed alignment that coalesce into a single run.
        SegmentSpec[] specs =
        [
            Spec(64, 8, 6), Spec(65, 3, 0), Spec(127, 1, 0), Spec(128, 64, 6), Spec(4096, 32, 4),
        ];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        Assert.Equal(1, Plan(specs, runs, SegmentReadOptions.Default));

        foreach (SegmentSpec spec in specs)
        {
            long relative = (long)spec.Offset - runs[0].Start;
            int alignment = 1 << spec.AlignmentExponent;

            // A 64-aligned base plus a relative offset that is a multiple of 2^k is 2^k-aligned.
            Assert.Equal(0, relative % alignment);
        }
    }

    // ---- gap and size budgets -------------------------------------------------------------

    [Theory]
    [InlineData(0, 2)]      // adjacent-but-for-one-byte, zero gap budget: two runs
    [InlineData(1, 1)]      // exactly at the budget: one run
    [InlineData(2, 1)]      // past the budget in the permissive direction: still one run
    public void Coalescing_happens_at_exactly_the_gap_budget(int gapBudget, int expectedRuns)
    {
        // [0, 8) then [9, 17): the gap is exactly 1 byte.
        SegmentSpec[] specs = [Spec(0, 8), Spec(9, 8)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        SegmentReadOptions options = new SegmentReadOptions { CoalesceGapBytes = gapBudget };

        Assert.Equal(expectedRuns, Plan(specs, runs, options));
    }

    [Fact]
    public void Adjacent_ranges_always_coalesce_even_with_a_zero_gap_budget()
    {
        SegmentSpec[] specs = [Spec(0, 8), Spec(8, 8)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        Assert.Equal(1, Plan(specs, runs, new SegmentReadOptions { CoalesceGapBytes = 0 }));
    }

    [Fact]
    public void Overlapping_ranges_coalesce_and_the_run_covers_the_further_end()
    {
        SegmentSpec[] specs = [Spec(0, 100), Spec(50, 100)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        Assert.Equal(1, Plan(specs, runs, SegmentReadOptions.Default));
        Assert.Equal(150, runs[0].End);
    }

    [Fact]
    public void A_run_that_would_exceed_the_size_budget_is_split()
    {
        // A 1 MiB gap is well inside the default gap budget, but the resulting run would be
        // 1 MiB + 128 bytes, past a 1 MiB ceiling. This is the trap MaxCoalescedReadBytes guards:
        // it bounds an allocation sized by file content.
        SegmentSpec[] specs = [Spec(0, 64), Spec(1 << 20, 64)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        SegmentReadOptions options = new SegmentReadOptions
        {
            CoalesceGapBytes = 1 << 20,
            MaxCoalescedReadBytes = 1 << 20,
        };

        Assert.Equal(2, Plan(specs, runs, options));
        Assert.Equal(0, runs[0].Start);
        Assert.Equal(64, runs[0].Length);
        Assert.Equal(1 << 20, runs[1].Start);
    }

    [Fact]
    public void A_run_exactly_at_the_size_budget_is_kept_whole()
    {
        SegmentSpec[] specs = [Spec(0, 64), Spec(1024 - 64, 64)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        SegmentReadOptions options = new SegmentReadOptions
        {
            CoalesceGapBytes = 1 << 20,
            MaxCoalescedReadBytes = 1024,
        };

        Assert.Equal(1, Plan(specs, runs, options));
        Assert.Equal(1024, runs[0].Length);
    }

    [Fact]
    public void A_single_segment_larger_than_the_size_budget_is_still_one_run()
    {
        // A segment cannot be split, so the budget bounds merging, never the file.
        SegmentSpec[] specs = [Spec(0, 4096)];
        CoalescedRun[] runs = new CoalescedRun[1];

        SegmentReadOptions options = new SegmentReadOptions { MaxCoalescedReadBytes = 64 };

        Assert.Equal(1, Plan(specs, runs, options));
        Assert.Equal(4096, runs[0].Length);
    }

    // ---- degenerate and adversarial inputs ------------------------------------------------

    [Fact]
    public void An_empty_list_plans_nothing()
    {
        Assert.Equal(0, Plan([], [], SegmentReadOptions.Default));
    }

    [Fact]
    public void A_zero_length_segment_is_planned_not_rejected()
    {
        SegmentSpec[] specs = [Spec(128, 0, 6)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Equal(1, Plan(specs, runs, SegmentReadOptions.Default));
        Assert.Equal(128, runs[0].Start);
        Assert.Equal(0, runs[0].Length);
    }

    [Fact]
    public void Boundary_row_counts_used_as_segment_lengths_all_plan()
    {
        int[] lengths = [0, 1, 1023, 1024, 1025, 8191, 8192, 8193];
        SegmentSpec[] specs = new SegmentSpec[lengths.Length];
        CoalescedRun[] runs = new CoalescedRun[lengths.Length];

        ulong offset = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            specs[i] = Spec(offset, (uint)lengths[i], 3);
            offset += 8192;
        }

        int count = Plan(specs, runs, SegmentReadOptions.Default);

        Assert.Equal(1, count);
        Assert.Equal(0, runs[0].Start);
        Assert.Equal((long)offset - 8192 + 8193, runs[0].End);
    }

    [Fact]
    public void An_unsorted_list_is_rejected_rather_than_mis_planned()
    {
        SegmentSpec[] specs = [Spec(4096, 8), Spec(64, 8)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Plan(specs, runs, SegmentReadOptions.Default));

        Assert.Equal("specs", error.ParamName);
    }

    [Fact]
    public void An_unsorted_list_is_rejected_even_across_a_run_boundary()
    {
        // The pair that ends a run must be checked too, or a backwards jump slips through the
        // `gap > budget` branch and produces a run that does not contain its own segments.
        SegmentSpec[] specs = [Spec(0, 8), Spec(1 << 30, 8), Spec(16, 8)];
        CoalescedRun[] runs = new CoalescedRun[specs.Length];

        Assert.Throws<ArgumentException>(() => Plan(specs, runs, SegmentReadOptions.Default));
    }

    [Fact]
    public void A_short_run_buffer_is_rejected()
    {
        SegmentSpec[] specs = [Spec(0, 8), Spec(1 << 30, 8)];
        CoalescedRun[] runs = new CoalescedRun[1];

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Plan(specs, runs, SegmentReadOptions.Default));

        Assert.Equal("runs", error.ParamName);
    }

    [Fact]
    public void An_alignment_exponent_above_the_cap_is_a_format_error()
    {
        SegmentSpec[] specs = [Spec(0, 8, 7)];
        CoalescedRun[] runs = new CoalescedRun[1];

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => Plan(specs, runs, SegmentReadOptions.Default));

        Assert.Contains("alignment_exponent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_that_overflows_u64_is_a_format_error()
    {
        SegmentSpec[] specs = [Spec(ulong.MaxValue, 16)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Throws<VortexFormatException>(() => Plan(specs, runs, SegmentReadOptions.Default));
    }

    [Fact]
    public void An_offset_beyond_the_signed_range_is_a_format_error()
    {
        SegmentSpec[] specs = [Spec((ulong)long.MaxValue + 1, 0)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Throws<VortexFormatException>(() => Plan(specs, runs, SegmentReadOptions.Default));
    }

    [Fact]
    public void A_length_that_cannot_be_addressed_is_a_format_error()
    {
        SegmentSpec[] specs = [Spec(0, uint.MaxValue)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Throws<VortexFormatException>(() => Plan(specs, runs, SegmentReadOptions.Default));
    }

    [Fact]
    public void A_null_options_is_a_caller_error()
    {
        SegmentSpec[] specs = [Spec(0, 8)];
        CoalescedRun[] runs = new CoalescedRun[1];

        Assert.Throws<ArgumentNullException>(() => Plan(specs, runs, null!));
    }

    // ---- CoalescedRun's own invariants -----------------------------------------------------

    [Fact]
    public void A_run_cannot_be_constructed_with_an_unaligned_start()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoalescedRun(65, 8, 0, 1));
    }

    [Fact]
    public void A_run_cannot_be_constructed_empty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoalescedRun(64, 8, 0, 0));
    }

    [Fact]
    public void Runs_compare_by_value()
    {
        CoalescedRun a = new CoalescedRun(64, 8, 0, 1);
        CoalescedRun b = new CoalescedRun(64, 8, 0, 1);
        CoalescedRun c = new CoalescedRun(128, 8, 0, 1);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.Equal(72, a.End);
        Assert.Contains("64", a.ToString(), StringComparison.Ordinal);
    }

    // ---- options validation ----------------------------------------------------------------

    [Fact]
    public void Options_reject_values_that_would_make_planning_meaningless()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SegmentReadOptions { CoalesceGapBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SegmentReadOptions { MaxCoalescedReadBytes = 63 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SegmentReadOptions { MaxPooledBytes = -1 });

        Assert.Equal(1 << 20, SegmentReadOptions.Default.CoalesceGapBytes);
        Assert.Equal(16 << 20, SegmentReadOptions.Default.MaxCoalescedReadBytes);
        Assert.Equal(8 << 20, SegmentReadOptions.Default.MaxPooledBytes);
    }
}
