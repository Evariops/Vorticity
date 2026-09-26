using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.listview</c>: unordered (offset, size) pairs over an elements child. Unlike a
/// list's offsets these are not ordered, so nothing can be inferred from monotonicity and every
/// row is range-checked, the null ones included.
/// </summary>
internal sealed class ListViewDecoder : ArrayDecoder
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
    /// Checks <c>offset >= 0</c>, <c>size >= 0</c> and <c>offset + size &lt;= elementsLength</c>
    /// for every row, with the addition done unsigned so it cannot wrap. A signed add of two
    /// individually legal offsets near the top of the range wraps to a negative sum, and a negative
    /// sum compares below the bound, turning the guard into a pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A canonical list view borrows its offsets and sizes buffers as they are, so this is the only
    /// per-row work a listview decode does. Both physical types are therefore resolved before the
    /// loop — one switch for the offsets, a second for the sizes — so the loop that runs is
    /// monomorphic in both rather than switching on a type per value read.
    /// </para>
    /// <para>
    /// <see cref="Widen{T}"/> reproduces the shared integer read's saturation exactly: a
    /// <c>u64</c> above <c>long.MaxValue</c> becomes <c>long.MaxValue</c>, which stays non-negative
    /// and so fails the bound check rather than the sign check, and a malformed file gets the same
    /// exception with the same numbers either way. Both ptypes are known to be integers by the time
    /// this runs, which is why the last arm widens rather than throwing.
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
    /// <remarks>
    /// A pass with no branch on a row decides the whole node, and only a node it refuses is walked
    /// again row by row, for the row and the numbers the message names.
    /// </remarks>
    private static void ValidateCore<TOffset, TSize>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> sizes, int length, int elementsLength)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        ReadOnlySpan<TOffset> typedOffsets = MemoryMarshal.Cast<byte, TOffset>(offsets)[..length];
        ReadOnlySpan<TSize> typedSizes = MemoryMarshal.Cast<byte, TSize>(sizes)[..length];
        ulong limit = (ulong)elementsLength;
        if (AllInRange(typedOffsets, typedSizes, limit))
        {
            return;
        }

        ReportFirst(typedOffsets, typedSizes, elementsLength);
    }

    /// <summary>
    /// Whether every row's offset and size are non-negative and end within
    /// <paramref name="limit"/>, gathered by or with no branch on a row.
    /// </summary>
    /// <remarks>
    /// A negative value, or a <c>u64</c> past <c>long.MaxValue</c>, sets the top bit of the gathered
    /// word by itself; with both below 2^63 the sum is exact, and an end past the limit sets every
    /// bit. Unsigned 64-bit offsets and sizes, the width a writer that does not narrow them leaves,
    /// go two rows to a vector.
    /// </remarks>
    private static bool AllInRange<TOffset, TSize>(ReadOnlySpan<TOffset> offsets, ReadOnlySpan<TSize> sizes, ulong limit)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        ulong bad = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && Unsafe.SizeOf<TOffset>() == 8 && Unsafe.SizeOf<TSize>() == 8)
        {
            // Eight rows to a vector, signed or not read as unsigned: a negative value sets its top
            // bit, gathered by or, and with both below 2^63 the end is exact, so the largest end is
            // kept and tested once, a max where the narrower vectors compare and gather each end.
            ref ulong o = ref Unsafe.As<TOffset, ulong>(ref MemoryMarshal.GetReference(offsets));
            ref ulong s = ref Unsafe.As<TSize, ulong>(ref MemoryMarshal.GetReference(sizes));
            Vector512<ulong> gathered = Vector512<ulong>.Zero;
            Vector512<ulong> furthest = Vector512<ulong>.Zero;
            for (; i <= offsets.Length - 8; i += 8)
            {
                Vector512<ulong> o0 = Vector512.LoadUnsafe(ref o, (nuint)i);
                Vector512<ulong> s0 = Vector512.LoadUnsafe(ref s, (nuint)i);
                gathered |= o0 | s0;
                furthest = Vector512.Max(furthest, o0 + s0);
            }

            bad = Vector512.GreaterThanAny(gathered, Vector512.Create((ulong)long.MaxValue))
                || Vector512.GreaterThanAny(furthest, Vector512.Create(limit)) ? ulong.MaxValue : 0;
        }
        else if (Vector512.IsHardwareAccelerated && Unsafe.SizeOf<TOffset>() == 4 && Unsafe.SizeOf<TSize>() == 4
            && limit <= int.MaxValue)
        {
            // Sixteen rows to a vector, as the four below.
            ref uint o = ref Unsafe.As<TOffset, uint>(ref MemoryMarshal.GetReference(offsets));
            ref uint s = ref Unsafe.As<TSize, uint>(ref MemoryMarshal.GetReference(sizes));
            Vector512<uint> gathered = Vector512<uint>.Zero;
            Vector512<uint> furthest = Vector512<uint>.Zero;
            for (; i <= offsets.Length - 16; i += 16)
            {
                Vector512<uint> o0 = Vector512.LoadUnsafe(ref o, (nuint)i);
                Vector512<uint> s0 = Vector512.LoadUnsafe(ref s, (nuint)i);
                gathered = Vector512.Max(gathered, Vector512.Max(o0, s0));
                furthest = Vector512.Max(furthest, o0 + s0);
            }

            bad = Vector512.GreaterThanAny(gathered, Vector512.Create((uint)limit))
                || Vector512.GreaterThanAny(furthest, Vector512.Create((uint)limit)) ? ulong.MaxValue : 0;
        }
        else if (typeof(TOffset) == typeof(ulong) && typeof(TSize) == typeof(ulong))
        {
            ref ulong o = ref Unsafe.As<TOffset, ulong>(ref MemoryMarshal.GetReference(offsets));
            ref ulong s = ref Unsafe.As<TSize, ulong>(ref MemoryMarshal.GetReference(sizes));
            Vector128<ulong> limits = Vector128.Create(limit);
            Vector128<ulong> gathered = Vector128<ulong>.Zero;
            for (; i <= offsets.Length - 4; i += 4)
            {
                Vector128<ulong> o0 = Vector128.LoadUnsafe(ref o, (nuint)i);
                Vector128<ulong> s0 = Vector128.LoadUnsafe(ref s, (nuint)i);
                Vector128<ulong> o1 = Vector128.LoadUnsafe(ref o, (nuint)(i + 2));
                Vector128<ulong> s1 = Vector128.LoadUnsafe(ref s, (nuint)(i + 2));
                gathered |= (o0 | s0) | Vector128.GreaterThan(o0 + s0, limits) |
                    (o1 | s1) | Vector128.GreaterThan(o1 + s1, limits);
            }

            bad = gathered.GetElement(0) | gathered.GetElement(1);
        }
        else if (Unsafe.SizeOf<TOffset>() == 4 && Unsafe.SizeOf<TSize>() == 4 && limit <= int.MaxValue)
        {
            // Four rows to a vector, signed or not read as unsigned: a negative value is then past
            // any limit an int can hold, and two values within the limit cannot wrap their sum.
            ref uint o = ref Unsafe.As<TOffset, uint>(ref MemoryMarshal.GetReference(offsets));
            ref uint s = ref Unsafe.As<TSize, uint>(ref MemoryMarshal.GetReference(sizes));
            Vector128<uint> limits = Vector128.Create((uint)limit);
            Vector128<uint> gathered = Vector128<uint>.Zero;
            for (; i <= offsets.Length - 4; i += 4)
            {
                Vector128<uint> o0 = Vector128.LoadUnsafe(ref o, (nuint)i);
                Vector128<uint> s0 = Vector128.LoadUnsafe(ref s, (nuint)i);
                gathered |= Vector128.GreaterThan(o0, limits) | Vector128.GreaterThan(s0, limits) |
                    Vector128.GreaterThan(o0 + s0, limits);
            }

            bad = gathered == Vector128<uint>.Zero ? 0 : ulong.MaxValue;
        }

        ref TOffset offset = ref MemoryMarshal.GetReference(offsets);
        ref TSize size = ref MemoryMarshal.GetReference(sizes);
        for (; i < offsets.Length; i++)
        {
            ulong o = (ulong)Widen(Unsafe.Add(ref offset, i));
            ulong s = (ulong)Widen(Unsafe.Add(ref size, i));
            bad |= (o | s) | (o + s > limit ? ulong.MaxValue : 0);
        }

        return (bad >> 63) == 0;
    }

    /// <summary>Walks the rows to the first one out of range, and raises on it.</summary>
    private static void ReportFirst<TOffset, TSize>(ReadOnlySpan<TOffset> typedOffsets, ReadOnlySpan<TSize> typedSizes, int elementsLength)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
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
    /// the runtime folds away per instantiation, leaving one widening conversion.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Widen<T>(T value)
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
