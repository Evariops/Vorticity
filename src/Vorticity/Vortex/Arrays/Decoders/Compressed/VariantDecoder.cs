using System;
using System.Numerics;
using System.Runtime.InteropServices;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Types.Variant;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.variant</c>: a logical variant column over Variant-typed storage, and a second
/// child, when there is one, holding paths shredded out of it. The storage child carries the whole
/// of an unshredded variant's work, so this decoder only checks the shape it produced. A shredded
/// child is put back into the storage's values (<see cref="ShreddedTree"/>), so a variant reads the
/// same shredded or not.
/// </summary>
internal sealed class VariantDecoder : ArrayDecoder
{
    private const string Id = "vortex.variant";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly VariantDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.variant"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Variant;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// The logical spelling is transparent to a take: it hands back its one child's node with a
    /// shape check and nothing else, so the rows it is asked for are the rows the child is asked for.
    /// </summary>
    /// <remarks>
    /// Without this the default route would decode the whole node and then gather, expanding every
    /// row of the storage to deliver a handful. The shape check runs against the selected length,
    /// because that is the node's length on this path; everything above it is the same code, which
    /// is why both routes share one core.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        VariantMetadata metadata = VariantMetadata.Read(node.Metadata);

        if (dtype.Kind != DTypeKind.Variant)
        {
            CompressedThrow.Format($"{Id} requires a variant dtype; the node declares {dtype.Kind}.");
        }

        ArrayDecodeContext.RequireChildCount(node.ChildCount, metadata.HasShreddedDType ? 2 : 1, Id);

        // The core storage wears the same dtype as the node, so the child decodes at the variant
        // dtype and whatever encoding it uses has to produce the `Struct{metadata, value}` form.
        // An encoding that does not is refused below rather than reinterpreted.
        int core = selective
            ? context.DecodeChildSelected(in node, 0, dtype, length, wanted)
            : context.DecodeChild(in node, 0, dtype, length);
        CanonicalNode storage = context.Canonical.GetNode(core);
        VariantStorage.Require(storage, selective ? wanted.Length : length, Id);
        if (!metadata.HasShreddedDType)
        {
            return core;
        }

        DType shreddedType = DTypeProtobuf.Read(metadata.ShreddedDType, context.Types);
        int shredded = selective
            ? context.DecodeChildSelected(in node, 1, shreddedType, length, wanted)
            : context.DecodeChild(in node, 1, shreddedType, length);
        return ShreddedTree.Assemble(context, dtype, core, shredded);
    }
}

/// <summary>
/// The shredded child of a <c>vortex.variant</c> put back into its core storage's values. The child
/// is the tree the reference builds from a Parquet shredding: an object is a struct by field name,
/// an array a list, a primitive its typed column, and a field that kept values of its own beside its
/// typed column is a nested variant, which its own decoder has already put back together.
/// </summary>
/// <remarks>
/// Each node of the tree maps onto the group the Parquet standard shreds into, a value and a typed
/// value: a nested variant is a group of its value alone, valid where the variant is, and any other
/// node a group of it as the typed value. The core storage's value is the residual of the variant's
/// own group. The rows are then rebuilt by the plan the Parquet reader uses
/// (<see cref="ShreddedVariant"/>), which no row of this mapping is copied for: the new nodes share
/// the decoded ones' buffers. A nested variant's values are read against the variant's metadata,
/// which the reference gives every field of an object.
/// </remarks>
internal static class ShreddedTree
{
    private const string Id = "vortex.variant";

    /// <summary>Fields a struct holds before its scratch goes to the heap.</summary>
    private const int StackFields = 32;

    /// <summary>The variant node of the core storage <paramref name="core"/> and the shredded tree <paramref name="shredded"/>.</summary>
    /// <param name="context">The decode context both were decoded in.</param>
    /// <param name="variant">The variant dtype the result wears.</param>
    /// <param name="core">The core storage, <c>Struct{metadata, value}</c>.</param>
    /// <param name="shredded">The shredded tree, of the same rows.</param>
    /// <returns>A <c>Struct{metadata, value}</c> node wearing <paramref name="variant"/>.</returns>
    /// <exception cref="VortexFormatException">The tree is not one a shredding builds.</exception>
    internal static int Assemble(ArrayDecodeContext context, DType variant, int core, int shredded)
    {
        CanonicalArena arena = context.Canonical;
        DTypeArena types = context.Types;
        CanonicalNode storage = arena.GetNode(core);
        if (arena.GetNode(shredded).Length != storage.Length)
        {
            CompressedThrow.ChildLength(Id, "shredded", arena.GetNode(shredded).Length, storage.Length);
        }

        int metadata = storage.GetFieldIndex(0);
        int value = storage.GetFieldIndex(1);
        int typed = Typed(arena, types, shredded, depth: 1, out DType typedType);
        DType group = types.Struct(
            ["metadata", "value", "typed_value"],
            [arena.GetNode(metadata).DType, arena.GetNode(value).DType, typedType],
            variant.Nullability);
        Span<int> fields = [metadata, value, typed];
        int node = arena.AddStruct(group, storage.Length, storage.Validity, fields);
        try
        {
            return ShreddedVariant.Compile(VortexTypes.FromDType(group), types).Assemble(arena, variant, node);
        }
        catch (VortexFormatException malformed)
        {
            CompressedThrow.Format($"{Id}'s shredded child is not what a shredding builds: {malformed.Message}");
            throw;
        }
    }

    /// <summary>The group a node of the tree stands for: of its value alone when it is a nested variant, of it as the typed value otherwise.</summary>
    private static int Group(CanonicalArena arena, DTypeArena types, int node, int depth, out DType group)
    {
        node = Plain(arena, node);
        CanonicalNode n = arena.GetNode(node);
        if (n.DType.Kind == DTypeKind.Variant)
        {
            VariantStorage.Require(n, n.Length, Id);
            int value = n.GetFieldIndex(1);
            group = types.Struct(["value"], [arena.GetNode(value).DType], Nullability.Nullable);
            return arena.AddStruct(group, n.Length, n.Validity, [value]);
        }

        int typed = Typed(arena, types, node, depth, out DType typedType);
        group = types.Struct(["typed_value"], [typedType], Nullability.Nullable);
        return arena.AddStruct(group, n.Length, Validity.FromNullability(Nullability.Nullable), [typed]);
    }

    /// <summary>The typed value a node of the tree is: an object's fields and an array's elements each made a group, a primitive as it is.</summary>
    private static int Typed(CanonicalArena arena, DTypeArena types, int node, int depth, out DType typed)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Shredded variant");
        node = Plain(arena, node);
        CanonicalNode n = arena.GetNode(node);
        switch (n.DType.Kind)
        {
            case DTypeKind.Struct:
            {
                int count = n.FieldCount;
                Span<int> fieldStack = stackalloc int[StackFields];
                Span<int> nameStack = stackalloc int[StackFields];
                using Scratch<int> fields = new(count, fieldStack);
                using Scratch<int> names = new(count, nameStack);
                DType[] groups = new DType[count];
                for (int i = 0; i < count; i++)
                {
                    fields.Span[i] = Group(arena, types, n.GetFieldIndex(i), depth + 1, out groups[i]);
                    names.Span[i] = types.InternName(n.DType.GetFieldNameUtf8(i));
                }

                typed = types.Struct(names.Span, groups, n.DType.Nullability);
                return arena.AddStruct(typed, n.Length, n.Validity, fields.Span);
            }

            case DTypeKind.List when n.Kind == CanonicalKind.ListView:
            {
                int elements = Group(arena, types, n.ElementsIndex, depth + 1, out DType element);
                typed = types.List(element, n.DType.Nullability);
                VortexBuffer offsets = Int32s(arena, n.Offsets, n.OffsetPType, n.Length);
                VortexBuffer sizes = Int32s(arena, n.Sizes, n.SizePType, n.Length);
                return arena.AddListView(typed, n.Length, n.Validity, elements, offsets, PType.I32, sizes, PType.I32);
            }

            default:
                typed = n.DType;
                return node;
        }
    }

    /// <summary>
    /// A list's offsets or sizes as the i32 the plan reads: the buffer itself when they are, a copy
    /// otherwise, since a writer picks the narrowest type that holds them.
    /// </summary>
    private static VortexBuffer Int32s(CanonicalArena arena, VortexBuffer source, PType ptype, int rows) => ptype switch
    {
        PType.I32 => source,
        PType.U8 => Int32s<byte>(arena, source, rows),
        PType.U16 => Int32s<ushort>(arena, source, rows),
        PType.U32 => Int32s<uint>(arena, source, rows),
        PType.U64 => Int32s<ulong>(arena, source, rows),
        PType.I8 => Int32s<sbyte>(arena, source, rows),
        PType.I16 => Int32s<short>(arena, source, rows),
        PType.I64 => Int32s<long>(arena, source, rows),
        _ => throw new VortexFormatException($"A shredded list's offsets are of {ptype}, not of integers."),
    };

    private static VortexBuffer Int32s<T>(CanonicalArena arena, VortexBuffer source, int rows)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source.Span)[..rows];
        VortexBuffer buffer = arena.AllocateUninitialized(rows * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> into = MemoryMarshal.Cast<byte, int>(bytes);
        for (int i = 0; i < values.Length; i++)
        {
            // A u64 past the signed range wraps negative here and is refused with the rest.
            long value = long.CreateTruncating(values[i]);
            if ((ulong)value > int.MaxValue)
            {
                throw new VortexFormatException($"A shredded list's offset {values[i]} is past what a list of elements holds.");
            }

            into[i] = (int)value;
        }

        return buffer;
    }

    /// <summary>The node in a form whose rows a plan reads in place: a dictionary, a run-end or a constant expanded.</summary>
    private static int Plain(CanonicalArena arena, int node)
    {
        node = arena.Decoded(node);
        return arena.GetNode(node).Kind == CanonicalKind.Constant ? arena.MaterializeConstant(node) : node;
    }
}

/// <summary>
/// Decodes <c>vortex.parquet.variant</c>: the Arrow-compatible binary spelling, a binary child
/// holding each row's variant metadata, and a binary child of its value, a typed child of the paths
/// shredded out of it, or both. A shredded variant's rows are put back together as the Parquet
/// standard's <c>construct_variant</c> builds them (<see cref="ShreddedVariant"/>), so a variant reads
/// the same shredded or not.
/// </summary>
internal sealed class ParquetVariantDecoder : ArrayDecoder
{
    private const string Id = "vortex.parquet.variant";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ParquetVariantDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.parquet.variant"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ParquetVariant;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// The selection goes into the two binary children, which is where the work is.
    /// </summary>
    /// <remarks>
    /// Nothing here is per-row: the node is a struct of two binary columns, so pushing the wanted
    /// rows down is the whole of the work and the saving is the children's, which then build a view
    /// per delivered row instead of one per row of the node.
    /// <para>
    /// This is only safe because the binary child decoder is specialized too. Declaring
    /// <see cref="SelectsWithoutFullDecode"/> takes the pushed route, which has no retained-chunk
    /// cache, so an unspecialized child on it would decode its whole node once per batch and make
    /// the take quadratic. That pairing spans two decoders, so no unit test can see it.
    /// </para>
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ParquetVariantMetadata metadata = ParquetVariantMetadata.Read(node.Metadata);

        if (dtype.Kind != DTypeKind.Variant)
        {
            CompressedThrow.Format($"{Id} requires a variant dtype; the node declares {dtype.Kind}.");
        }

        if (!metadata.HasValue && !metadata.HasTypedValue)
        {
            CompressedThrow.Format($"{Id} holds neither a value nor a typed_value child.");
        }

        // One child more than expected is allowed: the extra one is an explicit validity child,
        // and it comes first.
        int expected = 1 + (metadata.HasValue ? 1 : 0) + (metadata.HasTypedValue ? 1 : 0);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, expected, expected + 1, Id);
        int first = node.ChildCount == expected + 1 ? 1 : 0;

        DTypeArena types = context.Types;
        DType metadataType = types.Binary(Nullability.NonNullable);
        DType valueType = types.Binary(
            metadata.ValueNullable ? Nullability.Nullable : Nullability.NonNullable);

        // The validity child comes first, where the shared helpers expect it last.
        Validity validity = first == 0
            ? Validity.FromNullability(dtype.Nullability)
            : selective
                ? context.DecodeValidityChildSelected(in node, 0, length, wanted)
                : context.DecodeValidityChild(in node, 0, length);

        int produced = selective ? wanted.Length : length;

        int metadataChild = selective
            ? context.DecodeChildSelected(in node, first, metadataType, length, wanted)
            : context.DecodeChild(in node, first, metadataType, length);
        metadataChild = RequireBinary(context, metadataChild, produced, "metadata");
        int valueChild = -1;
        if (metadata.HasValue)
        {
            valueChild = selective
                ? context.DecodeChildSelected(in node, first + 1, valueType, length, wanted)
                : context.DecodeChild(in node, first + 1, valueType, length);
            valueChild = RequireBinary(context, valueChild, produced, "value");
        }

        if (!metadata.HasTypedValue)
        {
            Span<int> fields = stackalloc int[2];
            fields[0] = metadataChild;
            fields[1] = valueChild;

            // A struct wearing the variant dtype: the canonical shape is `Struct{metadata, value}`
            // while the schema still says variant.
            return context.Canonical.AddStruct(dtype, produced, validity, fields);
        }

        // Shredded: the children make the group the Parquet standard describes, a struct of the
        // metadata, the residual value when there is one and the typed value, valid where the
        // variant is; the plan that rebuilds each row's value from them is the Parquet reader's.
        DType typedType = DTypeProtobuf.Read(metadata.TypedValueDType, types);
        int typed = first + (metadata.HasValue ? 2 : 1);
        int typedChild = selective
            ? context.DecodeChildSelected(in node, typed, typedType, length, wanted)
            : context.DecodeChild(in node, typed, typedType, length);
        if (context.Canonical.GetNode(typedChild).Kind == CanonicalKind.Constant)
        {
            typedChild = context.Canonical.MaterializeConstant(typedChild);
        }

        DType group = metadata.HasValue
            ? types.Struct(["metadata", "value", "typed_value"], [metadataType, valueType, typedType], dtype.Nullability)
            : types.Struct(["metadata", "typed_value"], [metadataType, typedType], dtype.Nullability);
        Span<int> children = stackalloc int[3];
        int count = 0;
        children[count++] = metadataChild;
        if (metadata.HasValue)
        {
            children[count++] = valueChild;
        }

        children[count++] = typedChild;
        int groupNode = context.Canonical.AddStruct(group, produced, validity, children[..count]);
        try
        {
            return ShreddedVariant.Compile(VortexTypes.FromDType(group), types)
                .Assemble(context.Canonical, dtype, groupNode);
        }
        catch (VortexFormatException malformed)
        {
            CompressedThrow.Format($"{Id}'s shredded children are not what the Parquet standard allows: {malformed.Message}");
            throw;
        }
    }

    /// <summary>
    /// The child as a dense binary column: one checked for its kind and length, a constant -- one
    /// metadata for every row is the common case -- expanded as a dense child would have held it.
    /// </summary>
    private static int RequireBinary(ArrayDecodeContext context, int child, int length, string what)
    {
        if (context.Canonical.GetNode(child).Kind == CanonicalKind.Constant)
        {
            child = context.Canonical.MaterializeConstant(child);
        }

        CanonicalNode node = context.Canonical.GetNode(child);
        if (node.Kind != CanonicalKind.VarBinView)
        {
            CompressedThrow.ChildKind(Id, what, node.Kind, "a VarBinView");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(Id, what, node.Length, length);
        }

        return child;
    }
}

/// <summary>
/// The shape both variant spellings canonicalize to. There is no canonical variant kind: a variant
/// node is a two-field struct of the Parquet Variant metadata and value bytes wearing the variant
/// dtype, so both spellings end at the same two binary columns and the writer and the conformance
/// comparer each have one form to handle.
/// </summary>
internal static class VariantStorage
{
    /// <summary>Refuses a node that is not <c>Struct{metadata, value}</c> of the right length.</summary>
    /// <param name="node">The decoded node.</param>
    /// <param name="length">The row count it must have.</param>
    /// <param name="encodingId">The encoding asking, for the message.</param>
    internal static void Require(CanonicalNode node, int length, string encodingId)
    {
        if (node.Kind != CanonicalKind.Struct || node.FieldCount != 2)
        {
            CompressedThrow.Format(
                $"{encodingId}'s storage decoded as {node.Kind}; a variant's canonical form is a " +
                "two-field struct of {metadata, value}.");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(encodingId, "core_storage", node.Length, length);
        }
    }
}
