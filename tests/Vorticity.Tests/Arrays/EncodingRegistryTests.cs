// Contract §2.3: classification at open, failure at use. These lock in both halves - every id the
// frozen editions declare resolves the way the registry says it does, and an id we do not decode
// resolves to Unknown WITHOUT throwing.
using System;
using System.Text;
using Vorticity;
using Vorticity.Arrays;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class EncodingRegistryTests
{
    [Theory]
    [InlineData("vortex.null", ArrayEncodingId.Null)]
    [InlineData("vortex.bool", ArrayEncodingId.Bool)]
    [InlineData("vortex.primitive", ArrayEncodingId.Primitive)]
    [InlineData("vortex.decimal", ArrayEncodingId.Decimal)]
    [InlineData("vortex.varbin", ArrayEncodingId.VarBin)]
    [InlineData("vortex.varbinview", ArrayEncodingId.VarBinView)]
    [InlineData("vortex.struct", ArrayEncodingId.Struct)]
    [InlineData("vortex.list", ArrayEncodingId.List)]
    [InlineData("vortex.listview", ArrayEncodingId.ListView)]
    [InlineData("vortex.fixed_size_list", ArrayEncodingId.FixedSizeList)]
    [InlineData("vortex.ext", ArrayEncodingId.Extension)]
    [InlineData("vortex.chunked", ArrayEncodingId.Chunked)]
    [InlineData("vortex.constant", ArrayEncodingId.Constant)]
    [InlineData("vortex.masked", ArrayEncodingId.Masked)]
    [InlineData("fastlanes.for", ArrayEncodingId.FastLanesFor)]
    [InlineData("fastlanes.bitpacked", ArrayEncodingId.FastLanesBitPacked)]
    [InlineData("fastlanes.rle", ArrayEncodingId.FastLanesRle)]
    [InlineData("vortex.zigzag", ArrayEncodingId.ZigZag)]
    [InlineData("vortex.runend", ArrayEncodingId.RunEnd)]
    [InlineData("vortex.dict", ArrayEncodingId.Dict)]
    [InlineData("vortex.sparse", ArrayEncodingId.Sparse)]
    [InlineData("vortex.sequence", ArrayEncodingId.Sequence)]
    [InlineData("vortex.bytebool", ArrayEncodingId.ByteBool)]
    public void EveryImplementedArrayIdResolves(string id, ArrayEncodingId expected)
    {
        Assert.Equal(expected, EncodingRegistry.ResolveArray(Encoding.UTF8.GetBytes(id)));
    }

    [Theory]
    [InlineData("vortex.alp")]
    [InlineData("vortex.alprd")]
    [InlineData("vortex.fsst")]
    [InlineData("vortex.onpair")]
    [InlineData("vortex.datetimeparts")]
    [InlineData("vortex.decimal_byte_parts")]
    [InlineData("vortex.zstd")]
    [InlineData("vortex.zstd_buffers")]
    [InlineData("vortex.pco")]
    [InlineData("vortex.map")]
    [InlineData("vortex.variant")]
    [InlineData("vortex.parquet.variant")]
    [InlineData("fastlanes.delta")]
    [InlineData("vortex.patched")]
    public void EveryDeferredArrayIdIsUnknownAndDescribed(string id)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(id);
        Assert.Equal(ArrayEncodingId.Unknown, EncodingRegistry.ResolveArray(utf8));
        Assert.NotNull(EncodingRegistry.DescribeUnsupported(utf8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("vortex.")]
    [InlineData("vortex.nul")]
    [InlineData("vortex.nulll")]
    [InlineData("vortex.boop")]
    [InlineData("vortex.structt")]
    [InlineData("vortex.sparsx")]
    [InlineData("fastlanes.fox")]
    [InlineData("fastlanes.rlf")]
    [InlineData("wortex.bool")]
    [InlineData("vortex/bool")]
    [InlineData("VORTEX.BOOL")]
    [InlineData("vortex.acme.future_codec_2099")]
    public void AnUnrecognizedArrayIdIsUnknownAndNotAnError(string id)
    {
        Assert.Equal(ArrayEncodingId.Unknown, EncodingRegistry.ResolveArray(Encoding.UTF8.GetBytes(id)));
    }

    [Fact]
    public void ANonUtf8IdIsUnknownRatherThanADecodeFailure()
    {
        // Ids are file-controlled bytes; the matcher must never transcode one.
        byte[] garbage = [0xFF, 0xFE, 0x00, 0x80, 0xC0, 0xC1, 0xF5, 0xFF, 0x41, 0x42, 0x43];
        Assert.Equal(ArrayEncodingId.Unknown, EncodingRegistry.ResolveArray(garbage));
        Assert.Equal(LayoutEncodingId.Unknown, EncodingRegistry.ResolveLayout(garbage));
    }

    [Theory]
    [InlineData("vortex.flat", LayoutEncodingId.Flat)]
    [InlineData("vortex.chunked", LayoutEncodingId.Chunked)]
    [InlineData("vortex.struct", LayoutEncodingId.Struct)]
    [InlineData("vortex.dict", LayoutEncodingId.Dict)]
    [InlineData("vortex.zoned", LayoutEncodingId.Zoned)]
    [InlineData("vortex.stats", LayoutEncodingId.Stats)]
    public void EveryImplementedLayoutIdResolves(string id, LayoutEncodingId expected)
    {
        Assert.Equal(expected, EncodingRegistry.ResolveLayout(Encoding.UTF8.GetBytes(id)));
    }

    [Fact]
    public void TheExperimentalListLayoutIsUnknownWithItsOwnNote()
    {
        byte[] utf8 = "vortex.list"u8.ToArray();
        Assert.Equal(LayoutEncodingId.Unknown, EncodingRegistry.ResolveLayout(utf8));
        Assert.Equal("experimental list layout; in no core edition", EncodingRegistry.DescribeUnsupported(utf8));
    }

    [Fact]
    public void TheThreeNotesContractTwoPointEightPinsAreVerbatim()
    {
        Assert.Equal(
            "in no core edition; a default writer cannot emit it",
            EncodingRegistry.DescribeUnsupported("fastlanes.delta"u8));
        Assert.Equal(
            "in-memory only upstream; never produced by a conformant writer",
            EncodingRegistry.DescribeUnsupported("vortex.patched"u8));
        Assert.Equal(
            "experimental list layout; in no core edition",
            EncodingRegistry.DescribeUnsupported("vortex.list"u8));
    }

    [Fact]
    public void AGenuinelyUnknownIdHasNoNote()
    {
        Assert.Null(EncodingRegistry.DescribeUnsupported("vortex.acme.future"u8));
        Assert.Null(EncodingRegistry.DescribeUnsupported(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void GettingADecoderForAnUnknownIdNamesTheIdAndTheKind()
    {
        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(
                () => ArrayDecoderTable.Get(ArrayEncodingId.Unknown, "vortex.alp"));

        Assert.Equal("vortex.alp", error.ComponentId);
        Assert.Equal(VortexComponentKind.Array, error.Kind);
        Assert.Contains("vortex.alp", error.Message, StringComparison.Ordinal);
        Assert.Contains("array", error.Message, StringComparison.Ordinal);
        Assert.Contains("Phase 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GettingADecoderForAnIdWithNoNoteStillNamesIt()
    {
        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(
                () => ArrayDecoderTable.Get(ArrayEncodingId.Unknown, "vortex.acme.future"));

        Assert.Equal("vortex.acme.future", error.ComponentId);
        Assert.Equal(VortexComponentKind.Array, error.Kind);
    }

    [Fact]
    public void AnOutOfRangeEncodingIdIsUnsupportedRatherThanAnIndexOutOfRange()
    {
        Assert.Throws<VortexUnsupportedException>(
            () => ArrayDecoderTable.Get((ArrayEncodingId)9999, "vortex.whatever"));
        Assert.False(ArrayDecoderTable.IsImplemented((ArrayEncodingId)9999));
        Assert.False(ArrayDecoderTable.IsImplemented(ArrayEncodingId.Unknown));
    }
}
