// vortex.zigzag - vortex-zigzag-0.86.1/src/array.rs and src/compress.rs.
// Empty metadata, no buffers, one child: the unsigned counterpart of the array's own signed
// primitive type, with the SAME nullability. Decode is (x >> 1) ^ -(x & 1).
using System;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.zigzag</c> back into its signed primitive type.</summary>
public sealed class ZigZagDecoder : ArrayDecoder
{
    private const string Id = "vortex.zigzag";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ZigZagDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.zigzag"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ZigZag;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// ZigZag is transparent to a take: it is a bijection on single values, so the rows it is asked
    /// for are the rows its child is asked for.
    /// </summary>
    /// <remarks>
    /// Not in the take table, and it belongs there for the same reason `fastlanes.for` does: since
    /// the writer started choosing between them, zigzag is the other thing that sits directly above
    /// `fastlanes.bitpacked`, and without this the positional access underneath is unreachable on
    /// every column that took the zigzag branch.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);

        if (!node.Metadata.IsEmpty)
        {
            CompressedThrow.Format(
                $"{Id} expects empty metadata, got {node.Metadata.Length} bytes.");
        }

        if (dtype.Kind != DTypeKind.Primitive || !dtype.PType.IsSignedInteger())
        {
            CompressedThrow.Format($"{Id} requires a signed integer primitive dtype, not {dtype}.");
        }

        PType signed = dtype.PType;
        PType unsigned = CompressedValues.ToUnsigned(signed);
        DType encodedType = context.Types.Primitive(unsigned, dtype.Nullability);

        int encoded = selective
            ? context.DecodeChildSelected(in node, 0, encodedType, length, wanted)
            : context.DecodeChild(in node, 0, encodedType, length);
        int produced = selective ? wanted.Length : length;
        CanonicalNode child = context.Canonical.GetNode(encoded);
        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "encoded", child.Kind, "a Primitive");
        }

        if (child.PType != unsigned)
        {
            CompressedThrow.Format(
                $"{Id}'s encoded child decoded as {child.PType.Name()}; {unsigned.Name()} was required.");
        }

        if (child.Length != produced)
        {
            CompressedThrow.ChildLength(Id, "encoded", child.Length, produced);
        }

        int width = signed.ByteWidth();
        int total = ArrayDecodeContext.CheckedMultiply(produced, width, "ZigZag values");
        if (total == 0)
        {
            return context.Canonical.AddPrimitive(
                dtype, produced, child.Validity, signed, VortexBuffer.Empty);
        }

        VortexBuffer output = CompressedValues.Allocate(
            context, total, width, Id, out Span<byte> destination);
        IntegerKernels.ZigZagDecode(child.Values.Span[..total], destination, width);
        return context.Canonical.AddPrimitive(dtype, produced, child.Validity, signed, output);
    }
}
