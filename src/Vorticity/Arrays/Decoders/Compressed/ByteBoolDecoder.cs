using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.bytebool</c>: one byte per boolean, into a canonical bitmap. Any non-zero
/// byte means true, not only one.
/// </summary>
public sealed class ByteBoolDecoder : ArrayDecoder
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
}
