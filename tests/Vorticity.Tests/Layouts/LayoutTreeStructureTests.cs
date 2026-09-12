// The adversarial half: every arity, sum, depth and index rule the parser enforces, driven by
// hand-built Layout FlatBuffers.
//
// The promise these tests exist to keep (docs/09-contracts.md §4.2): malformed input yields
// VortexFormatException and nothing else - never an out-of-bounds read, never an unbounded
// allocation, never a hang, never a wrong value passed off as right.
using System;
using System.Collections.Generic;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Layouts;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LayoutTreeStructureTests
{
    private static readonly DTypeArena Types = new DTypeArena();

    private static DType I64 => Types.Primitive(PType.I64, Nullability.NonNullable);

    private static DType Utf8 => Types.Utf8(Nullability.NonNullable);

    // ------------------------------------------------------------------------------ vortex.flat

    [Fact]
    public void FlatWithNoSegmentIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.flat", 4);
        AssertFormat(root, I64, segmentCount: 4, "segment");
    }

    [Fact]
    public void FlatWithTwoSegmentsIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.flat", 4).WithSegments(0, 1);
        AssertFormat(root, I64, segmentCount: 4, "segment");
    }

    [Fact]
    public void FlatWithAChildIsRejected()
    {
        SyntheticLayout root = SyntheticLayout.Flat(4, 0).With(SyntheticLayout.Flat(4, 1));
        AssertFormat(root, I64, segmentCount: 4, "children");
    }

    [Fact]
    public void FlatWithOneSegmentAndNoChildParses()
    {
        LayoutTree tree = Parse(SyntheticLayout.Flat(4, 3), I64, segmentCount: 4);
        Assert.Equal(LayoutEncodingId.Flat, tree.Root.Encoding);
        Assert.Equal(4, tree.Root.RowCount);
        Assert.Equal(1, tree.Root.Segments.Length);
        Assert.Equal(3u, tree.Root.Segments[0]);
        Assert.Equal(0, tree.Root.ChildCount);
    }

    // --------------------------------------------------------------------------- vortex.chunked

    [Fact]
    public void ChunkedWhoseChunksDoNotSumIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 300)
            .With(SyntheticLayout.Flat(100, 0), SyntheticLayout.Flat(100, 1));
        AssertFormat(root, I64, segmentCount: 4, "chunks cover");
    }

    [Fact]
    public void ChunkedWithNoChildrenAndRowsIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 5);
        AssertFormat(root, I64, segmentCount: 4, "chunks cover");
    }

    [Fact]
    public void ChunkedWithNoChildrenAndNoRowsParses()
    {
        LayoutTree tree = Parse(new SyntheticLayout("vortex.chunked", 0), I64, segmentCount: 1);
        Assert.Equal(0, tree.Root.ChildCount);
        Assert.Equal(0, tree.Root.RowCount);
    }

    [Fact]
    public void ChunkedWithASegmentIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 0).WithSegments(0);
        AssertFormat(root, I64, segmentCount: 4, "segment");
    }

    [Fact]
    public void ChunkedMetadataFlagExcludesTheFirstChildFromTheChunks()
    {
        // UNTESTED against a real file by construction: 0.86.1 rejects non-empty chunked metadata
        // and no writer sets the flag. This pins OUR reading of it (contract §11.3).
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 200)
            .WithMetadata([1])
            .With(
                SyntheticLayout.Flat(7, 0),
                SyntheticLayout.Flat(100, 1),
                SyntheticLayout.Flat(100, 2));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        Assert.Equal(2, tree.Root.ChildCount);
        Assert.Equal(100, tree.Root.GetChild(0).RowCount);
        Assert.Equal(1u, tree.Root.GetChild(0).Segments[0]);
    }

    [Fact]
    public void ChunkedMetadataFlagWithAWrongGuessIsRejectedByTheSum()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 207)
            .WithMetadata([1])
            .With(
                SyntheticLayout.Flat(7, 0),
                SyntheticLayout.Flat(100, 1),
                SyntheticLayout.Flat(100, 2));

        AssertFormat(root, I64, segmentCount: 4, "chunks cover");
    }

    // ---------------------------------------------------------------------------- vortex.struct

    [Fact]
    public void StructWithTheWrongChildCountIsRejected()
    {
        DType schema = Struct(nullable: false);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4).With(SyntheticLayout.Flat(4, 0));
        AssertFormat(root, schema, segmentCount: 4, "children");
    }

    [Fact]
    public void NonNullableStructPutsFieldKAtIndexK()
    {
        DType schema = Struct(nullable: false);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(4, 1));

        LayoutTree tree = Parse(root, schema, segmentCount: 4);
        Assert.Equal(2, tree.Root.ChildCount);
        Assert.Equal(schema.GetField(0), tree.Root.GetChild(0).DType);
        Assert.Equal(schema.GetField(1), tree.Root.GetChild(1).DType);
    }

    [Fact]
    public void NullableStructPutsValidityFirstAsNonNullableBool()
    {
        DType schema = Struct(nullable: true);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(4, 1), SyntheticLayout.Flat(4, 2));

        LayoutTree tree = Parse(root, schema, segmentCount: 4);
        Assert.Equal(3, tree.Root.ChildCount);

        DType validity = tree.Root.GetChild(0).DType;
        Assert.Equal(DTypeKind.Bool, validity.Kind);
        Assert.Equal(Nullability.NonNullable, validity.Nullability);
        Assert.Equal(schema.GetField(0), tree.Root.GetChild(1).DType);
        Assert.Equal(schema.GetField(1), tree.Root.GetChild(2).DType);
    }

    [Fact]
    public void NullableStructWithNonNullableChildCountIsRejected()
    {
        DType schema = Struct(nullable: true);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(4, 1));
        AssertFormat(root, schema, segmentCount: 4, "children");
    }

    [Fact]
    public void StructWithNonEmptyMetadataIsRejected()
    {
        DType schema = Struct(nullable: false);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .WithMetadata([0])
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(4, 1));
        AssertFormat(root, schema, segmentCount: 4, "metadata must be empty");
    }

    [Fact]
    public void StructWhoseChildCoversADifferentRowCountIsRejected()
    {
        DType schema = Struct(nullable: false);
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4)
            .With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(3, 1));
        AssertFormat(root, schema, segmentCount: 4, "field 1");
    }

    [Fact]
    public void StructOverANonStructDTypeIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.struct", 4);
        AssertFormat(root, I64, segmentCount: 4, "non-struct");
    }

    // ------------------------------------------------------------------------------ vortex.dict

    [Fact]
    public void DictChildOrderIsValuesThenCodes()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.U16, null))
            .With(SyntheticLayout.Flat(3, 0), SyntheticLayout.Flat(8, 1));

        LayoutTree tree = Parse(root, Utf8, segmentCount: 4);

        // Child 0 is VALUES, with the layout's own dtype and its own cardinality.
        Assert.Equal(Utf8, tree.Root.GetChild(0).DType);
        Assert.Equal(3, tree.Root.GetChild(0).RowCount);

        // Child 1 is CODES, Primitive(codes_ptype) over the parent's rows.
        Assert.Equal(DTypeKind.Primitive, tree.Root.GetChild(1).DType.Kind);
        Assert.Equal(PType.U16, tree.Root.GetChild(1).DType.PType);
        Assert.Equal(8, tree.Root.GetChild(1).RowCount);
    }

    [Fact]
    public void DictCodesNullabilityFallsBackToTheLayoutDType()
    {
        DType nullableUtf8 = Types.Utf8(Nullability.Nullable);
        SyntheticLayout root = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.U8, null))
            .With(SyntheticLayout.Flat(3, 0), SyntheticLayout.Flat(8, 1));

        LayoutTree tree = Parse(root, nullableUtf8, segmentCount: 4);
        Assert.Equal(Nullability.Nullable, tree.Root.GetChild(1).DType.Nullability);

        SyntheticLayout explicitly = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.U8, false))
            .With(SyntheticLayout.Flat(3, 0), SyntheticLayout.Flat(8, 1));

        LayoutTree second = Parse(explicitly, nullableUtf8, segmentCount: 4);
        Assert.Equal(Nullability.NonNullable, second.Root.GetChild(1).DType.Nullability);
    }

    [Fact]
    public void DictWhoseCodesCoverADifferentRowCountIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.U16, null))
            .With(SyntheticLayout.Flat(3, 0), SyntheticLayout.Flat(7, 1));
        AssertFormat(root, Utf8, segmentCount: 4, "codes");
    }

    [Fact]
    public void DictWithTheWrongChildCountIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.U16, null))
            .With(SyntheticLayout.Flat(8, 0));
        AssertFormat(root, Utf8, segmentCount: 4, "children");
    }

    [Fact]
    public void DictWithFloatCodesIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.dict", 8)
            .WithMetadata(DictMetadataBytes(PType.F64, null))
            .With(SyntheticLayout.Flat(3, 0), SyntheticLayout.Flat(8, 1));
        AssertFormat(root, Utf8, segmentCount: 4, "integer physical type");
    }

    // ----------------------------------------------------------------------------- vortex.zoned

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 1, 0 })]
    public void ZonedMetadataThatIsNotVersionOnePlusAProtoIsRejected(byte[] metadata)
    {
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(metadata)
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(1, 1));

        Assert.Throws<VortexFormatException>(() => Parse(root, I64, segmentCount: 4));
    }

    [Fact]
    public void ZonedWithZoneLengthZeroParsesWithPruningUnavailable()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadataBytes(0, "vortex.min", "vortex.max"))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(1, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        Assert.True(tree.Root.TryGetZoneMap(out ZoneMap map));
        Assert.False(map.IsPruningAvailable);
        Assert.Equal(0, map.ZoneLength);
        Assert.Equal(2, map.AggregateCount);

        // The zones child is still resolved, exactly as upstream's deserialize does.
        Assert.Equal(2, tree.Root.ChildCount);
        Assert.Equal(DTypeKind.Struct, tree.Root.GetChild(1).DType.Kind);
    }

    [Fact]
    public void ZonedWithAnUnknownAggregateDisablesPruningWithoutThrowing()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadataBytes(4, "vortex.min", "vortex.bogus"))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        Assert.True(tree.Root.TryGetZoneMap(out ZoneMap map));
        Assert.False(map.IsPruningAvailable);
        Assert.Equal(AggregateId.Min, map.GetAggregate(0));
        Assert.Equal(AggregateId.Unknown, map.GetAggregate(1));

        // The zones table cannot be reconstructed, so its child stays unresolved - which is what
        // upstream does when try_aggregate_fns_from_specs returns Ok(None).
        Assert.Equal(1, tree.Root.ChildCount);
    }

    [Fact]
    public void ZonedDataChildRowCountMustMatch()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadataBytes(4, "vortex.min"))
            .With(SyntheticLayout.Flat(7, 0), SyntheticLayout.Flat(2, 1));
        AssertFormat(root, I64, segmentCount: 4, "data");
    }

    [Fact]
    public void ZonedZonesTableCarriesOneNullableColumnPerAggregate()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadataBytes(4, "vortex.min", "vortex.null_count", "vortex.nan_count"))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        DType zones = tree.Root.GetChild(1).DType;

        // nan_count has no state dtype for an integer column, so it contributes NO column.
        Assert.Equal(DTypeKind.Struct, zones.Kind);
        Assert.Equal(Nullability.NonNullable, zones.Nullability);
        Assert.Equal(2, zones.FieldCount);
        Assert.Equal("vortex.min()", zones.GetFieldName(0));
        Assert.Equal("vortex.null_count()", zones.GetFieldName(1));
        Assert.Equal(Nullability.Nullable, zones.GetField(0).Nullability);
        Assert.Equal(PType.I64, zones.GetField(0).PType);
        Assert.Equal(PType.U64, zones.GetField(1).PType);

        Assert.True(tree.Root.TryGetZoneMap(out ZoneMap map));
        Assert.Equal(3, map.AggregateCount);
        Assert.Equal(0, map.GetColumnIndex(0));
        Assert.Equal(1, map.GetColumnIndex(1));
        Assert.Equal(-1, map.GetColumnIndex(2));
    }

    // ----------------------------------------------------------------------------- vortex.stats

    [Fact]
    public void StatsMetadataShorterThanFourBytesIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([0, 0, 0])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));
        AssertFormat(root, I64, segmentCount: 4, "at least 4 bytes");
    }

    [Fact]
    public void StatsWithABareZoneLengthParsesWithAnEmptyTable()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        Assert.True(tree.Root.TryGetZoneMap(out ZoneMap map));
        Assert.False(map.IsPruningAvailable);
        Assert.Equal(4, map.ZoneLength);
        Assert.Equal(2, map.ZoneCount);
        Assert.Equal(0, tree.Root.GetChild(1).DType.FieldCount);
    }

    [Fact]
    public void StatsTableAddsATruncationFlagAfterMinAndMax()
    {
        // Bits: IsConstant(0), Max(3), Min(4), NullCount(6) -> 0b0101_1001 = 0x59.
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x59])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        DType table = tree.Root.GetChild(1).DType;

        Assert.Equal(6, table.FieldCount);
        Assert.Equal("is_constant", table.GetFieldName(0));
        Assert.Equal("max", table.GetFieldName(1));
        Assert.Equal("max_is_truncated", table.GetFieldName(2));
        Assert.Equal("min", table.GetFieldName(3));
        Assert.Equal("min_is_truncated", table.GetFieldName(4));
        Assert.Equal("null_count", table.GetFieldName(5));

        // The truncation flags are NON-nullable Bool; everything else is made nullable.
        Assert.Equal(Nullability.Nullable, table.GetField(0).Nullability);
        Assert.Equal(Nullability.NonNullable, table.GetField(2).Nullability);
        Assert.Equal(Nullability.NonNullable, table.GetField(4).Nullability);
    }

    [Fact]
    public void StatsIgnoresUnknownBitsInTheBitset()
    {
        // Bit 4 (Min) plus bits 9..15, which no Stat claims.
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x10, 0xFF])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        DType table = tree.Root.GetChild(1).DType;
        Assert.Equal(2, table.FieldCount);
        Assert.Equal("min", table.GetFieldName(0));
        Assert.Equal("min_is_truncated", table.GetFieldName(1));
    }

    [Fact]
    public void ChunkedFlaggedAsHavingAStatsTableButWithNoChildrenIsRejected()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.chunked", 0).WithMetadata([1]);
        AssertFormat(root, I64, segmentCount: 1, "statistics table");
    }

    [Fact]
    public void StatsSumColumnIsTheWidenedNullableType()
    {
        // Bit 5 is Sum: an i64 column sums to i64?, and a bool column to u64?.
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x20])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        DType table = Parse(root, I64, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal(1, table.FieldCount);
        Assert.Equal("sum", table.GetFieldName(0));
        Assert.Equal(PType.I64, table.GetField(0).PType);
        Assert.Equal(Nullability.Nullable, table.GetField(0).Nullability);

        DType boolTable = Parse(root, Types.Bool(Nullability.NonNullable), segmentCount: 4)
            .Root.GetChild(1).DType;
        Assert.Equal(PType.U64, boolTable.GetField(0).PType);
    }

    [Fact]
    public void StatsSumIsSkippedForAColumnThatCannotBeSummed()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x20])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        DType table = Parse(root, Utf8, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal(0, table.FieldCount);
    }

    [Fact]
    public void StatsUncompressedSizeColumnIsU64()
    {
        // Bit 7 is UncompressedSizeInBytes.
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x80])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        DType table = Parse(root, Utf8, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal(1, table.FieldCount);
        Assert.Equal("uncompressed_size_in_bytes", table.GetFieldName(0));
        Assert.Equal(PType.U64, table.GetField(0).PType);
    }

    [Fact]
    public void StatsNanCountOnlyExistsForFloatColumns()
    {
        // Bit 8 is NaNCount, which needs a second bitset byte.
        SyntheticLayout root = new SyntheticLayout("vortex.stats", 8)
            .WithMetadata([4, 0, 0, 0, 0x00, 0x01])
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        Assert.Equal(0, Parse(root, I64, segmentCount: 4).Root.GetChild(1).DType.FieldCount);

        DType floats = Types.Primitive(PType.F64, Nullability.NonNullable);
        DType table = Parse(root, floats, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal(1, table.FieldCount);
        Assert.Equal("nan_count", table.GetFieldName(0));
    }

    [Fact]
    public void ZonedBoundedMinIsAScalarAndBoundedMaxIsAStruct()
    {
        // The asymmetry is real: bounded_min's partial is the element type, bounded_max's is
        // {bound, unknown} (vortex-array-0.86.1/src/aggregate_fn/fns/bounded_max/mod.rs).
        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadataBytes(4, "vortex.bounded_max", "vortex.bounded_min"))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        DType zones = Parse(root, Utf8, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal(2, zones.FieldCount);
        Assert.Equal("vortex.bounded_max(64)", zones.GetFieldName(0));
        Assert.Equal("vortex.bounded_min(64)", zones.GetFieldName(1));

        DType boundedMax = zones.GetField(0);
        Assert.Equal(DTypeKind.Struct, boundedMax.Kind);
        Assert.Equal(Nullability.Nullable, boundedMax.Nullability);
        Assert.Equal(2, boundedMax.FieldCount);
        Assert.Equal("bound", boundedMax.GetFieldName(0));
        Assert.Equal("unknown", boundedMax.GetFieldName(1));
        Assert.Equal(DTypeKind.Utf8, boundedMax.GetField(0).Kind);
        Assert.Equal(Nullability.Nullable, boundedMax.GetField(0).Nullability);
        Assert.Equal(DTypeKind.Bool, boundedMax.GetField(1).Kind);
        Assert.Equal(Nullability.NonNullable, boundedMax.GetField(1).Nullability);

        DType boundedMin = zones.GetField(1);
        Assert.Equal(DTypeKind.Utf8, boundedMin.Kind);
        Assert.Equal(Nullability.Nullable, boundedMin.Nullability);
    }

    [Fact]
    public void ZonedMinDisplaysItsNonDefaultNanOption()
    {
        // NumericalAggregateOpts has IMPLICIT presence: an EMPTY options payload decodes to
        // skip_nans = false, which is the non-default configuration and therefore IS displayed.
        AggregateSpecList specs = new AggregateSpecList();
        specs.Add("vortex.min"u8, default);

        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadata.Serialize(ZonedMetadata.Create(4, specs)))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        DType zones = Parse(root, I64, segmentCount: 4).Root.GetChild(1).DType;
        Assert.Equal("vortex.min(skip_nans=false)", zones.GetFieldName(0));
    }

    [Fact]
    public void ZonedBoundedAggregateWithUnusableOptionsDisablesPruning()
    {
        // BoundedMin::deserialize requires exactly eight bytes and a non-zero value; upstream fails
        // the read, we degrade to "no zone map" (never a wrong value: Phase 1 prunes with nothing).
        AggregateSpecList specs = new AggregateSpecList();
        specs.Add("vortex.bounded_min"u8, [1, 2, 3]);

        SyntheticLayout root = new SyntheticLayout("vortex.zoned", 8)
            .WithMetadata(ZonedMetadata.Serialize(ZonedMetadata.Create(4, specs)))
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(2, 1));

        LayoutTree tree = Parse(root, Utf8, segmentCount: 4);
        Assert.True(tree.Root.TryGetZoneMap(out ZoneMap map));
        Assert.False(map.IsPruningAvailable);
        Assert.Equal(1, tree.Root.ChildCount);
    }

    // ---------------------------------------------------------------------------------- limits

    [Fact]
    public void SegmentIdEqualToTheSegmentCountIsRejectedAtParse()
    {
        LayoutTree ok = Parse(SyntheticLayout.Flat(4, 3), I64, segmentCount: 4);
        Assert.Equal(3u, ok.Root.Segments[0]);

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => Parse(SyntheticLayout.Flat(4, 4), I64, segmentCount: 4));
        Assert.Contains("missing segment 4", error.Message, StringComparison.Ordinal);
        Assert.Contains("segment count: 4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SixtyFourDeepParsesAndSixtyFiveDeepIsRejected()
    {
        byte[] ok = SyntheticLayoutWriter.Chain(64, out string[] okSpecs);
        LayoutTree tree = LayoutTree.Parse(ok, I64, okSpecs, segmentCount: 1);
        Assert.Equal(64, tree.NodeCount);

        byte[] tooDeep = SyntheticLayoutWriter.Chain(65, out string[] deepSpecs);
        Assert.Throws<VortexFormatException>(() => LayoutTree.Parse(tooDeep, I64, deepSpecs, segmentCount: 1));
    }

    [Fact]
    public void SharedChildrenCannotExplodeTheTree()
    {
        // 40 levels of two-way sharing: 2^40 materialized nodes if nothing bounds the walk. The
        // buffer is a few hundred bytes, and the node budget is derived from it.
        byte[] bytes = SyntheticLayoutWriter.SharedChildren(40, out string[] specs);
        Assert.True(bytes.Length < 4096, $"The DAG fixture is {bytes.Length} bytes.");

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => LayoutTree.Parse(bytes, I64, specs, segmentCount: 1));
        Assert.Contains("shared between parents", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSharedMetadataVectorCannotBeReParsedPerNode()
    {
        // 400 flat siblings, one shared 8 KB metadata vector: the buffer is about 12 KB, but a
        // parser that copies or re-parses the metadata per node touches 3 MB - and the same shape
        // with a 1 MB vector would touch gigabytes.
        byte[] bytes = SyntheticLayoutWriter.SharedMetadata(400, 8192, out string[] specs);
        Assert.True(bytes.Length < 32 * 1024, $"The shared-metadata fixture is {bytes.Length} bytes.");

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => LayoutTree.Parse(bytes, I64, specs, segmentCount: 1));
        Assert.Contains("shared between layout nodes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModestlySharedMetadataVectorIsStillAccepted()
    {
        // The budget is four times the buffer, so honest sharing - a writer deduplicating identical
        // metadata across a handful of nodes - still parses.
        byte[] bytes = SyntheticLayoutWriter.SharedMetadata(8, 64, out string[] specs);
        LayoutTree tree = LayoutTree.Parse(bytes, I64, specs, segmentCount: 1);
        Assert.Equal(8, tree.Root.ChildCount);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(64, tree.Root.GetChild(i).Metadata.Length);
        }
    }

    [Fact]
    public void ARowCountThatDoesNotFitLongIsRejected()
    {
        SyntheticLayout root = SyntheticLayout.Flat(ulong.MaxValue, 0);
        AssertFormat(root, I64, segmentCount: 1, "does not fit");
    }

    [Fact]
    public void AnUnknownLayoutIdParsesAndStopsThere()
    {
        SyntheticLayout root = new SyntheticLayout("vortex.list", 8)
            .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(8, 1));

        LayoutTree tree = Parse(root, I64, segmentCount: 4);
        Assert.Equal(LayoutEncodingId.Unknown, tree.Root.Encoding);
        Assert.Equal("vortex.list", tree.Root.EncodingIdText);
        Assert.Equal(0, tree.Root.ChildCount);

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => LayoutReaderTable.Get(tree.Root.Encoding, tree.Root.EncodingIdText));
        Assert.Equal("vortex.list", error.ComponentId);
        Assert.Equal(VortexComponentKind.Layout, error.Kind);
        Assert.Contains("in no core edition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpecIndexOutsideTheDictionaryIsRejected()
    {
        byte[] bytes = SyntheticLayoutWriter.Write(SyntheticLayout.Flat(4, 0), out _);
        Assert.Throws<VortexFormatException>(() => LayoutTree.Parse(bytes, I64, [], segmentCount: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void TruncatedLayoutBytesAreRejected(int keep)
    {
        byte[] bytes = SyntheticLayoutWriter.Write(SyntheticLayout.Flat(4, 0), out string[] specs);
        byte[] truncated = new byte[Math.Min(keep, bytes.Length)];
        Array.Copy(bytes, truncated, truncated.Length);
        Assert.Throws<VortexFormatException>(() => LayoutTree.Parse(truncated, I64, specs, segmentCount: 1));
    }

    [Fact]
    public void EveryTruncationOfARealTreeIsRejectedOrParses()
    {
        // Not "is rejected": some prefixes are still well-formed FlatBuffers. The promise is that
        // no prefix produces anything but a LayoutTree or a VortexFormatException.
        byte[] bytes = SyntheticLayoutWriter.Write(
            new SyntheticLayout("vortex.chunked", 8).With(SyntheticLayout.Flat(4, 0), SyntheticLayout.Flat(4, 1)),
            out string[] specs);

        for (int keep = 0; keep < bytes.Length; keep++)
        {
            byte[] truncated = new byte[keep];
            Array.Copy(bytes, truncated, keep);
            try
            {
                LayoutTree.Parse(truncated, I64, specs, segmentCount: 2);
            }
            catch (VortexFormatException)
            {
            }
        }
    }

    [Fact]
    public void EveryOneByteCorruptionOfARealTreeIsRejectedOrParses()
    {
        byte[] bytes = SyntheticLayoutWriter.Write(
            new SyntheticLayout("vortex.struct", 8)
                .With(SyntheticLayout.Flat(8, 0), SyntheticLayout.Flat(8, 1)),
            out string[] specs);

        DType schema = Struct(nullable: false);
        foreach (byte flip in new byte[] { 0x01, 0x7F, 0xFF })
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte[] corrupt = (byte[])bytes.Clone();
                corrupt[i] ^= flip;
                try
                {
                    LayoutTree.Parse(corrupt, schema, specs, segmentCount: 2);
                }
                catch (VortexFormatException)
                {
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------- helpers

    private static DType Struct(bool nullable)
    {
        DTypeArena types = Types;
        List<DType> fields = new List<DType>
        {
            types.Primitive(PType.I64, Nullability.NonNullable),
            types.Utf8(Nullability.NonNullable),
        };

        return types.Struct(
            new[] { "a", "b" },
            fields.ToArray(),
            nullable ? Nullability.Nullable : Nullability.NonNullable);
    }

    private static LayoutTree Parse(SyntheticLayout root, DType schema, int segmentCount)
    {
        byte[] bytes = SyntheticLayoutWriter.Write(root, out string[] specs);
        return LayoutTree.Parse(bytes, schema, specs, segmentCount);
    }

    private static void AssertFormat(SyntheticLayout root, DType schema, int segmentCount, string expected)
    {
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => Parse(root, schema, segmentCount));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static byte[] DictMetadataBytes(PType codes, bool? nullableCodes)
    {
        DictLayoutMetadata metadata = new DictLayoutMetadata(codes, nullableCodes, null);

        // A plain local plus try/finally, not `using`: CS1657 forbids passing a `using` variable
        // as a `ref` argument.
        ProtoWriter writer = new ProtoWriter(32);
        try
        {
            DictLayoutMetadata.Write(ref writer, in metadata);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] ZonedMetadataBytes(uint zoneLength, params string[] aggregateIds)
    {
        AggregateSpecList specs = new AggregateSpecList();
        foreach (string id in aggregateIds)
        {
            // Default numerical options: skip_nans = true reaches the wire as `08 01`, and an EMPTY
            // payload decodes to skip_nans = false (proto3 implicit presence). A bounded aggregate's
            // options are NOT protobuf at all: eight raw little-endian bytes of max_bytes.
            byte[] options = id switch
            {
                "vortex.min" or "vortex.max" => [0x08, 0x01],
                "vortex.bounded_min" or "vortex.bounded_max" => [64, 0, 0, 0, 0, 0, 0, 0],
                _ => [],
            };
            specs.Add(System.Text.Encoding.UTF8.GetBytes(id), options);
        }

        return ZonedMetadata.Serialize(ZonedMetadata.Create(zoneLength, specs));
    }
}
