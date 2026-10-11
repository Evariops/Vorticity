using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Types.Serialization;

/// <summary>
/// Reads and writes <c>vortex.dtype.DType</c> Protobuf messages.
/// </summary>
/// <remarks>
/// <para>
/// Both directions are allocation-free apart from the arena's own growth and the pooled scratch
/// that gathers a struct's or a union's repeated fields, whose length is not known in advance.
/// </para>
/// <para>
/// Every field number, wire type and name here is transcribed from the vendored schema rather than
/// recalled, and the reader accepts what the encoding allows rather than what a writer happens to
/// emit: a repeated scalar field arrives packed or unpacked, a repeated <c>names</c> and
/// <c>dtypes</c> pair may be interleaved and split across non-adjacent tags and so pairs by ordinal
/// within each field rather than by adjacency, and a repeated oneof case is last-wins.
/// </para>
/// <para>
/// Everything malformed throws <see cref="VortexFormatException"/> and nothing else: an
/// unrecognized <em>field number</em> is skipped, while a value outside the schema's domain -- an
/// undefined <see cref="PType"/>, a precision that does not fit in a byte, a nested dtype that is
/// required and absent -- is rejected.
/// </para>
/// </remarks>
internal static class DTypeProtobuf
{
    // --- DType.dtype_type oneof case numbers. Identical to DTypeKind by construction. ---
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

    // --- field numbers inside each case message ---
    private const int BoolNullable = 1;             // Bool / Utf8 / Binary / Variant: nullable = 1
    private const int PrimitiveType = 1;
    private const int PrimitiveNullable = 2;
    private const int DecimalPrecision = 1;
    private const int DecimalScale = 2;
    private const int DecimalNullable = 3;
    private const int StructNames = 1;
    private const int StructDTypes = 2;
    private const int StructNullable = 3;
    private const int ListElementType = 1;
    private const int ListNullable = 2;
    private const int FslElementType = 1;
    private const int FslSize = 2;
    private const int FslNullable = 3;
    private const int ExtensionId = 1;
    private const int ExtensionStorage = 2;
    private const int ExtensionMetadata = 3;
    private const int UnionNames = 1;
    private const int UnionDTypes = 2;
    private const int UnionTypeIds = 3;
    private const int UnionNullable = 4;
    private const int MapKeyType = 1;
    private const int MapValueType = 2;
    private const int MapKeysSorted = 3;
    private const int MapNullable = 4;

    // ---------------------------------------------------------------------------------- reading

    /// <summary>
    /// Reads the body of a <c>vortex.dtype.DType</c> message: no outer tag, no outer length prefix.
    /// </summary>
    /// <param name="message">The message body. May be empty, which is malformed for a root dtype.</param>
    /// <param name="arena">Arena the resulting nodes are interned into.</param>
    /// <returns>A handle into <paramref name="arena"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The message is malformed: truncated, no <c>dtype_type</c> case present, an undefined
    /// <see cref="PType"/>, a payload outside the model's range, or nesting deeper than
    /// <see cref="VortexLimits.MaxDTypeDepth"/>.
    /// </exception>
    public static DType Read(ReadOnlySpan<byte> message, DTypeArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ProtoReader reader = new ProtoReader(message);
        return Read(ref reader, arena, 0);
    }

    /// <summary>
    /// Reads a <c>DType</c> message body from <paramref name="reader"/>, consuming it to the end.
    /// </summary>
    /// <param name="reader">A reader positioned over exactly one <c>DType</c> message body.</param>
    /// <param name="arena">Arena the resulting nodes are interned into.</param>
    /// <param name="depth">
    /// Zero-based nesting depth. Checked against <see cref="VortexLimits.MaxDTypeDepth"/> on entry:
    /// the recursion happens before the arena sees anything, so the arena's own construction-time
    /// cap arrives too late to stop a 10 000-deep message from blowing the stack.
    /// </param>
    internal static DType Read(ref ProtoReader reader, DTypeArena arena, int depth)
    {
        VortexLimits.CheckDepth(depth + 1, VortexLimits.MaxDTypeDepth, "DType");

        // The oneof body is captured, not parsed, so that a repeated case costs nothing but the
        // skip: only the winner is materialised. That also keeps a hostile file from making us
        // build a thousand discarded struct nodes in the arena before the last case overrides them.
        int caseNumber = 0;
        ReadOnlySpan<byte> body = default;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field is >= CaseNull and <= CaseMap)
            {
                // Every arm of the oneof is a message type, so every one is length-delimited.
                RequireWire("DType.dtype_type", wire, ProtoWireType.LengthDelimited);
                body = reader.ReadLengthDelimited();
                caseNumber = field;     // proto3 oneof: last case on the wire wins.
                continue;
            }

            // Unknown field number: skipped, never rejected, so a newer writer's fields do not
            // make a file unreadable.
            reader.SkipField(wire);
        }

        if (caseNumber == 0)
        {
            ThrowNoCase();
        }

        switch (caseNumber)
        {
            case CaseNull: return ReadNull(body, arena);
            case CaseBool: return ReadLeaf(body, arena, DTypeKind.Bool);
            case CasePrimitive: return ReadPrimitive(body, arena);
            case CaseDecimal: return ReadDecimal(body, arena);
            case CaseUtf8: return ReadLeaf(body, arena, DTypeKind.Utf8);
            case CaseBinary: return ReadLeaf(body, arena, DTypeKind.Binary);
            case CaseStruct: return ReadStruct(body, arena, depth);
            case CaseList: return ReadList(body, arena, depth);
            case CaseExtension: return ReadExtension(body, arena, depth);
            case CaseFixedSizeList: return ReadFixedSizeList(body, arena, depth);
            case CaseVariant: return ReadLeaf(body, arena, DTypeKind.Variant);
            case CaseUnion: return ReadUnion(body, arena, depth);
            default: return ReadMap(body, arena, depth);
        }
    }

    /// <summary><c>message Null {}</c> — no fields at all.</summary>
    private static DType ReadNull(ReadOnlySpan<byte> body, DTypeArena arena)
    {
        // Drained rather than ignored: a corrupt body inside a well-framed message is still
        // corrupt, and draining is the only way to notice.
        DrainUnknown(body);

        // A Null dtype carries no `nullable` on the wire, and the arena forces Nullable, so the
        // argument is only for call-site symmetry.
        return arena.Null(Nullability.Nullable);
    }

    /// <summary><c>Bool</c>, <c>Utf8</c>, <c>Binary</c> and <c>Variant</c>: all <c>bool nullable = 1</c>.</summary>
    private static DType ReadLeaf(ReadOnlySpan<byte> body, DTypeArena arena, DTypeKind kind)
    {
        ProtoReader reader = new ProtoReader(body);
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == BoolNullable)
            {
                RequireWire("nullable", wire, ProtoWireType.Varint);
                nullable = reader.ReadBool();
                continue;
            }

            reader.SkipField(wire);
        }

        Nullability n = ToNullability(nullable);
        switch (kind)
        {
            case DTypeKind.Bool: return arena.Bool(n);
            case DTypeKind.Utf8: return arena.Utf8(n);
            case DTypeKind.Binary: return arena.Binary(n);
            default: return arena.Variant(n);
        }
    }

    /// <summary><c>message Primitive { PType type = 1; bool nullable = 2; }</c></summary>
    private static DType ReadPrimitive(ReadOnlySpan<byte> body, DTypeArena arena)
    {
        ProtoReader reader = new ProtoReader(body);
        int rawType = 0;
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case PrimitiveType:
                    RequireWire("Primitive.type", wire, ProtoWireType.Varint);

                    // A proto enum is an int32 on the wire, so a negative tag is expressible as a
                    // ten-byte varint and must be rejected rather than wrapped.
                    rawType = reader.ReadInt32();
                    break;

                case PrimitiveNullable:
                    RequireWire("Primitive.nullable", wire, ProtoWireType.Varint);
                    nullable = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if ((uint)rawType > PTypeExtensions.MaxPType)
        {
            ThrowUndefinedPType(rawType);
        }

        return arena.Primitive((PType)rawType, ToNullability(nullable));
    }

    /// <summary><c>message Decimal { uint32 precision = 1; int32 scale = 2; bool nullable = 3; }</c></summary>
    private static DType ReadDecimal(ReadOnlySpan<byte> body, DTypeArena arena)
    {
        ProtoReader reader = new ProtoReader(body);
        uint precision = 0;
        int scale = 0;
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case DecimalPrecision:
                    RequireWire("Decimal.precision", wire, ProtoWireType.Varint);
                    precision = reader.ReadVarint32();
                    break;

                case DecimalScale:
                    RequireWire("Decimal.scale", wire, ProtoWireType.Varint);
                    scale = reader.ReadInt32();
                    break;

                case DecimalNullable:
                    RequireWire("Decimal.nullable", wire, ProtoWireType.Varint);
                    nullable = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // The wire types are wider than the model's. Narrow explicitly: an unchecked cast would
        // turn precision 300 into 44 and scale 130 into -126, both of which are inside the arena's
        // accepted range and would therefore produce a wrong dtype rather than an error.
        if (precision > byte.MaxValue)
        {
            ThrowNarrowing("Decimal.precision", precision, 0, byte.MaxValue);
        }

        if (scale is < sbyte.MinValue or > sbyte.MaxValue)
        {
            ThrowNarrowing("Decimal.scale", scale, sbyte.MinValue, sbyte.MaxValue);
        }

        // The remaining range rules (1 <= precision <= MAX_PRECISION, scale <= precision when
        // scale > 0) belong to DTypeArena.Decimal and throw VortexFormatException from there.
        return arena.Decimal((byte)precision, (sbyte)scale, ToNullability(nullable));
    }

    /// <summary>
    /// <c>message Struct { repeated string names = 1; repeated DType dtypes = 2; bool nullable = 3; }</c>
    /// </summary>
    private static DType ReadStruct(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ProtoScratchList<int> names = default;
        ProtoScratchList<DType> fields = default;
        try
        {
            bool nullable = false;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case StructNames:
                        RequireWire("Struct.names", wire, ProtoWireType.LengthDelimited);

                        // Interned straight from the message bytes: no intermediate string.
                        names.Add(arena.InternName(reader.ReadLengthDelimited()));
                        break;

                    case StructDTypes:
                        RequireWire("Struct.dtypes", wire, ProtoWireType.LengthDelimited);
                        fields.Add(ReadNested(ref reader, arena, depth));
                        break;

                    case StructNullable:
                        RequireWire("Struct.nullable", wire, ProtoWireType.Varint);
                        nullable = reader.ReadBool();
                        break;

                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            // A names/dtypes length mismatch is reachable from a file, so DTypeArena.Struct
            // reports it as VortexFormatException rather than as an argument error.
            return arena.Struct(names.Span, fields.Span, ToNullability(nullable));
        }
        finally
        {
            fields.Dispose();
            names.Dispose();
        }
    }

    /// <summary><c>message List { DType element_type = 1; bool nullable = 2; }</c></summary>
    private static DType ReadList(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ReadOnlySpan<byte> element = default;
        bool hasElement = false;
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case ListElementType:
                    RequireWire("List.element_type", wire, ProtoWireType.LengthDelimited);
                    element = reader.ReadLengthDelimited();
                    hasElement = true;
                    break;

                case ListNullable:
                    RequireWire("List.nullable", wire, ProtoWireType.Varint);
                    nullable = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasElement)
        {
            ThrowMissingChild("List.element_type");
        }

        return arena.List(ReadBody(element, arena, depth + 1), ToNullability(nullable));
    }

    /// <summary>
    /// <c>message FixedSizeList { DType element_type = 1; uint32 size = 2; bool nullable = 3; }</c>
    /// </summary>
    private static DType ReadFixedSizeList(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ReadOnlySpan<byte> element = default;
        bool hasElement = false;
        uint size = 0;
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case FslElementType:
                    RequireWire("FixedSizeList.element_type", wire, ProtoWireType.LengthDelimited);
                    element = reader.ReadLengthDelimited();
                    hasElement = true;
                    break;

                case FslSize:
                    RequireWire("FixedSizeList.size", wire, ProtoWireType.Varint);
                    size = reader.ReadVarint32();
                    break;

                case FslNullable:
                    RequireWire("FixedSizeList.nullable", wire, ProtoWireType.Varint);
                    nullable = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasElement)
        {
            ThrowMissingChild("FixedSizeList.element_type");
        }

        return arena.FixedSizeList(ReadBody(element, arena, depth + 1), size, ToNullability(nullable));
    }

    /// <summary>
    /// <c>message Extension { string id = 1; DType storage_dtype = 2; optional bytes metadata = 3; }</c>
    /// </summary>
    private static DType ReadExtension(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ReadOnlySpan<byte> id = default;
        ReadOnlySpan<byte> storage = default;
        ReadOnlySpan<byte> metadata = default;
        bool hasStorage = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case ExtensionId:
                    RequireWire("Extension.id", wire, ProtoWireType.LengthDelimited);
                    id = reader.ReadLengthDelimited();
                    break;

                case ExtensionStorage:
                    RequireWire("Extension.storage_dtype", wire, ProtoWireType.LengthDelimited);
                    storage = reader.ReadLengthDelimited();
                    hasStorage = true;
                    break;

                case ExtensionMetadata:
                    RequireWire("Extension.metadata", wire, ProtoWireType.LengthDelimited);

                    // `optional bytes` distinguishes absent from empty on the wire; the model does
                    // not (see DType.ExtensionMetadata), so the distinction is dropped here rather
                    // than half-preserved.
                    metadata = reader.ReadLengthDelimited();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasStorage)
        {
            ThrowMissingChild("Extension.storage_dtype");
        }

        // Extension has no `nullable` field: its nullability is exactly the storage dtype's.
        return arena.Extension(id, ReadBody(storage, arena, depth + 1), metadata);
    }

    /// <summary>
    /// <c>message Union { repeated string names = 1; repeated DType dtypes = 2;
    /// repeated int32 type_ids = 3; bool nullable = 4; }</c>
    /// </summary>
    private static DType ReadUnion(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ProtoScratchList<int> names = default;
        ProtoScratchList<DType> fields = default;
        ProtoScratchList<byte> typeIds = default;
        try
        {
            bool nullable = false;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case UnionNames:
                        RequireWire("Union.names", wire, ProtoWireType.LengthDelimited);
                        names.Add(arena.InternName(reader.ReadLengthDelimited()));
                        break;

                    case UnionDTypes:
                        RequireWire("Union.dtypes", wire, ProtoWireType.LengthDelimited);
                        fields.Add(ReadNested(ref reader, arena, depth));
                        break;

                    case UnionTypeIds:
                        ReadTypeIds(ref reader, wire, ref typeIds);
                        break;

                    case UnionNullable:
                        RequireWire("Union.nullable", wire, ProtoWireType.Varint);
                        nullable = reader.ReadBool();
                        break;

                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            return arena.Union(names.Span, fields.Span, typeIds.Span, ToNullability(nullable));
        }
        finally
        {
            typeIds.Dispose();
            fields.Dispose();
            names.Dispose();
        }
    }

    /// <summary>
    /// <c>repeated int32 type_ids = 3</c>. proto3 packs repeated scalars by default, but the
    /// unpacked framing stays legal forever and a conforming reader has to accept both.
    /// </summary>
    private static void ReadTypeIds(ref ProtoReader reader, ProtoWireType wire, ref ProtoScratchList<byte> typeIds)
    {
        if (wire == ProtoWireType.LengthDelimited)
        {
            ProtoReader packed = reader.ReadMessage();
            while (!packed.End)
            {
                typeIds.Add(NarrowTypeId(packed.ReadInt32()));
            }

            return;
        }

        if (wire == ProtoWireType.Varint)
        {
            typeIds.Add(NarrowTypeId(reader.ReadInt32()));
            return;
        }

        ThrowWireType("Union.type_ids", wire, ProtoWireType.LengthDelimited);
    }

    /// <summary>
    /// <c>message Map { DType key_type = 1; DType value_type = 2; bool keys_sorted = 3; bool nullable = 4; }</c>
    /// </summary>
    private static DType ReadMap(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        ReadOnlySpan<byte> key = default;
        ReadOnlySpan<byte> value = default;
        bool hasKey = false;
        bool hasValue = false;
        bool keysSorted = false;
        bool nullable = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case MapKeyType:
                    RequireWire("Map.key_type", wire, ProtoWireType.LengthDelimited);
                    key = reader.ReadLengthDelimited();
                    hasKey = true;
                    break;

                case MapValueType:
                    RequireWire("Map.value_type", wire, ProtoWireType.LengthDelimited);
                    value = reader.ReadLengthDelimited();
                    hasValue = true;
                    break;

                case MapKeysSorted:
                    RequireWire("Map.keys_sorted", wire, ProtoWireType.Varint);
                    keysSorted = reader.ReadBool();
                    break;

                case MapNullable:
                    RequireWire("Map.nullable", wire, ProtoWireType.Varint);
                    nullable = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasKey)
        {
            ThrowMissingChild("Map.key_type");
        }

        if (!hasValue)
        {
            ThrowMissingChild("Map.value_type");
        }

        // Both children are materialised before the parent, and key precedes value in wire order.
        DType keyType = ReadBody(key, arena, depth + 1);
        DType valueType = ReadBody(value, arena, depth + 1);
        return arena.Map(keyType, valueType, keysSorted, ToNullability(nullable));
    }

    private static DType ReadNested(ref ProtoReader reader, DTypeArena arena, int depth)
    {
        ProtoReader nested = reader.ReadMessage();
        return Read(ref nested, arena, depth + 1);
    }

    private static DType ReadBody(ReadOnlySpan<byte> body, DTypeArena arena, int depth)
    {
        ProtoReader reader = new ProtoReader(body);
        return Read(ref reader, arena, depth);
    }

    /// <summary>Walks a body that has no known fields, so a corrupt encoding is still reported.</summary>
    private static void DrainUnknown(ReadOnlySpan<byte> body)
    {
        ProtoReader reader = new ProtoReader(body);
        while (reader.TryReadTag(out _, out ProtoWireType wire))
        {
            reader.SkipField(wire);
        }
    }

    // ---------------------------------------------------------------------------------- writing

    /// <summary>
    /// Writes the body of a <c>vortex.dtype.DType</c> message — a single <c>dtype_type</c> case
    /// field, with no outer tag and no outer length prefix.
    /// </summary>
    /// <param name="writer">Writer to append to. Must be passed by reference: it is a mutable struct.</param>
    /// <param name="dtype">The dtype to encode.</param>
    /// <exception cref="ArgumentException"><paramref name="dtype"/> is <c>default(DType)</c>.</exception>
    /// <remarks>
    /// Two encodings are deliberately lossy, in both cases because the model cannot represent the
    /// difference: an empty <c>Extension.metadata</c> is written as absent rather than as a
    /// present-but-empty <c>optional bytes</c>, and a <see cref="DTypeKind.Null"/> dtype's
    /// nullability is not written at all (the schema has no field for it).
    /// </remarks>
    public static void Write(ref ProtoWriter writer, DType dtype)
    {
        if (dtype.IsDefault)
        {
            ThrowDefaultDType();
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Null:
            {
                // `message Null {}` — an empty nested message, which is two bytes: tag + length 0.
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseNull);
                scope.End();
                break;
            }

            case DTypeKind.Bool:
                WriteLeaf(ref writer, CaseBool, dtype.IsNullable);
                break;

            case DTypeKind.Primitive:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CasePrimitive);
                writer.WriteEnum(PrimitiveType, (int)dtype.PType);
                writer.WriteBool(PrimitiveNullable, dtype.IsNullable);
                scope.End();
                break;
            }

            case DTypeKind.Decimal:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseDecimal);
                writer.WriteUInt32(DecimalPrecision, dtype.Precision);
                writer.WriteInt32(DecimalScale, dtype.Scale);
                writer.WriteBool(DecimalNullable, dtype.IsNullable);
                scope.End();
                break;
            }

            case DTypeKind.Utf8:
                WriteLeaf(ref writer, CaseUtf8, dtype.IsNullable);
                break;

            case DTypeKind.Binary:
                WriteLeaf(ref writer, CaseBinary, dtype.IsNullable);
                break;

            case DTypeKind.Struct:
                WriteStruct(ref writer, dtype);
                break;

            case DTypeKind.List:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseList);
                WriteField(ref writer, ListElementType, dtype.ElementType);
                writer.WriteBool(ListNullable, dtype.IsNullable);
                scope.End();
                break;
            }

            case DTypeKind.Extension:
                WriteExtension(ref writer, dtype);
                break;

            case DTypeKind.FixedSizeList:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseFixedSizeList);
                WriteField(ref writer, FslElementType, dtype.ElementType);
                writer.WriteUInt32(FslSize, dtype.FixedSize);
                writer.WriteBool(FslNullable, dtype.IsNullable);
                scope.End();
                break;
            }

            case DTypeKind.Variant:
                WriteLeaf(ref writer, CaseVariant, dtype.IsNullable);
                break;

            case DTypeKind.Union:
                WriteUnion(ref writer, dtype);
                break;

            case DTypeKind.Map:
            {
                ProtoWriter.MessageScope scope = writer.BeginMessage(CaseMap);
                WriteField(ref writer, MapKeyType, dtype.KeyType);
                WriteField(ref writer, MapValueType, dtype.ValueType);
                writer.WriteBool(MapKeysSorted, dtype.KeysSorted);
                writer.WriteBool(MapNullable, dtype.IsNullable);
                scope.End();
                break;
            }

            default:
                ThrowUnknownKind(dtype.Kind);
                break;
        }
    }

    /// <summary>
    /// Writes <paramref name="dtype"/> as a nested <c>DType</c> message under
    /// <paramref name="fieldNumber"/> — tag, length prefix and body.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dtype"/> is <c>default(DType)</c>.</exception>
    public static void WriteField(ref ProtoWriter writer, int fieldNumber, DType dtype)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        Write(ref writer, dtype);
        scope.End();
    }

    /// <summary>
    /// Encodes <paramref name="dtype"/> as a standalone <c>vortex.dtype.DType</c> message body.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dtype"/> is <c>default(DType)</c>.</exception>
    public static byte[] Serialize(DType dtype)
    {
        // A plain local plus try/finally, not `using`: C# forbids passing a `using` variable as a
        // `ref` argument (CS1657), and Write takes the writer by reference.
        ProtoWriter writer = new ProtoWriter(128);
        try
        {
            Write(ref writer, dtype);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteLeaf(ref ProtoWriter writer, int caseNumber, bool nullable)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(caseNumber);
        writer.WriteBool(BoolNullable, nullable);
        scope.End();
    }

    private static void WriteStruct(ref ProtoWriter writer, DType dtype)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(CaseStruct);
        int count = dtype.FieldCount;

        // Field-number order, which is what prost emits: every name, then every dtype. The two
        // lists pair by ordinal, so every element must be emitted even when it encodes to nothing
        // -- hence the `...Always` writer. WriteStringUtf8 would drop an empty field name and
        // silently shift the pairing of everything after it.
        for (int i = 0; i < count; i++)
        {
            writer.WriteStringUtf8Always(StructNames, dtype.GetFieldNameUtf8(i));
        }

        for (int i = 0; i < count; i++)
        {
            WriteField(ref writer, StructDTypes, dtype.GetField(i));
        }

        writer.WriteBool(StructNullable, dtype.IsNullable);
        scope.End();
    }

    private static void WriteExtension(ref ProtoWriter writer, DType dtype)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(CaseExtension);
        writer.WriteStringUtf8(ExtensionId, dtype.ExtensionIdUtf8);
        WriteField(ref writer, ExtensionStorage, dtype.StorageType);
        writer.WriteBytes(ExtensionMetadata, dtype.ExtensionMetadata);
        scope.End();
    }

    private static void WriteUnion(ref ProtoWriter writer, DType dtype)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(CaseUnion);
        int count = dtype.FieldCount;
        for (int i = 0; i < count; i++)
        {
            writer.WriteStringUtf8Always(UnionNames, dtype.GetFieldNameUtf8(i));
        }

        for (int i = 0; i < count; i++)
        {
            WriteField(ref writer, UnionDTypes, dtype.GetField(i));
        }

        if (count != 0)
        {
            // Packed, the proto3 default for a repeated scalar. An empty packed field is omitted
            // entirely, which is why the count is tested first.
            ProtoWriter.MessageScope ids = writer.BeginMessage(UnionTypeIds);
            for (int i = 0; i < count; i++)
            {
                writer.WriteVarint(dtype.GetTypeId(i));
            }

            ids.End();
        }

        writer.WriteBool(UnionNullable, dtype.IsNullable);
        scope.End();
    }

    // ---------------------------------------------------------------------------------- helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Nullability ToNullability(bool nullable) =>
        nullable ? Nullability.Nullable : Nullability.NonNullable;

    private static byte NarrowTypeId(int value)
    {
        // The schema comment on Union.type_ids says "each value must fit in uint8". The unsigned
        // compare rejects negatives as well, in one branch.
        if ((uint)value > byte.MaxValue)
        {
            ThrowNarrowing("Union.type_ids", value, 0, byte.MaxValue);
        }

        return (byte)value;
    }

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
            $"{what} arrived with wire type {actual}; spec/proto/dtype.proto declares it as {expected}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoCase() =>
        throw new VortexFormatException(
            "A vortex.dtype.DType message set none of the 13 dtype_type cases; a dtype is required.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUndefinedPType(int rawType) =>
        throw new VortexFormatException(
            $"PType tag {rawType} is not defined; spec/proto/dtype.proto defines 0..{PTypeExtensions.MaxPType}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNarrowing(string what, long value, long min, long max) =>
        throw new VortexFormatException($"{what} is {value}, outside the representable range [{min}, {max}].");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingChild(string what) =>
        throw new VortexFormatException($"{what} is absent; the nested dtype is required.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnknownKind(DTypeKind kind) =>
        throw new VortexFormatException($"DTypeKind {(byte)kind} has no vortex.dtype.DType case.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefaultDType() =>
        throw new ArgumentException("A default(DType) belongs to no arena and cannot be encoded.", "dtype");
}

/// <summary>
/// A stack-only, pooled accumulator for a repeated field whose length is unknown until the field
/// ends. Nothing is rented until the first element arrives, so a leaf dtype touches no pool.
/// </summary>
/// <remarks>
/// Shared by <see cref="DTypeProtobuf"/> (struct and union fields) and
/// <see cref="ScalarProtobuf"/> (list elements). Dispose it in a <c>finally</c>: it is a
/// <c>ref struct</c> and cannot be a <c>using</c> subject in every position the codecs need.
/// </remarks>
internal ref struct ProtoScratchList<T>
{
    private const int InitialCapacity = 8;

    private T[]? _array;
    private int _count;

    /// <summary>Number of elements added so far.</summary>
    internal readonly int Count => _count;

    /// <summary>The elements, in insertion order. Valid until the next <see cref="Add"/> or <see cref="Dispose"/>.</summary>
    internal readonly ReadOnlySpan<T> Span => _array is null ? default : new ReadOnlySpan<T>(_array, 0, _count);

    /// <summary>Appends one element, renting or growing the pooled backing array as needed.</summary>
    internal void Add(T item)
    {
        T[]? array = _array;
        if (array is null)
        {
            array = ArrayPool<T>.Shared.Rent(InitialCapacity);
            _array = array;
        }
        else if (_count == array.Length)
        {
            array = Grow();
        }

        array[_count++] = item;
    }

    /// <summary>Returns the pooled array. Safe to call more than once.</summary>
    internal void Dispose()
    {
        T[]? array = _array;
        _array = null;
        _count = 0;
        if (array is not null)
        {
            // Clearing matters for DType and ScalarValue: both hold an arena reference that would
            // otherwise stay rooted by the pool for as long as the bucket lives.
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] Grow()
    {
        T[] previous = _array!;
        long doubled = (long)previous.Length * 2;
        if (doubled > Array.MaxLength)
        {
            ThrowTooManyElements();
        }

        T[] next = ArrayPool<T>.Shared.Rent((int)doubled);
        Array.Copy(previous, next, _count);
        ArrayPool<T>.Shared.Return(previous, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _array = next;
        return next;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooManyElements() =>
        throw new VortexFormatException("A repeated Protobuf field declared more elements than an array can hold.");
}
