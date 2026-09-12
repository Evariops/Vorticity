// vortex.fixed_size_list - vortex-array-0.86.1/src/arrays/fixed_size_list/vtable/mod.rs
// `deserialize`. Phase 1 contract §9.1: child 0 is the elements, of length `n * list_size`, and an
// optional validity child at index 1.
//
// Two traps. `list_size` is a u32 in the dtype, so `n * list_size` is an unchecked multiply
// upstream and goes through CheckedMultiply here. And `list_size == 0` is legal: upstream
// special-cases it because `elements.len() / 0` is undefined, so nothing here divides by it.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.fixed_size_list</c>: positional groups over an elements child.</summary>
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
