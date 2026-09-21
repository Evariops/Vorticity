using System;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>
/// An extension column: one of the four core logical types wrapped around a storage column.
/// </summary>
/// <remarks>
/// A timestamp's timezone identifier is never resolved here: resolving one needs the operating
/// system's timezone database, which would make the result machine-dependent and break under
/// invariant globalization. The column surfaces the raw storage value and the parsed metadata, and
/// every conversion is explicit -- <see cref="ToUtcDateTime"/> refuses a zoned column and
/// <see cref="ToDateTimeOffset"/> makes the caller supply the zone.
/// <see cref="Storage"/> and the spans here borrow from the owning <see cref="RecordBatch"/> and
/// its dtype arena, and are invalid once that batch is disposed.
/// </remarks>
internal readonly ref struct ExtensionColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal ExtensionColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>
    /// Whether row <paramref name="index"/> is not null. An extension has no validity of its own;
    /// it is exactly its storage column's.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>The extension id exactly as the dtype carries it, e.g. <c>vortex.timestamp</c>.</summary>
    public ReadOnlySpan<byte> ExtensionIdUtf8 => _batch.Node(_node).DType.ExtensionIdUtf8;

    /// <summary>
    /// Which of the four core extensions this is, or <see cref="ExtensionKind.Unknown"/>. Resolving
    /// is not an error; reading a value through one of the conversions below is what throws.
    /// </summary>
    public ExtensionKind Kind => ExtensionDTypeRegistry.Resolve(ExtensionIdUtf8);

    /// <summary>The underlying storage column: the integer, or the 16-byte list for a UUID.</summary>
    public VortexColumn Storage => new VortexColumn(_batch, _batch.Node(_node).StorageIndex);

    /// <summary>
    /// The time unit, for <see cref="ExtensionKind.Date"/>, <see cref="ExtensionKind.Time"/> and
    /// <see cref="ExtensionKind.Timestamp"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The extension has no time unit.</exception>
    /// <exception cref="VortexUnsupportedException">The extension id is not one we implement.</exception>
    /// <exception cref="VortexFormatException">The extension metadata is malformed.</exception>
    public VortexTimeUnit TimeUnit
    {
        get
        {
            DType dtype = RequireSupportedDType();
            ReadOnlySpan<byte> metadata = dtype.ExtensionMetadata;
            DType storage = dtype.StorageType;
            switch (ExtensionDTypeRegistry.Resolve(dtype.ExtensionIdUtf8))
            {
                case ExtensionKind.Date:
                    return ExtensionDTypeRegistry.ReadDateUnit(metadata, storage);
                case ExtensionKind.Time:
                    return ExtensionDTypeRegistry.ReadTimeUnit(metadata, storage);
                case ExtensionKind.Timestamp:
                    return ExtensionDTypeRegistry.ReadTimestamp(metadata, storage).Unit;
                default:
                    return ColumnsThrow.WrongKind<VortexTimeUnit>(
                        "a vortex.uuid column", "a date, time or timestamp column");
            }
        }
    }

    /// <summary>
    /// <see langword="true"/> when this is a <see cref="ExtensionKind.Timestamp"/> carrying a
    /// timezone identifier. Always <see langword="false"/> for the other three.
    /// </summary>
    /// <exception cref="VortexUnsupportedException">The extension id is not one we implement.</exception>
    /// <exception cref="VortexFormatException">The extension metadata is malformed.</exception>
    public bool HasTimeZone
    {
        get
        {
            DType dtype = RequireSupportedDType();
            return ExtensionDTypeRegistry.Resolve(dtype.ExtensionIdUtf8) == ExtensionKind.Timestamp &&
                   ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, dtype.StorageType).HasTimeZone;
        }
    }

    /// <summary>
    /// The raw, <b>unresolved</b> timezone identifier of a <see cref="ExtensionKind.Timestamp"/>
    /// column - typically an IANA name such as <c>Europe/Paris</c>. Empty when there is none or the
    /// extension is not a timestamp. It is handed over unresolved.
    /// </summary>
    /// <exception cref="VortexUnsupportedException">The extension id is not one we implement.</exception>
    /// <exception cref="VortexFormatException">The extension metadata is malformed.</exception>
    public ReadOnlySpan<byte> TimeZoneUtf8
    {
        get
        {
            DType dtype = RequireSupportedDType();
            if (ExtensionDTypeRegistry.Resolve(dtype.ExtensionIdUtf8) != ExtensionKind.Timestamp)
            {
                return default;
            }

            return ExtensionDTypeRegistry
                .ReadTimestamp(dtype.ExtensionMetadata, dtype.StorageType)
                .TimeZoneUtf8;
        }
    }

    /// <summary>Row <paramref name="index"/> of a <c>vortex.date</c> column as a
    /// <see cref="DateOnly"/>.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <remarks>
    /// A <c>ms</c>-unit date stores an instant; the date returned is the UTC day containing it,
    /// which is how upstream defines the unit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The column is not a date, or row <paramref name="index"/> is null.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// The stored value falls outside <see cref="DateOnly"/>'s range.
    /// </exception>
    public DateOnly ToDateOnly(int index)
    {
        DType dtype = RequireKind(ExtensionKind.Date, "a date column");
        VortexTimeUnit unit = ExtensionDTypeRegistry.ReadDateUnit(dtype.ExtensionMetadata, dtype.StorageType);
        long raw = ReadStorageInteger(index);

        long days = unit == VortexTimeUnit.Days
            ? raw
            : TemporalConvert.FloorDiv(raw, TemporalConvert.MillisecondsPerDay);

        long dayNumber = days + TemporalConvert.UnixEpochDayNumber;
        if (dayNumber < 0 || dayNumber > TemporalConvert.MaxDayNumber)
        {
            ColumnsThrow.Format(
                $"Date row {index} is {days} days from the Unix epoch, which is outside " +
                "DateOnly's range.");
        }

        return DateOnly.FromDayNumber((int)dayNumber);
    }

    /// <summary>Row <paramref name="index"/> of a <c>vortex.time</c> column as a
    /// <see cref="TimeOnly"/>.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <remarks>
    /// <see cref="TimeOnly"/> has 100 ns resolution, so a <c>ns</c>-unit column is truncated toward
    /// the start of the tick. Read <see cref="Storage"/> directly when the last two digits matter.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The column is not a time, or row <paramref name="index"/> is null.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// The stored value is not a time of day in <c>[0, 24h)</c>.
    /// </exception>
    public TimeOnly ToTimeOnly(int index)
    {
        DType dtype = RequireKind(ExtensionKind.Time, "a time column");
        VortexTimeUnit unit = ExtensionDTypeRegistry.ReadTimeUnit(dtype.ExtensionMetadata, dtype.StorageType);
        long raw = ReadStorageInteger(index);

        if (!TemporalConvert.TryToTicks(raw, unit, out long ticks) ||
            ticks < 0 || ticks >= TimeSpan.TicksPerDay)
        {
            return ColumnsThrow.Format<TimeOnly>(
                $"Time row {index} is {raw} {ExtensionDTypeRegistry.Format(unit)}, which is not " +
                "a time of day in [0, 24h).");
        }

        return new TimeOnly(ticks);
    }

    /// <summary>
    /// Row <paramref name="index"/> of a <c>vortex.timestamp</c> column as a UTC
    /// <see cref="DateTime"/>. Valid only when the column is naive (no timezone) or UTC; a zoned
    /// column must go through <see cref="ToDateTimeOffset"/>, which makes the caller supply the
    /// zone.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <remarks>
    /// A naive timestamp is returned as if it were UTC; <see cref="DateTime.Kind"/> is always
    /// <see cref="DateTimeKind.Utc"/>. Nanosecond columns truncate to the 100 ns tick.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The column is not a timestamp, it carries a timezone other than UTC, or row
    /// <paramref name="index"/> is null.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// The unit is <c>days</c>, or the instant falls outside <see cref="DateTime"/>'s range.
    /// </exception>
    public DateTime ToUtcDateTime(int index)
    {
        DType dtype = RequireKind(ExtensionKind.Timestamp, "a timestamp column");
        TimestampOptions options =
            ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, dtype.StorageType);

        if (options.HasTimeZone && !TemporalConvert.IsUtcZone(options.TimeZoneUtf8))
        {
            ColumnsThrow.NotConvertible(
                "This timestamp column carries a timezone other than UTC, so a UTC DateTime would " +
                "be a guess. Use ToDateTimeOffset(index, timeZone), which makes the zone explicit.");
        }

        return Instant(index, options.Unit);
    }

    /// <summary>
    /// Row <paramref name="index"/> of a <c>vortex.timestamp</c> column, rendered in
    /// <paramref name="timeZone"/>. The stored value is treated as an instant in UTC.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <param name="timeZone">The zone to render in. The caller supplies it because resolving the
    /// column's own identifier would make the result machine-dependent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeZone"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The column is not a timestamp, or row <paramref name="index"/> is null.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// The unit is <c>days</c>, or the instant falls outside <see cref="DateTime"/>'s range.
    /// </exception>
    public DateTimeOffset ToDateTimeOffset(int index, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        DType dtype = RequireKind(ExtensionKind.Timestamp, "a timestamp column");
        TimestampOptions options =
            ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, dtype.StorageType);

        DateTimeOffset utc = new DateTimeOffset(Instant(index, options.Unit));
        return TimeZoneInfo.ConvertTime(utc, timeZone);
    }

    /// <summary>
    /// Row <paramref name="index"/> of a <c>vortex.uuid</c> column as a <see cref="Guid"/>. The
    /// storage bytes are RFC 4122 network order while <see cref="Guid"/>'s binary constructor reads
    /// its first three fields little-endian, so the conversion asks for the big-endian reading.
    /// Skipping the swap yields a plausible but wrong value on every row.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The column is not a uuid, or row <paramref name="index"/> is null.
    /// </exception>
    /// <exception cref="VortexFormatException">The storage is not 16 bytes of <c>u8</c> per row.</exception>
    public Guid ToGuid(int index)
    {
        RequireKind(ExtensionKind.Uuid, "a uuid column");

        CanonicalNode extension = _batch.Node(_node);
        ColumnCore.CheckRow(index, extension.Length);
        RequireNotNull(index);

        CanonicalNode list = _batch.Node(extension.StorageIndex);
        if (list.Kind != CanonicalKind.FixedSizeList || list.FixedSize != TemporalConvert.UuidByteCount)
        {
            ColumnsThrow.Format(
                $"vortex.uuid storage must be a fixed-size list of {TemporalConvert.UuidByteCount} " +
                $"elements; this one is {list.Kind}.");
        }

        CanonicalNode bytes = _batch.Node(list.ElementsIndex);
        if (bytes.Kind != CanonicalKind.Primitive || bytes.PType != PType.U8)
        {
            ColumnsThrow.Format("vortex.uuid storage elements must be u8.");
        }

        ReadOnlySpan<byte> all = bytes.Values.Span;
        long start = (long)index * TemporalConvert.UuidByteCount;
        if (start + TemporalConvert.UuidByteCount > all.Length)
        {
            ColumnsThrow.Format(
                $"vortex.uuid row {index} needs bytes [{start}, " +
                $"{start + TemporalConvert.UuidByteCount}) of a {all.Length}-byte buffer.");
        }

        return new Guid(all.Slice((int)start, TemporalConvert.UuidByteCount), bigEndian: true);
    }

    private DateTime Instant(int index, VortexTimeUnit unit)
    {
        long raw = ReadStorageInteger(index);
        if (unit == VortexTimeUnit.Days)
        {
            // A `days` unit passes dtype validation but has no instant, so the failure can only
            // surface here, when a value is unpacked.
            ColumnsThrow.Format("A vortex.timestamp with unit `days` has no instant representation.");
        }

        if (!TemporalConvert.TryToTicks(raw, unit, out long ticks))
        {
            ColumnsThrow.Format(
                $"Timestamp row {index} is {raw} {ExtensionDTypeRegistry.Format(unit)} from the " +
                "Unix epoch, which overflows a tick count.");
        }

        Int128 total = (Int128)TemporalConvert.UnixEpochTicks + ticks;
        if (total < 0 || total > DateTime.MaxValue.Ticks)
        {
            ColumnsThrow.Format(
                $"Timestamp row {index} is {raw} {ExtensionDTypeRegistry.Format(unit)} from the " +
                "Unix epoch, which is outside DateTime's range.");
        }

        return new DateTime((long)total, DateTimeKind.Utc);
    }

    private DType RequireSupportedDType()
    {
        DType dtype = _batch.Node(_node).DType;
        if (dtype.IsDefault || dtype.Kind != DTypeKind.Extension)
        {
            ColumnsThrow.Format("An extension column's dtype is not an extension dtype.");
        }

        // The single place an unsupported extension dtype is rejected, and only once the field is
        // actually read: a file may carry extensions this library does not implement in columns
        // nobody touches.
        ExtensionDTypeRegistry.RequireSupported(dtype.ExtensionIdUtf8);
        return dtype;
    }

    private DType RequireKind(ExtensionKind expected, string expectedText)
    {
        DType dtype = RequireSupportedDType();
        ExtensionKind actual = ExtensionDTypeRegistry.Resolve(dtype.ExtensionIdUtf8);
        if (actual != expected)
        {
            ColumnsThrow.WrongKind($"a {actual} column", expectedText);
        }

        return dtype;
    }

    private void RequireNotNull(int index)
    {
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            ColumnsThrow.NotConvertible(
                $"Row {index} is null and has no value to convert; check IsValid({index}) first.");
        }
    }

    private long ReadStorageInteger(int index)
    {
        CanonicalNode extension = _batch.Node(_node);
        ColumnCore.CheckRow(index, extension.Length);
        RequireNotNull(index);

        CanonicalNode storage = _batch.Node(extension.StorageIndex);
        if (storage.Kind != CanonicalKind.Primitive)
        {
            ColumnsThrow.Format(
                $"A temporal extension's storage must be primitive; this one is {storage.Kind}.");
        }

        if (index >= storage.Length)
        {
            ColumnsThrow.Format(
                $"An extension of {extension.Length} rows wraps a storage of {storage.Length}.");
        }

        return ColumnCore.ReadInteger(
            storage.Values.Span, storage.PType, index, "An extension storage value");
    }
}

/// <summary>
/// The unit arithmetic behind <see cref="ExtensionColumn"/>'s conversions. Separate so the
/// overflow rules are in one readable place and testable through the column.
/// </summary>
internal static class TemporalConvert
{
    /// <summary>Bytes in a UUID.</summary>
    internal const int UuidByteCount = 16;

    /// <summary>Milliseconds in a day; the <c>vortex.date</c> <c>ms</c> unit divides by it.</summary>
    internal const long MillisecondsPerDay = 86_400_000L;

    /// <summary><c>DateOnly.MaxValue.DayNumber</c>, hoisted so the range check is a compare.</summary>
    internal const int MaxDayNumber = 3_652_058;

    /// <summary><c>new DateOnly(1970, 1, 1).DayNumber</c>.</summary>
    internal const int UnixEpochDayNumber = 719_162;

    /// <summary><c>DateTime.UnixEpoch.Ticks</c>.</summary>
    internal const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary>Floor division; <c>/</c> truncates toward zero and would round a pre-1970 instant
    /// up to the next day.</summary>
    internal static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Converts <paramref name="value"/> of <paramref name="unit"/> into 100 ns ticks, truncating
    /// nanoseconds toward the start of the tick and refusing an overflow rather than wrapping.
    /// </summary>
    internal static bool TryToTicks(long value, VortexTimeUnit unit, out long ticks)
    {
        switch (unit)
        {
            case VortexTimeUnit.Nanoseconds:
                // FloorDiv, not `/`: this is the only lossy unit, and `/` truncates toward zero,
                // which for a pre-epoch instant rounds toward the future. Without the floor the
                // last sub-tick before the epoch collapses onto the epoch itself, widening the
                // zero bucket and letting a negative time of day slip past a `ticks < 0` guard.
                ticks = FloorDiv(value, 100);
                return true;
            case VortexTimeUnit.Microseconds:
                return TryScale(value, TimeSpan.TicksPerMicrosecond, out ticks);
            case VortexTimeUnit.Milliseconds:
                return TryScale(value, TimeSpan.TicksPerMillisecond, out ticks);
            case VortexTimeUnit.Seconds:
                return TryScale(value, TimeSpan.TicksPerSecond, out ticks);
            default:
                ticks = 0;
                return false;
        }
    }

    /// <summary>
    /// The timezone spellings <see cref="ExtensionColumn.ToUtcDateTime"/> accepts as "this is
    /// already UTC". Anything else - <c>Europe/Paris</c>, but also <c>Etc/GMT+1</c> - must go
    /// through <see cref="ExtensionColumn.ToDateTimeOffset"/>.
    /// </summary>
    internal static bool IsUtcZone(ReadOnlySpan<byte> zoneUtf8)
    {
        if (zoneUtf8.IsEmpty)
        {
            return true;
        }

        return EqualsAsciiIgnoreCase(zoneUtf8, "UTC"u8) ||
               EqualsAsciiIgnoreCase(zoneUtf8, "Z"u8) ||
               EqualsAsciiIgnoreCase(zoneUtf8, "Etc/UTC"u8) ||
               zoneUtf8.SequenceEqual("+00:00"u8) ||
               zoneUtf8.SequenceEqual("-00:00"u8);
    }

    private static bool TryScale(long value, long multiplier, out long ticks)
    {
        Int128 scaled = (Int128)value * multiplier;
        if (scaled < long.MinValue || scaled > long.MaxValue)
        {
            ticks = 0;
            return false;
        }

        ticks = (long)scaled;
        return true;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (ToLowerAscii(a[i]) != ToLowerAscii(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value) =>
        (byte)(value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value);
}
