// pco chunk metadata, checked field by field against the reference implementation.
//
// THE EXPECTED VALUES BELOW ARE NOT MINE. They come from
// `tools/conformance-gen/examples/dump_pco.rs`, which parses the same bytes with pco's own crate:
//
//   format_version FormatVersion { major: 4, minor: 1 }
//   mode           IntMult(U64(3))
//   delta_encoding Consecutive { order: 1, secondary_uses_delta: false }
//   per_latent_var:
//     Primary:   bins U64([Bin { weight: 1, lower: 9223372036854775809, offset_bits: 0 }]), ans_size_log 0
//     Secondary: bins U64([Bin { weight: 1, lower: 1, offset_bits: 0 }]),                   ans_size_log 0
//
// Reproduce by re-running the extraction below and piping the bytes into that example. A parser
// checked against its author's reading of a spec tests the reading; this tests the parser.
//
// THE FILE IS ALSO MORE INTERESTING THAN "THE SIMPLE CASE", which is why it is worth saying what it
// exercises: not Classic mode but IntMult with a base of 3, not NoOp but a consecutive delta of
// order 1, and two latent variables rather than one. What it does NOT exercise is the ANS coder -
// both tables have a single bin and an ANS size log of zero, the degenerate path - so the pieces
// after this one still need their own evidence.
using System;
using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoChunkMetaTests
{
    /// <summary>
    /// pco's file header from <c>encodings/pco</c>: format major 4, minor 1.
    /// </summary>
    /// <remarks>
    /// INLINE RATHER THAN EXTRACTED AT TEST TIME, and the reason is a classification bug this test
    /// caused. Reaching these bytes through a scan means registering a <c>vortex.pco</c> decoder,
    /// and registering one makes the build CLAIM the encoding: the four pco files moved into the
    /// in-scope count and then failed every value test, because a registered decoder that refuses is
    /// still a decoder as far as the scope split is concerned. The encoding is not supported until
    /// it decodes, so the decoder stays unregistered and the bytes live here.
    ///
    /// Re-derive them by registering <c>PcoDecoder</c>, hooking <c>OnWrapperParsed</c>, and scanning
    /// <c>encodings/pco</c>; feed the two concatenated into
    /// <c>tools/conformance-gen/examples/dump_pco.rs</c> to reproduce the expectations below.
    /// </remarks>
    private static readonly byte[] Header = [0x04, 0x01];

    /// <summary>The first chunk's metadata from the same file, 32 bytes.</summary>
    private static readonly byte[] Meta =
    [
        0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x10, 0x01, 0x01, 0x80, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x40, 0x00, 0x04, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void TheChunkMetadataMatchesTheReferenceParser()
    {
        PcoChunkMeta chunk = PcoChunkMeta.Read(Header, Meta, latentBits: 64);

        Assert.Equal(4, chunk.FormatMajor);
        Assert.Equal(1, chunk.FormatMinor);

        Assert.Equal(PcoModeKind.IntMult, chunk.Mode);
        Assert.Equal(3UL, chunk.ModeBase);

        Assert.Equal(PcoDeltaKind.Consecutive, chunk.Delta);
        Assert.Equal(1, chunk.DeltaOrder);
        Assert.False(chunk.SecondaryUsesDelta);

        // Only a lookback delta has a latent of its own.
        Assert.Null(chunk.DeltaLatent);

        Assert.Equal(0, chunk.Primary.AnsSizeLog);
        PcoBin primary = Assert.Single(chunk.Primary.Bins);
        Assert.Equal(1u, primary.Weight);
        Assert.Equal(9223372036854775809UL, primary.Lower);
        Assert.Equal(0, primary.OffsetBits);

        Assert.NotNull(chunk.Secondary);
        Assert.Equal(0, chunk.Secondary!.Value.AnsSizeLog);
        PcoBin secondary = Assert.Single(chunk.Secondary!.Value.Bins);
        Assert.Equal(1u, secondary.Weight);
        Assert.Equal(1UL, secondary.Lower);
        Assert.Equal(0, secondary.OffsetBits);
    }
}
