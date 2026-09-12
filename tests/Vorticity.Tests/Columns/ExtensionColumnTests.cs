// Phase 1 contract §12.2/§12.3 and docs/07-dotnet-mapping.md §3. The expectations here were
// computed independently (a Python date calculation), not by running the code under test, so a
// sign or epoch error cannot agree with itself.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed class ExtensionColumnTests
{
    [Fact]
    public void DateInDaysIsAnEpochOffset()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = DateColumn(f, VortexTimeUnit.Days, PType.I32, [19000, 0, -1]);

        ExtensionColumn column = batch.Root.AsExtension();
        Assert.Equal(ExtensionKind.Date, column.Kind);
        Assert.Equal(VortexTimeUnit.Days, column.TimeUnit);
        Assert.False(column.HasTimeZone);
        Assert.True(column.TimeZoneUtf8.IsEmpty);
        Assert.True(Encoding.UTF8.GetString(batch.Root.AsExtension().ExtensionIdUtf8) == "vortex.date");

        Assert.Equal(new DateOnly(2022, 1, 8), batch.Root.AsExtension().ToDateOnly(0));
        Assert.Equal(new DateOnly(1970, 1, 1), batch.Root.AsExtension().ToDateOnly(1));
        Assert.Equal(new DateOnly(1969, 12, 31), batch.Root.AsExtension().ToDateOnly(2));
    }

    [Fact]
    public void DateInMillisecondsFloorsToTheContainingDay()
    {
        using ColumnFixture f = new ColumnFixture();

        // 1641600000000 is types/date_ms row 0. -1 is the trap: truncating division would round a
        // pre-epoch instant UP to 1970-01-01.
        RecordBatch batch = DateColumn(f, VortexTimeUnit.Milliseconds, PType.I64, [1641600000000L, -1L, 0L]);

        Assert.Equal(new DateOnly(2022, 1, 8), batch.Root.AsExtension().ToDateOnly(0));
        Assert.Equal(new DateOnly(1969, 12, 31), batch.Root.AsExtension().ToDateOnly(1));
        Assert.Equal(new DateOnly(1970, 1, 1), batch.Root.AsExtension().ToDateOnly(2));
    }

    [Fact]
    public void DateOutsideDateOnlysRangeIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = DateColumn(f, VortexTimeUnit.Days, PType.I32, [int.MaxValue, int.MinValue]);

        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToDateOnly(0));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToDateOnly(1));
    }

    [Theory]
    [InlineData(VortexTimeUnit.Seconds, PType.I32, 3661L)]
    [InlineData(VortexTimeUnit.Milliseconds, PType.I32, 3661_000L)]
    [InlineData(VortexTimeUnit.Microseconds, PType.I64, 3661_000_000L)]
    [InlineData(VortexTimeUnit.Nanoseconds, PType.I64, 3661_000_000_000L)]
    public void TimeReadsEveryLegalUnitStoragePair(VortexTimeUnit unit, PType storage, long value)
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimeColumn(f, unit, storage, [0L, value]);

        Assert.Equal(unit, batch.Root.AsExtension().TimeUnit);
        Assert.Equal(new TimeOnly(0, 0, 0), batch.Root.AsExtension().ToTimeOnly(0));
        Assert.Equal(new TimeOnly(1, 1, 1), batch.Root.AsExtension().ToTimeOnly(1));
    }

    [Fact]
    public void TimeInNanosecondsTruncatesToTheTick()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimeColumn(f, VortexTimeUnit.Nanoseconds, PType.I64, [199L, 100L]);

        // 199 ns is one whole 100 ns tick plus 99 ns we cannot represent.
        Assert.Equal(1L, batch.Root.AsExtension().ToTimeOnly(0).Ticks);
        Assert.Equal(1L, batch.Root.AsExtension().ToTimeOnly(1).Ticks);
    }

    [Fact]
    public void TimeOutsideADayIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimeColumn(
            f, VortexTimeUnit.Microseconds, PType.I64, [86_400_000_000L, -1L, long.MaxValue]);

        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToTimeOnly(0));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToTimeOnly(1));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToTimeOnly(2));
    }

    // The pre-epoch half of "truncating nanoseconds toward the start of the tick". C# `/` truncates
    // toward ZERO, which for a negative value rounds toward the FUTURE: the tick containing -150 ns
    // starts at -200 ns, not -100 ns, and everything in (-100, 0) would land on the epoch itself.
    // Expected ticks are derived here by hand, not from the code under test.
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(99L, 0L)]
    [InlineData(100L, 1L)]
    [InlineData(-1L, -1L)]
    [InlineData(-99L, -1L)]
    [InlineData(-100L, -1L)]
    [InlineData(-101L, -2L)]
    [InlineData(-150L, -2L)]
    [InlineData(-200L, -2L)]
    public void APreEpochNanosecondTimestampFloorsToTheContainingTick(long nanoseconds, long expectedTicks)
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimestampColumn(f, VortexTimeUnit.Nanoseconds, null, [nanoseconds]);

        DateTime utc = batch.Root.AsExtension().ToUtcDateTime(0);
        Assert.Equal(DateTime.UnixEpoch.AddTicks(expectedTicks), utc);
    }

    // The same rounding decides whether ToTimeOnly's `ticks < 0` guard ever sees the value. -1 µs
    // is already rejected (TimeOutsideADayIsMalformed); -1 ns must be too, and used to slip through
    // as 00:00:00 because truncation lifted it to tick 0 first.
    [Theory]
    [InlineData(-1L)]
    [InlineData(-99L)]
    [InlineData(-100L)]
    public void ANegativeNanosecondTimeIsMalformed(long nanoseconds)
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimeColumn(f, VortexTimeUnit.Nanoseconds, PType.I64, [nanoseconds]);

        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToTimeOnly(0));
    }

    [Fact]
    public void NaiveTimestampConvertsToUtc()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimestampColumn(f, VortexTimeUnit.Milliseconds, null, [1700000000000L]);

        ExtensionColumn column = batch.Root.AsExtension();
        Assert.Equal(ExtensionKind.Timestamp, column.Kind);
        Assert.False(column.HasTimeZone);
        Assert.True(column.TimeZoneUtf8.IsEmpty);

        DateTime utc = batch.Root.AsExtension().ToUtcDateTime(0);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void NanosecondTimestampKeepsTheTick()
    {
        using ColumnFixture f = new ColumnFixture();

        // types/timestamp_ns_tz row 1: 1700000000001000001 ns, i.e. .001000001 s. The tick floor
        // is .0010000, and the remaining single nanosecond is unrepresentable.
        RecordBatch batch = TimestampColumn(
            f, VortexTimeUnit.Nanoseconds, "Europe/Paris", [1700000000001000001L]);

        ExtensionColumn column = batch.Root.AsExtension();
        Assert.True(column.HasTimeZone);
        Assert.True(column.TimeZoneUtf8.SequenceEqual("Europe/Paris"u8));
        Assert.Equal(VortexTimeUnit.Nanoseconds, column.TimeUnit);

        // A zoned column refuses a bare UTC DateTime: the core resolves no timezone.
        Assert.Throws<InvalidOperationException>(() => batch.Root.AsExtension().ToUtcDateTime(0));

        DateTimeOffset utc = batch.Root.AsExtension().ToDateTimeOffset(0, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc).AddTicks(10_000), utc.UtcDateTime);
    }

    [Fact]
    public void AUtcSpelledTimestampIsAcceptedByToUtcDateTime()
    {
        using ColumnFixture f = new ColumnFixture();
        foreach (string zone in new[] { "UTC", "utc", "Z", "Etc/UTC", "+00:00" })
        {
            RecordBatch batch = TimestampColumn(f, VortexTimeUnit.Seconds, zone, [0L]);
            Assert.Equal(DateTime.UnixEpoch, batch.Root.AsExtension().ToUtcDateTime(0));
        }

        RecordBatch paris = TimestampColumn(f, VortexTimeUnit.Seconds, "Europe/Paris", [0L]);
        Assert.Throws<InvalidOperationException>(() => paris.Root.AsExtension().ToUtcDateTime(0));
    }

    [Fact]
    public void ToDateTimeOffsetTakesTheCallersZone()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimestampColumn(f, VortexTimeUnit.Seconds, "Europe/Paris", [0L]);

        // A fixed custom zone, so the assertion does not depend on the machine's tz database.
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Plus2", TimeSpan.FromHours(2), "Test +2", "Test +2");

        DateTimeOffset value = batch.Root.AsExtension().ToDateTimeOffset(0, plusTwo);
        Assert.Equal(TimeSpan.FromHours(2), value.Offset);
        Assert.Equal(DateTime.UnixEpoch, value.UtcDateTime);
        Assert.Equal(new DateTime(1970, 1, 1, 2, 0, 0), value.DateTime);

        Assert.Throws<ArgumentNullException>(() => batch.Root.AsExtension().ToDateTimeOffset(0, null!));
    }

    [Fact]
    public void TimestampInDaysHasNoInstant()
    {
        using ColumnFixture f = new ColumnFixture();

        // Days passes upstream's dtype validation and fails when a value is unpacked; this is that.
        RecordBatch batch = TimestampColumn(f, VortexTimeUnit.Days, null, [1L]);
        Assert.Equal(VortexTimeUnit.Days, batch.Root.AsExtension().TimeUnit);
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToUtcDateTime(0));
        Assert.Throws<VortexFormatException>(() =>
            batch.Root.AsExtension().ToDateTimeOffset(0, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TimestampOutsideDateTimesRangeIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = TimestampColumn(
            f, VortexTimeUnit.Seconds, null, [long.MaxValue, long.MinValue, -62_135_596_801L]);

        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToUtcDateTime(0));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToUtcDateTime(1));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToUtcDateTime(2));
    }

    [Fact]
    public void UuidIsBigEndian()
    {
        using ColumnFixture f = new ColumnFixture();

        // types/uuid row 0 is the bytes 00..0f, and RFC 4122 network order makes that
        // 00010203-0405-0607-0809-0a0b0c0d0e0f. Reading it little-endian would give
        // 03020100-0504-0706-... - plausible, and wrong on every row.
        byte[] bytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            bytes[i] = (byte)i;
        }

        RecordBatch batch = UuidColumn(f, bytes, 2);

        Assert.Equal(ExtensionKind.Uuid, batch.Root.AsExtension().Kind);
        Assert.Equal(Guid.Parse("00010203-0405-0607-0809-0a0b0c0d0e0f"), batch.Root.AsExtension().ToGuid(0));
        Assert.Equal(Guid.Parse("10111213-1415-1617-1819-1a1b1c1d1e1f"), batch.Root.AsExtension().ToGuid(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Root.AsExtension().ToGuid(2));

        // The same bytes read as the raw storage, so a caller can do its own conversion.
        Assert.Equal(16u, batch.Root.AsExtension().Storage.AsFixedSizeList().Size);
        Assert.Equal(32, batch.Root.AsExtension().Storage.AsFixedSizeList().Elements.Length);
    }

    [Fact]
    public void UuidRejectsTheWrongStorageShape()
    {
        using ColumnFixture f = new ColumnFixture();

        // A 16-element FSL is required; an 8-element one is not a UUID.
        DType u8 = f.Types.Primitive(PType.U8, Nullability.NonNullable);
        int elements = f.Arena.AddPrimitive(u8, 8, Validity.NonNullable, PType.U8, f.Bytes(new byte[8]));
        DType fsl = f.Types.FixedSizeList(u8, 16, Nullability.NonNullable);
        int list = f.Arena.AddFixedSizeList(fsl, 1, Validity.NonNullable, elements, 8);
        DType ext = f.Types.Extension("vortex.uuid", fsl, default);
        RecordBatch batch = f.Batch(f.Arena.AddExtension(ext, 1, list));

        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToGuid(0));
    }

    [Fact]
    public void ANullRowHasNoValueToConvert()
    {
        using ColumnFixture f = new ColumnFixture();

        DType storage = f.Types.Primitive(PType.I32, Nullability.Nullable);
        Validity validity = f.BitmapValidity([true, false]);
        int values = f.Arena.AddPrimitive(storage, 2, validity, PType.I32, f.Int32s([19000, 0]));
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Days]);
        RecordBatch batch = f.Batch(f.Arena.AddExtension(ext, 2, values));

        Assert.True(batch.Root.AsExtension().IsValid(0));
        Assert.False(batch.Root.AsExtension().IsValid(1));
        Assert.Equal(new DateOnly(2022, 1, 8), batch.Root.AsExtension().ToDateOnly(0));
        Assert.Throws<InvalidOperationException>(() => batch.Root.AsExtension().ToDateOnly(1));
        Assert.Equal(ValidityKind.Bitmap, batch.Root.ValidityKind);
        Assert.Equal(1, batch.Root.NullCount);
    }

    [Fact]
    public void AnUnknownExtensionIdIsUnsupportedNotMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        DType storage = f.Types.Primitive(PType.I64, Nullability.NonNullable);
        int values = f.Arena.AddPrimitive(storage, 1, Validity.NonNullable, PType.I64, f.Int64s([1L]));
        DType ext = f.Types.Extension("acme.quaternion", storage, default);
        RecordBatch batch = f.Batch(f.Arena.AddExtension(ext, 1, values));

        // Resolving is free and never throws; reading through it is what fails, with kind "dtype"
        // (contract §2.3).
        Assert.Equal(ExtensionKind.Unknown, batch.Root.AsExtension().Kind);
        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(() => { _ = batch.Root.AsExtension().TimeUnit; });
        Assert.Equal(VortexComponentKind.DType, error.Kind);
        Assert.Equal("acme.quaternion", error.ComponentId);

        Assert.Throws<VortexUnsupportedException>(() => batch.Root.AsExtension().ToDateOnly(0));
        Assert.Throws<VortexUnsupportedException>(() => batch.Root.AsExtension().ToGuid(0));
        Assert.Throws<VortexUnsupportedException>(() => { _ = batch.Root.AsExtension().HasTimeZone; });
        Assert.Throws<VortexUnsupportedException>(() =>
        {
            _ = batch.Root.AsExtension().TimeZoneUtf8.Length;
        });
    }

    [Fact]
    public void TheWrongExtensionKindIsACallerError()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch date = DateColumn(f, VortexTimeUnit.Days, PType.I32, [0]);

        Assert.Throws<InvalidOperationException>(() => date.Root.AsExtension().ToTimeOnly(0));
        Assert.Throws<InvalidOperationException>(() => date.Root.AsExtension().ToUtcDateTime(0));
        Assert.Throws<InvalidOperationException>(() => date.Root.AsExtension().ToGuid(0));
        Assert.False(date.Root.AsExtension().HasTimeZone);
        Assert.True(date.Root.AsExtension().TimeZoneUtf8.IsEmpty);

        byte[] bytes = new byte[16];
        RecordBatch uuid = UuidColumn(f, bytes, 1);
        Assert.Throws<InvalidOperationException>(() => { _ = uuid.Root.AsExtension().TimeUnit; });
        Assert.Throws<InvalidOperationException>(() => uuid.Root.AsExtension().ToDateOnly(0));
    }

    [Fact]
    public void ExtensionValidityIsItsStoragesValidity()
    {
        using ColumnFixture f = new ColumnFixture();
        DType storage = f.Types.Primitive(PType.I32, Nullability.Nullable);
        int values = f.Arena.AddPrimitive(storage, 3, Validity.AllInvalid, PType.I32, f.Int32s([0, 0, 0]));
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Days]);
        RecordBatch batch = f.Batch(f.Arena.AddExtension(ext, 3, values));

        Assert.Equal(ValidityKind.AllInvalid, batch.Root.ValidityKind);
        Assert.Equal(3, batch.Root.NullCount);
        Assert.False(batch.Root.AsExtension().IsValid(0));
    }

    private static RecordBatch DateColumn(ColumnFixture f, VortexTimeUnit unit, PType ptype, int[] values)
    {
        DType storage = f.Types.Primitive(ptype, Nullability.NonNullable);
        int node = f.Arena.AddPrimitive(storage, values.Length, Validity.NonNullable, ptype, f.Int32s(values));
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)unit]);
        return f.Batch(f.Arena.AddExtension(ext, values.Length, node));
    }

    private static RecordBatch DateColumn(ColumnFixture f, VortexTimeUnit unit, PType ptype, long[] values)
    {
        DType storage = f.Types.Primitive(ptype, Nullability.NonNullable);
        int node = f.Arena.AddPrimitive(storage, values.Length, Validity.NonNullable, ptype, f.Int64s(values));
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)unit]);
        return f.Batch(f.Arena.AddExtension(ext, values.Length, node));
    }

    private static RecordBatch TimeColumn(ColumnFixture f, VortexTimeUnit unit, PType ptype, long[] values)
    {
        DType storage = f.Types.Primitive(ptype, Nullability.NonNullable);
        Vorticity.Buffers.VortexBuffer buffer = ptype == PType.I32
            ? f.Int32s(ToInt32(values))
            : f.Int64s(values);
        int node = f.Arena.AddPrimitive(storage, values.Length, Validity.NonNullable, ptype, buffer);
        DType ext = f.Types.Extension("vortex.time", storage, [(byte)unit]);
        return f.Batch(f.Arena.AddExtension(ext, values.Length, node));
    }

    private static RecordBatch TimestampColumn(
        ColumnFixture f, VortexTimeUnit unit, string? timeZone, long[] values)
    {
        DType storage = f.Types.Primitive(PType.I64, Nullability.NonNullable);
        int node = f.Arena.AddPrimitive(
            storage, values.Length, Validity.NonNullable, PType.I64, f.Int64s(values));

        // [0] unit, [1..3] u16 LE timezone length - ALWAYS written, so "no timezone" is 3 bytes.
        byte[] zone = timeZone is null ? [] : Encoding.UTF8.GetBytes(timeZone);
        byte[] metadata = new byte[3 + zone.Length];
        metadata[0] = (byte)unit;
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(1), (ushort)zone.Length);
        zone.CopyTo(metadata.AsSpan(3));

        DType ext = f.Types.Extension("vortex.timestamp", storage, metadata);
        return f.Batch(f.Arena.AddExtension(ext, values.Length, node));
    }

    private static RecordBatch UuidColumn(ColumnFixture f, byte[] bytes, int rows)
    {
        DType u8 = f.Types.Primitive(PType.U8, Nullability.NonNullable);
        int elements = f.Arena.AddPrimitive(u8, bytes.Length, Validity.NonNullable, PType.U8, f.Bytes(bytes));
        DType fsl = f.Types.FixedSizeList(u8, 16, Nullability.NonNullable);
        int list = f.Arena.AddFixedSizeList(fsl, rows, Validity.NonNullable, elements, 16);
        DType ext = f.Types.Extension("vortex.uuid", fsl, default);
        return f.Batch(f.Arena.AddExtension(ext, rows, list));
    }

    private static int[] ToInt32(long[] values)
    {
        int[] narrowed = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            narrowed[i] = unchecked((int)values[i]);
        }

        return narrowed;
    }
}
