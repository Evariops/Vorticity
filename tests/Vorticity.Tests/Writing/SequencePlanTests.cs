// vortex.sequence on the write side: the choice, and the bytes.
//
// The corpus cross-check proves the values come back. It cannot prove the METADATA has the shape
// the reference writes, because our own reader is as happy with a `uint64_value` multiplier as with
// an `int64_value` one and the values are identical either way. Upstream is not: it reads the
// multiplier's physical type FROM THE PROTO TAG (`multiplier_ptype_from_proto`), so the tag is part
// of the contract rather than an encoding detail.
//
// So one test here asserts the exact bytes, taken from a corpus file the reference wrote. It is the
// same class of check that caught the FSST symbol-table ordering: a rule our reader does not
// enforce, which therefore no round trip through it can see.
using System;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class SequencePlanTests
{
    [Fact]
    public void AnArithmeticProgressionBecomesASequence()
    {
        long[] values = new long[4096];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 1_641_600_000_000L + (i * 86_400_000L);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.Equal(ColumnScheme.Sequence, plan.Scheme);
        SequencePlan sequence = Assert.NotNull(plan.Sequence);
        Assert.Equal(1_641_600_000_000UL, sequence.BaseBits);
        Assert.Equal((Int128)86_400_000L, sequence.Step);
        Assert.False(sequence.StepIsUnsigned);
    }

    /// <summary>
    /// A descending progression is a sequence too, with a negative step on the signed wire field.
    /// </summary>
    [Fact]
    public void ADescendingProgressionKeepsItsSign()
    {
        long[] values = new long[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 1000 - (i * 3L);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int64Node(values, Validity.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.Equal(ColumnScheme.Sequence, plan.Scheme);
        SequencePlan sequence = Assert.NotNull(plan.Sequence);
        Assert.Equal((Int128)(-3), sequence.Step);
        Assert.False(sequence.StepIsUnsigned);
    }

    /// <summary>
    /// One null disqualifies the whole column, because the encoding has nowhere to record it.
    /// </summary>
    /// <remarks>
    /// `vortex.sequence` has no children and no buffers, so there is no validity child to write and
    /// no way to express a gap. This is the one rule that cannot be relaxed later without changing
    /// the encoding: the decoder derives validity from the DTYPE's nullability alone, so a null row
    /// would come back as a generated value claiming to be present.
    /// </remarks>
    [Fact]
    public void OneNullDisqualifiesTheColumn()
    {
        int[] values = new int[2048];
        bool[] valid = new bool[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i;
            valid[i] = true;
        }

        valid[1000] = false;

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int32Node(values, fixture.BitmapValidity(valid), Nullability.Nullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.NotEqual(ColumnScheme.Sequence, plan.Scheme);
    }

    /// <summary>
    /// A nullable column with no actual nulls still qualifies, which is the common case.
    /// </summary>
    [Fact]
    public void ANullableColumnWithNoNullsStillQualifies()
    {
        int[] values = new int[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * 5;
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int32Node(values, Validity.AllValid, Nullability.Nullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.Equal(ColumnScheme.Sequence, plan.Scheme);
    }

    /// <summary>One value out of step is enough to disqualify it.</summary>
    [Fact]
    public void ASingleBreakInTheProgressionDisqualifiesIt()
    {
        int[] values = new int[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * 4;
        }

        values[2047] += 1;

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int32Node(values, Validity.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.NotEqual(ColumnScheme.Sequence, plan.Scheme);

        // ...and the column is still bit-packed rather than written out whole: a near-progression
        // has a tiny frame-of-reference span.
        Assert.Equal(ColumnScheme.BitPacked, plan.Scheme);
    }

    /// <summary>
    /// The serialized metadata, byte for byte against a file the reference wrote.
    /// </summary>
    /// <remarks>
    /// `types/fsl_i32_3_nonnull_r8192`'s elements are 0, 1, 2, ... and its `vortex.sequence`
    /// metadata is <c>0a02 1800 1202 1802</c>: field 1 (base) holding a two-byte ScalarValue whose
    /// field 3 is sint64 0, and field 2 (multiplier) holding sint64 1 — zigzagged, so the 1 appears
    /// as 2 on the wire.
    ///
    /// The point of asserting the exact bytes rather than a round trip: both values are
    /// <c>int64_value</c> for an i32 column, which is what tells us the wire preserves the STEP's
    /// signedness and not the column's width. A writer that emitted <c>uint64_value</c> here would
    /// round-trip perfectly through our own reader and hand upstream a different physical type.
    /// </remarks>
    [Fact]
    public void TheMetadataMatchesTheReferenceByteForByte()
    {
        int[] values = new int[4096];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i;
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Int32Node(values, Validity.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.Equal(ColumnScheme.Sequence, plan.Scheme);

        byte[] metadata = ArrayBlobWriter.SequenceMetadataBytesForTests(Assert.NotNull(plan.Sequence), PType.I32);
        Assert.Equal(Convert.FromHexString("0a02180012021802"), metadata);
    }
}
