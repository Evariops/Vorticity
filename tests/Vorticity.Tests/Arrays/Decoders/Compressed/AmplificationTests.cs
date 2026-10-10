// Three of these encodings produce output out of all proportion to their input, which makes a
// tiny file an unbounded allocation unless something stops it:
//
//   * vortex.sequence has no children and no buffers at all - a 9-byte metadata and a row count;
//   * fastlanes.bitpacked at bit width 0 has an EMPTY packed buffer and still yields `len` rows;
//   * vortex.sparse needs only a fill scalar plus one patch.
//
// The row count reaches a decoder from far away - the layout's row_count, or a chunked parent's
// chunk_offsets - and nothing between there and the allocation bounds the product, so the guard
// has to live here. It is the read options' MaxDecompressedBytes, not a number invented at the call
// site, and a caller with a genuinely larger column raises it.
using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.File;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class AmplificationTests
{
    // 800 MB of i64 out of a nine-byte metadata.
    private const int HugeRowCount = 100_000_000;

    [Fact]
    public void ASequenceCannotConjureMoreThanTheDecompressionCeiling()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.Int64(1)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);

        VortexFormatException error =
            Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, HugeRowCount));
        Assert.Contains("decompression ceiling", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowCountThatOverflowsTheByteLengthIsRejectedBeforeTheCeilingIsConsulted()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.Int64(0)));

        using DecodeHarness harness = DecodeHarness.Load(root);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);

        // 2^28 rows of i64 is 2^31 bytes, one past int.MaxValue.
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 1 << 28));
    }

    [Fact]
    public void AZeroBitWidthBitPackedNodeCannotConjureMoreThanTheCeiling()
    {
        // The packed buffer is genuinely empty at bit width 0, so the file is a few dozen bytes.
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(0, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType u64 = harness.Types.Primitive(PType.U64, Nullability.NonNullable);

        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u64, HugeRowCount));
    }

    [Fact]
    public void ASparseNodeCannotConjureMoreThanTheCeiling()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(1, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(1)),
            TestBuffers.UInt32(0),
            TestBuffers.Int64(1));

        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, HugeRowCount));
    }

    [Fact]
    public void RaisingTheCeilingLetsALargeColumnThrough()
    {
        // The guard is policy, not a hard limit: the option exists so a caller who really does
        // read a 300 MiB column can.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sequence")
            .WithMetadata(TestMetadata.Sequence(store.Int64(0), store.Int64(1)));

        const int rows = 40_000_000;   // 320 MB of i64, above the 256 MiB default

        byte[] segment = TestBlob.Build(root, [], out string[] specs);
        TestDecoders.EnsureRegistered();

        using (ScanContext tight = new(specs))
        {
            Load(tight, segment);
            DType i64 = tight.Types.Primitive(PType.I64, Nullability.NonNullable);
            Assert.Throws<VortexFormatException>(
                () => tight.Decode.DecodeRoot(tight.Nodes.Root, i64, rows));
        }

        VortexReadOptions relaxed = new() { MaxDecompressedBytes = 1L << 31 };
        using (ScanContext wide = new(specs, relaxed))
        {
            Load(wide, segment);
            DType i64 = wide.Types.Primitive(PType.I64, Nullability.NonNullable);
            int decoded = wide.Decode.DecodeRoot(wide.Nodes.Root, i64, rows);
            Assert.Equal(rows, wide.Canonical.GetNode(decoded).Length);
        }
    }

    private static void Load(ScanContext scan, byte[] segment)
    {
        byte[] pinned = GC.AllocateArray<byte>(segment.Length, pinned: true);
        segment.CopyTo(pinned, 0);
        ArrayBlobReader.Load(
            scan.Nodes, Vorticity.Buffers.VortexBuffer.FromPinned(pinned, 0), scan.ArrayEncodings);
    }
}
