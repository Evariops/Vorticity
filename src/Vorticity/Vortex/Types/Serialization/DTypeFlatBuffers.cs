using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Types.Serialization;

/// <summary>
/// Reads and writes the <c>vortex.dtype.DType</c> FlatBuffers table.
/// </summary>
/// <remarks>
/// <para>
/// Every tag and field id here is transcribed from the vendored schema rather than recalled,
/// because a union occupies two vtable slots — the <c>ubyte</c> discriminant at field id 0, the
/// value's uoffset at field id 1 — and swapping them yields a codec that decodes the wrong table
/// while still round-tripping against itself.
/// </para>
/// <para>
/// Reading is validate-at-access: every offset comes back through <see cref="FlatBufferTable"/>,
/// which bounds-checks it against the real buffer before dereferencing it. Nothing malformed
/// escapes as anything but <see cref="VortexFormatException"/>.
/// </para>
/// <para>
/// Neither direction allocates beyond the arena's own growth and pooled scratch for the parallel
/// vectors of a struct or a union, whose length is known only once the vector is resolved.
/// </para>
/// </remarks>
internal static class DTypeFlatBuffers
{
    // ---- union Type tags. Identical to DTypeKind by construction; asserted by the tests. ----
    private const byte TagNone = 0;
    private const byte TagNull = 1;
    private const byte TagBool = 2;
    private const byte TagPrimitive = 3;
    private const byte TagDecimal = 4;
    private const byte TagUtf8 = 5;
    private const byte TagBinary = 6;
    private const byte TagStruct = 7;
    private const byte TagList = 8;
    private const byte TagExtension = 9;
    private const byte TagFixedSizeList = 10;
    private const byte TagVariant = 11;
    private const byte TagUnion = 12;
    private const byte TagMap = 13;

    /// <summary>
    /// <c>table DType { type: Type; }</c> — the union's <c>ubyte</c> discriminant. A FlatBuffers
    /// union occupies two slots and the tag is the first of them.
    /// </summary>
    private const int DTypeTypeTag = 0;

    /// <summary>The uoffset to the union's value table: the second of the union's two slots.</summary>
    private const int DTypeTypeValue = 1;

    // ---- field ids inside each case table, in schema declaration order ----

    /// <summary><c>Bool</c>, <c>Utf8</c>, <c>Binary</c> and <c>Variant</c> all declare only <c>nullable</c>.</summary>
    private const int LeafNullable = 0;

    private const int PrimitivePType = 0;
    private const int PrimitiveNullable = 1;

    private const int DecimalPrecision = 0;
    private const int DecimalScale = 1;
    private const int DecimalNullable = 2;

    private const int StructNames = 0;
    private const int StructDTypes = 1;
    private const int StructNullable = 2;

    private const int ListElementType = 0;
    private const int ListNullable = 1;

    private const int FslElementType = 0;
    private const int FslSize = 1;
    private const int FslNullable = 2;

    private const int ExtensionId = 0;
    private const int ExtensionStorage = 1;
    private const int ExtensionMetadata = 2;

    private const int UnionNames = 0;
    private const int UnionDTypes = 1;
    private const int UnionTypeIds = 2;
    private const int UnionNullable = 3;

    private const int MapKeyType = 0;
    private const int MapValueType = 1;
    private const int MapKeysSorted = 2;
    private const int MapNullable = 3;

    /// <summary>Initial builder capacity for <see cref="Serialize"/>. Grows on demand.</summary>
    private const int SerializeCapacity = 256;

    /// <summary>
    /// State carried through one dtype walk over one buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decoded graph is a DAG: forward-only uoffsets exclude cycles, but two slots may resolve
    /// to the same child table, and walking a DAG as a tree costs one visit per path — 2^depth for
    /// a Map whose key and value are the same table. Memoising every position would fix that and
    /// allocate a dictionary on every read, including the overwhelming majority of reads that need
    /// none, so the memo is switched on only once sharing is proven.
    /// </para>
    /// <para>
    /// The proof is a counting argument. Every visit is reached through a distinct 4-byte uoffset
    /// slot (the root offset, a table field, or a vector element), so a buffer holding no shared
    /// child admits at most <c>length / 4</c> visits. Exceeding that means some position is being
    /// revisited, and from that point the memo bounds the remaining work by the number of distinct
    /// positions. Total work stays O(buffer length) and a real file never allocates.
    /// </para>
    /// </remarks>
    private struct DTypeWalk
    {
        /// <summary>Visits left before the memo is switched on. See the counting argument above.</summary>
        public int TreeBudget;

        /// <summary>Table position to already-decoded dtype. Null until <see cref="TreeBudget"/> runs out.</summary>
        public Dictionary<int, DType>? Memo;

        public static DTypeWalk ForBuffer(int bufferLength) => new()
        {
            // + 8 rather than exactly length / 4: the bound only has to be an upper bound on a
            // tree's visits, and the slack keeps a tiny buffer from tripping the memo.
            TreeBudget = (bufferLength >> 2) + 8,
            Memo = null,
        };
    }

    // ---------------------------------------------------------------------------------- reading

    /// <summary>
    /// Reads a finished FlatBuffer whose <c>root_type</c> is <c>DType</c>.
    /// </summary>
    /// <param name="buffer">The whole FlatBuffer, starting at its root uoffset.</param>
    /// <param name="arena">Arena the resulting nodes are interned into.</param>
    /// <returns>A handle into <paramref name="arena"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The buffer is truncated, an offset escapes it, the union tag is absent or undefined,
    /// a required child is absent, a <see cref="PType"/> is undefined, two parallel vectors differ
    /// in length, or nesting exceeds <see cref="VortexLimits.MaxDTypeDepth"/>.
    /// </exception>
    public static DType Read(ReadOnlySpan<byte> buffer, DTypeArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        // Two independent bounds on the traversal, because depth bounds neither of them:
        // the FlatBuffers layer refuses to visit more than MaxFlatBufferTables tables in total,
        // and the walk below stops re-decoding a shared position once sharing is proven.
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        FlatBufferTable root = FlatBufferTable.Root(buffer, ref tableBudget);
        DTypeWalk walk = DTypeWalk.ForBuffer(buffer.Length);
        return ReadTableCore(in root, arena, 0, ref walk);
    }

    /// <summary>
    /// Reads a <c>DType</c> table reached from another FlatBuffer — <c>Struct_.dtypes[i]</c>,
    /// <c>List.element_type</c>, a footer's schema field, and so on.
    /// </summary>
    /// <param name="dtypeTable">The <c>DType</c> table itself, not its union value table.</param>
    /// <param name="arena">Arena the resulting nodes are interned into.</param>
    /// <param name="depth">
    /// Zero-based dtype nesting depth, checked against <see cref="VortexLimits.MaxDTypeDepth"/> on
    /// entry. The recursion runs before the arena sees anything, so the arena's own
    /// construction-time cap arrives too late to stop a 10 000-deep schema from blowing the stack.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">See <see cref="Read"/>.</exception>
    public static DType ReadTable(in FlatBufferTable dtypeTable, DTypeArena arena, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(arena);
        DTypeWalk walk = DTypeWalk.ForBuffer(dtypeTable.BufferLength);
        return ReadTableCore(in dtypeTable, arena, depth, ref walk);
    }

    /// <summary>
    /// One step of the walk: the depth guard, the shared-position memo, and then the union switch.
    /// </summary>
    private static DType ReadTableCore(
        in FlatBufferTable dtypeTable, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        // Depth first, and before the memo, so the stack guard still fires on the recursing path.
        VortexLimits.CheckDepth(depth + 1, VortexLimits.MaxDTypeDepth, "DType");

        if (dtypeTable.IsNull)
        {
            ThrowMissingChild("DType");
        }

        int position = dtypeTable.Position;
        Dictionary<int, DType>? memo = walk.Memo;
        if (memo is not null && memo.TryGetValue(position, out DType cached))
        {
            // A position already decoded in this buffer decodes to the same dtype however it is
            // reached: the arena interns the node, so this is the handle the walk would rebuild.
            // Skipping the depth check here is safe because the arena recomputes each node's depth
            // from its children's and applies the same cap at construction, so a shared subtree
            // re-reached deeper is still rejected there.
            return cached;
        }

        if (memo is null && --walk.TreeBudget < 0)
        {
            // More visits than a tree of this buffer's size can hold, so children are shared and
            // the walk is a DAG walk. Memoise from here on; a real file never reaches this line.
            memo = walk.Memo = new Dictionary<int, DType>();
        }

        DType result = ReadUnionValue(in dtypeTable, arena, depth, ref walk);

        // Re-read: the memo may have been switched on deeper inside the recursion above.
        memo = walk.Memo;
        if (memo is not null)
        {
            memo[position] = result;
        }

        return result;
    }

    private static DType ReadUnionValue(
        in FlatBufferTable dtypeTable, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        // Field 0 is the ubyte discriminant. An absent slot reads as 0, the union's no-case
        // constant, which is malformed for a table the schema requires to carry a type.
        byte tag = dtypeTable.GetUInt8(DTypeTypeTag);
        if (tag is < TagNull or > TagMap)
        {
            ThrowUndefinedTag(tag);
        }

        // Field 1 is the value table. FlatBuffers writes both slots or neither; a tag without a
        // value is a truncated union, not an optional field.
        FlatBufferTable value = dtypeTable.GetTable(DTypeTypeValue);
        if (value.IsNull)
        {
            ThrowMissingUnionValue(tag);
        }

        switch (tag)
        {
            case TagNull: return arena.Null(Nullability.Nullable);
            case TagBool: return arena.Bool(ReadLeafNullability(in value));
            case TagPrimitive: return ReadPrimitive(in value, arena);
            case TagDecimal: return ReadDecimal(in value, arena);
            case TagUtf8: return arena.Utf8(ReadLeafNullability(in value));
            case TagBinary: return arena.Binary(ReadLeafNullability(in value));
            case TagStruct: return ReadStruct(in value, arena, depth, ref walk);
            case TagList: return ReadList(in value, arena, depth, ref walk);
            case TagExtension: return ReadExtension(in value, arena, depth, ref walk);
            case TagFixedSizeList: return ReadFixedSizeList(in value, arena, depth, ref walk);
            case TagVariant: return arena.Variant(ReadLeafNullability(in value));
            case TagUnion: return ReadUnion(in value, arena, depth, ref walk);
            default: return ReadMap(in value, arena, depth, ref walk);
        }
    }

    /// <summary><c>Bool</c> / <c>Utf8</c> / <c>Binary</c> / <c>Variant</c>: <c>nullable: bool</c> at field 0.</summary>
    private static Nullability ReadLeafNullability(in FlatBufferTable table) =>
        ToNullability(table.GetBool(LeafNullable));

    /// <summary><c>table Primitive { ptype: PType; nullable: bool; }</c></summary>
    private static DType ReadPrimitive(in FlatBufferTable table, DTypeArena arena)
    {
        // `enum PType: uint8` — the whole 0..255 range is expressible, so the tag is validated
        // here rather than cast blindly into the model.
        byte ptype = table.GetUInt8(PrimitivePType);
        if (ptype > PTypeExtensions.MaxPType)
        {
            ThrowUndefinedPType(ptype);
        }

        return arena.Primitive((PType)ptype, ToNullability(table.GetBool(PrimitiveNullable)));
    }

    /// <summary><c>table Decimal { precision: uint8; scale: int8; nullable: bool; }</c></summary>
    private static DType ReadDecimal(in FlatBufferTable table, DTypeArena arena)
    {
        // Unlike the Protobuf side there is nothing to narrow: the schema's widths already match
        // the model's. The range rules (1 <= precision <= MAX_PRECISION, scale <= precision when
        // positive) belong to DTypeArena.Decimal and throw VortexFormatException from there.
        byte precision = table.GetUInt8(DecimalPrecision);
        sbyte scale = table.GetInt8(DecimalScale);
        return arena.Decimal(precision, scale, ToNullability(table.GetBool(DecimalNullable)));
    }

    /// <summary><c>table Struct_ { names: [string]; dtypes: [DType]; nullable: bool; }</c></summary>
    private static DType ReadStruct(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        FlatBufferVector names = table.GetVector(StructNames);
        FlatBufferVector dtypes = table.GetVector(StructDTypes);
        bool nullable = table.GetBool(StructNullable);

        int count = dtypes.Count;
        if (names.Count != count)
        {
            ThrowParallelLengths("Struct_", names.Count, count);
        }

        // Rent at least one element: a zero-length rental is legal but the Math.Max keeps the
        // "the span is a prefix of the rented array" reasoning uniform for every count.
        int[] nameHandles = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        DType[] fields = ArrayPool<DType>.Shared.Rent(Math.Max(count, 1));
        try
        {
            long nameBytes = 0;
            for (int i = 0; i < count; i++)
            {
                nameBytes += names.GetStringUtf8(i).Length;
            }

            for (int i = 0; i < count; i++)
            {
                // Interned straight from the file's bytes: no intermediate string. The names still
                // to come size the tables' growth, so a wide struct grows them once; capped by the
                // buffer, which holds every distinct name however often a shared one is named.
                ReadOnlySpan<byte> name = names.GetStringUtf8(i);
                nameHandles[i] = arena.InternName(name, count - i, (int)Math.Min(nameBytes, table.BufferLength));
                nameBytes -= name.Length;
            }

            for (int i = 0; i < count; i++)
            {
                FlatBufferTable child = dtypes.GetTable(i);
                fields[i] = ReadTableCore(in child, arena, depth + 1, ref walk);
            }

            return arena.Struct(
                new ReadOnlySpan<int>(nameHandles, 0, count),
                new ReadOnlySpan<DType>(fields, 0, count),
                ToNullability(nullable));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(nameHandles);

            // Cleared: a DType holds an arena reference that would otherwise stay rooted by the
            // pool for as long as the bucket lives.
            ArrayPool<DType>.Shared.Return(fields, clearArray: true);
        }
    }

    /// <summary><c>table List { element_type: DType; nullable: bool; }</c></summary>
    private static DType ReadList(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        FlatBufferTable element = table.GetTable(ListElementType);
        if (element.IsNull)
        {
            ThrowMissingChild("List.element_type");
        }

        bool nullable = table.GetBool(ListNullable);
        return arena.List(ReadTableCore(in element, arena, depth + 1, ref walk), ToNullability(nullable));
    }

    /// <summary><c>table FixedSizeList { element_type: DType; size: uint32; nullable: bool; }</c></summary>
    private static DType ReadFixedSizeList(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        FlatBufferTable element = table.GetTable(FslElementType);
        if (element.IsNull)
        {
            ThrowMissingChild("FixedSizeList.element_type");
        }

        // `size` is a uint32 and the model stores a uint32: 0 and uint.MaxValue are both legal
        // here. Whether a zero-size list is *useful* is not this layer's business.
        uint size = table.GetUInt32(FslSize);
        bool nullable = table.GetBool(FslNullable);
        return arena.FixedSizeList(
            ReadTableCore(in element, arena, depth + 1, ref walk), size, ToNullability(nullable));
    }

    /// <summary><c>table Extension { id: string; storage_dtype: DType; metadata: [ubyte]; }</c></summary>
    private static DType ReadExtension(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        // `id` is not marked required in the schema; an absent one reads as the empty string, which
        // is what the Protobuf codec does with an absent `string id = 1` too. Keeping the two
        // codecs identical here is what makes the equivalence property meaningful.
        ReadOnlySpan<byte> id = table.GetStringUtf8(ExtensionId);

        FlatBufferTable storage = table.GetTable(ExtensionStorage);
        if (storage.IsNull)
        {
            ThrowMissingChild("Extension.storage_dtype");
        }

        // `[ubyte]` and optional: absent reads as an empty span, which the model cannot tell from
        // a present-but-empty vector.
        ReadOnlySpan<byte> metadata = table.GetByteVector(ExtensionMetadata);

        // Extension has no `nullable` field: its nullability is exactly the storage dtype's.
        return arena.Extension(id, ReadTableCore(in storage, arena, depth + 1, ref walk), metadata);
    }

    /// <summary>
    /// <c>table Union { names: [string]; dtypes: [DType]; type_ids: [byte]; nullable: bool; }</c>
    /// </summary>
    private static DType ReadUnion(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        FlatBufferVector names = table.GetVector(UnionNames);
        FlatBufferVector dtypes = table.GetVector(UnionDTypes);

        // `type_ids: [byte]` is declared signed but defined as unsigned, so the bytes are taken
        // verbatim: a type id of 255 stays 255, not -1. The
        // element width is 1 either way, so the [ubyte] accessor reads the same bytes.
        ReadOnlySpan<byte> typeIds = table.GetByteVector(UnionTypeIds);
        bool nullable = table.GetBool(UnionNullable);

        int count = dtypes.Count;
        if (names.Count != count)
        {
            ThrowParallelLengths("Union", names.Count, count);
        }

        // The format requires one type id per member dtype.
        if (typeIds.Length != count)
        {
            ThrowTypeIdLength(typeIds.Length, count);
        }

        int[] nameHandles = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        DType[] fields = ArrayPool<DType>.Shared.Rent(Math.Max(count, 1));
        try
        {
            long nameBytes = 0;
            for (int i = 0; i < count; i++)
            {
                nameBytes += names.GetStringUtf8(i).Length;
            }

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> name = names.GetStringUtf8(i);
                nameHandles[i] = arena.InternName(name, count - i, (int)Math.Min(nameBytes, table.BufferLength));
                nameBytes -= name.Length;
            }

            for (int i = 0; i < count; i++)
            {
                FlatBufferTable child = dtypes.GetTable(i);
                fields[i] = ReadTableCore(in child, arena, depth + 1, ref walk);
            }

            return arena.Union(
                new ReadOnlySpan<int>(nameHandles, 0, count),
                new ReadOnlySpan<DType>(fields, 0, count),
                typeIds,
                ToNullability(nullable));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(nameHandles);
            ArrayPool<DType>.Shared.Return(fields, clearArray: true);
        }
    }

    /// <summary>
    /// <c>table Map { key_type: DType; value_type: DType; keys_sorted: bool; nullable: bool; }</c>
    /// </summary>
    private static DType ReadMap(in FlatBufferTable table, DTypeArena arena, int depth, ref DTypeWalk walk)
    {
        FlatBufferTable key = table.GetTable(MapKeyType);
        if (key.IsNull)
        {
            ThrowMissingChild("Map.key_type");
        }

        FlatBufferTable value = table.GetTable(MapValueType);
        if (value.IsNull)
        {
            ThrowMissingChild("Map.value_type");
        }

        bool keysSorted = table.GetBool(MapKeysSorted);
        bool nullable = table.GetBool(MapNullable);

        // Key before value: that is the order the arena stores the two children in, and the order
        // the wire format declares them.
        DType keyType = ReadTableCore(in key, arena, depth + 1, ref walk);
        DType valueType = ReadTableCore(in value, arena, depth + 1, ref walk);
        return arena.Map(keyType, valueType, keysSorted, ToNullability(nullable));
    }

    // ---------------------------------------------------------------------------------- writing

    /// <summary>
    /// Writes the <c>DType</c> table — the union wrapper, not the value table — and returns its
    /// offset. Children are written first, as the back-to-front builder requires.
    /// </summary>
    /// <param name="builder">Builder to append to. No table may be open when this is called.</param>
    /// <param name="dtype">The dtype to encode.</param>
    /// <returns>The offset of the written <c>DType</c> table, for <see cref="FlatBufferBuilder.AddOffset"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dtype"/> is <c>default(DType)</c>.</exception>
    /// <remarks>
    /// A <see cref="DTypeKind.Null"/> dtype's nullability is not written at all — <c>table Null
    /// {}</c> has no field for it. <c>Extension.metadata</c> is always written, even when empty,
    /// because an absent vector is a parse failure for other readers.
    /// </remarks>
    public static int Write(FlatBufferBuilder builder, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return WriteCore(builder, dtype, 0);
    }

    /// <summary>
    /// Builds a complete root buffer holding nothing but <paramref name="dtype"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dtype"/> is <c>default(DType)</c>.</exception>
    public static byte[] Serialize(DType dtype)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder(SerializeCapacity);
        int root = Write(builder, dtype);
        return builder.FinishToArray(root);
    }

    private static int WriteCore(FlatBufferBuilder builder, DType dtype, int depth)
    {
        if (dtype.IsDefault)
        {
            ThrowDefaultDType();
        }

        // A handle from a live arena is already depth-capped, but the guard costs one compare and
        // keeps a stale handle from recursing without bound.
        VortexLimits.CheckDepth(depth + 1, VortexLimits.MaxDTypeDepth, "DType");

        byte tag;
        int value;
        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                tag = TagNull;

                // `table Null {}` — an empty table: soffset plus a two-entry vtable, no fields.
                builder.StartTable();
                value = builder.EndTable();
                break;

            case DTypeKind.Bool:
                tag = TagBool;
                value = WriteLeaf(builder, dtype.IsNullable);
                break;

            case DTypeKind.Primitive:
                tag = TagPrimitive;
                builder.StartTable();
                builder.AddUInt8(PrimitivePType, (byte)dtype.PType);
                builder.AddBool(PrimitiveNullable, dtype.IsNullable);
                value = builder.EndTable();
                break;

            case DTypeKind.Decimal:
                tag = TagDecimal;
                builder.StartTable();
                builder.AddUInt8(DecimalPrecision, dtype.Precision);
                builder.AddInt8(DecimalScale, dtype.Scale);
                builder.AddBool(DecimalNullable, dtype.IsNullable);
                value = builder.EndTable();
                break;

            case DTypeKind.Utf8:
                tag = TagUtf8;
                value = WriteLeaf(builder, dtype.IsNullable);
                break;

            case DTypeKind.Binary:
                tag = TagBinary;
                value = WriteLeaf(builder, dtype.IsNullable);
                break;

            case DTypeKind.Struct:
                tag = TagStruct;
                value = WriteStruct(builder, dtype, depth);
                break;

            case DTypeKind.List:
            {
                tag = TagList;
                int element = WriteCore(builder, dtype.ElementType, depth + 1);
                builder.StartTable();
                builder.AddOffset(ListElementType, element);
                builder.AddBool(ListNullable, dtype.IsNullable);
                value = builder.EndTable();
                break;
            }

            case DTypeKind.Extension:
                tag = TagExtension;
                value = WriteExtension(builder, dtype, depth);
                break;

            case DTypeKind.FixedSizeList:
            {
                tag = TagFixedSizeList;
                int element = WriteCore(builder, dtype.ElementType, depth + 1);
                builder.StartTable();
                builder.AddOffset(FslElementType, element);
                builder.AddUInt32(FslSize, dtype.FixedSize);
                builder.AddBool(FslNullable, dtype.IsNullable);
                value = builder.EndTable();
                break;
            }

            case DTypeKind.Variant:
                tag = TagVariant;
                value = WriteLeaf(builder, dtype.IsNullable);
                break;

            case DTypeKind.Union:
                tag = TagUnion;
                value = WriteUnion(builder, dtype, depth);
                break;

            case DTypeKind.Map:
            {
                tag = TagMap;

                // Both children are complete objects before the Map table opens; key first,
                // matching the wire order.
                int key = WriteCore(builder, dtype.KeyType, depth + 1);
                int mapValue = WriteCore(builder, dtype.ValueType, depth + 1);
                builder.StartTable();
                builder.AddOffset(MapKeyType, key);
                builder.AddOffset(MapValueType, mapValue);
                builder.AddBool(MapKeysSorted, dtype.KeysSorted);
                builder.AddBool(MapNullable, dtype.IsNullable);
                value = builder.EndTable();
                break;
            }

            default:
                tag = TagNone;
                value = 0;
                ThrowUnknownKind(dtype.Kind);
                break;
        }

        builder.StartTable();

        // Slot 0 is the discriminant, slot 1 the value. AddUInt8 omits a field equal to its
        // default, but every tag here is 1..13, so the tag is always emitted.
        builder.AddUInt8(DTypeTypeTag, tag);
        builder.AddOffset(DTypeTypeValue, value);
        return builder.EndTable();
    }

    private static int WriteLeaf(FlatBufferBuilder builder, bool nullable)
    {
        builder.StartTable();
        builder.AddBool(LeafNullable, nullable);
        return builder.EndTable();
    }

    private static int WriteStruct(FlatBufferBuilder builder, DType dtype, int depth)
    {
        int count = dtype.FieldCount;

        // One scratch array serves both vectors in turn: the name offsets are consumed by
        // CreateOffsetVector before the child offsets overwrite them.
        int[] offsets = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            for (int i = 0; i < count; i++)
            {
                offsets[i] = builder.CreateStringUtf8(dtype.GetFieldNameUtf8(i));
            }

            int namesVector = builder.CreateOffsetVector(new ReadOnlySpan<int>(offsets, 0, count));

            for (int i = 0; i < count; i++)
            {
                offsets[i] = WriteCore(builder, dtype.GetField(i), depth + 1);
            }

            int dtypesVector = builder.CreateOffsetVector(new ReadOnlySpan<int>(offsets, 0, count));

            builder.StartTable();
            builder.AddOffset(StructNames, namesVector);
            builder.AddOffset(StructDTypes, dtypesVector);
            builder.AddBool(StructNullable, dtype.IsNullable);
            return builder.EndTable();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private static int WriteUnion(FlatBufferBuilder builder, DType dtype, int depth)
    {
        int count = dtype.FieldCount;
        int[] offsets = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        byte[] typeIds = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
        try
        {
            for (int i = 0; i < count; i++)
            {
                offsets[i] = builder.CreateStringUtf8(dtype.GetFieldNameUtf8(i));
            }

            int namesVector = builder.CreateOffsetVector(new ReadOnlySpan<int>(offsets, 0, count));

            for (int i = 0; i < count; i++)
            {
                offsets[i] = WriteCore(builder, dtype.GetField(i), depth + 1);
            }

            int dtypesVector = builder.CreateOffsetVector(new ReadOnlySpan<int>(offsets, 0, count));

            for (int i = 0; i < count; i++)
            {
                typeIds[i] = dtype.GetTypeId(i);
            }

            // `[byte]` and `[ubyte]` share the same one-byte-per-element encoding, so the byte
            // vector writer produces exactly the signed vector the schema declares.
            int typeIdsVector = builder.CreateByteVector(new ReadOnlySpan<byte>(typeIds, 0, count));

            builder.StartTable();
            builder.AddOffset(UnionNames, namesVector);
            builder.AddOffset(UnionDTypes, dtypesVector);
            builder.AddOffset(UnionTypeIds, typeIdsVector);
            builder.AddBool(UnionNullable, dtype.IsNullable);
            return builder.EndTable();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(typeIds);
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private static int WriteExtension(FlatBufferBuilder builder, DType dtype, int depth)
    {
        int id = builder.CreateStringUtf8(dtype.ExtensionIdUtf8);
        int storage = WriteCore(builder, dtype.StorageType, depth + 1);

        // The vector is written present but empty, never omitted: the reference implementation
        // treats an absent `metadata` as a parse failure, so an extension whose metadata is empty
        // would be unreadable elsewhere. A round trip through this codec alone cannot show the
        // difference, because the reader collapses absent with present-but-empty.
        ReadOnlySpan<byte> metadata = dtype.ExtensionMetadata;
        int metadataVector = builder.CreateByteVector(metadata);

        builder.StartTable();
        builder.AddOffset(ExtensionId, id);
        builder.AddOffset(ExtensionStorage, storage);
        builder.AddOffset(ExtensionMetadata, metadataVector);
        return builder.EndTable();
    }

    // ---------------------------------------------------------------------------------- helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Nullability ToNullability(bool nullable) =>
        nullable ? Nullability.Nullable : Nullability.NonNullable;

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUndefinedTag(byte tag) =>
        throw new VortexFormatException(
            tag == TagNone
                ? "The DType table's union tag is NONE (0); spec/flatbuffers/dtype.fbs requires " +
                  "one of the 13 Type cases."
                : $"DType union tag {tag} is not defined; spec/flatbuffers/dtype.fbs defines " +
                  $"{TagNull}..{TagMap}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingUnionValue(byte tag) =>
        throw new VortexFormatException(
            $"The DType table declares union tag {tag} but carries no value table; a FlatBuffers " +
            "union writes both of its two slots or neither.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingChild(string what) =>
        throw new VortexFormatException($"{what} is absent; the nested dtype is required.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowParallelLengths(string what, int names, int dtypes) =>
        throw new VortexFormatException(
            $"{what}.names.len() = {names} does not match dtypes.len() = {dtypes}; " +
            "spec/flatbuffers/dtype.fbs declares them as parallel vectors.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTypeIdLength(int typeIds, int dtypes) =>
        throw new VortexFormatException(
            $"Union.type_ids.len() = {typeIds} does not match dtypes.len() = {dtypes}; " +
            "spec/flatbuffers/dtype.fbs: \"length must equal dtypes.len()\".");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUndefinedPType(byte ptype) =>
        throw new VortexFormatException(
            $"PType tag {ptype} is not defined; spec/flatbuffers/dtype.fbs defines " +
            $"0..{PTypeExtensions.MaxPType}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnknownKind(DTypeKind kind) =>
        throw new VortexFormatException($"DTypeKind {(byte)kind} has no dtype.fbs union case.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefaultDType() =>
        throw new ArgumentException("A default(DType) belongs to no arena and cannot be encoded.", "dtype");
}
