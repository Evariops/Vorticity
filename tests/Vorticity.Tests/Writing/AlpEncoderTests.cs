// The ALP encoder, checked against the DECODER THAT READS THE REFERENCE'S OWN FILES.
//
// Same discipline as the FSST and PackBlock tests: an encoder and a decoder written together agree
// with each other even when both are wrong. AlpTables is the kernel the corpus's vortex.alp files
// exercise, so decoding through it makes the round trip a statement about the format.
//
// The sharp edge here is not the arithmetic, it is the EQUALITY. ALP is lossless only because a
// value is encoded exclusively when decoding the integer reproduces its exact bit pattern; compare
// with == instead and -0.0 becomes +0.0, a NaN becomes a different NaN, and the writer silently
// changes values that "compare equal".
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class AlpEncoderTests
{
    [Fact]
    public void DecimalShapedDoublesRoundTripThroughTheCorpusValidatedKernel()
    {
        double[] values = new double[512];
        for (int i = 0; i < values.Length; i++)
        {
            // Prices: two decimal places, which is the shape ALP exists for.
            values[i] = Math.Round(1.0 + (i * 0.37), 2);
        }

        AssertRoundTrip(values);
    }

    /// <summary>
    /// The shrink test, without which everything above is satisfied by an encoder that patches
    /// every value and is therefore perfectly lossless and perfectly useless.
    /// </summary>
    [Fact]
    public void AColumnOfShortDecimalsGetsMuchSmaller()
    {
        double[] values = new double[4096];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Math.Round(100.0 + ((i % 500) * 0.01), 2);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = DoubleNode(fixture, values);
        AlpPlan? plan = AlpPlan.TryBuild(fixture.Arena, node, values.Length * sizeof(double));

        Assert.NotNull(plan);
        Assert.Empty(plan.PatchIndices);
        Assert.True(
            plan.EncodedSize * 4 < values.Length * sizeof(double),
            $"{plan.EncodedSize} bytes estimated for {values.Length * sizeof(double)} plain");
    }

    /// <summary>
    /// Negative zero and NaN must survive as themselves. Both compare EQUAL to something they are
    /// not - <c>-0.0 == 0.0</c>, and a NaN payload is invisible to <c>==</c> - so an encoder that
    /// checked with <c>==</c> would emit a value the reader cannot distinguish from the original
    /// and would be wrong in a way no equality-based test could see.
    /// </summary>
    [Fact]
    public void NegativeZeroAndNaNSurviveAsThemselves()
    {
        double[] values = new double[256];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Math.Round(5.0 + (i * 0.25), 2);
        }

        values[3] = -0.0;
        values[7] = double.NaN;
        values[11] = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001);
        values[13] = double.PositiveInfinity;

        AssertRoundTrip(values);
    }

    /// <summary>Random bit patterns are not decimals, and ALP must decline rather than patch them all.</summary>
    [Fact]
    public void RandomBitsAreDeclined()
    {
        Random random = new Random(20260912);
        double[] values = new double[2048];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BitConverter.UInt64BitsToDouble(
                ((ulong)(uint)random.Next() << 32) | (uint)random.Next());
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = DoubleNode(fixture, values);
        Assert.Null(AlpPlan.TryBuild(fixture.Arena, node, values.Length * sizeof(double)));
    }

    /// <summary>
    /// A null row's float is unspecified, so it must not become a patch: paying for an exception
    /// nobody can read back is the one way this encoding wastes bytes silently.
    /// </summary>
    [Fact]
    public void NullRowsDoNotBecomePatches()
    {
        double[] values = new double[512];
        bool[] valid = new bool[512];
        for (int i = 0; i < values.Length; i++)
        {
            valid[i] = i % 4 != 0;

            // Garbage under the nulls, of exactly the shape that would defeat ALP if it counted.
            values[i] = valid[i]
                ? Math.Round(3.0 + (i * 0.05), 2)
                : BitConverter.UInt64BitsToDouble(0x4123_4567_89AB_CDEF);
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = DoubleNode(fixture, values, valid);
        AlpPlan? plan = AlpPlan.TryBuild(fixture.Arena, node, values.Length * sizeof(double));

        Assert.NotNull(plan);
        Assert.Empty(plan.PatchIndices);
    }

    [Fact]
    public void SinglePrecisionRoundTrips()
    {
        float[] values = new float[512];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Round(2.0f + (i * 0.25f), 2);
        }

        values[5] = -0.0f;
        values[9] = float.NaN;

        using ColumnFixture fixture = new ColumnFixture();
        int node = SingleNode(fixture, values);
        AlpPlan? plan = AlpPlan.TryBuild(fixture.Arena, node, values.Length * sizeof(float));
        Assert.NotNull(plan);

        int[] encoded = new int[values.Length];
        MemoryMarshal.Cast<byte, int>(plan.Encoded).CopyTo(encoded);
        float[] decoded = new float[values.Length];
        AlpTables.DecodeSingle(encoded, decoded, plan.ExponentE, plan.ExponentF);

        ReadOnlySpan<float> patches = MemoryMarshal.Cast<byte, float>(plan.PatchValues);
        for (int i = 0; i < plan.PatchIndices.Length; i++)
        {
            decoded[plan.PatchIndices[i]] = patches[i];
        }

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToInt32Bits(values[i]),
                BitConverter.SingleToInt32Bits(decoded[i]));
        }
    }

    /// <summary>
    /// The plan's integers and patches against the rule spelled out per row, at the exponents the
    /// plan chose: the conversion saturates, a NaN converts to zero, a patched row holds the first
    /// encoded integer. Edge values sit at every position a lane can put them, and the length leaves
    /// a tail.
    /// </summary>
    [Theory]
    [InlineData(1_023)]
    [InlineData(1_024)]
    public void TheEncodedRowsAreTheRulesExactly(int length)
    {
        double[] edges =
        [
            -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 9.2233720368547758E18,
            -9.2233720368547758E18, 1e300, double.Epsilon, 123456789.123, -2.5, 0.5,
        ];
        double[] doubles = new double[length];
        float[] singles = new float[length];
        for (int i = 0; i < length; i++)
        {
            doubles[i] = i % 5 == 1 ? edges[(i / 5) % edges.Length] : Math.Round(-40.0 + (i * 0.37), 2);
            singles[i] = i % 5 == 2 ? (float)edges[(i / 5) % edges.Length] : MathF.Round(-40f + (i * 0.25f), 2);
        }

        using ColumnFixture fixture = new ColumnFixture();
        AlpPlan? wide = AlpPlan.TryBuild(fixture.Arena, DoubleNode(fixture, doubles), length * sizeof(double) * 4L);
        Assert.NotNull(wide);
        List<int> expectedPatches = [];
        long[] expected = new long[length];
        long? fill = null;
        for (int i = 0; i < length; i++)
        {
            double x = doubles[i] * AlpTables.F10Double[wide.ExponentE] * AlpTables.If10Double[wide.ExponentF];
            x = (x + 6755399441055744.0) - 6755399441055744.0;
            long integer = double.IsNaN(x) ? 0 : (long)x;
            double back = integer * AlpTables.F10Double[wide.ExponentF] * AlpTables.If10Double[wide.ExponentE];
            if (BitConverter.DoubleToInt64Bits(back) == BitConverter.DoubleToInt64Bits(doubles[i]))
            {
                expected[i] = integer;
                fill ??= integer;
            }
            else
            {
                expectedPatches.Add(i);
            }
        }

        foreach (int patched in expectedPatches)
        {
            expected[patched] = fill ?? 0;
        }

        Assert.Equal(expectedPatches, wide.PatchIndices);
        Assert.Equal(expected, MemoryMarshal.Cast<byte, long>(wide.Encoded).ToArray());

        AlpPlan? narrow = AlpPlan.TryBuild(fixture.Arena, SingleNode(fixture, singles), length * sizeof(float) * 4L);
        Assert.NotNull(narrow);
        expectedPatches.Clear();
        int[] expectedNarrow = new int[length];
        int? fillNarrow = null;
        for (int i = 0; i < length; i++)
        {
            float x = singles[i] * AlpTables.F10Single[narrow.ExponentE] * AlpTables.If10Single[narrow.ExponentF];
            x = (x + 12582912f) - 12582912f;
            int integer = float.IsNaN(x) ? 0 : (int)x;
            float back = integer * AlpTables.F10Single[narrow.ExponentF] * AlpTables.If10Single[narrow.ExponentE];
            if (BitConverter.SingleToInt32Bits(back) == BitConverter.SingleToInt32Bits(singles[i]))
            {
                expectedNarrow[i] = integer;
                fillNarrow ??= integer;
            }
            else
            {
                expectedPatches.Add(i);
            }
        }

        foreach (int patched in expectedPatches)
        {
            expectedNarrow[patched] = fillNarrow ?? 0;
        }

        Assert.Equal(expectedPatches, narrow.PatchIndices);
        Assert.Equal(expectedNarrow, MemoryMarshal.Cast<byte, int>(narrow.Encoded).ToArray());
    }

    private static void AssertRoundTrip(double[] values)
    {
        using ColumnFixture fixture = new ColumnFixture();
        int node = DoubleNode(fixture, values);
        AlpPlan? plan = AlpPlan.TryBuild(fixture.Arena, node, values.Length * sizeof(double));
        Assert.NotNull(plan);

        long[] encoded = new long[values.Length];
        MemoryMarshal.Cast<byte, long>(plan.Encoded).CopyTo(encoded);
        double[] decoded = new double[values.Length];
        AlpTables.DecodeDouble(encoded, decoded, plan.ExponentE, plan.ExponentF);

        ReadOnlySpan<double> patches = MemoryMarshal.Cast<byte, double>(plan.PatchValues);
        Assert.Equal(plan.PatchIndices.Length, patches.Length);
        for (int i = 0; i < plan.PatchIndices.Length; i++)
        {
            decoded[plan.PatchIndices[i]] = patches[i];
        }

        for (int i = 0; i < values.Length; i++)
        {
            // Bit-for-bit, not ==: that is the whole contract.
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(values[i]),
                BitConverter.DoubleToInt64Bits(decoded[i]));
        }
    }

    private static int DoubleNode(ColumnFixture fixture, double[] values, bool[]? valid = null)
    {
        Validity validity = valid is null ? Validity.NonNullable : fixture.BitmapValidity(valid);
        Nullability nullability = valid is null ? Nullability.NonNullable : Nullability.Nullable;
        return fixture.Arena.AddPrimitive(
            fixture.Types.Primitive(PType.F64, nullability), values.Length, validity, PType.F64,
            fixture.Bytes(MemoryMarshal.AsBytes<double>(values)));
    }

    private static int SingleNode(ColumnFixture fixture, float[] values)
    {
        return fixture.Arena.AddPrimitive(
            fixture.Types.Primitive(PType.F32, Nullability.NonNullable), values.Length,
            Validity.NonNullable, PType.F32,
            fixture.Bytes(MemoryMarshal.AsBytes<float>(values)));
    }
}
