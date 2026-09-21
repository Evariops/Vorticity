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
