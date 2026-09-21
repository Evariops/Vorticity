using System;
using System.Buffers.Binary;

using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.patched</c>: an inner primitive array with sparse overrides indexed per
/// chunk and per lane. The patches are stored the way a compressed sparse row is: `lane_offsets`
/// holds one entry per chunk and lane plus a terminator, and each patch's index is an offset
/// within its own chunk, which is why it fits in a <c>u16</c>.
/// </summary>
/// <remarks>
/// No conformant writer emits this encoding, which the reference implementation only builds in
/// memory; it is decoded anyway because doing so costs nothing.
/// </remarks>
internal sealed class PatchedArrayDecoder : ArrayDecoder
{
    private const string Id = "vortex.patched";

    /// <summary>Values per patch-indexing chunk.</summary>
    private const int ChunkSize = 1024;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly PatchedArrayDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.patched"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Patched;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 4, Id);

        // Any primitive, not just an integer: patches exist chiefly to carry the values a float
        // codec could not represent, so restricting this to integers would refuse the main case.
        if (dtype.Kind != DTypeKind.Primitive)
        {
            CompressedThrow.Format($"{Id} requires a primitive dtype; the node declares {dtype.Kind}.");
        }

        PType ptype = dtype.PType;
        PatchedArrayMetadata metadata = PatchedArrayMetadata.Read(node.Metadata);

        int patches = checked((int)metadata.PatchCount);
        int lanes = (int)metadata.LaneCount;
        int offset = (int)metadata.Offset;
        int chunks = (int)(((long)offset + length + ChunkSize - 1) / ChunkSize);
        int laneOffsetCount = checked((chunks * lanes) + 1);

        int innerIndex = context.DecodeChild(in node, 0, dtype, length);
        int laneOffsetsIndex = context.DecodeChild(
            in node, 1, context.Types.Primitive(PType.U32, Nullability.NonNullable), laneOffsetCount);
        int indicesIndex = context.DecodeChild(
            in node, 2, context.Types.Primitive(PType.U16, Nullability.NonNullable), patches);
        int valuesIndex = context.DecodeChild(in node, 3, dtype, patches);

        CanonicalNode inner = Require(context, innerIndex, ptype, "inner", length);
        CanonicalNode laneOffsets = Require(context, laneOffsetsIndex, PType.U32, "lane offsets", laneOffsetCount);
        CanonicalNode indices = Require(context, indicesIndex, PType.U16, "patch indices", patches);
        CanonicalNode values = Require(context, valuesIndex, ptype, "patch values", patches);

        int width = ptype.ByteWidth();
        VortexBuffer output = CompressedValues.Allocate(
            context, length * width, width, Id, out Span<byte> destination);
        inner.Values.Span.CopyTo(destination);

        Apply(
            destination,
            laneOffsets.Values.Span,
            indices.Values.Span,
            values.Values.Span,
            width,
            lanes,
            offset,
            length,
            chunks);

        return context.Canonical.AddPrimitive(dtype, length, inner.Validity, ptype, output);
    }

    private static void Apply(
        Span<byte> destination,
        ReadOnlySpan<byte> laneOffsets,
        ReadOnlySpan<byte> indices,
        ReadOnlySpan<byte> values,
        int width,
        int lanes,
        int offset,
        int length,
        int chunks)
    {
        int patchCount = values.Length / width;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                laneOffsets.Slice(chunk * lanes * sizeof(uint), sizeof(uint)));
            int stop = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                laneOffsets.Slice(((chunk * lanes) + lanes) * sizeof(uint), sizeof(uint)));

            // File-supplied, so checked: a descending or out-of-range pair would index the patch
            // children outside their own length.
            if (start > stop || stop > patchCount)
            {
                CompressedThrow.Format(
                    $"{Id}'s lane offsets give chunk {chunk} the patch range [{start}, {stop}) of {patchCount}.");
            }

            for (int i = start; i < stop; i++)
            {
                int within = BinaryPrimitives.ReadUInt16LittleEndian(indices.Slice(i * sizeof(ushort), sizeof(ushort)));
                long index = ((long)chunk * ChunkSize) + within;

                // Out-of-view patches are normal on a sliced array, not corruption: slicing keeps
                // the whole chunk's patch list and narrows the window with `offset`.
                if (index < offset || index >= (long)offset + length)
                {
                    continue;
                }

                values.Slice(i * width, width).CopyTo(destination.Slice((int)(index - offset) * width, width));
            }
        }
    }

    private static CanonicalNode Require(
        ArrayDecodeContext context, int index, PType ptype, string what, int expected)
    {
        CanonicalNode child = context.Canonical.GetNode(index);
        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, what, child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s {what} child decoded as {child.PType.Name()}; {ptype.Name()} was required.");
        }

        if (child.Length != expected)
        {
            CompressedThrow.ChildLength(Id, what, child.Length, expected);
        }

        return child;
    }
}
