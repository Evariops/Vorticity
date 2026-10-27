using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Indexes;

/// <summary>
/// The geometry of the chunks of a file's columns (spec C, 2.1), by the offset of each chunk's segment: its encoding tree and
/// its marks (<see cref="ChunkGeometry"/>), the rows of a granule, and the chunk's first row in the file. What a scan that
/// reads part of a chunk plans its bytes by before it reads any; shared by the scan's lanes, read only.
/// </summary>
internal sealed class GranuleGeometry
{
    private readonly Dictionary<ulong, (ChunkGeometry Geometry, int GranuleRows, long FirstRow)> _chunks;

    private GranuleGeometry(Dictionary<ulong, (ChunkGeometry Geometry, int GranuleRows, long FirstRow)> chunks, long requestBytes)
    {
        _chunks = chunks;
        RequestBytes = requestBytes;
    }

    /// <summary>
    /// The bytes a request to the file's store is worth (<see cref="IO.CostProfile.RequestBytes"/>): what a chunk's live
    /// granules, read apart, must spare per request more than the chunk read whole (spec D, 4.4).
    /// </summary>
    internal long RequestBytes { get; }

    /// <summary>
    /// The geometry of the chunk whose segment starts at <paramref name="offset"/>, and its first row in the file; false when
    /// it is not known.
    /// </summary>
    internal bool TryGet(ulong offset, out ChunkGeometry geometry, out int granuleRows, out long firstRow)
    {
        if (_chunks.TryGetValue(offset, out (ChunkGeometry Geometry, int GranuleRows, long FirstRow) chunk))
        {
            (geometry, granuleRows, firstRow) = chunk;
            return true;
        }

        (geometry, granuleRows, firstRow) = (default, 0, 0);
        return false;
    }

    /// <summary>
    /// The geometry of the columns of <paramref name="tree"/>, a struct of them, whose marks <paramref name="marks"/> gives by
    /// the column's place: each column's chunks, the leaves its layout holds in their order, paired with its marks' chunks,
    /// each first row the rows of the leaves before it; a column whose chunks and marks do not pair is left out, its chunks
    /// read whole. Null when no column pairs. <paramref name="requestBytes"/> is what a request to its store is worth.
    /// </summary>
    internal static GranuleGeometry? Of(LayoutTree tree, IReadOnlyList<ColumnMarks?> marks, long requestBytes)
    {
        LayoutNode root = tree.Root;
        if (root.Encoding != LayoutEncodingId.Struct)
        {
            return null;
        }

        Dictionary<ulong, (ChunkGeometry Geometry, int GranuleRows, long FirstRow)>? chunks = null;
        List<LayoutNode> leaves = [];
        ReadOnlySpan<SegmentSpec> specs = tree.File.SegmentSpecs;
        for (int field = 0; field < marks.Count && field < root.ChildCount; field++)
        {
            if (marks[field] is not { } column)
            {
                continue;
            }

            leaves.Clear();
            if (!Leaves(root.GetChild(field), leaves) || leaves.Count != column.Chunks.Length)
            {
                continue;
            }

            long first = 0;
            for (int c = 0; c < leaves.Count; c++)
            {
                ReadOnlySpan<uint> segments = leaves[c].Segments;
                if (segments.Length == 1 && segments[0] < (uint)specs.Length)
                {
                    (chunks ??= [])[specs[(int)segments[0]].Offset] = (column.Chunks[c], column.GranuleRows, first);
                }

                first += leaves[c].RowCount;
            }
        }

        return chunks is null ? null : new GranuleGeometry(chunks, requestBytes);
    }

    /// <summary>
    /// The flat chunks of a column's layout, in the order its writer wrote them: under its zones, under its chunking, its codes
    /// under an object dictionary; false for a layout of another shape.
    /// </summary>
    private static bool Leaves(LayoutNode node, List<LayoutNode> into)
    {
        switch (node.Encoding)
        {
            case LayoutEncodingId.Flat:
                into.Add(node);
                return true;
            case LayoutEncodingId.Chunked:
                for (int c = 0; c < node.ChildCount; c++)
                {
                    if (!Leaves(node.GetChild(c), into))
                    {
                        return false;
                    }
                }

                return true;
            case LayoutEncodingId.Dict when node.ChildCount == 2:
                return Leaves(node.GetChild(1), into);
            case LayoutEncodingId.Zoned or LayoutEncodingId.Stats when node.ChildCount >= 1:
                return Leaves(node.GetChild(0), into);
            default:
                return false;
        }
    }
}

/// <summary>
/// What one lane of a scan plans of the chunks it reads a part of (spec C, 2.2): for each, the ranges of its region of
/// buffers that the part's granules hold, registered with the split's segments in place of the whole segment, and the plan its
/// batches decode by. A split reads a part of a chunk alone when the scan reads that part of it alone (its rows
/// <paramref name="scanned"/>), or, of a chunk it reads all of, when the blocks <paramref name="live"/> leaves dead within it
/// spare more than the requests its live granules cost read apart (spec D, 4.4). Under a take, the part is the rows it wants
/// of the split's (<paramref name="take"/>, spec C, 3), each cluster of them as the reader decodes it
/// (<see cref="FlatLayoutReader.ClusterGap"/>). A chunk the split wants whole, whose geometry is not known, or whose granules
/// hold most of its bytes is read whole, as before, and once.
/// </summary>
internal sealed class GranulePlans(
    GranuleGeometry geometry, VortexFile file, Scanning.RowSelection? take = null, Compute.BlockMask? live = null, RowRange? scanned = null)
{
    /// <summary>The share of a chunk's bytes past which its granules are read with the rest of it, in one request (N9).</summary>
    internal const double WholeShare = 0.75;

    /// <summary>The plans a chunk keeps at most, those of the splits registered last: the ones the lane reads next.</summary>
    private const int PlansPerChunk = 4;

    private readonly ArrayNodeArena _nodes = new ArrayNodeArena();
    private readonly GranuleBytes _planner = new GranuleBytes();
    private readonly Dictionary<ulong, List<Plan>> _plans = [];

    // Whether each chunk the scan reads every row of is read by its live granules, by its segment's offset.
    private readonly Dictionary<ulong, bool> _sparse = [];

    /// <summary>
    /// A part of a chunk read by its granules: its rows, the chunk's tree, the ranges of the chunk's region of buffers read,
    /// that region's length and alignment.
    /// </summary>
    internal sealed record Plan(int Start, int End, ReadOnlyMemory<byte> Tree, (long Start, long End)[] Ranges, long RegionLength, byte Alignment);

    /// <summary>
    /// Registers in <paramref name="segments"/> the ranges of the chunk of segment <paramref name="spec"/>, of
    /// <paramref name="rows"/> rows of <paramref name="dtype"/>, that rows <paramref name="part"/> hold, those the take wants
    /// of them under one, and keeps the plan; false when the chunk is read whole.
    /// </summary>
    internal bool TryRegister(in SegmentSpec spec, DType dtype, int rows, RowRange part, SegmentRequestSet segments)
    {
        if (part.IsEmpty || !geometry.TryGet(spec.Offset, out ChunkGeometry chunk, out int granuleRows, out long firstRow))
        {
            return false;
        }

        // The rows of the part a take wants: none, or every one, leave the part as it is.
        RowRange inFile = new RowRange(firstRow + part.Start, firstRow + part.End);
        int wanted = take is null ? 0 : take.CountIn(inFile);
        bool selected = wanted > 0 && wanted < part.Length;
        if ((take is not null && wanted == 0)
            || (!selected && ((part.Start == 0 && part.End >= rows) || (take is null && !Apart(spec, firstRow, rows)))))
        {
            return false;
        }

        (long Start, long Length)[] layout = ArrayBlobReader.BufferLayout(chunk.Tree.Span, out long regionLength);
        if (regionLength <= 0 || regionLength > spec.Length)
        {
            return false;
        }

        ArrayBlobReader.LoadLayout(_nodes, chunk.Tree.Span, file.ResolvedArrayEncodings);
        ArrayNode root = _nodes.Root;
        int start = checked((int)part.Start);
        int count = checked((int)part.Length);
        if (!(selected ? Selected(in root, WidthOf(dtype), rows, inFile, start, wanted, layout, chunk, granuleRows)
                : _planner.TryPlan(in root, WidthOf(dtype), rows, start, count, layout, chunk, granuleRows))
            || _planner.Bytes > WholeShare * spec.Length)
        {
            return false;
        }

        (long Start, long End)[] ranges = [.. _planner.Ranges];
        foreach ((long rangeStart, long rangeEnd) in ranges)
        {
            segments.Add(new SegmentSpec(spec.Offset + (ulong)rangeStart, checked((uint)(rangeEnd - rangeStart)), 0, 0, 0));
        }

        if (!_plans.TryGetValue(spec.Offset, out List<Plan>? held))
        {
            _plans[spec.Offset] = held = new List<Plan>(PlansPerChunk);
        }

        if (held.Count == PlansPerChunk)
        {
            held.RemoveAt(0);
        }

        held.Add(new Plan(start, start + count, chunk.Tree, ranges, regionLength, spec.AlignmentExponent));
        return true;
    }

    /// <summary>
    /// Whether the splits of the chunk of segment <paramref name="spec"/>, its rows from <paramref name="firstRow"/>, read
    /// their parts of it apart: when the scan reads a part of the chunk alone; or, reading every row of it, when its live
    /// granules make <c>r</c> runs and a share <c>s</c> of its rows such that <c>(r − 1)</c> requests more spare less than
    /// the <c>(1 − s)</c> of its bytes they leave (spec D, 4.4). Otherwise its splits read it whole, once.
    /// </summary>
    private bool Apart(in SegmentSpec spec, long firstRow, int rows)
    {
        RowRange chunk = new RowRange(firstRow, firstRow + rows);
        if (scanned is { } range && chunk.Intersect(range).Length < rows)
        {
            return true;
        }

        if (live is null)
        {
            return false;
        }

        if (!_sparse.TryGetValue(spec.Offset, out bool sparse))
        {
            _sparse[spec.Offset] = sparse = Sparse(chunk, spec.Length);
        }

        return sparse;
    }

    /// <summary>Whether the live blocks of <paramref name="chunk"/>, of <paramref name="bytes"/>, pay their reads apart.</summary>
    private bool Sparse(RowRange chunk, uint bytes)
    {
        long blockRows = live!.BlockRows;
        int first = (int)(chunk.Start / blockRows);
        int end = (int)Math.Min((chunk.End + blockRows - 1) / blockRows, live.BlockCount);
        int lived = 0;
        int runs = 0;
        bool before = false;
        for (int block = first; block < end; block++)
        {
            bool on = live.IsLive(block);
            lived += on ? 1 : 0;
            runs += on && !before ? 1 : 0;
            before = on;
        }

        int blocks = end - first;
        return lived > 0 && lived < blocks && (runs - 1) * (double)geometry.RequestBytes < (1 - ((double)lived / blocks)) * bytes;
    }

    /// <summary>
    /// Plans the <paramref name="wanted"/> rows the take wants of the rows <paramref name="inFile"/>, the part of the chunk
    /// from its row <paramref name="start"/>: their places in the chunk, each cluster of them read as the reader decodes it.
    /// </summary>
    private bool Selected(
        in ArrayNode root, int width, int rows, RowRange inFile, int start, int wanted, (long Start, long Length)[] layout,
        ChunkGeometry chunk, int granuleRows)
    {
        int[] places = System.Buffers.ArrayPool<int>.Shared.Rent(wanted);
        try
        {
            Span<int> local = places.AsSpan(0, take!.LocalIndices(inFile, places));
            for (int i = 0; i < local.Length; i++)
            {
                local[i] += start;
            }

            return _planner.TryPlan(in root, width, rows, local, FlatLayoutReader.ClusterGap, layout, chunk, granuleRows);
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(places);
        }
    }

    /// <summary>The plan of the chunk of segment offset <paramref name="offset"/> that holds rows <paramref name="rows"/>; null when it is read whole.</summary>
    internal Plan? Find(ulong offset, RowRange rows)
    {
        if (!_plans.TryGetValue(offset, out List<Plan>? held))
        {
            return null;
        }

        for (int p = held.Count - 1; p >= 0; p--)
        {
            if (held[p].Start <= rows.Start && rows.End <= held[p].End)
            {
                return held[p];
            }
        }

        return null;
    }

    /// <summary>The bytes of a value of <paramref name="type"/>, a number or the storage of one; 0 for another type.</summary>
    internal static int WidthOf(DType type) => type.Kind switch
    {
        DTypeKind.Primitive => type.PType.ByteWidth(),
        DTypeKind.Extension => WidthOf(type.StorageType),
        _ => 0,
    };
}
