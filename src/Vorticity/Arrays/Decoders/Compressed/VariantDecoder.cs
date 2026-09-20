using System;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.variant</c>: a logical variant column over Variant-typed storage. The storage
/// child carries the whole of the work, so this decoder only checks the shape it produced. Both
/// variant spellings can also carry a typed child holding paths shredded out of the variant;
/// merging one back is not implemented and such a file is refused by name rather than read
/// partially.
/// </summary>
public sealed class VariantDecoder : ArrayDecoder
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

        if (metadata.HasShreddedDType)
        {
            throw new VortexUnsupportedException(
                Id,
                VortexComponentKind.Array,
                "the array carries a SHREDDED child; this build reads the core storage only, and " +
                "merging shredded paths back into the variant is not implemented.");
        }

        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);

        // The core storage wears the same dtype as the node, so the child decodes at the variant
        // dtype and whatever encoding it uses has to produce the `Struct{metadata, value}` form.
        // An encoding that does not is refused below rather than reinterpreted.
        int core = selective
            ? context.DecodeChildSelected(in node, 0, dtype, length, wanted)
            : context.DecodeChild(in node, 0, dtype, length);
        CanonicalNode storage = context.Canonical.GetNode(core);
        VariantStorage.Require(storage, selective ? wanted.Length : length, Id);
        return core;
    }
}

/// <summary>
/// Decodes <c>vortex.parquet.variant</c>: the Arrow-compatible binary spelling, a pair of binary
/// children holding each row's variant metadata and value. A file carrying shredded paths, or one
/// whose rows are all shredded, is refused rather than read partially.
/// </summary>
public sealed class ParquetVariantDecoder : ArrayDecoder
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

        if (metadata.HasTypedValue)
        {
            throw new VortexUnsupportedException(
                Id,
                VortexComponentKind.Array,
                "the array carries a SHREDDED typed_value child; this build reads the unshredded " +
                "metadata and value only.");
        }

        if (!metadata.HasValue)
        {
            throw new VortexUnsupportedException(
                Id,
                VortexComponentKind.Array,
                "the array has no unshredded `value` child, so every row is shredded; this build " +
                "reads the unshredded form only.");
        }

        // One child more than expected is allowed: the extra one is an explicit validity child,
        // and it comes first.
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);
        int first = node.ChildCount == 3 ? 1 : 0;

        DTypeArena types = dtype.Arena;
        DType metadataType = types.Binary(Nullability.NonNullable);
        DType valueType = types.Binary(
            metadata.ValueNullable ? Nullability.Nullable : Nullability.NonNullable);

        Validity validity = first == 1
            ? context.DecodeValidity(in node, 0, dtype.Nullability, length)
            : Validity.FromNullability(dtype.Nullability);

        int produced = selective ? wanted.Length : length;
        if (selective && first == 1)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        int metadataChild = selective
            ? context.DecodeChildSelected(in node, first, metadataType, length, wanted)
            : context.DecodeChild(in node, first, metadataType, length);
        int valueChild = selective
            ? context.DecodeChildSelected(in node, first + 1, valueType, length, wanted)
            : context.DecodeChild(in node, first + 1, valueType, length);

        RequireBinary(context, metadataChild, produced, "metadata");
        RequireBinary(context, valueChild, produced, "value");

        Span<int> fields = stackalloc int[2];
        fields[0] = metadataChild;
        fields[1] = valueChild;

        // A struct wearing the variant dtype: the canonical shape is `Struct{metadata, value}`
        // while the schema still says variant.
        return context.Canonical.AddStruct(dtype, produced, validity, fields);
    }

    private static void RequireBinary(ArrayDecodeContext context, int child, int length, string what)
    {
        CanonicalNode node = context.Canonical.GetNode(child);
        if (node.Kind != CanonicalKind.VarBinView)
        {
            CompressedThrow.ChildKind(Id, what, node.Kind, "a VarBinView");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(Id, what, node.Length, length);
        }
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
    /// <summary>The struct field holding each row's variant metadata bytes.</summary>
    internal const int MetadataField = 0;

    /// <summary>The struct field holding each row's variant value bytes.</summary>
    internal const int ValueField = 1;

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
