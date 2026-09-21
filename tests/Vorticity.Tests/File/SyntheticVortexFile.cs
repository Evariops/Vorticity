// Builds well-formed - and deliberately malformed - Vortex file containers from the Phase 0
// writers. The corpus proves the open path reads real files; this proves it rejects the shapes no
// real writer produces, and it is the only way to reach the open's second read, taken when the
// footer segments fall outside the first tail read, which no golden file exercises.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Vorticity.File;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Tests.File;

/// <summary>A recipe for a synthetic Vortex file container.</summary>
internal sealed class SyntheticFileSpec
{
    /// <summary>The schema. Written as a dtype segment unless <see cref="ExcludeDType"/>.</summary>
    internal required DType Schema { get; init; }

    /// <summary>The root layout's <c>row_count</c>, straight onto the wire.</summary>
    internal ulong RowCount { get; init; } = 8192;

    /// <summary>When true no dtype segment is written and the postscript omits the field.</summary>
    internal bool ExcludeDType { get; init; }

    /// <summary>When true a <c>FileStatistics</c> segment is written.</summary>
    internal bool IncludeStatistics { get; init; }

    /// <summary>Statistics to write, one per root field. Ignored unless <see cref="IncludeStatistics"/>.</summary>
    internal ArrayStatsValues[]? Statistics { get; init; }

    /// <summary>Bytes of filler written between the dtype segment and the layout segment.</summary>
    internal int FillerBeforeLayout { get; init; }

    /// <summary>Bytes of filler written between the layout segment and the metadata values.</summary>
    internal int FillerAfterLayout { get; init; }

    /// <summary>Bytes of filler written between the metadata values and the statistics segment.</summary>
    internal int FillerAfterMetadata { get; init; }

    /// <summary>
    /// Writes the metadata values before the dtype segment instead of after the layout. With
    /// <see cref="FillerAfterMetadata"/> this is what places a metadata segment outside every
    /// window the open path reads, so fetching its value costs a targeted read.
    /// </summary>
    internal bool MetadataFirst { get; init; }

    /// <summary>User metadata entries, in the order the postscript lists them.</summary>
    internal (string Key, byte[] Value)[] Metadata { get; init; } = [];

    /// <summary>Keys written verbatim as UTF-8, bypassing the string overload. Overrides <see cref="Metadata"/> keys.</summary>
    internal byte[][]? MetadataKeysUtf8 { get; init; }

    /// <summary>Array encoding ids for the footer dictionary.</summary>
    internal string[] ArraySpecIds { get; init; } = ["vortex.primitive", "vortex.struct"];

    /// <summary>
    /// When set, <c>array_specs</c> holds <see cref="SharedArraySpecCount"/> entries that all point
    /// at ONE FlatBuffers string. Legal, and the shape that makes eager id interning quadratic.
    /// </summary>
    internal string? SharedArraySpecId { get; init; }

    /// <summary>How many entries share <see cref="SharedArraySpecId"/>.</summary>
    internal int SharedArraySpecCount { get; init; }

    /// <summary>Layout encoding ids for the footer dictionary.</summary>
    internal string[] LayoutSpecIds { get; init; } = ["vortex.flat"];

    /// <summary>Replaces the computed segment map with a forged one.</summary>
    internal SegmentSpec[]? OverrideSegmentSpecs { get; init; }

    /// <summary>Replaces the postscript's <c>layout</c> locator with a forged one.</summary>
    internal SegmentSpec? OverrideLayoutSegment { get; init; }

    /// <summary>Replaces the postscript's <c>footer</c> locator with a forged one.</summary>
    internal SegmentSpec? OverrideFooterSegment { get; init; }

    /// <summary>Replaces the postscript's <c>dtype</c> locator with a forged one.</summary>
    internal SegmentSpec? OverrideDTypeSegment { get; init; }

    /// <summary>The version written into the EOF marker.</summary>
    internal ushort Version { get; init; } = VortexFileFormat.Version;
}

/// <summary>Builds the bytes of a synthetic Vortex file container.</summary>
internal static class SyntheticVortexFile
{
    /// <summary>Builds a file from <paramref name="spec"/>.</summary>
    /// <param name="spec">The recipe.</param>
    /// <returns>The complete file bytes.</returns>
    internal static byte[] Build(SyntheticFileSpec spec)
    {
        List<byte> file = new List<byte>(4096);
        file.AddRange(VortexFileFormat.MagicBytes.ToArray());

        List<SegmentSpec> dataSegments = new List<SegmentSpec>();

        // One 64-byte-aligned data segment, so the aligned path of the segment map is exercised.
        Pad(file, 64);
        int dataOffset = file.Count;
        for (int i = 0; i < 96; i++)
        {
            file.Add((byte)i);
        }

        dataSegments.Add(new SegmentSpec((ulong)dataOffset, 96, 6, 0, 0));

        (string Key, byte[] Value)[] metadata = spec.Metadata;
        SegmentSpec[] metadataSpecs = new SegmentSpec[metadata.Length];
        if (spec.MetadataFirst)
        {
            for (int i = 0; i < metadata.Length; i++)
            {
                metadataSpecs[i] = Append(file, metadata[i].Value, 0);
            }

            AppendFiller(file, dataSegments, spec.FillerAfterMetadata);
        }

        SegmentSpec dtypeSpec = default;
        if (!spec.ExcludeDType)
        {
            byte[] dtypeBytes = DTypeFlatBuffers.Serialize(spec.Schema);
            dtypeSpec = Append(file, dtypeBytes, 3);
        }

        AppendFiller(file, dataSegments, spec.FillerBeforeLayout);

        byte[] layoutBytes = BuildLayout(spec);
        SegmentSpec layoutSpec = Append(file, layoutBytes, 3);

        AppendFiller(file, dataSegments, spec.FillerAfterLayout);

        // Metadata values live after the data and before the footer, as a real writer places them.
        if (!spec.MetadataFirst)
        {
            for (int i = 0; i < metadata.Length; i++)
            {
                metadataSpecs[i] = Append(file, metadata[i].Value, 0);
            }

            AppendFiller(file, dataSegments, spec.FillerAfterMetadata);
        }

        SegmentSpec statisticsSpec = default;
        if (spec.IncludeStatistics)
        {
            statisticsSpec = Append(file, BuildStatistics(spec), 3);
        }

        SegmentSpec[] segmentSpecs = spec.OverrideSegmentSpecs ?? dataSegments.ToArray();
        byte[] footerBytes = BuildFooter(spec, segmentSpecs);
        SegmentSpec footerSpec = Append(file, footerBytes, 3);

        byte[] postscriptBytes = BuildPostscript(
            spec,
            spec.OverrideDTypeSegment ?? dtypeSpec,
            spec.OverrideLayoutSegment ?? layoutSpec,
            statisticsSpec,
            spec.OverrideFooterSegment ?? footerSpec,
            metadataSpecs);
        file.AddRange(postscriptBytes);

        byte[] eof = new byte[VortexFileFormat.EofSize];
        BinaryPrimitives.WriteUInt16LittleEndian(eof.AsSpan(0, 2), spec.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(eof.AsSpan(2, 2), (ushort)postscriptBytes.Length);
        VortexFileFormat.MagicBytes.CopyTo(eof.AsSpan(4, 4));
        file.AddRange(eof);

        return file.ToArray();
    }

    private static byte[] BuildLayout(SyntheticFileSpec spec)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int layout = LayoutWriter.Write(builder, 0, spec.RowCount, default, default, [0u]);
        return builder.FinishToArray(layout);
    }

    private static byte[] BuildStatistics(SyntheticFileSpec spec)
    {
        int fieldCount = spec.Schema.Kind == DTypeKind.Struct ? spec.Schema.FieldCount : 1;
        ArrayStatsValues[] values = spec.Statistics ?? new ArrayStatsValues[fieldCount];

        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int[] offsets = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            offsets[i] = ArrayWriter.WriteStats(builder, in values[i]);
        }

        int statistics = FileStatisticsWriter.Write(builder, offsets);
        return builder.FinishToArray(statistics);
    }

    private static byte[] BuildFooter(SyntheticFileSpec spec, SegmentSpec[] segmentSpecs)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int[] arrayIds;
        if (spec.SharedArraySpecId is not null)
        {
            int shared = builder.CreateString(spec.SharedArraySpecId);
            arrayIds = new int[spec.SharedArraySpecCount];
            arrayIds.AsSpan().Fill(shared);
        }
        else
        {
            arrayIds = new int[spec.ArraySpecIds.Length];
            for (int i = 0; i < arrayIds.Length; i++)
            {
                arrayIds[i] = builder.CreateString(spec.ArraySpecIds[i]);
            }
        }

        int[] layoutIds = new int[spec.LayoutSpecIds.Length];
        for (int i = 0; i < layoutIds.Length; i++)
        {
            layoutIds[i] = builder.CreateString(spec.LayoutSpecIds[i]);
        }

        int footer = FooterWriter.Write(builder, arrayIds, layoutIds, segmentSpecs, default, 0);
        return builder.FinishToArray(footer);
    }

    private static byte[] BuildPostscript(
        SyntheticFileSpec spec,
        SegmentSpec dtypeSpec,
        SegmentSpec layoutSpec,
        SegmentSpec statisticsSpec,
        SegmentSpec footerSpec,
        SegmentSpec[] metadataSpecs)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();

        int[] metadataOffsets = new int[metadataSpecs.Length];
        for (int i = 0; i < metadataSpecs.Length; i++)
        {
            byte[] key = spec.MetadataKeysUtf8 is not null
                ? spec.MetadataKeysUtf8[i]
                : Encoding.UTF8.GetBytes(spec.Metadata[i].Key);
            int segment = PostscriptWriter.WriteSegment(builder, in metadataSpecs[i], CompressionScheme.None);

            // PostscriptWriter.WriteMetadata refuses an empty or over-long key, which is correct
            // for a writer and useless for a reader test. The table is written by hand so the
            // forged shapes reach the reader at all.
            int keyOffset = builder.CreateStringUtf8(key);
            builder.StartTable();
            builder.AddOffset(SchemaFieldIds.PostscriptMetadataKey, keyOffset);
            builder.AddOffset(SchemaFieldIds.PostscriptMetadataSegment, segment);
            metadataOffsets[i] = builder.EndTable();
        }

        int dtype = spec.ExcludeDType
            ? 0
            : PostscriptWriter.WriteSegment(builder, in dtypeSpec, CompressionScheme.None);
        int layout = PostscriptWriter.WriteSegment(builder, in layoutSpec, CompressionScheme.None);
        int statistics = spec.IncludeStatistics
            ? PostscriptWriter.WriteSegment(builder, in statisticsSpec, CompressionScheme.None)
            : 0;
        int footer = PostscriptWriter.WriteSegment(builder, in footerSpec, CompressionScheme.None);

        int postscript = PostscriptWriter.Write(builder, dtype, layout, statistics, footer, metadataOffsets);
        return builder.FinishToArray(postscript);
    }

    private static void AppendFiller(List<byte> file, List<SegmentSpec> dataSegments, int length)
    {
        if (length <= 0)
        {
            return;
        }

        int offset = file.Count;
        for (int i = 0; i < length; i++)
        {
            file.Add(0x5a);
        }

        dataSegments.Add(new SegmentSpec((ulong)offset, (uint)length, 0, 0, 0));
    }

    private static SegmentSpec Append(List<byte> file, byte[] bytes, byte alignmentExponent)
    {
        int offset = file.Count;
        file.AddRange(bytes);
        return new SegmentSpec((ulong)offset, (uint)bytes.Length, alignmentExponent, 0, 0);
    }

    private static void Pad(List<byte> file, int alignment)
    {
        while (file.Count % alignment != 0)
        {
            file.Add(0);
        }
    }

    /// <summary>A tiny two-field struct schema, enough for every container test here.</summary>
    /// <param name="arena">The arena to build in.</param>
    /// <returns>The schema.</returns>
    internal static DType SmallSchema(DTypeArena arena)
    {
        DType[] fields = [arena.Primitive(PType.I64, Nullability.NonNullable), arena.Utf8(Nullability.Nullable)];
        string[] names = ["ints", "strs"];
        return arena.Struct(names, fields, Nullability.NonNullable);
    }
}
