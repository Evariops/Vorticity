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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// <remarks>
    /// <para>
    /// PERF-AUDIT-v2.md R7. This is the ONE loop of a listview scan: a canonical ListView borrows
    /// its offsets and sizes buffers as they are, so once the children are decoded there is nothing
    /// else per row to do. Which means the type switch inside <c>ReadInteger</c> — twice per row,
    /// on a property of the CALL and not of the row — was the scan. Short-circuited on
    /// `listview.vortex` at a million rows, the free version reads **164 µs against 2 552**: the
    /// check was **93,6 %** of it.
    /// </para>
    /// <para>
    /// THE TWO TYPES ARE RESOLVED BEFORE THE LOOP, the way <c>RowKernels.Gather</c> resolves its
    /// codes: one switch picks the offsets' type, a second picks the sizes' type, and
    /// the loop that runs is monomorphic in both. Only the pairs a file actually uses are ever
    /// instantiated, and in practice that is one.
    /// </para>
    /// <para>
    /// THE SEMANTICS ARE THE OLD ONES, TO THE MESSAGE. <see cref="Widen{T}"/> reproduces
    /// <c>ReadInteger</c>'s saturation exactly — a <c>u64</c> above <c>long.MaxValue</c> becomes
    /// <c>long.MaxValue</c>, which stays non-negative and fails the bound on the second check
    /// rather than the first — so a malformed file gets the same exception with the same numbers as
    /// before. The integer-ness of both ptypes is already established by
    /// <c>RequireIntegerPType</c> above, which is why the last arm widens rather than throwing.
    /// </para>
    /// </remarks>
    internal static void ValidateRanges(
        ReadOnlySpan<byte> offsets,
        PType offsetPType,
        ReadOnlySpan<byte> sizes,
        PType sizePType,
        int length,
        int elementsLength)
    {
        switch (offsetPType)
        {
            case PType.U8: ValidateSizes<byte>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.U16: ValidateSizes<ushort>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.U32: ValidateSizes<uint>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.U64: ValidateSizes<ulong>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.I8: ValidateSizes<sbyte>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.I16: ValidateSizes<short>(offsets, sizes, sizePType, length, elementsLength); break;
            case PType.I32: ValidateSizes<int>(offsets, sizes, sizePType, length, elementsLength); break;
            default: ValidateSizes<long>(offsets, sizes, sizePType, length, elementsLength); break;
        }
    }

    /// <summary>The second half of the dispatch: the sizes' physical type.</summary>
    private static void ValidateSizes<TOffset>(
        ReadOnlySpan<byte> offsets,
        ReadOnlySpan<byte> sizes,
        PType sizePType,
        int length,
        int elementsLength)
        where TOffset : unmanaged
    {
        switch (sizePType)
        {
            case PType.U8: ValidateCore<TOffset, byte>(offsets, sizes, length, elementsLength); break;
            case PType.U16: ValidateCore<TOffset, ushort>(offsets, sizes, length, elementsLength); break;
            case PType.U32: ValidateCore<TOffset, uint>(offsets, sizes, length, elementsLength); break;
            case PType.U64: ValidateCore<TOffset, ulong>(offsets, sizes, length, elementsLength); break;
            case PType.I8: ValidateCore<TOffset, sbyte>(offsets, sizes, length, elementsLength); break;
            case PType.I16: ValidateCore<TOffset, short>(offsets, sizes, length, elementsLength); break;
            case PType.I32: ValidateCore<TOffset, int>(offsets, sizes, length, elementsLength); break;
            default: ValidateCore<TOffset, long>(offsets, sizes, length, elementsLength); break;
        }
    }

    /// <summary>The loop, with both widths resolved and both spans cast once.</summary>
    private static void ValidateCore<TOffset, TSize>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> sizes, int length, int elementsLength)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        ReadOnlySpan<TOffset> typedOffsets = MemoryMarshal.Cast<byte, TOffset>(offsets)[..length];
        ReadOnlySpan<TSize> typedSizes = MemoryMarshal.Cast<byte, TSize>(sizes)[..length];
        ulong limit = (ulong)elementsLength;

        for (int i = 0; i < typedOffsets.Length; i++)
        {
            long offset = Widen(typedOffsets[i]);
            long size = Widen(typedSizes[i]);

            if (offset < 0 || size < 0)
            {
                throw new VortexFormatException(
                    $"List row {i} has a negative offset {offset} or size {size}.");
            }

            // Both are now in [0, long.MaxValue], so the ulong sum is exact and elementsLength is a
            // non-negative int. The message reports `end`, not `offset + size`: the latter is the
            // wrapped value this check exists to catch.
            ulong end = (ulong)offset + (ulong)size;
            if (end > limit)
            {
                throw new VortexFormatException(
                    $"List row {i} spans [{offset}, {end}) of an elements child holding " +
                    $"{elementsLength} values.");
            }
        }
    }

    /// <summary>
    /// One value as a <c>long</c>, saturating exactly as <c>CanonicalSupport.ReadInteger</c> does.
    /// </summary>
    /// <remarks>
    /// Written as <c>typeof(T) == typeof(X)</c> rather than generic math because that is the form
    /// the JIT folds away per instantiation — <c>RowKernels.WidenCode</c> is the same shape, for
    /// the same reason.
    /// </remarks>
    private static long Widen<T>(T value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref value);
        }

        if (typeof(T) == typeof(ushort))
        {
            return Unsafe.As<T, ushort>(ref value);
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref value);
        }

        if (typeof(T) == typeof(ulong))
        {
            ulong wide = Unsafe.As<T, ulong>(ref value);
            return wide > long.MaxValue ? long.MaxValue : (long)wide;
        }

        if (typeof(T) == typeof(sbyte))
        {
            return Unsafe.As<T, sbyte>(ref value);
        }

        if (typeof(T) == typeof(short))
        {
            return Unsafe.As<T, short>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            return Unsafe.As<T, int>(ref value);
        }

        return Unsafe.As<T, long>(ref value);
    }
}
