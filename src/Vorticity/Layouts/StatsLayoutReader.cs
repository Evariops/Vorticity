// vortex.stats - the legacy ancestor of vortex.zoned, and the only zone map in editions
// core2025.05.0 through core2025.10.0 (spec/editions/*.toml). Read by the same machinery upstream:
// LegacyStats and Zoned share ZonedData and ZonedReader
// (vortex-layout-0.86.1/src/layouts/zoned/mod.rs).
//
// STRUCTURAL ONLY, per PHASE1-CONTRACTS.md §2.7: it resolves its data child and reads correctly,
// ZoneMap.IsPruningAvailable is always false, and its legacy metadata is parsed best-effort. No
// 0.86.1 writer path constructs one, so there is no fixture; deferring it entirely would instead
// make every pre-2026 file with statistics unreadable, because the layout sits ON THE PATH to the
// data.
using System;

using Vorticity.Arrays;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>Reads a legacy <c>vortex.stats</c> layout by reading its data child.</summary>
public sealed class StatsLayoutReader : LayoutReader
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
