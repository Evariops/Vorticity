using System;
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

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        CanonicalSupport.RequireBinaryLike(dtype, Id);

        int bufferCount = node.BufferCount;
        if (bufferCount < 1)
        {
            throw new VortexFormatException(
                $"{Id} requires at least 1 buffer (the views); this node has {bufferCount}.");
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        int dataBufferCount = bufferCount - 1;
        VortexBuffer views = node.GetBuffer(dataBufferCount);
        CanonicalSupport.RequireExactBuffer(views, length, CanonicalSupport.ViewSize, Id + " views");

        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackBuffers];
        Scratch<VortexBuffer> data = new Scratch<VortexBuffer>(dataBufferCount, stack);
        try
        {
            Span<VortexBuffer> buffers = data.Span;
            for (int i = 0; i < dataBufferCount; i++)
            {
                buffers[i] = node.GetBuffer(i);
            }

            ValidateViews(context, views.Span, buffers, validity, length, dtype.Kind == DTypeKind.Utf8);
            return context.Canonical.AddVarBinView(dtype, length, validity, views, buffers);
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
