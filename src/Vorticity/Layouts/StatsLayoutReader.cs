using System;

using Vorticity.Arrays;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a legacy <c>vortex.stats</c> layout, the ancestor of <c>vortex.zoned</c>, by resolving its
/// data child.
/// </summary>
/// <remarks>
/// Support is structural only: the layer reads correctly, its zone map never offers pruning, and
/// its metadata is parsed best-effort. No current writer emits one, but the layout sits on the path
/// to the data in older files, so skipping it would make every one of them unreadable.
/// </remarks>
internal sealed class StatsLayoutReader : LayoutReader
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly StatsLayoutReader Instance = new StatsLayoutReader();

    private StatsLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Stats;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.stats"u8;

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

        LayoutNode data = node.GetChild(0);
        return ExecuteChild(in data, rows, in fields, context);
    }
}
