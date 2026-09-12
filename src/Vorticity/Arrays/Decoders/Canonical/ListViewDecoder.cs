// vortex.listview - vortex-array-0.86.1/src/arrays/listview/vtable/mod.rs `deserialize` and
// `ListViewData::validate` / `validate_offsets_and_sizes` in .../listview/array.rs.
//
// The whole difference from `vortex.list` is that these offsets are NOT ordered, so nothing can be
// inferred from monotonicity: EVERY row is checked for `offset >= 0`, `size >= 0` and
// `offset + size <= elements_len`. Upstream checks all rows including the null ones and so does
// this.
//
// THE ADD MUST NOT BE A SIGNED ADD. Both operands are already i64 by the time they are read, so
// `offset + size` is an unchecked long add that WRAPS NEGATIVE for two individually legal values
// near 2^62 - and a wrapped sum compares below `elements_len`, turning the guard into a pass.
// Upstream widens to u64 and uses `checked_add` (`validate_offsets_and_sizes`); after the two sign
// checks both operands are in [0, long.MaxValue], so a ulong add is exact and is the same test.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.listview</c>: unordered (offset, size) pairs over an elements child.</summary>
public sealed class ListViewDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.listview";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ListViewDecoder Instance = new ListViewDecoder();

    private ListViewDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.listview"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ListView;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ListViewMetadata metadata = ListViewMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.List, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 3, 4, Id);

        PType offsetPType = metadata.OffsetPType;
        PType sizePType = metadata.SizePType;
        CanonicalSupport.RequireIntegerPType(offsetPType, Id + " offset_ptype");
        CanonicalSupport.RequireIntegerPType(sizePType, Id + " size_ptype");

        int elementsLength = ArrayDecodeContext.CheckedLength(metadata.ElementsLength, Id + " elements_len");

        int elementsIndex = context.DecodeChild(in node, 0, dtype.ElementType, elementsLength);
        int offsetsIndex = context.DecodeChild(
            in node, 1, context.Types.Primitive(offsetPType, Nullability.NonNullable), length);
        int sizesIndex = context.DecodeChild(
            in node, 2, context.Types.Primitive(sizePType, Nullability.NonNullable), length);
        Validity validity = context.DecodeValidity(in node, 3, dtype.Nullability, length);

        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetPType, length, Id + " offsets");
        CanonicalNode sizes = CanonicalSupport.RequirePrimitiveChild(
            context, sizesIndex, sizePType, length, Id + " sizes");

        ValidateRanges(
            offsets.Values.Span, offsetPType, sizes.Values.Span, sizePType, length, elementsLength);

        return context.Canonical.AddListView(
            dtype, length, validity, elementsIndex,
            offsets.Values, offsetPType, sizes.Values, sizePType);
    }

    /// <summary>
    /// Class I: <c>offset >= 0</c>, <c>size >= 0</c> and <c>offset + size &lt;= elementsLength</c>
    /// for every row, with the addition done UNSIGNED so it cannot wrap. A signed add of two
    /// legal i64 offsets near 2^62 wraps to a negative sum that passes the bound.
    /// </summary>
    internal static void ValidateRanges(
        ReadOnlySpan<byte> offsets,
        PType offsetPType,
        ReadOnlySpan<byte> sizes,
        PType sizePType,
        int length,
        int elementsLength)
    {
        for (int i = 0; i < length; i++)
        {
            long offset = CanonicalSupport.ReadInteger(offsets, offsetPType, i);
            long size = CanonicalSupport.ReadInteger(sizes, sizePType, i);

            if (offset < 0 || size < 0)
            {
                throw new VortexFormatException(
                    $"List row {i} has a negative offset {offset} or size {size}.");
            }

            // Both are now in [0, long.MaxValue], so the ulong sum is exact and elementsLength is a
            // non-negative int. The message reports `end`, not `offset + size`: the latter is the
            // wrapped value this check exists to catch.
            ulong end = (ulong)offset + (ulong)size;
            if (end > (ulong)elementsLength)
            {
                throw new VortexFormatException(
                    $"List row {i} spans [{offset}, {end}) of an elements child holding " +
                    $"{elementsLength} values.");
            }
        }
    }
}
