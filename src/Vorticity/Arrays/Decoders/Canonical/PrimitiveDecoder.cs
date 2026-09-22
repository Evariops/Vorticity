using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.primitive</c>: one values buffer of fixed-width values, read zero-copy, with
/// an optional validity child. Both the buffer's exact byte length and its alignment are checked
/// before it is ever reinterpreted as the physical type.
/// </summary>
internal sealed class PrimitiveDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.primitive";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly PrimitiveDecoder Instance = new PrimitiveDecoder();

    private PrimitiveDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.primitive"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Primitive;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Primitive, Id);

        PType ptype = dtype.PType;
        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        VortexBuffer values = node.GetBuffer(0);
        CanonicalSupport.RequireExactBuffer(values, length, ptype.ByteWidth(), Id + " values");

        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, values);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ValidityDecodesRange(in node, 0);
    }

    /// <summary>A window onto the values buffer: no byte moves, whatever the range.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Primitive, Id);

        PType ptype = dtype.PType;
        int width = ptype.ByteWidth();
        VortexBuffer values = node.GetBuffer(0);
        CanonicalSupport.RequireExactBuffer(values, length, width, Id + " values");
        Validity validity = context.DecodeValidityRange(in node, 0, dtype.Nullability, length, start, count);

        return context.Canonical.AddPrimitive(
            dtype, count, validity, ptype, values.Slice((int)((long)start * width), (int)((long)count * width)));
    }
}
