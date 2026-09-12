// Metadata arrives as untrusted protobuf. These assert that every shape of damage - truncation,
// a garbage tag, a field outside its declared domain - surfaces as VortexFormatException and
// nothing else, from every metadata-bearing encoding this component owns.
using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class MalformedMetadataTests
{
    // {bit_width: 4, offset: 1024}: field 2 is a varint of 1024, which the codec's domain check
    // rejects. Hand-written because BitPackedMetadata's constructor refuses to build it.
    private static ReadOnlySpan<byte> BitPackedOffset1024 => [0x08, 0x04, 0x10, 0x80, 0x08];

    [Fact]
    public void ABitPackedOffsetOf1024OnTheWireIsRejected()
    {
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(BitPackedOffset1024.ToArray())
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, new byte[512]);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 1024));
    }

    // {values_len: 1, indices_len: 1024, indices_ptype: 1, values_idx_offsets_len: 1,
    //  values_idx_offsets_ptype: 3, offset: 1024}. `RLEData::try_new` is
    // `vortex_ensure!(offset < 1024, "Offset must be smaller than 1024")` and `deserialize` goes
    // through it, so the reference cannot produce or accept this node. Without the bound the
    // decoder happily reads it, taking chunk 1's value window for row 0 - a chunk-to-offsets
    // mapping the reference never produces for any input it accepts.
    private static ReadOnlySpan<byte> RleOffset1024 =>
        [0x08, 0x01, 0x10, 0x80, 0x08, 0x18, 0x01, 0x20, 0x01, 0x28, 0x03, 0x30, 0x80, 0x08];

    [Fact]
    public void AnRleOffsetOf1024OnTheWireIsRejected() =>
        AssertRejects("fastlanes.rle", RleOffset1024.ToArray());

    [Fact]
    public void AnRleOffsetOf1024CannotEvenBeConstructed() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RleMetadata(1, 1024, PType.U16, 1, PType.U64, RleMetadata.OffsetLimit));

    [Fact]
    public void AnRleOffsetJustBelowTheLimitIsStillAccepted()
    {
        RleMetadata value = new RleMetadata(1, 1024, PType.U16, 1, PType.U64, RleMetadata.OffsetLimit - 1);
        Assert.Equal(1023UL, value.Offset);
    }

    [Theory]
    [InlineData("fastlanes.bitpacked")]
    [InlineData("vortex.runend")]
    [InlineData("vortex.dict")]
    [InlineData("vortex.sparse")]
    [InlineData("vortex.sequence")]
    [InlineData("fastlanes.rle")]
    [InlineData("fastlanes.for")]
    public void ATruncatedVarintInTheMetadataIsRejected(string encoding)
    {
        // 0x08 is "field 1, varint"; 0x80 opens a continuation byte that never arrives.
        AssertRejects(encoding, [0x08, 0x80]);
    }

    [Theory]
    [InlineData("fastlanes.bitpacked")]
    [InlineData("vortex.runend")]
    [InlineData("vortex.dict")]
    [InlineData("vortex.sparse")]
    [InlineData("vortex.sequence")]
    [InlineData("fastlanes.rle")]
    [InlineData("fastlanes.for")]
    public void ATruncatedLengthDelimitedFieldIsRejected(string encoding)
    {
        // Field 1, length-delimited, declaring 100 bytes that are not there.
        AssertRejects(encoding, [0x0A, 0x64, 0x01, 0x02]);
    }

    [Theory]
    [InlineData("fastlanes.bitpacked")]
    [InlineData("vortex.runend")]
    [InlineData("vortex.dict")]
    [InlineData("vortex.sparse")]
    [InlineData("vortex.sequence")]
    [InlineData("fastlanes.rle")]
    [InlineData("fastlanes.for")]
    public void AGroupWireTypeIsRejected(string encoding)
    {
        // Wire types 3 and 4 are proto2 groups, which docs/02-format.md §5.3's read-forever rule
        // does NOT ask us to tolerate.
        AssertRejects(encoding, [0x0B, 0x0C]);
    }

    [Fact]
    public void AnUndefinedPTypeTagIsRejected()
    {
        // RunEndMetadata.ends_ptype = 99. Prost coerces an unknown enum to its default; we must not.
        TestNode root = new TestNode("vortex.runend")
            .WithMetadata([0x08, 0x63, 0x10, 0x01, 0x18, 0x00])
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(1), TestBuffers.Int32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void AnUnknownFieldNumberIsTolerated()
    {
        // Read-forever (docs/02-format.md §5.3): a metadata message may gain an optional field
        // without a new encoding id, so an unrecognized field number is SKIPPED - the mirror image
        // of the domain checks above, which reject.
        byte[] metadata = TestMetadata.RunEnd(PType.U32, 1, 0);
        byte[] extended = new byte[metadata.Length + 3];
        metadata.CopyTo(extended, 0);
        extended[metadata.Length] = 0xF8;       // field 31, varint
        extended[metadata.Length + 1] = 0x01;
        extended[metadata.Length + 2] = 0x2A;

        TestNode root = new TestNode("vortex.runend")
            .WithMetadata(extended)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(1), TestBuffers.Int32(42));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 1));
        Assert.Equal([42], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void AMissingSparsePatchDescriptorIsRejected()
    {
        // SparseMetadata.patches is required; an empty body carries none.
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata([])
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        ScalarStore store = new();
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestMetadata.Scalar(store.Int64(0)), TestBuffers.UInt32(0), TestBuffers.Int32(1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void ASignedPatchIndicesPTypeOnTheWireIsRejected()
    {
        // SparseMetadata { patches { len: 1, offset: 0, indices_ptype: I32 (6) } }, hand-written
        // because PatchesMetadata.Create refuses to build it.
        byte[] metadata = [0x0A, 0x06, 0x08, 0x01, 0x10, 0x00, 0x18, 0x06];

        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(metadata)
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        ScalarStore store = new();
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestMetadata.Scalar(store.Int64(0)), TestBuffers.Int32(0), TestBuffers.Int32(1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    private static void AssertRejects(string encoding, byte[] metadata)
    {
        TestNode root = Shape(encoding).WithMetadata(metadata);
        using DecodeHarness harness = DecodeHarness.Load(
            root,
            new byte[4096],
            TestBuffers.UInt32(0, 1),
            TestBuffers.Int32(0, 1),
            TestBuffers.UInt64(0, 1));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 4));
    }

    // The largest legal child/buffer shape for each encoding, so the metadata parse is always what
    // fails first rather than a child-count check.
    private static TestNode Shape(string encoding) => encoding switch
    {
        "fastlanes.bitpacked" => new TestNode(encoding).WithBuffer(0),
        "vortex.runend" => new TestNode(encoding)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2)),
        "vortex.dict" => new TestNode(encoding)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2)),
        "vortex.sparse" => new TestNode(encoding)
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2)),
        "vortex.sequence" => new TestNode(encoding),
        "fastlanes.rle" => new TestNode(encoding)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3)),
        _ => new TestNode(encoding).WithChild(new TestNode("vortex.primitive").WithBuffer(2)),
    };
}
