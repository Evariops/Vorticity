// The segments of each top-level column of a file, found through its layout tree: where a test sees which columns a
// writer laid together, now that the index directory no longer lists the synopsis part's columns.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;

namespace Vorticity.Tests.Writing;

internal static class ColumnSegments
{
    /// <summary>The segments of each top-level column of <paramref name="file"/>, in the order of its fields.</summary>
    internal static async Task<List<VortexSegment>[]> OfAsync(VortexFile file, CancellationToken cancellationToken)
    {
        VortexLayout root = await file.GetLayoutAsync(cancellationToken);
        int fields = file.DType.FieldCount;
        VortexLayout columns = Columns(root, fields) ?? throw new InvalidOperationException("The file's layout holds no struct of its columns.");
        ImmutableArray<VortexSegment> map = file.SegmentMap;
        List<VortexSegment>[] segments = new List<VortexSegment>[fields];
        for (int f = 0; f < fields; f++)
        {
            segments[f] = [];
            Collect(columns.Children[f], map, segments[f]);
        }

        return segments;
    }

    /// <summary>The first struct node, depth first, with a child for each of the file's <paramref name="fields"/>.</summary>
    private static VortexLayout? Columns(VortexLayout node, int fields)
    {
        if (node.Encoding == "vortex.struct" && node.Children.Length == fields)
        {
            return node;
        }

        foreach (VortexLayout child in node.Children)
        {
            if (Columns(child, fields) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The segments of the column's data under <paramref name="node"/>: of a zoned node, its data's, the zone map aside.</summary>
    private static void Collect(VortexLayout node, ImmutableArray<VortexSegment> map, List<VortexSegment> into)
    {
        foreach (int segment in node.Segments)
        {
            into.Add(map[segment]);
        }

        if (node.Encoding == "vortex.zoned")
        {
            Collect(node.Children[0], map, into);
            return;
        }

        foreach (VortexLayout child in node.Children)
        {
            Collect(child, map, into);
        }
    }
}
