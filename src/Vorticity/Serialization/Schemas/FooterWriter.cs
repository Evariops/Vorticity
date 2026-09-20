// Writer for `table Footer` and `table FileStatistics` in spec/flatbuffers/footer.fbs.
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Builds the <c>Footer</c> table and its four spec dictionaries.</summary>
/// <inheritdoc cref="ArrayWriter" path="/remarks"/>
internal static class FooterWriter
{
    /// <summary>Writes the <c>Footer</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="arraySpecIdOffsets">
    /// One offset per array encoding id, as returned by
    /// <see cref="FlatBufferBuilder.CreateStringUtf8"/>. This method wraps each in its
    /// <c>ArraySpec</c> table.
    /// </param>
    /// <param name="layoutSpecIdOffsets">The same, for layout encoding ids.</param>
    /// <param name="segments">
    /// The segment map, written as an inline struct vector. Always emitted, even when empty: an
    /// absent <c>segment_specs</c> is a read error, not an empty map.
    /// </param>
    /// <param name="compressionSpecs">At most <see cref="VortexLimits.MaxCompressionSpecs"/> schemes.</param>
    /// <param name="encryptionSpecCount">How many empty <c>EncryptionSpec</c> tables to emit.</param>
    /// <returns>The offset of the table written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A spec id offset is 0, there are too many compression specs, or
    /// <paramref name="encryptionSpecCount"/> is negative.
    /// </exception>
    public static int Write(
        FlatBufferBuilder b,
        ReadOnlySpan<int> arraySpecIdOffsets,
        ReadOnlySpan<int> layoutSpecIdOffsets,
        ReadOnlySpan<SegmentSpec> segments,
        ReadOnlySpan<CompressionScheme> compressionSpecs,
        int encryptionSpecCount)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentOutOfRangeException.ThrowIfNegative(encryptionSpecCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            compressionSpecs.Length, VortexLimits.MaxCompressionSpecs);

        int arraySpecs = WriteSpecs(b, arraySpecIdOffsets);
        int layoutSpecs = WriteSpecs(b, layoutSpecIdOffsets);
        int segmentSpecs = b.CreateStructVector(MemoryMarshal.Cast<SegmentSpec, SegmentSpecBlock>(segments));
        int compression = WriteCompressionSpecs(b, compressionSpecs);
        int encryption = WriteEncryptionSpecs(b, encryptionSpecCount);

        b.StartTable();
        b.AddOffset(SchemaFieldIds.FooterArraySpecs, arraySpecs);
        b.AddOffset(SchemaFieldIds.FooterLayoutSpecs, layoutSpecs);
        b.AddOffset(SchemaFieldIds.FooterSegmentSpecs, segmentSpecs);
        b.AddOffset(SchemaFieldIds.FooterCompressionSpecs, compression);
        b.AddOffset(SchemaFieldIds.FooterEncryptionSpecs, encryption);
        return b.EndTable();
    }

    // SegmentSpec is declared Pack = 1 so a reader can reinterpret it wherever it lands, which
    // also makes its natural alignment 1 - and FlatBufferBuilder aligns a struct vector to
    // alignof(T). FlatBuffers requires the alignment of the struct's widest member instead, 8 for
    // SegmentSpec's uint64 offset, which is what flatc-generated writers emit and what all 819
    // golden corpus files contain. Casting through a proxy of the same size whose alignment IS 8
    // buys exactly that padding; the element bytes are unchanged.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SegmentSpecBlock
    {
        private readonly ulong _low;
        private readonly ulong _high;
    }

    private static int WriteSpecs(FlatBufferBuilder b, ReadOnlySpan<int> idOffsets)
    {
        if (idOffsets.IsEmpty)
        {
            return 0;
        }

        int[] rented = ArrayPool<int>.Shared.Rent(idOffsets.Length);
        try
        {
            for (int i = 0; i < idOffsets.Length; i++)
            {
                // `id` is `required` in the schema, so a 0 offset would build a footer our own
                // reader rejects.
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idOffsets[i], 0, nameof(idOffsets));
                b.StartTable();
                b.AddOffset(SchemaFieldIds.SpecId, idOffsets[i]);
                rented[i] = b.EndTable();
            }

            return b.CreateOffsetVector(rented.AsSpan(0, idOffsets.Length));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static int WriteCompressionSpecs(FlatBufferBuilder b, ReadOnlySpan<CompressionScheme> schemes)
    {
        if (schemes.IsEmpty)
        {
            return 0;
        }

        int[] rented = ArrayPool<int>.Shared.Rent(schemes.Length);
        try
        {
            for (int i = 0; i < schemes.Length; i++)
            {
                b.StartTable();
                b.AddUInt8(SchemaFieldIds.CompressionSpecScheme, (byte)schemes[i]);
                rented[i] = b.EndTable();
            }

            return b.CreateOffsetVector(rented.AsSpan(0, schemes.Length));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static int WriteEncryptionSpecs(FlatBufferBuilder b, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int[] rented = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                // `table EncryptionSpec {}` has no fields at all in v1.
                b.StartTable();
                rented[i] = b.EndTable();
            }

            return b.CreateOffsetVector(rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }
}

/// <summary>Builds the <c>FileStatistics</c> table.</summary>
/// <inheritdoc cref="ArrayWriter" path="/remarks"/>
internal static class FileStatisticsWriter
{
    /// <summary>Writes the <c>FileStatistics</c> table.</summary>
    /// <param name="b">The builder. No table may be open.</param>
    /// <param name="fieldStatsOffsets">Offsets from <see cref="ArrayWriter.WriteStats"/>.</param>
    /// <returns>The offset of the table written.</returns>
    public static int Write(FlatBufferBuilder b, ReadOnlySpan<int> fieldStatsOffsets)
    {
        ArgumentNullException.ThrowIfNull(b);
        int fieldStats = fieldStatsOffsets.IsEmpty ? 0 : b.CreateOffsetVector(fieldStatsOffsets);

        b.StartTable();
        b.AddOffset(SchemaFieldIds.FileStatisticsFieldStats, fieldStats);
        return b.EndTable();
    }
}
