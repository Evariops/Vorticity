using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Reader for the format's <c>table Array</c>.</summary>
/// <remarks>
/// The array blob's FlatBuffer is finished without a file identifier, so nothing here looks for
/// one at offset 4. Its node tree is a directed acyclic graph rather than a tree — forward-only
/// uoffsets exclude cycles, but two parents may legally share one child table — so depth alone
/// cannot bound a walk of it and <see cref="Root"/> carries a table budget instead.
/// </remarks>
internal readonly ref struct ArrayView
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly FlatBufferTable _table;

    private ArrayView(ReadOnlySpan<byte> buffer, FlatBufferTable table)
    {
        _buffer = buffer;
        _table = table;
    }

    /// <summary>Parses <paramref name="buffer"/> as an <c>Array</c> root.</summary>
    /// <param name="buffer">
    /// The array blob's FlatBuffer region — <c>[len-4-fb_length, len-4)</c> of the segment —
    /// starting at its root uoffset.
    /// </param>
    /// <param name="tableBudget">
    /// Remaining table allowance, seeded with <see cref="VortexLimits.MaxFlatBufferTables"/> and
    /// kept alive for the whole walk of the node tree.
    /// </param>
    /// <exception cref="VortexFormatException">The buffer is not a well-formed FlatBuffer.</exception>
    public static ArrayView Root(ReadOnlySpan<byte> buffer, ref int tableBudget) =>
        new(buffer, FlatBufferTable.Root(buffer, ref tableBudget));

    /// <summary>
    /// The root array node. Named with a trailing underscore because <see cref="Root"/> is the
    /// static factory; the wire field is <c>root</c>.
    /// </summary>
    /// <exception cref="VortexFormatException">The field is absent.</exception>
    public ArrayNodeView Root_
    {
        get
        {
            FlatBufferTable node = _table.GetTable(SchemaFieldIds.ArrayRoot);
            if (node.IsNull)
            {
                SchemaThrow.MissingRequired("Array", "root");
            }

            return new ArrayNodeView(node);
        }
    }

    /// <summary>
    /// The blob's buffer descriptors, reinterpreted in place. Absent means empty.
    /// </summary>
    /// <remarks>
    /// <c>Buffer</c>'s natural alignment is 4, not 8 — its widest member is a <c>uint32</c> — so
    /// the elements are required to start on a 4-byte boundary inside the blob and nothing more.
    /// Demanding 8 would reject most of the array blobs a conforming writer produces.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The elements escape the buffer or are not 4-byte aligned inside it.
    /// </exception>
    public ReadOnlySpan<BufferSpec> Buffers
    {
        get
        {
            ReadOnlySpan<BufferSpec> buffers =
                _table.GetStructVector<BufferSpec>(SchemaFieldIds.ArrayBuffers);
            if (!buffers.IsEmpty)
            {
                long offset = SchemaThrow.InBufferOffset(_buffer, buffers);
                if ((offset & 3) != 0)
                {
                    SchemaThrow.UnalignedStructVector("Array.buffers", offset, 4);
                }
            }

            return buffers;
        }
    }
}

/// <summary>Reader for the format's <c>table ArrayNode</c>.</summary>
/// <remarks>
/// An <c>ArrayNode</c> carries neither its DType nor its length; both are supplied top-down by the
/// parent.
/// </remarks>
internal readonly ref struct ArrayNodeView
{
    private readonly FlatBufferTable _table;

    internal ArrayNodeView(FlatBufferTable table) => _table = table;

    /// <summary>True when the field this view came from was absent.</summary>
    public bool IsNull => _table.IsNull;

    /// <summary>Index into <c>Footer.array_specs</c>. 0 when absent.</summary>
    public ushort Encoding => _table.GetUInt16(SchemaFieldIds.ArrayNodeEncoding);

    /// <summary>
    /// The encoding-specific Protobuf metadata, opaque at this layer. Absent means empty; so does
    /// a present but zero-length vector, and the two are indistinguishable through this reader.
    /// </summary>
    public ReadOnlySpan<byte> Metadata => _table.GetByteVector(SchemaFieldIds.ArrayNodeMetadata);

    /// <summary>Number of child nodes. Absent means zero.</summary>
    public int ChildCount => _table.GetVector(SchemaFieldIds.ArrayNodeChildren).Count;

    /// <summary>Reads one child node.</summary>
    /// <param name="index">0-based index, below <see cref="ChildCount"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, the child is malformed, or the traversal has exhausted its table
    /// budget or exceeded <see cref="VortexLimits.MaxFlatBufferDepth"/>.
    /// </exception>
    public ArrayNodeView GetChild(int index) =>
        new(_table.GetVector(SchemaFieldIds.ArrayNodeChildren).GetTable(index));

    /// <summary>Number of buffer indices this node claims. Absent means zero.</summary>
    public int BufferIndexCount => BufferIndices.Length;

    /// <summary>
    /// Indices into <c>Array.buffers</c>, reinterpreted in place. Absent means empty.
    /// </summary>
    /// <remarks>
    /// Their address is what is tested, so the view is read over a buffer whose base is aligned as
    /// its FlatBuffer: the blob reader parses the arena's copy, never the segment in place.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The elements escape the buffer or are not 2-byte aligned.
    /// </exception>
    public ReadOnlySpan<ushort> BufferIndices =>
        _table.GetStructVector<ushort>(SchemaFieldIds.ArrayNodeBuffers);

    /// <summary>True when the optional <c>stats</c> sub-table is present.</summary>
    public bool HasStats => _table.HasField(SchemaFieldIds.ArrayNodeStats);

    /// <summary>The node's statistics; <see cref="ArrayStatsView.IsNull"/> when absent.</summary>
    public ArrayStatsView Stats => new(_table.GetTable(SchemaFieldIds.ArrayNodeStats));
}

/// <summary>Reader for the format's <c>table ArrayStats</c>.</summary>
/// <remarks>
/// Six of the eleven fields are FlatBuffers <c>= null</c> fields: <em>absent means unknown, not
/// <see langword="false"/> or 0</em>. They are exposed only through the <c>TryGet</c> accessors so
/// a defaulting read is not expressible.
/// </remarks>
internal readonly ref struct ArrayStatsView
{
    private readonly FlatBufferTable _table;

    internal ArrayStatsView(FlatBufferTable table) => _table = table;

    /// <summary>True when the field this view came from was absent.</summary>
    public bool IsNull => _table.IsNull;

    /// <summary>The minimum as a Protobuf-serialized <c>ScalarValue</c>. Absent means empty.</summary>
    public ReadOnlySpan<byte> MinBytes => _table.GetByteVector(SchemaFieldIds.ArrayStatsMin);

    /// <summary>Exactness of <see cref="MinBytes"/>. Defaults to <see cref="StatPrecision.Inexact"/>.</summary>
    public StatPrecision MinPrecision =>
        (StatPrecision)_table.GetUInt8(SchemaFieldIds.ArrayStatsMinPrecision);

    /// <summary>The maximum as a Protobuf-serialized <c>ScalarValue</c>. Absent means empty.</summary>
    public ReadOnlySpan<byte> MaxBytes => _table.GetByteVector(SchemaFieldIds.ArrayStatsMax);

    /// <summary>Exactness of <see cref="MaxBytes"/>. Defaults to <see cref="StatPrecision.Inexact"/>.</summary>
    public StatPrecision MaxPrecision =>
        (StatPrecision)_table.GetUInt8(SchemaFieldIds.ArrayStatsMaxPrecision);

    /// <summary>The sum as a Protobuf-serialized <c>ScalarValue</c>. Absent means empty.</summary>
    public ReadOnlySpan<byte> SumBytes => _table.GetByteVector(SchemaFieldIds.ArrayStatsSum);

    /// <summary>Reads the tri-state <c>is_sorted</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsSorted(out bool value) =>
        _table.TryGetBool(SchemaFieldIds.ArrayStatsIsSorted, out value);

    /// <summary>Reads the tri-state <c>is_strict_sorted</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsStrictSorted(out bool value) =>
        _table.TryGetBool(SchemaFieldIds.ArrayStatsIsStrictSorted, out value);

    /// <summary>Reads the tri-state <c>is_constant</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsConstant(out bool value) =>
        _table.TryGetBool(SchemaFieldIds.ArrayStatsIsConstant, out value);

    /// <summary>Reads the tri-state <c>null_count</c>. A present 0 is not the same as absent.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetNullCount(out ulong value) =>
        _table.TryGetUInt64(SchemaFieldIds.ArrayStatsNullCount, out value);

    /// <summary>Reads the tri-state <c>uncompressed_size_in_bytes</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetUncompressedSizeInBytes(out ulong value) =>
        _table.TryGetUInt64(SchemaFieldIds.ArrayStatsUncompressedSizeInBytes, out value);

    /// <summary>Reads the tri-state <c>nan_count</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetNanCount(out ulong value) =>
        _table.TryGetUInt64(SchemaFieldIds.ArrayStatsNanCount, out value);
}
