using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity;

/// <summary>The typed accessors of <see cref="Column{T}"/>, one family per .NET type the dtypes map to.</summary>
/// <remarks>
/// A nullable column's indexer returns <c>null</c> for a null row and pays a validity check per
/// element; <c>Values</c> is the raw buffer, undefined at a null slot, to be read with
/// <see cref="Column{T}.ValidityWords"/>.
/// </remarks>
public static class ColumnExtensions
{
    extension<T>(Column<T> column)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>The values, contiguous: a span over the decoded buffer, no copy.</summary>
        public ReadOnlySpan<T> Values => ColumnData.Values<T>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>.</summary>
        public T this[int index] => ColumnData.Values<T>(column.Arena, column.Node)[index];

        /// <summary>The values, copied into a new array.</summary>
        /// <returns>The array.</returns>
        public T[] ToArray() => ColumnData.Values<T>(column.Arena, column.Node).ToArray();

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<T> destination) => ColumnData.Values<T>(column.Arena, column.Node).CopyTo(destination);
    }

    extension<T>(Column<T?> column)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>The raw values, contiguous; undefined where the validity bit is 0.</summary>
        public ReadOnlySpan<T> Values => ColumnData.Values<T>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public T? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index)
            ? ColumnData.Values<T>(column.Arena, column.Node)[index]
            : null;
    }

    extension(Column<bool> column)
    {
        /// <summary>The values as 64-bit words, bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c>.</summary>
        public ReadOnlySpan<ulong> Bits => ArenaWords.Bits(column.Arena, column.ValuesNode);

        /// <summary>The value of row <paramref name="index"/>.</summary>
        public bool this[int index] => Bit(column.Arena, column.ValuesNode, index);
    }

    extension(Column<bool?> column)
    {
        /// <summary>The raw values as 64-bit words; undefined where the validity bit is 0.</summary>
        public ReadOnlySpan<ulong> Bits => ArenaWords.Bits(column.Arena, column.ValuesNode);

        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public bool? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Bit(column.Arena, column.ValuesNode, index) : null;
    }

    extension(Column<string> column)
    {
        /// <summary>The UTF-8 bytes of row <paramref name="index"/>, borrowed; empty for a null.</summary>
        public ReadOnlySpan<byte> this[int index] => ColumnData.Bytes(column.Arena, column.Node, index);

        /// <summary>Row <paramref name="index"/> as a <see cref="string"/>, which allocates; null for a null.</summary>
        /// <param name="index">A row.</param>
        /// <returns>The text.</returns>
        public string? GetString(int index) => ColumnData.String(column.Arena, column.Node, index);

        /// <summary>The UTF-8 byte length of row <paramref name="index"/>; 0 for a null.</summary>
        /// <param name="index">A row.</param>
        /// <returns>The length.</returns>
        public int GetLength(int index) => ColumnData.ByteLength(column.Arena, column.Node, index);
    }

    extension(Column<ReadOnlyMemory<byte>> column)
    {
        /// <summary>The bytes of row <paramref name="index"/>, borrowed; empty for a null.</summary>
        public ReadOnlySpan<byte> this[int index] => ColumnData.Bytes(column.Arena, column.Node, index);
    }

    extension(Column<ReadOnlyMemory<byte>?> column)
    {
        /// <summary>The bytes of row <paramref name="index"/>, borrowed; empty for a null.</summary>
        public ReadOnlySpan<byte> this[int index] => ColumnData.Bytes(column.Arena, column.Node, index);
    }

    extension(Column<decimal> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public decimal this[int index] => ColumnData.Decimal(column.Arena, column.Node, index);

        /// <summary>The digits after the point.</summary>
        public int Scale => column.Type.Scale;

        /// <summary>The unscaled values as stored, of the storage's width: <c>sbyte</c> to <c>Int128</c>.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => StorageOf<decimal, TStorage>(column);
    }

    extension(Column<decimal?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public decimal? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Decimal(column.Arena, column.Node, index) : null;

        /// <summary>The digits after the point.</summary>
        public int Scale => column.Type.Scale;

        /// <summary>The unscaled values as stored.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => StorageOf<decimal?, TStorage>(column);
    }

    extension(Column<VortexDecimal> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public VortexDecimal this[int index] => ColumnData.Wide(column.Arena, column.Node, index);

        /// <summary>The unscaled values as stored.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => StorageOf<VortexDecimal, TStorage>(column);
    }

    extension(Column<VortexDecimal?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public VortexDecimal? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Wide(column.Arena, column.Node, index) : null;
    }

    extension(Column<DateOnly> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public DateOnly this[int index] => Temporal.Date(column.Arena, column.Node, column.Type, index);

        /// <summary>The stored values: days in an <c>int</c>, or milliseconds in a <c>long</c>.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => StorageOf<DateOnly, TStorage>(column);
    }

    extension(Column<DateOnly?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateOnly? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Date(column.Arena, column.Node, column.Type, index) : null;
    }

    extension(Column<TimeOnly> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public TimeOnly this[int index] => Temporal.Time(column.Arena, column.Node, column.Type, index);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values.</summary>
        /// <typeparam name="TStorage">The storage's .NET type: <c>int</c> for seconds and milliseconds, <c>long</c> otherwise.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => StorageOf<TimeOnly, TStorage>(column);
    }

    extension(Column<TimeOnly?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public TimeOnly? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Time(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;
    }

    extension(Column<DateTime> column)
    {
        /// <summary>The value of row <paramref name="index"/>; <see cref="DateTimeKind.Utc"/> for a UTC column, unspecified for a naive one.</summary>
        public DateTime this[int index] => Temporal.Timestamp(column.Arena, column.Node, column.Type, index);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values, since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long> Storage() => StorageOf<DateTime, long>(column);
    }

    extension(Column<DateTime?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateTime? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Timestamp(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values, since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long?> Storage() => StorageOf<DateTime?, long?>(column);
    }

    extension(Column<DateTimeOffset> column)
    {
        /// <summary>The value of row <paramref name="index"/>, in the column's zone.</summary>
        public DateTimeOffset this[int index] => Temporal.Zoned(column.Arena, column.Node, column.Type, index);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The column's zone, resolved once.</summary>
        public TimeZoneInfo TimeZone => column.Type.ZoneInfo;

        /// <summary>The stored values, UTC since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long> Storage() => StorageOf<DateTimeOffset, long>(column);
    }

    extension(Column<DateTimeOffset?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateTimeOffset? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Zoned(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The column's zone, resolved once.</summary>
        public TimeZoneInfo TimeZone => column.Type.ZoneInfo;
    }

    extension(Column<Guid> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public Guid this[int index] => ColumnData.Guid(column.Arena, column.Node, index);
    }

    extension(Column<Guid?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public Guid? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Guid(column.Arena, column.Node, index) : null;
    }

    extension<T>(Column<ReadOnlyMemory<T>> column)
    {
        /// <summary>The elements of row <paramref name="index"/>, as a range of <c>Elements</c>.</summary>
        public Range this[int index] => ColumnData.ListRange(column.Arena, column.Node, index);

        /// <summary>Every row's elements, contiguous, in row order.</summary>
        public Column<T> Elements => new Column<T>(column.Arena, ColumnData.Elements(column.Arena, column.Node), column.Type.ElementType!, column.Extensions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Bit(CanonicalArena arena, int node, int index)
    {
        CanonicalNode bits = arena.GetNode(node);
        ColumnData.CheckRow(index, bits.Length);
        int bit = bits.BitOffset + index;
        return ((bits.Bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
    }

    internal static T ExtensionValue<T>(Column<T> column, int index)
        where T : IVortexExtension<T>
    {
        CanonicalNode storage = column.Arena.GetNode(column.ValuesNode);
        ColumnData.CheckRow(index, storage.Length);
        int width = storage.Kind == CanonicalKind.Primitive ? storage.PType.ByteWidth() : Types.Numerics.DecimalStorage.ByteWidth(storage.Storage);
        return T.FromStorage(storage.Values.Span.Slice(index * width, width), column.Type.ExtensionMetadata.Span);
    }

    internal static Column<TStorage> StorageOf<T, TStorage>(Column<T> column)
    {
        CanonicalNode storage = column.Arena.GetNode(column.ValuesNode);
        int width = storage.Kind switch
        {
            CanonicalKind.Primitive => storage.PType.ByteWidth(),
            CanonicalKind.Decimal => Types.Numerics.DecimalStorage.ByteWidth(storage.Storage),
            _ => -1,
        };

        if (width < 0 || WidthOf<TStorage>() != width)
        {
            throw new VortexSchemaException($"The column is stored {width} bytes wide; {typeof(TStorage)} is not its storage type.");
        }

        VortexType type = storage.Kind == CanonicalKind.Primitive ? VortexType.Primitive(storage.PType) : column.Type;
        return new Column<TStorage>(column.Arena, column.ValuesNode, VortexType.WithNullability(type, column.Type.IsNullable), column.Extensions);
    }

    private static int WidthOf<TStorage>()
    {
        ClrShape shape = ClrShape.For<TStorage>.Value;
        if (shape.Kind is ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float)
        {
            return shape.PType.ByteWidth();
        }

        Type core = Nullable.GetUnderlyingType(typeof(TStorage)) ?? typeof(TStorage);
        return core == typeof(Int128) || core == typeof(UInt128) ? 16 : Unsafe.SizeOf<TStorage>();
    }
}

/// <summary>The accessors of a column of a registered extension type.</summary>
public static class ExtensionColumnExtensions
{
    extension<T>(Column<T> column)
        where T : IVortexExtension<T>
    {
        /// <summary>The value of row <paramref name="index"/>, read from its storage by the registered type.</summary>
        public T this[int index] => ColumnExtensions.ExtensionValue<T>(column, index);

        /// <summary>The stored values.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> Storage<TStorage>()
            where TStorage : unmanaged => ColumnExtensions.StorageOf<T, TStorage>(column);
    }
}

/// <summary>The temporal columns' values, from their storage and unit.</summary>
internal static class Temporal
{
    internal static DateOnly Date(CanonicalArena arena, int node, VortexType type, int index)
    {
        long stored = ColumnData.Int64(arena, node, index);
        long days = type.Unit == TimeUnit.Milliseconds ? Math.DivRem(stored, 86_400_000L, out long rem) - (rem < 0 ? 1 : 0) : stored;
        return DateOnly.FromDayNumber((int)(TemporalUnits.UnixEpochDayNumber + days));
    }

    internal static TimeOnly Time(CanonicalArena arena, int node, VortexType type, int index) =>
        new TimeOnly(TemporalUnits.ToTicks(ColumnData.Int64(arena, node, index), type.Unit ?? TimeUnit.Microseconds));

    internal static DateTime Timestamp(CanonicalArena arena, int node, VortexType type, int index) =>
        new DateTime(
            TemporalUnits.UnixEpochTicks + TemporalUnits.ToTicks(ColumnData.Int64(arena, node, index), type.Unit ?? TimeUnit.Microseconds),
            type.TimeZone is null ? DateTimeKind.Unspecified : DateTimeKind.Utc);

    internal static DateTimeOffset Zoned(CanonicalArena arena, int node, VortexType type, int index)
    {
        DateTimeOffset utc = new DateTimeOffset(
            TemporalUnits.UnixEpochTicks + TemporalUnits.ToTicks(ColumnData.Int64(arena, node, index), type.Unit ?? TimeUnit.Microseconds),
            TimeSpan.Zero);
        return TimeZoneInfo.ConvertTime(utc, type.ZoneInfo);
    }
}
