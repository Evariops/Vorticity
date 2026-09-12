// vortex.constant - vortex-array-0.86.1/src/arrays/constant/vtable/mod.rs `deserialize`.
//
// Phase 1 contract §0a C1 corrects spec/METADATA.md here: the metadata is EMPTY and the serialized
// ScalarValue is BUFFER 0. Upstream names the parameter `_metadata` and reads the scalar from
// `buffers[0]`, so the metadata is ignored rather than required-empty - rejecting a stray byte
// there would reject a file upstream reads. This is the one exception to
// EncodingMetadata.RequireEmpty, and it is deliberate.
//
// 494 of the 819 corpus files contain a constant node, because the compressor folds every constant
// column and every all-valid or all-null validity array into one. The filling is done with
// Span.Fill-style bulk writes and a null scalar short-circuits to AllInvalid over zeroed buffers.
using System;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.constant</c>: one scalar, repeated.</summary>
public sealed class ConstantDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.constant";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ConstantDecoder Instance = new ConstantDecoder();

    private ConstantDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.constant"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Constant;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Exactly one buffer, and it is the scalar. `node.Metadata` is NOT read: see the header.
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        TypedScalar scalar = TypedScalarReader.Read(
            node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);

        return ConstantCanonicalizer.Build(context, dtype, length, in scalar);
    }

    /// <summary>
    /// Every row is the same value, so selecting rows only changes how many are built.
    /// </summary>
    /// <remarks>
    /// The take table calls this one "pointwise, trivial" and it is: the selection's only effect is
    /// its length. Worth having anyway - a constant column is what a compressor produces from a
    /// degenerate one, so it is common, and building 65 536 copies to keep 64 is the exact shape of
    /// waste this whole path exists to remove.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        TypedScalar scalar = TypedScalarReader.Read(
            node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);

        return ConstantCanonicalizer.Build(context, dtype, wanted.Length, in scalar);
    }
}
