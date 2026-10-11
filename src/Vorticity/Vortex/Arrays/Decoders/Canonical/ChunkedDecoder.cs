using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.chunked</c>: the concatenation of its chunks, canonicalized.</summary>
/// <remarks>
/// <para>
/// Child 0 is a <c>u64</c> array of one more offset than there are chunks, and it has to be
/// materialized and validated before any chunk is decoded, since the chunks' lengths are the
/// differences between its entries. A zero-length chunk is legal, wherever it sits.
/// </para>
/// <para>
/// A range or a selection decodes the chunks it meets and no other, and a range inside one chunk is
/// that chunk's range as it decodes it: a view onto the segment for a chunk stored plain, where the
/// whole array would have been copied end to end to be cut again. Only a range that crosses a
/// boundary is concatenated, and only its rows.
/// </para>
/// </remarks>
internal sealed class ChunkedDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.chunked";

    private const int StackChunks = 32;

    /// <summary>The wanted rows a selection renumbers on the stack: a take's batch, past which the pool lends.</summary>
    private const int StackRows = 128;

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
        ReadOnlySpan<byte> offsets = Offsets(context, in node, length, out int chunkCount);

        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunkCount, stack);
        try
        {
            Span<int> chunks = scratch.Span;
            long start = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                long end = ReadOffset(offsets, i + 1);
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

    /// <summary>Every chunk decodes a range: a range then decodes the chunks it meets and no other.</summary>
    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (int i = 1; i < node.ChildCount; i++)
        {
            if (!context.ChildDecodesRange(in node, i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One chunk at most, itself a view: several are concatenated into a buffer of their own,
    /// whatever each of them is.
    /// </summary>
    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount <= 1 ||
            (node.ChildCount == 2 && context.ChildMaterializesNothing(in node, 1, dtype));
    }

    /// <summary>
    /// The chunks the range meets, each its part of the range, put end to end only when there is
    /// more than one: a range inside a chunk is the chunk's own range.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySpan<byte> offsets = Offsets(context, in node, length, out int chunkCount);

        long end = (long)start + count;
        int first = ChunkOf(offsets, chunkCount, start);
        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunkCount - first, stack);
        try
        {
            Span<int> parts = scratch.Span;
            int made = 0;
            for (int i = first; i < chunkCount; i++)
            {
                long chunkStart = ReadOffset(offsets, i);
                if (chunkStart >= end)
                {
                    break;
                }

                int chunkLength = (int)(ReadOffset(offsets, i + 1) - chunkStart);
                if (chunkLength == 0)
                {
                    continue;
                }

                int from = (int)Math.Max(start - chunkStart, 0);
                int to = (int)Math.Min(end - chunkStart, chunkLength);
                parts[made++] = from == 0 && to == chunkLength
                    ? context.DecodeChild(in node, i + 1, dtype, chunkLength)
                    : context.DecodeChildRange(in node, i + 1, dtype, chunkLength, from, to - from);
            }

            return made == 1 ? parts[0] : CanonicalConcat.Concat(context, dtype, count, parts[..made]);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// Every chunk selects: a chunk that does not is decoded whole once by the reader's retained
    /// chunk rather than once by every batch of a take.
    /// </summary>
    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (int i = 1; i < node.ChildCount; i++)
        {
            if (!context.ChildSelectsWithoutFullDecode(in node, i, dtype))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// What a row costs is its chunk's to say, and a chunk selecting by frame would make a
    /// dictionary over these values pay a frame for each of its entries.
    /// </remarks>
    public override bool SelectsByRow => false;

    /// <summary>
    /// The wanted rows of each chunk they fall in, selected by that chunk, put end to end: the chunks
    /// no row falls in are not decoded.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySpan<byte> offsets = Offsets(context, in node, length, out int chunkCount);
        if (wanted.IsEmpty)
        {
            return CanonicalConcat.Concat(context, dtype, 0, []);
        }

        Span<int> partStack = stackalloc int[StackChunks];
        Span<int> localStack = stackalloc int[StackRows];
        Scratch<int> partScratch = new Scratch<int>(Math.Min(chunkCount, wanted.Length), partStack);
        Scratch<int> localScratch = new Scratch<int>(wanted.Length, localStack);
        try
        {
            Span<int> parts = partScratch.Span;
            Span<int> local = localScratch.Span;
            int made = 0;
            int w = 0;
            int chunk = ChunkOf(offsets, chunkCount, wanted[0]);
            while (w < wanted.Length)
            {
                // The wanted rows ascend, so the next one's chunk is at or after this one's; a gap of
                // many chunks is crossed by halving rather than chunk by chunk.
                long chunkEnd = ReadOffset(offsets, chunk + 1);
                if (wanted[w] >= chunkEnd)
                {
                    chunk = ChunkOf(offsets, chunkCount, wanted[w]);
                    chunkEnd = ReadOffset(offsets, chunk + 1);
                }

                long chunkStart = ReadOffset(offsets, chunk);
                int first = w;
                while (w < wanted.Length && wanted[w] < chunkEnd)
                {
                    local[w] = (int)(wanted[w] - chunkStart);
                    w++;
                }

                parts[made++] = context.DecodeChildSelected(
                    in node, chunk + 1, dtype, (int)(chunkEnd - chunkStart), local[first..w]);
            }

            return made == 1 ? parts[0] : CanonicalConcat.Concat(context, dtype, wanted.Length, parts[..made]);
        }
        finally
        {
            localScratch.Dispose();
            partScratch.Dispose();
        }
    }

    /// <summary>
    /// The chunk offsets, decoded and validated whole before a single chunk is: offsets that end far
    /// past the declared row count would otherwise have an enormous chunk decoded from them.
    /// </summary>
    /// <param name="context">The decode context.</param>
    /// <param name="node">The chunked node.</param>
    /// <param name="length">The rows the node declares.</param>
    /// <param name="chunkCount">The number of chunks.</param>
    /// <returns>The offsets' bytes, one more <c>u64</c> than there are chunks.</returns>
    private static ReadOnlySpan<byte> Offsets(
        ArrayDecodeContext context, in ArrayNode node, int length, out int chunkCount)
    {
        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);

        int childCount = node.ChildCount;
        if (childCount < 1)
        {
            throw new VortexFormatException("Chunked array needs at least one child.");
        }

        chunkCount = childCount - 1;
        int offsetCount = chunkCount + 1;

        DType offsetsDType = context.Types.Primitive(PType.U64, Nullability.NonNullable);
        int offsetsIndex = context.DecodeChild(in node, 0, offsetsDType, offsetCount);
        CanonicalNode offsetsNode = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, PType.U64, offsetCount, Id + " chunk_offsets");
        ReadOnlySpan<byte> offsets = offsetsNode.Values.Span;

        long previous = ReadOffset(offsets, 0);
        if (previous != 0)
        {
            throw new VortexFormatException(
                $"{Id} chunk_offsets must start at 0; this array starts at {previous}.");
        }

        for (int i = 0; i < chunkCount; i++)
        {
            long next = ReadOffset(offsets, i + 1);
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

        return offsets;
    }

    /// <summary>The first chunk that ends past <paramref name="row"/>, by halving the validated offsets.</summary>
    private static int ChunkOf(ReadOnlySpan<byte> offsets, int chunkCount, long row)
    {
        int low = 0;
        int high = chunkCount;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (ReadOffset(offsets, middle + 1) <= row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
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
