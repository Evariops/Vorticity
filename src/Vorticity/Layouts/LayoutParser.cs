using System;
using System.Buffers.Binary;
using System.Globalization;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// The depth-first walk that flattens the serialized layout table into a <see cref="LayoutTree"/>,
/// pushing dtypes down to the children and validating each encoding's arity. It is stricter than
/// upstream in two places: segment ids are bounds-checked here, at parse time, rather than lazily
/// when a segment is requested, and a <c>vortex.stats</c> data child must match the parent's row
/// count.
/// </summary>
internal static class LayoutParser
{
    internal static int ParseNode(LayoutTree.Builder b, in LayoutView view, DType dtype, int depth)
    {
        // Semantic depth, counted separately from the FlatBuffers table depth.
        VortexLimits.CheckDepth(depth, VortexLimits.MaxLayoutDepth, "layout");

        int index = b.NewRecord();

        LayoutNodeRecord record = default;
        record.DType = dtype;
        record.ZoneMapIndex = -1;
        record.ChunkOffsetStart = -1;
        record.EncodingSpecIndex = view.Encoding;
        record.Encoding = b.ResolveEncoding(record.EncodingSpecIndex);

        ulong wireRows = view.RowCount;
        if (wireRows > long.MaxValue)
        {
            LayoutsThrow.RowCountRange(wireRows);
        }

        record.RowCount = (long)wireRows;

        // Both vectors stay where they are: the tree retains the buffer and a node records only
        // (offset, length) into it. Copying per node would be quadratic in a buffer whose vectors
        // are shared between tables, which FlatBuffers allows and nothing forbids.
        ReadOnlySpan<byte> metadata = view.Metadata;
        b.Charge(metadata.Length);
        record.MetadataStart = b.OffsetOf(metadata);
        record.MetadataLength = metadata.Length;

        ReadOnlySpan<uint> segments = view.Segments;
        b.Charge((long)segments.Length * sizeof(uint));
        ValidateSegments(b, segments);
        record.SegmentStart = b.OffsetOf(segments);
        record.SegmentCount = segments.Length;

        int wireChildren = view.ChildCount;

        switch (record.Encoding)
        {
            case LayoutEncodingId.Flat:
                ParseFlat(metadata, segments.Length, wireChildren);
                break;

            case LayoutEncodingId.Chunked:
                ParseChunked(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            case LayoutEncodingId.Struct:
                ParseStruct(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            case LayoutEncodingId.Dict:
                ParseDict(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            case LayoutEncodingId.Zoned:
                ParseZoned(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            case LayoutEncodingId.Stats:
                ParseStats(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            case LayoutEncodingId.List:
                ParseList(b, in view, dtype, depth, metadata, segments.Length, wireChildren, ref record);
                break;

            default:
                // An unknown layout's children have no derivable dtypes, so they are not
                // materialized. That is not an error here: it becomes one only when a projection
                // puts this node on the path to data, in LayoutReaderTable.Get.
                record.ChildCount = 0;
                break;
        }

        b.Records[index] = record;
        return index;
    }

    private static void ValidateSegments(LayoutTree.Builder b, ReadOnlySpan<uint> segments)
    {
        int count = b.SegmentSpecCount;
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] >= (uint)count)
            {
                LayoutsThrow.MissingSegment(segments[i], count);
            }
        }
    }

    // ------------------------------------------------------------------------------ vortex.flat

    private static void ParseFlat(ReadOnlySpan<byte> metadata, int segmentCount, int childCount)
    {
        // Exactly one segment, no children.
        if (segmentCount != 1)
        {
            LayoutsThrow.Arity("vortex.flat", "segment", segmentCount, 1);
        }

        if (childCount != 0)
        {
            LayoutsThrow.Arity("vortex.flat", "children", childCount, 0);
        }

        // Parsed for validation only. Normally absent; when present it inlines the whole array
        // flatbuffer.
        FlatLayoutMetadata.Read(metadata);
    }

    // --------------------------------------------------------------------------- vortex.chunked

    private static void ParseChunked(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.chunked", "segment", segmentCount, 0);
        }

        // No fixture exercises this branch, and none can be produced from an upstream that rejects
        // non-empty chunked metadata outright. The schema describes a first-byte flag meaning
        // "child 0 is the statistics table for the other chunks"; it is read here, that child is
        // excluded from the chunk list, and the offsets check below catches a wrong guess.
        ChunkedLayoutMetadata chunked = ChunkedLayoutMetadata.Read(metadata);

        int firstChunk = chunked.HasStatsTable ? 1 : 0;
        if (wireChildren < firstChunk)
        {
            LayoutsThrow.Format(
                "A vortex.chunked layout's metadata marks child 0 as a statistics table, but the " +
                "layout has no children.");
        }

        int chunkCount = wireChildren - firstChunk;
        record.ChildStart = b.ReserveChildren(chunkCount);
        record.ChildCount = chunkCount;
        record.ChunkOffsetStart = b.ReserveChunkOffsets(chunkCount + 1);
        b.ChunkOffsets[record.ChunkOffsetStart] = 0;

        long offset = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            LayoutView child = view.GetChild(firstChunk + i);
            int childIndex = ParseNode(b, in child, dtype, depth + 1);
            b.ChildIndices[record.ChildStart + i] = childIndex;

            long childRows = b.Records[childIndex].RowCount;
            if (childRows > long.MaxValue - offset)
            {
                LayoutsThrow.Format("A vortex.chunked layout's child row counts overflow.");
            }

            offset += childRows;
            b.ChunkOffsets[record.ChunkOffsetStart + i + 1] = offset;
        }

        // "Chunked child row counts do not add up to parent row count" - and for zero children
        // that means the parent must cover zero rows.
        if (offset != record.RowCount)
        {
            LayoutsThrow.Format(
                $"A vortex.chunked layout's {chunkCount.ToString(CultureInfo.InvariantCulture)} " +
                $"chunks cover {offset.ToString(CultureInfo.InvariantCulture)} rows; the layout " +
                $"declares {record.RowCount.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    // ---------------------------------------------------------------------------- vortex.struct

    private static void ParseStruct(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.struct", "segment", segmentCount, 0);
        }

        if (!metadata.IsEmpty)
        {
            // EmptyMetadata::deserialize: "EmptyMetadata should not have metadata bytes".
            LayoutsThrow.Format(
                $"A vortex.struct layout carries {metadata.Length.ToString(CultureInfo.InvariantCulture)} " +
                "metadata bytes; its metadata must be empty.");
        }

        if (dtype.Kind != DTypeKind.Struct)
        {
            LayoutsThrow.Format($"A vortex.struct layout cannot produce the non-struct dtype {dtype}.");
        }

        int fieldCount = dtype.FieldCount;
        bool nullable = dtype.IsNullable;
        int validityChildren = nullable ? 1 : 0;
        int expected = fieldCount + validityChildren;
        if (wireChildren != expected)
        {
            LayoutsThrow.Arity("vortex.struct", "children", wireChildren, expected);
        }

        record.ChildStart = b.ReserveChildren(wireChildren);
        record.ChildCount = wireChildren;

        for (int i = 0; i < wireChildren; i++)
        {
            // Serialized order: validity first when the struct dtype is nullable, then the fields
            // in dtype order. The validity child is a non-nullable Bool.
            DType childType = nullable && i == 0
                ? b.Types.Bool(Nullability.NonNullable)
                : dtype.GetField(i - validityChildren);

            LayoutView child = view.GetChild(i);
            int childIndex = ParseNode(b, in child, childType, depth + 1);
            b.ChildIndices[record.ChildStart + i] = childIndex;

            long childRows = b.Records[childIndex].RowCount;
            if (childRows != record.RowCount)
            {
                LayoutsThrow.RowCountMismatch(
                    "vortex.struct",
                    nullable && i == 0
                        ? "validity"
                        : "field " + (i - validityChildren).ToString(CultureInfo.InvariantCulture),
                    childRows,
                    record.RowCount);
            }
        }
    }

    // ------------------------------------------------------------------------------ vortex.dict

    private static void ParseDict(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.dict", "segment", segmentCount, 0);
        }

        if (wireChildren != 2)
        {
            LayoutsThrow.Arity("vortex.dict", "children", wireChildren, 2);
        }

        DictLayoutMetadata dict = DictLayoutMetadata.Read(metadata);
        if (!dict.CodesPType.IsInteger())
        {
            LayoutsThrow.Format(
                $"A vortex.dict layout's codes must be an integer physical type, not {dict.CodesPType.Name()}.");
        }

        // An absent `is_nullable_codes` falls back to the layout dtype's nullability, which is not
        // the same as reading it as false.
        Nullability codesNullability = dict.IsNullableCodes switch
        {
            true => Nullability.Nullable,
            false => Nullability.NonNullable,
            _ => dtype.Nullability,
        };

        DType codesType = b.Types.Primitive(dict.CodesPType, codesNullability);

        record.ChildStart = b.ReserveChildren(2);
        record.ChildCount = 2;

        // Child 0 is the values and child 1 the codes, the opposite order from the vortex.dict
        // array, whose child 0 is the codes.
        LayoutView values = view.GetChild(0);
        b.ChildIndices[record.ChildStart] = ParseNode(b, in values, dtype, depth + 1);

        LayoutView codes = view.GetChild(1);
        int codesIndex = ParseNode(b, in codes, codesType, depth + 1);
        b.ChildIndices[record.ChildStart + 1] = codesIndex;

        // The codes child covers the parent's rows; the values child's row count is the dictionary
        // cardinality and is deliberately unconstrained.
        long codesRows = b.Records[codesIndex].RowCount;
        if (codesRows != record.RowCount)
        {
            LayoutsThrow.RowCountMismatch("vortex.dict", "codes", codesRows, record.RowCount);
        }
    }

    // ------------------------------------------------------------------------------ vortex.list

    /// <summary>
    /// <c>vortex.list</c>: elements, offsets, and a validity child when the dtype is nullable.
    /// </summary>
    /// <remarks>
    /// The children's dtypes are derived rather than read, which is why an unknown layout cannot
    /// have children at all: the elements child takes the list's element dtype, the offsets child is
    /// a non-nullable primitive of the width the metadata names, and the validity child is a
    /// non-nullable Bool.
    /// </remarks>
    private static void ParseList(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.list", "segment", segmentCount, 0);
        }

        if (dtype.Kind != DTypeKind.List)
        {
            LayoutsThrow.Format($"A vortex.list layout requires a List dtype, not {dtype.Kind}.");
        }

        int expected = dtype.Nullability == Nullability.Nullable ? 3 : 2;
        if (wireChildren != expected)
        {
            LayoutsThrow.Arity("vortex.list", "children", wireChildren, expected);
        }

        ListLayoutMetadata list = ListLayoutMetadata.Read(metadata);
        if (!list.OffsetsPType.IsInteger())
        {
            LayoutsThrow.Format(
                $"A vortex.list layout's offsets must be an integer physical type, not " +
                $"{list.OffsetsPType.Name()}.");
        }

        DType offsetsType = b.Types.Primitive(list.OffsetsPType, Nullability.NonNullable);

        record.ChildStart = b.ReserveChildren(expected);
        record.ChildCount = expected;

        LayoutView elements = view.GetChild(0);
        b.ChildIndices[record.ChildStart] = ParseNode(b, in elements, dtype.ElementType, depth + 1);

        LayoutView offsets = view.GetChild(1);
        int offsetsIndex = ParseNode(b, in offsets, offsetsType, depth + 1);
        b.ChildIndices[record.ChildStart + 1] = offsetsIndex;

        // One boundary per row plus one to close the last list, which is the invariant upstream
        // checks as `offsets.row_count() - 1 == row_count`.
        long offsetRows = b.Records[offsetsIndex].RowCount;
        if (offsetRows != record.RowCount + 1)
        {
            LayoutsThrow.RowCountMismatch("vortex.list", "offsets", offsetRows, record.RowCount + 1);
        }

        if (expected == 3)
        {
            DType validityType = b.Types.Bool(Nullability.NonNullable);
            LayoutView validity = view.GetChild(2);
            int validityIndex = ParseNode(b, in validity, validityType, depth + 1);
            b.ChildIndices[record.ChildStart + 2] = validityIndex;

            long validityRows = b.Records[validityIndex].RowCount;
            if (validityRows != record.RowCount)
            {
                LayoutsThrow.RowCountMismatch("vortex.list", "validity", validityRows, record.RowCount);
            }
        }
    }

    // ----------------------------------------------------------------------------- vortex.zoned

    private static void ParseZoned(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.zoned", "segment", segmentCount, 0);
        }

        if (wireChildren != 2)
        {
            LayoutsThrow.Arity("vortex.zoned", "children", wireChildren, 2);
        }

        // [u8 version = 1] ++ ZonedMetadataProto. A missing version byte, a version other than 1
        // and an empty protobuf tail are three distinct rejections upstream and all three here.
        ZonedMetadata zoned = ZonedMetadata.Read(metadata, b.Specs);

        // Filled before the children are parsed: a nested zone map reserves after this one, and
        // growing the table would leave the span behind.
        int aggregateCount = b.Specs.Count;
        int aggregateStart = b.ReserveZoneAggregates(aggregateCount);
        bool derived = ZoneMapSchema.TryBuildAggregateTable(
            b.Types, dtype, b.Specs, out DType zonesType, b.ZoneAggregates.AsSpan(aggregateStart, aggregateCount));

        int childCount = derived ? 2 : 1;
        record.ChildStart = b.ReserveChildren(childCount);
        record.ChildCount = childCount;

        LayoutView data = view.GetChild(0);
        int dataIndex = ParseNode(b, in data, dtype, depth + 1);
        b.ChildIndices[record.ChildStart] = dataIndex;

        long dataRows = b.Records[dataIndex].RowCount;
        if (dataRows != record.RowCount)
        {
            LayoutsThrow.RowCountMismatch("vortex.zoned", "data", dataRows, record.RowCount);
        }

        int zoneCount = 0;
        if (derived)
        {
            LayoutView zones = view.GetChild(1);
            int zonesIndex = ParseNode(b, in zones, zonesType, depth + 1);
            b.ChildIndices[record.ChildStart + 1] = zonesIndex;

            // The zone count is the zones child's own row count and not a function of zone_len: a
            // consistent file has ceil(row_count / zone_len) zones, but nothing enforces it.
            zoneCount = CheckedZoneCount(b.Records[zonesIndex].RowCount);
        }

        // zone_len == 0 means "no zone map": the reader behaves as the data child
        // (ZonedLayout::zoned_reader). An unresolvable aggregate disables pruning the same way.
        bool pruning = derived && zoned.ZoneLength > 0;
        record.ZoneMapIndex = b.AddZoneMap(
            new ZoneMap(pruning, zoneCount, zoned.ZoneLength, null, aggregateStart, aggregateCount));
    }

    // ----------------------------------------------------------------------------- vortex.stats

    private static void ParseStats(
        LayoutTree.Builder b,
        in LayoutView view,
        DType dtype,
        int depth,
        ReadOnlySpan<byte> metadata,
        int segmentCount,
        int wireChildren,
        ref LayoutNodeRecord record)
    {
        if (segmentCount != 0)
        {
            LayoutsThrow.Arity("vortex.stats", "segment", segmentCount, 0);
        }

        if (wireChildren != 2)
        {
            LayoutsThrow.Arity("vortex.stats", "children", wireChildren, 2);
        }

        // No fixture exercises this branch. The legacy stats metadata is not protobuf and carries
        // no version byte: a little-endian u32 zone length, then a raw stat bitset whose bit index
        // is the statistic's discriminant. A bare four-byte metadata is valid.
        if (metadata.Length < 4)
        {
            LayoutsThrow.Format(
                "A vortex.stats layout's metadata must contain at least 4 bytes for its zone " +
                $"length; it holds {metadata.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        uint zoneLength = BinaryPrimitives.ReadUInt32LittleEndian(metadata);

        Span<LegacyStat> stats = stackalloc LegacyStat[ZoneMapSchema.MaxLegacyStat + 1];
        int statCount = ZoneMapSchema.ReadLegacyStatBitset(metadata.Slice(4), stats);
        DType zonesType = ZoneMapSchema.LegacyStatsTable(b.Types, dtype, stats.Slice(0, statCount));

        record.ChildStart = b.ReserveChildren(2);
        record.ChildCount = 2;

        LayoutView data = view.GetChild(0);
        int dataIndex = ParseNode(b, in data, dtype, depth + 1);
        b.ChildIndices[record.ChildStart] = dataIndex;

        // Upstream does not assert this for vortex.stats, only for vortex.zoned. Asserting it is
        // free and a mismatch is meaningless.
        long dataRows = b.Records[dataIndex].RowCount;
        if (dataRows != record.RowCount)
        {
            LayoutsThrow.RowCountMismatch("vortex.stats", "data", dataRows, record.RowCount);
        }

        LayoutView zones = view.GetChild(1);
        int zonesIndex = ParseNode(b, in zones, zonesType, depth + 1);
        b.ChildIndices[record.ChildStart + 1] = zonesIndex;

        int zoneCount = CheckedZoneCount(b.Records[zonesIndex].RowCount);

        // Structural only: the legacy zone map never enables pruning.
        record.ZoneMapIndex = b.AddZoneMap(new ZoneMap(false, zoneCount, zoneLength, null, 0, 0));
    }

    private static int CheckedZoneCount(long rowCount)
    {
        if (rowCount > int.MaxValue)
        {
            LayoutsThrow.Format(
                $"A zone map declares {rowCount.ToString(CultureInfo.InvariantCulture)} zones, " +
                "which cannot be addressed.");
        }

        return (int)rowCount;
    }
}
