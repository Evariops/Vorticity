// vortex.list - vortex-array-0.86.1/src/arrays/list/vtable/mod.rs `deserialize`, then
// `list_view_from_list` (the encoding's own `execute`, which is how upstream canonicalizes it).
//
// Like VarBin, List has no canonical form of its own: `Canonical::List` IS a ListViewArray. Phase 1
// contract §9.2 requires the conversion in the decoder. `offsets` has n + 1 entries; a ListView
// wants n offsets and n sizes, so `offsets[0..n]` is reused with no copy and only the sizes are
// materialized.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
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

        // Uninitialized: the difference loop below writes every size.
        VortexBuffer sizes = CanonicalSupport.AllocateUninitialized(
            context, sizeBytes, CanonicalSupport.MaxRequiredAlignment, out Span<byte> writableSizes);

        long first = CanonicalSupport.ReadInteger(offsetBytes, offsetPType, 0);
        if (first < 0)
        {
            throw new VortexFormatException($"{Id} offsets must not be negative; offset 0 is {first}.");
        }

        Differences(offsetBytes, offsetPType, writableSizes, length);

        long last = CanonicalSupport.ReadInteger(offsetBytes, offsetPType, length);
        if (last > elementsLength)
        {
            throw new VortexFormatException(
                $"{Id} offsets end at {last}, past the {elementsLength}-element child.");
        }

        // The first n offsets are already exactly what a ListView wants; slicing keeps it zero-copy.
        VortexBuffer viewOffsets = offsets.Values.Slice(0, sizeBytes);

        return context.Canonical.AddListView(
            dtype, length, validity, elementsIndex, viewOffsets, offsetPType, sizes, offsetPType);
    }
    /// <summary>
    /// Writes <c>sizes[i] = offsets[i + 1] - offsets[i]</c>, refusing a decreasing pair.
    /// </summary>
    /// <param name="offsets">n + 1 offsets, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">Their physical type.</param>
    /// <param name="sizes">n sizes of room, in the same physical type.</param>
    /// <param name="count">n.</param>
    /// <remarks>
    /// A million rows of this loop were 20% of a 1M-row `vortex.list` scan, doing nothing but
    /// subtracting one integer from the next -- through <c>ReadInteger</c>'s switch on the physical
    /// type twice and <c>WriteInteger</c>'s once, per row. It is a shifted subtract, which is what
    /// a vector unit is for: the monotonicity check becomes one <c>LessThanAny</c> per block, and
    /// a block that fails it falls to the scalar loop, which raises with the offending index.
    /// </remarks>
    private static void Differences(
        ReadOnlySpan<byte> offsets, PType ptype, Span<byte> sizes, int count)
    {
        switch (ptype)
        {
            case PType.U8:
                Differences<byte>(offsets, sizes, count);
                break;
            case PType.U16:
                Differences<ushort>(offsets, sizes, count);
                break;
            case PType.U32:
                Differences<uint>(offsets, sizes, count);
                break;
            case PType.U64:
                Differences<ulong>(offsets, sizes, count);
                break;
            case PType.I8:
                Differences<sbyte>(offsets, sizes, count);
                break;
            case PType.I16:
                Differences<short>(offsets, sizes, count);
                break;
            case PType.I32:
                Differences<int>(offsets, sizes, count);
                break;
            default:
                Differences<long>(offsets, sizes, count);
                break;
        }
    }

    private static void Differences<T>(ReadOnlySpan<byte> offsets, Span<byte> sizes, int count)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> source = MemoryMarshal.Cast<byte, T>(offsets)[..(count + 1)];
        Span<T> destination = MemoryMarshal.Cast<byte, T>(sizes)[..count];

        int i = 0;
        if (Vector<T>.IsSupported)
        {
            int lanes = Vector<T>.Count;
            for (; i <= count - lanes; i += lanes)
            {
                Vector<T> low = Vector.LoadUnsafe(in source[i]);
                Vector<T> high = Vector.LoadUnsafe(in source[i + 1]);
                if (Vector.LessThanAny(high, low))
                {
                    // One pair in this block decreases; the scalar loop below finds which.
                    break;
                }

                (high - low).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < count; i++)
        {
            T low = source[i];
            T high = source[i + 1];
            if (high < low)
            {
                throw new VortexFormatException(
                    $"{Id} offsets must not decrease; offset {i + 1} is {high} after {low}.");
            }

            destination[i] = high - low;
        }
    }
}
