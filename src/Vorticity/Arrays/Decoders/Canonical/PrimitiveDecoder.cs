// vortex.primitive - vortex-array-0.86.1/src/arrays/primitive/vtable/mod.rs `deserialize`.
// Phase 1 contract §9.1: one buffer (`values`), an optional validity child at index 0, empty
// metadata. Both the exact byte length and the alignment are class I and are checked before the
// buffer is ever reinterpreted.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.primitive</c>: fixed-width values, read zero-copy.</summary>
public sealed class PrimitiveDecoder : ArrayDecoder
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
}
