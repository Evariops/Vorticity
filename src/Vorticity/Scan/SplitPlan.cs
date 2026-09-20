using System;

using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>
/// The natural split boundaries of one scan, in root-layout row coordinates; a split is the unit
/// of independence. The boundaries come from a read-only traversal of the parsed layout tree,
/// which <see cref="LayoutReader"/> offers no method to ask for.
/// </summary>
/// <remarks>
/// <para>
/// The boundary list is not the split list: sub-dividing a span wider than the cap is left to
/// <see cref="SplitCursor"/>, which does it lazily. A caller may legitimately ask for one row per
/// batch on a three-billion-row file, and materializing three billion boundaries would be an
/// unbounded allocation. What is materialized is bounded by the layout tree, which the parser
/// already bounds.
/// </para>
/// <para>
/// An unknown layout id is treated as opaque rather than fatal. Throwing here would move the
/// unsupported-encoding failure out of <c>LayoutReaderTable.Get</c>, which is the only place meant
/// to raise it; segment registration reaches the same node moments later and throws with the right
/// id and kind.
/// </para>
/// </remarks>
internal sealed class SplitPlan
{
    /// <summary>The batch size a file with no zone map implies.</summary>
    internal const long DefaultBatchRows = 8192;

    private readonly long[] _boundaries;
    private readonly int _count;
    private readonly long _maxRows;

    private SplitPlan(long[] boundaries, int count, long maxRows)
    {
        _boundaries = boundaries;
        _count = count;
        _maxRows = maxRows;
    }

    /// <summary>The cap every split honours.</summary>
    internal long MaxRows => _maxRows;

    /// <summary>How many boundaries the layout contributed; there is one fewer span than this.</summary>
    internal int BoundaryCount => _count;

    /// <summary>Boundary <paramref name="index"/>, ascending and distinct.</summary>
    /// <param name="index">0-based, below <see cref="BoundaryCount"/>.</param>
    internal long BoundaryAt(int index) => _boundaries[index];

    /// <summary>
    /// The batch size the file's own shape implies: the first zone map's zone length, or
    /// <see cref="DefaultBatchRows"/> when the file has none.
    /// </summary>
    /// <param name="tree">The parsed layout tree.</param>
    /// <returns>A positive row count.</returns>
    internal static long NaturalBatchRows(LayoutTree tree)
    {
        int nodes = tree.NodeCount;
        for (int i = 0; i < nodes; i++)
        {
            if (tree.GetNode(i).TryGetZoneMap(out ZoneMap map) && map.ZoneLength > 0)
            {
                return map.ZoneLength;
            }
        }

        return DefaultBatchRows;
    }

    /// <summary>Walks the selected subtrees and collects their split boundaries.</summary>
    /// <param name="tree">The parsed layout tree.</param>
    /// <param name="rows">The rows the scan wants, in root coordinates.</param>
    /// <param name="mask">The projection.</param>
    /// <param name="maxRows">The batch-size cap; must be positive.</param>
    /// <returns>The plan.</returns>
    internal static SplitPlan Compute(LayoutTree tree, RowRange rows, in FieldMask mask, long maxRows)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        BoundaryList list = new BoundaryList();
        if (!rows.IsEmpty)
        {
            list.Push(rows.Start);
            LayoutNode root = tree.Root;
            Walk(in root, rows, rowOffset: 0, in mask, list, depth: 1);

            // The interior boundaries are hints; the two ends are load-bearing, and the walk is
            // trusted for neither. A node whose children cover fewer rows than it claims - a zoned
            // layout over a short data child, say - would otherwise contribute a maximum below
            // rows.End and the scan would SILENTLY DROP the tail. Pinning the end here makes the
            // split set cover the requested range whatever the tree says; the layout reader then
            // fails loudly on the rows the file cannot actually produce, which is the correct
            // failure for a malformed file (§1.4).
            list.Push(rows.End);
        }

        int count = list.Finish();
        return new SplitPlan(list.Items, count, maxRows);
    }

    /// <summary>A fresh cursor over this plan's splits.</summary>
    internal SplitCursor CreateCursor() => new SplitCursor(this);

    /// <summary>A fresh cursor over this plan's splits, last one first.</summary>
    /// <param name="reverse">Whether to walk the splits backwards.</param>
    /// <remarks>
    /// The same splits in the opposite order, and the same splits matters: a descending walk that
    /// cut the range differently from the ascending one would deliver different batches, and the
    /// two are compared row for row.
    /// </remarks>
    internal SplitCursor CreateCursor(bool reverse) => new SplitCursor(this, reverse);

    /// <summary>
    /// The split holding <paramref name="row"/>, exactly as <see cref="SplitCursor"/> would cut it,
    /// found without walking the splits before it.
    /// </summary>
    /// <param name="row">A row inside the plan's range.</param>
    /// <returns>The split.</returns>
    internal RowRange SplitOf(long row)
    {
        int low = 0;
        int high = _count - 2;
        while (low < high)
        {
            int mid = (low + high + 1) >>> 1;
            if (_boundaries[mid] <= row)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        long start = _boundaries[low];
        long end = _boundaries[low + 1];
        long size = SplitCursor.SubSize(end - start, _maxRows);
        long first = start + ((row - start) / size * size);
        return new RowRange(first, Math.Min(first + size, end));
    }

    private static void Walk(
        in LayoutNode node, RowRange local, long rowOffset, in FieldMask mask, BoundaryList list, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxLayoutDepth, "Layout");

        if (local.IsEmpty)
        {
            return;
        }

        switch (node.Encoding)
        {
            case LayoutEncodingId.Chunked:
                WalkChunked(in node, local, rowOffset, in mask, list, depth);
                return;

            case LayoutEncodingId.Struct:
                WalkStruct(in node, local, rowOffset, in mask, list, depth);
                return;

            case LayoutEncodingId.Dict:
                // Child 1 is the codes - the row-aligned side. Child 0 is the dictionary and has a
                // row count of its own that has nothing to do with the scan's rows
                // (vortex-layout-0.86.1/src/layouts/dict/reader.rs::register_splits).
                if (node.ChildCount == 2)
                {
                    LayoutNode codes = node.GetChild(1);
                    FieldMask all = FieldMask.All;
                    Walk(in codes, local, rowOffset, in all, list, depth + 1);
                    return;
                }

                break;

            case LayoutEncodingId.Zoned:
            case LayoutEncodingId.Stats:
                // Child 0 is the data; the zones child informs pruning, which reads it through its
                // own path before the splits are planned.
                if (node.ChildCount >= 1)
                {
                    LayoutNode data = node.GetChild(0);
                    Walk(in data, local, rowOffset, in mask, list, depth + 1);
                    return;
                }

                break;

            default:
                break;
        }

        // vortex.flat, an unknown id, and any node whose arity is wrong: indivisible. A wrong arity
        // is reported by the layout reader, which sees the same node during RegisterSegments.
        list.Push(rowOffset + local.End);
    }

    private static void WalkChunked(
        in LayoutNode node, RowRange local, long rowOffset, in FieldMask mask, BoundaryList list, int depth)
    {
        ReadOnlySpan<long> offsets = node.ChunkOffsets;
        int chunks = node.ChildCount;
        if (offsets.Length != chunks + 1)
        {
            list.Push(rowOffset + local.End);
            return;
        }

        for (int i = 0; i < chunks; i++)
        {
            long start = offsets[i];
            long end = offsets[i + 1];
            if (end <= local.Start)
            {
                continue;
            }

            if (start >= local.End)
            {
                break;
            }

            RowRange chunkRows = new RowRange(start, end).Intersect(local);
            if (chunkRows.IsEmpty)
            {
                continue;
            }

            LayoutNode chunk = node.GetChild(i);
            Walk(
                in chunk,
                new RowRange(chunkRows.Start - start, chunkRows.End - start),
                rowOffset + start,
                in mask,
                list,
                depth + 1);
        }
    }

    private static void WalkStruct(
        in LayoutNode node, RowRange local, long rowOffset, in FieldMask mask, BoundaryList list, int depth)
    {
        DType dtype = node.DType;
        int validityChildren = !dtype.IsDefault && dtype.IsNullable ? 1 : 0;
        if (dtype.IsDefault || dtype.Kind != DTypeKind.Struct ||
            node.ChildCount != dtype.FieldCount + validityChildren)
        {
            list.Push(rowOffset + local.End);
            return;
        }

        if (validityChildren == 1)
        {
            LayoutNode validity = node.GetChild(0);
            FieldMask all = FieldMask.All;
            Walk(in validity, local, rowOffset, in all, list, depth + 1);
        }

        int fieldCount = dtype.FieldCount;
        for (int k = 0; k < fieldCount; k++)
        {
            if (!mask.Includes(k))
            {
                continue;
            }

            LayoutNode child = node.GetChild(k + validityChildren);
            FieldMask childMask = mask.Descend(k);
            Walk(in child, local, rowOffset, in childMask, list, depth + 1);
        }

        // An empty struct - or one whose every field was projected away - still has to register the
        // end of its range, or the scan would produce no splits at all.
        list.Push(rowOffset + local.End);
    }

    /// <summary>A growable ascending set of boundaries. Open-time only; it allocates.</summary>
    private sealed class BoundaryList
    {
        internal long[] Items = new long[32];
        private int _count;

        internal void Push(long row)
        {
            if (_count == Items.Length)
            {
                Array.Resize(ref Items, Items.Length * 2);
            }

            Items[_count++] = row;
        }

        /// <summary>Sorts and de-duplicates in place; returns the surviving count.</summary>
        internal int Finish()
        {
            long[] items = Items;
            int count = _count;
            if (count <= 1)
            {
                return count;
            }

            Array.Sort(items, 0, count);

            int write = 1;
            for (int read = 1; read < count; read++)
            {
                if (items[read] != items[write - 1])
                {
                    items[write++] = items[read];
                }
            }

            return write;
        }
    }
}

/// <summary>
/// Walks a <see cref="SplitPlan"/>'s spans, sub-dividing any span wider than the cap into evenly
/// sized pieces.
/// </summary>
/// <remarks>
/// Even sub-division rather than "max-sized pieces plus a remainder" is upstream's
/// <c>subdivide_large_spans</c> (vortex-layout-0.86.1/src/scan/split_by.rs): a 9000-row span capped
/// at 8192 yields 4500 + 4500, not 8192 + 808, so a parallel decode is not left with one nearly
/// empty split.
/// </remarks>
internal struct SplitCursor
{
    private readonly SplitPlan _plan;
    private readonly bool _reverse;
    private int _span;          // index of the span [BoundaryAt(_span), BoundaryAt(_span + 1))
    private long _cursor;       // first row of the next split
    private long _subSize;      // even sub-division size within the current span

    internal SplitCursor(SplitPlan plan)
        : this(plan, reverse: false)
    {
    }

    internal SplitCursor(SplitPlan plan, bool reverse)
    {
        _plan = plan;
        _reverse = reverse;
        _span = reverse ? plan.BoundaryCount - 1 : -1;
        _cursor = 0;
        _subSize = 0;
    }

    /// <summary>The next split, or <see langword="false"/> when the plan is exhausted.</summary>
    /// <param name="range">The split's rows, in root coordinates.</param>
    /// <returns>Whether a split was produced.</returns>
    internal bool TryNext(out RowRange range) => _reverse ? TryPrevious(out range) : TryForward(out range);

    /// <summary>
    /// The spans last first, and each span's sub-divisions last first, which is the forward walk
    /// read backwards and not a second way of cutting the same rows.
    /// </summary>
    /// <param name="range">The split's rows, in root coordinates.</param>
    /// <returns>Whether a split was produced.</returns>
    private bool TryPrevious(out RowRange range)
    {
        SplitPlan plan = _plan;

        while (true)
        {
            // `_cursor` is the END of the next split here, where the forward walk holds its start,
            // and `_subSize` of zero means no span is open. Which sub-division that end belongs to
            // is arithmetic on the span's start, so the reversed walk needs no counter of its own --
            // and it must need none: this cursor lives by value inside the enumerator, and one more
            // field of it is eight bytes on every scan, which three allocation axes have no room
            // for.
            if (_subSize == 0 || _cursor <= plan.BoundaryAt(_span))
            {
                int next = _span - 1;
                if (next < 0)
                {
                    range = RowRange.Empty;
                    return false;
                }

                _span = next;
                long low = plan.BoundaryAt(next);
                long high = plan.BoundaryAt(next + 1);
                if (high <= low)
                {
                    _subSize = 0;
                    continue;
                }

                _subSize = SubSize(high - low, plan.MaxRows);
                _cursor = high;
            }

            long start = plan.BoundaryAt(_span);
            long stop = _cursor;
            start += ((stop - start - 1) / _subSize) * _subSize;
            _cursor = start;
            range = new RowRange(start, stop);
            return true;
        }
    }

    private bool TryForward(out RowRange range)
    {
        SplitPlan plan = _plan;
        int boundaries = plan.BoundaryCount;

        while (true)
        {
            if (_span < 0 || _cursor >= plan.BoundaryAt(_span + 1))
            {
                int next = _span + 1;
                if (next + 1 >= boundaries)
                {
                    range = RowRange.Empty;
                    return false;
                }

                _span = next;
                _cursor = plan.BoundaryAt(next);
                _subSize = SubSize(plan.BoundaryAt(next + 1) - _cursor, plan.MaxRows);
                continue;
            }

            long end = plan.BoundaryAt(_span + 1);
            long stop = _cursor + _subSize;
            if (stop > end || stop < _cursor)
            {
                stop = end;
            }

            range = new RowRange(_cursor, stop);
            _cursor = stop;
            return true;
        }
    }

    /// <summary>The even sub-division size of a span: what every split of it but the last holds.</summary>
    /// <param name="span">The span's rows.</param>
    /// <param name="maxRows">The cap.</param>
    internal static long SubSize(long span, long maxRows)
    {
        if (span <= maxRows)
        {
            return span;
        }

        // ceil(span / maxRows) sub-ranges, then ceil(span / subCount) rows each. Both divisions are
        // on non-negative longs that cannot overflow: span <= long.MaxValue and maxRows >= 1.
        long subCount = (span / maxRows) + (span % maxRows == 0 ? 0 : 1);
        return (span / subCount) + (span % subCount == 0 ? 0 : 1);
    }
}
