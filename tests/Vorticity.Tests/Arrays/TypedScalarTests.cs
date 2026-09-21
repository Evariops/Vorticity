// One test per row of the typed scalar mapping table, plus the ways each row can be violated.
// The wire bytes are produced by Phase 0's ScalarProtobuf writer, so these exercise the typed
// interpreter and not a hand-rolled encoder.
using System;
using System.Text;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class TypedScalarTests
{
    private readonly DTypeArena _types = new DTypeArena();
    private readonly ScalarStore _store = new ScalarStore();

    [Fact]
    public void AnEmptyMessageIsMalformedRatherThanNull()
    {
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => { _ = Read([], _types.Primitive(PType.I32, Nullability.Nullable)).IsNull; });
        Assert.Contains("missing kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullValueIsLegalAgainstAnyDType()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Null());
        Assert.True(Read(message, _types.Primitive(PType.I32, Nullability.Nullable)).IsNull);
        Assert.True(Read(message, _types.Utf8(Nullability.Nullable)).IsNull);
        Assert.True(Read(message, _types.Bool(Nullability.Nullable)).IsNull);
    }

    [Fact]
    public void BoolValueRequiresABoolDType()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Bool(true));
        Assert.True(Read(message, _types.Bool(Nullability.NonNullable)).AsBool);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.I32, Nullability.NonNullable)).AsBool; });
    }

    [Theory]
    [InlineData(PType.I8, -128L, true)]
    [InlineData(PType.I8, 127L, true)]
    [InlineData(PType.I8, 128L, false)]
    [InlineData(PType.I8, -129L, false)]
    [InlineData(PType.I16, 32767L, true)]
    [InlineData(PType.I16, 32768L, false)]
    [InlineData(PType.I32, 2147483647L, true)]
    [InlineData(PType.I32, 2147483648L, false)]
    [InlineData(PType.I64, long.MinValue, true)]
    [InlineData(PType.U8, 0L, true)]
    [InlineData(PType.U8, 255L, true)]
    [InlineData(PType.U8, 256L, false)]
    [InlineData(PType.U8, -1L, false)]
    [InlineData(PType.U64, -1L, false)]
    public void Int64ValueIsRangeCheckedAgainstThePType(PType ptype, long value, bool legal)
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(value));
        DType dtype = _types.Primitive(ptype, Nullability.NonNullable);

        if (legal)
        {
            Assert.Equal(value, Read(message, dtype).AsInt64);
        }
        else
        {
            Assert.Throws<VortexFormatException>(() => { _ = Read(message, dtype).AsInt64; });
        }
    }

    [Fact]
    public void Int64ValueAgainstAFloatPTypeIsAnError()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.F32, Nullability.NonNullable)).AsInt64; });
    }

    [Theory]
    [InlineData(PType.U8, 255UL, true)]
    [InlineData(PType.U8, 256UL, false)]
    [InlineData(PType.I8, 127UL, true)]
    [InlineData(PType.I8, 128UL, false)]
    [InlineData(PType.I64, (ulong)long.MaxValue, true)]
    [InlineData(PType.U64, ulong.MaxValue, true)]
    public void UInt64ValueAcceptsSignedPTypesForBackwardCompatibility(PType ptype, ulong value, bool legal)
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.UInt64(value));
        DType dtype = _types.Primitive(ptype, Nullability.NonNullable);

        if (legal)
        {
            Assert.Equal(value, Read(message, dtype).AsUInt64);
        }
        else
        {
            Assert.Throws<VortexFormatException>(() => { _ = Read(message, dtype).AsUInt64; });
        }
    }

    [Fact]
    public void UInt64ValueAgainstF16CarriesTheRawBitsBecauseF16UsedToBeSerializedThatWay()
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)(-2.5f));
        byte[] message = ScalarProtobuf.SerializeValue(_store.UInt64(bits));
        Assert.Equal((Half)(-2.5f), Read(message, _types.Primitive(PType.F16, Nullability.NonNullable)).AsF16);
    }

    [Theory]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    public void UInt64ValueAgainstF32OrF64IsAnError(PType ptype)
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.UInt64(1));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(ptype, Nullability.NonNullable)).AsUInt64; });
    }

    [Fact]
    public void UInt64ValueAboveUShortMaxAgainstF16IsAnError()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.UInt64(65536));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.F16, Nullability.NonNullable)).AsF16; });
    }

    [Fact]
    public void F32ValueRequiresExactlyF32AndPreservesNegativeZero()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.F32(-0.0f));
        TypedScalar scalar = Read(message, _types.Primitive(PType.F32, Nullability.NonNullable));
        Assert.Equal(0x8000_0000u, BitConverter.SingleToUInt32Bits(scalar.AsF32));

        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.F64, Nullability.NonNullable)).AsF32; });
    }

    [Fact]
    public void F64ValueRequiresExactlyF64AndPreservesANanPayload()
    {
        double nan = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001UL);
        byte[] message = ScalarProtobuf.SerializeValue(_store.F64(nan));
        TypedScalar scalar = Read(message, _types.Primitive(PType.F64, Nullability.NonNullable));
        Assert.Equal(0x7FF8_0000_0000_0001UL, BitConverter.DoubleToUInt64Bits(scalar.AsF64));

        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.F32, Nullability.NonNullable)).AsF64; });
    }

    [Fact]
    public void F16ValueRequiresExactlyF16()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.F16((Half)1.5f));
        Assert.Equal((Half)1.5f, Read(message, _types.Primitive(PType.F16, Nullability.NonNullable)).AsF16);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.F32, Nullability.NonNullable)).AsF16; });
    }

    [Fact]
    public void StringValueIsLegalAgainstUtf8AndBinaryAndNothingElse()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.String("héllo"u8));
        Assert.True(Read(message, _types.Utf8(Nullability.NonNullable)).AsUtf8.SequenceEqual("héllo"u8));
        Assert.True(Read(message, _types.Binary(Nullability.NonNullable)).AsBinary.SequenceEqual("héllo"u8));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.I32, Nullability.NonNullable)).WireKind; });
    }

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(2, 4, 2)]
    [InlineData(4, 9, 2)]
    [InlineData(8, 18, 4)]
    [InlineData(16, 38, 10)]
    [InlineData(32, 40, 10)]
    public void BytesValueAgainstADecimalSelectsTheStorageWidthFromItsLength(
        int width,
        byte precision,
        sbyte scale)
    {
        // -1, little-endian two's complement at the stored width: every byte 0xFF.
        byte[] unscaled = new byte[width];
        unscaled.AsSpan().Fill(0xFF);

        byte[] message = ScalarProtobuf.SerializeValue(_store.Bytes(unscaled));
        TypedScalar scalar = Read(message, _types.Decimal(precision, scale, Nullability.NonNullable));

        VortexDecimal value = scalar.AsDecimal;
        Assert.Equal(precision, value.Precision);
        Assert.Equal(scale, value.Scale);
        Assert.True(value.IsNegative);
        Assert.True(value.TryToInt128(out Int128 unscaledValue));
        Assert.Equal(Int128.NegativeOne, unscaledValue);
    }

    [Fact]
    public void APositiveDecimalIsNotSignExtended()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Bytes([0x39, 0x30]));
        TypedScalar scalar = Read(message, _types.Decimal(4, 2, Nullability.NonNullable));
        Assert.True(scalar.AsDecimal.TryToInt128(out Int128 unscaled));
        Assert.Equal((Int128)12345, unscaled);
        Assert.Equal("123.45", scalar.AsDecimal.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(33)]
    public void ADecimalBytesValueOfAnUnsupportedWidthIsMalformed(int width)
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Bytes(new byte[width]));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Decimal(38, 2, Nullability.NonNullable)).WireKind; });
    }

    [Fact]
    public void BytesValueIsAlsoLegalAgainstUtf8AndBinary()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Bytes([0x00, 0xFF]));
        Assert.Equal(2, Read(message, _types.Binary(Nullability.NonNullable)).AsBinary.Length);
        Assert.Equal(2, Read(message, _types.Utf8(Nullability.NonNullable)).AsUtf8.Length);
    }

    [Fact]
    public void AStructListValueMustHaveExactlyTheFieldCount()
    {
        DType structType = _types.Struct(
            ["a", "b"],
            [
                _types.Primitive(PType.I32, Nullability.NonNullable),
                _types.Utf8(Nullability.NonNullable),
            ],
            Nullability.NonNullable);

        ScalarValue good = _store.List([_store.Int64(7), _store.String("x"u8)]);
        TypedScalar scalar = Read(ScalarProtobuf.SerializeValue(good), structType);
        Assert.Equal(2, scalar.ElementCount);
        Assert.Equal(7, scalar.GetElement(0).AsInt64);
        Assert.True(scalar.GetElement(1).AsUtf8.SequenceEqual("x"u8));

        ScalarValue tooFew = _store.List([_store.Int64(7)]);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(ScalarProtobuf.SerializeValue(tooFew), structType).ElementCount; });

        ScalarValue tooMany = _store.List([_store.Int64(7), _store.String("x"u8), _store.Int64(9)]);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(ScalarProtobuf.SerializeValue(tooMany), structType).ElementCount; });
    }

    [Fact]
    public void AStructFieldOfTheWrongTypeIsRejectedAtReadTimeNotAtAccessTime()
    {
        DType structType = _types.Struct(
            ["a"],
            [_types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);

        ScalarValue bad = _store.List([_store.String("x"u8)]);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(ScalarProtobuf.SerializeValue(bad), structType).ElementCount; });
    }

    [Fact]
    public void AListValueRecursesPerElementAgainstTheElementDType()
    {
        DType listType = _types.List(
            _types.Primitive(PType.I32, Nullability.NonNullable),
            Nullability.NonNullable);

        ScalarValue list = _store.List([_store.Int64(1), _store.Int64(2), _store.Int64(3)]);
        byte[] message = ScalarProtobuf.SerializeValue(list);
        TypedScalar scalar = Read(message, listType);

        Assert.Equal(3, scalar.ElementCount);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i + 1, scalar.GetElement(i).AsInt64);
        }

        // A ref struct cannot be captured, so the out-of-range cases re-read inside the lambda.
        Assert.Throws<VortexFormatException>(() => { _ = Read(message, listType).GetElement(3).AsInt64; });
        Assert.Throws<VortexFormatException>(() => { _ = Read(message, listType).GetElement(-1).AsInt64; });
    }

    [Fact]
    public void AListElementOutOfTheElementDTypesRangeIsRejected()
    {
        DType listType = _types.List(
            _types.Primitive(PType.U8, Nullability.NonNullable),
            Nullability.NonNullable);

        ScalarValue list = _store.List([_store.Int64(1), _store.Int64(999)]);
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(ScalarProtobuf.SerializeValue(list), listType).ElementCount; });
    }

    [Fact]
    public void AnExtensionDTypeIsInterpretedAgainstItsStorage()
    {
        // Forgetting this one line turns every temporal constant into a type error.
        DType storage = _types.Primitive(PType.I64, Nullability.NonNullable);
        DType timestamp = _types.Extension("vortex.timestamp", storage, [2, 0, 0]);

        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1_700_000_000_000));
        TypedScalar scalar = Read(message, timestamp);

        Assert.Equal(1_700_000_000_000L, scalar.AsInt64);
        Assert.Equal(DTypeKind.Primitive, scalar.DType.Kind);
        Assert.Equal(PType.I64, scalar.DType.PType);
    }

    [Fact]
    public void ANestedExtensionUnwrapsAllTheWayDown()
    {
        DType inner = _types.Extension(
            "vortex.date", _types.Primitive(PType.I32, Nullability.NonNullable), [4]);
        DType outer = _types.Extension("acme.wrapper", inner, []);

        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(19000));
        Assert.Equal(19000L, Read(message, outer).AsInt64);
    }

    /// <summary>A variant scalar reads as its nested typed scalar, RFC 0015's <c>(dtype, value)</c>.</summary>
    /// <remarks>
    /// This asserted the OPPOSITE until `vortex.variant` gained a decoder: a variant scalar was
    /// refused, because nothing could do anything with one. The `vortex.variant` corpus files are
    /// exactly this shape -- a constant carrier holding `variant(i32 = 1)` -- so refusing it made
    /// them unreadable. What is validated is the NESTED half against its own dtype, which is what
    /// keeps `variant(i32 = 1)` a checked value rather than an opaque blob.
    /// </remarks>
    [Fact]
    public void AVariantScalarReadsAsItsNestedTypedScalar()
    {
        Scalar inner = new Scalar(_types.Primitive(PType.I32, Nullability.NonNullable), _store.Int64(1));
        byte[] message = ScalarProtobuf.SerializeValue(_store.Variant(in inner));

        TypedScalar scalar = Read(message, _types.Variant(Nullability.NonNullable));
        Assert.Equal(ScalarValueKind.Variant, scalar.WireKind);

        Scalar nested = scalar.AsVariantScalar;
        Assert.Equal(DTypeKind.Primitive, nested.DType.Kind);
        Assert.Equal(PType.I32, nested.DType.PType);
        Assert.Equal(1L, TypedScalarReader.Interpret(nested.Value, nested.DType).AsInt64);
    }

    /// <summary>A variant scalar against a non-variant dtype is still refused.</summary>
    [Fact]
    public void AVariantScalarNeedsAVariantDType()
    {
        Scalar inner = new Scalar(_types.Primitive(PType.I32, Nullability.NonNullable), _store.Int64(1));
        byte[] message = ScalarProtobuf.SerializeValue(_store.Variant(in inner));

        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.I32, Nullability.NonNullable)).WireKind; });
    }

    [Fact]
    public void AUnionScalarIsUnsupportedRatherThanMisread()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Union(0, _store.Int64(1)));
        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => { _ = Read(message, _types.Primitive(PType.I32, Nullability.NonNullable)).WireKind; });
        Assert.Equal(VortexComponentKind.DType, error.Kind);
    }

    [Fact]
    public void AMapScalarIsUnsupportedBecausePhaseOneHasNoCanonicalFormForIt()
    {
        DType mapType = _types.Map(
            _types.Utf8(Nullability.NonNullable),
            _types.Primitive(PType.I64, Nullability.NonNullable),
            keysSorted: false,
            Nullability.NonNullable);

        byte[] message = ScalarProtobuf.SerializeValue(_store.List([_store.Int64(1)]));
        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => { _ = Read(message, mapType).ElementCount; });
        Assert.Equal(VortexComponentKind.DType, error.Kind);
    }

    [Fact]
    public void AListValueAgainstAScalarDTypeIsMalformed()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.List([_store.Int64(1)]));
        Assert.Throws<VortexFormatException>(
            () => { _ = Read(message, _types.Primitive(PType.I32, Nullability.NonNullable)).ElementCount; });
    }

    [Theory]
    [InlineData(PType.U8, 1)]
    [InlineData(PType.U16, 2)]
    [InlineData(PType.U32, 4)]
    [InlineData(PType.U64, 8)]
    [InlineData(PType.I8, 1)]
    [InlineData(PType.I16, 2)]
    [InlineData(PType.I32, 4)]
    [InlineData(PType.I64, 8)]
    public void WriteToFillsTheWholeDestinationWithRepeatedCopies(PType ptype, int width)
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1));
        TypedScalar scalar = Read(message, _types.Primitive(ptype, Nullability.NonNullable));

        Span<byte> destination = stackalloc byte[width * 5];
        scalar.WriteTo(destination, ptype);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, destination[i * width]);
            for (int b = 1; b < width; b++)
            {
                Assert.Equal(0, destination[(i * width) + b]);
            }
        }
    }

    [Fact]
    public void WriteToRejectsARaggedDestination()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1));
        DType dtype = _types.Primitive(PType.I32, Nullability.NonNullable);
        byte[] destination = new byte[7];
        Assert.Throws<ArgumentException>(() => Read(message, dtype).WriteTo(destination, PType.I32));
    }

    [Fact]
    public void WriteToWritesZerosForANullScalar()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Null());
        TypedScalar scalar = Read(message, _types.Primitive(PType.I64, Nullability.Nullable));

        Span<byte> destination = stackalloc byte[16];
        destination.Fill(0xEE);
        scalar.WriteTo(destination, PType.I64);
        foreach (byte b in destination)
        {
            Assert.Equal(0, b);
        }
    }

    [Fact]
    public void WriteToRoundTripsAFloat()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.F64(-3.5));
        TypedScalar scalar = Read(message, _types.Primitive(PType.F64, Nullability.NonNullable));

        Span<byte> destination = stackalloc byte[16];
        scalar.WriteTo(destination, PType.F64);
        Assert.Equal(-3.5, BitConverter.ToDouble(destination[..8]));
        Assert.Equal(-3.5, BitConverter.ToDouble(destination[8..]));
    }

    [Fact]
    public void WriteToRejectsAValueTooWideForThePType()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(300));
        DType dtype = _types.Primitive(PType.I64, Nullability.NonNullable);

        // The dtype says i64 and the caller asks for i8: the narrowing must not wrap silently.
        byte[] destination = new byte[1];
        Assert.Throws<OverflowException>(() => Read(message, dtype).WriteTo(destination, PType.I8));
    }

    [Fact]
    public void ReadRejectsADefaultDType()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1));
        Assert.Throws<ArgumentException>(
            () => { _ = TypedScalarReader.Read(message, default, _store, _types).WireKind; });
    }

    [Fact]
    public void ReadRejectsNullCollaborators()
    {
        byte[] message = ScalarProtobuf.SerializeValue(_store.Int64(1));
        DType dtype = _types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<ArgumentNullException>(
            () => { _ = TypedScalarReader.Read(message, dtype, null!, _types).WireKind; });
        Assert.Throws<ArgumentNullException>(
            () => { _ = TypedScalarReader.Read(message, dtype, _store, null!).WireKind; });
    }

    [Fact]
    public void AsInt64AcceptsAUInt64WireKindAndRejectsOneTooLarge()
    {
        DType dtype = _types.Primitive(PType.U64, Nullability.NonNullable);

        byte[] small = ScalarProtobuf.SerializeValue(_store.UInt64(5));
        Assert.Equal(5L, Read(small, dtype).AsInt64);

        byte[] huge = ScalarProtobuf.SerializeValue(_store.UInt64(ulong.MaxValue));
        Assert.Throws<VortexFormatException>(() => { _ = Read(huge, dtype).AsInt64; });
    }

    [Fact]
    public void InterpretValidatesAnAlreadyParsedValue()
    {
        ScalarValue value = _store.Int64(300);
        Assert.Throws<VortexFormatException>(
            () => { _ = TypedScalarReader.Interpret(value, _types.Primitive(PType.I8, Nullability.NonNullable)).AsInt64; });
        Assert.Equal(300L, TypedScalarReader.Interpret(value, _types.Primitive(PType.I16, Nullability.NonNullable)).AsInt64);
    }

    [Fact]
    public void InterpretRejectsAnAbsentValue()
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = TypedScalarReader.Interpret(default, _types.Bool(Nullability.NonNullable)).WireKind; });
    }

    private TypedScalar Read(ReadOnlySpan<byte> message, DType dtype) =>
        TypedScalarReader.Read(message, dtype, _store, _types);
}
