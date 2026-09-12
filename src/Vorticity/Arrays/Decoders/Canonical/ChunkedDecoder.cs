// vortex.chunked - vortex-array-0.86.1/src/arrays/chunked/vtable/mod.rs `deserialize`, then the
// encoding's `execute`, which canonicalizes by concatenating.
//
// Child 0 is a u64 chunk-offsets array of `nchunks + 1` entries and must be materialized BEFORE any
// chunk can be decoded, because the chunks' lengths come out of it. Upstream then computes
// `end - start` with an unchecked usize subtraction, so a non-monotone offsets array underflows
// there into an enormous chunk length; every entry is therefore range-checked and the sequence
// checked for monotonicity first (Phase 1 contract §9.3 step 3).
//
// A zero-length chunk is legal and appears in the corpus (encodings/chunked_empty_chunks: zero-row
// chunks first, in the middle and last).
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.chunked</c>: the concatenation of its chunks, canonicalized.</summary>
public sealed class ChunkedDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.chunked";

    private const int StackChunks = 32;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ChunkedDecoder Instance = new ChunkedDecoder();

    private ChunkedDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.chunked"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Chunked;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);

        int childCount = node.ChildCount;
        if (childCount < 1)
        {
            throw new VortexFormatException("Chunked array needs at least one child.");
        }

        int chunkCount = childCount - 1;
        int offsetCount = chunkCount + 1;

        DType offsetsDType = context.Types.Primitive(PType.U64, Nullability.NonNullable);
        int offsetsIndex = context.DecodeChild(in node, 0, offsetsDType, offsetCount);
        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, PType.U64, offsetCount, Id + " chunk_offsets");

        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunkCount, stack);
        try
        {
            Span<int> chunks = scratch.Span;
            ReadOnlySpan<byte> offsetBytes = offsets.Values.Span;

            // The WHOLE offsets array is validated before a single chunk is decoded. Checking the
            // last offset afterwards would be too late: `chunk_offsets = [0, 2^31 - 1]` on a
            // two-row array would already have decoded a two-billion-row chunk before the mismatch
            // was noticed.
            long previous = ReadOffset(offsetBytes, 0);
            if (previous != 0)
            {
                throw new VortexFormatException(
                    $"{Id} chunk_offsets must start at 0; this array starts at {previous}.");
            }

            for (int i = 0; i < chunkCount; i++)
            {
                long next = ReadOffset(offsetBytes, i + 1);
                if (next < previous)
                {
                    throw new VortexFormatException(
                        $"{Id} chunk_offsets must not decrease; offset {i + 1} is {next} after {previous}.");
                }

                previous = next;
            }

            if (previous != length)
            {
                throw new VortexFormatException(
                    $"{Id} chunk_offsets end at {previous} but the array declares {length} rows.");
            }

            long start = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                long end = ReadOffset(offsetBytes, i + 1);
                chunks[i] = context.DecodeChild(in node, i + 1, dtype, (int)(end - start));
                start = end;
            }

            return CanonicalConcat.Concat(context, dtype, length, chunks);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Reads one <c>u64</c> chunk offset and narrows it. A value above <see cref="int.MaxValue"/>
    /// is rejected here rather than at the subtraction, where it would have become a chunk length.
    /// </summary>
    private static long ReadOffset(ReadOnlySpan<byte> offsets, int index)
    {
        ulong raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
            offsets.Slice(index * 8, 8));
        return ArrayDecodeContext.CheckedLength(raw, Id + " chunk offset");
    }
}
