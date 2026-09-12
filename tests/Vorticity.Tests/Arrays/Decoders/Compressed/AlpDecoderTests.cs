// vortex.alp. 33 corpus files already cover the happy path with and without patches, so what is
// here is the arithmetic contract and the failure surface.
//
// The test that matters most is TheInverseTableIsUsedRatherThanADivision. `x * IF10[e]` and
// `x / F10[e]` are not the same function in binary floating point, and the difference shows up on
// ordinary data, not on adversarial data: 3 * 0.1 is 0.30000000000000004 while 3 / 10.0 is 0.3.
// The reference multiplies by a precomputed inverse, so we must too, or every ALP column with
// e != 0 comes back one ulp off on a scattering of rows -- a corpus failure nobody would guess the
// cause of from the symptom.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class AlpDecoderTests
{
    [Fact]
    public void TheInverseTableIsUsedRatherThanADivision()
    {
        // encoded = 3, e = 1, f = 0. Decoding is 3 * F10[0] * IF10[1] = 3 * 1.0 * 0.1.
        double[] decoded = DecodeDouble([3, 6, 7], exponentE: 1, exponentF: 0);

        Assert.Equal(3 * 0.1, decoded[0]);
        Assert.Equal(6 * 0.1, decoded[1]);
        Assert.Equal(7 * 0.1, decoded[2]);

        // Stated as literals too, so the test still fails if someone "fixes" the expression above.
        Assert.Equal(0.30000000000000004, decoded[0]);
        Assert.NotEqual(3 / 10.0, decoded[0]);
    }

    [Fact]
    public void DecodesDoublesAcrossExponents()
    {
        // 12345 with e = 2, f = 0 is 123.45.
        Assert.Equal(12345 * 1.0 * 0.01, DecodeDouble([12345], 2, 0)[0]);

        // f shifts the other way: e = 2, f = 1 is 12345 * 10 * 0.01.
        Assert.Equal(12345 * 10.0 * 0.01, DecodeDouble([12345], 2, 1)[0]);

        // e = f = 0 is the identity, and negatives survive it.
        Assert.Equal(-7.0, DecodeDouble([-7], 0, 0)[0]);
    }

    [Fact]
    public void DecodesFloatsInSinglePrecision()
    {
        // The f32 tables are f32 literals, not narrowed f64 ones. Computing in double and narrowing
        // at the end would give a different answer here.
        float[] decoded = DecodeSingle([3, 12345], exponentE: 1, exponentF: 0);
        Assert.Equal(3 * 1.0f * 0.1f, decoded[0]);
        Assert.Equal(12345 * 1.0f * 0.1f, decoded[1]);
    }

    [Fact]
    public void PatchesOverwriteTheRowsTheEncoderCouldNotRepresent()
    {
        // Row 1 is the value ALP could not round-trip, so it is stored verbatim as a double.
        byte[] encoded = TestBuffers.Int64(1, 0, 3);
        byte[] indices = TestBuffers.UInt32(1);
        byte[] values = TestBuffers.Double(double.PositiveInfinity);

        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(1, 0, PatchesMetadata.Create(1, 0, PType.U32)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded, indices, values);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 3));

        Assert.Equal(1 * 0.1, Value(node, 0));
        Assert.Equal(double.PositiveInfinity, Value(node, 1));
        Assert.Equal(3 * 0.1, Value(node, 2));
    }

    [Fact]
    public void ValidityComesFromTheEncodedChild()
    {
        byte[] encoded = TestBuffers.Int64(1, 2, 3);
        byte[] validity = TestBuffers.Bitmap(true, false, true);

        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(0, 0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(1)));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded, validity);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 3));

        Assert.True(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
        Assert.True(harness.IsValid(node, 2));
    }

    [Theory]
    [InlineData(11u, 0u)]   // f32's inverse table has 11 entries, so 11 is one past the end
    [InlineData(0u, 11u)]
    [InlineData(255u, 0u)]
    public void AnExponentPastTheTableIsRejectedForFloats(uint exponentE, uint exponentF)
    {
        // Upstream only checks that the exponent fits in a u8, and then indexes a static slice with
        // it. For a reader of untrusted input the table length is the bound that matters.
        byte[] encoded = TestBuffers.Int32(1);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(exponentE, exponentF))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f32 = harness.Types.Primitive(PType.F32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f32, 1));
    }

    [Fact]
    public void AnExponentPastTheTableIsRejectedForDoubles()
    {
        // f64's tables run to 10^23, so 24 entries: index 24 is out.
        byte[] encoded = TestBuffers.Int64(1);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(24, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void AnExponentBeyondMaxExponentButInsideTheTableIsAccepted()
    {
        // f64's MAX_EXPONENT is 18 while its table has 24 entries, and upstream indexes the table,
        // not the constant. A reader that bounded by MAX_EXPONENT would reject a legal file.
        double[] decoded = DecodeDouble([1], exponentE: 0, exponentF: 20);
        Assert.Equal(1 * 1e20, decoded[0]);
    }

    [Fact]
    public void AnIntegerDTypeIsRejected()
    {
        byte[] encoded = TestBuffers.Int64(1);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(0, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 1));
    }

    [Fact]
    public void AnF16DTypeIsRejected()
    {
        // ALP is defined for f32 and f64 only; there is no half-precision table.
        byte[] encoded = TestBuffers.Int64(1);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(0, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f16 = harness.Types.Primitive(PType.F16, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f16, 1));
    }

    [Fact]
    public void AnEncodedChildOfTheWrongWidthIsRejected()
    {
        // f64 encodes to i64; an i32 child would read two values out of every eight bytes.
        byte[] encoded = TestBuffers.Int32(1, 2);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(0, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 2));
    }

    [Fact]
    public void ABufferOnTheNodeIsRejected()
    {
        byte[] encoded = TestBuffers.Int64(1);
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(0, 0))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyFloatColumn()
    {
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(1, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 0));

        Assert.Equal(CanonicalKind.Primitive, node.Kind);
        Assert.Equal(0, node.Length);
    }

    private static double[] DecodeDouble(long[] encoded, uint exponentE, uint exponentF)
    {
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(exponentE, exponentF))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Int64(encoded));
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, encoded.Length));

        double[] values = new double[encoded.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Value(node, i);
        }

        return values;
    }

    private static float[] DecodeSingle(int[] encoded, uint exponentE, uint exponentF)
    {
        TestNode root = new TestNode("vortex.alp")
            .WithMetadata(TestMetadata.Alp(exponentE, exponentF))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Int32(encoded));
        DType f32 = harness.Types.Primitive(PType.F32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f32, encoded.Length));

        float[] values = new float[encoded.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(node.Values.Span.Slice(i * 4, 4));
        }

        return values;
    }

    private static double Value(CanonicalNode node, int index) =>
        BinaryPrimitives.ReadDoubleLittleEndian(node.Values.Span.Slice(index * 8, 8));
}
