using System;
using System.Buffers.Binary;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>fastlanes.for</c>: <c>value = encoded + reference</c>, wrapping. The node's metadata
/// is not empty; it is a bare protobuf scalar carrying the reference without its dtype, so an empty
/// metadata means a null reference and is a format error rather than a zero reference.
/// </summary>
internal sealed class ForDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.for";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ForDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.for"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesFor;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start: 0, count: length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount == 1 && context.ChildDecodesRange(in node, 0);
    }

    /// <summary>Transparent to a range as to a take: the child's range, plus the reference.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// Frame of reference is transparent to a take: it adds a constant to every row, so the rows it
    /// is asked for are the rows its child is asked for.
    /// </summary>
    /// <remarks>
    /// The offset add runs over whatever strategy the child uses, which matters far more than its
    /// own cost suggests: the child is almost always <c>fastlanes.bitpacked</c>, so without this the
    /// positional access underneath is never reached.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true, start: 0, count: wanted.Length);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective, int start, int count)
    {

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);

        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);

        // The reference is interpreted against the node's own dtype, which is what makes
        // "the reference has the array's dtype" automatic rather than a separate check.
        TypedScalar reference = TypedScalarReader.Read(node.Metadata, dtype, context.Scalars, context.Types);
        if (reference.IsNull)
        {
            CompressedThrow.Format("Reference value cannot be null.");
        }

        int width = ptype.ByteWidth();
        Span<byte> referenceBytes = stackalloc byte[8];
        referenceBytes = referenceBytes[..width];
        reference.WriteTo(referenceBytes, ptype);
        ulong referenceBits = ReadBits(referenceBytes);

        // The encoded child carries the array's dtype unchanged, including its nullability, and the
        // node has no validity of its own: the child's is the array's.
        int encoded = selective
            ? context.DecodeChildSelected(in node, 0, dtype, length, wanted)
            : start == 0 && count == length
                ? context.DecodeChild(in node, 0, dtype, length)
                : context.DecodeChildRange(in node, 0, dtype, length, start, count);

        // Everything below is expressed in the number of rows produced, which the selection or
        // the range shortens; the child's own bound checks still use the node's declared length.
        int produced = count;

        // The child is checked before the zero-reference shortcut, so a child that decoded to the
        // wrong shape is rejected whatever the reference happens to be.
        CanonicalNode child = context.Canonical.GetNode(encoded);
        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "encoded", child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s encoded child decoded as {child.PType.Name()}; {ptype.Name()} was required.");
        }

        if (child.Length != produced)
        {
            CompressedThrow.ChildLength(Id, "encoded", child.Length, produced);
        }

        // A zero reference adds nothing, so the child is returned untouched: same buffer, same
        // validity, same dtype.
        if (referenceBits == 0)
        {
            return encoded;
        }

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, "FoR values");
        if (total == 0)
        {
            return context.Canonical.AddPrimitive(
                dtype, produced, child.Validity, ptype, VortexBuffer.Empty);
        }

        // Left uninitialized: the kernel writes all `total` bytes, and the zero-length case
        // returned above it.
        //
        // The reference is added into a fresh buffer rather than into the child's own, because a
        // child that came straight from the file is a view of a read-only mapping and writing to it
        // faults. Guarding the in-place form would buy nothing anyway: the kernel pass is paid
        // either way, so only the second buffer's traffic is at stake.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, width, Id, out Span<byte> destination);
        IntegerKernels.AddWrapping(child.Values.Span[..total], destination, width, referenceBits);
        return context.Canonical.AddPrimitive(dtype, produced, child.Validity, ptype, output);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(value),
    };
}
