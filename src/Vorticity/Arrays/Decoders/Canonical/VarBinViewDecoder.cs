using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Unicode;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.varbinview</c>: Arrow-style 16-byte views over a node's data buffers. The
/// views are the last buffer, not the first, and the data buffers are all the others, of which
/// there may be none; reading the first buffer as views would read data as views and produce
/// plausible garbage.
/// </summary>
internal sealed class VarBinViewDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.varbinview";

    private const int StackBuffers = 8;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly VarBinViewDecoder Instance = new VarBinViewDecoder();

    private VarBinViewDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.varbinview"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.VarBinView;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, 0, length, ranged: false);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ValidityDecodesRange(in node, 0);
    }

    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ValidityMaterializesNothing(in node, 0);
    }

    /// <summary>
    /// The range's views over the whole data buffers: the views are sixteen bytes a row and the
    /// data they point into is shared by every row, so a range is a window onto the views and
    /// nothing else moves.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count, ranged: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count, bool ranged)
    {
        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        CanonicalSupport.RequireBinaryLike(dtype, Id);

        int bufferCount = node.BufferCount;
        if (bufferCount < 1)
        {
            throw new VortexFormatException(
                $"{Id} requires at least 1 buffer (the views); this node has {bufferCount}.");
        }

        Validity validity = ranged
            ? context.DecodeValidityRange(in node, 0, dtype.Nullability, length, start, count)
            : context.DecodeValidity(in node, 0, dtype.Nullability, length);

        int dataBufferCount = bufferCount - 1;
        VortexBuffer views = node.GetBuffer(dataBufferCount);
        CanonicalSupport.RequireExactBuffer(views, length, CanonicalSupport.ViewSize, Id + " views");
        if (ranged)
        {
            views = views.Slice(start * CanonicalSupport.ViewSize, count * CanonicalSupport.ViewSize);
        }

        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackBuffers];
        Scratch<VortexBuffer> data = new Scratch<VortexBuffer>(dataBufferCount, stack);
        try
        {
            Span<VortexBuffer> buffers = data.Span;
            for (int i = 0; i < dataBufferCount; i++)
            {
                buffers[i] = node.GetBuffer(i);
            }

            ValidateViews(context, views.Span, buffers, validity, count, dtype.Kind == DTypeKind.Utf8);
            return context.Canonical.AddVarBinView(dtype, count, validity, views, buffers);
        }
        finally
        {
            data.Dispose();
        }
    }

    /// <summary>
    /// Bounds-checks (and, for Utf8, UTF-8-checks) every view a consumer may dereference. A
    /// referencing view's four-byte prefix is compared with the value it points at: the prefix is a
    /// redundant copy the Arrow columnar layout requires, and checking it is what makes
    /// prefix-first comparison sound for readers of the canonical views. Null rows are skipped,
    /// since a null slot's view is garbage by construction and no consumer may dereference it.
    /// </summary>
    internal static void ValidateViews(
        ArrayDecodeContext context,
        ReadOnlySpan<byte> views,
        ReadOnlySpan<VortexBuffer> dataBuffers,
        Validity validity,
        int length,
        bool requireUtf8)
    {
        ValidityMask mask = ValidityMask.From(context, validity);
        ValidateViews(views, dataBuffers, mask, length, requireUtf8);
    }

    /// <summary>The validation itself, over the rows <paramref name="mask"/> says are valid.</summary>
    /// <remarks>
    /// A column without nulls takes a pass that decides every view without a call and defers what a
    /// view cannot decide alone: the bytes of inline values, the prefixes against their values, and
    /// the span of each data buffer the values reference, which one vector sweep then finds to be
    /// ASCII. A column that fails any of it, or has nulls, is validated view by view, which accepts
    /// every valid UTF-8 value and names the row that is not.
    /// </remarks>
    internal static void ValidateViews(
        ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, in ValidityMask mask, int length, bool requireUtf8)
    {
        if (mask.AllValid && dataBuffers.Length <= MaxSweptBuffers && Swept(views, dataBuffers, length, requireUtf8))
        {
            return;
        }

        ValidateEach(views, dataBuffers, mask, length, requireUtf8);
    }

    /// <summary>Data buffers whose referenced spans the sweeping pass tracks on the stack.</summary>
    private const int MaxSweptBuffers = 64;

    /// <summary>
    /// The bytes a data buffer's views leave unnamed between the values they name, per view, up to
    /// which one sweep of the span they reach costs less than a check of each value: measured at
    /// about 150 on 24-byte values, where a check costs a view's pass and a call, and a swept byte a
    /// hundredth of a nanosecond.
    /// </summary>
    private const int SweptGapPerView = 160;

    /// <summary>
    /// Every view of a column without nulls, bounds checked as it goes; true when every prefix
    /// matched and, for Utf8, every inline value and every referenced value is ASCII.
    /// </summary>
    /// <remarks>
    /// The values a buffer's views name are checked by one sweep of the span they reach, which is
    /// as long as the values when a writer laid them in row order. Views scattered over a buffer
    /// much longer than what they name, a window of a chunk whose values are shared or reordered,
    /// would have every batch sweep most of the buffer: their values are checked one by one.
    /// </remarks>
    private static bool Swept(ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, int length, bool requireUtf8)
    {
        Span<uint> lowest = stackalloc uint[MaxSweptBuffers];
        Span<uint> highest = stackalloc uint[MaxSweptBuffers];
        lowest.Fill(uint.MaxValue);
        highest.Clear();

        ref byte view = ref MemoryMarshal.GetReference(views);

        // Every byte of every inline view, gathered by or: its length, below 0x80, its value and
        // its padding, which writers leave zero. A high bit here is a value that is not ASCII or
        // padding that is not zero, and either goes to the view-by-view validation, which masks
        // the padding off and decides the value.
        Vector128<byte> inline = Vector128<byte>.Zero;
        uint prefixes = 0;
        uint bufferCount = (uint)dataBuffers.Length;

        // The buffer the last referencing view named, whose span is kept in registers: views name
        // their buffers in runs, and the arrays are written when the run ends.
        uint current = uint.MaxValue;
        ref byte currentBase = ref Unsafe.NullRef<byte>();
        ulong currentLength = 0;
        uint low = uint.MaxValue;
        uint high = 0;
        int i = 0;
        int scalarUntil = 0;
        while (i < length)
        {
            // Four views at a time while all four are inline: one maximum of their lengths decides
            // it, and their bytes join the gathered ones with no further look. Four that are not
            // go one by one before the next four are tried.
            if (i >= scalarUntil && i <= length - 4)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref view);
                Vector128<byte> b = Vector128.LoadUnsafe(ref view, (nuint)CanonicalSupport.ViewSize);
                Vector128<byte> c = Vector128.LoadUnsafe(ref view, (nuint)(2 * CanonicalSupport.ViewSize));
                Vector128<byte> d = Vector128.LoadUnsafe(ref view, (nuint)(3 * CanonicalSupport.ViewSize));
                Vector128<uint> longest = Vector128.Max(
                    Vector128.Max(a.AsUInt32(), b.AsUInt32()), Vector128.Max(c.AsUInt32(), d.AsUInt32()));
                if (longest.ToScalar() <= CanonicalSupport.MaxInlineViewLength)
                {
                    inline |= (a | b) | (c | d);
                    i += 4;
                    view = ref Unsafe.Add(ref view, 4 * CanonicalSupport.ViewSize);
                    continue;
                }

                scalarUntil = i + 4;
            }

            uint size = Unsafe.ReadUnaligned<uint>(ref view);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                inline |= Vector128.LoadUnsafe(ref view);
                i++;
                view = ref Unsafe.Add(ref view, CanonicalSupport.ViewSize);
                continue;
            }

            uint bufferIndex = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 8));
            uint offset = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12));
            if (bufferIndex != current)
            {
                if (bufferIndex >= bufferCount)
                {
                    ThrowBufferIndex(i, bufferIndex, dataBuffers.Length);
                }

                if (current != uint.MaxValue)
                {
                    lowest[(int)current] = low;
                    highest[(int)current] = high;
                }

                current = bufferIndex;
                ReadOnlySpan<byte> target = dataBuffers[(int)bufferIndex].Span;
                currentBase = ref MemoryMarshal.GetReference(target);
                currentLength = (uint)target.Length;
                low = lowest[(int)bufferIndex];
                high = highest[(int)bufferIndex];
            }

            ulong end = (ulong)offset + size;
            if (end > currentLength)
            {
                ThrowViewRange(i, offset, size, (int)currentLength);
            }

            prefixes |= Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 4)) ^
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref currentBase, offset));
            low = Math.Min(low, offset);
            high = Math.Max(high, (uint)end);
            i++;
            view = ref Unsafe.Add(ref view, CanonicalSupport.ViewSize);
        }

        if (current != uint.MaxValue)
        {
            lowest[(int)current] = low;
            highest[(int)current] = high;
        }

        if (prefixes != 0)
        {
            return false;
        }

        if (!requireUtf8)
        {
            return true;
        }

        if (inline.ExtractMostSignificantBits() != 0)
        {
            return false;
        }

        ulong scattered = 0;
        for (int b = 0; b < dataBuffers.Length; b++)
        {
            if (highest[b] <= lowest[b])
            {
                continue;
            }

            // A span longer than the gap allowed to every view of the batch may be views scattered
            // over the buffer, or long values in row order: a sample of the views tells which.
            if ((ulong)(highest[b] - lowest[b]) > (ulong)length * SweptGapPerView && Scattered(views, length, b))
            {
                scattered |= 1UL << b;
            }
            else if (!System.Text.Ascii.IsValid(dataBuffers[b].Span[(int)lowest[b]..(int)highest[b]]))
            {
                return false;
            }
        }

        return scattered == 0 || ScatteredAscii(views, dataBuffers, length, scattered);
    }

    /// <summary>Views read to tell a buffer's values scattered from long ones in row order.</summary>
    private const int SampledViews = 64;

    /// <summary>
    /// Whether the first <see cref="SampledViews"/> views that name <paramref name="buffer"/> leave
    /// more bytes unnamed between their values than a check of each value costs: what a sweep would
    /// pay for nothing. Only which way the values are checked rides on it.
    /// </summary>
    private static bool Scattered(ReadOnlySpan<byte> views, int length, int buffer)
    {
        uint low = uint.MaxValue;
        uint high = 0;
        ulong bytes = 0;
        int count = 0;
        for (int i = 0; i < length && count < SampledViews; i++)
        {
            ReadOnlySpan<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);
            if (size <= CanonicalSupport.MaxInlineViewLength
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]) != (uint)buffer)
            {
                continue;
            }

            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
            low = Math.Min(low, offset);
            high = Math.Max(high, offset + size);
            bytes += size;
            count++;
        }

        return count > 0 && high - low > bytes + ((ulong)count * SweptGapPerView);
    }

    /// <summary>
    /// Whether every value the views name in the buffers of <paramref name="scattered"/> is ASCII,
    /// checked value by value: the views were bounds checked by the sweep.
    /// </summary>
    private static bool ScatteredAscii(ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, int length, ulong scattered)
    {
        for (int i = 0; i < length; i++)
        {
            ReadOnlySpan<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                continue;
            }

            int bufferIndex = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            if ((scattered & (1UL << bufferIndex)) == 0)
            {
                continue;
            }

            int offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
            if (!System.Text.Ascii.IsValid(dataBuffers[bufferIndex].Span.Slice(offset, (int)size)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bounds-checks (and, for Utf8, UTF-8-checks) every view a consumer may dereference, one at a
    /// time; the rows <paramref name="mask"/> says are null are skipped.
    /// </summary>
    private static void ValidateEach(
        ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, in ValidityMask mask, int length, bool requireUtf8)
    {
        if (mask.AllInvalid)
        {
            return;
        }

        bool allValid = mask.AllValid;
        for (int i = 0; i < length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                continue;
            }

            ReadOnlySpan<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);

            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                // Inlined: the value is the first `size` of the 12 bytes that follow, so it can
                // never escape the view. Only the UTF-8 rule can fail.
                if (requireUtf8 && !IsInlineAscii(view, (int)size)
                    && !Utf8.IsValid(view.Slice(4, (int)size)))
                {
                    ThrowNotUtf8(i);
                }

                continue;
            }

            uint bufferIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);

            if (bufferIndex >= (uint)dataBuffers.Length)
            {
                ThrowBufferIndex(i, bufferIndex, dataBuffers.Length);
            }

            VortexBuffer target = dataBuffers[(int)bufferIndex];
            if ((ulong)offset + size > (ulong)(uint)target.Length)
            {
                ThrowViewRange(i, offset, size, target.Length);
            }

            // size > 12 on this path, so the value always has at least the four prefix bytes, and
            // the bounds check above puts both casts inside an int-length buffer.
            ReadOnlySpan<byte> value = target.Span.Slice((int)offset, (int)size);
            if (!view.Slice(4, 4).SequenceEqual(value[..4]))
            {
                ThrowPrefixMismatch(i);
            }

            if (requireUtf8 && !Utf8.IsValid(value))
            {
                ThrowNotUtf8(i);
            }
        }
    }

    /// <summary>
    /// True when an inlined value is all-ASCII, and therefore valid UTF-8, without a call.
    /// </summary>
    /// <param name="view">The whole sixteen-byte view; its bytes 4..16 hold the value.</param>
    /// <param name="size">The value's length, twelve or fewer by the inline rule.</param>
    /// <remarks>
    /// Encodings whose values tile a heap can validate the whole heap in one pass and test one byte
    /// per boundary; a varbinview does not tile, and most of its views carry their value inline,
    /// inside the sixteen bytes of the view itself, where there is no buffer to sweep. So the
    /// inline case is the one worth answering without a call.
    ///
    /// The twelve inline bytes are always readable, which is what makes this branchless: the value
    /// lives at [4, 4 + size) of a view that is exactly sixteen bytes, so the two loads below are
    /// in bounds whatever <paramref name="size"/> is, and the mask is what restricts them to the
    /// value. Walking those bytes in a loop instead is slower than the call it would replace, since
    /// on so few bytes an intrinsified validator beats a scalar loop; the cost being removed is the
    /// call itself, so whatever replaces it must not be a loop.
    ///
    /// Everything with a high bit set falls through to <c>Utf8.IsValid</c>, which decides. Nothing
    /// here changes what is accepted.
    /// </remarks>
    private static bool IsInlineAscii(ReadOnlySpan<byte> view, int size)
    {
        ulong low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(view.Slice(4, 8));
        ulong high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));

        // `size` is at most twelve, so the second shift is at most 32 and neither is the
        // undefined-by-masking case a 64-bit shift would be.
        ulong lowMask = size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
        ulong highMask = size <= 8 ? 0UL : (1UL << ((size - 8) * 8)) - 1;

        const ulong HighBits = 0x8080_8080_8080_8080UL;
        return (((low & lowMask) | (high & highMask)) & HighBits) == 0;
    }

    private static void ThrowPrefixMismatch(int row) =>
        throw new VortexFormatException(
            $"Row {row}'s view prefix does not match the first four bytes of the value it " +
            "references.");

    private static void ThrowNotUtf8(int row) =>
        throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");

    private static void ThrowBufferIndex(int row, uint bufferIndex, int count) =>
        throw new VortexFormatException(
            $"Row {row} references data buffer {bufferIndex}; the array has {count}.");

    private static void ThrowViewRange(int row, uint offset, uint size, int bufferLength) =>
        throw new VortexFormatException(
            $"Row {row} spans [{offset}, {(ulong)offset + size}) of a data buffer holding " +
            $"{bufferLength} bytes.");
}
