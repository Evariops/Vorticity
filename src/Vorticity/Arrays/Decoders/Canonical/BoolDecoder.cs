using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.bool</c>: a least-significant-bit-first bitmap with a sub-byte start offset.
/// </summary>
/// <remarks>
/// The bit offset is not normalized away, because shifting the bitmap would cost an allocation and
/// a copy per batch; it travels into the canonical node instead, and every consumer of the bitmap
/// has to apply it.
/// </remarks>
internal sealed class BoolDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.bool";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly BoolDecoder Instance = new BoolDecoder();

    private BoolDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.bool"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Bool;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Reading the metadata already rejects an offset of eight or more, so the offset passed
        // below is a sub-byte one.
        BoolMetadata metadata = BoolMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Bool, Id);

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        // AddBool rejects a bitmap too small to hold `offset + length` bits.
        VortexBuffer bits = node.GetBuffer(0);
        return context.Canonical.AddBool(dtype, length, validity, bits, (int)metadata.Offset);
    }
}
