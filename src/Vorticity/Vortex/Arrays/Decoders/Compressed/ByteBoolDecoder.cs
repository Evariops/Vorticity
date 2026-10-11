using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.bytebool</c>: one byte per boolean, into a canonical bitmap. Any non-zero
/// byte means true, not only one.
/// </summary>
internal sealed class ByteBoolDecoder : ArrayDecoder
{
    private const string Id = "vortex.bytebool";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ByteBoolDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.bytebool"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ByteBool;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        VortexBuffer values = Values(in node, dtype, length);
        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        if (length == 0)
        {
            return context.Canonical.AddBool(dtype, 0, validity, VortexBuffer.Empty, 0);
        }

        int bitmapBytes = (length + 7) / 8;

        // Uninitialized: PackBytes writes every byte of the bitmap, the partial last one included.
        VortexBuffer bits = CompressedValues.AllocateUninitialized(
            context, bitmapBytes, 8, Id, out Span<byte> destination);
        BitmapKernels.PackBytes(values.Span, destination);

        return context.Canonical.AddBool(dtype, length, validity, bits, 0);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// The wanted bytes read where they lie and packed eight to a byte, and the wanted rows'
    /// validity: no bitmap of the whole, which a take would pack a byte per row to read a few.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        ReadOnlySpan<byte> source = Values(in node, dtype, length).Span;
        Validity validity = context.DecodeValiditySelected(in node, 0, dtype.Nullability, length, wanted);
        int count = wanted.Length;
        VortexBuffer bits = CompressedValues.AllocateUninitialized(
            context, Math.Max((count + 7) / 8, 1), 8, Id, out Span<byte> destination);
        destination[0] = 0;
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            int packed = 0;
            for (int k = 0; k < 8; k++)
            {
                packed |= (source[wanted[i + k]] != 0 ? 1 : 0) << k;
            }

            destination[i >> 3] = (byte)packed;
        }

        if (i < count)
        {
            int packed = 0;
            for (int k = 0; i + k < count; k++)
            {
                packed |= (source[wanted[i + k]] != 0 ? 1 : 0) << k;
            }

            destination[i >> 3] = (byte)packed;
        }

        return context.Canonical.AddBool(dtype, count, validity, bits, 0);
    }

    /// <summary>The node's one byte per value, checked against its shape.</summary>
    private static VortexBuffer Values(in ArrayNode node, DType dtype, int length)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        if (!node.Metadata.IsEmpty)
        {
            CompressedThrow.Format(
                $"{Id} expects empty metadata, got {node.Metadata.Length} bytes.");
        }

        if (dtype.Kind != DTypeKind.Bool)
        {
            CompressedThrow.Format($"{Id} requires a Bool dtype, not {dtype}.");
        }

        // The buffer holds one byte per element, exactly; anything else is a malformed node.
        VortexBuffer values = node.GetBuffer(0);
        if (values.Length != length)
        {
            CompressedThrow.Format(
                $"{Id} needs exactly {length} value bytes; the buffer holds {values.Length}.");
        }

        return values;
    }
}
