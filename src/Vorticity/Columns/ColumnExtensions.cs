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

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<T?> destination) => ColumnData.CopyNullable(column.Arena, column.Node, destination);
    }

    extension(Column<bool> column)
    {
        /// <summary>The values as 64-bit words, bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c>.</summary>
        public ReadOnlySpan<ulong> Bits => ArenaWords.Bits(column.Arena, column.ValuesNode);

        /// <summary>The value of row <paramref name="index"/>.</summary>
        public bool this[int index] => Bit(column.Arena, column.ValuesNode, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<bool> destination) => ColumnData.CopyBits(column.Arena, column.ValuesNode, destination);
    }

    extension(Column<bool?> column)
    {
        /// <summary>The raw values as 64-bit words; undefined where the validity bit is 0.</summary>
        public ReadOnlySpan<ulong> Bits => ArenaWords.Bits(column.Arena, column.ValuesNode);

        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public bool? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Bit(column.Arena, column.ValuesNode, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<bool?> destination) => ColumnData.CopyBits(column.Arena, column.Node, column.ValuesNode, destination);
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

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<decimal> destination) => ColumnData.CopyDecimals(column.Arena, column.Node, destination);

        /// <summary>The digits after the point.</summary>
        public int Scale => column.Type.Scale;

        /// <summary>The unscaled values as stored, of the storage's width: <c>sbyte</c> to <c>Int128</c>.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<decimal, TStorage>(column);
    }

    extension(Column<decimal?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public decimal? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Decimal(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<decimal?> destination) => ColumnData.CopyDecimals(column.Arena, column.Node, destination);

        /// <summary>The digits after the point.</summary>
        public int Scale => column.Type.Scale;

        /// <summary>The unscaled values as stored.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<decimal?, TStorage>(column);
    }

    extension(Column<VortexDecimal> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public VortexDecimal this[int index] => ColumnData.Wide(column.Arena, column.Node, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<VortexDecimal> destination) => ColumnData.CopyWide(column.Arena, column.Node, destination);

        /// <summary>The unscaled values as stored.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<VortexDecimal, TStorage>(column);
    }

    extension(Column<VortexDecimal?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public VortexDecimal? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Wide(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<VortexDecimal?> destination) => ColumnData.CopyWide(column.Arena, column.Node, destination);
    }

    extension(Column<DateOnly> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public DateOnly this[int index] => Temporal.Date(column.Arena, column.Node, column.Type, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateOnly> destination) => Temporal.Copy<DateOnly, Temporal.Dates>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The stored values: days in an <c>int</c>, or milliseconds in a <c>long</c>.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<DateOnly, TStorage>(column);
    }

    extension(Column<DateOnly?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateOnly? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Date(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateOnly?> destination) => Temporal.Copy<DateOnly, Temporal.Dates>(column.Arena, column.Node, column.Type, destination);
    }

    extension(Column<TimeOnly> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public TimeOnly this[int index] => Temporal.Time(column.Arena, column.Node, column.Type, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<TimeOnly> destination) => Temporal.Copy<TimeOnly, Temporal.Times>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values.</summary>
        /// <typeparam name="TStorage">The storage's .NET type: <c>int</c> for seconds and milliseconds, <c>long</c> otherwise.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<TimeOnly, TStorage>(column);
    }

    extension(Column<TimeOnly?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public TimeOnly? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Time(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<TimeOnly?> destination) => Temporal.Copy<TimeOnly, Temporal.Times>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;
    }

    extension(Column<DateTime> column)
    {
        /// <summary>The value of row <paramref name="index"/>; <see cref="DateTimeKind.Utc"/> for a UTC column, unspecified for a naive one.</summary>
        public DateTime this[int index] => Temporal.Timestamp(column.Arena, column.Node, column.Type, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateTime> destination) => Temporal.Copy<DateTime, Temporal.Timestamps>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values, since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long> AsStorage() => StorageOf<DateTime, long>(column);
    }

    extension(Column<DateTime?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateTime? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Timestamp(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateTime?> destination) => Temporal.Copy<DateTime, Temporal.Timestamps>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The stored values, since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long?> AsStorage() => StorageOf<DateTime?, long?>(column);
    }

    extension(Column<DateTimeOffset> column)
    {
        /// <summary>The value of row <paramref name="index"/>, in the column's zone.</summary>
        public DateTimeOffset this[int index] => Temporal.Zoned(column.Arena, column.Node, column.Type, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateTimeOffset> destination) => Temporal.Copy<DateTimeOffset, Temporal.Zoneds>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The column's zone, resolved once.</summary>
        public TimeZoneInfo TimeZone => column.Type.ZoneInfo;

        /// <summary>The stored values, UTC since the Unix epoch in <c>Unit</c>.</summary>
        /// <returns>The storage column.</returns>
        public Column<long> AsStorage() => StorageOf<DateTimeOffset, long>(column);
    }

    extension(Column<DateTimeOffset?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public DateTimeOffset? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? Temporal.Zoned(column.Arena, column.Node, column.Type, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<DateTimeOffset?> destination) => Temporal.Copy<DateTimeOffset, Temporal.Zoneds>(column.Arena, column.Node, column.Type, destination);

        /// <summary>The unit the values are stored in.</summary>
        public TimeUnit Unit => column.Type.Unit ?? TimeUnit.Microseconds;

        /// <summary>The column's zone, resolved once.</summary>
        public TimeZoneInfo TimeZone => column.Type.ZoneInfo;
    }

    extension(Column<Guid> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public Guid this[int index] => ColumnData.Guid(column.Arena, column.Node, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<Guid> destination) => ColumnData.CopyGuids(column.Arena, column.Node, destination);
    }

    extension(Column<Guid?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public Guid? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Guid(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<Guid?> destination) => ColumnData.CopyGuids(column.Arena, column.Node, destination);
    }

    extension(Column<Int128> column)
    {
        /// <summary>The unscaled values as stored, contiguous and with no copy: a column stored in 128 bits only.</summary>
        /// <exception cref="InvalidOperationException">The column is stored in another width; <c>CopyTo</c> converts it.</exception>
        public ReadOnlySpan<Int128> Values => ColumnData.Values128<Int128>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>.</summary>
        public Int128 this[int index] => ColumnData.Integer128(column.Arena, column.Node, index);

        /// <summary>Copies the values into <paramref name="destination"/>, from whatever width the column is stored in.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<Int128> destination) => ColumnData.CopyIntegers128(column.Arena, column.Node, destination);

        /// <summary>The unscaled values as stored, of the storage's width: <c>sbyte</c> to <c>Int128</c>, or 32 bytes.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<Int128, TStorage>(column);
    }

    extension(Column<Int128?> column)
    {
        /// <summary>The raw values as stored in 128 bits; undefined where the validity bit is 0.</summary>
        /// <exception cref="InvalidOperationException">The column is stored in another width; <c>CopyTo</c> converts it.</exception>
        public ReadOnlySpan<Int128> Values => ColumnData.Values128<Int128>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public Int128? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Integer128(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<Int128?> destination) => ColumnData.CopyIntegers128(column.Arena, column.Node, destination);
    }

    extension(Column<UInt128> column)
    {
        /// <summary>The unscaled values as stored, contiguous and with no copy: a column stored in 128 bits only, read as unsigned.</summary>
        /// <exception cref="InvalidOperationException">The column is stored in another width; <c>CopyTo</c> converts it.</exception>
        public ReadOnlySpan<UInt128> Values => ColumnData.Values128<UInt128>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>; a negative value is refused.</summary>
        public UInt128 this[int index] => ColumnData.UInteger128(column.Arena, column.Node, index);

        /// <summary>Copies the values into <paramref name="destination"/>, from whatever width the column is stored in.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<UInt128> destination) => ColumnData.CopyUIntegers128(column.Arena, column.Node, destination);
    }

    extension(Column<UInt128?> column)
    {
        /// <summary>The raw values as stored in 128 bits; undefined where the validity bit is 0.</summary>
        /// <exception cref="InvalidOperationException">The column is stored in another width; <c>CopyTo</c> converts it.</exception>
        public ReadOnlySpan<UInt128> Values => ColumnData.Values128<UInt128>(column.Arena, column.Node);

        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public UInt128? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.UInteger128(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<UInt128?> destination) => ColumnData.CopyUIntegers128(column.Arena, column.Node, destination);
    }

    extension(Column<BigInteger> column)
    {
        /// <summary>The value of row <paramref name="index"/>.</summary>
        public BigInteger this[int index] => ColumnData.Big(column.Arena, column.Node, index);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<BigInteger> destination) => ColumnData.CopyBigs(column.Arena, column.Node, destination);

        /// <summary>The unscaled values as stored, of the storage's width.</summary>
        /// <typeparam name="TStorage">The storage's .NET type.</typeparam>
        /// <returns>The storage column.</returns>
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => StorageOf<BigInteger, TStorage>(column);
    }

    extension(Column<BigInteger?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public BigInteger? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index) ? ColumnData.Big(column.Arena, column.Node, index) : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<BigInteger?> destination) => ColumnData.CopyBigs(column.Arena, column.Node, destination);
    }

    extension(Column<TimeSpan> column)
    {
        /// <summary>The value of row <paramref name="index"/>, from its ticks.</summary>
        public TimeSpan this[int index] => new TimeSpan(ColumnData.Values<long>(column.Arena, column.Node)[index]);

        /// <summary>Copies the values into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<TimeSpan> destination) => ColumnData.CopySpans(column.Arena, column.Node, destination);

        /// <summary>The stored values: 100 ns ticks.</summary>
        /// <returns>The storage column.</returns>
        public Column<long> AsStorage() => StorageOf<TimeSpan, long>(column);
    }

    extension(Column<TimeSpan?> column)
    {
        /// <summary>The value of row <paramref name="index"/>, or null.</summary>
        public TimeSpan? this[int index] => ArenaWords.IsValid(column.Arena, column.Node, index)
            ? new TimeSpan(ColumnData.Values<long>(column.Arena, column.Node)[index])
            : null;

        /// <summary>Copies the values into <paramref name="destination"/>, a null for a null row.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<TimeSpan?> destination) => ColumnData.CopySpans(column.Arena, column.Node, destination);

        /// <summary>The stored values: 100 ns ticks.</summary>
        /// <returns>The storage column.</returns>
        public Column<long?> AsStorage() => StorageOf<TimeSpan?, long?>(column);
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
        public Column<TStorage> AsStorage<TStorage>()
            where TStorage : unmanaged => ColumnExtensions.StorageOf<T, TStorage>(column);
    }
}

/// <summary>The temporal columns' values, from their storage and unit.</summary>
internal static class Temporal
{
    internal static DateOnly Date(CanonicalArena arena, int node, VortexType type, int index) =>
        Dates.Convert(ColumnData.Int64(arena, node, index), type, index);

    internal static TimeOnly Time(CanonicalArena arena, int node, VortexType type, int index) =>
        Times.Convert(ColumnData.Int64(arena, node, index), type, index);

    internal static DateTime Timestamp(CanonicalArena arena, int node, VortexType type, int index) =>
        Timestamps.Convert(ColumnData.Int64(arena, node, index), type, index);

    internal static DateTimeOffset Zoned(CanonicalArena arena, int node, VortexType type, int index) =>
        Zoneds.Convert(ColumnData.Int64(arena, node, index), type, index);

    /// <summary>Copies every row of a temporal column, its storage resolved once.</summary>
    internal static void Copy<T, TConvert>(CanonicalArena arena, int node, VortexType type, Span<T> destination)
        where T : struct
        where TConvert : struct, IConvert<T>
    {
        ColumnData.IntegerStorage stored = ColumnData.Integers(arena, node);
        Span<T> into = ColumnData.Destination(destination, stored.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = TConvert.Convert(stored[i], type, i);
        }
    }

    /// <summary>Copies every row of a nullable temporal column, a null for a null row.</summary>
    internal static void Copy<T, TConvert>(CanonicalArena arena, int node, VortexType type, Span<T?> destination)
        where T : struct
        where TConvert : struct, IConvert<T>
    {
        ColumnData.IntegerStorage stored = ColumnData.Integers(arena, node);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        Span<T?> into = ColumnData.Destination(destination, stored.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = ColumnData.IsValid(valid, i) ? TConvert.Convert(stored[i], type, i) : null;
        }
    }

    /// <summary>One temporal type's conversion from its storage.</summary>
    internal interface IConvert<T>
    {
        static abstract T Convert(long stored, VortexType type, int index);
    }

    internal readonly struct Dates : IConvert<DateOnly>
    {
        public static DateOnly Convert(long stored, VortexType type, int index)
        {
            long days = type.Unit == TimeUnit.Milliseconds ? TemporalUnits.FloorDiv(stored, TemporalUnits.MillisecondsPerDay) : stored;
            return TemporalUnits.TryDate(days, out DateOnly date) ? date : OutOfRange<DateOnly>(index, stored, type);
        }
    }

    internal readonly struct Times : IConvert<TimeOnly>
    {
        public static TimeOnly Convert(long stored, VortexType type, int index) =>
            TemporalUnits.TryTime(stored, type.Unit ?? TimeUnit.Microseconds, out TimeOnly time) ? time : OutOfRange<TimeOnly>(index, stored, type);
    }

    internal readonly struct Timestamps : IConvert<DateTime>
    {
        public static DateTime Convert(long stored, VortexType type, int index) =>
            TemporalUnits.TryInstant(stored, type.Unit ?? TimeUnit.Microseconds, out long ticks)
                ? new DateTime(ticks, type.TimeZone is null ? DateTimeKind.Unspecified : DateTimeKind.Utc)
                : OutOfRange<DateTime>(index, stored, type);
    }

    internal readonly struct Zoneds : IConvert<DateTimeOffset>
    {
        public static DateTimeOffset Convert(long stored, VortexType type, int index) =>
            TemporalUnits.TryInstant(stored, type.Unit ?? TimeUnit.Microseconds, out long ticks)
            && TemporalUnits.TryZoned(ticks, type.ZoneInfo, out DateTimeOffset value)
                ? value
                : OutOfRange<DateTimeOffset>(index, stored, type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T OutOfRange<T>(int index, long stored, VortexType type) =>
        Columns.ColumnsThrow.Format<T>($"Row {index} stores {stored} in a column of {type}, which is outside what a {typeof(T).Name} holds.");
}
