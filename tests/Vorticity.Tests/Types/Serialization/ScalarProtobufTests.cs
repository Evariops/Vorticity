// Adversarial tests for the Scalar Protobuf codec. The wire types in the schema are
// the trap: int64_value is zigzag while uint64_value is not, and f16_value is a varint of raw bits
// while f32/f64 are fixed-width. A codec that got any of those wrong would round-trip perfectly
// against itself, so the decisive cases below are hand-assembled bytes.
using System;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Types.Serialization;

public sealed class ScalarProtobufTests
{
    // The ScalarValue message's oneof case numbers, restated rather than imported.
    private const int CaseNull = 1;
    private const int CaseBool = 2;
    private const int CaseInt64 = 3;
    private const int CaseUInt64 = 4;
    private const int CaseF32 = 5;
    private const int CaseF64 = 6;
    private const int CaseString = 7;
    private const int CaseBytes = 8;
    private const int CaseList = 9;
    private const int CaseF16 = 10;
    private const int CaseVariant = 11;
    private const int CaseUnion = 12;

    // DType.primitive is case 3 of the DType message's oneof.
    private const int DTypeCasePrimitive = 3;

    // ------------------------------------------------------------------ hand-written wire vectors

    /// <summary>
    /// <c>int64_value</c> is <c>sint64</c>: ZigZag, not a two's-complement varint. <c>-1</c> encodes
    /// to the single byte 1, and 1 encodes to 2. A codec that used a plain varint would produce a
    /// ten-byte field for <c>-1</c> and read this vector back as <c>1</c>.
    /// </summary>
    [Theory]
    [InlineData(0L, new byte[] { 0x18, 0x00 })]
    [InlineData(-1L, new byte[] { 0x18, 0x01 })]
    [InlineData(1L, new byte[] { 0x18, 0x02 })]
    [InlineData(-2L, new byte[] { 0x18, 0x03 })]
    [InlineData(63L, new byte[] { 0x18, 0x7E })]
    [InlineData(-64L, new byte[] { 0x18, 0x7F })]
    [InlineData(64L, new byte[] { 0x18, 0x80, 0x01 })]
    public void Int64_uses_the_zigzag_encoding(long value, byte[] expected)
    {
        ScalarStore store = new ScalarStore();
        Assert.Equal(expected, ScalarProtobuf.SerializeValue(store.Int64(value)));
        Assert.Equal(value, ScalarProtobuf.ReadValue(expected, new ScalarStore(), new DTypeArena()).AsInt64);
    }

    /// <summary>ZigZag of <see cref="long.MinValue"/> is <see cref="ulong.MaxValue"/>: ten bytes, all bits set.</summary>
    [Fact]
    public void Int64_min_value_round_trips_through_zigzag()
    {
        byte[] expected = [0x18, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        ScalarStore store = new ScalarStore();

        Assert.Equal(expected, ScalarProtobuf.SerializeValue(store.Int64(long.MinValue)));
        Assert.Equal(
            long.MinValue,
            ScalarProtobuf.ReadValue(expected, new ScalarStore(), new DTypeArena()).AsInt64);
    }

    /// <summary>
    /// <c>uint64_value</c> is a plain varint, so the same payload byte means a different number in
    /// field 3 and in field 4. This is the pair that catches a copy-paste between the two.
    /// </summary>
    [Fact]
    public void Uint64_is_a_plain_varint_unlike_int64()
    {
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();

        ScalarValue asInt64 = ScalarProtobuf.ReadValue([0x18, 0x01], store, arena);
        ScalarValue asUInt64 = ScalarProtobuf.ReadValue([0x20, 0x01], store, arena);

        Assert.Equal(ScalarValueKind.Int64, asInt64.Kind);
        Assert.Equal(-1L, asInt64.AsInt64);
        Assert.Equal(ScalarValueKind.UInt64, asUInt64.Kind);
        Assert.Equal(1UL, asUInt64.AsUInt64);

        Assert.Equal(new byte[] { 0x20, 0x01 }, ScalarProtobuf.SerializeValue(store.UInt64(1)));
    }

    /// <summary>
    /// <c>f16_value</c> is a <c>uint64</c> varint carrying the raw binary16 bits — not a fixed16, not
    /// a float. Tag 10 with wire type 0 is 0x50; 0x8000 (negative zero) encodes to 80 80 02.
    /// </summary>
    [Theory]
    [InlineData((ushort)0x0000, new byte[] { 0x50, 0x00 })]
    [InlineData((ushort)0x8000, new byte[] { 0x50, 0x80, 0x80, 0x02 })]      // -0.0
    [InlineData((ushort)0x7E01, new byte[] { 0x50, 0x81, 0xFC, 0x01 })]      // quiet NaN, payload 1
    [InlineData((ushort)0x3C00, new byte[] { 0x50, 0x80, 0x78 })]            // 1.0
    [InlineData((ushort)0xFFFF, new byte[] { 0x50, 0xFF, 0xFF, 0x03 })]
    public void F16_is_a_varint_of_the_raw_bits(ushort bits, byte[] expected)
    {
        ScalarStore store = new ScalarStore();
        Assert.Equal(expected, ScalarProtobuf.SerializeValue(store.F16FromBits(bits)));
        Assert.Equal(bits, ScalarProtobuf.ReadValue(expected, new ScalarStore(), new DTypeArena()).F16Bits);
    }

    /// <summary>
    /// Bit fidelity, stated as a property rather than as bytes: a NaN payload and the sign of zero
    /// survive, because the codec moves bits and never a <see cref="Half"/> value through a
    /// comparison.
    /// </summary>
    [Theory]
    [InlineData((ushort)0x8000)]
    [InlineData((ushort)0x7E01)]
    [InlineData((ushort)0xFE01)]
    [InlineData((ushort)0x7C00)]
    [InlineData((ushort)0xFC00)]
    [InlineData((ushort)0x0001)]
    public void F16_bit_patterns_survive_a_round_trip(ushort bits)
    {
        ScalarStore store = new ScalarStore();
        ScalarValue round = RoundTrip(store.F16FromBits(bits));

        Assert.Equal(bits, round.F16Bits);
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(round.AsF16));
    }

    /// <summary><c>null_value</c> is a <c>google.protobuf.NullValue</c> enum: field 1, varint, value 0.</summary>
    [Fact]
    public void Null_encodes_as_the_zero_enum_and_is_still_emitted()
    {
        ScalarStore store = new ScalarStore();
        Assert.Equal(new byte[] { 0x08, 0x00 }, ScalarProtobuf.SerializeValue(store.Null()));
    }

    /// <summary>
    /// A oneof member has explicit presence, so <c>false</c>, <c>""</c> and an empty
    /// <c>ListValue</c> must still be emitted. Under proto3 implicit presence they would vanish and
    /// read back as <see cref="ScalarValueKind.Absent"/> — a different value entirely.
    /// </summary>
    [Fact]
    public void Default_valued_cases_are_still_emitted()
    {
        ScalarStore store = new ScalarStore();

        Assert.Equal(new byte[] { 0x10, 0x00 }, ScalarProtobuf.SerializeValue(store.Bool(false)));
        Assert.Equal(new byte[] { 0x18, 0x00 }, ScalarProtobuf.SerializeValue(store.Int64(0)));
        Assert.Equal(new byte[] { 0x20, 0x00 }, ScalarProtobuf.SerializeValue(store.UInt64(0)));
        Assert.Equal(new byte[] { 0x3A, 0x00 }, ScalarProtobuf.SerializeValue(store.String(ReadOnlySpan<byte>.Empty)));
        Assert.Equal(new byte[] { 0x42, 0x00 }, ScalarProtobuf.SerializeValue(store.Bytes(ReadOnlySpan<byte>.Empty)));
        Assert.Equal(new byte[] { 0x4A, 0x00 }, ScalarProtobuf.SerializeValue(store.List(ReadOnlySpan<ScalarValue>.Empty)));
        Assert.Equal(
            new byte[] { 0x2D, 0x00, 0x00, 0x00, 0x00 },
            ScalarProtobuf.SerializeValue(store.F32(0f)));
    }

    /// <summary>
    /// <c>-0.0</c> must survive, but the proto3 <c>value != 0</c> presence test treats it as
    /// absent, so the codec has to use the bit-preserving writer.
    /// </summary>
    [Fact]
    public void Negative_zero_survives_for_both_float_widths()
    {
        ScalarStore store = new ScalarStore();

        byte[] f32 = ScalarProtobuf.SerializeValue(store.F32(-0.0f));
        Assert.Equal(new byte[] { 0x2D, 0x00, 0x00, 0x00, 0x80 }, f32);
        ScalarValue readF32 = ScalarProtobuf.ReadValue(f32, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.F32, readF32.Kind);
        Assert.Equal(0x8000_0000u, BitConverter.SingleToUInt32Bits(readF32.AsF32));

        byte[] f64 = ScalarProtobuf.SerializeValue(store.F64(-0.0d));
        Assert.Equal(new byte[] { 0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80 }, f64);
        ScalarValue readF64 = ScalarProtobuf.ReadValue(f64, new ScalarStore(), new DTypeArena());
        Assert.Equal(0x8000_0000_0000_0000ul, BitConverter.DoubleToUInt64Bits(readF64.AsF64));
    }

    [Theory]
    [InlineData(0x7FC0_0001u)]      // quiet NaN with a payload
    [InlineData(0xFFC0_0001u)]      // negative quiet NaN with a payload
    [InlineData(0x7F80_0001u)]      // signalling NaN
    [InlineData(0x7F80_0000u)]      // +inf
    [InlineData(0xFF80_0000u)]      // -inf
    [InlineData(0x8000_0000u)]      // -0.0
    public void F32_bit_patterns_survive_a_round_trip(uint bits)
    {
        ScalarStore store = new ScalarStore();
        ScalarValue round = RoundTrip(store.F32(BitConverter.UInt32BitsToSingle(bits)));
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(round.AsF32));
    }

    [Theory]
    [InlineData(0x7FF8_0000_0000_0001ul)]
    [InlineData(0xFFF8_0000_0000_0001ul)]
    [InlineData(0x7FF0_0000_0000_0000ul)]
    [InlineData(0x8000_0000_0000_0000ul)]
    public void F64_bit_patterns_survive_a_round_trip(ulong bits)
    {
        ScalarStore store = new ScalarStore();
        ScalarValue round = RoundTrip(store.F64(BitConverter.UInt64BitsToDouble(bits)));
        Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(round.AsF64));
    }

    // ---------------------------------------------------------------------------- round trips

    [Fact]
    public void Every_wire_kind_round_trips()
    {
        DTypeArena arena = new DTypeArena();
        ScalarStore store = new ScalarStore();

        AssertRoundTrip(store.Null());
        AssertRoundTrip(store.Bool(true));
        AssertRoundTrip(store.Bool(false));
        AssertRoundTrip(store.Int64(long.MinValue));
        AssertRoundTrip(store.Int64(long.MaxValue));
        AssertRoundTrip(store.Int64(0));
        AssertRoundTrip(store.UInt64(0));
        AssertRoundTrip(store.UInt64(ulong.MaxValue));
        AssertRoundTrip(store.F32(3.5f));
        AssertRoundTrip(store.F64(-1.25d));
        AssertRoundTrip(store.F16(BitConverter.UInt16BitsToHalf(0x3C00)));
        AssertRoundTrip(store.String("héllo — wörld"));
        AssertRoundTrip(store.String(ReadOnlySpan<byte>.Empty));
        AssertRoundTrip(store.Bytes([0x00, 0xFF, 0x7F, 0x80]));
        AssertRoundTrip(store.Bytes(ReadOnlySpan<byte>.Empty));
        AssertRoundTrip(store.List([store.Int64(1), store.Int64(2)]));
        AssertRoundTrip(store.List(ReadOnlySpan<ScalarValue>.Empty));
        AssertRoundTrip(store.Union(3, store.Int64(-5)));
        AssertRoundTrip(store.Union(0, store.Absent));
        AssertRoundTrip(store.Union(uint.MaxValue, store.Bool(true)));
        AssertRoundTrip(store.Variant(new Scalar(arena.Primitive(PType.I32, Nullability.Nullable), store.Int64(7))));
    }

    /// <summary>An empty <c>ScalarValue</c> body sets no case, which is Absent — never Null.</summary>
    [Fact]
    public void An_empty_message_is_absent_and_not_null()
    {
        ScalarStore store = new ScalarStore();

        Assert.Equal(Array.Empty<byte>(), ScalarProtobuf.SerializeValue(store.Absent));

        ScalarValue read = ScalarProtobuf.ReadValue(Array.Empty<byte>(), store, new DTypeArena());
        Assert.True(read.IsAbsent);
        Assert.False(read.IsNull);
        Assert.Equal(ScalarValueKind.Absent, read.Kind);
        Assert.NotEqual(store.Null(), read);
    }

    /// <summary>Three levels of <c>list_value</c>, with a mixed innermost list.</summary>
    [Fact]
    public void A_list_of_lists_three_deep_round_trips()
    {
        ScalarStore store = new ScalarStore();
        ScalarValue innermost = store.List([store.Int64(-1), store.String("x")]);
        ScalarValue middle = store.List([innermost, store.List(ReadOnlySpan<ScalarValue>.Empty)]);
        ScalarValue outer = store.List([middle]);

        ScalarValue round = RoundTrip(outer);

        Assert.Equal(outer, round);
        Assert.Equal(1, round.ListCount);
        Assert.Equal(2, round.GetListElement(0).ListCount);
        Assert.Equal(2, round.GetListElement(0).GetListElement(0).ListCount);
        Assert.Equal(-1L, round.GetListElement(0).GetListElement(0).GetListElement(0).AsInt64);
        Assert.Equal("x", Encoding.UTF8.GetString(round.GetListElement(0).GetListElement(0).GetListElement(1).AsBytes));
    }

    /// <summary>
    /// An absent element is a legal, empty <c>ScalarValue</c> message. It has to keep its slot:
    /// dropping it would renumber every element after it.
    /// </summary>
    [Fact]
    public void An_absent_list_element_keeps_its_slot()
    {
        ScalarStore store = new ScalarStore();
        ScalarValue list = store.List([store.Int64(1), store.Absent, store.Int64(3)]);

        byte[] encoded = ScalarProtobuf.SerializeValue(list);
        ScalarValue round = ScalarProtobuf.ReadValue(encoded, new ScalarStore(), new DTypeArena());

        Assert.Equal(3, round.ListCount);
        Assert.Equal(1L, round.GetListElement(0).AsInt64);
        Assert.True(round.GetListElement(1).IsAbsent);
        Assert.Equal(3L, round.GetListElement(2).AsInt64);
        Assert.Equal(list, round);
    }

    /// <summary>
    /// A variant scalar carries a whole nested <c>Scalar</c>, dtype included: the dtype half has to
    /// travel through <see cref="DTypeProtobuf"/> and land in the caller's arena.
    /// </summary>
    [Fact]
    public void A_variant_carries_a_nested_dtype()
    {
        DTypeArena sourceArena = new DTypeArena();
        ScalarStore sourceStore = new ScalarStore();
        DType nested = sourceArena.Struct(
            ["id", "name"],
            [sourceArena.Primitive(PType.I64, Nullability.NonNullable), sourceArena.Utf8(Nullability.Nullable)],
            Nullability.Nullable);
        ScalarValue variant = sourceStore.Variant(new Scalar(nested, sourceStore.String("payload")));

        DTypeArena arena = new DTypeArena();
        ScalarStore store = new ScalarStore();
        ScalarValue round = ScalarProtobuf.ReadValue(ScalarProtobuf.SerializeValue(variant), store, arena);

        Assert.Equal(ScalarValueKind.Variant, round.Kind);
        Assert.Equal(nested, round.AsVariant.DType);
        Assert.Equal("payload", Encoding.UTF8.GetString(round.AsVariant.Value.AsBytes));
        Assert.Equal(variant, round);
    }

    /// <summary>A variant whose nested value is absent: the message omits <c>Scalar.value</c>.</summary>
    [Fact]
    public void A_variant_with_an_absent_value_round_trips()
    {
        DTypeArena arena = new DTypeArena();
        ScalarStore store = new ScalarStore();
        ScalarValue variant = store.Variant(new Scalar(arena.Bool(Nullability.Nullable), store.Absent));

        ScalarValue round = ScalarProtobuf.ReadValue(
            ScalarProtobuf.SerializeValue(variant), new ScalarStore(), new DTypeArena());

        Assert.True(round.AsVariant.Value.IsAbsent);
        Assert.Equal(variant, round);
    }

    // ------------------------------------------------------------------------------- Scalar

    [Fact]
    public void A_scalar_round_trips_with_its_dtype()
    {
        DTypeArena sourceArena = new DTypeArena();
        ScalarStore sourceStore = new ScalarStore();
        Scalar scalar = new Scalar(
            sourceArena.Decimal(10, 2, Nullability.Nullable),
            sourceStore.Int64(-12345));

        DTypeArena arena = new DTypeArena();
        ScalarStore store = new ScalarStore();
        Scalar round = ScalarProtobuf.ReadScalar(ScalarProtobuf.SerializeScalar(scalar), arena, store);

        Assert.Equal(scalar.DType, round.DType);
        Assert.Equal(scalar.Value, round.Value);
        Assert.Equal(scalar, round);
    }

    /// <summary>A scalar whose value is absent is legal: the <c>value</c> field is simply omitted.</summary>
    [Fact]
    public void A_scalar_with_an_absent_value_round_trips()
    {
        DTypeArena sourceArena = new DTypeArena();
        ScalarStore sourceStore = new ScalarStore();
        Scalar scalar = new Scalar(sourceArena.Utf8(Nullability.Nullable), sourceStore.Absent);

        byte[] encoded = ScalarProtobuf.SerializeScalar(scalar);
        Scalar round = ScalarProtobuf.ReadScalar(encoded, new DTypeArena(), new ScalarStore());

        Assert.True(round.Value.IsAbsent);
        Assert.False(round.IsNull);
        Assert.Equal(scalar, round);
    }

    /// <summary>
    /// A <c>Scalar</c> with no dtype is rejected at the parse boundary. Representing it would hand
    /// the caller a <c>default(DType)</c> whose every accessor throws
    /// <see cref="InvalidOperationException"/> — the wrong exception, arbitrarily far from the file
    /// that caused it.
    /// </summary>
    [Fact]
    public void A_scalar_without_a_dtype_is_rejected()
    {
        byte[] message = new PbBuilder().LenField(2, new PbBuilder().BoolField(CaseBool, true).ToArray()).ToArray();
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadScalar(message, new DTypeArena(), new ScalarStore()));

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadScalar(Array.Empty<byte>(), new DTypeArena(), new ScalarStore()));
    }

    /// <summary>The exact bytes of a whole <c>Scalar</c>, assembled from the schema by hand.</summary>
    [Fact]
    public void A_scalar_has_the_exact_documented_encoding()
    {
        DTypeArena arena = new DTypeArena();
        ScalarStore store = new ScalarStore();
        Scalar scalar = new Scalar(arena.Primitive(PType.I32, Nullability.Nullable), store.Int64(-1));

        // Scalar.dtype = 1 -> the DType message { primitive { type: I32, nullable: true } };
        // Scalar.value = 2 -> the ScalarValue message { int64_value: zigzag(-1) = 1 }.
        byte[] expected = new PbBuilder()
            .LenField(
                1,
                new PbBuilder()
                    .LenField(DTypeCasePrimitive, new PbBuilder().VarintField(1, 6).BoolField(2, true).ToArray())
                    .ToArray())
            .LenField(2, new byte[] { 0x18, 0x01 })
            .ToArray();

        Assert.Equal(expected, ScalarProtobuf.SerializeScalar(scalar));
    }

    // ------------------------------------------------------------------------ unknown fields

    /// <summary>
    /// An unrecognized field number is skipped whatever its wire type, and the case around it
    /// still parses.
    /// </summary>
    [Fact]
    public void Unknown_fields_inside_a_scalar_value_are_skipped()
    {
        byte[] message = new PbBuilder()
            .VarintField(13, 1)                                     // just past the last defined case
            .Fixed64Field(50, 0xDEAD_BEEF_CAFE_F00D)
            .SInt64Field(CaseInt64, -42)
            .LenField(51, Encoding.UTF8.GetBytes("a future field"))
            .Fixed32Field(52, 1)
            .ToArray();

        ScalarValue read = ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.Int64, read.Kind);
        Assert.Equal(-42L, read.AsInt64);
    }

    [Fact]
    public void Unknown_fields_inside_a_list_and_a_union_are_skipped()
    {
        byte[] listBody = new PbBuilder()
            .VarintField(9, 1)
            .LenField(1, new PbBuilder().SInt64Field(CaseInt64, 5).ToArray())
            .Fixed32Field(8, 3)
            .ToArray();
        ScalarValue list = ScalarProtobuf.ReadValue(
            new PbBuilder().LenField(CaseList, listBody).ToArray(), new ScalarStore(), new DTypeArena());
        Assert.Equal(1, list.ListCount);
        Assert.Equal(5L, list.GetListElement(0).AsInt64);

        byte[] unionBody = new PbBuilder()
            .LenField(7, Encoding.UTF8.GetBytes("unknown"))
            .VarintField(1, 9)
            .LenField(2, new PbBuilder().BoolField(CaseBool, true).ToArray())
            .ToArray();
        ScalarValue union = ScalarProtobuf.ReadValue(
            new PbBuilder().LenField(CaseUnion, unionBody).ToArray(), new ScalarStore(), new DTypeArena());
        Assert.Equal(9U, union.UnionTypeId);
        Assert.True(union.UnionValue.AsBool);
    }

    // ------------------------------------------------------------------------------ last wins

    [Fact]
    public void A_repeated_oneof_case_is_last_wins()
    {
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();

        byte[] intThenString = new PbBuilder()
            .SInt64Field(CaseInt64, 7)
            .LenField(CaseString, Encoding.UTF8.GetBytes("winner"))
            .ToArray();
        ScalarValue read = ScalarProtobuf.ReadValue(intThenString, store, arena);
        Assert.Equal(ScalarValueKind.String, read.Kind);
        Assert.Equal("winner", Encoding.UTF8.GetString(read.AsBytes));

        byte[] stringThenInt = new PbBuilder()
            .LenField(CaseString, Encoding.UTF8.GetBytes("loser"))
            .SInt64Field(CaseInt64, 7)
            .ToArray();
        read = ScalarProtobuf.ReadValue(stringThenInt, store, arena);
        Assert.Equal(ScalarValueKind.Int64, read.Kind);
        Assert.Equal(7L, read.AsInt64);
    }

    /// <summary>
    /// The overridden case is never materialised, so an out-of-range <c>f16_value</c> that loses the
    /// oneof does not sink a well-formed winner.
    /// </summary>
    [Fact]
    public void An_overridden_case_is_not_materialised()
    {
        byte[] message = new PbBuilder()
            .VarintField(CaseF16, 0x1_0000)     // would be rejected if it won
            .BoolField(CaseBool, true)
            .ToArray();

        ScalarValue read = ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena());
        Assert.True(read.AsBool);
    }

    /// <summary>Null then a value: the value wins, and the result is not null.</summary>
    [Fact]
    public void A_null_case_followed_by_a_value_is_overridden()
    {
        byte[] message = new PbBuilder().VarintField(CaseNull, 0).SInt64Field(CaseInt64, 3).ToArray();
        ScalarValue read = ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena());

        Assert.False(read.IsNull);
        Assert.Equal(3L, read.AsInt64);
    }

    /// <summary>
    /// <c>google.protobuf.NullValue</c> defines only 0, but proto3 enums are open: an unrecognized
    /// number still sets the oneof case, so the scalar is still null rather than rejected.
    /// </summary>
    [Fact]
    public void An_unrecognised_null_value_enum_number_still_means_null()
    {
        byte[] message = new PbBuilder().VarintField(CaseNull, 7).ToArray();
        ScalarValue read = ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena());

        Assert.True(read.IsNull);
        Assert.False(read.IsAbsent);
    }

    // ------------------------------------------------------------------------- malformed input

    /// <summary>
    /// The field is a <c>uint64</c> but binary16 has 16 bits. Truncating would be a silent wrong
    /// value, so anything wider is malformed.
    /// </summary>
    [Theory]
    [InlineData(0x1_0000ul)]
    [InlineData(0xFFFF_FFFFul)]
    [InlineData(ulong.MaxValue)]
    public void An_f16_value_wider_than_16_bits_is_rejected(ulong bits)
    {
        byte[] message = new PbBuilder().VarintField(CaseF16, bits).ToArray();
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void The_widest_legal_f16_value_is_accepted()
    {
        byte[] message = new PbBuilder().VarintField(CaseF16, 0xFFFF).ToArray();
        Assert.Equal(
            (ushort)0xFFFF,
            ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()).F16Bits);
    }

    /// <summary>A known case arriving with the wrong framing is corrupt, not a future extension.</summary>
    [Theory]
    [InlineData(CaseBool, ProtoWireType.LengthDelimited)]
    [InlineData(CaseInt64, ProtoWireType.Fixed64)]
    [InlineData(CaseF32, ProtoWireType.Varint)]
    [InlineData(CaseF64, ProtoWireType.Fixed32)]
    [InlineData(CaseString, ProtoWireType.Varint)]
    [InlineData(CaseList, ProtoWireType.Varint)]
    [InlineData(CaseF16, ProtoWireType.Fixed32)]
    [InlineData(CaseVariant, ProtoWireType.Fixed64)]
    [InlineData(CaseUnion, ProtoWireType.Varint)]
    [InlineData(CaseNull, ProtoWireType.LengthDelimited)]
    internal void A_case_with_the_wrong_wire_type_is_rejected(int caseNumber, ProtoWireType wire)
    {
        PbBuilder builder = new PbBuilder().Tag(caseNumber, wire);
        switch (wire)
        {
            case ProtoWireType.Varint: builder.Varint(1); break;
            case ProtoWireType.Fixed32: builder.Raw([0, 0, 0, 0]); break;
            case ProtoWireType.Fixed64: builder.Raw([0, 0, 0, 0, 0, 0, 0, 0]); break;
            default: builder.Varint(0); break;
        }

        byte[] message = builder.ToArray();
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void A_group_tag_is_rejected()
    {
        byte[] message = new PbBuilder().Tag(CaseBool, ProtoWireType.StartGroup).ToArray();
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void A_truncated_value_is_rejected()
    {
        ScalarStore store = new ScalarStore();
        byte[] full = ScalarProtobuf.SerializeValue(store.String("a reasonably long string value"));
        for (int keep = 1; keep < full.Length; keep++)
        {
            byte[] truncated = full.AsSpan(0, keep).ToArray();
            Assert.Throws<VortexFormatException>(
                () => ScalarProtobuf.ReadValue(truncated, new ScalarStore(), new DTypeArena()));
        }
    }

    /// <summary>A nested element length that escapes the enclosing list body must be caught first.</summary>
    [Fact]
    public void A_list_element_length_that_escapes_the_body_is_rejected()
    {
        // ListValue body claims a 100-byte element but supplies one byte.
        byte[] listBody = [0x0A, 0x64, 0x00];
        byte[] message = new PbBuilder().LenField(CaseList, listBody).ToArray();

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    /// <summary><c>UnionValue.type_id</c> is a <c>uint32</c>: out of domain above 2^32-1.</summary>
    [Fact]
    public void A_union_type_id_wider_than_uint32_is_rejected()
    {
        byte[] unionBody = new PbBuilder().VarintField(1, 0x1_0000_0000ul).ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, unionBody).ToArray();

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    /// <summary>An eleven-byte varint is unrepresentable and must be rejected, not wrapped.</summary>
    [Fact]
    public void An_oversized_varint_is_rejected()
    {
        byte[] message = [0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    /// <summary>
    /// A variant's nested dtype travels through <see cref="DTypeProtobuf"/>, and a malformed one
    /// must surface as a format error from there rather than as a half-built scalar.
    /// </summary>
    [Fact]
    public void A_variant_carrying_a_malformed_dtype_is_rejected()
    {
        // A DType message that sets no case at all.
        byte[] scalarBody = new PbBuilder()
            .LenField(1, Array.Empty<byte>())
            .LenField(2, new PbBuilder().BoolField(CaseBool, true).ToArray())
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseVariant, scalarBody).ToArray();

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));

        // ...and one whose PType tag is undefined.
        byte[] badPrimitive = new PbBuilder()
            .LenField(DTypeCasePrimitive, new PbBuilder().VarintField(1, 250).ToArray())
            .ToArray();
        byte[] withBadPType = new PbBuilder()
            .LenField(CaseVariant, new PbBuilder().LenField(1, badPrimitive).ToArray())
            .ToArray();

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(withBadPType, new ScalarStore(), new DTypeArena()));
    }

    /// <summary>
    /// A union alternative whose payload is itself malformed is rejected, not silently reduced to
    /// an absent value.
    /// </summary>
    [Fact]
    public void A_union_carrying_a_malformed_value_is_rejected()
    {
        byte[] unionBody = new PbBuilder()
            .VarintField(1, 2)
            .LenField(2, new PbBuilder().VarintField(CaseF16, 0x1_0000).ToArray())
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, unionBody).ToArray();

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(message, new ScalarStore(), new DTypeArena()));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ScalarProtobuf.ReadValue(Array.Empty<byte>(), null!, new DTypeArena()));
        Assert.Throws<ArgumentNullException>(
            () => ScalarProtobuf.ReadValue(Array.Empty<byte>(), new ScalarStore(), null!));
        Assert.Throws<ArgumentNullException>(
            () => ScalarProtobuf.ReadScalar(Array.Empty<byte>(), null!, new ScalarStore()));
        Assert.Throws<ArgumentNullException>(
            () => ScalarProtobuf.ReadScalar(Array.Empty<byte>(), new DTypeArena(), null!));
    }

    [Fact]
    public void Writing_a_scalar_with_a_default_dtype_is_an_argument_error()
    {
        ScalarStore store = new ScalarStore();
        Scalar scalar = new Scalar(default, store.Bool(true));
        Assert.Throws<ArgumentException>(() => ScalarProtobuf.SerializeScalar(scalar));
    }

    // ----------------------------------------------------------------------------- depth cap

    /// <summary>
    /// Nesting through <c>list_value</c> is capped at <see cref="VortexLimits.MaxDTypeDepth"/>, and
    /// the cap fires on the way down: a 5000-deep message costs 64 frames, not 5000.
    /// </summary>
    [Fact]
    public void List_nesting_is_accepted_up_to_the_cap_and_rejected_beyond_it()
    {
        ScalarValue read = ScalarProtobuf.ReadValue(
            NestedLists(VortexLimits.MaxDTypeDepth - 1), new ScalarStore(), new DTypeArena());
        Assert.Equal(ScalarValueKind.List, read.Kind);

        byte[] overCap = NestedLists(VortexLimits.MaxDTypeDepth);
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(overCap, new ScalarStore(), new DTypeArena()));

        byte[] pathological = NestedLists(5000);
        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(pathological, new ScalarStore(), new DTypeArena()));
    }

    /// <summary>The same cap has to cover the other two recursive cases.</summary>
    [Fact]
    public void Union_and_variant_nesting_are_capped_too()
    {
        byte[] unions = new PbBuilder().BoolField(CaseBool, true).ToArray();
        for (int i = 0; i < 200; i++)
        {
            byte[] body = new PbBuilder().VarintField(1, 1).LenField(2, unions).ToArray();
            unions = new PbBuilder().LenField(CaseUnion, body).ToArray();
        }

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(unions, new ScalarStore(), new DTypeArena()));

        byte[] dtype = new PbBuilder().LenField(DTypeCasePrimitive, Array.Empty<byte>()).ToArray();
        byte[] variants = new PbBuilder().BoolField(CaseBool, true).ToArray();
        for (int i = 0; i < 200; i++)
        {
            byte[] scalarBody = new PbBuilder().LenField(1, dtype).LenField(2, variants).ToArray();
            variants = new PbBuilder().LenField(CaseVariant, scalarBody).ToArray();
        }

        Assert.Throws<VortexFormatException>(
            () => ScalarProtobuf.ReadValue(variants, new ScalarStore(), new DTypeArena()));
    }

    // ------------------------------------------------------------------------------ write API

    /// <summary>
    /// <c>WriteValueField</c> emits the tag and a zero length even for an absent value: inside a
    /// <c>ListValue</c> that empty message is the element's slot.
    /// </summary>
    [Fact]
    public void WriteValueField_emits_an_empty_message_for_an_absent_value()
    {
        ScalarStore store = new ScalarStore();
        ProtoWriter writer = new ProtoWriter(32);
        byte[] encoded;
        try
        {
            ScalarProtobuf.WriteValueField(ref writer, 4, store.Absent);
            encoded = writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        Assert.Equal(new byte[] { 0x22, 0x00 }, encoded);
    }

    [Fact]
    public void WriteValue_appends_to_a_caller_owned_writer()
    {
        ScalarStore store = new ScalarStore();
        ProtoWriter writer = new ProtoWriter(32);
        byte[] encoded;
        try
        {
            writer.WriteUInt32Always(1, 1);
            ScalarProtobuf.WriteValue(ref writer, store.Bool(true));
            encoded = writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        Assert.Equal(new byte[] { 0x08, 0x01, 0x10, 0x01 }, encoded);
    }

    // -------------------------------------------------------------------------------- helpers

    private static ScalarValue RoundTrip(ScalarValue value) =>
        ScalarProtobuf.ReadValue(ScalarProtobuf.SerializeValue(value), new ScalarStore(), new DTypeArena());

    private static void AssertRoundTrip(ScalarValue value)
    {
        ScalarValue round = RoundTrip(value);
        Assert.Equal(value.Kind, round.Kind);
        Assert.Equal(value, round);
        Assert.Equal(value.GetHashCode(), round.GetHashCode());
        Assert.Equal(value.ToString(), round.ToString());
        Assert.Equal(ScalarProtobuf.SerializeValue(value), ScalarProtobuf.SerializeValue(round));
    }

    /// <summary>A ScalarValue message of <paramref name="wrappers"/> nested lists over a bool.</summary>
    private static byte[] NestedLists(int wrappers)
    {
        byte[] inner = new PbBuilder().BoolField(CaseBool, true).ToArray();
        for (int i = 0; i < wrappers; i++)
        {
            byte[] listBody = new PbBuilder().LenField(1, inner).ToArray();
            inner = new PbBuilder().LenField(CaseList, listBody).ToArray();
        }

        return inner;
    }
}
