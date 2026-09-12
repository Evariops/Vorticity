// vortex.list - vortex-array-0.86.1/src/arrays/list/vtable/mod.rs `deserialize`, then
// `list_view_from_list` (the encoding's own `execute`, which is how upstream canonicalizes it).
//
// Like VarBin, List has no canonical form of its own: `Canonical::List` IS a ListViewArray. Phase 1
// contract §9.2 requires the conversion in the decoder. `offsets` has n + 1 entries; a ListView
// wants n offsets and n sizes, so `offsets[0..n]` is reused with no copy and only the sizes are
// materialized.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.list</c> into the canonical <c>ListView</c> form.</summary>
public sealed class ListDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.list";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ListDecoder Instance = new ListDecoder();

    private ListDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.list"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.List;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ListMetadata metadata = ListMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.List, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);

        PType offsetPType = metadata.OffsetPType;

        // Arrow-style list offsets are legitimately SIGNED - encodings/list.vortex carries i32 -
        // so only "integer" is required here. Unsignedness is a patch-index rule, not this one.
        CanonicalSupport.RequireIntegerPType(offsetPType, Id + " offset_ptype");

        int elementsLength = ArrayDecodeContext.CheckedLength(metadata.ElementsLength, Id + " elements_len");
        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " offset count");

        int elementsIndex = context.DecodeChild(in node, 0, dtype.ElementType, elementsLength);
        int offsetsIndex = context.DecodeChild(
            in node, 1, context.Types.Primitive(offsetPType, Nullability.NonNullable), offsetCount);
        Validity validity = context.DecodeValidity(in node, 2, dtype.Nullability, length);

        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetPType, offsetCount, Id + " offsets");

        int width = offsetPType.ByteWidth();
        ReadOnlySpan<byte> offsetBytes = offsets.Values.Span;

        int sizeBytes = ArrayDecodeContext.CheckedMultiply(length, width, Id + " sizes");
        VortexBuffer sizes = CanonicalSupport.Allocate(
            context, sizeBytes, CanonicalSupport.MaxRequiredAlignment, out Span<byte> writableSizes);

        long previous = CanonicalSupport.ReadInteger(offsetBytes, offsetPType, 0);
        if (previous < 0)
        {
            throw new VortexFormatException($"{Id} offsets must not be negative; offset 0 is {previous}.");
        }

        for (int i = 0; i < length; i++)
        {
            long next = CanonicalSupport.ReadInteger(offsetBytes, offsetPType, i + 1);
            if (next < previous)
            {
                throw new VortexFormatException(
                    $"{Id} offsets must not decrease; offset {i + 1} is {next} after {previous}.");
            }

            CanonicalSupport.WriteInteger(writableSizes, offsetPType, i, next - previous);
            previous = next;
        }

        if (previous > elementsLength)
        {
            throw new VortexFormatException(
                $"{Id} offsets end at {previous}, past the {elementsLength}-element child.");
        }

        // The first n offsets are already exactly what a ListView wants; slicing keeps it zero-copy.
        VortexBuffer viewOffsets = offsets.Values.Slice(0, sizeBytes);

        return context.Canonical.AddListView(
            dtype, length, validity, elementsIndex, viewOffsets, offsetPType, sizes, offsetPType);
    }
}
