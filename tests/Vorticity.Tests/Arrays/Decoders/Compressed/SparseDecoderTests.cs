using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class SparseDecoderTests
{
    [Fact]
    public void FillsTheArrayAndScattersThePatches()
    {
        ScalarStore store = new();
        TestNode root = Node(
            TestMetadata.Sparse(PatchesMetadata.Create(3, 0, PType.U32)),
            TestMetadata.Scalar(store.Int64(7)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(7)),
            TestBuffers.UInt32(1, 4, 5),
            TestBuffers.Int32(100, 200, 300));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 6));
        Assert.Equal([7, 100, 7, 7, 200, 300], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ThePatchOffsetShiftsEveryIndex()
    {
        ScalarStore store = new();
        TestNode root = Node(
            TestMetadata.Sparse(PatchesMetadata.Create(2, 10, PType.U32)),
            TestMetadata.Scalar(store.Int64(0)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(0)),
            TestBuffers.UInt32(10, 13),
            TestBuffers.Int32(5, 6));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));
        Assert.Equal([5, 0, 0, 6], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ANullFillMakesEveryUnpatchedRowNull()
    {
        ScalarStore store = new();
        TestNode root = Node(
            TestMetadata.Sparse(PatchesMetadata.Create(2, 0, PType.U32)),
            TestMetadata.Scalar(store.Null()));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Null()),
            TestBuffers.UInt32(1, 3),
            TestBuffers.Int32(11, 33));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 5));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b0_1010, bits.Bits.Span[0] & 0b1_1111);
        int[] values = ForDecoderTests.ReadInt32(node);
        Assert.Equal(11, values[1]);
        Assert.Equal(33, values[3]);
    }

    [Fact]
    public void ANullPatchValueOverridesANonNullFill()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(2, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(2)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(9)),
            TestBuffers.UInt32(0, 2),
            TestBuffers.Int32(1, 2),
            TestBuffers.Bitmap(true, false));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 3));

        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b011, bits.Bits.Span[0] & 0b111);
        Assert.Equal([1, 9, 0], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void AShortVarBinViewFillIsInlinedAndALongOneGetsItsOwnBuffer()
    {
        ScalarStore store = new();
        byte[] longFill = Encoding.UTF8.GetBytes("this fill value is far too long to inline");
        byte[] patchData = Encoding.UTF8.GetBytes("patched-value-that-is-long-enough");
        byte[] views = new byte[16];
        DictDecoderTests.WriteView(views, patchData, 0, patchData.Length);

        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.varbinview").WithBuffer(2).WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.String(longFill)),
            TestBuffers.UInt32(1),
            patchData,
            views);

        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 3));

        // The fill needs a data buffer of its own at index 0, so the patch view's buffer index
        // must have been shifted from 0 to 1.
        Assert.Equal(2, node.DataBufferCount);
        Assert.Equal("this fill value is far too long to inline", DictDecoderTests.ReadValue(node, 0));
        Assert.Equal("patched-value-that-is-long-enough", DictDecoderTests.ReadValue(node, 1));
        Assert.Equal("this fill value is far too long to inline", DictDecoderTests.ReadValue(node, 2));
    }

    [Fact]
    public void AShortVarBinViewFillNeedsNoExtraBuffer()
    {
        ScalarStore store = new();
        byte[] patchData = Encoding.UTF8.GetBytes("patched-value-that-is-long-enough");
        byte[] views = new byte[16];
        DictDecoderTests.WriteView(views, patchData, 0, patchData.Length);

        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.varbinview").WithBuffer(2).WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.String("short"u8)),
            TestBuffers.UInt32(0),
            patchData,
            views);

        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 2));

        Assert.Equal(1, node.DataBufferCount);
        Assert.Equal("patched-value-that-is-long-enough", DictDecoderTests.ReadValue(node, 0));
        Assert.Equal("short", DictDecoderTests.ReadValue(node, 1));
    }

    [Fact]
    public void ABoolFillAndBoolPatchesDecode()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(2, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.bool").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Bool(true)),
            TestBuffers.UInt32(1, 3),
            TestBuffers.Bitmap(false, true));

        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(boolType, 5));

        Assert.Equal(CanonicalKind.Bool, node.Kind);
        bool[] expected = [true, false, true, true, true];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], (node.Bits.Span[i >> 3] & (1 << (i & 7))) != 0);
        }
    }

    [Fact]
    public void AThirdChildIsRejected()
    {
        // Upstream passes `None` for chunk offsets unconditionally on this path, so sparse has
        // exactly two children even when the descriptor declares chunk offsets.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(
                PatchesMetadata.CreateChunked(1, 0, PType.U32, 1, PType.U32, 0)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(0)),
            TestBuffers.UInt32(0),
            TestBuffers.Int32(1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void AMissingFillValueBufferIsRejected()
    {
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(0), TestBuffers.Int32(1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void AnEmptyFillValueBufferIsRejected()
    {
        // A zero-byte ScalarValue message carries no kind: "Scalar value missing kind".
        TestNode root = Node(
            TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)), []);

        using DecodeHarness harness = DecodeHarness.Load(
            root, Array.Empty<byte>(), TestBuffers.UInt32(0), TestBuffers.Int32(1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
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
        int patchCount = Math.Max(1, rows / 100);
        uint[] indices = new uint[patchCount];
        int[] values = new int[patchCount];
        for (int i = 0; i < patchCount; i++)
        {
            indices[i] = (uint)(i * 100);
            values[i] = i + 1;
        }

        TestNode root = Node(
            TestMetadata.Sparse(PatchesMetadata.Create((ulong)patchCount, 0, PType.U32)),
            TestMetadata.Scalar(store.Int64(-1)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(-1)),
            TestBuffers.UInt32(indices),
            TestBuffers.Int32(values));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, rows));
        int[] decoded = ForDecoderTests.ReadInt32(node);

        for (int i = 0; i < rows; i++)
        {
            int expected = i % 100 == 0 && i / 100 < patchCount ? (i / 100) + 1 : -1;
            Assert.Equal(expected, decoded[i]);
        }
    }

    [Fact]
    public void DecimalFillAndPatchesDecode()
    {
        ScalarStore store = new();
        byte[] fillBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(fillBytes, 12345);

        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.decimal")
                .WithMetadata(TestMetadata.Decimal(DecimalStorageType.I64))
                .WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Bytes(fillBytes)),
            TestBuffers.UInt32(1),
            TestBuffers.Int64(999));

        DType dec = harness.Types.Decimal(18, 4, Nullability.NonNullable);

        // vortex.decimal is the canonical decoders' encoding, not ours; without it the child
        // cannot be decoded and the failure is an unsupported-encoding one, not a wrong value.
        if (!ArrayDecoderTable.IsImplemented(ArrayEncodingId.Decimal))
        {
            Assert.Throws<VortexUnsupportedException>(() => harness.DecodeRoot(dec, 2));
            return;
        }

        CanonicalNode node = harness.Node(harness.DecodeRoot(dec, 2));
        Assert.Equal(CanonicalKind.Decimal, node.Kind);
        Assert.Equal(12345L, BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span[..8]));
        Assert.Equal(999L, BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(8, 8)));
    }

    private static TestNode Node(byte[] metadata, byte[] fill)
    {
        _ = fill;
        return new TestNode("vortex.sparse")
            .WithMetadata(metadata)
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));
    }
}
