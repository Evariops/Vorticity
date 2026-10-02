using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.masked</c>: the values as a first child with their nullability stripped, plus
/// an optional mask child. The dtype asked for must itself be nullable, since without a mask child
/// the validity comes from that nullability and a non-nullable dtype would make the encoding a
/// no-op.
/// </summary>
internal sealed class MaskedDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.masked";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly MaskedDecoder Instance = new MaskedDecoder();

    private MaskedDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.masked"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Masked;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, 2, Id);

        if (dtype.IsDefault || dtype.Nullability != Nullability.Nullable)
        {
            throw new VortexFormatException(
                $"{Id} produces a nullable array; it was asked for a non-nullable dtype.");
        }

        // Import first, then flip. The dtype handed in is a handle into the file's arena, shared by
        // every concurrent scan of that open file, and flipping the nullability of a non-leaf node
        // mutates the arena it belongs to without synchronization. The import copies the node into
        // this flow's own arena, of which each parallel split has one, before it is rewritten.
        DType childDType = DTypeImport.Into(context.Types, dtype).WithNullability(Nullability.NonNullable);
        int childIndex = context.DecodeChild(in node, 0, childDType, length);
        Validity validity = context.DecodeValidity(in node, 1, dtype.Nullability, length);

        if (context.Options.VerifyStatistics)
        {
            VerifyChildHasNoNulls(context, childIndex);
        }

        return CanonicalRewrap.WithValidity(context.Canonical, childIndex, dtype, validity, length);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>The values and the mask both select, when there is a mask.</summary>
    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (dtype.IsDefault || context.Options.VerifyStatistics)
        {
            return false;
        }

        // The values' own dtype is the column's without its nulls, which the question does not
        // turn on: asked of the column's, it imports nothing.
        return context.ChildSelectsWithoutFullDecode(in node, 0, dtype) &&
            (node.ChildCount < 2 ||
                context.ChildSelectsWithoutFullDecode(in node, 1, context.Types.Bool(Nullability.NonNullable)));
    }

    /// <summary>
    /// The wanted rows of the values and of the mask, each selected by its own encoding: the mask's
    /// wanted bits are all that is classified, where a whole decode classifies every bit of it.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Checking the values for nulls means reading every row of them, so a read that asks for it
        // decodes the node whole, as the fallback does.
        if (context.Options.VerifyStatistics)
        {
            return base.DecodeSelected(context, in node, dtype, length, wanted);
        }

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, 2, Id);

        if (dtype.IsDefault || dtype.Nullability != Nullability.Nullable)
        {
            throw new VortexFormatException(
                $"{Id} produces a nullable array; it was asked for a non-nullable dtype.");
        }

        DType childDType = DTypeImport.Into(context.Types, dtype).WithNullability(Nullability.NonNullable);

        // The mask first: when it nulls every wanted row, no value is read, the values a null row
        // holds being nobody's, and a take of scattered rows reads each from a page of its own.
        Validity validity = context.DecodeValiditySelected(in node, 1, dtype.Nullability, length, wanted);
        int childIndex = validity.Kind == ValidityKind.AllInvalid && CanonicalFill.CanBuild(childDType)
            ? CanonicalFill.BuildZeroed(context, childDType, wanted.Length, Validity.FromNullability(Nullability.NonNullable))
            : context.DecodeChildSelected(in node, 0, childDType, length, wanted);
        return CanonicalRewrap.WithValidity(context.Canonical, childIndex, dtype, validity, wanted.Length);
    }

    /// <summary>
    /// Asserts that the child holds no nulls. This is a linear scan of information the mask already
    /// supersedes, so it runs only when the caller has asked for statistics to be verified.
    /// </summary>
    private static void VerifyChildHasNoNulls(ArrayDecodeContext context, int childIndex)
    {
        CanonicalNode child = context.Canonical.GetNode(childIndex);
        if (child.Validity.IsAllValid)
        {
            return;
        }

        throw new VortexFormatException(
            $"{Id}'s child must contain no nulls; it reports {child.Validity.Kind}.");
    }
}
