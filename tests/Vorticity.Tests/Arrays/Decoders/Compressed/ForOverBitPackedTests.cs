using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// A frame of reference over bit-packed values, which adds its reference in the pass that unpacks
/// them: every width, the blocks a window cuts at either end, the patches, and a range.
/// </summary>
public sealed class ForOverBitPackedTests
{
    [Theory]
    [InlineData(PType.U8, 8)]
    [InlineData(PType.U16, 16)]
    [InlineData(PType.U32, 32)]
    [InlineData(PType.U64, 64)]
    internal void AddsTheReferenceToEveryValueAtEveryBitWidth(PType ptype, int elementBits)
    {
        ulong reference = ReferenceFor(elementBits);
        foreach (int offset in (int[])[0, 7])
        {
            // Two whole blocks and part of a third, from a row that cuts the first one too.
            int rows = (2 * FastLanes.BlockSize) + 300 - offset;
            for (int bitWidth = 0; bitWidth <= elementBits; bitWidth++)
            {
                ulong[] values = TestPacking.Ramp(3, bitWidth);
                byte[] packed = TestPacking.Pack(values, bitWidth, elementBits);
                TestNode root = new TestNode("fastlanes.for")
                    .WithMetadata(ForDecoderTests.Reference(store => store.UInt64(reference)))
                    .WithChild(new TestNode("fastlanes.bitpacked")
                        .WithMetadata(TestMetadata.BitPacked((uint)bitWidth, (uint)offset))
                        .WithBuffer(0));

                using DecodeHarness harness = DecodeHarness.Load(root, packed);
                DType dtype = harness.Types.Primitive(ptype, Nullability.NonNullable);
                CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, rows));

                Assert.Equal(rows, node.Length);
                for (int i = 0; i < rows; i++)
                {
                    Assert.Equal(Wrap(unchecked(values[offset + i] + reference), elementBits), Read(node, i, elementBits / 8));
                }
            }
        }
    }

    [Fact]
    public void AddsTheReferenceToThePatchesToo()
    {
        const ulong reference = 1_000_000;
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(ForDecoderTests.Reference(store => store.UInt64(reference)))
            .WithChild(new TestNode("fastlanes.bitpacked")
                .WithMetadata(TestMetadata.BitPacked(4, 0, PatchesMetadata.Create(2, 0, PType.U32)))
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
                .WithChild(new TestNode("vortex.primitive").WithBuffer(2)));

        using DecodeHarness harness = DecodeHarness.Load(
            root, packed, TestBuffers.UInt32(1, 3), TestBuffers.UInt32(7777, 8888));
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 8));

        Assert.Equal(values[0] + reference, Read(node, 0, 4));
        Assert.Equal(7777 + reference, Read(node, 1, 4));
        Assert.Equal(values[2] + reference, Read(node, 2, 4));
        Assert.Equal(8888 + reference, Read(node, 3, 4));
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(700, 1500)]
    [InlineData(1023, 2)]
    [InlineData(2048, 300)]
    public void AddsTheReferenceOverARange(int start, int count)
    {
        const int bitWidth = 13;
        const int rows = (2 * FastLanes.BlockSize) + 300;
        ulong reference = ReferenceFor(64);
        ulong[] values = TestPacking.Ramp(3, bitWidth);
        byte[] packed = TestPacking.Pack(values, bitWidth, 64);
        TestNode root = new TestNode("fastlanes.for")
            .WithMetadata(ForDecoderTests.Reference(store => store.UInt64(reference)))
            .WithChild(new TestNode("fastlanes.bitpacked")
                .WithMetadata(TestMetadata.BitPacked(bitWidth, 0))
                .WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u64 = harness.Types.Primitive(PType.U64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(
            harness.Scan.Decode.DecodeRootRange(harness.Scan.Nodes.Root, u64, rows, start, count, keepEncoding: false));

        Assert.Equal(count, node.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(unchecked(values[start + i] + reference), Read(node, i, 8));
        }
    }

    /// <summary>A reference of every other bit, inside the element width, so that sums carry and wrap.</summary>
    private static ulong ReferenceFor(int elementBits) => Wrap(0xA5A5_A5A5_A5A5_A5A5UL, elementBits);

    private static ulong Wrap(ulong value, int elementBits) =>
        elementBits == 64 ? value : value & ((1UL << elementBits) - 1);

    private static ulong Read(CanonicalNode node, int row, int width)
    {
        ReadOnlySpan<byte> bytes = node.Values.Span.Slice(row * width, width);
        ulong value = 0;
        for (int i = 0; i < width; i++)
        {
            value |= (ulong)bytes[i] << (8 * i);
        }

        return value;
    }
}
