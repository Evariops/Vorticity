using System;

using Vorticity.Arrays;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a <c>vortex.zoned</c> layout, which carries no segments of its own and exactly two children:
/// child 0 holds the data, child 1 one row of aggregates per zone. Only the data child is read here;
/// the zone map is consumed separately, when pruning decides which splits are worth reading at all.
/// </summary>
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
        return ExecuteRowChild(in data, rows, in fields, context);
    }
}
