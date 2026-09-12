// Real files, written by Vortex 0.86.1, read through the public column API and compared against
// their sidecars. Agreeing with our own fixtures proves nothing; this is the part that does not.
//
// The set is every corpus entry whose layout tree uses only the three encodings the test walker
// understands (vortex.flat, vortex.struct, vortex.zoned), whose array encodings this build decodes,
// and whose root dtype is in Phase 1 scope. dict and chunked layouts belong to §11 and are read there.
using System;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed class CorpusColumnTests
{
    public static TheoryData<string> WalkableEntries
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string entry in CorpusColumns.Entries())
            {
                if (SidecarValues.IsWalkable(entry))
                {
                    data.Add(entry);
                }
            }

            return data;
        }
    }

    [Fact]
    public void TheCorpusSweepIsNotEmpty()
    {
        // A helper that silently matched nothing would turn this whole file into a no-op.
        Assert.True(WalkableEntries.Count > 400, $"only {WalkableEntries.Count} entries matched");
    }

    [Theory]
    [MemberData(nameof(WalkableEntries))]
    public async Task EveryValueMatchesItsSidecar(string entry)
    {
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync(entry);

        Assert.Equal(SidecarValues.RowCount(entry), corpus.Batch.RowCount);
        Assert.Equal(SidecarValues.RootKind(entry) == "struct", corpus.Batch.IsTabular);
        Assert.Equal(corpus.Schema.Kind == DTypeKind.Struct, corpus.Batch.IsTabular);

        int compared = SidecarValues.AssertRows(corpus.Batch, entry);
        Assert.Equal(corpus.Batch.RowCount, compared);
    }

    [Fact]
    public async Task ANonStructRootIsExposedAsItself()
    {
        // docs/07-dotnet-mapping.md §5: FieldCount == 1, a null field name, Root is the column.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/non_struct_root_i64");

        Assert.False(corpus.Batch.IsTabular);
        Assert.Equal(1, corpus.Batch.FieldCount);
        Assert.Null(corpus.Batch.GetFieldName(0));
        Assert.Equal(1025, corpus.Batch.RowCount);
        Assert.Equal("i64", corpus.Schema.ToString());

        ReadOnlySpan<long> values = corpus.Batch.Root.AsPrimitive<long>().Values;
        Assert.Equal(1025, values.Length);
        Assert.Equal(1025, corpus.Batch.Column(0).AsPrimitive<long>().Values.Length);
    }

    [Fact]
    public async Task StructFieldNamesAreAddressableByIndex()
    {
        // types/struct_field_names carries a field named "" and one named "a.b" on purpose; the
        // sidecar's dotted paths are ambiguous for it by construction.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/struct_field_names");
        RecordBatch batch = corpus.Batch;

        Assert.True(batch.IsTabular);
        Assert.Equal(12, batch.FieldCount);
        Assert.Equal(string.Empty, batch.GetFieldName(0));
        Assert.Equal("a.b", batch.GetFieldName(9));
        Assert.Equal("A", batch.GetFieldName(10));
        Assert.Equal("a", batch.GetFieldName(11));

        // Case matters, and so does the dot: "a.b" is one field, not a path to b inside a.
        Assert.True(batch.TryGetFieldIndex("a.b"u8, out int dotted));
        Assert.Equal(9, dotted);
        Assert.True(batch.TryGetFieldIndex("A"u8, out int upper));
        Assert.Equal(10, upper);
        Assert.True(batch.TryGetFieldIndex("a"u8, out int lower));
        Assert.Equal(11, lower);
        Assert.NotEqual(upper, lower);

        Assert.True(batch.TryGetFieldIndex("élan"u8, out int accented));
        Assert.Equal("élan", batch.GetFieldName(accented));
        Assert.True(batch.TryGetFieldIndex("\U0001F600"u8, out int emoji));
        Assert.Equal("😀", batch.GetFieldName(emoji));

        for (int i = 0; i < batch.FieldCount; i++)
        {
            Assert.Equal(1025, batch.Column(i).Length);
            Assert.Equal(PType.I32, batch.Column(i).DType.PType);
        }
    }

    [Fact]
    public async Task NestedStructsNavigateByIndexAndByName()
    {
        // types/struct_nested_deep is shaped {a: i32, b: {c: utf8?, d: {e: bool, f: list(i64)}}?}.
        // Only its 1-row file avoids the dict layout and the compressed encodings §11 owns, so it
        // is the deepest REAL nesting reachable from this component; the 1024-row synthetic
        // equivalent is in RecordBatchTests.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/struct_nested_deep_nonnull_r1");
        RecordBatch batch = corpus.Batch;

        Assert.True(batch.IsTabular);
        Assert.Equal(2, batch.FieldCount);
        Assert.Equal("a", batch.GetFieldName(0));
        Assert.Equal("b", batch.GetFieldName(1));

        StructColumn b = batch.Column(1).AsStruct();
        Assert.Equal(2, b.FieldCount);
        Assert.Equal("c", b.GetFieldName(0));
        Assert.Equal("d", b.GetFieldName(1));

        StructColumn d = batch.Column(1).AsStruct().GetField(1).AsStruct();
        Assert.Equal(2, d.FieldCount);
        Assert.Equal("e", d.GetFieldName(0));
        Assert.Equal("f", d.GetFieldName(1));

        VortexColumn byIndex = batch.Column(1).AsStruct().GetField(1).AsStruct().GetField(0);
        VortexColumn byName =
            batch.Column("b"u8).AsStruct().GetField("d"u8).AsStruct().GetField("e"u8);
        Assert.Equal(byIndex.Length, byName.Length);
        Assert.Equal(byIndex.DType, byName.DType);
        Assert.Equal(DTypeKind.Bool, byIndex.DType.Kind);

        // The list leaf, reached by name through two struct levels.
        ListColumn list = batch.Column("b"u8).AsStruct().GetField("d"u8).AsStruct().GetField("f"u8).AsList();
        Assert.Equal(1, list.Length);
        Assert.Equal(PType.I64, list.Elements.DType.PType);
    }

    [Fact]
    public async Task UuidBytesConvertToTheRfc4122Guid()
    {
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/uuid_nonnull_r1024");
        RecordBatch batch = corpus.Batch;

        ExtensionColumn column = batch.Root.AsExtension();
        Assert.Equal(ExtensionKind.Uuid, column.Kind);
        Assert.Equal("vortex.uuid", Encoding.UTF8.GetString(batch.Root.AsExtension().ExtensionIdUtf8));

        for (int row = 0; row < batch.RowCount; row++)
        {
            // Rebuild the expected Guid from the raw storage bytes, big-endian, so a swapped
            // conversion cannot agree with itself.
            batch.Root.AsExtension().Storage.AsFixedSizeList().GetRange(row, out int start, out int count);
            Assert.Equal(16, count);
            ReadOnlySpan<byte> bytes = batch.Root.AsExtension().Storage.AsFixedSizeList()
                .Elements.AsPrimitive<byte>().Values.Slice(start, count);
            Assert.Equal(new Guid(bytes, bigEndian: true), batch.Root.AsExtension().ToGuid(row));
        }

        // Row 0 of the corpus is the bytes 00..0f.
        Assert.Equal(Guid.Parse("00010203-0405-0607-0809-0a0b0c0d0e0f"), batch.Root.AsExtension().ToGuid(0));
    }

    [Fact]
    public async Task TemporalColumnsExposeTheirUnitAndConvert()
    {
        await using CorpusColumns days = await CorpusColumns.LoadAsync("types/date_days_nonnull_r1024");
        Assert.Equal(VortexTimeUnit.Days, days.Batch.Root.AsExtension().TimeUnit);
        Assert.Equal(new DateOnly(2022, 1, 8), days.Batch.Root.AsExtension().ToDateOnly(0));

        await using CorpusColumns ms = await CorpusColumns.LoadAsync("types/date_ms_nonnull_r1024");
        Assert.Equal(VortexTimeUnit.Milliseconds, ms.Batch.Root.AsExtension().TimeUnit);
        Assert.Equal(new DateOnly(2022, 1, 8), ms.Batch.Root.AsExtension().ToDateOnly(0));

        // The two files carry the same logical dates in different units.
        for (int row = 0; row < 1024; row++)
        {
            Assert.Equal(
                days.Batch.Root.AsExtension().ToDateOnly(row),
                ms.Batch.Root.AsExtension().ToDateOnly(row));
        }

        await using CorpusColumns time = await CorpusColumns.LoadAsync("types/time_us_nonnull_r1024");
        Assert.Equal(VortexTimeUnit.Microseconds, time.Batch.Root.AsExtension().TimeUnit);
        Assert.Equal(new TimeOnly(0), time.Batch.Root.AsExtension().ToTimeOnly(0));
        Assert.Equal(new TimeOnly(70L), time.Batch.Root.AsExtension().ToTimeOnly(1));
    }

    [Fact]
    public async Task AZonedTimestampRefusesABareUtcConversion()
    {
        await using CorpusColumns naive = await CorpusColumns.LoadAsync("types/timestamp_ms_nonnull_r1024");
        Assert.False(naive.Batch.Root.AsExtension().HasTimeZone);
        Assert.Equal(
            new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc),
            naive.Batch.Root.AsExtension().ToUtcDateTime(0));

        await using CorpusColumns zoned = await CorpusColumns.LoadAsync("types/timestamp_ns_tz_nonnull_r1024");
        ExtensionColumn column = zoned.Batch.Root.AsExtension();
        Assert.True(column.HasTimeZone);
        Assert.Equal(VortexTimeUnit.Nanoseconds, column.TimeUnit);
        Assert.Equal("Europe/Paris", Encoding.UTF8.GetString(zoned.Batch.Root.AsExtension().TimeZoneUtf8));

        Assert.Throws<InvalidOperationException>(() => zoned.Batch.Root.AsExtension().ToUtcDateTime(0));
        Assert.Equal(
            new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc),
            zoned.Batch.Root.AsExtension().ToDateTimeOffset(0, TimeZoneInfo.Utc).UtcDateTime);
    }

    [Fact]
    public async Task NullableColumnsReportTheSidecarsNullCounts()
    {
        // The sidecar's null_counts line is the independent count; NullCount must agree with it.
        foreach (string entry in new[]
                 {
                     "types/i32_nullable_r1025",
                     "types/i32_nullable_r8193",
                     "types/bool_nullable_r1023",
                     "types/binary_nullable_r1025",
                     "types/fsl_i32_3_nullable_r1024",
                     "types/list_i32_nullable_r1024",
                     "types/null_nullable_r1024",
                 })
        {
            await using CorpusColumns corpus = await CorpusColumns.LoadAsync(entry);
            int expected = SidecarValues.RootNullCount(entry);
            Assert.Equal(expected, corpus.Batch.Root.NullCount);

            int counted = 0;
            for (int row = 0; row < corpus.Batch.RowCount; row++)
            {
                if (!corpus.Batch.Root.IsValid(row))
                {
                    counted++;
                }
            }

            Assert.Equal(expected, counted);
        }
    }

    [Fact]
    public async Task DecimalsCarryTheFullPrecision76Range()
    {
        // decimal(40,10) is i256-backed: precision 39-76 is exactly why VortexDecimal exists and
        // why System.Decimal is not the mapping (docs/07-dotnet-mapping.md §2).
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/decimal40_10_nonnull_r1024");
        DecimalColumn column = corpus.Batch.Root.AsDecimal();

        Assert.Equal(Vorticity.Types.Numerics.DecimalStorageType.I256, column.Storage);
        Assert.Equal((byte)40, column.Precision);
        Assert.Equal((sbyte)10, column.Scale);
        Assert.Equal(corpus.Batch.RowCount * 32, column.StorageBytes.Length);

        Assert.Equal(
            "-9999999999999999999999999999999999999999",
            corpus.Batch.Root.AsDecimal()[1].Unscaled.ToString());
        Assert.Equal(
            "-999999999999999999999999999999.9999999999",
            corpus.Batch.Root.AsDecimal()[1].ToString());

        // A value that legally exists in the format and cannot be a System.Decimal.
        Assert.False(corpus.Batch.Root.AsDecimal()[1].TryToDecimal(out _));
    }

    [Fact]
    public async Task DisposingACorpusBatchReleasesItsSegments()
    {
        CorpusColumns corpus = await CorpusColumns.LoadAsync("types/i32_nonnull_r1024");
        try
        {
            Assert.Equal(1024, corpus.Batch.RowCount);
            corpus.Batch.Dispose();
            Assert.Throws<ObjectDisposedException>(() => { _ = corpus.Batch.Column(0).Length; });
        }
        finally
        {
            await corpus.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("encodings/bool_bit_offset3", 3)]
    [InlineData("encodings/bool_bit_offset7", 7)]
    [InlineData("encodings/bool_bit_offset3_r1023", 3)]
    [InlineData("encodings/bool_bit_offset7_r1023", 7)]
    public async Task ARealBoolColumnCarriesItsBitOffset(string entry, int expectedOffset)
    {
        // vortex.bool's BoolMetadata.offset is the only thing that distinguishes one bool array
        // from another, and these fixtures exist to carry a non-zero one. Assert the offset really
        // is non-zero before trusting the values: a reader that ignored it would pass a
        // zero-offset file and fail here.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync(entry);
        BoolColumn column = corpus.Batch.Root.AsBool();
        Assert.Equal(expectedOffset, column.BitOffset);
        Assert.Equal(
            (expectedOffset + corpus.Batch.RowCount + 7) / 8,
            corpus.Batch.Root.AsBool().Bits.Length);

        // Values still have to match the sidecar with the offset applied.
        Assert.Equal(corpus.Batch.RowCount, SidecarValues.AssertRows(corpus.Batch, entry));
    }

    [Fact]
    public async Task AStraddlingBoolBitmapReadsCorrectly()
    {
        // The straddle fixture puts the offset where the last row lands in a byte the naive
        // (offset + length + 7) / 8 arithmetic would drop.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("encodings/bool_bit_offset_straddle");
        Assert.True(corpus.Batch.Root.AsBool().BitOffset > 0);
        Assert.Equal(corpus.Batch.RowCount, SidecarValues.AssertRows(corpus.Batch, "encodings/bool_bit_offset_straddle"));
    }

    [Fact]
    public async Task AListOfNullableElementsKeepsTheElementValidity()
    {
        // types/list_utf8_nullable_elems: the LIST rows and the ELEMENT rows have independent
        // validity, and only the r1 file avoids the compressed encodings §11 owns.
        await using CorpusColumns corpus = await CorpusColumns.LoadAsync("types/list_utf8_nullable_elems_nullable_r1");
        ListColumn list = corpus.Batch.Root.AsList();

        Assert.Equal(1, list.Length);
        Assert.Equal(DTypeKind.Utf8, list.Elements.DType.Kind);
        Assert.True(list.Elements.DType.IsNullable);
        Assert.True(corpus.Batch.Root.AsList().Elements.AsBinary().IsUtf8);

        // The sweep already compared the values; this asserts the shape the contract names.
        Assert.Equal(1, SidecarValues.AssertRows(corpus.Batch, "types/list_utf8_nullable_elems_nullable_r1"));
    }

    [Fact]
    public async Task AMaterializedAllZeroValidityChildCollapsesToAllInvalid()
    {
        // containers/all_null_i64_explicit_validity is one of only two corpus files that carry a
        // materialized all-zero validity child rather than a folded vortex.constant node. Contract
        // §2.6 rule 3 requires it to reach the caller as AllInvalid so a per-row check can be
        // skipped - the whole reason ValidityKind is public (docs/07-dotnet-mapping.md §1).
        await using CorpusColumns corpus =
            await CorpusColumns.LoadAsync("containers/all_null_i64_explicit_validity_r1025");
        RecordBatch batch = corpus.Batch;

        Assert.True(batch.TryGetFieldIndex("all_null"u8, out int index));
        VortexColumn column = batch.Column(index);
        Assert.Equal(1025, column.Length);
        Assert.Equal(ValidityKind.AllInvalid, column.ValidityKind);
        Assert.False(column.IsAllValid);
        Assert.Equal(1025, column.NullCount);
        Assert.False(batch.Column(index).IsValid(0));
        Assert.False(batch.Column(index).IsValid(1024));
    }

    [Fact]
    public async Task ANonNullableColumnReportsNonNullableNotAllValid()
    {
        // The distinction is visible to callers and the two are not interchangeable: NonNullable
        // means the dtype forbids nulls, AllValid means it allows them and there happen to be none.
        await using CorpusColumns nonNullable = await CorpusColumns.LoadAsync("types/i32_nonnull_r1024");
        Assert.Equal(ValidityKind.NonNullable, nonNullable.Batch.Root.ValidityKind);
        Assert.True(nonNullable.Batch.Root.IsAllValid);
        Assert.Equal(0, nonNullable.Batch.Root.NullCount);
        Assert.False(nonNullable.Schema.IsNullable);

        await using CorpusColumns nullable = await CorpusColumns.LoadAsync("types/i32_nullable_r1024");
        Assert.True(nullable.Schema.IsNullable);
        Assert.NotEqual(ValidityKind.NonNullable, nullable.Batch.Root.ValidityKind);
    }

    [Fact]
    public void EveryReadableEntryHasASidecar()
    {
        foreach (string entry in CorpusColumns.Entries())
        {
            Assert.True(
                System.IO.File.Exists(CorpusColumns.PathOf(entry, ".jsonl")),
                entry + " has no sidecar");
        }
    }
}
