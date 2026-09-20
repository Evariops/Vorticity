using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.fixed_size_list</c>: positional groups over an elements child of
/// <c>length * list_size</c> values, with an optional validity child after it. The list size is a
/// <c>u32</c>, so the product is checked for overflow; a list size of zero is legal, so nothing
/// here divides by it.
/// </summary>
public sealed class FixedSizeListDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.fixed_size_list";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly FixedSizeListDecoder Instance = new FixedSizeListDecoder();

    private FixedSizeListDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.fixed_size_list"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FixedSizeList;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.FixedSizeList, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, 2, Id);

        uint size = dtype.FixedSize;
        int elementsLength = ElementCount(length, size);

        int elementsIndex = context.DecodeChild(in node, 0, dtype.ElementType, elementsLength);
        Validity validity = context.DecodeValidity(in node, 1, dtype.Nullability, length);

        return context.Canonical.AddFixedSizeList(dtype, length, validity, elementsIndex, size);
    }

    /// <summary><c>length * size</c>, refusing to overflow. Shared with the constant builder.</summary>
    internal static int ElementCount(int length, uint size)
    {
        if (size > int.MaxValue)
        {
            throw new VortexFormatException(
                $"{Id} declares {size} elements per row, which cannot be addressed.");
        }

        return ArrayDecodeContext.CheckedMultiply(length, (int)size, Id + " elements");
    }
}
