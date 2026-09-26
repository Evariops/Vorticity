// vortex.datetimeparts. The corpus proves the arithmetic on 6 files; these cover what it cannot.
//
// The recomposition test is deliberately the reference's own fixture
// (vortex-datetime-parts-0.86.1/src/canonical.rs `test_decode_to_temporal`): a day-only value, a
// day+second value and a day+second+subsecond value, each in both signs. Negative components are
// the case a naive implementation gets wrong, because it reaches for unsigned parts or for a
// floor-division that does not round toward zero.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class DateTimePartsDecoderTests
{
    [Fact]
    public void RecomposesTheReferencesOwnMillisecondFixture()
    {
        // 86_400_000 ms is one day. The six values are +/- of: a day, a day + 1 s, a day + 1 s + 1 ms.
        byte[] days = TestBuffers.Int64(1, -1, 1, -1, 1, -1);
        byte[] seconds = TestBuffers.Int64(0, 0, 1, -1, 1, -1);
        byte[] subseconds = TestBuffers.Int64(0, 0, 0, 0, 1, -1);

        CanonicalNode node = Decode(
            VortexTimeUnit.Milliseconds, PType.I64, PType.I64, PType.I64, days, seconds, subseconds, 6);

        Assert.Equal(86_400_000L, Value(node, 0));
        Assert.Equal(-86_400_000L, Value(node, 1));
        Assert.Equal(86_400_000L + 1000, Value(node, 2));
        Assert.Equal(-86_400_000L - 1000, Value(node, 3));
        Assert.Equal(86_400_000L + 1000 + 1, Value(node, 4));
        Assert.Equal(-86_400_000L - 1000 - 1, Value(node, 5));
    }

    [Theory]
    [InlineData(VortexTimeUnit.Seconds, 86_400L)]
    [InlineData(VortexTimeUnit.Milliseconds, 86_400_000L)]
    [InlineData(VortexTimeUnit.Microseconds, 86_400_000_000L)]
    [InlineData(VortexTimeUnit.Nanoseconds, 86_400_000_000_000L)]
    internal void TheDivisorComesFromTheDTypesUnit(VortexTimeUnit unit, long oneDay)
    {
        byte[] days = TestBuffers.Int64(1);
        byte[] seconds = TestBuffers.Int64(0);
        byte[] subseconds = TestBuffers.Int64(0);

        CanonicalNode node = Decode(unit, PType.I64, PType.I64, PType.I64, days, seconds, subseconds, 1);
        Assert.Equal(oneDay, Value(node, 0));
    }

    [Fact]
    public void ThePartsMayBeNarrowerThanI64()
    {
        // The realistic shape: days bit-packs into an i32, seconds fits a u32 (< 86400), and
        // subseconds is often a u16 or constant. Each part widens on its own.
        byte[] days = TestBuffers.Int32(2);
        byte[] seconds = TestBuffers.UInt32(3);
        byte[] subseconds = TestBuffers.UInt16(4);

        CanonicalNode node = Decode(
            VortexTimeUnit.Milliseconds, PType.I32, PType.U32, PType.U16, days, seconds, subseconds, 1);

        Assert.Equal((2 * 86_400_000L) + 3000 + 4, Value(node, 0));
    }

    [Fact]
    public void ValidityComesFromTheDaysChildAlone()
    {
        byte[] days = TestBuffers.Int64(1, 2, 3);
        byte[] seconds = TestBuffers.Int64(0, 0, 0);
        byte[] subseconds = TestBuffers.Int64(0, 0, 0);
        byte[] validity = TestBuffers.Bitmap(true, false, true);

        TestNode root = Root(PType.I64, PType.I64, PType.I64, daysValidityBuffer: 3);
        using DecodeHarness harness = DecodeHarness.Load(
            root, days, seconds, subseconds, validity);
        DType dtype = TimestampDType(harness, VortexTimeUnit.Milliseconds, Nullability.Nullable);
        CanonicalNode ext = harness.Node(harness.DecodeRoot(dtype, 3));

        Assert.Equal(CanonicalKind.Extension, ext.Kind);
        CanonicalNode storage = harness.Node(ext.StorageIndex);
        Assert.False(storage.Validity.IsAllValid);
    }

    [Fact]
    public void TheArithmeticWrapsRatherThanSaturating()
    {
        // The reference multiplies i64 in release mode and widens with `as`, so an absurd day count
        // produces a wrong timestamp, not a panic and not i64::MAX. Disagreeing here would show up
        // as a conformance failure on exactly the inputs a fuzzer reaches first.
        byte[] days = TestBuffers.Int64(long.MaxValue);
        byte[] seconds = TestBuffers.Int64(0);
        byte[] subseconds = TestBuffers.Int64(0);

        CanonicalNode node = Decode(
            VortexTimeUnit.Seconds, PType.I64, PType.I64, PType.I64, days, seconds, subseconds, 1);

        Assert.Equal(unchecked(long.MaxValue * 86_400L), Value(node, 0));
    }

    [Fact]
    public void AU64PartTruncatesRatherThanSaturating()
    {
        // num_traits' as_() keeps the low 64 bits; CompressedValues.ReadInteger would have clamped
        // to i64::MaxValue, which is why this path does not use it.
        byte[] days = TestBuffers.UInt64(ulong.MaxValue);
        byte[] seconds = TestBuffers.Int64(0);
        byte[] subseconds = TestBuffers.Int64(0);

        CanonicalNode node = Decode(
            VortexTimeUnit.Seconds, PType.U64, PType.I64, PType.I64, days, seconds, subseconds, 1);

        Assert.Equal(unchecked(-1L * 86_400L), Value(node, 0));
    }

    [Fact]
    public void AWholeDaysUnitIsRejected()
    {
        // Upstream panics here ("cannot decode into TimeUnit::D"). A reader of untrusted input
        // cannot, so it is a format error.
        byte[] days = TestBuffers.Int64(1);
        byte[] zero = TestBuffers.Int64(0);

        TestNode root = Root(PType.I64, PType.I64, PType.I64);
        using DecodeHarness harness = DecodeHarness.Load(root, days, zero, zero);
        DType dtype = TimestampDType(harness, VortexTimeUnit.Days, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 1));
    }

    [Fact]
    public void ANonTimestampExtensionIsRejected()
    {
        byte[] days = TestBuffers.Int64(1);
        byte[] zero = TestBuffers.Int64(0);

        TestNode root = Root(PType.I64, PType.I64, PType.I64);
        using DecodeHarness harness = DecodeHarness.Load(root, days, zero, zero);
        DType storage = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType date = harness.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Days]);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(date, 1));
    }

    [Fact]
    public void ANonExtensionDTypeIsRejected()
    {
        byte[] days = TestBuffers.Int64(1);
        byte[] zero = TestBuffers.Int64(0);

        TestNode root = Root(PType.I64, PType.I64, PType.I64);
        using DecodeHarness harness = DecodeHarness.Load(root, days, zero, zero);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 1));
    }

    [Fact]
    public void AFloatPartIsRejected()
    {
        byte[] days = TestBuffers.Int64(1);
        byte[] zero = TestBuffers.Int64(0);

        TestNode root = Root(PType.I64, PType.F64, PType.I64);
        using DecodeHarness harness = DecodeHarness.Load(root, days, zero, zero);
        DType dtype = TimestampDType(harness, VortexTimeUnit.Milliseconds, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void AWrongChildCountIsRejected(int children)
    {
        byte[] zero = TestBuffers.Int64(0);
        TestNode root = new TestNode("vortex.datetimeparts")
            .WithMetadata(TestMetadata.DateTimeParts(PType.I64, PType.I64, PType.I64));
        for (int i = 0; i < children; i++)
        {
            root = root.WithChild(new TestNode("vortex.primitive").WithBuffer(0));
        }

        using DecodeHarness harness = DecodeHarness.Load(root, zero);
        DType dtype = TimestampDType(harness, VortexTimeUnit.Milliseconds, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(dtype, 1));
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyTimestamp()
    {
        TestNode root = Root(PType.I64, PType.I64, PType.I64);
        using DecodeHarness harness = DecodeHarness.Load(
            root, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>());
        DType dtype = TimestampDType(harness, VortexTimeUnit.Milliseconds, Nullability.NonNullable);
        CanonicalNode ext = harness.Node(harness.DecodeRoot(dtype, 0));

        Assert.Equal(CanonicalKind.Extension, ext.Kind);
        Assert.Equal(0, ext.Length);
        Assert.Equal(0, harness.Node(ext.StorageIndex).Length);
    }

    [Theory]
    [InlineData(PType.I8, PType.U16, PType.U32)]
    [InlineData(PType.U8, PType.I32, PType.I64)]
    [InlineData(PType.I16, PType.U32, PType.U64)]
    [InlineData(PType.U16, PType.I64, PType.I8)]
    [InlineData(PType.I32, PType.U64, PType.U8)]
    [InlineData(PType.U32, PType.I8, PType.I16)]
    [InlineData(PType.I64, PType.U8, PType.U16)]
    [InlineData(PType.U64, PType.I16, PType.I32)]
    internal void EveryPartTypeWidensAndWrapsAsTheScalarLineDoes(PType daysPType, PType secondsPType, PType subsecondsPType)
    {
        // Thirty-seven rows: whole vector steps where a machine has them and a tail after them,
        // over every bit pattern each type can hold, so a widening that sign-extends an unsigned
        // part, or a multiply that keeps other than the low 64 bits, shows on some row.
        const int rows = 37;
        Random random = new Random(20260926);
        byte[] days = RandomBytes(random, daysPType, rows);
        byte[] seconds = RandomBytes(random, secondsPType, rows);
        byte[] subseconds = RandomBytes(random, subsecondsPType, rows);

        CanonicalNode node = Decode(
            VortexTimeUnit.Nanoseconds, daysPType, secondsPType, subsecondsPType, days, seconds, subseconds, rows);

        for (int i = 0; i < rows; i++)
        {
            long expected = unchecked(
                Widened(subsecondsPType, subseconds, i)
                + (Widened(secondsPType, seconds, i) * 1_000_000_000L)
                + (Widened(daysPType, days, i) * 86_400_000_000_000L));
            Assert.Equal(expected, Value(node, i));
        }

        static byte[] RandomBytes(Random random, PType type, int rows)
        {
            byte[] bytes = new byte[rows * type.ByteWidth()];
            random.NextBytes(bytes);
            return bytes;
        }

        // long.CreateTruncating, spelled out per type: sign-extended, zero-extended, or the 64 bits as they are.
        static long Widened(PType type, byte[] bytes, int i) => type switch
        {
            PType.I8 => (sbyte)bytes[i],
            PType.U8 => bytes[i],
            PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)),
            PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2)),
            PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4)),
            PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)),
            _ => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(i * 8)),
        };
    }

    private static CanonicalNode Decode(
        VortexTimeUnit unit,
        PType daysPType,
        PType secondsPType,
        PType subsecondsPType,
        byte[] days,
        byte[] seconds,
        byte[] subseconds,
        int length)
    {
        TestNode root = Root(daysPType, secondsPType, subsecondsPType);
        DecodeHarness harness = DecodeHarness.Load(root, days, seconds, subseconds);
        DType dtype = TimestampDType(harness, unit, Nullability.NonNullable);
        CanonicalNode ext = harness.Node(harness.DecodeRoot(dtype, length));
        Assert.Equal(CanonicalKind.Extension, ext.Kind);

        CanonicalNode storage = harness.Node(ext.StorageIndex);
        Assert.Equal(PType.I64, storage.PType);
        return storage;
    }

    private static TestNode Root(
        PType days, PType seconds, PType subseconds, int daysValidityBuffer = -1)
    {
        TestNode daysChild = new TestNode("vortex.primitive").WithBuffer(0);
        if (daysValidityBuffer >= 0)
        {
            daysChild = daysChild.WithChild(new TestNode("vortex.bool").WithBuffer(daysValidityBuffer));
        }

        return new TestNode("vortex.datetimeparts")
            .WithMetadata(TestMetadata.DateTimeParts(days, seconds, subseconds))
            .WithChild(daysChild)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));
    }

    private static DType TimestampDType(
        DecodeHarness harness, VortexTimeUnit unit, Nullability nullability)
    {
        DType storage = harness.Types.Primitive(PType.I64, nullability);

        // vortex.timestamp metadata: the unit byte, then a u16 timezone length. Always written,
        // even when the zone is absent.
        byte[] metadata = [(byte)unit, 0, 0];
        return harness.Types.Extension("vortex.timestamp", storage, metadata);
    }

    private static long Value(CanonicalNode node, int index) =>
        BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(index * 8, 8));
}
