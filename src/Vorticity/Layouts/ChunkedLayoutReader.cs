using System;
using System.Buffers;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a <c>vortex.chunked</c> layout by concatenating the chunks a range touches. The layout
/// owns no segment of its own: every child is one chunk, in row order, and a layout with no child
/// at all is legal and then covers zero rows. Chunk offsets are derived rather than stored, and the
/// parser has already checked that they sum to the parent's row count.
/// </summary>
internal sealed class ChunkedLayoutReader : LayoutReader
{
    private const int StackChunks = 16;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ChunkedLayoutReader Instance = new ChunkedLayoutReader();

    private ChunkedLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Chunked;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.chunked"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        ReadOnlySpan<long> offsets = node.ChunkOffsets;
        ChunkRange(offsets, rows, out int first, out int last);

        for (int i = first; i < last; i++)
        {
            RowRange local = LocalRange(offsets, rows, i);
            if (local.IsEmpty)
            {
                continue;
            }

            LayoutNode chunk = node.GetChild(i);
            RegisterChild(in chunk, local, in fields, segments);
        }
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        int length = context.HasSelection ? context.Selection.Length : BatchLength(rows);
        ReadOnlySpan<long> offsets = node.ChunkOffsets;
        ChunkRange(offsets, rows, out int first, out int last);

        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(Math.Max(last - first, 0), stack);

        // The mask of live blocks is in file coordinates and the children are not, so it is cleared
        // for them here; what it means for a chunk travels in the selection, the one currency that
        // is re-based per child. A chunk holding dead blocks goes through the selection path --
        // this batch's rows and nothing else -- rather than being decoded whole and retained, which
        // would decode every block of the chunk for the few that live. A chunk with no dead block
        // is decoded whole and retained.
        Compute.BlockMask? live = context.LiveBlocks;
        context.LiveBlocks = null;
        try
        {
            Span<int> chunks = scratch.Span;
            int count = 0;
            for (int i = first; i < last; i++)
            {
                RowRange local = LocalRange(offsets, rows, i);
                if (local.IsEmpty)
                {
                    continue;
                }

                LayoutNode chunk = node.GetChild(i);
                if (context.HasSelection)
                {
                    chunks[count++] = ExecuteChunkSelected(in chunk, local, in fields, context, offsets[i]);
                }
                else if (live is not null
                    && local.Length < chunk.RowCount
                    && live.HasDeadBlocks(new RowRange(offsets[i], offsets[i + 1])))
                {
                    chunks[count++] = ExecuteChunkLive(in chunk, local, in fields, context);
                }
                else
                {
                    chunks[count++] = ExecuteRowChild(in chunk, local, in fields, context);
                }
            }

            if (count == 0)
            {
                // No chunk intersects: an empty range, or a chunked layout with no children at all.
                DType empty = node.DType;
                int zero = CanonicalFill.BuildZeroed(
                    context.Decode, empty, 0, Validity.FromNullability(empty.Nullability));
                return MaskProjection.Apply(context.Decode, zero, in fields);
            }

            if (count == 1)
            {
                return chunks[0];
            }

            // Every chunk was decoded at the same dtype, so the concatenated dtype is any of
            // theirs - and it is the projected one when a mask narrowed the children.
            DType dtype = context.Canonical.GetNode(chunks[0]).DType;
            return CanonicalConcat.Concat(context.Decode, dtype, length, chunks[..count]);
        }
        finally
        {
            context.LiveBlocks = live;
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Executes one chunk that has dead blocks in it, through the selection path: this batch's
    /// rows, as a chunk-local selection, and nothing else of the chunk is decoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows are contiguous, so the selection is a counted range written out row by row; teaching
    /// the selection to carry <c>[start, start + length)</c> instead would buy nothing measurable.
    /// The buffer is rented from the shared pool, so this allocates nothing once the pool is warm,
    /// and the loop is bounded by one block because the splits are cut at the mask's block
    /// boundaries.
    /// </para>
    /// <para>
    /// An encoding that <c>SelectsWithoutFullDecode</c> then materializes these rows alone; one that
    /// does not decodes the chunk once, retains it, and gathers this range out of it. Against the
    /// whole-chunk path the trade runs both ways: that path decodes the chunk again for every batch
    /// touching it, where this decodes it once, but it then copies rows the whole-chunk path would
    /// merely have sliced.
    /// </para>
    /// </remarks>
    private static int ExecuteChunkLive(
        in LayoutNode chunk, RowRange local, in FieldMask fields, ScanContext context)
    {
        int length = BatchLength(local);
        int[] range = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        try
        {
            int start = (int)local.Start;
            for (int i = 0; i < length; i++)
            {
                range[i] = start + i;
            }

            (int[]? Buffer, int Count) saved = context.ExchangeSelection(range, length);
            try
            {
                return ExecuteRowChild(in chunk, local, in fields, context);
            }
            finally
            {
                context.ExchangeSelection(saved.Buffer, saved.Count);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(range);
        }
    }

    /// <summary>
    /// Executes one chunk with the selection re-based into that chunk's own row space.
    /// </summary>
    /// <remarks>
    /// The only reader that has to do this, and the reason the selection is defined to live in its
    /// sibling `rows` argument's coordinate space: struct, zoned and stats pass `rows` through
    /// untouched and therefore pass the selection through by doing nothing, while this one
    /// re-partitions and so has to re-partition both. A chunk that ends up wanting no rows still
    /// runs - it produces an empty node, which concatenates to nothing.
    ///
    /// The pass over the whole selection looks like a cost per chunk and is not one: a chunk
    /// boundary is a split boundary, because the split walk recurses into every touched chunk and
    /// pushes its end, so the caller's loop runs this once per split and the selection it walks is
    /// the split's own. Replacing the pass with two binary searches would therefore save nothing.
    /// </remarks>
    private static int ExecuteChunkSelected(
        in LayoutNode chunk, RowRange local, in FieldMask fields, ScanContext context, long start)
    {
        ReadOnlySpan<int> selection = context.Selection;
        long end = start + chunk.RowCount;

        int[] rebased = ArrayPool<int>.Shared.Rent(Math.Max(selection.Length, 1));
        try
        {
            int count = 0;
            for (int i = 0; i < selection.Length; i++)
            {
                long row = selection[i];
                if (row >= start && row < end)
                {
                    rebased[count++] = (int)(row - start);
                }
            }

            (int[]? Buffer, int Count) saved = context.ExchangeSelection(rebased, count);
            try
            {
                return ExecuteRowChild(in chunk, local, in fields, context);
            }
            finally
            {
                context.ExchangeSelection(saved.Buffer, saved.Count);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rebased);
        }
    }

    /// <summary>
    /// The half-open chunk range a row range touches.
    /// </summary>
    /// <remarks>
    /// The selection is upstream's: a miss on the range start falls back to the insertion point
    /// minus one, a miss on the range end to the insertion point itself. .NET's
    /// <c>BinarySearch</c> returns <c>~insertionPoint</c> where Rust returns
    /// <c>Err(insertion_point)</c>, so the two agree once the complement is undone.
    /// </remarks>
    internal static void ChunkRange(ReadOnlySpan<long> offsets, RowRange rows, out int first, out int last)
    {
        if (offsets.Length < 2)
        {
            // No chunks at all: offsets is [0] (or empty for a node that is not chunked).
            first = 0;
            last = 0;
            return;
        }

        int chunkCount = offsets.Length - 1;

        int start = offsets.BinarySearch(rows.Start);
        first = start >= 0 ? start : Math.Max(~start - 1, 0);

        int end = offsets.BinarySearch(rows.End);
        last = end >= 0 ? end : ~end;

        first = Math.Min(first, chunkCount);
        last = Math.Min(last, chunkCount);
        if (last < first)
        {
            last = first;
        }
    }

    /// <summary>The part of <paramref name="rows"/> that falls in chunk <paramref name="chunk"/>, chunk-local.</summary>
    private static RowRange LocalRange(ReadOnlySpan<long> offsets, RowRange rows, int chunk)
    {
        long chunkStart = offsets[chunk];
        long chunkEnd = offsets[chunk + 1];
        long lo = Math.Max(rows.Start, chunkStart);
        long hi = Math.Min(rows.End, chunkEnd);
        if (hi <= lo)
        {
            return RowRange.Empty;
        }

        return new RowRange(lo - chunkStart, hi - chunkStart);
    }
}
