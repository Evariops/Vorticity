using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Reader for <c>table Layout</c> in spec/flatbuffers/layout.fbs.</summary>
/// <remarks>
/// The layout tree comes from the file and two parents may legally share one child, so its depth
/// bounds nothing: <see cref="Root"/> carries a table budget that the whole walk spends from.
/// </remarks>
public readonly ref struct LayoutView
{
    private readonly FlatBufferTable _table;

    internal LayoutView(FlatBufferTable table) => _table = table;

    /// <summary>Parses <paramref name="buffer"/> as a <c>Layout</c> root.</summary>
    /// <param name="buffer">The layout segment's bytes, starting at the root uoffset.</param>
    /// <param name="tableBudget">
    /// Remaining table allowance, seeded with <see cref="VortexLimits.MaxFlatBufferTables"/> and
    /// kept alive for the whole walk of the layout tree.
    /// </param>
    /// <exception cref="VortexFormatException">The buffer is not a well-formed FlatBuffer.</exception>
    public static LayoutView Root(ReadOnlySpan<byte> buffer, ref int tableBudget) =>
        new(FlatBufferTable.Root(buffer, ref tableBudget));

    /// <summary>True when the field this view came from was absent.</summary>
    public bool IsNull => _table.IsNull;

    /// <summary>Index into <c>Footer.layout_specs</c>. 0 when absent.</summary>
    public ushort Encoding => _table.GetUInt16(SchemaFieldIds.LayoutEncoding);

    /// <summary>
    /// Rows represented by this layout, returned exactly as the file states it. It is
    /// attacker-controlled: narrowing it to an <c>int</c> and range-checking it belongs to the
    /// consumer, once, at the layout level.
    /// </summary>
    public ulong RowCount => _table.GetUInt64(SchemaFieldIds.LayoutRowCount);

    /// <summary>The layout-specific metadata, opaque at this layer. Absent means empty.</summary>
    public ReadOnlySpan<byte> Metadata => _table.GetByteVector(SchemaFieldIds.LayoutMetadata);

    /// <summary>Number of child layouts. Absent means zero.</summary>
    public int ChildCount => _table.GetVector(SchemaFieldIds.LayoutChildren).Count;

    /// <summary>Reads one child layout.</summary>
    /// <param name="index">0-based index, below <see cref="ChildCount"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, the child is malformed, or the traversal has exhausted its table
    /// budget or exceeded <see cref="VortexLimits.MaxFlatBufferDepth"/>.
    /// </exception>
    public LayoutView GetChild(int index) =>
        new(_table.GetVector(SchemaFieldIds.LayoutChildren).GetTable(index));

    /// <summary>
    /// Identifiers of the <c>SegmentSpec</c>s this layout needs, reinterpreted in place. Absent
    /// means empty.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The elements escape the buffer or are not 4-byte aligned.
    /// </exception>
    public ReadOnlySpan<uint> Segments =>
        _table.GetStructVector<uint>(SchemaFieldIds.LayoutSegments);
}
