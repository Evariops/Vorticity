// Writers for `table Array`, `table ArrayNode` and `table ArrayStats` in spec/flatbuffers/array.fbs,
// and for `table Layout` in spec/flatbuffers/layout.fbs.
using System;
using System.Runtime.InteropServices;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>
/// A settable mirror of <c>ArrayStats</c>, so the six tri-state fields survive a round trip.
/// </summary>
/// <remarks>
/// The <c>= null</c> fields are <see cref="Nullable{T}"/> here for the same reason the reader
/// exposes them only through <c>TryGet</c>: <see langword="null"/> means "unknown" and is a
/// different wire shape from a present <see langword="false"/> or a present 0.
/// </remarks>
public struct ArrayStatsValues
{
    /// <summary>The minimum as a Protobuf-serialized <c>ScalarValue</c>, or null to omit it.</summary>
    public byte[]? Min;

    /// <summary>Exactness of <see cref="Min"/>.</summary>
    public StatPrecision MinPrecision;

    /// <summary>The maximum as a Protobuf-serialized <c>ScalarValue</c>, or null to omit it.</summary>
    public byte[]? Max;

    /// <summary>Exactness of <see cref="Max"/>.</summary>
    public StatPrecision MaxPrecision;

    /// <summary>The sum as a Protobuf-serialized <c>ScalarValue</c>, or null to omit it.</summary>
    public byte[]? Sum;

    /// <summary>Tri-state <c>is_sorted</c>.</summary>
    public bool? IsSorted;

    /// <summary>Tri-state <c>is_strict_sorted</c>.</summary>
    public bool? IsStrictSorted;

    /// <summary>Tri-state <c>is_constant</c>.</summary>
    public bool? IsConstant;

    /// <summary>Tri-state <c>null_count</c>. A present 0 is written as present.</summary>
    public ulong? NullCount;

    /// <summary>Tri-state <c>uncompressed_size_in_bytes</c>.</summary>
    public ulong? UncompressedSizeInBytes;

    /// <summary>Tri-state <c>nan_count</c>.</summary>
    public ulong? NanCount;
}

/// <summary>Builds the <c>Array</c>, <c>ArrayNode</c> and <c>ArrayStats</c> tables.</summary>
public static class ArrayWriter
{
    /// <summary>Writes one <c>ArrayStats</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="stats">The values. A null byte array omits the field; a zero-length one writes an empty vector.</param>
    /// <returns>The offset of the table written.</returns>
    public static int WriteStats(FlatBufferBuilder b, in ArrayStatsValues stats)
    {
        ArgumentNullException.ThrowIfNull(b);

        int min = stats.Min is null ? 0 : b.CreateByteVector(stats.Min);
        int max = stats.Max is null ? 0 : b.CreateByteVector(stats.Max);
        int sum = stats.Sum is null ? 0 : b.CreateByteVector(stats.Sum);

        b.StartTable();
        b.AddOffset(SchemaFieldIds.ArrayStatsMin, min);
        b.AddUInt8(SchemaFieldIds.ArrayStatsMinPrecision, (byte)stats.MinPrecision);
        b.AddOffset(SchemaFieldIds.ArrayStatsMax, max);
        b.AddUInt8(SchemaFieldIds.ArrayStatsMaxPrecision, (byte)stats.MaxPrecision);
        b.AddOffset(SchemaFieldIds.ArrayStatsSum, sum);

        // The Always variants: a tri-state field that happens to hold the schema default must still
        // occupy a vtable slot, or "present and false" would decay into "unknown" on the way out.
        if (stats.IsSorted.HasValue)
        {
            b.AddBoolAlways(SchemaFieldIds.ArrayStatsIsSorted, stats.IsSorted.GetValueOrDefault());
        }

        if (stats.IsStrictSorted.HasValue)
        {
            b.AddBoolAlways(
                SchemaFieldIds.ArrayStatsIsStrictSorted, stats.IsStrictSorted.GetValueOrDefault());
        }

        if (stats.IsConstant.HasValue)
        {
            b.AddBoolAlways(SchemaFieldIds.ArrayStatsIsConstant, stats.IsConstant.GetValueOrDefault());
        }

        if (stats.NullCount.HasValue)
        {
            b.AddUInt64Always(SchemaFieldIds.ArrayStatsNullCount, stats.NullCount.GetValueOrDefault());
        }

        if (stats.UncompressedSizeInBytes.HasValue)
        {
            b.AddUInt64Always(
                SchemaFieldIds.ArrayStatsUncompressedSizeInBytes,
                stats.UncompressedSizeInBytes.GetValueOrDefault());
        }

        if (stats.NanCount.HasValue)
        {
            b.AddUInt64Always(SchemaFieldIds.ArrayStatsNanCount, stats.NanCount.GetValueOrDefault());
        }

        return b.EndTable();
    }

    /// <summary>Writes one <c>ArrayNode</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="encoding">Index into <c>Footer.array_specs</c>.</param>
    /// <param name="metadata">Encoding-specific Protobuf bytes. Empty omits the field.</param>
    /// <param name="childOffsets">Offsets of child nodes from this method. Empty omits the field.</param>
    /// <param name="bufferIndices">Indices into <c>Array.buffers</c>. Empty omits the field.</param>
    /// <param name="statsOffset">Offset from <see cref="WriteStats"/>, or 0 to omit.</param>
    /// <returns>The offset of the table written.</returns>
    public static int WriteNode(
        FlatBufferBuilder b,
        ushort encoding,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<int> childOffsets,
        ReadOnlySpan<ushort> bufferIndices,
        int statsOffset)
    {
        ArgumentNullException.ThrowIfNull(b);

        int metadataVector = metadata.IsEmpty ? 0 : b.CreateByteVector(metadata);
        int children = childOffsets.IsEmpty ? 0 : b.CreateOffsetVector(childOffsets);
        int buffers = bufferIndices.IsEmpty ? 0 : b.CreateScalarVector(bufferIndices);

        b.StartTable();
        b.AddUInt16(SchemaFieldIds.ArrayNodeEncoding, encoding);
        b.AddOffset(SchemaFieldIds.ArrayNodeMetadata, metadataVector);
        b.AddOffset(SchemaFieldIds.ArrayNodeChildren, children);
        b.AddOffset(SchemaFieldIds.ArrayNodeBuffers, buffers);
        b.AddOffset(SchemaFieldIds.ArrayNodeStats, statsOffset);
        return b.EndTable();
    }

    /// <summary>Writes the <c>Array</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="rootNodeOffset">Offset of the root node from <see cref="WriteNode"/>.</param>
    /// <param name="buffers">The blob's buffer descriptors. Empty omits the field.</param>
    /// <returns>The offset of the table written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rootNodeOffset"/> is 0.</exception>
    public static int Write(FlatBufferBuilder b, int rootNodeOffset, ReadOnlySpan<BufferSpec> buffers)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rootNodeOffset, 0);

        int bufferVector = buffers.IsEmpty
            ? 0
            : b.CreateStructVector(MemoryMarshal.Cast<BufferSpec, BufferSpecBlock>(buffers));

        b.StartTable();
        b.AddOffset(SchemaFieldIds.ArrayRoot, rootNodeOffset);
        b.AddOffset(SchemaFieldIds.ArrayBuffers, bufferVector);
        return b.EndTable();
    }

    // Same alignment proxy trick as FooterWriter.SegmentSpecBlock: BufferSpec is Pack = 1, so
    // alignof is 1, but FlatBuffers aligns a Buffer vector to 4 - the width of its uint32 length.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct BufferSpecBlock
    {
        private readonly uint _low;
        private readonly uint _high;
    }
}

/// <summary>Builds the <c>Layout</c> table.</summary>
public static class LayoutWriter
{
    /// <summary>Writes one <c>Layout</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="encoding">Index into <c>Footer.layout_specs</c>.</param>
    /// <param name="rowCount">Rows this layout represents.</param>
    /// <param name="metadata">Layout-specific bytes. Empty omits the field.</param>
    /// <param name="childOffsets">Offsets of child layouts from this method. Empty omits the field.</param>
    /// <param name="segments">Segment ids this layout needs. Empty omits the field.</param>
    /// <returns>The offset of the table written.</returns>
    public static int Write(
        FlatBufferBuilder b,
        ushort encoding,
        ulong rowCount,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<int> childOffsets,
        ReadOnlySpan<uint> segments)
    {
        ArgumentNullException.ThrowIfNull(b);

        int metadataVector = metadata.IsEmpty ? 0 : b.CreateByteVector(metadata);
        int children = childOffsets.IsEmpty ? 0 : b.CreateOffsetVector(childOffsets);
        int segmentVector = segments.IsEmpty ? 0 : b.CreateScalarVector(segments);

        b.StartTable();
        b.AddUInt16(SchemaFieldIds.LayoutEncoding, encoding);
        b.AddUInt64(SchemaFieldIds.LayoutRowCount, rowCount);
        b.AddOffset(SchemaFieldIds.LayoutMetadata, metadataVector);
        b.AddOffset(SchemaFieldIds.LayoutChildren, children);
        b.AddOffset(SchemaFieldIds.LayoutSegments, segmentVector);
        return b.EndTable();
    }
}
