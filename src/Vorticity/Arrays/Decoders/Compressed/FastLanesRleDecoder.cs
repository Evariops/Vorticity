// fastlanes.rle - vortex-fastlanes-0.86.1/src/rle/vtable/mod.rs (`validate_parts`) and
// src/rle/array/rle_decompress.rs, over fastlanes-0.7.2/src/rle.rs.
//
// Despite the crate it comes from, the RLE gather is NOT transposed: `RLE::decode_unchecked` is a
// straight `output[i] = rle_vals[rle_idxs[i]]`. What the FastLanes block size buys is the chunking:
// the indices are padded to a multiple of 1024 and one absolute value-index offset is stored per
// 1024-element chunk, so a chunk's dictionary is `values[offsets[c] - offsets[0] ..]`.
//
// Exactly three children, no buffers. The nullability lives on the INDICES child, not on the node,
// and the indices child is `indices_len` long while the array is `len` long starting at `offset` -
// so the decoded validity has to be windowed, not used as is.
//
// Upstream tolerates a garbage index at a null position (it can be anything after the indices are
// further compressed) and fills such a row with the chunk's first value; it also fills a
// single-value chunk without looking at the indices at all. Both behaviours are reproduced here,
// because a file the reference reads must not fail for us.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>fastlanes.rle</c> by gathering each chunk's values through its indices.</summary>
public sealed class FastLanesRleDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.rle";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly FastLanesRleDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.rle"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesRle;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 3, Id);

        RleMetadata metadata = RleMetadata.Read(node.Metadata);
        PType ptype = RequirePrimitive(dtype);

        // "RLE indices must be u8 or u16" - the whole domain, because the kernel's index type is
        // u16 and a u8 index only appears after the indices are themselves downcast.
        if (metadata.IndicesPType is not (PType.U8 or PType.U16))
        {
            CompressedThrow.Format(
                $"{Id} indices must be u8 or u16, not {metadata.IndicesPType.Name()}.");
        }

        if (!metadata.ValuesIdxOffsetsPType.IsUnsignedInteger())
        {
            CompressedThrow.Format(
                $"{Id} value index offsets must be a non-nullable unsigned integer, not " +
                $"{metadata.ValuesIdxOffsetsPType.Name()}.");
        }

        int valuesLength = ArrayDecodeContext.CheckedLength(metadata.ValuesLength, $"{Id} values_len");
        int indicesLength = ArrayDecodeContext.CheckedLength(metadata.IndicesLength, $"{Id} indices_len");
        int offsetsLength = ArrayDecodeContext.CheckedLength(
            metadata.ValuesIdxOffsetsLength, $"{Id} values_idx_offsets_len");
        int offset = ArrayDecodeContext.CheckedLength(metadata.Offset, $"{Id} offset");

        if (indicesLength % FastLanes.BlockSize != 0)
        {
            CompressedThrow.Format(
                $"{Id} indices length must be a multiple of {FastLanes.BlockSize}, got {indicesLength}.");
        }

        if ((long)offset + length > indicesLength)
        {
            CompressedThrow.Format(
                $"{Id} offset + length, {offset} + {length}, exceeds the indices length {indicesLength}.");
        }

        int chunks = indicesLength / FastLanes.BlockSize;
        if (chunks != offsetsLength)
        {
            CompressedThrow.Format(
                $"{Id} needs one value index offset per chunk: {chunks} chunks against " +
                $"{offsetsLength} offsets.");
        }

        if (indicesLength < valuesLength)
        {
            CompressedThrow.Format(
                $"{Id} must have at least as many indices as values: {indicesLength} against " +
                $"{valuesLength}.");
        }

        DType valuesType = context.Types.Primitive(ptype, Nullability.NonNullable);
        DType indicesType = context.Types.Primitive(metadata.IndicesPType, dtype.Nullability);
        DType offsetsType = context.Types.Primitive(
            metadata.ValuesIdxOffsetsPType, Nullability.NonNullable);

        int valuesIndex = context.DecodeChild(in node, 0, valuesType, valuesLength);
        int indicesIndex = context.DecodeChild(in node, 1, indicesType, indicesLength);
        int offsetsIndex = context.DecodeChild(in node, 2, offsetsType, offsetsLength);

        ReadOnlySpan<byte> values = CompressedValues.RequireIndexChild(
            context, valuesIndex, ptype, valuesLength, Id, "values");
        ReadOnlySpan<byte> offsets = CompressedValues.RequireIndexChild(
            context, offsetsIndex, metadata.ValuesIdxOffsetsPType, offsetsLength, Id,
            "values_idx_offsets");

        CanonicalNode indicesNode = context.Canonical.GetNode(indicesIndex);
        if (indicesNode.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "indices", indicesNode.Kind, "a Primitive");
        }

        if (indicesNode.PType != metadata.IndicesPType)
        {
            CompressedThrow.Format(
                $"{Id}'s indices child decoded as {indicesNode.PType.Name()}; " +
                $"{metadata.IndicesPType.Name()} was declared.");
        }

        if (indicesNode.Length != indicesLength)
        {
            CompressedThrow.ChildLength(Id, "indices", indicesNode.Length, indicesLength);
        }

        ValidateOffsets(offsets, metadata.ValuesIdxOffsetsPType, offsetsLength, valuesLength);

        Validity validity = CompressedValues.SliceValidity(
            context, indicesNode.Validity, offset, length, dtype);
        ValidityReader indicesValidity = ValidityReader.Of(context.Canonical, indicesNode.Validity);

        int width = ptype.ByteWidth();
        int indexWidth = metadata.IndicesPType.ByteWidth();
        int total = ArrayDecodeContext.CheckedMultiply(length, width, "RLE values");
        if (total == 0)
        {
            return context.Canonical.AddPrimitive(dtype, length, validity, ptype, VortexBuffer.Empty);
        }

        // Uninitialized: every row of the output is written below, either by the chunk gather or
        // by the single-value fill.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, width, Id, out Span<byte> destination);
        ReadOnlySpan<byte> indices = indicesNode.Values.Span;

        ulong firstOffset = CompressedValues.ReadUnsigned(offsets, metadata.ValuesIdxOffsetsPType, 0);

        // BY CHUNK, NOT BY ROW. The chunk boundary is fixed by the FastLanes block size, so which
        // chunk a row belongs to is a property of the loop, not a question to ask per row: the
        // division, the offsets lookup through `switch (ptype)` and the compare against the
        // previous chunk were all paid a million times to answer it 977 times. Inside a chunk the
        // gather is `RowKernels`', with both physical types resolved before it starts.
        int row = 0;
        while (row < length)
        {
            int encoded = offset + row;
            int chunk = encoded / FastLanes.BlockSize;
            int chunkBase = (int)(CompressedValues.ReadUnsigned(
                offsets, metadata.ValuesIdxOffsetsPType, chunk) - firstOffset);
            int chunkEnd = chunk + 1 < offsetsLength
                ? (int)(CompressedValues.ReadUnsigned(
                    offsets, metadata.ValuesIdxOffsetsPType, chunk + 1) - firstOffset)
                : valuesLength;
            int chunkValueCount = chunkEnd - chunkBase;
            if (chunkValueCount <= 0)
            {
                CompressedThrow.Format($"{Id} chunk {chunk} references no values.");
            }

            // Rows of this chunk that are also rows of the window.
            int chunkRows = Math.Min(
                ((chunk + 1) * FastLanes.BlockSize) - encoded, length - row);

            if (chunkValueCount == 1)
            {
                // "fills a single-value chunk without looking at the indices at all" -- upstream's
                // behaviour, and now one tiled write instead of `chunkRows` copies.
                RowKernels.TileRow(width, values, chunkBase, destination, row, chunkRows);
            }
            else
            {
                int bad = indicesValidity.IsAllValid
                    ? RowKernels.Gather(
                        indices[(encoded * indexWidth)..], metadata.IndicesPType,
                        values[(chunkBase * width)..], width, chunkValueCount,
                        destination[(row * width)..], chunkRows)
                    : RowKernels.GatherMasked(
                        indices[(encoded * indexWidth)..], metadata.IndicesPType,
                        values[(chunkBase * width)..], width, chunkValueCount,
                        destination[(row * width)..], chunkRows,
                        indicesValidity.Bits, indicesValidity.BitOffset + encoded,
                        default, 0, true, default);
                if (bad >= 0)
                {
                    ThrowIndex(indices, metadata.IndicesPType, encoded + bad, row + bad, chunk,
                        chunkValueCount);
                }

                // A null index selects nothing, and upstream fills such a row with the chunk's
                // FIRST value rather than leaving it undefined. The masked gather zeroes it, so
                // the zeroed rows are re-filled here; scanning the mask a second time costs a word
                // per 64 rows, where testing validity inside the gather costs a branch per row.
                if (!indicesValidity.IsAllValid)
                {
                    FillNulls(
                        in indicesValidity, encoded, row, chunkRows, width, values, chunkBase,
                        destination);
                }
            }

            row += chunkRows;
        }

        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
    }

    /// <summary>Re-fills the rows a null index left zeroed with the chunk's first value.</summary>
    private static void FillNulls(
        in ValidityReader indicesValidity, int encoded, int row, int chunkRows, int width,
        ReadOnlySpan<byte> values, int chunkBase, Span<byte> destination)
    {
        for (int i = 0; i < chunkRows; i++)
        {
            if (!indicesValidity.IsValid(encoded + i))
            {
                values.Slice(chunkBase * width, width)
                    .CopyTo(destination.Slice((row + i) * width, width));
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowIndex(
        ReadOnlySpan<byte> indices, PType indicesPType, int encoded, int row, int chunk,
        int chunkValueCount) =>
        CompressedThrow.Format(
            $"{Id} index {RowKernels.CodeAt(indices, indicesPType, encoded)} at row {row} is out " +
            $"of bounds for chunk {chunk}, which holds {chunkValueCount} values.");

    private static PType RequirePrimitive(DType dtype)
    {
        if (dtype.Kind != DTypeKind.Primitive)
        {
            CompressedThrow.Format($"{Id} requires a primitive dtype, not {dtype}.");
        }

        return dtype.PType;
    }

    // "RLE values_idx_offsets must be non-decreasing" and
    // "RLE values_idx_offsets span N values but only M are present": both are checked upstream in
    // rle_decompress before any gather, and both keep the per-chunk slicing inside `values`.
    private static void ValidateOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, int offsetsLength, int valuesLength)
    {
        if (offsetsLength == 0)
        {
            return;
        }

        ulong first = CompressedValues.ReadUnsigned(offsets, ptype, 0);
        ulong previous = first;
        for (int i = 1; i < offsetsLength; i++)
        {
            ulong value = CompressedValues.ReadUnsigned(offsets, ptype, i);
            if (value < previous)
            {
                CompressedThrow.Format(
                    $"{Id} value index offsets must be non-decreasing; {value} follows {previous}.");
            }

            previous = value;
        }

        if (previous - first > (ulong)(uint)valuesLength)
        {
            CompressedThrow.Format(
                $"{Id} value index offsets span {previous - first} values but only {valuesLength} " +
                "are present.");
        }
    }
}
