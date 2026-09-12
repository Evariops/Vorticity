// vortex.zoned - vortex-layout-0.86.1/src/layouts/zoned/{mod.rs,reader.rs}. Zero segments, exactly
// two children: 0 = data (the node's own dtype), 1 = zones (one row of aggregates per zone).
//
// PHASE 1 DOES NO PRUNING (docs/01-scope.md §3). The zone map's SHAPE is parsed and exposed through
// LayoutNode.TryGetZoneMap; the zones child is never read, because nothing consumes it and reading
// it would be I/O spent on a pruning pass that does not exist yet. Reading the data child alone is
// exactly what upstream does when zone_len == 0 or an aggregate cannot be resolved, so this is a
// path upstream already takes, not a new one.
using System;

using Vorticity.Arrays;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.zoned</c> layout by reading its data child; the zone map only informs.</summary>
public sealed class ZonedLayoutReader : LayoutReader
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly ZonedLayoutReader Instance = new ZonedLayoutReader();

    private ZonedLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Zoned;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.zoned"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        LayoutNode data = node.GetChild(0);
        RegisterChild(in data, rows, in fields, segments);
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        // The row range reaches the data child unchanged: a zoned layout re-partitions nothing.
        LayoutNode data = node.GetChild(0);
        return ExecuteChild(in data, rows, in fields, context);
    }
}
