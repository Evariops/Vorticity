// Adversarial tests for the DType Protobuf codec. A round trip against our own writer proves
// almost nothing about a transcription -- it agrees with whatever mistake the writer also makes --
// so the interesting cases here are hand-assembled wire bytes, malformed framing, and the
// narrowing boundaries that separate the proto types from the model's.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Types.Serialization;

/// <summary>
/// Builds Protobuf wire bytes by hand, so a test can assert against an encoding the codec did not
/// produce. Shared by <see cref="DTypeProtobufTests"/> and <see cref="ScalarProtobufTests"/>.
/// </summary>
internal sealed class PbBuilder
{
    private readonly List<byte> _bytes = new List<byte>();

    internal int Length => _bytes.Count;

    internal PbBuilder Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _bytes.Add((byte)value);
        return this;
    }

    internal PbBuilder Tag(int fieldNumber, ProtoWireType wire) =>
        Varint(((ulong)(uint)fieldNumber << 3) | (byte)wire);

    internal PbBuilder Raw(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            _bytes.Add(bytes[i]);
        }

        return this;
    }

    internal PbBuilder VarintField(int fieldNumber, ulong value) =>
        Tag(fieldNumber, ProtoWireType.Varint).Varint(value);

    /// <summary>A proto <c>int32</c>/<c>enum</c> field: two's complement, ten bytes when negative.</summary>
    internal PbBuilder Int32Field(int fieldNumber, int value) =>
        Tag(fieldNumber, ProtoWireType.Varint).Varint(unchecked((ulong)(long)value));

    /// <summary>A proto <c>sint64</c> field: ZigZag.</summary>
    internal PbBuilder SInt64Field(int fieldNumber, long value) =>
        Tag(fieldNumber, ProtoWireType.Varint).Varint(unchecked((ulong)((value << 1) ^ (value >> 63))));

    internal PbBuilder BoolField(int fieldNumber, bool value) =>
        VarintField(fieldNumber, value ? 1UL : 0UL);

    internal PbBuilder LenField(int fieldNumber, ReadOnlySpan<byte> body) =>
        Tag(fieldNumber, ProtoWireType.LengthDelimited).Varint((ulong)body.Length).Raw(body);

    internal PbBuilder StringField(int fieldNumber, string value) =>
        LenField(fieldNumber, Encoding.UTF8.GetBytes(value));

    internal PbBuilder Fixed32Field(int fieldNumber, uint bits)
    {
        Tag(fieldNumber, ProtoWireType.Fixed32);
        for (int i = 0; i < 4; i++)
        {
            _bytes.Add((byte)(bits >> (8 * i)));
        }

        return this;
    }

    internal PbBuilder Fixed64Field(int fieldNumber, ulong bits)
    {
        Tag(fieldNumber, ProtoWireType.Fixed64);
        for (int i = 0; i < 8; i++)
        {
            _bytes.Add((byte)(bits >> (8 * i)));
        }

        return this;
    }

    internal byte[] ToArray() => _bytes.ToArray();
}

public sealed class DTypeProtobufTests
{
    // spec/proto/dtype.proto oneof case numbers, spelled out so a test never borrows the codec's
    // own constants (which is how a renumbering would pass unnoticed).
    private const int CaseNull = 1;
    private const int CaseBool = 2;
    private const int CasePrimitive = 3;
    private const int CaseDecimal = 4;
    private const int CaseUtf8 = 5;
    private const int CaseBinary = 6;
    private const int CaseStruct = 7;
    private const int CaseList = 8;
    private const int CaseExtension = 9;
    private const int CaseFixedSizeList = 10;
    private const int CaseVariant = 11;
    private const int CaseUnion = 12;
    private const int CaseMap = 13;

    // ------------------------------------------------------------------ hand-written wire vectors

    /// <summary>
    /// The exact bytes for <c>primitive { type: I32, nullable: true }</c>, checked against
    /// spec/proto/dtype.proto by hand: case 3 length-delimited, then <c>type = 1</c> varint 6 and
    /// <c>nullable = 2</c> varint 1. If the codec ever renumbers a field this is the test that
    /// notices, because it does not consult the codec for the expected bytes.
    /// </summary>
    [Fact]
    public void Primitive_i32_nullable_has_the_exact_documented_encoding()
    {
        DTypeArena arena = new DTypeArena();
        byte[] encoded = DTypeProtobuf.Serialize(arena.Primitive(PType.I32, Nullability.Nullable));

        Assert.Equal(new byte[] { 0x1A, 0x04, 0x08, 0x06, 0x10, 0x01 }, encoded);
    }

    /// <summary>
    /// proto3 implicit presence: a field equal to its default is omitted. <c>u8</c> is PType 0 and
    /// non-nullable is <c>false</c>, so the whole Primitive body encodes to nothing.
    /// </summary>
    [Fact]
    public void Primitive_u8_nonnullable_encodes_to_an_empty_case_message()
    {
        DTypeArena arena = new DTypeArena();
        byte[] encoded = DTypeProtobuf.Serialize(arena.Primitive(PType.U8, Nullability.NonNullable));

        Assert.Equal(new byte[] { 0x1A, 0x00 }, encoded);
        Assert.Equal(arena.Primitive(PType.U8, Nullability.NonNullable), DTypeProtobuf.Read(encoded, new DTypeArena()));
    }

    /// <summary><c>message Null {}</c> has no fields, so the case message is empty.</summary>
    [Fact]
    public void Null_encodes_to_an_empty_case_message()
    {
        DTypeArena arena = new DTypeArena();
        Assert.Equal(new byte[] { 0x0A, 0x00 }, DTypeProtobuf.Serialize(arena.Null(Nullability.Nullable)));
    }

    /// <summary>
    /// <c>Union.type_ids</c> is <c>repeated int32</c>, which proto3 packs by default: one
    /// length-delimited field holding the concatenated varints, not one field per element.
    /// </summary>
    [Fact]
    public void Union_type_ids_are_written_packed()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType u8 = arena.Primitive(PType.U8, Nullability.NonNullable);
        int[] names = [arena.InternName("a"), arena.InternName("b")];
        DType union = arena.Union(
            names,
            [i32, u8],
            [0, 7],
            Nullability.NonNullable);

        byte[] encoded = DTypeProtobuf.Serialize(union);

        // case 12 -> tag 0x62. Inside: names "a","b" (field 1), two dtypes (field 2),
        // then the packed type_ids: tag 0x1A, length 2, values 0x00 0x07.
        byte[] expected = new PbBuilder()
            .LenField(
                CaseUnion,
                new PbBuilder()
                    .StringField(1, "a")
                    .StringField(1, "b")
                    .LenField(2, new PbBuilder().LenField(CasePrimitive, new PbBuilder().VarintField(1, 6).ToArray()).ToArray())
                    .LenField(2, new PbBuilder().LenField(CasePrimitive, Array.Empty<byte>()).ToArray())
                    .LenField(3, new byte[] { 0x00, 0x07 })
                    .ToArray())
            .ToArray();

        Assert.Equal(expected, encoded);
    }

    /// <summary>
    /// Every composite case, written out byte for byte. A round trip cannot see field <em>order</em>
    /// at all — the reader accepts any permutation — but prost emits fields in field-number order,
    /// and the conformance corpus compares bytes. This is the test that pins the order, and with it
    /// the field number of every member of every composite message.
    /// </summary>
    [Fact]
    public void Composite_cases_are_written_in_field_number_order()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType i64Nullable = arena.Primitive(PType.I64, Nullability.Nullable);
        DType utf8 = arena.Utf8(Nullability.NonNullable);
        DType utf8Nullable = arena.Utf8(Nullability.Nullable);

        byte[] i32Message = new PbBuilder().LenField(CasePrimitive, new PbBuilder().VarintField(1, 6).ToArray()).ToArray();
        byte[] i64NullableMessage = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, 7).BoolField(2, true).ToArray()).ToArray();
        byte[] utf8Message = new PbBuilder().LenField(CaseUtf8, Array.Empty<byte>()).ToArray();
        byte[] utf8NullableMessage = new PbBuilder()
            .LenField(CaseUtf8, new PbBuilder().BoolField(1, true).ToArray()).ToArray();

        // Struct: names (1) ... then dtypes (2) ... then nullable (3).
        Assert.Equal(
            new PbBuilder()
                .LenField(
                    CaseStruct,
                    new PbBuilder()
                        .StringField(1, "a")
                        .StringField(1, "b")
                        .LenField(2, i32Message)
                        .LenField(2, utf8NullableMessage)
                        .ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.Struct(["a", "b"], [i32, utf8Nullable], Nullability.NonNullable)));

        // Map: key_type (1), value_type (2), keys_sorted (3), nullable (4).
        Assert.Equal(
            new PbBuilder()
                .LenField(
                    CaseMap,
                    new PbBuilder()
                        .LenField(1, utf8Message)
                        .LenField(2, i64NullableMessage)
                        .BoolField(3, true)
                        .BoolField(4, true)
                        .ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.Map(utf8, i64Nullable, keysSorted: true, Nullability.Nullable)));

        // Extension: id (1), storage_dtype (2), metadata (3). No nullable field at all.
        Assert.Equal(
            new PbBuilder()
                .LenField(
                    CaseExtension,
                    new PbBuilder()
                        .StringField(1, "vortex.date")
                        .LenField(2, i32Message)
                        .LenField(3, [0xAA])
                        .ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.Extension("vortex.date", i32, [0xAA])));

        // FixedSizeList: element_type (1), size (2), nullable (3).
        Assert.Equal(
            new PbBuilder()
                .LenField(
                    CaseFixedSizeList,
                    new PbBuilder().LenField(1, i32Message).VarintField(2, 3).ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.FixedSizeList(i32, 3, Nullability.NonNullable)));

        // List: element_type (1), nullable (2).
        Assert.Equal(
            new PbBuilder()
                .LenField(CaseList, new PbBuilder().LenField(1, i32Message).BoolField(2, true).ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.List(i32, Nullability.Nullable)));

        // Decimal: precision (1) uint32, scale (2) int32 (ten bytes when negative), nullable (3).
        Assert.Equal(
            new PbBuilder()
                .LenField(
                    CaseDecimal,
                    new PbBuilder().VarintField(1, 10).Int32Field(2, -2).BoolField(3, true).ToArray())
                .ToArray(),
            DTypeProtobuf.Serialize(arena.Decimal(10, -2, Nullability.Nullable)));
    }

    // ---------------------------------------------------------------------------- round trips

    [Fact]
    public void Every_leaf_kind_round_trips_in_both_nullabilities()
    {
        DTypeArena source = new DTypeArena();
        AssertRoundTrip(source.Null(Nullability.Nullable));
        AssertRoundTrip(source.Bool(Nullability.Nullable));
        AssertRoundTrip(source.Bool(Nullability.NonNullable));
        AssertRoundTrip(source.Utf8(Nullability.Nullable));
        AssertRoundTrip(source.Utf8(Nullability.NonNullable));
        AssertRoundTrip(source.Binary(Nullability.Nullable));
        AssertRoundTrip(source.Binary(Nullability.NonNullable));
        AssertRoundTrip(source.Variant(Nullability.Nullable));
        AssertRoundTrip(source.Variant(Nullability.NonNullable));
    }

    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    [InlineData(PType.I8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.F16)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    public void Every_ptype_round_trips(PType ptype)
    {
        DTypeArena source = new DTypeArena();
        AssertRoundTrip(source.Primitive(ptype, Nullability.Nullable));
        AssertRoundTrip(source.Primitive(ptype, Nullability.NonNullable));
    }

    /// <summary>Includes the boundaries the arena admits, and a negative scale.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 2)]
    [InlineData(38, 38)]
    [InlineData(38, -38)]
    [InlineData(19, -1)]
    public void Decimal_round_trips(int precision, int scale)
    {
        DTypeArena source = new DTypeArena();
        AssertRoundTrip(source.Decimal((byte)precision, (sbyte)scale, Nullability.Nullable));
        AssertRoundTrip(source.Decimal((byte)precision, (sbyte)scale, Nullability.NonNullable));
    }

    [Fact]
    public void A_nested_struct_round_trips()
    {
        DTypeArena source = new DTypeArena();
        DType inner = source.Struct(
            ["c", "d"],
            [source.Utf8(Nullability.Nullable), source.Binary(Nullability.NonNullable)],
            Nullability.NonNullable);
        DType outer = source.Struct(
            ["a", "b"],
            [source.Primitive(PType.I32, Nullability.NonNullable), inner],
            Nullability.Nullable);

        AssertRoundTrip(outer);
    }

    /// <summary>
    /// A struct field may legitimately be named the empty string. <c>WriteStringUtf8</c> would omit
    /// it under proto3 implicit presence and silently shift the names/dtypes pairing by one, so the
    /// codec has to use the explicit-presence writer for a repeated field.
    /// </summary>
    [Fact]
    public void A_struct_with_an_empty_field_name_keeps_its_pairing()
    {
        DTypeArena source = new DTypeArena();
        DType s = source.Struct(
            ["", "after"],
            [source.Primitive(PType.I64, Nullability.NonNullable), source.Utf8(Nullability.Nullable)],
            Nullability.NonNullable);

        DType round = RoundTrip(s);
        Assert.Equal(2, round.FieldCount);
        Assert.Equal(string.Empty, round.GetFieldName(0));
        Assert.Equal("after", round.GetFieldName(1));
        Assert.Equal(s, round);
    }

    [Fact]
    public void An_empty_struct_round_trips()
    {
        DTypeArena source = new DTypeArena();
        AssertRoundTrip(source.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.Nullable));
    }

    [Fact]
    public void A_union_round_trips_with_its_type_ids()
    {
        DTypeArena source = new DTypeArena();
        int[] names = [source.InternName("small"), source.InternName("large"), source.InternName("text")];
        DType union = source.Union(
            names,
            [
                source.Primitive(PType.I8, Nullability.NonNullable),
                source.Primitive(PType.I64, Nullability.Nullable),
                source.Utf8(Nullability.NonNullable)
            ],
            [0, 255, 7],
            Nullability.Nullable);

        DType round = RoundTrip(union);
        Assert.Equal(union, round);
        Assert.Equal(0, round.GetTypeId(0));
        Assert.Equal(255, round.GetTypeId(1));
        Assert.Equal(7, round.GetTypeId(2));
    }

    [Fact]
    public void A_map_round_trips_in_both_key_orders()
    {
        DTypeArena source = new DTypeArena();
        DType keys = source.Utf8(Nullability.NonNullable);
        DType values = source.Primitive(PType.I64, Nullability.Nullable);
        AssertRoundTrip(source.Map(keys, values, keysSorted: true, Nullability.NonNullable));
        AssertRoundTrip(source.Map(keys, values, keysSorted: false, Nullability.Nullable));

        // Key and value are distinguishable: a codec that swapped fields 1 and 2 would pass every
        // symmetric test above.
        DType asymmetric = source.Map(
            source.Primitive(PType.I32, Nullability.NonNullable),
            source.Utf8(Nullability.Nullable),
            keysSorted: false,
            Nullability.NonNullable);
        DType round = RoundTrip(asymmetric);
        Assert.Equal(DTypeKind.Primitive, round.KeyType.Kind);
        Assert.Equal(DTypeKind.Utf8, round.ValueType.Kind);
    }

    [Fact]
    public void An_extension_round_trips_with_and_without_metadata()
    {
        DTypeArena source = new DTypeArena();
        DType storage = source.Primitive(PType.I32, Nullability.NonNullable);

        DType withMeta = source.Extension("vortex.date", storage, [0x01, 0x02, 0x03]);
        DType round = RoundTrip(withMeta);
        Assert.Equal("vortex.date", round.ExtensionId);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, round.ExtensionMetadata.ToArray());
        Assert.Equal(withMeta, round);

        AssertRoundTrip(source.Extension("vortex.time", storage, ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// An extension has no <c>nullable</c> field of its own: it reports its storage dtype's. The
    /// round trip has to preserve that rather than default it to non-nullable.
    /// </summary>
    [Fact]
    public void An_extension_inherits_the_storage_nullability_across_a_round_trip()
    {
        DTypeArena source = new DTypeArena();
        DType ext = source.Extension(
            "vortex.date",
            source.Primitive(PType.I32, Nullability.Nullable),
            ReadOnlySpan<byte>.Empty);

        DType round = RoundTrip(ext);
        Assert.True(round.IsNullable);
        Assert.True(round.StorageType.IsNullable);
    }

    [Fact]
    public void Lists_and_fixed_size_lists_round_trip()
    {
        DTypeArena source = new DTypeArena();
        DType element = source.Primitive(PType.F32, Nullability.Nullable);
        AssertRoundTrip(source.List(element, Nullability.Nullable));
        AssertRoundTrip(source.List(element, Nullability.NonNullable));
        AssertRoundTrip(source.FixedSizeList(element, 3, Nullability.NonNullable));

        // size 0 is the proto3 default and is therefore omitted from the wire; it must still read
        // back as 0 rather than as "absent".
        AssertRoundTrip(source.FixedSizeList(element, 0, Nullability.Nullable));
        AssertRoundTrip(source.FixedSizeList(element, uint.MaxValue, Nullability.NonNullable));
    }

    /// <summary>
    /// A deterministic sweep over shapes hand-written cases would not think of. Seeded, so a
    /// failure reproduces exactly.
    /// </summary>
    [Fact]
    public void A_seeded_sweep_of_generated_dtypes_round_trips()
    {
        DTypeArena source = new DTypeArena();
        uint state = 0x5EED_1234;
        for (int i = 0; i < 400; i++)
        {
            AssertRoundTrip(Generate(source, ref state, 3));
        }
    }

    // ------------------------------------------------------------------------ unknown fields

    /// <summary>
    /// The read-forever promise (docs/02-format.md section 5.3): an unrecognized field number is
    /// skipped whatever its wire type, and the fields around it still parse.
    /// </summary>
    [Fact]
    public void Unknown_fields_around_the_case_are_skipped()
    {
        byte[] message = new PbBuilder()
            .VarintField(100, 0xDEAD_BEEF)
            .Fixed64Field(101, 0x0102_0304_0506_0708)
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, (ulong)PType.I32).BoolField(2, true).ToArray())
            .LenField(102, Encoding.UTF8.GetBytes("an unknown string field"))
            .Fixed32Field(103, 0xCAFE_F00D)
            .VarintField(536870911, 1)     // the largest legal field number
            .ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Equal(arena.Primitive(PType.I32, Nullability.Nullable), DTypeProtobuf.Read(message, arena));
    }

    /// <summary>Unknown fields inside a case message are skipped too, not just around it.</summary>
    [Fact]
    public void Unknown_fields_inside_a_case_message_are_skipped()
    {
        byte[] primitive = new PbBuilder()
            .LenField(50, Encoding.UTF8.GetBytes("future field"))
            .VarintField(1, (ulong)PType.F64)
            .Fixed32Field(51, 7)
            .BoolField(2, true)
            .VarintField(52, 9)
            .ToArray();
        byte[] message = new PbBuilder().LenField(CasePrimitive, primitive).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Equal(arena.Primitive(PType.F64, Nullability.Nullable), DTypeProtobuf.Read(message, arena));
    }

    /// <summary>
    /// A field number outside 1..13 is an unknown field, not an undefined oneof case: it is skipped,
    /// and the message is then rejected for setting no case at all.
    /// </summary>
    [Fact]
    public void An_out_of_range_case_number_leaves_the_oneof_unset()
    {
        byte[] message = new PbBuilder().LenField(14, Array.Empty<byte>()).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    // ------------------------------------------------------------------------------ last wins

    /// <summary>
    /// proto3 oneof semantics: when a case repeats on the wire the last one wins. This must not
    /// throw -- a conforming encoder that merged two messages produces exactly this.
    /// </summary>
    [Fact]
    public void A_repeated_oneof_case_is_last_wins()
    {
        DTypeArena arena = new DTypeArena();

        byte[] boolThenPrimitive = new PbBuilder()
            .LenField(CaseBool, new PbBuilder().BoolField(1, true).ToArray())
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, (ulong)PType.I16).ToArray())
            .ToArray();
        Assert.Equal(
            arena.Primitive(PType.I16, Nullability.NonNullable),
            DTypeProtobuf.Read(boolThenPrimitive, arena));

        byte[] primitiveThenBool = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, (ulong)PType.I16).ToArray())
            .LenField(CaseBool, new PbBuilder().BoolField(1, true).ToArray())
            .ToArray();
        Assert.Equal(arena.Bool(Nullability.Nullable), DTypeProtobuf.Read(primitiveThenBool, arena));
    }

    /// <summary>
    /// The overridden case is never parsed, so a malformed loser does not sink a well-formed
    /// winner. Deferring the body is also what stops a hostile file from making the arena build a
    /// thousand discarded struct nodes.
    /// </summary>
    [Fact]
    public void An_overridden_case_body_is_not_parsed()
    {
        // A Primitive whose `type` is the undefined tag 200 -- fatal if it were parsed.
        byte[] message = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, 200).ToArray())
            .LenField(CaseUtf8, new PbBuilder().BoolField(1, true).ToArray())
            .ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Equal(arena.Utf8(Nullability.Nullable), DTypeProtobuf.Read(message, arena));
    }

    // ------------------------------------------------------------------------- malformed input

    [Fact]
    public void An_empty_message_sets_no_case_and_is_rejected()
    {
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(Array.Empty<byte>(), arena));
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(ReadOnlySpan<byte>.Empty, arena));
    }

    [Fact]
    public void A_message_of_only_unknown_fields_is_rejected()
    {
        byte[] message = new PbBuilder().VarintField(99, 1).Fixed32Field(98, 2).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>Every oneof arm is a message type, so a varint-framed case is corrupt framing.</summary>
    [Fact]
    public void A_case_field_with_the_wrong_wire_type_is_rejected()
    {
        byte[] message = new PbBuilder().VarintField(CasePrimitive, 6).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    [Fact]
    public void A_known_inner_field_with_the_wrong_wire_type_is_rejected()
    {
        // Primitive.nullable is a varint; here it arrives length-delimited.
        byte[] primitive = new PbBuilder().LenField(2, new byte[] { 1 }).ToArray();
        byte[] message = new PbBuilder().LenField(CasePrimitive, primitive).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>proto3 never emits a group; wire types 3 and 4 have no skippable framing.</summary>
    [Fact]
    public void A_group_tag_is_rejected()
    {
        byte[] message = new PbBuilder().Tag(5, ProtoWireType.StartGroup).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    [Theory]
    [InlineData(1)]    // the case tag alone
    [InlineData(2)]    // tag + length, no body
    [InlineData(4)]    // half the body
    public void A_truncated_message_is_rejected(int keep)
    {
        DTypeArena arena = new DTypeArena();
        byte[] full = DTypeProtobuf.Serialize(arena.Primitive(PType.I32, Nullability.Nullable));
        byte[] truncated = full.AsSpan(0, keep).ToArray();

        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(truncated, arena));
    }

    /// <summary>A nested length that escapes the enclosing body must be caught before the read.</summary>
    [Fact]
    public void A_nested_length_that_escapes_the_body_is_rejected()
    {
        // Case 3, declared body length 100, but only two bytes follow.
        byte[] message = [0x1A, 0x64, 0x08, 0x06];
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(255)]
    [InlineData(int.MaxValue)]
    public void An_undefined_ptype_is_rejected(int rawType)
    {
        byte[] message = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, (ulong)(uint)rawType).ToArray())
            .ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>A proto enum is an int32, so a negative tag arrives as a ten-byte varint.</summary>
    [Fact]
    public void A_negative_ptype_is_rejected()
    {
        byte[] message = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().Int32Field(1, -1).ToArray())
            .ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    // -------------------------------------------------------------------- narrowing boundaries

    /// <summary>
    /// <c>Decimal.precision</c> is a <c>uint32</c> on the wire and a <c>uint8</c> in the model, so
    /// the range test has to happen <em>before</em> the narrowing. 266 is the case that proves it:
    /// it truncates to 10, a perfectly legal precision, so a codec that cast first and validated
    /// afterwards would accept the file and return <c>decimal(10, ...)</c> — a wrong answer rather
    /// than an error. The arena's own range check cannot catch this one.
    /// </summary>
    [Theory]
    [InlineData(256u)]
    [InlineData(266u)]              // truncates to a legal 10
    [InlineData(300u)]
    [InlineData(65536u + 38u)]      // truncates to a legal 38
    [InlineData(uint.MaxValue)]
    public void A_decimal_precision_that_does_not_fit_in_a_byte_is_rejected(uint precision)
    {
        byte[] message = new PbBuilder()
            .LenField(CaseDecimal, new PbBuilder().VarintField(1, precision).ToArray())
            .ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>
    /// <c>Decimal.scale</c> is an <c>int32</c> on the wire and an <c>int8</c> in the model. Same
    /// trap as precision, and the same three witnesses: 258 truncates to 2,
    /// <see cref="int.MaxValue"/> to -1 and <see cref="int.MinValue"/> to 0, all legal scales for
    /// precision 10.
    /// </summary>
    [Theory]
    [InlineData(128)]
    [InlineData(200)]
    [InlineData(258)]               // truncates to a legal 2
    [InlineData(int.MaxValue)]      // truncates to a legal -1
    [InlineData(-129)]
    [InlineData(-200)]
    [InlineData(-254)]              // truncates to a legal 2
    [InlineData(int.MinValue)]      // truncates to a legal 0
    public void A_decimal_scale_that_does_not_fit_in_an_sbyte_is_rejected(int scale)
    {
        byte[] message = new PbBuilder()
            .LenField(CaseDecimal, new PbBuilder().VarintField(1, 10).Int32Field(2, scale).ToArray())
            .ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>Precision 0 fits in a byte but is not a legal decimal; the arena rejects it.</summary>
    [Fact]
    public void A_decimal_precision_of_zero_is_rejected()
    {
        byte[] message = new PbBuilder().LenField(CaseDecimal, Array.Empty<byte>()).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>
    /// The schema comment on <c>Union.type_ids</c> says each value must fit in <c>uint8</c>. The
    /// check is an unsigned compare, so a negative int32 is rejected by the same branch.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(65536)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_union_type_id_outside_uint8_is_rejected(int typeId)
    {
        byte[] union = new PbBuilder()
            .StringField(1, "a")
            .LenField(2, new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray())
            .LenField(3, new PbBuilder().Varint(unchecked((ulong)(long)typeId)).ToArray())   // packed payload
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, union).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>
    /// A repeated scalar may arrive unpacked -- one varint field per element -- and stays legal
    /// forever. A reader that only understood the packed form would reject a valid file.
    /// </summary>
    [Fact]
    public void Union_type_ids_are_accepted_unpacked()
    {
        byte[] union = new PbBuilder()
            .StringField(1, "a")
            .StringField(1, "b")
            .LenField(2, new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray())
            .LenField(2, new PbBuilder().LenField(CaseUtf8, Array.Empty<byte>()).ToArray())
            .VarintField(3, 4)
            .VarintField(3, 9)
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, union).ToArray();

        DTypeArena arena = new DTypeArena();
        DType read = DTypeProtobuf.Read(message, arena);

        Assert.Equal(DTypeKind.Union, read.Kind);
        Assert.Equal(2, read.FieldCount);
        Assert.Equal(4, read.GetTypeId(0));
        Assert.Equal(9, read.GetTypeId(1));
    }

    /// <summary>A packed payload that ends mid-varint is truncated input, not an empty list.</summary>
    [Fact]
    public void A_truncated_packed_type_id_payload_is_rejected()
    {
        byte[] union = new PbBuilder()
            .LenField(3, new byte[] { 0x80 })      // continuation bit set, nothing follows
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, union).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    // ------------------------------------------------------------------------ repeated pairing

    /// <summary>
    /// <c>names</c> and <c>dtypes</c> pair by ordinal, not by adjacency. A conforming encoder is
    /// free to interleave them, and a decoder that assumed one contiguous run of each would mispair
    /// every field.
    /// </summary>
    [Fact]
    public void Struct_names_and_dtypes_may_be_interleaved()
    {
        byte[] body = new PbBuilder()
            .StringField(1, "a")
            .LenField(2, new PbBuilder().LenField(CasePrimitive, new PbBuilder().VarintField(1, (ulong)PType.I32).ToArray()).ToArray())
            .StringField(1, "b")
            .LenField(2, new PbBuilder().LenField(CaseUtf8, new PbBuilder().BoolField(1, true).ToArray()).ToArray())
            .BoolField(3, true)
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseStruct, body).ToArray();

        DTypeArena arena = new DTypeArena();
        DType read = DTypeProtobuf.Read(message, arena);

        DType expected = arena.Struct(
            ["a", "b"],
            [arena.Primitive(PType.I32, Nullability.NonNullable), arena.Utf8(Nullability.Nullable)],
            Nullability.Nullable);
        Assert.Equal(expected, read);
    }

    [Fact]
    public void A_struct_with_more_names_than_dtypes_is_rejected()
    {
        byte[] body = new PbBuilder()
            .StringField(1, "a")
            .StringField(1, "b")
            .LenField(2, new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray())
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseStruct, body).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    [Fact]
    public void A_union_whose_type_ids_do_not_match_its_dtypes_is_rejected()
    {
        byte[] body = new PbBuilder()
            .StringField(1, "a")
            .LenField(2, new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray())
            .LenField(3, new byte[] { 0x00, 0x01 })     // two ids, one dtype
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseUnion, body).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    // ------------------------------------------------------------------- required nested dtypes

    [Theory]
    [InlineData(CaseList)]
    [InlineData(CaseFixedSizeList)]
    [InlineData(CaseExtension)]
    [InlineData(CaseMap)]
    public void A_missing_required_child_dtype_is_rejected(int caseNumber)
    {
        byte[] message = new PbBuilder().LenField(caseNumber, Array.Empty<byte>()).ToArray();
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>A map needs both children; supplying only the key is still malformed.</summary>
    [Fact]
    public void A_map_with_only_a_key_type_is_rejected()
    {
        byte[] body = new PbBuilder()
            .LenField(1, new PbBuilder().LenField(CaseUtf8, Array.Empty<byte>()).ToArray())
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseMap, body).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>
    /// A nested dtype that sets no case is malformed, and the error has to surface from inside the
    /// parent rather than producing a struct with a hole in it.
    /// </summary>
    [Fact]
    public void A_struct_field_whose_dtype_sets_no_case_is_rejected()
    {
        byte[] body = new PbBuilder()
            .StringField(1, "a")
            .LenField(2, Array.Empty<byte>())       // an empty DType message
            .ToArray();
        byte[] message = new PbBuilder().LenField(CaseStruct, body).ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(message, arena));
    }

    /// <summary>An undefined PType nested three levels down still surfaces as a format error.</summary>
    [Fact]
    public void A_malformed_dtype_deep_inside_a_map_is_rejected()
    {
        byte[] badPrimitive = new PbBuilder()
            .LenField(CasePrimitive, new PbBuilder().VarintField(1, 99).ToArray())
            .ToArray();
        byte[] list = new PbBuilder().LenField(CaseList, new PbBuilder().LenField(1, badPrimitive).ToArray()).ToArray();
        byte[] map = new PbBuilder()
            .LenField(
                CaseMap,
                new PbBuilder()
                    .LenField(1, new PbBuilder().LenField(CaseUtf8, Array.Empty<byte>()).ToArray())
                    .LenField(2, list)
                    .ToArray())
            .ToArray();

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(map, arena));
    }

    /// <summary>
    /// <c>Extension.metadata</c> is <c>optional bytes</c>, so absent and present-but-empty are
    /// different on the wire — but <see cref="DType.ExtensionMetadata"/> cannot represent the
    /// difference, so the codec collapses them. Pinned here so the loss is a decision, not a
    /// surprise: both wire forms decode to the same dtype, and both re-encode as absent.
    /// </summary>
    [Fact]
    public void An_empty_extension_metadata_is_indistinguishable_from_an_absent_one()
    {
        byte[] storage = new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray();

        byte[] absent = new PbBuilder()
            .LenField(CaseExtension, new PbBuilder().StringField(1, "vortex.x").LenField(2, storage).ToArray())
            .ToArray();
        byte[] presentButEmpty = new PbBuilder()
            .LenField(
                CaseExtension,
                new PbBuilder().StringField(1, "vortex.x").LenField(2, storage).LenField(3, Array.Empty<byte>()).ToArray())
            .ToArray();

        DTypeArena arena = new DTypeArena();
        DType a = DTypeProtobuf.Read(absent, arena);
        DType b = DTypeProtobuf.Read(presentButEmpty, arena);

        Assert.Equal(a, b);
        Assert.True(a.ExtensionMetadata.IsEmpty);
        Assert.Equal(absent, DTypeProtobuf.Serialize(b));
    }

    /// <summary>An extension id is a UTF-8 string, not ASCII, and is interned from raw bytes.</summary>
    [Fact]
    public void A_non_ascii_extension_id_round_trips()
    {
        DTypeArena source = new DTypeArena();
        DType ext = source.Extension(
            "vortex.日付",
            source.Primitive(PType.I32, Nullability.NonNullable),
            [0xFF]);

        DType round = RoundTrip(ext);
        Assert.Equal("vortex.日付", round.ExtensionId);
        Assert.Equal(ext, round);
    }

    // ----------------------------------------------------------------------------- depth cap

    /// <summary>
    /// 63 list wrappers over a leaf is exactly <see cref="VortexLimits.MaxDTypeDepth"/>; 64 is one
    /// too many. The reader has to hold this line itself: the arena's construction-time cap fires
    /// only on the way back up, long after the recursion has run.
    /// </summary>
    [Fact]
    public void Nesting_is_accepted_up_to_the_cap_and_rejected_beyond_it()
    {
        DTypeArena arena = new DTypeArena();

        byte[] atCap = NestedLists(VortexLimits.MaxDTypeDepth - 1);
        DType read = DTypeProtobuf.Read(atCap, arena);
        Assert.Equal(DTypeKind.List, read.Kind);

        byte[] overCap = NestedLists(VortexLimits.MaxDTypeDepth);
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(overCap, arena));
    }

    /// <summary>
    /// The cap fires on the way down, so a pathologically deep message costs 64 frames, not
    /// 5000 -- which is the difference between an exception and a process-killing stack overflow.
    /// </summary>
    [Fact]
    public void A_pathologically_deep_message_is_rejected_without_recursing_into_it()
    {
        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(NestedLists(5000), arena));
    }

    [Fact]
    public void Nesting_through_struct_fields_is_capped_too()
    {
        byte[] inner = new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray();
        for (int i = 0; i < VortexLimits.MaxDTypeDepth; i++)
        {
            byte[] body = new PbBuilder().StringField(1, "f").LenField(2, inner).ToArray();
            inner = new PbBuilder().LenField(CaseStruct, body).ToArray();
        }

        DTypeArena arena = new DTypeArena();
        Assert.Throws<VortexFormatException>(() => DTypeProtobuf.Read(inner, arena));
    }

    // ------------------------------------------------------------------------------ write API

    [Fact]
    public void Writing_a_default_dtype_is_an_argument_error_not_a_format_error()
    {
        Assert.Throws<ArgumentException>(() => DTypeProtobuf.Serialize(default));
    }

    [Fact]
    public void Reading_with_a_null_arena_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DTypeProtobuf.Read(Array.Empty<byte>(), null!));
    }

    /// <summary>
    /// <c>WriteField</c> is the shape every enclosing codec uses: the dtype nested under a field
    /// number of the caller's choosing, tag and length included.
    /// </summary>
    [Fact]
    public void WriteField_nests_the_dtype_under_the_requested_field_number()
    {
        DTypeArena arena = new DTypeArena();
        DType dtype = arena.Primitive(PType.I64, Nullability.Nullable);

        ProtoWriter writer = new ProtoWriter(64);
        byte[] encoded;
        try
        {
            writer.WriteUInt32Always(1, 7);
            DTypeProtobuf.WriteField(ref writer, 2, dtype);
            writer.WriteUInt32Always(3, 9);
            encoded = writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        ProtoReader reader = new ProtoReader(encoded);
        Assert.True(reader.TryReadTag(out int field, out _));
        Assert.Equal(1, field);
        Assert.Equal(7U, reader.ReadVarint32());

        Assert.True(reader.TryReadTag(out field, out ProtoWireType wire));
        Assert.Equal(2, field);
        Assert.Equal(ProtoWireType.LengthDelimited, wire);
        Assert.Equal(dtype, DTypeProtobuf.Read(reader.ReadLengthDelimited(), arena));

        Assert.True(reader.TryReadTag(out field, out _));
        Assert.Equal(3, field);
        Assert.Equal(9U, reader.ReadVarint32());
        Assert.True(reader.End);
    }

    // -------------------------------------------------------------------------------- helpers

    private static DType RoundTrip(DType dtype) =>
        DTypeProtobuf.Read(DTypeProtobuf.Serialize(dtype), new DTypeArena());

    private static void AssertRoundTrip(DType dtype)
    {
        DType round = RoundTrip(dtype);
        Assert.Equal(dtype, round);
        Assert.Equal(dtype.GetHashCode(), round.GetHashCode());
        Assert.Equal(dtype.ToString(), round.ToString());

        // Re-encoding the decoded form must be byte-identical, or the codec has a non-canonical
        // corner that would break the conformance corpus.
        Assert.Equal(DTypeProtobuf.Serialize(dtype), DTypeProtobuf.Serialize(round));
    }

    /// <summary>A DType message of <paramref name="wrappers"/> nested Lists over a <c>bool</c> leaf.</summary>
    private static byte[] NestedLists(int wrappers)
    {
        byte[] inner = new PbBuilder().LenField(CaseBool, Array.Empty<byte>()).ToArray();
        for (int i = 0; i < wrappers; i++)
        {
            byte[] body = new PbBuilder().LenField(1, inner).ToArray();
            inner = new PbBuilder().LenField(CaseList, body).ToArray();
        }

        return inner;
    }

    /// <summary>xorshift32: deterministic, seeded, no <see cref="Random"/>.</summary>
    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static DType Generate(DTypeArena arena, ref uint state, int budget)
    {
        Nullability n = (Next(ref state) & 1) == 0 ? Nullability.Nullable : Nullability.NonNullable;
        int pick = (int)(Next(ref state) % (budget > 0 ? 13u : 6u));
        switch (pick)
        {
            case 0: return arena.Null(n);
            case 1: return arena.Bool(n);
            case 2: return arena.Primitive((PType)(Next(ref state) % 11), n);
            case 3:
            {
                byte precision = (byte)(1 + (Next(ref state) % 38));
                sbyte scale = (sbyte)((int)(Next(ref state) % (2u * precision + 1)) - precision);
                return arena.Decimal(precision, scale, n);
            }

            case 4: return arena.Utf8(n);
            case 5: return arena.Binary(n);
            case 6:
            {
                int count = (int)(Next(ref state) % 4);
                string[] names = new string[count];
                DType[] fields = new DType[count];
                for (int i = 0; i < count; i++)
                {
                    names[i] = "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    fields[i] = Generate(arena, ref state, budget - 1);
                }

                return arena.Struct(names, fields, n);
            }

            case 7: return arena.List(Generate(arena, ref state, budget - 1), n);
            case 8:
            {
                string id = "ext." + (Next(ref state) % 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                DType storage = Generate(arena, ref state, budget - 1);
                byte[] metadata = (Next(ref state) & 1) == 0
                    ? Array.Empty<byte>()
                    : new byte[] { 0xAA, 0xBB };
                return arena.Extension(id, storage, metadata);
            }

            case 9: return arena.FixedSizeList(Generate(arena, ref state, budget - 1), Next(ref state) % 8, n);
            case 10: return arena.Variant(n);
            case 11:
            {
                int count = (int)(Next(ref state) % 4);
                int[] names = new int[count];
                DType[] fields = new DType[count];
                byte[] ids = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    names[i] = arena.InternName("u" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    fields[i] = Generate(arena, ref state, budget - 1);
                    ids[i] = (byte)(Next(ref state) & 0xFF);
                }

                return arena.Union(names, fields, ids, n);
            }

            default:
                return arena.Map(
                    Generate(arena, ref state, budget - 1),
                    Generate(arena, ref state, budget - 1),
                    (Next(ref state) & 1) == 0,
                    n);
        }
    }
}
