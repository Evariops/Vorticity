// fastlanes.bitpacked - vortex-fastlanes-0.86.1/src/bitpacking/vtable/mod.rs (`deserialize`),
// src/bitpacking/array/mod.rs (`validate`) and src/bitpacking/array/bitpack_decompress.rs.
//
// The child layout is decided by the METADATA, never by the child count (contract §10.2):
//
//     patches absent            -> validity child 0     shapes: [V?]
//     patches, no chunk offsets -> validity child 2     shapes: [idx, val, V?]
//     patches, chunk offsets    -> validity child 3     shapes: [idx, val, chunks, V?]
//
// Three children is ambiguous between "patches + validity" and "patches + chunk offsets, no
// validity", and only `chunk_offsets_ptype`'s presence resolves it. The corpus exercises all five
// counts: c0 x137, c1 x55, c2 x1, c3 x40, c4 x43.
//
// The packed-length check is what makes the unpack kernel safe: with
// `packed.len() == ceil((len + offset) / 1024) * 128 * bit_width` every block the loop touches is
// fully present, so the kernel needs no per-element bounds check.
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
public sealed class BitPackedDecoder : ArrayDecoder
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
    /// The `fastlanes.bitpacked` row of the take table: "O(1) positional access via the inverse
    /// transposition". The table also describes a density threshold - decode a whole 1024-block
    /// once enough of it is wanted, per element below that - and it is deliberately NOT implemented
    /// here, because the crossover is a measurement nobody has made. Per-element is strictly better
    /// than today's behaviour at every density up to "all of it", and at "all of it" the scan does
    /// not take this path at all: `ExecuteWithTake` skips the pushdown when every row of the split
    /// is wanted.
    ///
    /// PATCHES ARE STILL DECODED IN FULL. There are few of them by construction - a patch that
    /// paid for itself is rare in the column - and they arrive as their own child arrays, so
    /// selecting among them would mean pushing a second, differently-based selection into them.
    /// The gain would be a fraction of a fraction.
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
            output = CompressedValues.Allocate(context, total, width, Id, out destination);
            Gather(packed.Span, bitWidth, offset, width, wanted, destination);
        }

        if (metadata.HasPatches)
        {
            // Applied against the FULL row space and then narrowed, which is the cheap direction:
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

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);

        BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
        int elementBits = ptype.ByteWidth() * 8;

        // Class I, all before the packed buffer is touched.
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
        int total = ArrayDecodeContext.CheckedMultiply(length, width, "BitPacked values");

        VortexBuffer output = VortexBuffer.Empty;
        Span<byte> destination = default;
        if (total != 0)
        {
            // UNINITIALIZED: Unpack writes all `total` bytes, including at bit width 0 where it
            // clears the span itself rather than inheriting a cleared one. Patches only overwrite.
            output = CompressedValues.AllocateUninitialized(context, total, width, Id, out destination);
            Unpack(packed.Span, bitWidth, offset, length, width, destination);
        }

        // Patches are decoded and applied BEFORE the validity child, only because the validity
        // child sits after them; the order of the two operations is otherwise independent.
        if (metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, in metadata, width, destination);
        }

        Validity validity = context.DecodeValidity(
            in node, validityChildIndex, dtype.Nullability, length);

        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
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

        for (int i = 0; i < wanted.Length; i++)
        {
            int encoded = wanted[i] + offset;
            int block = encoded / FastLanes.BlockSize;
            int within = encoded - (block * FastLanes.BlockSize);
            ReadOnlySpan<T> source = packed.Slice(block * elementsPerBlock, elementsPerBlock);

            // W == T is the copy-through case the bulk kernel branches out too: the packed word at
            // (row, lane) IS the value, with no shift and no mask.
            destination[i] = bitWidth == elementBits
                ? source[(lanes * FastLanes.PackedRowTable(elementBits)[within])
                    + FastLanes.PackedLaneTable(elementBits)[within]]
                : FastLanes.UnpackOne(source, bitWidth, within);
        }
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

        // THE WINDOW DECIDES WHICH BLOCKS ARE TOUCHED, and there are at most three kinds: a
        // partial first, a contiguous run of whole ones, and a partial last. Blocks before the
        // window were being visited only to be `continue`d, and the whole run was being unpacked a
        // block at a time -- which made `UnpackBlock` rebuild its per-row shape table once per
        // block, for a bit width that is the same across every block of the node.
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

                // A window that is partial at both ends of the SAME block was already copied
                // whole by the head: its clamp is [offset, end), which is all of it.
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

        // A span is read below, so a constant child is expanded: see
        // `CanonicalSupport.RequirePrimitiveChild`.
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
            // Read, validated and then deliberately unused: Phase 1 never slices patches, so
            // `offset_within_chunk` and the per-chunk index offsets have nothing to accelerate.
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(
                patchesMetadata.ChunkOffsetsLength, $"{Id} patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(
                patchesMetadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = context.DecodeChild(in node, 2, chunkOffsetsType, chunkOffsetsLength);
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

        // A span is read below, so a constant child is expanded: see
        // `CanonicalSupport.RequirePrimitiveChild`.
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

        // `assert!(values.all_valid(ctx)?, "Patch values must be all valid")`.
        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{Id} patch values must not contain nulls.");
        }

        ReadOnlySpan<byte> source = values.Values.Span;
        for (int i = 0; i < patches.Count; i++)
        {
            int position = patches.GetPosition(i);
            source.Slice(i * width, width).CopyTo(destination.Slice(position * width, width));
        }
    }
}
