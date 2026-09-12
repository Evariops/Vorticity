using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class SequenceDecoderTests
{
    [Fact]
    public void GeneratesBasePlusIndexTimesMultiplier()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(2000), store.Int64(14)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, 5));

        Assert.Equal([2000L, 2014L, 2028L, 2042L, 2056L], ReadInt64(node));
    }

    [Fact]
    public void ANegativeMultiplierDescends()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(10), store.Int64(-3)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, 4));

        Assert.Equal([10L, 7L, 4L, 1L], ReadInt64(node));
    }

    [Fact]
    public void AZeroLengthSequenceIsRejected()
    {
        // "SequenceArray length must be greater than zero" - the corpus manifest skips
        // encodings/sequence_r0 for exactly this reason.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.Int64(1)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 0));
    }

    [Fact]
    public void AnOverflowingLastValueIsRejectedBeforeAnythingIsGenerated()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(120), store.Int64(1)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i8 = harness.Types.Primitive(PType.I8, Nullability.NonNullable);

        // 120 + 7 = 127 fits; 120 + 8 does not.
        Assert.Equal(8, harness.Node(harness.DecodeRoot(i8, 8)).Length);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i8, 9));
    }

    [Fact]
    public void AnUnderflowingLastValueIsRejected()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(-120), store.Int64(-1)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i8 = harness.Types.Primitive(PType.I8, Nullability.NonNullable);

        Assert.Equal(9, harness.Node(harness.DecodeRoot(i8, 9)).Length);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i8, 10));
    }

    [Fact]
    public void AUInt64MultiplierOverAnUnsignedOutputIsAccepted()
    {
        // The multiplier's ptype comes from the proto oneof tag, not from the output dtype.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.UInt64(5), store.UInt64(1000)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 3));

        Assert.Equal([5u, 1005u, 2005u], ReadUInt32(node));
    }

    [Fact]
    public void AUInt64MultiplierOverASignedOutputRespectsTheSignedRoom()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.UInt64(1_000_000_000)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);

        // 0 + 2 * 1e9 fits an i32; 3 * 1e9 does not.
        Assert.Equal(3, harness.Node(harness.DecodeRoot(i32, 3)).Length);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 4));
    }

    [Fact]
    public void AFloatMultiplierIsRejected()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.F64(1.5)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 4));
    }

    [Fact]
    public void ABaseOutsideTheOutputPTypeIsRejected()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(1000), store.Int64(0)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i8 = harness.Types.Primitive(PType.I8, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i8, 4));
    }

    [Fact]
    public void AMissingMultiplierIsRejected()
    {
        // SequenceMetadata.Read raises "multiplier required" for a body that carries only a base.
        ScalarStore store = new();
        byte[] baseOnly = TestMetadata.Sequence(store.Int64(1), store.Int64(1));
        byte[] truncated = baseOnly.AsSpan(0, 3).ToArray();

        TestNode root = new TestNode("vortex.sequence").WithMetadata(truncated);
        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 2));
    }

    [Fact]
    public void ChildrenOrBuffersAreRejected()
    {
        ScalarStore store = new();
        byte[] metadata = TestMetadata.Sequence(store.Int64(0), store.Int64(1));

        TestNode withBuffer = new TestNode("vortex.sequence").WithMetadata(metadata).WithBuffer(0);
        using (DecodeHarness harness = DecodeHarness.Load(withBuffer, new byte[4]))
        {
            DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
            Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 2));
        }

        TestNode withChild = new TestNode("vortex.sequence")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));
        using (DecodeHarness harness = DecodeHarness.Load(withChild, new byte[16]))
        {
            DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
            Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 2));
        }
    }

    [Fact]
    public void ANullableSequenceIsAllValid()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(1), store.Int64(1)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, 3));
        Assert.Equal(ValidityKind.AllValid, node.Validity.Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8193)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(-5), store.Int64(3)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, rows));

        Assert.Equal(rows, node.Length);
        long[] values = ReadInt64(node);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(-5L + (3L * i), values[i]);
        }
    }

    private static long[] ReadInt64(CanonicalNode node)
    {
        long[] values = new long[node.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(i * 8, 8));
        }

        return values;
    }

    private static uint[] ReadUInt32(CanonicalNode node)
    {
        uint[] values = new uint[node.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(node.Values.Span.Slice(i * 4, 4));
        }

        return values;
    }
}
