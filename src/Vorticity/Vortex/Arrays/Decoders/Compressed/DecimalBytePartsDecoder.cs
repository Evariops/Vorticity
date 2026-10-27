using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.decimal_byte_parts</c> into a canonical decimal. The encoding reserves several
/// primitive parts but only the most significant one is ever present, so the decode relabels that
/// signed child's buffer as a decimal instead of recomposing anything, and stays zero-copy.
/// Storage width therefore comes from the child, not from the precision, which is why no
/// "storage wide enough for the precision" check applies here.
/// </summary>
internal sealed class DecimalBytePartsDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.decimal_byte_parts";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DecimalBytePartsDecoder Instance = new DecimalBytePartsDecoder();

    private DecimalBytePartsDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.decimal_byte_parts"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.DecimalByteParts;

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Asks the most significant part, which is the unscaled value: a comparison, a range or a set of a decimal column
    /// is the question it asks of those integers, and the part's encoding answers it.
    /// </summary>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        PType msp = Part(in node, dtype);
        int width = msp.ByteWidth() * 8;
        Compute.IntegerQuestion question;
        if (predicate.Decimals is { } range)
        {
            question = Compute.IntegerQuestion.OfRange(range, width, signed: true);
        }
        else if (predicate.DecimalMembers is { } members)
        {
            question = Compute.IntegerQuestion.OfMembers(members, width, signed: true);
        }
        else
        {
            return false;
        }

        return context.TryEvaluateIntegersChild(
            in node, 0, context.Types.Primitive(msp, dtype.Nullability), length, in question, ref bits);
    }

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, 0, length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount == 1 && context.ChildDecodesRange(in node, 0);
    }

    /// <summary>The part's range, relabelled: the unscaled values of the range's rows.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count);
    }

    private static int Core(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        PType msp = Part(in node, dtype);

        // The child is typed with the array's own nullability, and its validity becomes the whole
        // array's.
        DType childType = context.Types.Primitive(msp, dtype.Nullability);
        int childIndex = CanonicalSupport.ExpandIfConstant(context, start == 0 && count == length
            ? context.DecodeChild(in node, 0, childType, length)
            : context.DecodeChildRange(in node, 0, childType, length, start, count));
        length = count;
        CanonicalNode child = context.Canonical.GetNode(childIndex);

        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "msp", child.Kind, "a Primitive");
        }

        if (child.PType != msp)
        {
            CompressedThrow.Format(
                $"{Id}'s msp child decoded as {child.PType.Name()}; {msp.Name()} was declared.");
        }

        if (child.Length != length)
        {
            CompressedThrow.ChildLength(Id, "msp", child.Length, length);
        }

        DecimalStorageType storage = DecimalStorage.FromByteWidth(msp.ByteWidth());
        return context.Canonical.AddDecimal(
            dtype, length, child.Validity, storage, dtype.Precision, dtype.Scale, child.Values);
    }

    /// <summary>The type of the most significant part, checked against the node's shape first.</summary>
    /// <exception cref="VortexFormatException">The node is not this encoding over one child, or its part is unsigned.</exception>
    private static PType Part(in ArrayNode node, DType dtype)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Decimal, Id);

        DecimalBytePartsMetadata metadata = DecimalBytePartsMetadata.Read(node.Metadata);
        PType msp = metadata.ZerothChildPType;

        // An unsigned most significant part would reinterpret the sign bit as magnitude, so this is
        // a correctness check, not a taste one.
        if (!msp.IsSignedInteger())
        {
            CompressedThrow.Format(
                $"{Id}'s zeroth_child_ptype is {msp.Name()}; a signed integer is required.");
        }

        return msp;
    }
}
