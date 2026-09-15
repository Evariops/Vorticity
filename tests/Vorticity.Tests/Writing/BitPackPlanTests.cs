// What the corpus cannot see about the bit-packing decision.
//
// The corpus proves the OUTPUT is right: every file we write is read back by our own reader and by
// Vortex Rust, value for value. What it cannot prove is that the right scheme was CHOSEN, because a
// writer that declines every encoding passes both of those and simply produces bigger files. These
// tests assert the choice itself, at the one place it is made.
//
// Two of them exist because a bug lived there. The null case cost 37 kB on
// `containers/zoned_many_zones_nulls` and was invisible to every value test in the suite: a null
// row's PACKED value is zero, but its RAW value put through a frame of reference is `-reference`,
// 64 bits wide, so every null in the column counted as an exception and the whole scheme was
// priced out of existence. The file round-tripped perfectly at every stage. It was just bigger.
using System;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class BitPackPlanTests
{
    /// <summary>
    /// The shape of `types/i64_nonnull_r8192`: three extreme values among thousands of small ones.
    /// </summary>
    /// <remarks>
    /// The reference writes it as zigzag over a 17-bit bit-packing with two patches, and we wrote
    /// it canonically at 3.31x its size, because a frame of reference anchored at
    /// <see cref="long.MinValue"/> makes every ordinary value 63 bits wide. Neither half of the fix
    /// works alone: patches over a frame of reference make HALF the column an exception, and zigzag
    /// without patches still has to carry the two extremes at full width.
    /// </remarks>
    [Fact]
    public void ThreeExtremeValuesDoNotDefeatBitPacking()
    {
        long[] values = new long[8192];
        values[0] = 0;
        values[1] = long.MinValue;
        values[2] = long.MaxValue;
        for (int i = 3; i < values.Length; i++)
        {
            // Alternating either side of zero, growing: magnitude decides the width, position
            // does not.
            values[i] = (i % 2 == 0 ? 1L : -1L) * (i * 7);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        BitPackPlan? plan = Plan(fixture, node);
        Assert.NotNull(plan);
        Assert.Equal(BitPackTransform.ZigZag, plan.Transform);
        Assert.Equal(2L, plan.Exceptions);

        // The rows themselves are the pack's to find, since stage R5b-2: the plan carries the
        // count, the pack sizes its patch arrays from it and fills them as it transforms.
        (int[] indices, _) = ArrayBlobWriter.Patches(fixture.Arena, fixture.Arena.GetNode(node), plan);
        Assert.Equal([1, 2], indices);

        // 8192 * 7 is under 2^16, so zigzag needs one more bit than that and nothing near 64.
        Assert.InRange(plan.BitWidth, 1, 20);
    }

    /// <summary>
    /// A null row is never an exception, whatever the frame of reference is.
    /// </summary>
    /// <remarks>
    /// The regression this pins: with a reference far from zero, treating a null's raw bits as a
    /// value to encode makes it the widest value in the column. Every null then prices as a patch,
    /// the cost function refuses every width, and a column that bit-packs into 7 bits is written
    /// out whole.
    /// </remarks>
    [Fact]
    public void NullRowsAreNeverPatches()
    {
        long[] values = new long[1024];
        bool[] valid = new bool[1024];
        for (int i = 0; i < values.Length; i++)
        {
            // A tight band a long way from zero: the reference is 58254, so a raw zero encodes as
            // -58254 under the frame.
            valid[i] = i % 4 != 0;
            values[i] = valid[i] ? 58254 + (i % 114) : 0;
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, fixture.BitmapValidity(valid), Nullability.Nullable);

        BitPackPlan? plan = Plan(fixture, node);
        Assert.NotNull(plan);
        Assert.Equal(0L, plan.Exceptions);
        Assert.Equal(BitPackTransform.Frame, plan.Transform);
        Assert.InRange(plan.BitWidth, 1, 8);
    }

    /// <summary>
    /// Frame of reference still wins where it always did: a large magnitude with a small span.
    /// </summary>
    /// <remarks>
    /// Without this, "prefer zigzag" would pass the test above and quietly cost 47 bits a value on
    /// every timestamp column in existence.
    /// </remarks>
    [Fact]
    public void ALargeMagnitudeWithASmallSpanStaysFramed()
    {
        long[] values = new long[4096];
        for (int i = 0; i < values.Length; i++)
        {
            // Jittered on purpose: `base + i` is an arithmetic progression, which `vortex.sequence`
            // now claims before bit-packing is ever asked. The property under test is about
            // magnitude against span, and it needs data that is not a progression to reach.
            values[i] = 1_700_000_000_000_000_000L + ((i * 7919) % 4096);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        BitPackPlan? plan = Plan(fixture, node);
        Assert.NotNull(plan);
        Assert.Equal(BitPackTransform.Frame, plan.Transform);
        Assert.Equal(0L, plan.Exceptions);
        Assert.Equal(12, plan.BitWidth);
    }

    /// <summary>
    /// Patches are priced, not free: enough exceptions and the narrow width stops paying.
    /// </summary>
    /// <remarks>
    /// The guard against a cost function that only ever counts packed bytes. Half a column of
    /// 40-bit values cannot be carried as exceptions to an 8-bit packing, and a planner that
    /// thought it could would emit four thousand patches to save four thousand bytes.
    /// </remarks>
    [Fact]
    public void TooManyExceptionsAreNotWorthTheNarrowerWidth()
    {
        long[] values = new long[4096];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i % 2 == 0 ? i % 128 : 1L << (40 + (i % 3));
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        BitPackPlan? plan = Plan(fixture, node);
        if (plan is not null)
        {
            Assert.True(
                plan.Exceptions * 10 < values.Length,
                $"{plan.Exceptions} patches over {values.Length} rows at width {plan.BitWidth}");
        }
    }

    /// <summary>
    /// The patch values are in the ENCODED domain, which is the only one the decoder can use.
    /// </summary>
    /// <remarks>
    /// The decoder overwrites the UNPACKED buffer and only then hands it to the wrapper, so a patch
    /// carrying the original value would be transformed a second time. Asserted here rather than
    /// left to the cross-check because a wrong value is a wrong value in every file that has one,
    /// and this says which direction is wrong.
    /// </remarks>
    [Fact]
    public void PatchValuesAreEncodedNotRaw()
    {
        long[] values = new long[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 1000 + (i % 64);
        }

        values[7] = 1L << 40;

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        BitPackPlan? plan = Plan(fixture, node);
        Assert.NotNull(plan);
        Assert.Equal(BitPackTransform.Frame, plan.Transform);
        Assert.Equal(1L, plan.Exceptions);
        (int[] indices, ulong[] patched) =
            ArrayBlobWriter.Patches(fixture.Arena, fixture.Arena.GetNode(node), plan);
        Assert.Equal([7], indices);

        // Frame of reference over a minimum of 1000: the stored value is the offset, not 2^40.
        Assert.Equal((ulong)((1L << 40) - 1000), patched[0]);
    }

    private static BitPackPlan? Plan(ColumnFixture fixture, int node)
    {
        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        return plan.Scheme == ColumnScheme.BitPacked ? plan.BitPack : null;
    }
}
