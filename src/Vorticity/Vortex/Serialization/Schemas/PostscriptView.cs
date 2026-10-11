using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Serialization.Schemas;

/// <summary>Reader for the format's <c>table Postscript</c>.</summary>
/// <remarks>
/// The postscript is the first thing a reader parses, and the format caps its size, so everything
/// here is bounded by construction. Its one file-supplied vector, the user metadata list, is
/// validated eagerly in <see cref="Root"/>: the count is capped, and reading every key there also
/// enforces that each one is present, non-empty, short enough and distinct from the others.
/// </remarks>
internal readonly ref struct PostscriptView
{
    private readonly FlatBufferTable _table;
    private readonly int _metadataCount;

    private PostscriptView(FlatBufferTable table, int metadataCount)
    {
        _table = table;
        _metadataCount = metadataCount;
    }

    /// <summary>Parses <paramref name="buffer"/> as a <c>Postscript</c> root.</summary>
    /// <param name="buffer">The postscript bytes, starting at the root uoffset.</param>
    /// <param name="tableBudget">
    /// Remaining table allowance, seeded with <see cref="VortexLimits.MaxFlatBufferTables"/> and
    /// kept alive by the caller for the whole traversal.
    /// </param>
    /// <exception cref="VortexFormatException">
    /// The buffer is not a well-formed FlatBuffer, or the user metadata list violates the
    /// format's rules on its count and its keys.
    /// </exception>
    public static PostscriptView Root(ReadOnlySpan<byte> buffer, ref int tableBudget)
    {
        FlatBufferTable table = FlatBufferTable.Root(buffer, ref tableBudget);
        return new PostscriptView(table, ValidateMetadata(table));
    }

    /// <summary>True when the optional <c>dtype</c> segment is present.</summary>
    public bool HasDType => _table.HasField(SchemaFieldIds.PostscriptDType);

    /// <summary>The optional <c>dtype</c> segment; <see cref="PostscriptSegmentView.IsNull"/> when absent.</summary>
    public PostscriptSegmentView DType =>
        new(_table.GetTable(SchemaFieldIds.PostscriptDType));

    /// <summary>The required <c>layout</c> segment.</summary>
    /// <exception cref="VortexFormatException">The field is absent.</exception>
    public PostscriptSegmentView Layout => Require(SchemaFieldIds.PostscriptLayout, "layout");

    /// <summary>True when the optional file <c>statistics</c> segment is present.</summary>
    public bool HasStatistics => _table.HasField(SchemaFieldIds.PostscriptStatistics);

    /// <summary>The optional <c>statistics</c> segment; <see cref="PostscriptSegmentView.IsNull"/> when absent.</summary>
    public PostscriptSegmentView Statistics =>
        new(_table.GetTable(SchemaFieldIds.PostscriptStatistics));

    /// <summary>The required <c>footer</c> segment.</summary>
    /// <exception cref="VortexFormatException">The field is absent.</exception>
    public PostscriptSegmentView Footer => Require(SchemaFieldIds.PostscriptFooter, "footer");

    /// <summary>
    /// Number of user metadata segments, already validated to be at most
    /// <see cref="VortexLimits.MaxMetadataSegments"/>.
    /// </summary>
    public int MetadataCount => _metadataCount;

    /// <summary>Reads one user metadata entry.</summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public PostscriptMetadataView GetMetadata(int index) =>
        new(_table.GetVector(SchemaFieldIds.PostscriptMetadata).GetTable(index));

    private PostscriptSegmentView Require(int fieldId, string name)
    {
        FlatBufferTable segment = _table.GetTable(fieldId);
        if (segment.IsNull)
        {
            SchemaThrow.MissingRequired("Postscript", name);
        }

        return new PostscriptSegmentView(segment);
    }

    private static int ValidateMetadata(FlatBufferTable table)
    {
        FlatBufferVector entries = table.GetVector(SchemaFieldIds.PostscriptMetadata);
        int count = entries.Count;
        if (count > VortexLimits.MaxMetadataSegments)
        {
            SchemaThrow.MetadataCount(count);
        }

        // O(n^2) over at most 16 entries and no allocation: a HashSet keyed on file-controlled
        // bytes would allocate and hash untrusted input for 120 comparisons at worst.
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = new PostscriptMetadataView(entries.GetTable(i)).KeyUtf8;
            for (int j = 0; j < i; j++)
            {
                ReadOnlySpan<byte> other = new PostscriptMetadataView(entries.GetTable(j)).KeyUtf8;
                if (key.SequenceEqual(other))
                {
                    SchemaThrow.DuplicateMetadataKey(j, i);
                }
            }
        }

        return count;
    }
}

/// <summary>Reader for the format's <c>table PostscriptMetadata</c>.</summary>
internal readonly ref struct PostscriptMetadataView
{
    private readonly FlatBufferTable _table;

    internal PostscriptMetadataView(FlatBufferTable table) => _table = table;

    /// <summary>The entry's key, as UTF-8 bytes.</summary>
    /// <exception cref="VortexFormatException">
    /// The key is absent, empty, or longer than <see cref="VortexLimits.MaxMetadataKeyLength"/>
    /// bytes. All three make the file malformed rather than the entry skippable.
    /// </exception>
    public ReadOnlySpan<byte> KeyUtf8
    {
        get
        {
            if (!_table.HasField(SchemaFieldIds.PostscriptMetadataKey))
            {
                SchemaThrow.MissingRequired("PostscriptMetadata", "key");
            }

            ReadOnlySpan<byte> key = _table.GetStringUtf8(SchemaFieldIds.PostscriptMetadataKey);
            if (key.IsEmpty)
            {
                SchemaThrow.MetadataKeyEmpty();
            }

            if (key.Length > VortexLimits.MaxMetadataKeyLength)
            {
                SchemaThrow.MetadataKeyTooLong(key.Length);
            }

            return key;
        }
    }

    /// <summary>The entry's segment locator.</summary>
    /// <exception cref="VortexFormatException">The field is absent.</exception>
    public PostscriptSegmentView Segment
    {
        get
        {
            FlatBufferTable segment = _table.GetTable(SchemaFieldIds.PostscriptMetadataSegment);
            if (segment.IsNull)
            {
                SchemaThrow.MissingRequired("PostscriptMetadata", "segment");
            }

            return new PostscriptSegmentView(segment);
        }
    }
}

/// <summary>
/// Reader for the format's <c>table PostscriptSegment</c>.
/// </summary>
/// <remarks>
/// A <em>table</em>, not the 16-byte <see cref="SegmentSpec"/> <em>struct</em>, even though the two
/// carry the same five field names. The postscript spells compression and encryption out inline so
/// a reader can decrypt without first fetching the footer.
/// </remarks>
internal readonly ref struct PostscriptSegmentView
{
    private readonly FlatBufferTable _table;

    internal PostscriptSegmentView(FlatBufferTable table) => _table = table;

    /// <summary>True when the field this view came from was absent.</summary>
    public bool IsNull => _table.IsNull;

    /// <summary>Offset relative to the start of the file. 0 when absent.</summary>
    public ulong Offset => _table.GetUInt64(SchemaFieldIds.PostscriptSegmentOffset);

    /// <summary>Length in bytes. 0 when absent.</summary>
    public uint Length => _table.GetUInt32(SchemaFieldIds.PostscriptSegmentLength);

    /// <summary>
    /// Base-2 exponent of the segment's alignment, straight from the file and unchecked;
    /// <see cref="ToSegmentSpec"/> is where it meets
    /// <see cref="VortexLimits.MaxAlignmentExponent"/>.
    /// </summary>
    public byte AlignmentExponent => _table.GetUInt8(SchemaFieldIds.PostscriptSegmentAlignmentExponent);

    /// <summary>
    /// The inline <c>_compression</c> spec's scheme; <see cref="CompressionScheme.None"/> when the
    /// sub-table is absent. Values outside the enum are returned as read, not rejected.
    /// </summary>
    public CompressionScheme Compression =>
        (CompressionScheme)_table
            .GetTable(SchemaFieldIds.PostscriptSegmentCompression)
            .GetUInt8(SchemaFieldIds.CompressionSpecScheme);

    /// <summary>
    /// Materializes the equivalent <see cref="SegmentSpec"/>, validating the alignment exponent.
    /// </summary>
    /// <remarks>
    /// <see cref="SegmentSpec.Compression"/> and <see cref="SegmentSpec.Encryption"/> are indices
    /// into the footer's spec tables and have no counterpart in a postscript segment, whose specs
    /// are inline; both are set to 0. Read <see cref="Compression"/> for the inline scheme.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The view is null, or the alignment exponent exceeds
    /// <see cref="VortexLimits.MaxAlignmentExponent"/>.
    /// </exception>
    public SegmentSpec ToSegmentSpec()
    {
        if (_table.IsNull)
        {
            SchemaThrow.MissingRequired("Postscript", "segment");
        }

        byte exponent = AlignmentExponent;
        VortexLimits.CheckAlignmentExponent(exponent);
        return new SegmentSpec(Offset, Length, exponent, 0, 0);
    }
}
