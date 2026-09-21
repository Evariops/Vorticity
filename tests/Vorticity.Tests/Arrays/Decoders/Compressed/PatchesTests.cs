// The rules Patches enforces on a patch set. Every one of them is exercised through
// vortex.sparse, which is the shortest path to Patches.Create with a real decoded indices child.
using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class PatchesTests
{
    [Fact]
    public void ChunkSizeIsTheSharedPatchChunkSize()
    {
        Assert.Equal(1024, Patches.ChunkSize);
        Assert.Equal(PatchesMetadata.ChunkSize, Patches.ChunkSize);
    }

    [Fact]
    public void AnEmptyPatchSetIsRejected()
    {
        // "Patch indices must not be empty" - the reason encodings/sparse_r0 and sparse_r1 are in
        // the corpus manifest's `skipped` list.
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(0, 0, PType.U32), [], [], 4));
    }

    [Fact]
    public void UnsortedIndicesAreRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(3, 0, PType.U32), [2, 1, 3], [1, 2, 3], 8));
    }

    [Fact]
    public void EqualAdjacentIndicesAreAccepted()
    {
        // `is_sorted` upstream means non-decreasing, not strictly increasing.
        int[] values = Decode(PatchesMetadata.Create(2, 0, PType.U32), [1, 1], [5, 6], 3);
        Assert.Equal([0, 6, 0], values);
    }

    [Fact]
    public void MorePatchesThanRowsIsRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(3, 0, PType.U32), [0, 1, 2], [1, 2, 3], 2));
    }

    [Fact]
    public void AnIndexBelowTheOffsetIsRejected()
    {
        // Upstream computes `max - offset` as a usize subtraction and would underflow here.
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(2, 5, PType.U32), [3, 6], [1, 2], 4));
    }

    [Fact]
    public void AnIndexAtExactlyOffsetPlusArrayLengthIsRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(1, 2, PType.U32), [6], [1], 4));
    }

    [Fact]
    public void AnIndexAtOffsetPlusArrayLengthMinusOneIsAccepted()
    {
        int[] values = Decode(PatchesMetadata.Create(1, 2, PType.U32), [5], [42], 4);
        Assert.Equal([0, 0, 0, 42], values);
    }

    [Fact]
    public void ASignedIndicesPTypeIsRejectedByTheCodec()
    {
        // PatchesMetadata.ReadBody rejects it before a decoder ever sees it, and the constructor
        // helper refuses to build one at all.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PatchesMetadata.Create(1, 0, PType.I32));
    }

    [Fact]
    public void ANullableIndicesChildIsRejected()
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(2, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(0)),
            TestBuffers.UInt32(0, 1),
            TestBuffers.Int32(1, 2),
            TestBuffers.Bitmap(true, false));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 4));
    }

    [Fact]
    public void IndicesAndValuesOfDifferentLengthsAreRejected()
    {
        // The declared patch count is 3 but only two indices are present, so the indices child
        // decodes to the wrong length.
        Assert.Throws<VortexFormatException>(
            () => Decode(PatchesMetadata.Create(3, 0, PType.U32), [0, 1], [1, 2], 4));
    }

    [Fact]
    public void ChunkOffsetsAreReadAndValidatedButNotUsed()
    {
        // Nothing slices patches, so the chunk offsets have nothing to accelerate. They must
        // still be present and well formed, because their presence is what moves the validity
        // child of a bit-packed node to index 3.
        BitPackedTestShapes.AssertChunkOffsetsShapeDecodes();
    }

    private static int[] Decode(
        in PatchesMetadata metadata, uint[] indices, int[] values, int length)
    {
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(in metadata))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestMetadata.Scalar(store.Int64(0)),
            TestBuffers.UInt32(indices),
            TestBuffers.Int32(values));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        return ForDecoderTests.ReadInt32(harness.Node(harness.DecodeRoot(i32, length)));
    }
}
