using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Compressed;
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

    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ValidityMaterializesNothing(in node, 0);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// The wanted values gathered out of the buffer as it lies, and the wanted rows' validity: no
    /// node of the whole, and no pass over a validity bitmap beyond the bits wanted.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Primitive, Id);

        PType ptype = dtype.PType;
        int width = ptype.ByteWidth();
        VortexBuffer values = node.GetBuffer(0);
        CanonicalSupport.RequireExactBuffer(values, length, width, Id + " values");
        Validity validity = context.DecodeValiditySelected(in node, 0, dtype.Nullability, length, wanted);

        int count = wanted.Length;
        VortexBuffer gathered = VortexBuffer.Empty;
        if (count != 0)
        {
            gathered = CanonicalSupport.AllocateUninitialized(
                context, ArrayDecodeContext.CheckedMultiply(count, width, Id + " selected values"), width,
                out Span<byte> destination);
            if (RowKernels.Gather(
                    MemoryMarshal.AsBytes(wanted), PType.I32, values.Span, width, length, destination, count) >= 0)
            {
                throw new VortexFormatException($"{Id} was asked for a row past its {length}.");
            }
        }

        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, gathered);
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
