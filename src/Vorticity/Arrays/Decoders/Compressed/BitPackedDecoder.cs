using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>fastlanes.bitpacked</c>, patches included, into a primitive array.</summary>
/// <remarks>
/// The metadata, never the child count, decides what the children are: without patches, only an
/// optional validity child; with patches, the patch indices and patch values first, then the
/// per-chunk offsets when the metadata declares them, then the optional validity child. Three
/// children on their own are ambiguous between patches with validity and patches with chunk
/// offsets, and only the declared chunk-offsets type tells the two apart.
///
/// The packed buffer must hold exactly one whole block for every block the rows span; that check is
/// what lets the unpack kernel run without a per-element bounds check.
/// </remarks>
internal sealed class BitPackedDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.bitpacked";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly BitPackedDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.bitpacked"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesBitPacked;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// Unpacks only the wanted rows, one at a time, without materializing a single block.
    /// </summary>
    /// <remarks>
    /// The inverse transposition gives positional access to a packed row, so a wanted row is read
    /// without unpacking the block around it. Switching to a whole-block unpack once enough of a
    /// block is wanted would need a crossover density this code does not know; per element is never
    /// the worse choice here, and when every row of a split is wanted the scan does not push the
    /// take down at all.
    ///
    /// Patches are still decoded in full. There are few of them by construction, and they arrive as
    /// their own child arrays, so selecting among them would mean pushing a second, differently
    /// based selection into those children, for a fraction of a fraction.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
        int elementBits = ptype.ByteWidth() * 8;

        if (metadata.BitWidth > (uint)elementBits)
        {
            CompressedThrow.Format(
                $"{Id} bit width {metadata.BitWidth} exceeds the {elementBits} bits of " +
                $"{ptype.Name()}.");
        }

        int bitWidth = (int)metadata.BitWidth;
        int offset = (int)metadata.Offset;

        VortexBuffer packed = node.GetBuffer(0);
        long blocks = ((long)length + offset + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        long expected = blocks * FastLanes.BlockByteLength(bitWidth);
        if (packed.Length != expected)
        {
            CompressedThrow.Format(
                $"{Id} needs exactly {expected} packed bytes for {length} rows at offset " +
                $"{offset} and bit width {bitWidth}; the buffer holds {packed.Length}.");
        }

        int validityChildIndex = metadata.HasPatches
            ? (metadata.Patches.HasChunkOffsets ? 3 : 2)
            : 0;
        ArrayDecodeContext.RequireChildCount(
            node.ChildCount, validityChildIndex, validityChildIndex + 1, Id);

        int width = ptype.ByteWidth();
        int count = wanted.Length;
        int total = ArrayDecodeContext.CheckedMultiply(count, width, "BitPacked values");

        VortexBuffer output = VortexBuffer.Empty;
        Span<byte> destination = default;
        if (total != 0)
        {
            // Uninitialized: the gather writes every wanted value, and clears the span itself at
            // bit width 0. Patches only overwrite.
            output = CompressedValues.AllocateUninitialized(context, total, width, Id, out destination);
            Gather(packed.Span, bitWidth, offset, width, wanted, destination);
        }

        if (metadata.HasPatches)
        {
            // Applied against the whole row space and then narrowed, which is the cheap direction:
            // a patch set is small, and the alternative is a search per wanted row.
            ApplySelectedPatches(context, in node, dtype, length, in metadata, width, wanted, destination);
        }

        Validity validity = Compute.CanonicalFilter.FilterValidity(
            context.Canonical,
            context.DecodeValidity(in node, validityChildIndex, dtype.Nullability, length),
            wanted);

        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, output);
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
        BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
        int validityChildIndex = metadata.HasPatches
            ? (metadata.Patches.HasChunkOffsets ? 3 : 2)
            : 0;
        return context.ValidityDecodesRange(in node, validityChildIndex);
    }

    /// <summary>
    /// Unpacks the blocks the range touches and no other, the partial first and last ones through
    /// a scratch block; the patches are decoded whole, as they are small, and applied where they
    /// fall inside the range.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
        int elementBits = ptype.ByteWidth() * 8;

        // The metadata is checked before the packed buffer is touched.
        if (metadata.BitWidth > (uint)elementBits)
        {
            CompressedThrow.Format(
                $"{Id} bit width {metadata.BitWidth} exceeds the {elementBits} bits of " +
                $"{ptype.Name()}.");
        }

        int bitWidth = (int)metadata.BitWidth;
        int offset = (int)metadata.Offset;   // BitPackedMetadata.Read already rejects >= 1024

        VortexBuffer packed = node.GetBuffer(0);
        long blocks = ((long)length + offset + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        long expected = blocks * FastLanes.BlockByteLength(bitWidth);
        if (packed.Length != expected)
        {
            CompressedThrow.Format(
                $"{Id} needs exactly {expected} packed bytes for {length} rows at offset " +
                $"{offset} and bit width {bitWidth}; the buffer holds {packed.Length}.");
        }

        int validityChildIndex = metadata.HasPatches
            ? (metadata.Patches.HasChunkOffsets ? 3 : 2)
            : 0;
        ArrayDecodeContext.RequireChildCount(
            node.ChildCount, validityChildIndex, validityChildIndex + 1, Id);

        int width = ptype.ByteWidth();
        int total = ArrayDecodeContext.CheckedMultiply(count, width, "BitPacked values");

        VortexBuffer output = VortexBuffer.Empty;
        Span<byte> destination = default;
        if (total != 0)
        {
            // Uninitialized: Unpack writes all `total` bytes, including at bit width 0 where it
            // clears the span itself rather than inheriting a cleared one. Patches only overwrite.
            output = CompressedValues.AllocateUninitialized(context, total, width, Id, out destination);
            Unpack(packed.Span, bitWidth, offset + start, count, width, destination);
        }

        // Patches are decoded and applied before the validity child only because the validity child
        // sits after them; the order of the two operations is otherwise independent.
        if (metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, in metadata, width, destination, start, count);
        }

        bool whole = start == 0 && count == length;
        Validity validity = whole
            ? context.DecodeValidity(in node, validityChildIndex, dtype.Nullability, length)
            : context.DecodeValidityRange(in node, validityChildIndex, dtype.Nullability, length, start, count);

        return context.Canonical.AddPrimitive(dtype, count, validity, ptype, output);
    }

    /// <summary>Extracts the wanted rows one at a time.</summary>
    private static void Gather(
        ReadOnlySpan<byte> packed, int bitWidth, int offset, int width, ReadOnlySpan<int> wanted,
        Span<byte> destination)
    {
        switch (width)
        {
            case 1:
                Gather<byte>(packed, bitWidth, offset, wanted, destination);
                break;
            case 2:
                Gather<ushort>(packed, bitWidth, offset, wanted, destination);
                break;
            case 4:
                Gather<uint>(packed, bitWidth, offset, wanted, destination);
                break;
            default:
                Gather<ulong>(packed, bitWidth, offset, wanted, destination);
                break;
        }
    }

    private static void Gather<T>(
        ReadOnlySpan<byte> packedBytes, int bitWidth, int offset, ReadOnlySpan<int> wanted,
        Span<byte> destinationBytes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<T> destination = MemoryMarshal.Cast<byte, T>(destinationBytes);

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        ReadOnlySpan<T> packed = MemoryMarshal.Cast<byte, T>(packedBytes);
        int elementsPerBlock = FastLanes.BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = FastLanes.BlockSize / elementBits;
        ReadOnlySpan<int> rowOf = FastLanes.PackedRowTable(elementBits);
        ReadOnlySpan<int> laneOf = FastLanes.PackedLaneTable(elementBits);

        if (bitWidth == elementBits)
        {
            // The copy-through case the bulk kernel branches out too: the packed word at
            // (row, lane) IS the value, with no shift and no mask.
            for (int i = 0; i < wanted.Length; i++)
            {
                int encoded = wanted[i] + offset;
                int block = encoded / FastLanes.BlockSize;
                int within = encoded - (block * FastLanes.BlockSize);
                destination[i] = packed[(block * elementsPerBlock) + (lanes * rowOf[within]) + laneOf[within]];
            }

            return;
        }

        FastLanes.GatherRows(packed, bitWidth, offset, wanted, destination);
    }

    private static void Unpack(
        ReadOnlySpan<byte> packed, int bitWidth, int offset, int length, int width, Span<byte> destination)
    {
        switch (width)
        {
            case 1:
                Unpack<byte>(packed, bitWidth, offset, length, destination);
                break;
            case 2:
                Unpack<ushort>(packed, bitWidth, offset, length, destination);
                break;
            case 4:
                Unpack<uint>(packed, bitWidth, offset, length, destination);
                break;
            default:
                Unpack<ulong>(packed, bitWidth, offset, length, destination);
                break;
        }
    }

    private static void Unpack<T>(
        ReadOnlySpan<byte> packedBytes, int bitWidth, int offset, int length, Span<byte> destinationBytes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<T> destination = MemoryMarshal.Cast<byte, T>(destinationBytes);

        // A zero bit width packs nothing at all; every value is zero and the buffer is empty.
        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        // MemoryMarshal.Cast rather than VortexBuffer.Cast: upstream reinterprets the packed bytes
        // through a raw pointer and so needs natural alignment, but a .NET span read does not, and
        // rejecting an under-aligned buffer would refuse a file whose values are perfectly readable.
        ReadOnlySpan<T> packed = MemoryMarshal.Cast<byte, T>(packedBytes);
        int elementsPerBlock = FastLanes.BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();

        // The window decides which blocks are touched, and there are at most three kinds: a partial
        // first, a contiguous run of whole ones, and a partial last. The whole run is handed to the
        // kernel in one call so the per-row shape table, which depends only on the bit width and so
        // is the same for every block of the node, is built once rather than once per block.
        int end = offset + length;
        int firstBlock = offset / FastLanes.BlockSize;
        int lastBlock = (end - 1) / FastLanes.BlockSize;

        bool headPartial = offset % FastLanes.BlockSize != 0;
        bool tailPartial = end % FastLanes.BlockSize != 0;

        int firstWhole = headPartial ? firstBlock + 1 : firstBlock;
        int lastWhole = tailPartial ? lastBlock - 1 : lastBlock;

        if (headPartial || tailPartial)
        {
            T[] scratchArray = ArrayPool<T>.Shared.Rent(FastLanes.BlockSize);
            try
            {
                Span<T> scratch = scratchArray.AsSpan(0, FastLanes.BlockSize);
                if (headPartial)
                {
                    CopyPartial(packed, bitWidth, elementsPerBlock, firstBlock, offset, end, scratch, destination);
                }

                // A window partial at both ends of one and the same block was already copied whole
                // by the head: its clamp is [offset, end), which is all of it.
                if (tailPartial && !(headPartial && lastBlock == firstBlock))
                {
                    CopyPartial(packed, bitWidth, elementsPerBlock, lastBlock, offset, end, scratch, destination);
                }
            }
            finally
            {
                ArrayPool<T>.Shared.Return(scratchArray);
            }
        }

        int wholeBlocks = lastWhole - firstWhole + 1;
        if (wholeBlocks > 0)
        {
            FastLanes.UnpackBlocks(
                packed.Slice(firstWhole * elementsPerBlock, wholeBlocks * elementsPerBlock),
                bitWidth,
                destination.Slice(
                    (firstWhole * FastLanes.BlockSize) - offset, wholeBlocks * FastLanes.BlockSize),
                wholeBlocks);
        }
    }

    /// <summary>Unpacks one block into scratch and copies out only the part the window wants.</summary>
    private static void CopyPartial<T>(
        ReadOnlySpan<T> packed, int bitWidth, int elementsPerBlock, int block, int offset, int end,
        Span<T> scratch, Span<T> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int encodedStart = block * FastLanes.BlockSize;
        int from = Math.Max(encodedStart, offset);
        int to = Math.Min(encodedStart + FastLanes.BlockSize, end);
        if (to <= from)
        {
            return;
        }

        FastLanes.UnpackBlock(packed.Slice(block * elementsPerBlock, elementsPerBlock), bitWidth, scratch);
        scratch.Slice(from - encodedStart, to - from).CopyTo(destination[(from - offset)..]);
    }

    /// <summary>
    /// Applies the patches that land on a wanted row, at the position that row now occupies.
    /// </summary>
    /// <remarks>
    /// The validation is the full decode's, unchanged - a malformed patch set must be refused
    /// whether or not a take happens to skip the row it corrupts.
    /// </remarks>
    private static void ApplySelectedPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        in BitPackedMetadata metadata,
        int width,
        ReadOnlySpan<int> wanted,
        Span<byte> destination)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(
            patchesMetadata.Length, $"{Id} patch count");

        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = context.DecodeChild(in node, 0, indicesType, patchCount);
        int valuesIndex = context.DecodeChild(in node, 1, dtype, patchCount);

        if (patchesMetadata.HasChunkOffsets)
        {
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            int chunkOffsets = context.DecodeChild(
                in node, 2,
                context.Types.Primitive(patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable),
                chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, patchesMetadata.ChunkOffsetsPType, chunkOffsetsLength, Id,
                "patch_chunk_offsets");
        }

        bool walked = context.IsNodeChecked(in node);
        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // A span is read below, so a constant child is expanded into one rather than refused.
        CanonicalNode values = context.Canonical.GetNode(
            CanonicalSupport.ExpandIfConstant(context, valuesIndex));
        if (values.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "patch_values", values.Kind, "a Primitive");
        }

        if (values.PType != dtype.PType)
        {
            CompressedThrow.Format(
                $"{Id}'s patch values decoded as {values.PType.Name()}; " +
                $"{dtype.PType.Name()} was required.");
        }

        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        Patches.ApplySelected(in patches, values.Values.Span, width, wanted, destination);
    }

    private static void ApplyPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        in BitPackedMetadata metadata,
        int width,
        Span<byte> destination,
        int start,
        int count)
    {
        PatchesMetadata patchesMetadata = metadata.Patches;
        int patchCount = ArrayDecodeContext.CheckedLength(
            patchesMetadata.Length, $"{Id} patch count");

        // A range reads the whole patch set, decoded once for every range of the node.
        bool whole = start == 0 && count == length;
        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = whole
            ? context.DecodeChild(in node, 0, indicesType, patchCount)
            : context.DecodeWholeChild(in node, 0, indicesType, patchCount);
        int valuesIndex = whole
            ? context.DecodeChild(in node, 1, dtype, patchCount)
            : context.DecodeWholeChild(in node, 1, dtype, patchCount);

        if (patchesMetadata.HasChunkOffsets)
        {
            // Read, validated and then deliberately unused: the range's first patch is found by a
            // binary search over the indices, which the per-chunk offsets would only shorten.
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(
                patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = whole
                ? context.DecodeChild(in node, 2, chunkOffsetsType, chunkOffsetsLength)
                : context.DecodeWholeChild(in node, 2, chunkOffsetsType, chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, patchesMetadata.ChunkOffsetsPType, chunkOffsetsLength, Id,
                "patch_chunk_offsets");
        }

        bool walked = context.IsNodeChecked(in node);
        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // A span is read below, so a constant child is expanded into one rather than refused.
        CanonicalNode values = context.Canonical.GetNode(
            CanonicalSupport.ExpandIfConstant(context, valuesIndex));
        if (values.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "patch_values", values.Kind, "a Primitive");
        }

        if (values.PType != dtype.PType)
        {
            CompressedThrow.Format(
                $"{Id}'s patch values decoded as {values.PType.Name()}; " +
                $"{dtype.PType.Name()} was required.");
        }

        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        ReadOnlySpan<byte> source = values.Values.Span;
        if (whole)
        {
            patches.ApplyAll(source, width, destination);
            return;
        }

        Patches.ApplyRange(in patches, source, width, start, count, destination);
    }
}
