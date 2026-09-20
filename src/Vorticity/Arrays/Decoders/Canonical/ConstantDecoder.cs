using System;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.constant</c>: one scalar, repeated.</summary>
/// <remarks>
/// The serialized scalar is the node's only buffer, and the node's metadata is ignored rather than
/// required to be empty: writers leave bytes there, and refusing them would refuse files that are
/// otherwise readable. This is the one encoding whose metadata is not checked.
/// </remarks>
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
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Exactly one buffer, and it is the scalar; the node's metadata is deliberately not read.
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        TypedScalar scalar = TypedScalarReader.Read(
            node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);

        return ConstantCanonicalizer.Build(context, dtype, length, in scalar);
    }

    /// <summary>
    /// Every row is the same value, so selecting rows only changes how many are built.
    /// </summary>
    /// <remarks>
    /// The selection's only effect is its length. It is worth overriding anyway: a constant column
    /// is what a compressor makes of a degenerate one, so it is common, and building a whole batch
    /// of copies to keep a handful of rows is the waste this path exists to remove.
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
