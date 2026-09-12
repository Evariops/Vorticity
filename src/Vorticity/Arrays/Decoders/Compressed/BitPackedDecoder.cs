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
            output = CompressedValues.Allocate(context, total, width, Id, out destination);
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
        int blocks = packed.Length / elementsPerBlock;

        T[] scratchArray = ArrayPool<T>.Shared.Rent(FastLanes.BlockSize);
        try
        {
            Span<T> scratch = scratchArray.AsSpan(0, FastLanes.BlockSize);
            for (int block = 0; block < blocks; block++)
            {
                ReadOnlySpan<T> source = packed.Slice(block * elementsPerBlock, elementsPerBlock);
                int encodedStart = block * FastLanes.BlockSize;
                int encodedEnd = encodedStart + FastLanes.BlockSize;

                if (encodedStart >= offset && encodedEnd <= offset + length)
                {
                    // The whole block lands inside the output: unpack straight into it.
                    FastLanes.UnpackBlock(
                        source, bitWidth, destination.Slice(encodedStart - offset, FastLanes.BlockSize));
                    continue;
                }

                int from = Math.Max(encodedStart, offset);
                int to = Math.Min(encodedEnd, offset + length);
                if (to <= from)
                {
                    continue;
                }

                FastLanes.UnpackBlock(source, bitWidth, scratch);
                scratch.Slice(from - encodedStart, to - from).CopyTo(destination[(from - offset)..]);
            }
        }
        finally
        {
            ArrayPool<T>.Shared.Return(scratchArray);
        }
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

        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id);

        CanonicalNode values = context.Canonical.GetNode(valuesIndex);
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
