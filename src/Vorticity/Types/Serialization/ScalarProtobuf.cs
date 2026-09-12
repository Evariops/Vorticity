// Protobuf codec for vortex.scalar.Scalar and vortex.scalar.ScalarValue, transcribed field by
// field from spec/proto/scalar.proto:
//
//   message Scalar      { vortex.dtype.DType dtype = 1; ScalarValue value = 2; }
//   message ScalarValue { oneof kind {
//       google.protobuf.NullValue null_value = 1;   bool   bool_value  = 2;
//       sint64 int64_value  = 3;                    uint64 uint64_value = 4;
//       float  f32_value    = 5;                    double f64_value    = 6;
//       string string_value = 7;                    bytes  bytes_value  = 8;
//       ListValue list_value = 9;                   uint64 f16_value    = 10;
//       Scalar variant_value = 11;                  UnionValue union_value = 12; } }
//   message ListValue   { repeated ScalarValue values = 1; }
//   message UnionValue  { uint32 type_id = 1; ScalarValue value = 2; }
//
// The wire types are not the ones a careless transcription would pick, and getting any of them
// wrong produces a file that round-trips against itself and against nothing else:
//
//   * int64_value is SINT64 -- zigzag. uint64_value is a plain varint.
//   * f32_value is fixed32 and f64_value is fixed64, but f16_value is a uint64 VARINT carrying the
//     raw binary16 bits. It is not a float on the wire at all.
//   * null_value is a google.protobuf.NullValue enum, i.e. a varint. Present means the scalar is
//     null; absent -- no case set at all -- means Absent, which is a different thing
//     (docs/08-semantics.md section 1: "a statistic with no value licenses nothing").
//   * Every arm is a oneof member, so every one has explicit presence: the writers here are the
//     `...Always` variants throughout, or `false`, `0`, `""` and `-0.0` would silently vanish and
//     read back as Absent. docs/04-conformance.md section 4.3 lists -0.0 as a required case.
//
// TODO(decimal-scalar): spec/proto/scalar.proto has no 128-bit case, so a Decimal(p) scalar with
// p > 18 cannot be carried by int64_value or uint64_value and must arrive through one of the
// twelve cases above -- most plausibly bytes_value holding the unscaled i128/i256, but the
// encoding (width, endianness, sign extension) is specified nowhere in the vendored spec. No
// Decimal kind is invented here: the twelve wire cases are implemented faithfully and the
// ambiguity is resolved against the reference implementation in Phase 1.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Types.Serialization;

/// <summary>
/// Reads and writes <c>vortex.scalar.Scalar</c> and <c>vortex.scalar.ScalarValue</c> Protobuf
/// messages (spec/proto/scalar.proto).
/// </summary>
/// <remarks>
/// A bare <c>ScalarValue</c> body also stands alone: it is the array metadata of
/// <c>vortex.constant</c> (spec/METADATA.md), which is why <see cref="ReadValue"/> exists
/// separately from <see cref="ReadScalar"/> rather than only as its nested half.
/// </remarks>
public static class ScalarProtobuf
{
    // --- ScalarValue.kind oneof case numbers. Identical to ScalarValueKind by construction. ---
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

    // --- field numbers inside the auxiliary messages ---
    private const int ScalarDType = 1;
    private const int ScalarValueField = 2;
    private const int ListValues = 1;
    private const int UnionTypeId = 1;
    private const int UnionValueField = 2;

    // ---------------------------------------------------------------------------------- reading

    /// <summary>
    /// Reads the body of a <c>vortex.scalar.ScalarValue</c> message: no outer tag, no outer length
    /// prefix. An empty body sets no <c>kind</c> case and therefore yields
    /// <see cref="ScalarValueKind.Absent"/> — never <see cref="ScalarValueKind.Null"/>.
    /// </summary>
    /// <param name="message">The message body.</param>
    /// <param name="store">Store the resulting value nodes are appended to.</param>
    /// <param name="dtypes">Arena for the dtype nested inside a <c>variant_value</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="dtypes"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The message is malformed: truncated, a known field carrying the wrong wire type, an
    /// <c>f16_value</c> that does not fit in 16 bits, or nesting deeper than
    /// <see cref="VortexLimits.MaxDTypeDepth"/>.
    /// </exception>
    public static ScalarValue ReadValue(ReadOnlySpan<byte> message, ScalarStore store, DTypeArena dtypes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dtypes);
        return ReadValueBody(message, store, dtypes, 0);
    }

    /// <summary>
    /// Reads the body of a <c>vortex.scalar.Scalar</c> message.
    /// </summary>
    /// <param name="message">The message body.</param>
    /// <param name="dtypes">Arena the <c>dtype</c> field is read into.</param>
    /// <param name="store">Store the <c>value</c> field is read into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dtypes"/> or <paramref name="store"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The message is malformed, or carries no <c>dtype</c>. A dtype-less scalar is rejected rather
    /// than represented: <c>default(DType)</c> throws <see cref="InvalidOperationException"/> from
    /// every accessor, and the parse boundary is the only place that can still turn the problem
    /// into a <see cref="VortexFormatException"/>.
    /// </exception>
    public static Scalar ReadScalar(ReadOnlySpan<byte> message, DTypeArena dtypes, ScalarStore store)
    {
        ArgumentNullException.ThrowIfNull(dtypes);
        ArgumentNullException.ThrowIfNull(store);
        return ReadScalarBody(message, dtypes, store, 0);
    }

    private static ScalarValue ReadValueBody(
        ReadOnlySpan<byte> message,
        ScalarStore store,
        DTypeArena dtypes,
        int depth)
    {
        // The recursion runs before the store's own construction-time cap can see anything, so the
        // reader has to hold the line itself (docs/03-architecture.md section 6).
        VortexLimits.CheckDepth(depth + 1, VortexLimits.MaxDTypeDepth, "Scalar");

        ProtoReader reader = new ProtoReader(message);

        // The winning case is materialised once, after the loop: a repeated oneof case is
        // last-wins, and deferring means an overridden list_value bomb is skipped, not built.
        int kind = 0;
        ulong bits = 0;
        ReadOnlySpan<byte> body = default;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case CaseNull:
                    RequireWire("ScalarValue.null_value", wire, ProtoWireType.Varint);

                    // google.protobuf.NullValue defines only NULL_VALUE = 0, but proto3 enums are
                    // open: an unrecognized number still sets the oneof case. The value is read to
                    // advance past it and then deliberately discarded.
                    reader.ReadVarint();
                    kind = CaseNull;
                    break;

                case CaseBool:
                    RequireWire("ScalarValue.bool_value", wire, ProtoWireType.Varint);
                    bits = reader.ReadBool() ? 1UL : 0UL;
                    kind = CaseBool;
                    break;

                case CaseInt64:
                    RequireWire("ScalarValue.int64_value", wire, ProtoWireType.Varint);

                    // sint64: zigzag, NOT a two's-complement varint. Reading it as int64 would
                    // turn -1 into 1 and 1 into -1 with no error anywhere.
                    bits = unchecked((ulong)reader.ReadSInt64());
                    kind = CaseInt64;
                    break;

                case CaseUInt64:
                    RequireWire("ScalarValue.uint64_value", wire, ProtoWireType.Varint);
                    bits = reader.ReadVarint();
                    kind = CaseUInt64;
                    break;

                case CaseF32:
                    RequireWire("ScalarValue.f32_value", wire, ProtoWireType.Fixed32);
                    bits = reader.ReadFixed32();
                    kind = CaseF32;
                    break;

                case CaseF64:
                    RequireWire("ScalarValue.f64_value", wire, ProtoWireType.Fixed64);
                    bits = reader.ReadFixed64();
                    kind = CaseF64;
                    break;

                case CaseString:
                    RequireWire("ScalarValue.string_value", wire, ProtoWireType.LengthDelimited);
                    body = reader.ReadLengthDelimited();
                    kind = CaseString;
                    break;

                case CaseBytes:
                    RequireWire("ScalarValue.bytes_value", wire, ProtoWireType.LengthDelimited);
                    body = reader.ReadLengthDelimited();
                    kind = CaseBytes;
                    break;

                case CaseList:
                    RequireWire("ScalarValue.list_value", wire, ProtoWireType.LengthDelimited);
                    body = reader.ReadLengthDelimited();
                    kind = CaseList;
                    break;

                case CaseF16:
                    RequireWire("ScalarValue.f16_value", wire, ProtoWireType.Varint);

                    // A uint64 varint carrying the raw binary16 bits, not a float.
                    bits = reader.ReadVarint();
                    kind = CaseF16;
                    break;

                case CaseVariant:
                    RequireWire("ScalarValue.variant_value", wire, ProtoWireType.LengthDelimited);
                    body = reader.ReadLengthDelimited();
                    kind = CaseVariant;
                    break;

                case CaseUnion:
                    RequireWire("ScalarValue.union_value", wire, ProtoWireType.LengthDelimited);
                    body = reader.ReadLengthDelimited();
                    kind = CaseUnion;
                    break;

                default:
                    // Unknown field number: skipped, never rejected (docs/02-format.md section 5.3).
                    reader.SkipField(wire);
                    break;
            }
        }

        switch (kind)
        {
            case 0: return store.Absent;
            case CaseNull: return store.Null();
            case CaseBool: return store.Bool(bits != 0);
            case CaseInt64: return store.Int64(unchecked((long)bits));
            case CaseUInt64: return store.UInt64(bits);

            // Bit-preserving on purpose: a NaN payload and a negative zero are values here, not
            // noise (docs/08-semantics.md section 2 keeps IEEE comparison out of value identity).
            case CaseF32: return store.F32(BitConverter.UInt32BitsToSingle((uint)bits));
            case CaseF64: return store.F64(BitConverter.UInt64BitsToDouble(bits));
            case CaseString: return store.String(body);
            case CaseBytes: return store.Bytes(body);
            case CaseList: return ReadList(body, store, dtypes, depth);

            case CaseF16:
                // The field is a uint64 but binary16 has 16 bits. Truncating would be a silent
                // wrong value, so anything wider is malformed.
                if (bits > ushort.MaxValue)
                {
                    ThrowF16TooWide(bits);
                }

                return store.F16FromBits((ushort)bits);

            case CaseVariant: return store.Variant(ReadScalarBody(body, dtypes, store, depth + 1));
            default: return ReadUnion(body, store, dtypes, depth);
        }
    }

    /// <summary><c>message ListValue { repeated ScalarValue values = 1; }</c></summary>
    private static ScalarValue ReadList(
        ReadOnlySpan<byte> body,
        ScalarStore store,
        DTypeArena dtypes,
        int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ProtoScratchList<ScalarValue> values = default;
        try
        {
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field == ListValues)
                {
                    RequireWire("ListValue.values", wire, ProtoWireType.LengthDelimited);

                    // An empty element message is a legal Absent element, and it must keep its
                    // slot: dropping it would renumber every element after it.
                    values.Add(ReadValueBody(reader.ReadLengthDelimited(), store, dtypes, depth + 1));
                    continue;
                }

                reader.SkipField(wire);
            }

            return store.List(values.Span);
        }
        finally
        {
            values.Dispose();
        }
    }

    /// <summary><c>message UnionValue { uint32 type_id = 1; ScalarValue value = 2; }</c></summary>
    private static ScalarValue ReadUnion(
        ReadOnlySpan<byte> body,
        ScalarStore store,
        DTypeArena dtypes,
        int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        uint typeId = 0;
        ReadOnlySpan<byte> valueBody = default;
        bool hasValue = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case UnionTypeId:
                    RequireWire("UnionValue.type_id", wire, ProtoWireType.Varint);

                    // uint32: out of domain above 2^32-1, unlike an int32 field.
                    typeId = reader.ReadVarint32();
                    break;

                case UnionValueField:
                    RequireWire("UnionValue.value", wire, ProtoWireType.LengthDelimited);
                    valueBody = reader.ReadLengthDelimited();
                    hasValue = true;
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // An absent `value` is a present union alternative carrying nothing, which the model spells
        // Absent. An outer-null union uses null_value instead (the comment on UnionValue in
        // spec/proto/scalar.proto), so the two never collide.
        ScalarValue value = hasValue
            ? ReadValueBody(valueBody, store, dtypes, depth + 1)
            : store.Absent;
        return store.Union(typeId, value);
    }

    /// <summary><c>message Scalar { vortex.dtype.DType dtype = 1; ScalarValue value = 2; }</c></summary>
    /// <param name="body">The <c>Scalar</c> message body.</param>
    /// <param name="dtypes">Arena for the <c>dtype</c> field.</param>
    /// <param name="store">Store for the <c>value</c> field.</param>
    /// <param name="valueDepth">Scalar nesting depth the <c>value</c> field is read at.</param>
    private static Scalar ReadScalarBody(
        ReadOnlySpan<byte> body,
        DTypeArena dtypes,
        ScalarStore store,
        int valueDepth)
    {
        ProtoReader reader = new ProtoReader(body);
        ReadOnlySpan<byte> dtypeBody = default;
        ReadOnlySpan<byte> valueBody = default;
        bool hasDType = false;
        bool hasValue = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case ScalarDType:
                    RequireWire("Scalar.dtype", wire, ProtoWireType.LengthDelimited);
                    dtypeBody = reader.ReadLengthDelimited();
                    hasDType = true;
                    break;

                case ScalarValueField:
                    RequireWire("Scalar.value", wire, ProtoWireType.LengthDelimited);
                    valueBody = reader.ReadLengthDelimited();
                    hasValue = true;
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasDType)
        {
            ThrowMissingDType();
        }

        // The dtype's own depth budget is independent of the scalar's: a dtype nested inside a
        // variant scalar is not a continuation of the scalar chain, and charging it to the same
        // 64 would reject legal files. Total stack use stays bounded by the two caps together.
        DType dtype = DTypeProtobuf.Read(dtypeBody, dtypes);
        ScalarValue value = hasValue
            ? ReadValueBody(valueBody, store, dtypes, valueDepth)
            : store.Absent;
        return new Scalar(dtype, value);
    }

    // ---------------------------------------------------------------------------------- writing

    /// <summary>
    /// Writes the body of a <c>vortex.scalar.ScalarValue</c> message — one <c>kind</c> case field,
    /// with no outer tag and no outer length prefix. An <see cref="ScalarValueKind.Absent"/> value
    /// writes nothing at all, which is exactly how absence is spelled on the wire.
    /// </summary>
    /// <param name="writer">Writer to append to. Must be passed by reference: it is a mutable struct.</param>
    /// <param name="value">The value to encode.</param>
    /// <exception cref="ArgumentException">
    /// A nested variant scalar carries <c>default(DType)</c>, which has no encoding.
    /// </exception>
    public static void WriteValue(ref ProtoWriter writer, ScalarValue value)
    {
        switch (value.Kind)
        {
            case ScalarValueKind.Absent:
                // No case set. Deliberately distinct from Null.
                break;

            case ScalarValueKind.Null:
                // google.protobuf.NullValue.NULL_VALUE = 0, emitted even though it is the default:
                // a oneof member has explicit presence, and omitting it would mean Absent.
                writer.WriteEnumAlways(CaseNull, 0);
                break;

            case ScalarValueKind.Bool:
                writer.WriteBoolAlways(CaseBool, value.AsBool);
                break;

            case ScalarValueKind.Int64:
                writer.WriteSInt64Always(CaseInt64, value.AsInt64);
                break;

            case ScalarValueKind.UInt64:
                writer.WriteUInt64Always(CaseUInt64, value.AsUInt64);
                break;

            case ScalarValueKind.F32:
                // ...Always, or -0.0 is dropped by the proto3 `value != 0` test and reads back
                // as Absent. docs/04-conformance.md section 4.3 requires -0.0 to survive.
                writer.WriteFloatAlways(CaseF32, value.AsF32);
                break;

            case ScalarValueKind.F64:
                writer.WriteDoubleAlways(CaseF64, value.AsF64);
                break;

            case ScalarValueKind.String:
                writer.WriteStringUtf8Always(CaseString, value.AsBytes);
                break;

            case ScalarValueKind.Bytes:
                writer.WriteBytesAlways(CaseBytes, value.AsBytes);
                break;

            case ScalarValueKind.List:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseList);
                int count = value.ListCount;
                for (int i = 0; i < count; i++)
                {
                    WriteValueField(ref writer, ListValues, value.GetListElement(i));
                }

                scope.End();
                break;
            }

            case ScalarValueKind.F16:
                // The raw 16 bits as a uint64 varint. Bits, not a value: a NaN payload survives.
                writer.WriteUInt64Always(CaseF16, value.F16Bits);
                break;

            case ScalarValueKind.Variant:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseVariant);
                WriteScalar(ref writer, value.AsVariant);
                scope.End();
                break;
            }

            case ScalarValueKind.Union:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseUnion);

                // type_id is a plain uint32 field of UnionValue, not a oneof member, so proto3
                // implicit presence applies and 0 is omitted.
                writer.WriteUInt32(UnionTypeId, value.UnionTypeId);
                ScalarValue inner = value.UnionValue;
                if (!inner.IsAbsent)
                {
                    WriteValueField(ref writer, UnionValueField, inner);
                }

                scope.End();
                break;
            }

            default:
                ThrowUnknownKind(value.Kind);
                break;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> as a nested <c>ScalarValue</c> message under
    /// <paramref name="fieldNumber"/>. An absent value still produces the tag and a zero length:
    /// inside a <c>ListValue</c> that empty message is the element's slot.
    /// </summary>
    public static void WriteValueField(ref ProtoWriter writer, int fieldNumber, ScalarValue value)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        WriteValue(ref writer, value);
        scope.End();
    }

    /// <summary>Encodes <paramref name="value"/> as a standalone <c>ScalarValue</c> message body.</summary>
    public static byte[] SerializeValue(ScalarValue value)
    {
        // A plain local plus try/finally, not `using`: CS1657 forbids passing a `using` variable
        // as a `ref` argument.
        ProtoWriter writer = new ProtoWriter(64);
        try
        {
            WriteValue(ref writer, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Writes the body of a <c>vortex.scalar.Scalar</c> message: the dtype, then the value when it
    /// is present.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scalar"/>'s dtype is <c>default(DType)</c>.</exception>
    public static void WriteScalar(ref ProtoWriter writer, in Scalar scalar)
    {
        DType dtype = scalar.DType;
        if (dtype.IsDefault)
        {
            ThrowDefaultDType();
        }

        DTypeProtobuf.WriteField(ref writer, ScalarDType, dtype);

        ScalarValue value = scalar.Value;
        if (!value.IsAbsent)
        {
            // An absent value is written by omitting the message field, which is how proto3 spells
            // an unset singular message. It reads back as Absent, so the round trip is exact.
            WriteValueField(ref writer, ScalarValueField, value);
        }
    }

    /// <summary>Encodes <paramref name="scalar"/> as a standalone <c>Scalar</c> message body.</summary>
    /// <exception cref="ArgumentException"><paramref name="scalar"/>'s dtype is <c>default(DType)</c>.</exception>
    public static byte[] SerializeScalar(in Scalar scalar)
    {
        ProtoWriter writer = new ProtoWriter(128);
        try
        {
            WriteScalar(ref writer, in scalar);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------- helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RequireWire(string what, ProtoWireType actual, ProtoWireType expected)
    {
        if (actual != expected)
        {
            ThrowWireType(what, actual, expected);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWireType(string what, ProtoWireType actual, ProtoWireType expected) =>
        throw new VortexFormatException(
            $"{what} arrived with wire type {actual}; spec/proto/scalar.proto declares it as {expected}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowF16TooWide(ulong bits) =>
        throw new VortexFormatException(
            $"ScalarValue.f16_value is {bits}, which does not fit in the 16 bits of a binary16 value.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingDType() =>
        throw new VortexFormatException("A vortex.scalar.Scalar message carried no dtype; it is required.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnknownKind(ScalarValueKind kind) =>
        throw new VortexFormatException($"ScalarValueKind {(byte)kind} has no vortex.scalar.ScalarValue case.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefaultDType() =>
        throw new ArgumentException(
            "A Scalar whose dtype is default(DType) cannot be encoded: the message requires a dtype.",
            "scalar");
}
