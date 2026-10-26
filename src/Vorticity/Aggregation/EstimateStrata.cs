using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Aggregating;

/// <summary>
/// The ranges an estimate draws from in a file or an object (<see cref="ScanSource.StrataAsync"/>): its rows cut where the
/// chunks of every column the question reads end together, gathered until a range holds a given number of bytes of the
/// narrowest of them. A range then reads whole chunks of each column, never a block alone: a block holds some 15 KB of
/// two columns, and drawn one by one a share of a month would be as many requests as blocks.
/// </summary>
internal static class EstimateStrata
{
    /// <summary>
    /// <paramref name="rows"/> of a file of layout <paramref name="tree"/> cut into ranges between the common starts of the
    /// chunks of <paramref name="mask"/>'s columns, each of <paramref name="rangeBytes"/> of the narrowest column at least,
    /// at its mean bytes a row; the rows left at the end join the last range. One range for rows no common start cuts.
    /// </summary>
    internal static List<RowRange> Clusters(LayoutTree tree, ReadOnlySpan<SegmentSpec> segments, RowRange rows, in FieldMask mask, long rangeBytes)
    {
        List<RowRange> clusters = [];
        if (rows.IsEmpty)
        {
            return clusters;
        }

        long least = Narrowest(tree, segments, in mask);
        long fileRows = tree.Root.RowCount;
        long rangeRows = least <= 0 || fileRows <= 0 ? rows.Length : Math.Max(1, (long)Math.Ceiling((double)rangeBytes * fileRows / least));
        long start = rows.Start;
        foreach (long at in SplitPlan.CommonStarts(tree, rows, in mask))
        {
            if (at - start >= rangeRows)
            {
                clusters.Add(new RowRange(start, at));
                start = at;
            }
        }

        if (clusters.Count > 0 && rows.End - start < rangeRows)
        {
            clusters[^1] = new RowRange(clusters[^1].Start, rows.End);
        }
        else
        {
            clusters.Add(new RowRange(start, rows.End));
        }

        return clusters;
    }

    /// <summary>The bytes of the narrowest top-level column of <paramref name="mask"/> over the whole file, or of the file for one of no struct.</summary>
    private static long Narrowest(LayoutTree tree, ReadOnlySpan<SegmentSpec> segments, in FieldMask mask)
    {
        LayoutNode root = tree.Root;
        if (root.Encoding != LayoutEncodingId.Struct)
        {
            return Bytes(root, segments);
        }

        long least = long.MaxValue;
        int count = mask.IsAll ? root.ChildCount : mask.NamedFieldCount;
        for (int i = 0; i < count; i++)
        {
            int field = mask.IsAll ? i : mask.GetNamedField(i);
            if (field < root.ChildCount)
            {
                least = Math.Min(least, Bytes(root.GetChild(field), segments));
            }
        }

        return least == long.MaxValue ? Bytes(root, segments) : least;
    }

    /// <summary>The bytes of every segment under <paramref name="node"/>.</summary>
    private static long Bytes(LayoutNode node, ReadOnlySpan<SegmentSpec> segments)
    {
        long bytes = 0;
        foreach (uint segment in node.Segments)
        {
            bytes += segments[(int)segment].Length;
        }

        for (int c = 0; c < node.ChildCount; c++)
        {
            bytes += Bytes(node.GetChild(c), segments);
        }

        return bytes;
    }
}
