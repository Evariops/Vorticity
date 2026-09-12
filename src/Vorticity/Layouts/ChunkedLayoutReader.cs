// vortex.chunked - vortex-layout-0.86.1/src/layouts/chunked/{mod.rs,reader.rs}. Zero segments; every
// child is a chunk, in row order. ZERO CHILDREN IS LEGAL (an empty stream), and then the layout must
// cover zero rows.
//
// Chunk offsets are derived, never stored, and the parser has already checked that they sum to the
// parent's row count (docs/03-architecture.md §6: verify, never assume). The chunk SELECTION is
// upstream's, transcribed: binary search for the range start falling back to insertion point - 1,
// binary search for the end falling back to insertion point.
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.chunked</c> layout by concatenating the chunks a range touches.</summary>
public sealed class ChunkedLayoutReader : LayoutReader
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

        int length = BatchLength(rows);
        ReadOnlySpan<long> offsets = node.ChunkOffsets;
        ChunkRange(offsets, rows, out int first, out int last);

        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(Math.Max(last - first, 0), stack);
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
                chunks[count++] = ExecuteChild(in chunk, local, in fields, context);
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
            // theirs - and it is the PROJECTED one when a mask narrowed the children.
            DType dtype = context.Canonical.GetNode(chunks[0]).DType;
            return CanonicalConcat.Concat(context.Decode, dtype, length, chunks[..count]);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// The half-open chunk range a row range touches.
    /// </summary>
    /// <remarks>
    /// vortex-layout-0.86.1/src/layouts/chunked/reader.rs <c>chunk_range</c>:
    /// <code>
    /// let start_chunk = offsets.binary_search(&amp;range.start).unwrap_or_else(|x| x.saturating_sub(1));
    /// let end_chunk   = offsets.binary_search(&amp;range.end).unwrap_or_else(|x| x);
    /// </code>
    /// .NET's <c>BinarySearch</c> returns <c>~insertionPoint</c> where Rust returns
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
