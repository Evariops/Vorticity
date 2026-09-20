// Writers for the postscript tables of spec/flatbuffers/footer.fbs.
//
// FlatBuffers is built back to front, so every sub-object - a string, a vector, a nested table -
// must be finished BEFORE the table that references it is opened. Each method below therefore
// creates its children first and only then calls StartTable.
//
// These are writers, not parsers: a bad argument here is a CALLER error and gets Argument*, per
// the Phase 1 contract §1.4. File-supplied values never reach this code.
using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Builds the <c>Postscript</c>, <c>PostscriptMetadata</c> and <c>PostscriptSegment</c> tables.</summary>
/// <inheritdoc cref="ArrayWriter" path="/remarks"/>
internal static class PostscriptWriter
{
    /// <summary>Writes one <c>PostscriptSegment</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="spec">
    /// The locator. Its <see cref="SegmentSpec.Compression"/> and <see cref="SegmentSpec.Encryption"/>
    /// are footer indices and have no counterpart here, so they are ignored; the postscript spells
    /// compression out inline through <paramref name="scheme"/>.
    /// </param>
    /// <param name="scheme">
    /// The inline compression scheme. <see cref="CompressionScheme.None"/> omits the sub-table.
    /// </param>
    /// <returns>The offset of the table written.</returns>
    public static int WriteSegment(FlatBufferBuilder b, in SegmentSpec spec, CompressionScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(b);

        int compression = 0;
        if (scheme != CompressionScheme.None)
        {
            b.StartTable();
            b.AddUInt8(SchemaFieldIds.CompressionSpecScheme, (byte)scheme);
            compression = b.EndTable();
        }

        b.StartTable();
        b.AddUInt64(SchemaFieldIds.PostscriptSegmentOffset, spec.Offset);
        b.AddUInt32(SchemaFieldIds.PostscriptSegmentLength, spec.Length);
        b.AddUInt8(SchemaFieldIds.PostscriptSegmentAlignmentExponent, spec.AlignmentExponent);
        b.AddOffset(SchemaFieldIds.PostscriptSegmentCompression, compression);
        return b.EndTable();
    }

    /// <summary>Writes one <c>PostscriptMetadata</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="keyUtf8">
    /// The key. Must be non-empty and at most <see cref="VortexLimits.MaxMetadataKeyLength"/>
    /// UTF-8 bytes — spec/flatbuffers/footer.fbs states readers reject anything else.
    /// </param>
    /// <param name="segmentOffset">Offset of the <c>PostscriptSegment</c> from <see cref="WriteSegment"/>.</param>
    /// <returns>The offset of the table written.</returns>
    /// <exception cref="ArgumentException">The key is empty or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentOffset"/> is 0.</exception>
    public static int WriteMetadata(FlatBufferBuilder b, ReadOnlySpan<byte> keyUtf8, int segmentOffset)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (keyUtf8.IsEmpty)
        {
            throw new ArgumentException("A metadata key must not be empty.", nameof(keyUtf8));
        }

        if (keyUtf8.Length > VortexLimits.MaxMetadataKeyLength)
        {
            throw new ArgumentException(
                $"A metadata key may be at most {VortexLimits.MaxMetadataKeyLength} UTF-8 bytes, " +
                $"but this one is {keyUtf8.Length}.",
                nameof(keyUtf8));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentOffset, 0);

        int key = b.CreateStringUtf8(keyUtf8);
        b.StartTable();
        b.AddOffset(SchemaFieldIds.PostscriptMetadataKey, key);
        b.AddOffset(SchemaFieldIds.PostscriptMetadataSegment, segmentOffset);
        return b.EndTable();
    }

    /// <summary>Writes the <c>Postscript</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="dtypeSegment">Offset of the optional <c>dtype</c> segment, or 0.</param>
    /// <param name="layoutSegment">Offset of the required <c>layout</c> segment.</param>
    /// <param name="statisticsSegment">Offset of the optional <c>statistics</c> segment, or 0.</param>
    /// <param name="footerSegment">Offset of the required <c>footer</c> segment.</param>
    /// <param name="metadataOffsets">Offsets from <see cref="WriteMetadata"/>, at most 16.</param>
    /// <returns>The offset of the table written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A required segment offset is 0, or there are more than
    /// <see cref="VortexLimits.MaxMetadataSegments"/> metadata entries.
    /// </exception>
    public static int Write(
        FlatBufferBuilder b,
        int dtypeSegment,
        int layoutSegment,
        int statisticsSegment,
        int footerSegment,
        ReadOnlySpan<int> metadataOffsets)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(layoutSegment, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(footerSegment, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            metadataOffsets.Length, VortexLimits.MaxMetadataSegments);

        int metadata = metadataOffsets.IsEmpty ? 0 : b.CreateOffsetVector(metadataOffsets);

        b.StartTable();
        b.AddOffset(SchemaFieldIds.PostscriptDType, dtypeSegment);
        b.AddOffset(SchemaFieldIds.PostscriptLayout, layoutSegment);
        b.AddOffset(SchemaFieldIds.PostscriptStatistics, statisticsSegment);
        b.AddOffset(SchemaFieldIds.PostscriptFooter, footerSegment);
        b.AddOffset(SchemaFieldIds.PostscriptMetadata, metadata);
        return b.EndTable();
    }
}
