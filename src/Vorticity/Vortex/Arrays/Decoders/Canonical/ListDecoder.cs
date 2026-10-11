using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.list</c> into the canonical <c>ListView</c> form: a list has no canonical
/// form of its own, so the conversion happens in the decoder. The serialized offsets hold
/// <c>n + 1</c> entries while a list view wants <c>n</c> offsets and <c>n</c> sizes, so the first
/// <c>n</c> offsets are reused as they are and only the sizes are materialized.
/// </summary>
internal sealed class ListDecoder : ArrayDecoder
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

        // Arrow-style list offsets are legitimately signed, so only integerness is required here.
        // Refusing a signed offset type is a patch-index rule, not this one.
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
    /// <summary>Rows a selection's offsets pair on the stack before it rents.</summary>
    private const int StackRows = 128;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// Where the offsets and the validity select and the elements decode to views of their own
    /// buffers: the elements are kept whole, every batch borrowing the same views, so a selection
    /// costs its rows and not the column's.
    /// </summary>
    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (dtype.IsDefault || dtype.Kind != DTypeKind.List || node.ChildCount is < 2 or > 3)
        {
            return false;
        }

        DType offsets = context.Types.Primitive(ListMetadata.Read(node.Metadata).OffsetPType, Nullability.NonNullable);
        return context.ChildMaterializesNothing(in node, 0, dtype.ElementType)
            && context.ChildSelectsWithoutFullDecode(in node, 1, offsets)
            && (node.ChildCount == 2 || context.ChildSelectsWithoutFullDecode(in node, 2, context.Types.Bool(Nullability.NonNullable)));
    }

    /// <summary>
    /// The wanted rows' offsets and each one's next, in one selection of the offsets, the wanted
    /// rows' validity, and the elements whole, which the views' offsets point into: no size of a
    /// row not wanted, and no offset of one read.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        ListMetadata metadata = ListMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.List, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);
        PType offsetPType = metadata.OffsetPType;
        CanonicalSupport.RequireIntegerPType(offsetPType, Id + " offset_ptype");
        int elementsLength = ArrayDecodeContext.CheckedLength(metadata.ElementsLength, Id + " elements_len");
        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " offset count");

        int elementsIndex = context.DecodeChild(in node, 0, dtype.ElementType, elementsLength);
        Validity validity = context.DecodeValiditySelected(in node, 2, dtype.Nullability, length, wanted);
        int count = wanted.Length;
        int width = offsetPType.ByteWidth();
        int bytes = ArrayDecodeContext.CheckedMultiply(count, width, Id + " sizes");
        VortexBuffer viewOffsets = CanonicalSupport.AllocateUninitialized(
            context, Math.Max(bytes, width), CanonicalSupport.MaxRequiredAlignment, out Span<byte> writableOffsets);
        VortexBuffer sizes = CanonicalSupport.AllocateUninitialized(
            context, Math.Max(bytes, width), CanonicalSupport.MaxRequiredAlignment, out Span<byte> writableSizes);

        // A row's offsets are its own and the next row's: the wanted rows and the one after each,
        // ascending, a row after a wanted one being its next's start when it is wanted too.
        Span<int> pairStack = stackalloc int[2 * StackRows];
        Span<int> startStack = stackalloc int[StackRows];
        Scratch<int> pairScratch = new Scratch<int>(2 * count, pairStack);
        Scratch<int> startScratch = new Scratch<int>(count, startStack);
        try
        {
            Span<int> pairs = pairScratch.Span;
            Span<int> starts = startScratch.Span;
            int paired = 0;
            for (int i = 0; i < count; i++)
            {
                int row = wanted[i];
                if (paired == 0 || pairs[paired - 1] != row)
                {
                    pairs[paired++] = row;
                }

                starts[i] = paired - 1;
                pairs[paired++] = row + 1;
            }

            int offsetsIndex = context.DecodeChildSelected(
                in node, 1, context.Types.Primitive(offsetPType, Nullability.NonNullable), offsetCount, pairs[..paired]);
            CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
                context, offsetsIndex, offsetPType, paired, Id + " offsets");
            ReadOnlySpan<byte> values = offsets.Values.Span;
            switch (offsetPType)
            {
                case PType.U8:
                    Spans<byte>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.U16:
                    Spans<ushort>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.U32:
                    Spans<uint>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.U64:
                    Spans<ulong>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.I8:
                    Spans<sbyte>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.I16:
                    Spans<short>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                case PType.I32:
                    Spans<int>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
                default:
                    Spans<long>(values, starts, wanted, elementsLength, writableOffsets, writableSizes);
                    break;
            }
        }
        finally
        {
            startScratch.Dispose();
            pairScratch.Dispose();
        }

        return context.Canonical.AddListView(
            dtype, count, validity, elementsIndex, viewOffsets.Slice(0, bytes), offsetPType, sizes.Slice(0, bytes), offsetPType);
    }

    /// <summary>
    /// Writes each wanted row's offset and size from the pair of offsets <paramref name="starts"/>
    /// names in <paramref name="values"/>, refusing a pair that does not lie in the elements.
    /// </summary>
    private static void Spans<T>(
        ReadOnlySpan<byte> values, ReadOnlySpan<int> starts, ReadOnlySpan<int> wanted, int elementsLength,
        Span<byte> offsets, Span<byte> sizes)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> pairs = MemoryMarshal.Cast<byte, T>(values);
        Span<T> into = MemoryMarshal.Cast<byte, T>(offsets);
        Span<T> lengths = MemoryMarshal.Cast<byte, T>(sizes);
        T limit = T.CreateSaturating(elementsLength);
        for (int i = 0; i < starts.Length; i++)
        {
            T start = pairs[starts[i]];
            T end = pairs[starts[i] + 1];
            if (start < T.Zero || end < start || end > limit)
            {
                throw new VortexFormatException(
                    $"{Id} row {wanted[i]} spans [{start}, {end}) of a {elementsLength}-element child.");
            }

            into[i] = start;
            lengths[i] = end - start;
        }
    }

    /// <summary>
    /// Writes <c>sizes[i] = offsets[i + 1] - offsets[i]</c>, refusing a decreasing pair.
    /// </summary>
    /// <param name="offsets">n + 1 offsets, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">Their physical type.</param>
    /// <param name="sizes">n sizes of room, in the same physical type.</param>
    /// <param name="count">n.</param>
    /// <remarks>
    /// Subtracting each offset from the next is the only per-row work a list decode does, so the
    /// physical type is resolved once, before the loop, instead of per row: what remains is a
    /// shifted subtract over vectors, with the monotonicity check as one <c>LessThanAny</c> per
    /// block. A block that fails it falls to the scalar loop, which raises with the offending index.
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
        if (Vector.IsHardwareAccelerated)
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
