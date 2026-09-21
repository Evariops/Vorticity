// The empty-metadata validator, the chunked-layout flag byte and fastlanes.for's bare ScalarValue.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class EncodingMetadataTests
{
    public static TheoryData<string> EmptyMetadataEncodings =>
        new TheoryData<string>
        {
            "vortex.null",
            "vortex.primitive",
            "vortex.varbinview",
            "vortex.struct",
            "vortex.chunked",
            "vortex.masked",
            "vortex.fixed_size_list",
            "vortex.ext",
            "vortex.bytebool",
            "vortex.zigzag",
        };

    [Theory]
    [MemberData(nameof(EmptyMetadataEncodings))]
    public void RequireEmptyAcceptsAnAbsentMetadataField(string encodingId)
    {
        // An absent FlatBuffers field and a zero-length one both arrive as an empty span.
        EncodingMetadata.RequireEmpty(default, encodingId);
        EncodingMetadata.RequireEmpty(Array.Empty<byte>(), encodingId);
    }

    [Theory]
    [MemberData(nameof(EmptyMetadataEncodings))]
    public void RequireEmptyRejectsASingleByte(string encodingId)
    {
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => EncodingMetadata.RequireEmpty(new byte[] { 0 }, encodingId));
        Assert.Contains(encodingId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForReferenceScalarReadsTheBareScalarValueFromTheCorpus()
    {
        // encodings/fastlanes_for_r1: metadata_b64 "GICo1rkH", a bare ScalarValue whose only field
        // is int64_value (tag 3, zigzag sint64). The file has one row and its sidecar says that row
        // is 10^9, which is exactly the frame of reference a single-row FoR array carries -
        // so this asserts the decoded reference against a value the corpus states independently.
        byte[] metadata = Convert.FromBase64String("GICo1rkH");
        ScalarValue value = EncodingMetadata.ReadReferenceScalar(metadata, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.Int64, value.Kind);
        Assert.Equal(1000000000L, value.AsInt64);
    }

    [Fact]
    public void ForReferenceScalarReadsANegativeCorpusValue()
    {
        // types/decimal18_4_nullable_r8193 carries a negative frame of reference.
        byte[] metadata = Convert.FromBase64String("GP3/n/b0rNvgGw==");
        ScalarValue value = EncodingMetadata.ReadReferenceScalar(metadata, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.Int64, value.Kind);
        Assert.True(value.AsInt64 < 0);
    }

    [Fact]
    public void ForReferenceScalarRejectsAnEmptyPayload()
    {
        // Empty decodes to Absent, which upstream turns into a null reference and then rejects
        // ("Reference value cannot be null"). Reading it as a value would be silently wrong.
        Assert.Throws<VortexFormatException>(
            () => EncodingMetadata.ReadReferenceScalar(
                Array.Empty<byte>(), new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void ForReferenceScalarRejectsMalformedPayloads()
    {
        Assert.Throws<VortexFormatException>(
            () => EncodingMetadata.ReadReferenceScalar(new byte[] { 0x18 }, new ScalarStore(), new DTypeArena()));
        Assert.Throws<VortexFormatException>(
            () => EncodingMetadata.ReadReferenceScalar(
                WireBuilder.GroupTag(), new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void ForReferenceScalarRejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(
            () => EncodingMetadata.ReadReferenceScalar(new byte[] { 0x10, 0x01 }, null!, new DTypeArena()));
        Assert.Throws<ArgumentNullException>(
            () => EncodingMetadata.ReadReferenceScalar(new byte[] { 0x10, 0x01 }, new ScalarStore(), null!));
    }

    [Fact]
    public void ForReferenceScalarSurvivesEveryTruncationOfACorpusPayload()
    {
        byte[] metadata = Convert.FromBase64String("GICo1rkH");
        for (int length = 0; length < metadata.Length; length++)
        {
            byte[] truncated = metadata.AsSpan(0, length).ToArray();
            try
            {
                EncodingMetadata.ReadReferenceScalar(truncated, new ScalarStore(), new DTypeArena());
            }
            catch (VortexFormatException)
            {
            }
        }
    }

    [Fact]
    public void ChunkedLayoutTreatsTheFirstByteAsTheStatsTableFlag()
    {
        // Note that Vortex 0.86.1 itself declares this layout's metadata as
        // EmptyMetadata and rejects a non-empty payload outright, so every flag-set case below is
        // untestable against a real file - see the remarks on ChunkedLayoutMetadata.
        Assert.False(ChunkedLayoutMetadata.Read(Array.Empty<byte>()).HasStatsTable);
        Assert.False(ChunkedLayoutMetadata.Read(new byte[] { 0 }).HasStatsTable);
        Assert.True(ChunkedLayoutMetadata.Read(new byte[] { 1 }).HasStatsTable);
        Assert.True(ChunkedLayoutMetadata.Read(new byte[] { 1, 2, 3 }).HasStatsTable);
        Assert.False(ChunkedLayoutMetadata.Read(new byte[] { 0, 2, 3 }).HasStatsTable);
        Assert.True(ChunkedLayoutMetadata.Read(new byte[] { 255 }).HasStatsTable);
    }

    [Fact]
    public void ChunkedLayoutSerializesTheFlagBack()
    {
        Span<byte> destination = stackalloc byte[4];
        ChunkedLayoutMetadata off = new ChunkedLayoutMetadata(false);
        Assert.Equal(0, ChunkedLayoutMetadata.Write(in off, destination));

        ChunkedLayoutMetadata on = new ChunkedLayoutMetadata(true);
        Assert.Equal(1, ChunkedLayoutMetadata.Write(in on, destination));
        Assert.Equal(1, destination[0]);
        Assert.True(ChunkedLayoutMetadata.Read(destination[..1]).HasStatsTable);
    }

    [Fact]
    public void ChunkedLayoutWriteRejectsAnEmptyDestination()
    {
        Assert.Throws<ArgumentException>(
            () =>
            {
                ChunkedLayoutMetadata value = new ChunkedLayoutMetadata(true);
                _ = ChunkedLayoutMetadata.Write(in value, Span<byte>.Empty);
            });
    }
}
