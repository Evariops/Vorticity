// File-level statistics: the shape rule - one entry per top-level field of a struct root, one
// for any other root - and the per-statistic DType rule that upstream has a regression test for
// because it was once wrong.
//
// Most golden files carry statistics, so the shape rule is exercised on real bytes by
// VortexFileCorpusTests; what is left for here is the values, the widened sum DType, the tri-state
// distinction between absent and present-and-zero, and the two malformed shapes.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileStatisticsTests
{
    private static async Task<VortexFile> Open(byte[] bytes) =>
        await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None);

    [Fact]
    public async Task AStructRootGetsOneShallowEntryPerTopLevelField()
    {
        // types/user_metadata_segments has 33 top-level fields, two of which are themselves
        // structs. Shallow means 33 entries, not 33 plus the nested fields.
        CorpusEntry entry = CorpusManifest.Find("types/user_metadata_segments");
        await using VortexFile file = await Open(CorpusManifest.Bytes(entry.Id));

        Assert.True(file.HasFileStatistics);
        Assert.Equal(33, file.DType.FieldCount);
        Assert.Equal(33, file.FileStatistics.FieldCount);

        for (int i = 0; i < file.FileStatistics.FieldCount; i++)
        {
            Assert.Equal(file.DType.GetField(i), file.FileStatistics.GetFieldDType(i));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => file.FileStatistics.GetField(33));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.FileStatistics.GetFieldDType(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.FileStatistics.GetSumDType(33));
    }

    [Fact]
    public async Task ANonStructRootGetsExactlyOneEntryTypedAgainstTheWholeFileDType()
    {
        CorpusEntry entry = CorpusManifest.Find("distributions/constant_i64_r1024");
        await using VortexFile file = await Open(CorpusManifest.Bytes(entry.Id));

        Assert.False(file.IsTabular);
        Assert.Equal(1, file.FileStatistics.FieldCount);
        Assert.Equal(file.DType, file.FileStatistics.GetFieldDType(0));

        // A constant i64 column: min and max are recorded and exact, and the sum widens to i64?.
        FieldStatistics stats = file.FileStatistics.GetField(0);
        Assert.True(stats.HasMin);
        Assert.True(stats.HasMax);
        Assert.Equal(StatPrecision.Exact, stats.MinPrecision);
        Assert.Equal(StatPrecision.Exact, stats.MaxPrecision);
        Assert.Equal(stats.Min, stats.Max);

        DType sumDType = file.FileStatistics.GetSumDType(0);
        Assert.Equal(DTypeKind.Primitive, sumDType.Kind);
        Assert.Equal(PType.I64, sumDType.PType);
        Assert.Equal(Nullability.Nullable, sumDType.Nullability);
    }

    [Fact]
    public async Task TheSumDTypeWidensTheFieldDTypeRatherThanMatchingIt()
    {
        // The Rust reference's sum aggregate widens unsigned to u64?, signed to i64?, floats to
        // f64?, bool to u64?, decimal to precision + 10 (capped at 76), same scale.
        CorpusEntry entry = CorpusManifest.Find("types/user_metadata_segments");
        await using VortexFile file = await Open(CorpusManifest.Bytes(entry.Id));

        for (int i = 0; i < file.FileStatistics.FieldCount; i++)
        {
            DType field = file.FileStatistics.GetFieldDType(i);
            DType sum = file.FileStatistics.GetSumDType(i);

            switch (field.Kind)
            {
                case DTypeKind.Bool:
                    Assert.Equal(PType.U64, sum.PType);
                    Assert.Equal(Nullability.Nullable, sum.Nullability);
                    break;

                case DTypeKind.Primitive:
                    PType expected = field.PType.IsUnsignedInteger() ? PType.U64
                        : field.PType.IsSignedInteger() ? PType.I64
                        : PType.F64;
                    Assert.Equal(expected, sum.PType);
                    Assert.Equal(Nullability.Nullable, sum.Nullability);
                    break;

                case DTypeKind.Decimal:
                    Assert.Equal(DTypeKind.Decimal, sum.Kind);
                    Assert.Equal((byte)Math.Min(76, field.Precision + 10), sum.Precision);
                    Assert.Equal(field.Scale, sum.Scale);
                    Assert.Equal(Nullability.Nullable, sum.Nullability);
                    break;

                default:
                    // Everything else has no summable dtype, so no sum statistic can be recorded.
                    Assert.True(sum.IsDefault);
                    Assert.False(file.FileStatistics.GetField(i).HasSum);
                    break;
            }
        }
    }

    [Fact]
    public async Task ANullColumnRecordsNoMinOrMaxBecauseTheStatisticHasNoDType()
    {
        // Stat::dtype returns None for min/max on DType::Null, so upstream skips them entirely.
        CorpusEntry entry = CorpusManifest.Find("types/null_nullable_r1024");
        await using VortexFile file = await Open(CorpusManifest.Bytes(entry.Id));

        Assert.Equal(DTypeKind.Null, file.DType.Kind);
        Assert.True(file.HasFileStatistics);
        FieldStatistics stats = file.FileStatistics.GetField(0);
        Assert.False(stats.HasMin);
        Assert.False(stats.HasMax);
        Assert.False(stats.HasSum);
        Assert.True(file.FileStatistics.GetSumDType(0).IsDefault);
    }

    [Fact]
    public async Task AnInexactMinIsSurfacedAsABoundAndNotAsAValue()
    {
        // distributions/huge_string_r16 truncates its min to 64 bytes and flags it inexact. The
        // reader must report the flag faithfully; comparing the bound for equality against the
        // real minimum fails by design (corpus manifest caveat 1).
        CorpusEntry entry = CorpusManifest.Find("distributions/huge_string_r16");
        await using VortexFile file = await Open(CorpusManifest.Bytes(entry.Id));

        bool sawInexact = false;
        for (int i = 0; i < file.FileStatistics.FieldCount; i++)
        {
            FieldStatistics stats = file.FileStatistics.GetField(i);
            if (stats.HasMin && stats.MinPrecision == StatPrecision.Inexact)
            {
                sawInexact = true;
            }
        }

        Assert.True(sawInexact, "huge_string_r16 must carry at least one inexact bound");
    }

    [Fact]
    public async Task AbsentAndPresentZeroTriStatesAreDistinguished()
    {
        DTypeArena arena = new DTypeArena();
        DType schema = arena.Primitive(PType.I64, Nullability.NonNullable);

        ArrayStatsValues present = default;
        present.NullCount = 0;
        present.IsSorted = false;
        present.NanCount = 0;

        byte[] withStats = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = schema,
            IncludeStatistics = true,
            Statistics = [present],
        });

        await using (VortexFile file = await Open(withStats))
        {
            FieldStatistics stats = file.FileStatistics.GetField(0);
            Assert.True(stats.TryGetStoredNullCount(out ulong nulls));
            Assert.Equal(0ul, nulls);
            Assert.True(stats.TryGetIsSorted(out bool sorted));
            Assert.False(sorted);
            Assert.True(stats.TryGetNanCount(out ulong nans));
            Assert.Equal(0ul, nans);
            Assert.False(stats.TryGetIsConstant(out _));
            Assert.False(stats.TryGetIsStrictSorted(out _));
            Assert.False(stats.TryGetUncompressedSizeInBytes(out _));
            Assert.False(stats.HasMin);
        }

        byte[] withoutStats = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = schema,
            IncludeStatistics = true,
            Statistics = [default],
        });

        await using (VortexFile file = await Open(withoutStats))
        {
            FieldStatistics stats = file.FileStatistics.GetField(0);
            Assert.False(stats.TryGetStoredNullCount(out _));
            Assert.False(stats.TryGetIsSorted(out _));
            Assert.False(stats.TryGetNanCount(out _));
        }
    }

    [Fact]
    public async Task TooFewOrTooManyFieldStatsEntriesAreRejected()
    {
        DTypeArena arena = new DTypeArena();
        DType schema = SyntheticVortexFile.SmallSchema(arena);
        Assert.Equal(2, schema.FieldCount);

        foreach (int count in new[] { 0, 1, 3 })
        {
            byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
            {
                Schema = schema,
                IncludeStatistics = true,
                Statistics = new ArrayStatsValues[count],
            });

            VortexFormatException error =
                await Assert.ThrowsAsync<VortexFormatException>(async () => await Open(bytes));
            Assert.Contains("field entries", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ANonStructRootWithTwoFieldStatsEntriesIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = arena.Primitive(PType.I32, Nullability.NonNullable),
            IncludeStatistics = true,
            Statistics = new ArrayStatsValues[2],
        });

        VortexFormatException error =
            await Assert.ThrowsAsync<VortexFormatException>(async () => await Open(bytes));
        Assert.Contains("non-struct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptedPrecisionValueIsRejected()
    {
        // "Corrupted min_precision field" upstream: a value outside {Exact, Inexact} is malformed,
        // not an unknown-but-forward-compatible one.
        DTypeArena arena = new DTypeArena();
        DType schema = arena.Primitive(PType.I32, Nullability.NonNullable);

        ArrayStatsValues corrupt = default;

        // A minimal ScalarValue: field 3 (int64_value, zigzag), value 0.
        corrupt.Min = [0x18, 0x00];
        corrupt.MinPrecision = (StatPrecision)2;

        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = schema,
            IncludeStatistics = true,
            Statistics = [corrupt],
        });

        VortexFormatException error =
            await Assert.ThrowsAsync<VortexFormatException>(async () => await Open(bytes));
        Assert.Contains("Corrupted min_precision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZeroFieldStructRootAcceptsAnEmptyStatisticsVector()
    {
        DTypeArena arena = new DTypeArena();
        DType schema = arena.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.NonNullable);

        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = schema,
            IncludeStatistics = true,
            Statistics = [],
        });

        await using VortexFile file = await Open(bytes);
        Assert.True(file.HasFileStatistics);
        Assert.Equal(0, file.FileStatistics.FieldCount);
    }

    [Fact]
    public async Task StatisticsAreAbsentWhenTheSegmentIsAbsent()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
        });

        await using VortexFile file = await Open(bytes);
        Assert.False(file.HasFileStatistics);
        Assert.Throws<InvalidOperationException>(() => file.FileStatistics);
    }
}
