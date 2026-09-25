// The guarantees that are not about one encoding: the depth cap, lazy resolution of an id we do not
// implement, arena reuse across batches, truncated metadata, and the class II check that only runs
// when the caller asks for it.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.File;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class DecoderGuardTests
{
    private static byte[] I32(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }

        return bytes;
    }

    private static byte[] U64(params ulong[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
        }

        return bytes;
    }

    [Fact]
    public void ADeeplyNestedTreeIsRejectedNotRecursedInto()
    {
        // Seventy nested single-chunk chunked nodes. VortexLimits.MaxArrayDepth is 64, so this must
        // be a clean VortexFormatException rather than a StackOverflow.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int offsets = b.AddBuffer(U64(0, 2));
        int values = b.AddBuffer(I32(1, 2));

        BlobNode node = new BlobNode("vortex.primitive").WithBuffers(values);
        for (int i = 0; i < 70; i++)
        {
            node = new BlobNode("vortex.chunked").WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(offsets),
                node);
        }

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));
    }

    [Fact]
    public void AnUnimplementedEncodingThrowsUnsupportedWithItsId()
    {
        // Resolution is lazy, and the failure names the id and the kind.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("acme.nonesuch").WithBuffers(b.AddBuffer(I32(1, 2)));

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => h.Decode(b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));

        Assert.Contains("acme.nonesuch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnimplementedEncodingUnderAValidityChildAlsoThrowsUnsupported()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(I32(1, 2));
        int junk = b.AddBuffer([0x00]);

        BlobNode node = new BlobNode("vortex.primitive")
            .WithBuffers(values)
            .WithChildren(new BlobNode("acme.nonesuch").WithBuffers(junk));

        Assert.Throws<VortexUnsupportedException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.Nullable), 2));
    }

    [Fact]
    public void TruncatedMetadataIsMalformedNotIgnored()
    {
        // A varint whose continuation bit is set but whose bytes have run out.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata([0x08, 0x80])
            .WithBuffers(b.AddBuffer([0xFF]));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), 4));
    }

    [Fact]
    public void AnUnknownMetadataFieldNumberIsSkipped()
    {
        // Files are read forever: a metadata message may gain an optional field
        // without a new encoding id, and an unrecognized field number is tolerated.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata([0x08, 0x03, 0x50, 0x01])
            .WithBuffers(b.AddBuffer([0xFF, 0xFF]));

        int index = h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), 4);
        Assert.Equal(3, h.Node(index).BitOffset);
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
    public void TheBoundaryRowCountsAllDecode(int length)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int bits = b.AddBuffer(new byte[(length + 7 + 5) / 8]);
        BlobNode node = new BlobNode("vortex.bool")
            .WithMetadata(TestMetadata.Bool(5))
            .WithBuffers(bits);

        int index = h.Decode(b, node, h.Types.Bool(Nullability.NonNullable), length);
        Assert.Equal(length, h.Node(index).Length);
        Assert.Equal(5, h.Node(index).BitOffset);
    }

    [Fact]
    public void ChunkedValidatesEveryOffsetBeforeDecodingAnyChunk()
    {
        // chunk_offsets = [0, 2^31 - 2] on a two-row array. Checking the last offset only after the
        // loop would have decoded a two-billion-row chunk first; this must fail immediately.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int offsets = b.AddBuffer(U64(0, int.MaxValue - 1));
        int values = b.AddBuffer(I32(1, 2));

        BlobNode node = new BlobNode("vortex.chunked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(offsets),
            new BlobNode("vortex.primitive").WithBuffers(values));

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));
    }

    [Fact]
    public void AConstantCannotConjureAnUnboundedBufferFromASmallNode()
    {
        // fixed_size_list(i64)[100000] over 8192 rows is 819 200 000 elements. The elements child is
        // a single constant node of a few dozen bytes, so nothing in the file is large: the 6.5 GB
        // is conjured entirely by the row count times the dtype's list size. CheckedMultiply catches
        // the products that overflow int; MaxDecompressedSize catches the rest.
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.FixedSizeList(
            h.Types.Primitive(PType.I64, Nullability.NonNullable), 100000, Nullability.NonNullable);

        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.fixed_size_list").WithChildren(
            new BlobNode("vortex.constant").WithBuffers(b.AddBuffer(TestMetadata.ScalarInt64(1))));

        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 8192));
    }

    [Fact]
    public void MaxDecompressedSizeIsHonoured()
    {
        using ScanContext scan = new ScanContext(
            TestEncodings.Ids, new VortexReadOptions { MaxDecompressedSize = 1024 });

        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.constant")
            .WithBuffers(b.AddBuffer(TestMetadata.ScalarInt64(1)));

        PinnedSegment segment = b.Build(node);
        ArrayBlobReader.Load(scan.Nodes, segment.Buffer, scan.ArrayEncodings);

        // 128 i64 rows is exactly 1024 bytes: at the ceiling, not over it.
        DType dtype = scan.Types.Primitive(PType.I64, Nullability.NonNullable);
        int index = scan.Decode.Decode(scan.Nodes.Root, dtype, 128);
        Assert.Equal(1024, scan.Canonical.GetNode(index).Values.Length);

        scan.ResetBatch();
        ArrayBlobReader.Load(scan.Nodes, segment.Buffer, scan.ArrayEncodings);
        Assert.Throws<VortexFormatException>(
            () => scan.Decode.Decode(scan.Nodes.Root, dtype, 1024));

        GC.KeepAlive(segment);
    }

    // Four columns of 128 i64 rows stand for 1024 bytes each: each at the ceiling of one decode,
    // together past a batch ceiling of three.
    [Fact]
    public void MaxBatchDecompressedSizeBoundsTheColumnsOfABatchTogether()
    {
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.struct").WithChildren(Constant(b), Constant(b), Constant(b), Constant(b));
        PinnedSegment segment = b.Build(node);

        using ScanContext narrow = new ScanContext(
            TestEncodings.Ids, new VortexReadOptions { MaxDecompressedSize = 1024, MaxBatchDecompressedSize = 3 * 1024 });
        ArrayBlobReader.Load(narrow.Nodes, segment.Buffer, narrow.ArrayEncodings);
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => narrow.Decode.Decode(narrow.Nodes.Root, FourLongs(narrow), 128));
        Assert.Contains("MaxBatchDecompressedSize", error.Message, StringComparison.Ordinal);

        using ScanContext wide = new ScanContext(
            TestEncodings.Ids, new VortexReadOptions { MaxDecompressedSize = 1024, MaxBatchDecompressedSize = 4 * 1024 });
        for (int batch = 0; batch < 2; batch++)
        {
            // Each batch starts from nothing.
            wide.ResetBatch();
            ArrayBlobReader.Load(wide.Nodes, segment.Buffer, wide.ArrayEncodings);
            int index = wide.Decode.Decode(wide.Nodes.Root, FourLongs(wide), 128);
            Assert.Equal(CanonicalKind.Struct, wide.Canonical.GetNode(index).Kind);
        }

        GC.KeepAlive(segment);

        static BlobNode Constant(BlobBuilder b) =>
            new BlobNode("vortex.constant").WithBuffers(b.AddBuffer(TestMetadata.ScalarInt64(1)));

        static DType FourLongs(ScanContext scan)
        {
            DType i64 = scan.Types.Primitive(PType.I64, Nullability.NonNullable);
            return scan.Types.Struct(["a", "b", "c", "d"], [i64, i64, i64, i64], Nullability.NonNullable);
        }
    }

    [Fact]
    public void ResetBatchLetsTheSameContextDecodeAgain()
    {
        using DecodeHarness h = new DecodeHarness();

        BlobBuilder first = new BlobBuilder();
        BlobNode a = new BlobNode("vortex.primitive").WithBuffers(first.AddBuffer(I32(1, 2, 3)));
        int one = h.Decode(first, a, h.Types.Primitive(PType.I32, Nullability.NonNullable), 3);
        Assert.Equal(3, h.Node(one).Length);

        h.Scan.ResetBatch();
        Assert.Equal(0, h.Canonical.NodeCount);

        BlobBuilder second = new BlobBuilder();
        BlobNode c = new BlobNode("vortex.primitive").WithBuffers(second.AddBuffer(I32(9, 8)));
        int two = h.Decode(second, c, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2);
        Assert.Equal(2, h.Node(two).Length);
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(h.Node(two).Values.Span[..4]));
    }

    [Fact]
    public void MaskedVerifiesItsChildOnlyWhenAsked()
    {
        // The child is a nullable-looking constant(false) under a non-nullable dtype... which is
        // itself rejected, so instead the child carries an explicit all-invalid validity that the
        // masked contract forbids. Class II: silent by default, loud under VerifyStatistics.
        BlobBuilder Build(out BlobNode node)
        {
            BlobBuilder b = new BlobBuilder();
            int values = b.AddBuffer(I32(1, 2));
            int offsets = b.AddBuffer(U64(0, 2));
            int allNull = b.AddBuffer(TestMetadata.ScalarBool(false));

            // A chunked child whose single chunk is all-null: the chunk's dtype is non-nullable so
            // the validity comes from the explicit child, which is what upstream's all_valid()
            // assertion is there to catch.
            node = new BlobNode("vortex.masked").WithChildren(
                new BlobNode("vortex.chunked").WithChildren(
                    new BlobNode("vortex.primitive").WithBuffers(offsets),
                    new BlobNode("vortex.primitive")
                        .WithBuffers(values)
                        .WithChildren(new BlobNode("vortex.constant").WithBuffers(allNull))));
            return b;
        }

        using (DecodeHarness quiet = new DecodeHarness())
        {
            BlobBuilder b = Build(out BlobNode node);
            int index = quiet.Decode(b, node, quiet.Types.Primitive(PType.I32, Nullability.Nullable), 2);
            Assert.Equal(ValidityKind.AllValid, quiet.Node(index).Validity.Kind);
        }

        using ScanContext loud = new ScanContext(
            TestEncodings.Ids, new VortexReadOptions { VerifyStatistics = true });
        BlobBuilder builder = Build(out BlobNode loudNode);
        PinnedSegment segment = builder.Build(loudNode);
        ArrayBlobReader.Load(loud.Nodes, segment.Buffer, loud.ArrayEncodings);

        Assert.Throws<VortexFormatException>(() => loud.Decode.Decode(
            loud.Nodes.Root, loud.Types.Primitive(PType.I32, Nullability.Nullable), 2));
        GC.KeepAlive(segment);
    }
}
