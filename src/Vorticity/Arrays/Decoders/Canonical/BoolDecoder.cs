// vortex.bool - vortex-array-0.86.1/src/arrays/bool/vtable/mod.rs `deserialize`.
// Phase 1 contract §9.1: one buffer (`bits`), an optional validity child at index 0, and a
// BoolMetadata carrying a BIT offset below 8.
//
// The offset is the trap. It is not normalized away: shifting the bitmap would cost an allocation
// and a copy per batch (contract §2.6 rule 5), so it travels into the canonical node and every
// consumer applies it. `encodings/bool_bit_offset{3,7,straddle}` exist precisely to break a
// decoder that ignores it.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.bool</c>: an LSB-first bitmap with a sub-byte start offset.</summary>
public sealed class BoolDecoder : ArrayDecoder
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

        // Class I: BoolMetadata.Read already rejects offset >= 8, which is the bound
        // `vortex_ensure!(offset < 8)` in vortex-array-0.86.1/src/arrays/bool/array.rs.
        BoolMetadata metadata = BoolMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Bool, Id);

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        // Class I: AddBool rejects a bitmap that cannot hold `offset + length` bits.
        VortexBuffer bits = node.GetBuffer(0);
        return context.Canonical.AddBool(dtype, length, validity, bits, (int)metadata.Offset);
    }
}
