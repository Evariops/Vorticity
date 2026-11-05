// A stored value turned into the .NET type a caller asked for: what a key cursor reads at each step
// and what a grouped aggregate delivers for each group. Each type, nullable or not, has a path of its
// own that builds the value without a box: a literal read back as a DateTime was an object, a decimal
// three BigIntegers and an object, a Guid an array and an object.
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class StoredValueConversionTests
{
    private static readonly DateTime Instant = new DateTime(2026, 11, 5, 10, 30, 15, DateTimeKind.Utc).AddTicks(1_230);

    private static readonly long Micros = (Instant.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    private static readonly VortexType Utc = VortexType.Timestamp(TimeUnit.Microseconds, "UTC");

    private static readonly VortexType Naive = VortexType.Timestamp(TimeUnit.Microseconds);

    private static readonly VortexType Price = VortexType.Decimal(18, 2);

    private static readonly VortexType Wide = VortexType.Decimal(28, 4);

    private static readonly Guid Id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void ALiteralBecomesAnInstantOfItsColumnsKind()
    {
        DateTime utc = LiteralValues.ToValue<DateTime>(FilterLiteral.From(Micros), Utc);
        Assert.Equal(Instant, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(Instant, LiteralValues.ToValue<DateTime?>(FilterLiteral.From(Micros), Utc));
        Assert.Equal(DateTimeKind.Unspecified, LiteralValues.ToValue<DateTime>(FilterLiteral.From(Micros), Naive).Kind);
        Assert.Equal(new DateTimeOffset(Instant), LiteralValues.ToValue<DateTimeOffset?>(FilterLiteral.From(Micros), Utc));
        Assert.Null(LiteralValues.ToValue<DateTime?>(FilterLiteral.Null, Utc));
    }

    [Fact]
    public void ALiteralBecomesADateATimeAndADuration()
    {
        DateOnly date = DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(20_000);
        Assert.Equal(date, LiteralValues.ToValue<DateOnly>(FilterLiteral.From(20_000L), VortexType.Date));
        Assert.Equal(date, LiteralValues.ToValue<DateOnly?>(FilterLiteral.From(20_000L), VortexType.Date));
        Assert.Equal(new TimeOnly(1, 2, 3), LiteralValues.ToValue<TimeOnly?>(FilterLiteral.From(3_723_000_000L), VortexType.Time(TimeUnit.Microseconds)));
        Assert.Equal(TimeSpan.FromTicks(-123), LiteralValues.ToValue<TimeSpan?>(FilterLiteral.From(-123L), VortexType.Int64));

        VortexFormatException outside = Assert.Throws<VortexFormatException>(() => LiteralValues.ToValue<DateOnly>(FilterLiteral.From(long.MaxValue), VortexType.Date));
        Assert.Contains("outside what a DateOnly holds", outside.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiteralBecomesADecimalWhetherItIsAnIntegerOrWide()
    {
        Assert.Equal(-123.45m, LiteralValues.ToValue<decimal>(FilterLiteral.From(-12_345L), Price));
        Assert.Equal(123.45m, LiteralValues.ToValue<decimal?>(FilterLiteral.From(12_345UL), Price));
        Assert.Equal(0.00m, LiteralValues.ToValue<decimal>(FilterLiteral.From(0L), Price));

        // Past a long, a decimal literal carries its unscaled value's bytes.
        BigInteger unscaled = BigInteger.Pow(10, 24) + 7;
        Assert.Equal(-100_000_000_000_000_000_000.0007m, LiteralValues.ToValue<decimal>(SymLowering.WideLiteral(-unscaled), Wide));
    }

    [Fact]
    public void ALiteralBecomesAGuidANullablePrimitiveAndABool()
    {
        Assert.Equal(Id, LiteralValues.ToValue<Guid?>(FilterLiteral.From(Id.ToByteArray(bigEndian: true)), VortexType.Uuid));
        Assert.Equal(-5L, LiteralValues.ToValue<long?>(FilterLiteral.From(-5L), VortexType.Int64));
        Assert.Equal((ushort)65_535, LiteralValues.ToValue<ushort?>(FilterLiteral.From(65_535UL), VortexType.UInt16));
        Assert.Equal((Half)1.5, LiteralValues.ToValue<Half?>(FilterLiteral.From(1.5), VortexType.Float16));
        Assert.Equal(2.5f, LiteralValues.ToValue<float?>(FilterLiteral.From(2.5), VortexType.Float32));
        Assert.True(LiteralValues.ToValue<bool?>(FilterLiteral.From(true), VortexType.Bool));
        Assert.Null(LiteralValues.ToValue<int?>(FilterLiteral.Null, VortexType.Int32));
    }

    [Fact]
    public void AStoredValueBecomesEachTypeWithoutALiteral()
    {
        DateTime utc = StorageValues.ToClr<long, DateTime>(Micros, Shape(Utc));
        Assert.Equal(Instant, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(Instant, StorageValues.ToClr<long, DateTime?>(Micros, Shape(Utc)));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(-3), StorageValues.ToClr<int, DateOnly?>(-3, Shape(VortexType.Date)));
        Assert.Equal(TimeSpan.FromTicks(42), StorageValues.ToClr<long, TimeSpan>(42, Shape(VortexType.Int64)));
        Assert.Equal(-123.45m, StorageValues.ToClr<Int128, decimal>(-12_345, Shape(Price)));
        Assert.Equal(99_999_999_999_999.9999m, StorageValues.ToClr<Int128, decimal?>(999_999_999_999_999_999, Shape(Wide)));
        Assert.Equal(Id, StorageValues.ToClr<UInt128, Guid?>(BinaryPrimitives.ReadUInt128BigEndian(Id.ToByteArray(bigEndian: true)), Shape(VortexType.Uuid)));
    }

    [Fact]
    public void BytesBecomeTextAndBinaryInOneCopy()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("Île-de-France");
        Assert.Equal("Île-de-France", StorageValues.BytesToClr<string>(utf8, Shape(VortexType.Utf8)));
        ReadOnlyMemory<byte> copy = StorageValues.BytesToClr<ReadOnlyMemory<byte>>(utf8, Shape(VortexType.Binary));
        Assert.Equal(utf8, copy.ToArray());
        Assert.Equal(utf8, StorageValues.BytesToClr<ReadOnlyMemory<byte>?>(utf8, Shape(VortexType.Binary))!.Value.ToArray());
    }

    [Fact]
    public void NoConversionAllocates()
    {
        ReleaseOnlyCeilings.Require();
        ColumnShape utc = Shape(Utc);
        ColumnShape price = Shape(Price);
        ColumnShape uuid = Shape(VortexType.Uuid);
        ColumnShape date = Shape(VortexType.Date);
        FilterLiteral instant = FilterLiteral.From(Micros);
        FilterLiteral cents = FilterLiteral.From(-12_345L);
        FilterLiteral id = FilterLiteral.From(Id.ToByteArray(bigEndian: true));
        UInt128 bits = BinaryPrimitives.ReadUInt128BigEndian(Id.ToByteArray(bigEndian: true));

        long sink = Convert();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            sink += Convert();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(sink);
        Assert.Equal(0, allocated);

        long Convert() =>
            LiteralValues.ToValue<DateTime?>(instant, Utc)!.Value.Ticks
            + LiteralValues.ToValue<DateTimeOffset>(instant, Utc).Ticks
            + (long)LiteralValues.ToValue<decimal?>(cents, Price)!.Value
            + LiteralValues.ToValue<Guid>(id, VortexType.Uuid).GetHashCode()
            + LiteralValues.ToValue<long?>(cents, VortexType.Int64)!.Value
            + LiteralValues.ToValue<DateOnly?>(FilterLiteral.From(20_000L), VortexType.Date)!.Value.DayNumber
            + StorageValues.ToClr<long, DateTime?>(Micros, utc)!.Value.Ticks
            + (long)StorageValues.ToClr<Int128, decimal?>(-12_345, price)!.Value
            + StorageValues.ToClr<UInt128, Guid?>(bits, uuid)!.Value.GetHashCode()
            + StorageValues.ToClr<int, DateOnly>(-3, date).DayNumber;
    }

    private static ColumnShape Shape(VortexType type) => new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));
}
