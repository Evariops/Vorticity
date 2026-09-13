// vortex.variant - vortex-array-0.86.1/src/arrays/variant/vtable/mod.rs, and
// vortex.parquet.variant - vortex-parquet-variant-0.86.1/src/vtable.rs.
//
// THE LAST TWO ENCODINGS OF THE CORPUS, and they are one decoder because they are one shape. A
// variant column is a self-describing value per row; upstream keeps two spellings of it -- a
// logical `vortex.variant` over any Variant-typed storage, and an `arrow.parquet.variant`-
// compatible pair of binary children -- and neither has a canonical form of its own: `execute`
// returns the array unchanged for both.
//
// WHAT THIS BUILD DOES WITH THEM, and why it is not a new canonical kind. There is no
// `CanonicalKind.Variant` and adding one would mean a new column type, a new arena builder, a new
// writer path and a new public accessor for a shape `AsStruct()` already expresses. So a variant
// node is a STRUCT of `{metadata, value}` WEARING THE VARIANT DTYPE -- exactly the trick
// `vortex.map` uses to be a ListView wearing a Map dtype, and for the same reason:
// `CanonicalArena.AddStruct` does not constrain the dtype's kind, so the representation is
// available without a lie.
//
// The two fields are the Parquet Variant binary encoding, which is what
// `vortex.parquet.variant` already stores and what `VariantDecoder` therefore has to PRODUCE for
// the logical spelling: a `vortex.variant` over a `vortex.constant` carries a typed SCALAR, and
// `ConstantCanonicalizer` encodes it (see `Types/Variant/ParquetVariant.cs`). Both paths end at the
// same two binary columns, so the conformance comparer has one thing to decode and the writer has
// one thing to serialize.
//
// SHREDDING IS REFUSED BY NAME. Both encodings can carry a second, typed child holding selected
// paths pulled out of the variant, and merging it back is the bulk of what `parquet-variant` does.
// No corpus file has one, so implementing it would be untested guessing; a file that has one gets a
// `VortexUnsupportedException` saying which child it was.
using System;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.variant</c>: a logical variant column over Variant-typed storage.</summary>
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

        // The core storage wears the SAME dtype as the node -- upstream asserts it -- so the child
        // decodes at the variant dtype and whatever encoding it uses has to produce the
        // `Struct{metadata, value}` form. `vortex.constant` does; anything else that does not will
        // say so below rather than be reinterpreted.
        int core = context.DecodeChild(in node, 0, dtype, length);
        CanonicalNode storage = context.Canonical.GetNode(core);
        VariantStorage.Require(storage, length, Id);
        return core;
    }
}

/// <summary>Decodes <c>vortex.parquet.variant</c>: the Arrow-compatible binary spelling.</summary>
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

        // "children.len() == expected || expected + 1": the extra one, when present, is an explicit
        // validity child and it comes FIRST. Upstream's rule, transcribed.
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);
        int first = node.ChildCount == 3 ? 1 : 0;

        DTypeArena types = dtype.Arena;
        DType metadataType = types.Binary(Nullability.NonNullable);
        DType valueType = types.Binary(
            metadata.ValueNullable ? Nullability.Nullable : Nullability.NonNullable);

        Validity validity = first == 1
            ? context.DecodeValidity(in node, 0, dtype.Nullability, length)
            : Validity.FromNullability(dtype.Nullability);

        int metadataChild = context.DecodeChild(in node, first, metadataType, length);
        int valueChild = context.DecodeChild(in node, first + 1, valueType, length);

        RequireBinary(context, metadataChild, length, "metadata");
        RequireBinary(context, valueChild, length, "value");

        Span<int> fields = stackalloc int[2];
        fields[0] = metadataChild;
        fields[1] = valueChild;

        // A STRUCT WEARING THE VARIANT DTYPE. See the file header: the shape is
        // `Struct{metadata, value}` and the schema still says variant.
        return context.Canonical.AddStruct(dtype, length, validity, fields);
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

/// <summary>The shape both variant spellings canonicalize to.</summary>
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
