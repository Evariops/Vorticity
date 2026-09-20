using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Reader for <c>table Footer</c> in spec/flatbuffers/footer.fbs.</summary>
/// <remarks>
/// The footer is a dictionary, not a tree: five flat vectors. Only <c>segment_specs</c> is
/// reinterpreted in place; the others are walked one element at a time and never allocate.
/// </remarks>
public readonly ref struct FooterView
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly FlatBufferTable _table;

    private FooterView(ReadOnlySpan<byte> buffer, FlatBufferTable table)
    {
        _buffer = buffer;
        _table = table;
    }

    /// <summary>Parses <paramref name="buffer"/> as a <c>Footer</c> root.</summary>
    /// <param name="buffer">The footer segment's bytes, starting at the root uoffset.</param>
    /// <param name="tableBudget">
    /// Remaining table allowance, seeded with <see cref="VortexLimits.MaxFlatBufferTables"/>.
    /// </param>
    /// <exception cref="VortexFormatException">The buffer is not a well-formed FlatBuffer.</exception>
    public static FooterView Root(ReadOnlySpan<byte> buffer, ref int tableBudget) =>
        new(buffer, FlatBufferTable.Root(buffer, ref tableBudget));

    /// <summary>
    /// Number of entries in <c>array_specs</c>, the dictionary the <c>u16</c> in
    /// <c>ArrayNode.encoding</c> indexes.
    /// </summary>
    /// <remarks>
    /// Absent means empty. The vector is deliberately <em>not</em> checked for uniqueness, count or
    /// registration here: the writer pre-populates it with every id the enabled editions permit for
    /// byte determinism, so it routinely names ids no array in the file uses.
    /// </remarks>
    public int ArraySpecCount => _table.GetVector(SchemaFieldIds.FooterArraySpecs).Count;

    /// <summary>Reads the UTF-8 id of one <c>ArraySpec</c>.</summary>
    /// <param name="index">0-based index, below <see cref="ArraySpecCount"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, or the spec is missing its required <c>id</c>.
    /// </exception>
    public ReadOnlySpan<byte> GetArraySpecIdUtf8(int index) =>
        SpecId(SchemaFieldIds.FooterArraySpecs, index, "ArraySpec");

    /// <summary>
    /// Number of entries in <c>layout_specs</c>, the dictionary the <c>u16</c> in
    /// <c>Layout.encoding</c> indexes. Absent means empty.
    /// </summary>
    public int LayoutSpecCount => _table.GetVector(SchemaFieldIds.FooterLayoutSpecs).Count;

    /// <summary>Reads the UTF-8 id of one <c>LayoutSpec</c>.</summary>
    /// <param name="index">0-based index, below <see cref="LayoutSpecCount"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, or the spec is missing its required <c>id</c>.
    /// </exception>
    public ReadOnlySpan<byte> GetLayoutSpecIdUtf8(int index) =>
        SpecId(SchemaFieldIds.FooterLayoutSpecs, index, "LayoutSpec");

    /// <summary>
    /// The segment map, reinterpreted in place with no copy and no traversal.
    /// </summary>
    /// <remarks>
    /// Unlike every other vector in the footer, an absent <c>segment_specs</c> is an error rather
    /// than an empty vector: a file layout with no segment map cannot be read at all. The elements
    /// must also begin on an 8-byte boundary <em>inside the footer buffer</em>, which is what
    /// FlatBuffers guarantees for a struct whose widest member is a <c>uint64</c>.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The field is absent, the elements escape the buffer, or they are not 8-byte aligned.
    /// </exception>
    public ReadOnlySpan<SegmentSpec> SegmentSpecs
    {
        get
        {
            if (!_table.HasField(SchemaFieldIds.FooterSegmentSpecs))
            {
                SchemaThrow.MissingSegmentSpecs();
            }

            ReadOnlySpan<SegmentSpec> specs =
                _table.GetStructVector<SegmentSpec>(SchemaFieldIds.FooterSegmentSpecs);
            if (!specs.IsEmpty)
            {
                long offset = SchemaThrow.InBufferOffset(_buffer, specs);
                if ((offset & 7) != 0)
                {
                    SchemaThrow.UnalignedStructVector("Footer.segment_specs", offset, 8);
                }
            }

            return specs;
        }
    }

    /// <summary>Number of entries in <c>compression_specs</c>. Absent means empty.</summary>
    /// <exception cref="VortexFormatException">
    /// The count exceeds <see cref="VortexLimits.MaxCompressionSpecs"/>, the ceiling
    /// spec/flatbuffers/footer.fbs states for this vector.
    /// </exception>
    public int CompressionSpecCount
    {
        get
        {
            int count = _table.GetVector(SchemaFieldIds.FooterCompressionSpecs).Count;
            if (count > VortexLimits.MaxCompressionSpecs)
            {
                SchemaThrow.CompressionSpecCount(count);
            }

            return count;
        }
    }

    /// <summary>Reads one compression spec's scheme.</summary>
    /// <param name="index">0-based index, below <see cref="CompressionSpecCount"/>.</param>
    /// <remarks>A value outside <see cref="CompressionScheme"/> is returned as read, not rejected.</remarks>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public CompressionScheme GetCompressionScheme(int index) =>
        (CompressionScheme)_table
            .GetVector(SchemaFieldIds.FooterCompressionSpecs)
            .GetTable(index)
            .GetUInt8(SchemaFieldIds.CompressionSpecScheme);

    /// <summary>
    /// Number of entries in <c>encryption_specs</c>. Absent means empty. <c>EncryptionSpec</c> is
    /// an empty table in v1, so there is nothing else to read from one.
    /// </summary>
    public int EncryptionSpecCount => _table.GetVector(SchemaFieldIds.FooterEncryptionSpecs).Count;

    private ReadOnlySpan<byte> SpecId(int fieldId, int index, string table)
    {
        FlatBufferTable spec = _table.GetVector(fieldId).GetTable(index);
        if (!spec.HasField(SchemaFieldIds.SpecId))
        {
            SchemaThrow.MissingRequired(table, "id");
        }

        return spec.GetStringUtf8(SchemaFieldIds.SpecId);
    }
}

/// <summary>Reader for <c>table FileStatistics</c> in spec/flatbuffers/footer.fbs.</summary>
public readonly ref struct FileStatisticsView
{
    private readonly FlatBufferTable _table;

    private FileStatisticsView(FlatBufferTable table) => _table = table;

    /// <summary>Parses <paramref name="buffer"/> as a <c>FileStatistics</c> root.</summary>
    /// <param name="buffer">The statistics segment's bytes, starting at the root uoffset.</param>
    /// <param name="tableBudget">
    /// Remaining table allowance, seeded with <see cref="VortexLimits.MaxFlatBufferTables"/>.
    /// </param>
    /// <exception cref="VortexFormatException">The buffer is not a well-formed FlatBuffer.</exception>
    public static FileStatisticsView Root(ReadOnlySpan<byte> buffer, ref int tableBudget) =>
        new(FlatBufferTable.Root(buffer, ref tableBudget));

    /// <summary>
    /// Number of per-field statistics entries. One per field of a struct root schema, otherwise
    /// one entry in total (spec/flatbuffers/footer.fbs). Absent means empty.
    /// </summary>
    public int FieldStatsCount => _table.GetVector(SchemaFieldIds.FileStatisticsFieldStats).Count;

    /// <summary>Reads one field's statistics.</summary>
    /// <param name="index">0-based index, below <see cref="FieldStatsCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public ArrayStatsView GetFieldStats(int index) =>
        new(_table.GetVector(SchemaFieldIds.FileStatisticsFieldStats).GetTable(index));
}
