using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

        int length = context.HasSelection ? context.SelectionCount : BatchLength(rows);
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
    /// The rows are contiguous, so the selection is set as the range <c>[start, start + length)</c>:
    /// a reader that slices or decodes a range takes it as one, and the rows are written out into
    /// the buffer only for a reader that takes them one by one. The buffer is rented from the
    /// shared pool, so this allocates nothing once the pool is warm.
    /// </para>
    /// <para>
    /// An encoding that decodes a range of its rows decodes these rows as one, the selection being a
    /// dense one, even when it could also take them a row at a time; one that can only take rows
    /// takes them; the rows are their own window, because the window the plan gave the batch may
    /// hold dead blocks, which no batch reads. Any other encoding decodes the chunk once, retains
    /// it, and gathers this range out of it. Against the
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
            ScanContext.SavedSelection saved = context.ExchangeSelectionRange(range, (int)local.Start, length);
            int lead = context.WindowLead;
            int span = context.WindowSpan;
            context.WindowLead = 0;
            context.WindowSpan = length;
            try
            {
                return ExecuteRowChild(in chunk, local, in fields, context);
            }
            finally
            {
                context.WindowLead = lead;
                context.WindowSpan = span;
                context.RestoreSelection(in saved);
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

        int[] rebased = ArrayPool<int>.Shared.Rent(Math.Max(selection.Length, 1));
        try
        {
            int count = Rebase(selection, start, chunk.RowCount, rebased);
            ScanContext.SavedSelection saved = context.ExchangeSelection(rebased, count);
            try
            {
                return ExecuteRowChild(in chunk, local, in fields, context);
            }
            finally
            {
                context.RestoreSelection(in saved);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rebased);
        }
    }

    /// <summary>The selected rows that fall in a chunk, in the chunk's own row space.</summary>
    /// <param name="selection">The rows, in the chunked node's space.</param>
    /// <param name="start">The chunk's first row.</param>
    /// <param name="rows">The chunk's row count.</param>
    /// <param name="into">Room for every row of <paramref name="selection"/>.</param>
    /// <returns>How many fall in the chunk.</returns>
    /// <remarks>
    /// Every column of a split walks the same selection, so the walk has no branch on a row: the
    /// rows are moved a vector at a time and checked all at once, which is the whole answer when
    /// the split lies in one chunk, and otherwise each row is written at the next slot and kept by
    /// its own verdict.
    /// </remarks>
    internal static int Rebase(ReadOnlySpan<int> selection, long start, long rows, Span<int> into)
    {
        if (start > int.MaxValue)
        {
            return 0;
        }

        int origin = (int)start;
        uint limit = (uint)Math.Min(rows, uint.MaxValue);
        Span<int> slots = into[..selection.Length];
        int i = 0;
        if (Vector128.IsHardwareAccelerated && selection.Length >= Vector128<int>.Count)
        {
            Vector128<int> shift = Vector128.Create(origin);
            Vector128<uint> bound = Vector128.Create(limit);
            Vector128<uint> outside = Vector128<uint>.Zero;
            ref int from = ref MemoryMarshal.GetReference(selection);
            ref int to = ref MemoryMarshal.GetReference(slots);
            for (; i <= selection.Length - Vector128<int>.Count; i += Vector128<int>.Count)
            {
                Vector128<int> local = Vector128.LoadUnsafe(ref from, (nuint)i) - shift;
                local.StoreUnsafe(ref to, (nuint)i);
                outside |= Vector128.GreaterThanOrEqual(local.AsUInt32(), bound);
            }

            if (outside != Vector128<uint>.Zero)
            {
                i = 0;
            }
        }

        int count = i;
        for (; i < selection.Length; i++)
        {
            int local = selection[i] - origin;
            slots[count] = local;
            count += (uint)local < limit ? 1 : 0;
        }

        return count;
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
