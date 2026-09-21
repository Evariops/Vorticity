// WHERE in a file a component id is used, which turns out to matter.
//
// The manifest's `array_ids` pools every encoding a file contains, wherever it sits. Four corpus
// files (types/decimal18_4_{nonnull,nullable}_r8193 and types/timestamp_ms_{nonnull,nullable}_r8193)
// use an encoding - vortex.decimal_byte_parts, vortex.datetimeparts - ONLY inside the zone map of
// their vortex.zoned layout. That distinction is a property of the READ PATH, not of the decoder
// table: ZonedLayoutReader never reads the zones child, which only pruning reads, through its own
// path.
//
// So "out of scope" splits in two, and the acceptance criterion - "fails with a named component
// rather than a wrong answer" - is met by both halves:
//
//   * unsupported on the DATA path  -> the scan must throw, naming the id and the kind;
//   * unsupported only in a ZONE MAP -> the scan must succeed AND return the right values, which is
//     then checked against the sidecar exactly as an in-scope file would be.
//
// This index is what tells the two apart, and it reads the sidecar's own layout tree to do it
// rather than assuming.
using System;
using System.Collections.Generic;
using Vorticity.Conformance.Sidecar;

namespace Vorticity.Conformance.Comparison;

/// <summary>The component ids of one file, split by whether an unfiltered scan reaches them.</summary>
internal sealed class LayoutComponentIndex
{
    private LayoutComponentIndex(HashSet<string> data, HashSet<string> zoneMapOnly)
    {
        OnTheDataPath = data;
        InZoneMapsOnly = zoneMapOnly;
    }

    /// <summary>Ids an unfiltered scan decodes: layout ids and array ids on the data path.</summary>
    internal HashSet<string> OnTheDataPath { get; }

    /// <summary>Ids that appear only inside a zone map, which an unfiltered scan never decodes.</summary>
    internal HashSet<string> InZoneMapsOnly { get; }

    /// <summary>Builds the index from a sidecar's `layout` line.</summary>
    /// <param name="layoutTree">The `tree` member of the layout line.</param>
    internal static LayoutComponentIndex Build(JsonValue layoutTree)
    {
        HashSet<string> data = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> zones = new HashSet<string>(StringComparer.Ordinal);
        Walk(layoutTree, inZoneMap: false, data, zones);
        zones.ExceptWith(data);
        return new LayoutComponentIndex(data, zones);
    }

    private static void Walk(JsonValue node, bool inZoneMap, HashSet<string> data, HashSet<string> zones)
    {
        if (node.Kind != JsonKind.Object)
        {
            return;
        }

        HashSet<string> sink = inZoneMap ? zones : data;

        if (node.Find("encoding_id") is JsonValue encoding && encoding.Kind == JsonKind.String)
        {
            sink.Add(encoding.Text);
        }

        if (node.Find("array_tree") is JsonValue tree && tree.Kind == JsonKind.Object)
        {
            CollectArrayIds(tree, sink);
        }

        JsonValue? children = node.Find("children");
        if (children is null || children.Kind != JsonKind.Array)
        {
            return;
        }

        bool zoned = node.Find("encoding_id")?.Text is "vortex.zoned" or "vortex.stats";
        for (int i = 0; i < children.Items.Length; i++)
        {
            JsonValue child = children.Items[i];

            // A zoned layout is [data, zones]; the sidecar names them, and the index is the
            // fallback for a writer that leaves the name empty.
            bool childInZoneMap = inZoneMap ||
                (zoned && (i >= 1 || string.Equals(child.Find("name")?.Text, "zones", StringComparison.Ordinal)));

            Walk(child, childInZoneMap, data, zones);
        }
    }

    private static void CollectArrayIds(JsonValue node, HashSet<string> sink)
    {
        if (node.Find("id") is JsonValue id && id.Kind == JsonKind.String)
        {
            sink.Add(id.Text);
        }

        JsonValue? children = node.Find("children");
        if (children is null || children.Kind != JsonKind.Array)
        {
            return;
        }

        foreach (JsonValue child in children.Items)
        {
            CollectArrayIds(child, sink);
        }
    }
}
