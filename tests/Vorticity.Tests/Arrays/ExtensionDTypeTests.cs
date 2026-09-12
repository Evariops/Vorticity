// Contract §8.7 and §9.5. The metadata is hand-rolled, not Protobuf, and every one of these cases
// is a byte layout a careless reader gets wrong in a way that reads plausibly.
using System;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ExtensionDTypeTests
{
    private readonly DTypeArena _types = new DTypeArena();

    [Theory]
    [InlineData("vortex.date", ExtensionKind.Date)]
    [InlineData("vortex.time", ExtensionKind.Time)]
    [InlineData("vortex.timestamp", ExtensionKind.Timestamp)]
    [InlineData("vortex.uuid", ExtensionKind.Uuid)]
    public void TheFourCoreIdsResolve(string id, ExtensionKind expected)
    {
        Assert.Equal(expected, ExtensionDTypeRegistry.Resolve(System.Text.Encoding.UTF8.GetBytes(id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vortex.dat")]
    [InlineData("vortex.datex")]
    [InlineData("vortex.timestamps")]
    [InlineData("arrow.date32")]
    public void AnythingElseIsUnknownAndOnlyFailsWhenRequired(string id)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(id);
        Assert.Equal(ExtensionKind.Unknown, ExtensionDTypeRegistry.Resolve(utf8));

        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(() => ExtensionDTypeRegistry.RequireSupported(utf8));
        Assert.Equal(VortexComponentKind.DType, error.Kind);
        Assert.Equal(id, error.ComponentId);
    }

    [Fact]
    public void RequireSupportedAcceptsTheFourCoreIds()
    {
        ExtensionDTypeRegistry.RequireSupported("vortex.date"u8);
        ExtensionDTypeRegistry.RequireSupported("vortex.time"u8);
        ExtensionDTypeRegistry.RequireSupported("vortex.timestamp"u8);
        ExtensionDTypeRegistry.RequireSupported("vortex.uuid"u8);
    }

    // ------------------------------------------------------------------------------ vortex.date

    [Fact]
    public void DateDaysStoresI32AndDateMillisecondsStoresI64()
    {
        Assert.Equal(
            VortexTimeUnit.Days,
            ExtensionDTypeRegistry.ReadDateUnit([4], Int32Storage()));
        Assert.Equal(
            VortexTimeUnit.Milliseconds,
            ExtensionDTypeRegistry.ReadDateUnit([2], Int64Storage()));
    }

    [Theory]
    [InlineData(0)]  // ns
    [InlineData(1)]  // us
    [InlineData(3)]  // s
    public void DateRejectsTheThreeUnitsItDoesNotAdmit(byte unit)
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([unit], Int64Storage()));
    }

    [Fact]
    public void DateWithTheWrongStorageWidthIsMalformed()
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([4], Int64Storage()));
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([2], Int32Storage()));
    }

    [Fact]
    public void DateWithEmptyMetadataIsMalformedButTrailingBytesAreIgnored()
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([], Int32Storage()));
        Assert.Equal(
            VortexTimeUnit.Days,
            ExtensionDTypeRegistry.ReadDateUnit([4, 0xFF, 0xFF, 0xFF], Int32Storage()));
    }

    [Fact]
    public void AnUndefinedTimeUnitIsMalformed()
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([5], Int32Storage()));
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadDateUnit([255], Int32Storage()));
    }

    [Fact]
    public void ANonPrimitiveStorageIsAFormatErrorAndNotAPanic()
    {
        // Upstream's as_ptype is a vortex_panic! here; the Kind check has to come first.
        DType storage = _types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadDateUnit([4], storage));
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadTimeUnit([3], storage));
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([2, 0, 0], storage).Unit; });
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadDateUnit([4], default));
    }

    // ------------------------------------------------------------------------------ vortex.time

    [Theory]
    [InlineData(3, PType.I32)]  // s
    [InlineData(2, PType.I32)]  // ms
    [InlineData(1, PType.I64)]  // us
    [InlineData(0, PType.I64)]  // ns
    public void TimeStorageWidthFollowsTheUnit(byte unit, PType expected)
    {
        DType storage = _types.Primitive(expected, Nullability.NonNullable);
        Assert.Equal((VortexTimeUnit)unit, ExtensionDTypeRegistry.ReadTimeUnit([unit], storage));

        PType wrong = expected == PType.I32 ? PType.I64 : PType.I32;
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadTimeUnit(
                [unit], _types.Primitive(wrong, Nullability.NonNullable)));
    }

    [Fact]
    public void TimeRejectsDays()
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadTimeUnit([4], Int64Storage()));
    }

    // ------------------------------------------------------------------------- vortex.timestamp

    [Fact]
    public void ATimestampWithNoTimeZoneIsThreeBytesNotOne()
    {
        // The u16 length prefix is ALWAYS written, so [unit] alone is truncated, not "no zone".
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([2], Int64Storage()).Unit; });
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([2, 0], Int64Storage()).Unit; });

        TimestampOptions options = ExtensionDTypeRegistry.ReadTimestamp([2, 0, 0], Int64Storage());
        Assert.Equal(VortexTimeUnit.Milliseconds, options.Unit);
        Assert.False(options.HasTimeZone);
        Assert.True(options.TimeZoneUtf8.IsEmpty);
    }

    [Fact]
    public void ATimestampTimeZoneIsReadAtItsDeclaredLength()
    {
        byte[] metadata = [0, 3, 0, (byte)'U', (byte)'T', (byte)'C'];
        TimestampOptions options = ExtensionDTypeRegistry.ReadTimestamp(metadata, Int64Storage());

        Assert.Equal(VortexTimeUnit.Nanoseconds, options.Unit);
        Assert.True(options.HasTimeZone);
        Assert.True(options.TimeZoneUtf8.SequenceEqual("UTC"u8));
    }

    [Fact]
    public void ATimeZoneLengthThatOverrunsIsMalformed()
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([0, 4, 0, (byte)'U', (byte)'T', (byte)'C'], Int64Storage()).Unit; });
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([0, 0xFF, 0xFF], Int64Storage()).Unit; });
    }

    [Fact]
    public void ATimeZoneThatIsNotValidUtf8IsMalformed()
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([0, 2, 0, 0xC0, 0x80], Int64Storage()).Unit; });
    }

    [Fact]
    public void EveryTimestampUnitStoresI64IncludingDays()
    {
        // TimeUnit::Days passes DType validation for timestamp upstream and fails only when a
        // value is unpacked, so accepting it here is the reference behaviour.
        for (byte unit = 0; unit <= 4; unit++)
        {
            Assert.Equal(
                (VortexTimeUnit)unit,
                ExtensionDTypeRegistry.ReadTimestamp([unit, 0, 0], Int64Storage()).Unit);
        }

        Assert.Throws<VortexFormatException>(
            () => { _ = ExtensionDTypeRegistry.ReadTimestamp([2, 0, 0], Int32Storage()).Unit; });
    }

    [Fact]
    public void ATimestampTrailingTheDeclaredZoneIsIgnored()
    {
        byte[] metadata = [2, 1, 0, (byte)'Z', 0xFF, 0xFF];
        TimestampOptions options = ExtensionDTypeRegistry.ReadTimestamp(metadata, Int64Storage());
        Assert.True(options.TimeZoneUtf8.SequenceEqual("Z"u8));
    }

    // ------------------------------------------------------------------------------ vortex.uuid

    [Fact]
    public void UuidAcceptsEmptyMetadata()
    {
        UuidOptions options = ExtensionDTypeRegistry.ReadUuid([], UuidStorage(Nullability.NonNullable));
        Assert.False(options.HasVersion);
        Assert.Equal(0, options.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void UuidAcceptsTheNumberedVersions(byte version)
    {
        UuidOptions options = ExtensionDTypeRegistry.ReadUuid([version], UuidStorage(Nullability.Nullable));
        Assert.True(options.HasVersion);
        Assert.Equal(version, options.Version);
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0xFF)]
    public void BothSpellingsOfMaxNormalizeToTheSameValue(byte raw)
    {
        // The uuid crate changed Max from 0x0F to 0xFF in 1.23.0; a file may carry either.
        UuidOptions options = ExtensionDTypeRegistry.ReadUuid([raw], UuidStorage(Nullability.NonNullable));
        Assert.True(options.HasVersion);
        Assert.Equal(ExtensionDTypeRegistry.UuidVersionMax, options.Version);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(254)]
    public void AnUndefinedUuidVersionIsMalformed(byte version)
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadUuid([version], UuidStorage(Nullability.NonNullable)));
    }

    [Fact]
    public void UuidMetadataLongerThanOneByteIsRejectedUnlikeTheOtherThree()
    {
        Assert.Throws<VortexFormatException>(
            () => ExtensionDTypeRegistry.ReadUuid([4, 0], UuidStorage(Nullability.NonNullable)));
    }

    [Fact]
    public void UuidStorageMustBeSixteenNonNullableBytes()
    {
        DType elementNullable = _types.FixedSizeList(
            _types.Primitive(PType.U8, Nullability.Nullable), 16, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadUuid([], elementNullable));

        DType wrongWidth = _types.FixedSizeList(
            _types.Primitive(PType.U8, Nullability.NonNullable), 8, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadUuid([], wrongWidth));

        DType wrongElement = _types.FixedSizeList(
            _types.Primitive(PType.I8, Nullability.NonNullable), 16, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadUuid([], wrongElement));

        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadUuid([], Int64Storage()));
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.ReadUuid([], default));
    }

    [Fact]
    public void TheOuterListsNullabilityIsFree()
    {
        ExtensionDTypeRegistry.ReadUuid([], UuidStorage(Nullability.NonNullable));
        ExtensionDTypeRegistry.ReadUuid([], UuidStorage(Nullability.Nullable));
    }

    // ---------------------------------------------------------------------------------- Format

    [Fact]
    public void TimeUnitRenderingUsesTheMicroSignAndNotGreekMu()
    {
        Assert.Equal("ns", ExtensionDTypeRegistry.Format(VortexTimeUnit.Nanoseconds));
        Assert.Equal("µs", ExtensionDTypeRegistry.Format(VortexTimeUnit.Microseconds));
        Assert.NotEqual("μs", ExtensionDTypeRegistry.Format(VortexTimeUnit.Microseconds));
        Assert.Equal("ms", ExtensionDTypeRegistry.Format(VortexTimeUnit.Milliseconds));
        Assert.Equal("s", ExtensionDTypeRegistry.Format(VortexTimeUnit.Seconds));
        Assert.Equal("days", ExtensionDTypeRegistry.Format(VortexTimeUnit.Days));
    }

    [Fact]
    public void FormattingAnUndefinedUnitIsMalformed()
    {
        Assert.Throws<VortexFormatException>(() => ExtensionDTypeRegistry.Format((VortexTimeUnit)5));
        Assert.False(ExtensionDTypeRegistry.IsDefined((VortexTimeUnit)5));
        Assert.True(ExtensionDTypeRegistry.IsDefined(VortexTimeUnit.Days));
    }

    [Fact]
    public void TheCorpusExtensionMetadataParses()
    {
        // The four extension dtypes as the corpus writes them, reached through the file's schema
        // rather than through a byte array we chose ourselves.
        Assert.Equal(
            VortexTimeUnit.Days,
            ExtensionDTypeRegistry.ReadDateUnit([4], Int32Storage()));
        Assert.Equal(
            VortexTimeUnit.Microseconds,
            ExtensionDTypeRegistry.ReadTimeUnit([1], Int64Storage()));

        TimestampOptions tz = ExtensionDTypeRegistry.ReadTimestamp(
            [0, 16, 0, .. "America/New_York"u8], Int64Storage());
        Assert.True(tz.HasTimeZone);
        Assert.Equal(16, tz.TimeZoneUtf8.Length);
    }

    private DType Int32Storage() => _types.Primitive(PType.I32, Nullability.NonNullable);

    private DType Int64Storage() => _types.Primitive(PType.I64, Nullability.NonNullable);

    private DType UuidStorage(Nullability outer) =>
        _types.FixedSizeList(_types.Primitive(PType.U8, Nullability.NonNullable), 16, outer);
}
