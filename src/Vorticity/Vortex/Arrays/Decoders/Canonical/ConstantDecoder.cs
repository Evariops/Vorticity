using System;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.constant</c>: one scalar, repeated.</summary>
/// <remarks>
/// The serialized scalar is the node's only buffer, and the node's metadata is ignored rather than
/// required to be empty: writers leave bytes there, and refusing them would refuse files that are
/// otherwise readable. This is the one encoding whose metadata is not checked.
/// </remarks>
internal sealed class ConstantDecoder : ArrayDecoder
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

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// A predicate on a constant of integers, the question it asks of the one value; on any other, the value judged once as
    /// a column of one row (<see cref="Compute.ValueVerdicts"/>), a match of bytes on no text declining.
    /// </summary>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (dtype.Kind == DTypeKind.Primitive && dtype.PType.IsInteger()
            && Compute.IntegerQuestion.TryOf(predicate, dtype.PType, out Compute.IntegerQuestion question))
        {
            return TryEvaluateIntegers(context, in node, dtype, length, in question, ref bits);
        }

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        TypedScalar scalar = TypedScalarReader.Read(node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);
        int value = ConstantCanonicalizer.Build(context, dtype, 1, in scalar);
        Span<byte> verdict = stackalloc byte[2];
        if (!Compute.ValueVerdicts.TryJudge(context.Canonical, value, predicate, verdict[..1], verdict[1..]))
        {
            return false;
        }

        if (verdict[0] == Compute.Trilean.Unknown)
        {
            bits.FillUnknown();
        }
        else
        {
            bits.Fill(verdict[0] == Compute.Trilean.True);
        }

        return true;
    }

    /// <summary>The one value judged once, its verdict every row's; a null one leaves every row unknown.</summary>
    /// <inheritdoc/>
    public override bool TryEvaluateIntegers(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, in Compute.IntegerQuestion question,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (dtype.Kind != DTypeKind.Primitive || !dtype.PType.IsInteger())
        {
            return false;
        }

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        TypedScalar scalar = TypedScalarReader.Read(node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);
        if (scalar.IsNull)
        {
            bits.FillUnknown();
            return true;
        }

        Span<byte> value = stackalloc byte[8];
        value.Clear();
        scalar.WriteTo(value[..dtype.PType.ByteWidth()], dtype.PType);
        bits.Fill(question.Holds(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value)));
        return true;
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node) => true;

    /// <summary>
    /// A constant decodes to one value whatever its length, except as a list, whose offsets and
    /// sizes are one per row, and in a struct holding one; and except a null one, whose rows are
    /// laid out zeroed under their validity (<see cref="ConstantCanonicalizer"/>): a merge's input
    /// whose variant column was null over a chunk of a million rows decoded it whole, 32 MB, and
    /// kept it for every batch, where a window of its rows does.
    /// </summary>
    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OneValue(dtype))
        {
            return false;
        }

        // A malformed node is the decode's to refuse.
        return dtype.Kind == DTypeKind.Null || node.BufferCount != 1
            || !TypedScalarReader.Read(node.GetBuffer(0).Span, dtype, context.Scalars, context.Types).IsNull;
    }

    private static bool OneValue(DType dtype)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.List:
            case DTypeKind.FixedSizeList:
            case DTypeKind.Map:
            case DTypeKind.Union:
                return false;
            case DTypeKind.Struct:
                for (int i = 0; i < dtype.FieldCount; i++)
                {
                    if (!OneValue(dtype.GetField(i)))
                    {
                        return false;
                    }
                }

                return true;
            case DTypeKind.Extension:
                return OneValue(dtype.StorageType);
            default:
                return true;
        }
    }

    /// <summary>Every row is the same value, so a range only changes how many are built.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        TypedScalar scalar = TypedScalarReader.Read(
            node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);

        return ConstantCanonicalizer.Build(context, dtype, count, in scalar);
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
