// Every table shape that appears in spec/flatbuffers/*.fbs, built once and read back once.
//
// Field ids are the 0-based DECLARATION ORDER in the vendored .fbs files - that is what a
// FlatBuffers vtable slot means, and it is the only thing tying this hand-written runtime to the
// upstream schemas. They are transcribed here from the schema text, never from memory, and each
// block names its source file so a schema refresh has an obvious place to land.
//
// A FlatBuffers UNION occupies TWO slots: id n is the ubyte type tag and id n + 1 is the value's
// uoffset. `table DType { type: Type; }` therefore means field 0 = tag, field 1 = value.
using System;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferSchemaShapeTests
{
    // spec/flatbuffers/array.fbs
    private const int ArrayRoot = 0;
    private const int ArrayBuffers = 1;

    private const int NodeEncoding = 0;
    private const int NodeMetadata = 1;
    private const int NodeChildren = 2;
    private const int NodeBuffers = 3;
    private const int NodeStats = 4;

    private const int StatsMin = 0;
    private const int StatsMinPrecision = 1;
    private const int StatsMax = 2;
    private const int StatsMaxPrecision = 3;
    private const int StatsSum = 4;
    private const int StatsIsSorted = 5;
    private const int StatsIsStrictSorted = 6;
    private const int StatsIsConstant = 7;
    private const int StatsNullCount = 8;
    private const int StatsUncompressedSize = 9;
    private const int StatsNanCount = 10;

    // spec/flatbuffers/layout.fbs
    private const int LayoutEncoding = 0;
    private const int LayoutRowCount = 1;
    private const int LayoutMetadata = 2;
    private const int LayoutChildren = 3;
    private const int LayoutSegments = 4;

    // spec/flatbuffers/footer.fbs
    private const int PostscriptDType = 0;
    private const int PostscriptLayout = 1;
    private const int PostscriptStatistics = 2;
    private const int PostscriptFooter = 3;
    private const int PostscriptMetadata = 4;

    private const int MetadataKey = 0;
    private const int MetadataSegment = 1;

    private const int SegmentOffset = 0;
    private const int SegmentLength = 1;
    private const int SegmentAlignmentExponent = 2;
    private const int SegmentCompression = 3;
    private const int SegmentEncryption = 4;

    private const int FooterArraySpecs = 0;
    private const int FooterLayoutSpecs = 1;
    private const int FooterSegmentSpecs = 2;
    private const int FooterCompressionSpecs = 3;
    private const int FooterEncryptionSpecs = 4;

    private const int SpecId = 0;
    private const int CompressionScheme = 0;
    private const int FileStatisticsFieldStats = 0;

    // spec/flatbuffers/dtype.fbs
    private const int DTypeTag = 0;
    private const int DTypeValue = 1;
    private const int NullableOnly = 0;
    private const int PrimitivePType = 0;
    private const int PrimitiveNullable = 1;
    private const int DecimalPrecision = 0;
    private const int DecimalScale = 1;
    private const int DecimalNullable = 2;
    private const int StructNames = 0;
    private const int StructDTypes = 1;
    private const int StructNullable = 2;
    private const int ListElementType = 0;
    private const int ListNullable = 1;
    private const int FixedSizeListElementType = 0;
    private const int FixedSizeListSize = 1;
    private const int FixedSizeListNullable = 2;
    private const int ExtensionId = 0;
    private const int ExtensionStorage = 1;
    private const int ExtensionMetadata = 2;
    private const int UnionNames = 0;
    private const int UnionDTypes = 1;
    private const int UnionTypeIds = 2;
    private const int UnionNullable = 3;
    private const int MapKeyType = 0;
    private const int MapValueType = 1;
    private const int MapKeysSorted = 2;
    private const int MapNullable = 3;

    // union Type { Null = 1, ..., Map = 13 }
    private const byte TagNull = 1;
    private const byte TagBool = 2;
    private const byte TagPrimitive = 3;
    private const byte TagDecimal = 4;
    private const byte TagUtf8 = 5;
    private const byte TagBinary = 6;
    private const byte TagStruct = 7;
    private const byte TagList = 8;
    private const byte TagExtension = 9;
    private const byte TagFixedSizeList = 10;
    private const byte TagVariant = 11;
    private const byte TagUnion = 12;
    private const byte TagMap = 13;

    [Fact]
    public void Postscript_round_trips_with_all_four_segments_and_user_metadata()
    {
        using var builder = new FlatBufferBuilder();

        int zstd = WriteCompressionSpec(builder, 3);            // CompressionScheme.ZStd
        int noEncryption = WriteEncryptionSpec(builder);
        int dtypeSegment = WritePostscriptSegment(builder, 64, 128, 6, zstd, noEncryption);
        int layoutSegment = WritePostscriptSegment(builder, 192, 256, 6, 0, 0);
        int statisticsSegment = WritePostscriptSegment(builder, 448, 64, 3, 0, 0);
        int footerSegment = WritePostscriptSegment(builder, 512, 1024, 6, 0, 0);

        int key = builder.CreateString("vortex.writer");
        int metadataSegment = WritePostscriptSegment(builder, 1536, 32, 0, 0, 0);
        builder.StartTable();
        builder.AddOffset(MetadataKey, key);
        builder.AddOffset(MetadataSegment, metadataSegment);
        int metadataEntry = builder.EndTable();
        int metadataVector = builder.CreateOffsetVector(new[] { metadataEntry });

        builder.StartTable();
        builder.AddOffset(PostscriptDType, dtypeSegment);
        builder.AddOffset(PostscriptLayout, layoutSegment);
        builder.AddOffset(PostscriptStatistics, statisticsSegment);
        builder.AddOffset(PostscriptFooter, footerSegment);
        builder.AddOffset(PostscriptMetadata, metadataVector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable postscript = FlatBufferTable.Root(bytes);

        FlatBufferTable dtype = postscript.GetTable(PostscriptDType);
        Assert.Equal(64UL, dtype.GetUInt64(SegmentOffset));
        Assert.Equal(128u, dtype.GetUInt32(SegmentLength));
        Assert.Equal((byte)6, dtype.GetUInt8(SegmentAlignmentExponent));
        Assert.Equal((byte)3, dtype.GetTable(SegmentCompression).GetUInt8(CompressionScheme));
        Assert.False(dtype.GetTable(SegmentEncryption).IsNull);

        // `_compression` and `_encryption` are optional; an absent one is a null sub-table, not a
        // zero-valued one.
        FlatBufferTable layout = postscript.GetTable(PostscriptLayout);
        Assert.Equal(192UL, layout.GetUInt64(SegmentOffset));
        Assert.True(layout.GetTable(SegmentCompression).IsNull);

        Assert.Equal(448UL, postscript.GetTable(PostscriptStatistics).GetUInt64(SegmentOffset));
        Assert.Equal(1024u, postscript.GetTable(PostscriptFooter).GetUInt32(SegmentLength));

        FlatBufferVector metadata = postscript.GetVector(PostscriptMetadata);
        Assert.Equal(1, metadata.Count);
        FlatBufferTable entry = metadata.GetTable(0);
        Assert.True(entry.GetStringUtf8(MetadataKey).SequenceEqual("vortex.writer"u8));
        Assert.Equal(1536UL, entry.GetTable(MetadataSegment).GetUInt64(SegmentOffset));
    }

    [Fact]
    public void Postscript_with_only_its_required_segments_round_trips()
    {
        // `layout` and `footer` are the only required segments; a reader must handle a postscript
        // with no dtype, no statistics and no metadata.
        using var builder = new FlatBufferBuilder();
        int layoutSegment = WritePostscriptSegment(builder, 8, 16, 3, 0, 0);
        int footerSegment = WritePostscriptSegment(builder, 24, 32, 3, 0, 0);

        builder.StartTable();
        builder.AddOffset(PostscriptLayout, layoutSegment);
        builder.AddOffset(PostscriptFooter, footerSegment);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable postscript = FlatBufferTable.Root(bytes);
        Assert.True(postscript.GetTable(PostscriptDType).IsNull);
        Assert.True(postscript.GetTable(PostscriptStatistics).IsNull);
        Assert.Equal(0, postscript.GetVector(PostscriptMetadata).Count);
        Assert.Equal(8UL, postscript.GetTable(PostscriptLayout).GetUInt64(SegmentOffset));
        Assert.Equal(24UL, postscript.GetTable(PostscriptFooter).GetUInt64(SegmentOffset));
    }

    [Fact]
    public void Footer_round_trips_including_its_struct_vector_of_SegmentSpec()
    {
        SegmentSpecLike[] segments =
        [
            new() { Offset = 0, Length = 4096, AlignmentExponent = 6, Compression = 0, Encryption = 0 },
            new() { Offset = 4096, Length = 128, AlignmentExponent = 3, Compression = 1, Encryption = 0 },
            new() { Offset = 4224, Length = 64, AlignmentExponent = 0, Compression = 0, Encryption = 1 },
        ];

        using var builder = new FlatBufferBuilder();

        int flatId = builder.CreateString("vortex.flat");
        int chunkedId = builder.CreateString("vortex.chunked");
        builder.StartTable();
        builder.AddOffset(SpecId, flatId);
        int flatLayoutSpec = builder.EndTable();
        builder.StartTable();
        builder.AddOffset(SpecId, chunkedId);
        int chunkedLayoutSpec = builder.EndTable();
        int layoutSpecs = builder.CreateOffsetVector(new[] { flatLayoutSpec, chunkedLayoutSpec });

        int primitiveId = builder.CreateString("vortex.primitive");
        builder.StartTable();
        builder.AddOffset(SpecId, primitiveId);
        int primitiveArraySpec = builder.EndTable();
        int arraySpecs = builder.CreateOffsetVector(new[] { primitiveArraySpec });

        int segmentSpecs = builder.CreateStructVector<SegmentSpecLike>(segments);
        int compressionSpecs = builder.CreateOffsetVector(new[] { WriteCompressionSpec(builder, 1) });
        int encryptionSpecs = builder.CreateOffsetVector(new[] { WriteEncryptionSpec(builder) });

        builder.StartTable();
        builder.AddOffset(FooterArraySpecs, arraySpecs);
        builder.AddOffset(FooterLayoutSpecs, layoutSpecs);
        builder.AddOffset(FooterSegmentSpecs, segmentSpecs);
        builder.AddOffset(FooterCompressionSpecs, compressionSpecs);
        builder.AddOffset(FooterEncryptionSpecs, encryptionSpecs);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable footer = FlatBufferTable.Root(aligned.Span);

        FlatBufferVector layoutSpecVector = footer.GetVector(FooterLayoutSpecs);
        Assert.Equal(2, layoutSpecVector.Count);
        Assert.True(layoutSpecVector.GetTable(0).GetStringUtf8(SpecId).SequenceEqual("vortex.flat"u8));
        Assert.True(layoutSpecVector.GetTable(1).GetStringUtf8(SpecId).SequenceEqual("vortex.chunked"u8));
        Assert.True(footer.GetVector(FooterArraySpecs).GetTable(0).GetStringUtf8(SpecId)
            .SequenceEqual("vortex.primitive"u8));

        // The point of the whole exercise: segment_specs is reinterpreted in place, no traversal.
        ReadOnlySpan<SegmentSpecLike> read = footer.GetStructVector<SegmentSpecLike>(FooterSegmentSpecs);
        Assert.Equal(3, read.Length);
        for (int i = 0; i < read.Length; i++)
        {
            Assert.Equal(segments[i].Offset, read[i].Offset);
            Assert.Equal(segments[i].Length, read[i].Length);
            Assert.Equal(segments[i].AlignmentExponent, read[i].AlignmentExponent);
            Assert.Equal(segments[i].Compression, read[i].Compression);
            Assert.Equal(segments[i].Encryption, read[i].Encryption);
        }

        Assert.Equal((byte)1, footer.GetVector(FooterCompressionSpecs).GetTable(0).GetUInt8(CompressionScheme));
        Assert.False(footer.GetVector(FooterEncryptionSpecs).GetTable(0).IsNull);
    }

    [Fact]
    public void ArrayNode_round_trips_with_nested_children_and_a_Buffer_struct_vector()
    {
        BufferLike[] buffers =
        [
            new() { Padding = 0, AlignmentExponent = 6, Compression = 0, Length = 4096 },
            new() { Padding = 16, AlignmentExponent = 6, Compression = 1, Length = 512 },
        ];

        using var builder = new FlatBufferBuilder();

        // Two leaves, then a parent, then the root Array - children first, as FlatBuffers requires.
        int leafA = WriteArrayNode(builder, encoding: 1, metadata: [0x01, 0x02], children: 0,
            bufferIndices: [0], stats: 0);
        int leafB = WriteArrayNode(builder, encoding: 2, metadata: [0x03], children: 0,
            bufferIndices: [1], stats: 0);
        int childVector = builder.CreateOffsetVector(new[] { leafA, leafB });
        int stats = WriteArrayStats(builder);
        int rootNode = WriteArrayNode(builder, encoding: 7, metadata: default, children: childVector,
            bufferIndices: default, stats: stats);

        int bufferVector = builder.CreateStructVector<BufferLike>(buffers);
        builder.StartTable();
        builder.AddOffset(ArrayRoot, rootNode);
        builder.AddOffset(ArrayBuffers, bufferVector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable array = FlatBufferTable.Root(aligned.Span);

        ReadOnlySpan<BufferLike> readBuffers = array.GetStructVector<BufferLike>(ArrayBuffers);
        Assert.Equal(2, readBuffers.Length);
        Assert.Equal(4096u, readBuffers[0].Length);
        Assert.Equal((ushort)16, readBuffers[1].Padding);

        FlatBufferTable node = array.GetTable(ArrayRoot);
        Assert.Equal((ushort)7, node.GetUInt16(NodeEncoding));
        Assert.True(node.GetByteVector(NodeMetadata).IsEmpty);

        FlatBufferVector children = node.GetVector(NodeChildren);
        Assert.Equal(2, children.Count);
        FlatBufferTable first = children.GetTable(0);
        Assert.Equal((ushort)1, first.GetUInt16(NodeEncoding));
        Assert.True(first.GetByteVector(NodeMetadata).SequenceEqual(new byte[] { 0x01, 0x02 }));
        Assert.True(first.GetStructVector<ushort>(NodeBuffers).SequenceEqual(new ushort[] { 0 }));
        Assert.Equal((ushort)2, children.GetTable(1).GetUInt16(NodeEncoding));
        Assert.True(children.GetTable(1).GetStructVector<ushort>(NodeBuffers).SequenceEqual(new ushort[] { 1 }));
    }

    [Fact]
    public void ArrayStats_round_trips_its_tri_state_fields()
    {
        using var builder = new FlatBufferBuilder();
        int stats = WriteArrayStats(builder);
        builder.StartTable();
        builder.AddUInt16(NodeEncoding, 1);
        builder.AddOffset(NodeChildren, 0);        // absent: AddOffset(id, 0) writes nothing
        builder.AddOffset(NodeStats, stats);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable node = FlatBufferTable.Root(bytes);
        Assert.False(node.HasField(NodeChildren));
        FlatBufferTable read = node.GetTable(NodeStats);

        // Protobuf-serialized ScalarValue blobs, opaque at this layer.
        Assert.True(read.GetByteVector(StatsMin).SequenceEqual(new byte[] { 0x08, 0x01 }));
        Assert.True(read.GetByteVector(StatsMax).SequenceEqual(new byte[] { 0x08, 0x7F }));
        Assert.True(read.GetByteVector(StatsSum).IsEmpty);

        // Precision.Exact == 1, so min_precision is stored; Precision.Inexact == 0 is the schema
        // default, so max_precision is absent and reads back as Inexact.
        Assert.Equal((byte)1, read.GetUInt8(StatsMinPrecision));
        Assert.Equal((byte)0, read.GetUInt8(StatsMaxPrecision));

        // The three-way distinction the `= null` fields exist for.
        Assert.True(read.TryGetBool(StatsIsSorted, out bool isSorted));
        Assert.True(isSorted);
        Assert.True(read.TryGetBool(StatsIsStrictSorted, out bool isStrict));
        Assert.False(isStrict);                                    // computed: not strictly sorted
        Assert.False(read.TryGetBool(StatsIsConstant, out _));     // never computed

        Assert.True(read.TryGetUInt64(StatsNullCount, out ulong nullCount));
        Assert.Equal(0UL, nullCount);                              // computed: no nulls
        Assert.True(read.TryGetUInt64(StatsUncompressedSize, out ulong uncompressed));
        Assert.Equal(1UL << 40, uncompressed);
        Assert.False(read.TryGetUInt64(StatsNanCount, out _));     // never computed
    }

    [Fact]
    public void FileStatistics_round_trips_a_vector_of_ArrayStats()
    {
        using var builder = new FlatBufferBuilder();
        int[] fieldStats = [WriteArrayStats(builder), WriteArrayStats(builder)];
        int vector = builder.CreateOffsetVector(fieldStats);
        builder.StartTable();
        builder.AddOffset(FileStatisticsFieldStats, vector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferVector stats = FlatBufferTable.Root(bytes).GetVector(FileStatisticsFieldStats);
        Assert.Equal(2, stats.Count);
        for (int i = 0; i < stats.Count; i++)
        {
            FlatBufferTable entry = stats.GetTable(i);
            Assert.True(entry.TryGetUInt64(StatsNullCount, out ulong count));
            Assert.Equal(0UL, count);
            Assert.True(entry.TryGetBool(StatsIsSorted, out bool sorted));
            Assert.True(sorted);
            Assert.False(entry.TryGetUInt64(StatsNanCount, out _));
            Assert.True(entry.GetByteVector(StatsMin).SequenceEqual(new byte[] { 0x08, 0x01 }));
        }
    }

    [Fact]
    public void Layout_round_trips_with_nested_children_and_a_segment_id_vector()
    {
        using var builder = new FlatBufferBuilder();

        int chunkA = WriteLayout(builder, encoding: 1, rowCount: 8192, metadata: default,
            children: 0, segments: [0, 1]);
        int chunkB = WriteLayout(builder, encoding: 1, rowCount: 4096, metadata: default,
            children: 0, segments: [2]);
        int chunkVector = builder.CreateOffsetVector(new[] { chunkA, chunkB });

        // ChunkedLayout uses metadata[0] as a flag saying whether the first child is the stats
        // table for the other chunks - spec/flatbuffers/layout.fbs.
        int root = WriteLayout(builder, encoding: 2, rowCount: 12288, metadata: [0x00],
            children: chunkVector, segments: default);
        byte[] bytes = builder.FinishToArray(root);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable layout = FlatBufferTable.Root(aligned.Span);
        Assert.Equal((ushort)2, layout.GetUInt16(LayoutEncoding));
        Assert.Equal(12288UL, layout.GetUInt64(LayoutRowCount));
        Assert.True(layout.GetByteVector(LayoutMetadata).SequenceEqual(new byte[] { 0x00 }));
        Assert.True(layout.GetStructVector<uint>(LayoutSegments).IsEmpty);

        FlatBufferVector children = layout.GetVector(LayoutChildren);
        Assert.Equal(2, children.Count);
        Assert.Equal(8192UL, children.GetTable(0).GetUInt64(LayoutRowCount));
        Assert.True(children.GetTable(0).GetStructVector<uint>(LayoutSegments)
            .SequenceEqual(new uint[] { 0, 1 }));
        Assert.Equal(4096UL, children.GetTable(1).GetUInt64(LayoutRowCount));
        Assert.True(children.GetTable(1).GetStructVector<uint>(LayoutSegments)
            .SequenceEqual(new uint[] { 2 }));
    }

    [Fact]
    public void Every_DType_union_arm_round_trips()
    {
        using var builder = new FlatBufferBuilder();

        // The payload-free arms first. `table Null {}` has no fields at all; the rest carry only
        // `nullable`, so a non-nullable one is an empty table too and they all share a vtable.
        int nullType = WriteEmptyTable(builder);
        int nullDType = WriteDType(builder, TagNull, nullType);
        int boolType = WriteNullableOnly(builder, nullable: true);
        int boolDType = WriteDType(builder, TagBool, boolType);
        int utf8Type = WriteNullableOnly(builder, nullable: false);
        int utf8DType = WriteDType(builder, TagUtf8, utf8Type);
        int binaryType = WriteNullableOnly(builder, nullable: true);
        int binaryDType = WriteDType(builder, TagBinary, binaryType);
        int variantType = WriteNullableOnly(builder, nullable: false);
        int variantDType = WriteDType(builder, TagVariant, variantType);

        // Primitive { ptype: PType; nullable: bool; }. PType.I32 == 6.
        builder.StartTable();
        builder.AddUInt8(PrimitivePType, 6);
        builder.AddBool(PrimitiveNullable, true);
        int primitiveType = builder.EndTable();
        int primitiveDType = WriteDType(builder, TagPrimitive, primitiveType);

        // Decimal { precision: uint8; scale: int8; nullable: bool; } - a negative scale is legal
        // (spec/METADATA.md), so it is the value used here.
        builder.StartTable();
        builder.AddUInt8(DecimalPrecision, 76);
        builder.AddInt8(DecimalScale, -4);
        builder.AddBool(DecimalNullable, true);
        int decimalType = builder.EndTable();
        int decimalDType = WriteDType(builder, TagDecimal, decimalType);

        // List { element_type: DType; nullable: bool; }
        builder.StartTable();
        builder.AddOffset(ListElementType, primitiveDType);
        builder.AddBool(ListNullable, true);
        int listType = builder.EndTable();
        int listDType = WriteDType(builder, TagList, listType);

        // FixedSizeList { element_type: DType; size: uint32; nullable: bool; }
        builder.StartTable();
        builder.AddOffset(FixedSizeListElementType, primitiveDType);
        builder.AddUInt32(FixedSizeListSize, 3);
        builder.AddBool(FixedSizeListNullable, false);
        int fslType = builder.EndTable();
        int fslDType = WriteDType(builder, TagFixedSizeList, fslType);

        // Extension { id: string; storage_dtype: DType; metadata: [ubyte]; }
        int extensionId = builder.CreateString("vortex.date");
        int extensionMetadata = builder.CreateByteVector(stackalloc byte[] { 0x01 });
        builder.StartTable();
        builder.AddOffset(ExtensionId, extensionId);
        builder.AddOffset(ExtensionStorage, primitiveDType);
        builder.AddOffset(ExtensionMetadata, extensionMetadata);
        int extensionType = builder.EndTable();
        int extensionDType = WriteDType(builder, TagExtension, extensionType);

        // Struct_ { names: [string]; dtypes: [DType]; nullable: bool; }
        int nameA = builder.CreateString("id");
        int nameB = builder.CreateString("payload");
        int names = builder.CreateOffsetVector(new[] { nameA, nameB });
        int dtypes = builder.CreateOffsetVector(new[] { primitiveDType, utf8DType });
        builder.StartTable();
        builder.AddOffset(StructNames, names);
        builder.AddOffset(StructDTypes, dtypes);
        builder.AddBool(StructNullable, false);
        int structType = builder.EndTable();
        int structDType = WriteDType(builder, TagStruct, structType);

        // Union { names; dtypes; type_ids: [byte]; nullable; } - type_ids is a signed [byte] that
        // spec/flatbuffers/dtype.fbs says to interpret as unsigned, so 200 must survive the trip.
        int unionTypeIds = builder.CreateScalarVector<sbyte>(new sbyte[] { 0, unchecked((sbyte)200) });
        builder.StartTable();
        builder.AddOffset(UnionNames, names);
        builder.AddOffset(UnionDTypes, dtypes);
        builder.AddOffset(UnionTypeIds, unionTypeIds);
        builder.AddBool(UnionNullable, true);
        int unionType = builder.EndTable();
        int unionDType = WriteDType(builder, TagUnion, unionType);

        // Map { key_type; value_type; keys_sorted; nullable; }
        builder.StartTable();
        builder.AddOffset(MapKeyType, utf8DType);
        builder.AddOffset(MapValueType, primitiveDType);
        builder.AddBool(MapKeysSorted, true);
        builder.AddBool(MapNullable, false);
        int mapType = builder.EndTable();
        int mapDType = WriteDType(builder, TagMap, mapType);

        // One holder table pointing at every arm, so a single buffer covers the whole union.
        int all = builder.CreateOffsetVector(new[]
        {
            nullDType, boolDType, primitiveDType, decimalDType, utf8DType, binaryDType,
            structDType, listDType, extensionDType, fslDType, variantDType, unionDType, mapDType,
        });
        builder.StartTable();
        builder.AddOffset(0, all);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferVector arms = FlatBufferTable.Root(aligned.Span).GetVector(0);
        Assert.Equal(13, arms.Count);

        // The tags come out in union declaration order, 1..13.
        for (int i = 0; i < arms.Count; i++)
        {
            Assert.Equal((byte)(i + 1), arms.GetTable(i).GetUInt8(DTypeTag));
        }

        Assert.True(arms.GetTable(0).GetTable(DTypeValue).IsNull is false);
        Assert.True(arms.GetTable(1).GetTable(DTypeValue).GetBool(NullableOnly));

        FlatBufferTable primitive = arms.GetTable(2).GetTable(DTypeValue);
        Assert.Equal((byte)6, primitive.GetUInt8(PrimitivePType));
        Assert.True(primitive.GetBool(PrimitiveNullable));

        FlatBufferTable dec = arms.GetTable(3).GetTable(DTypeValue);
        Assert.Equal((byte)76, dec.GetUInt8(DecimalPrecision));
        Assert.Equal((sbyte)-4, dec.GetInt8(DecimalScale));

        FlatBufferTable structured = arms.GetTable(6).GetTable(DTypeValue);
        FlatBufferVector fieldNames = structured.GetVector(StructNames);
        Assert.Equal(2, fieldNames.Count);
        Assert.True(fieldNames.GetStringUtf8(0).SequenceEqual("id"u8));
        Assert.True(fieldNames.GetStringUtf8(1).SequenceEqual("payload"u8));
        Assert.Equal((byte)3, structured.GetVector(StructDTypes).GetTable(0).GetUInt8(DTypeTag));
        Assert.Equal((byte)5, structured.GetVector(StructDTypes).GetTable(1).GetUInt8(DTypeTag));
        Assert.False(structured.GetBool(StructNullable));

        FlatBufferTable list = arms.GetTable(7).GetTable(DTypeValue);
        Assert.Equal((byte)3, list.GetTable(ListElementType).GetUInt8(DTypeTag));

        FlatBufferTable extension = arms.GetTable(8).GetTable(DTypeValue);
        Assert.True(extension.GetStringUtf8(ExtensionId).SequenceEqual("vortex.date"u8));
        Assert.Equal((byte)3, extension.GetTable(ExtensionStorage).GetUInt8(DTypeTag));
        Assert.True(extension.GetByteVector(ExtensionMetadata).SequenceEqual(new byte[] { 0x01 }));

        FlatBufferTable fsl = arms.GetTable(9).GetTable(DTypeValue);
        Assert.Equal(3u, fsl.GetUInt32(FixedSizeListSize));

        FlatBufferTable union = arms.GetTable(11).GetTable(DTypeValue);
        ReadOnlySpan<sbyte> typeIds = union.GetStructVector<sbyte>(UnionTypeIds);
        Assert.Equal(2, typeIds.Length);
        Assert.Equal((byte)200, (byte)typeIds[1]);

        FlatBufferTable map = arms.GetTable(12).GetTable(DTypeValue);
        Assert.Equal((byte)5, map.GetTable(MapKeyType).GetUInt8(DTypeTag));
        Assert.Equal((byte)3, map.GetTable(MapValueType).GetUInt8(DTypeTag));
        Assert.True(map.GetBool(MapKeysSorted));
        Assert.False(map.GetBool(MapNullable));
    }

    // ---------------------------------------------------------------------------------------
    // Shape writers. Each names its .fbs source; the field ids above are the declaration order.
    // ---------------------------------------------------------------------------------------

    /// <summary>spec/flatbuffers/footer.fbs <c>table CompressionSpec { scheme: CompressionScheme; }</c>.</summary>
    private static int WriteCompressionSpec(FlatBufferBuilder builder, byte scheme)
    {
        builder.StartTable();
        builder.AddUInt8(CompressionScheme, scheme);
        return builder.EndTable();
    }

    /// <summary>spec/flatbuffers/footer.fbs <c>table EncryptionSpec {}</c>.</summary>
    private static int WriteEncryptionSpec(FlatBufferBuilder builder) => WriteEmptyTable(builder);

    private static int WriteEmptyTable(FlatBufferBuilder builder)
    {
        builder.StartTable();
        return builder.EndTable();
    }

    /// <summary>Any dtype.fbs arm whose only field is <c>nullable: bool</c>.</summary>
    private static int WriteNullableOnly(FlatBufferBuilder builder, bool nullable)
    {
        builder.StartTable();
        builder.AddBool(NullableOnly, nullable);
        return builder.EndTable();
    }

    /// <summary>
    /// spec/flatbuffers/dtype.fbs <c>table DType { type: Type; }</c>. A FlatBuffers union takes two
    /// slots: field 0 is the <c>ubyte</c> tag, field 1 the value's uoffset.
    /// </summary>
    private static int WriteDType(FlatBufferBuilder builder, byte tag, int valueTable)
    {
        builder.StartTable();
        builder.AddUInt8(DTypeTag, tag);
        builder.AddOffset(DTypeValue, valueTable);
        return builder.EndTable();
    }

    /// <summary>spec/flatbuffers/footer.fbs <c>table PostscriptSegment</c>.</summary>
    private static int WritePostscriptSegment(
        FlatBufferBuilder builder, ulong offset, uint length, byte alignmentExponent,
        int compressionSpec, int encryptionSpec)
    {
        builder.StartTable();
        builder.AddUInt64(SegmentOffset, offset);
        builder.AddUInt32(SegmentLength, length);
        builder.AddUInt8(SegmentAlignmentExponent, alignmentExponent);
        builder.AddOffset(SegmentCompression, compressionSpec);
        builder.AddOffset(SegmentEncryption, encryptionSpec);
        return builder.EndTable();
    }

    /// <summary>spec/flatbuffers/array.fbs <c>table ArrayNode</c>.</summary>
    private static int WriteArrayNode(
        FlatBufferBuilder builder, ushort encoding, ReadOnlySpan<byte> metadata, int children,
        ReadOnlySpan<ushort> bufferIndices, int stats)
    {
        int metadataVector = metadata.IsEmpty ? 0 : builder.CreateByteVector(metadata);
        int buffersVector = bufferIndices.IsEmpty ? 0 : builder.CreateScalarVector(bufferIndices);

        builder.StartTable();
        builder.AddUInt16(NodeEncoding, encoding);
        builder.AddOffset(NodeMetadata, metadataVector);
        builder.AddOffset(NodeChildren, children);
        builder.AddOffset(NodeBuffers, buffersVector);
        builder.AddOffset(NodeStats, stats);
        return builder.EndTable();
    }

    /// <summary>
    /// spec/flatbuffers/array.fbs <c>table ArrayStats</c>, with each of the three tri-states in a
    /// different state: known-true, known-false, and unknown.
    /// </summary>
    private static int WriteArrayStats(FlatBufferBuilder builder)
    {
        int min = builder.CreateByteVector(stackalloc byte[] { 0x08, 0x01 });
        int max = builder.CreateByteVector(stackalloc byte[] { 0x08, 0x7F });

        builder.StartTable();
        builder.AddOffset(StatsMin, min);
        builder.AddUInt8(StatsMinPrecision, 1);                 // Precision.Exact
        builder.AddOffset(StatsMax, max);
        builder.AddUInt8(StatsMaxPrecision, 0);                 // Precision.Inexact, the default
        builder.AddBoolAlways(StatsIsSorted, true);
        builder.AddBoolAlways(StatsIsStrictSorted, false);
        // is_constant is deliberately never written: "not computed".
        builder.AddUInt64Always(StatsNullCount, 0);
        builder.AddUInt64Always(StatsUncompressedSize, 1UL << 40);
        // nan_count is deliberately never written.
        return builder.EndTable();
    }

    /// <summary>spec/flatbuffers/layout.fbs <c>table Layout</c>.</summary>
    private static int WriteLayout(
        FlatBufferBuilder builder, ushort encoding, ulong rowCount, ReadOnlySpan<byte> metadata,
        int children, ReadOnlySpan<uint> segments)
    {
        int metadataVector = metadata.IsEmpty ? 0 : builder.CreateByteVector(metadata);
        int segmentVector = segments.IsEmpty ? 0 : builder.CreateScalarVector(segments);

        builder.StartTable();
        builder.AddUInt16(LayoutEncoding, encoding);
        builder.AddUInt64(LayoutRowCount, rowCount);
        builder.AddOffset(LayoutMetadata, metadataVector);
        builder.AddOffset(LayoutChildren, children);
        builder.AddOffset(LayoutSegments, segmentVector);
        return builder.EndTable();
    }
}
