// Every one of the 819 golden files' layout trees, parsed and compared node by node against the
// sidecar's `layout` line - encoding id, pushed-down dtype, row count, segment ids and metadata
// length - plus the manifest's `layout_ids` set and its `zone_maps` count.
//
// This is the test that makes the reader a LAYOUT TREE INTERPRETER rather than a fixed-structure
// parser: nothing here knows what shape a file has, and the corpus contains struct-over-zoned-over-
// chunked-over-flat, dict layouts, bare flat roots and an inlined array tree.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LayoutTreeCorpusTests
{
    [Fact]
    public async Task EveryCorpusLayoutTreeMatchesItsSidecar()
    {
        int files = 0;
        int nodes = 0;

        foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
        {
            if (!entry.HasDTypeSegment)
            {
                // types/no_dtype_segment needs a caller-supplied schema; opening it is file-open's
                // test, not this component's.
                continue;
            }

            await using VortexFile file = await VortexFile.OpenAsync(LayoutCorpus.FullPath(entry));
            LayoutTree tree = LayoutTree.Parse(file);
            SidecarLayoutNode expected = LayoutCorpus.ReadLayout(entry);

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Compare(expected, tree.Root, entry.Id, entry.Id, seen);

            Assert.True(
                tree.Root.RowCount == entry.RowCount,
                $"{entry.Id}: root covers {tree.Root.RowCount} rows, manifest says {entry.RowCount}.");

            // The manifest's layout_ids is the whole file's set. Ours can be a subset only when the
            // tree stops at an unknown layout, whose children we deliberately do not materialize.
            HashSet<string> declared = new HashSet<string>(entry.LayoutIds, StringComparer.Ordinal);
            foreach (string id in seen)
            {
                Assert.True(declared.Contains(id), $"{entry.Id}: tree uses undeclared layout '{id}'.");
            }

            if (!seen.Contains("vortex.list"))
            {
                Assert.True(
                    seen.Count == declared.Count,
                    $"{entry.Id}: found {seen.Count} layout ids, manifest declares {declared.Count}.");
            }

            files++;
            nodes += tree.NodeCount;
        }

        Assert.Equal(820, files);
        Assert.True(nodes > 819, $"Only {nodes} layout nodes over the whole corpus.");
    }

    [Fact]
    public async Task RealFilesSpendFarMoreThanTheNodeBudgetPerLayoutNode()
    {
        // The parser bounds the materialized node count by layoutBytes / 4 + 1, which is what stops
        // a shared-children DAG from expanding a 3 KB buffer into a million records. The bound is
        // not a heuristic - every node but the root is reached through a distinct 4-byte child slot
        // unless a table is SHARED - but it is only useful if real files stay clear of it, so the
        // margin is measured rather than assumed. Densest file in the 0.86.1 corpus, when this was
        // written: types/struct_nested_deep_nonnull_r0 at 27% of the budget.
        double worst = 0;
        string worstId = string.Empty;

        foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
        {
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            await using VortexFile file = await VortexFile.OpenAsync(LayoutCorpus.FullPath(entry));
            LayoutTree tree = LayoutTree.Parse(file);

            long budget = ((long)file.RootLayoutBytes.Length / 4) + 1;
            double used = (double)tree.NodeCount / budget;
            if (used > worst)
            {
                worst = used;
                worstId = entry.Id;
            }
        }

        Assert.True(
            worst <= 0.5,
            $"{worstId} uses {worst.ToString("P0", CultureInfo.InvariantCulture)} of the node budget; " +
            "real files must stay well clear of it or the bound is a conformance risk.");
    }

    [Fact]
    public async Task ZoneMapsMatchTheirSidecars()
    {
        int checkedMaps = 0;

        foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
        {
            if (entry.ZoneMaps == 0 || !entry.HasDTypeSegment)
            {
                continue;
            }

            if (Array.IndexOf(entry.LayoutIds, "vortex.list") >= 0)
            {
                // Two of this file's three zone maps sit UNDER the unknown vortex.list layout,
                // whose children the tree deliberately does not materialize (contract §2.8).
                continue;
            }

            await using VortexFile file = await VortexFile.OpenAsync(LayoutCorpus.FullPath(entry));
            LayoutTree tree = LayoutTree.Parse(file);
            SidecarZoneMap[] expected = LayoutCorpus.ReadZoneMaps(entry);

            List<LayoutNode> zoned = new List<LayoutNode>();
            CollectZoned(tree.Root, zoned);

            Assert.True(
                expected.Length == zoned.Count,
                $"{entry.Id}: sidecar has {expected.Length} zone maps, tree has {zoned.Count}.");

            for (int i = 0; i < zoned.Count; i++)
            {
                LayoutNode node = zoned[i];
                SidecarZoneMap map = expected[i];
                string where = $"{entry.Id}[zone {i.ToString(CultureInfo.InvariantCulture)}]";

                Assert.True(node.TryGetZoneMap(out ZoneMap actual), $"{where}: no zone map.");
                Assert.True(
                    actual.ZoneLength == map.ZoneLength,
                    $"{where}: zone length {actual.ZoneLength}, expected {map.ZoneLength}.");
                Assert.True(
                    actual.ZoneCount == map.ZoneCount,
                    $"{where}: {actual.ZoneCount} zones, expected {map.ZoneCount}.");
                Assert.True(
                    actual.AggregateCount == map.Aggregates.Length,
                    $"{where}: {actual.AggregateCount} aggregates, expected {map.Aggregates.Length}.");
                Assert.True(actual.IsPruningAvailable, $"{where}: pruning should be available.");

                // Every aggregate the corpus uses is one we know, so every spec contributes a
                // column and the zones child's dtype is fully derivable.
                for (int a = 0; a < actual.AggregateCount; a++)
                {
                    Assert.True(
                        actual.GetAggregate(a) != AggregateId.Unknown,
                        $"{where}: aggregate {a} did not resolve.");
                    Assert.True(
                        actual.GetColumnIndex(a) == a,
                        $"{where}: aggregate {a} maps to column {actual.GetColumnIndex(a)}.");
                }

                // The zones child dtype is the derivation this component owns end to end: the
                // sidecar's own struct is the only oracle for it.
                Assert.Equal(2, node.ChildCount);
                DType zones = node.GetChild(1).DType;
                SidecarDTypeAssert.Equal(map.ZonesDType, zones, where + ".zones");

                // And the column NAMES are the aggregates' display forms, "{id}({options})", which
                // this component derives from the options payload rather than reading anywhere.
                Assert.True(
                    zones.FieldCount == map.Aggregates.Length,
                    $"{where}: {zones.FieldCount} zone columns for {map.Aggregates.Length} aggregates.");
                for (int a = 0; a < map.Aggregates.Length; a++)
                {
                    Assert.True(
                        string.Equals(map.Aggregates[a], zones.GetFieldName(a), StringComparison.Ordinal),
                        $"{where}: column {a} is named '{zones.GetFieldName(a)}', " +
                        $"the sidecar says '{map.Aggregates[a]}'.");
                }

                checkedMaps++;
            }
        }

        Assert.True(checkedMaps > 500, $"Only {checkedMaps} zone maps checked.");
    }

    private static void CollectZoned(LayoutNode node, List<LayoutNode> into)
    {
        if (node.Encoding == LayoutEncodingId.Zoned)
        {
            into.Add(node);
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            CollectZoned(node.GetChild(i), into);
        }
    }

    private static void Compare(
        SidecarLayoutNode expected, LayoutNode actual, string where, string fileId, HashSet<string> seen)
    {
        seen.Add(actual.EncodingIdText);

        Assert.True(
            string.Equals(expected.EncodingId, actual.EncodingIdText, StringComparison.Ordinal),
            $"{where}: expected layout '{expected.EncodingId}', got '{actual.EncodingIdText}'.");

        Assert.True(
            expected.RowCount == actual.RowCount,
            $"{where}: expected {expected.RowCount} rows, got {actual.RowCount}.");

        Assert.True(
            expected.MetadataBytes == actual.Metadata.Length,
            $"{where}: expected {expected.MetadataBytes} metadata bytes, got {actual.Metadata.Length}.");

        ReadOnlySpan<uint> segments = actual.Segments;
        Assert.True(
            expected.SegmentIds.Length == segments.Length,
            $"{where}: expected {expected.SegmentIds.Length} segments, got {segments.Length}.");
        for (int i = 0; i < segments.Length; i++)
        {
            Assert.True(
                expected.SegmentIds[i] == segments[i],
                $"{where}: segment {i} is {segments[i]}, expected {expected.SegmentIds[i]}.");
        }

        SidecarDTypeAssert.Equal(expected.DType, actual.DType, where);

        if (actual.Encoding == LayoutEncodingId.Unknown)
        {
            // Children of an unknown layout are not materialized: their dtypes are not derivable.
            Assert.Equal(0, actual.ChildCount);
            return;
        }

        Assert.True(
            expected.Children.Length == actual.ChildCount,
            $"{where}: expected {expected.Children.Length} children, got {actual.ChildCount}.");

        for (int i = 0; i < actual.ChildCount; i++)
        {
            SidecarLayoutNode child = expected.Children[i];
            string name = child.Name.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : child.Name;
            Compare(child, actual.GetChild(i), where + "/" + name, fileId, seen);
        }
    }
}
