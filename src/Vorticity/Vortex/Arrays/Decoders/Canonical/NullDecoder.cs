using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.null</c>: an all-null column with no payload at all.</summary>
internal sealed class NullDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.null";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullDecoder Instance = new NullDecoder();

    private NullDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.null"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Null;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Null, Id);

        return context.Canonical.AddNull(dtype, length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node) => true;

    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype) => true;

    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Null, Id);

        return context.Canonical.AddNull(dtype, count);
    }
}
