// FlatBuffers field ids for every table in spec/flatbuffers/footer.fbs, array.fbs and layout.fbs.
//
// A FlatBuffers field id is its 0-based DECLARATION ORDER in the .fbs, not a written tag, so an
// off-by-one here is completely silent: the vtable slot for the neighbouring field is read instead
// and a value of a compatible width comes back looking plausible. Every constant below was
// transcribed from the .fbs and then re-derived from real bytes by parsing all 819 golden corpus
// files (postscript -> footer -> layout -> array blob) and checking each field against
// corpus/manifest.json (declared_array_ids, declared_layout_ids, layout_ids, row_count,
// array_node_shapes, metadata_segments).
//
// Constants, not an enum: they are indices into a vtable, mixed freely with ints.
namespace Vorticity.Serialization.Schemas;

/// <summary>Field ids, 0-based declaration order, for the Vortex FlatBuffers schemas.</summary>
internal static class SchemaFieldIds
{
    // table Postscript { dtype; layout; statistics; footer; metadata; }   footer.fbs
    internal const int PostscriptDType = 0;
    internal const int PostscriptLayout = 1;
    internal const int PostscriptStatistics = 2;
    internal const int PostscriptFooter = 3;
    internal const int PostscriptMetadata = 4;

    // table PostscriptMetadata { key (required); segment (required); }   footer.fbs
    internal const int PostscriptMetadataKey = 0;
    internal const int PostscriptMetadataSegment = 1;

    // table PostscriptSegment { offset; length; alignment_exponent; _compression; _encryption; }
    // A TABLE, unlike the 16-byte SegmentSpec STRUCT that carries the same five field names.
    internal const int PostscriptSegmentOffset = 0;
    internal const int PostscriptSegmentLength = 1;
    internal const int PostscriptSegmentAlignmentExponent = 2;
    internal const int PostscriptSegmentCompression = 3;
    internal const int PostscriptSegmentEncryption = 4;

    // table FileStatistics { field_stats; }                              footer.fbs
    internal const int FileStatisticsFieldStats = 0;

    // table Footer { array_specs; layout_specs; segment_specs; compression_specs; encryption_specs; }
    internal const int FooterArraySpecs = 0;
    internal const int FooterLayoutSpecs = 1;
    internal const int FooterSegmentSpecs = 2;
    internal const int FooterCompressionSpecs = 3;
    internal const int FooterEncryptionSpecs = 4;

    // table ArraySpec { id (required); }   table LayoutSpec { id (required); }
    internal const int SpecId = 0;

    // table CompressionSpec { scheme; }                                  footer.fbs
    internal const int CompressionSpecScheme = 0;

    // table Array { root; buffers; }                                     array.fbs
    internal const int ArrayRoot = 0;
    internal const int ArrayBuffers = 1;

    // table ArrayNode { encoding; metadata; children; buffers; stats; }  array.fbs
    internal const int ArrayNodeEncoding = 0;
    internal const int ArrayNodeMetadata = 1;
    internal const int ArrayNodeChildren = 2;
    internal const int ArrayNodeBuffers = 3;
    internal const int ArrayNodeStats = 4;

    // table ArrayStats { min; min_precision; max; max_precision; sum; is_sorted; is_strict_sorted;
    //                    is_constant; null_count; uncompressed_size_in_bytes; nan_count; }
    internal const int ArrayStatsMin = 0;
    internal const int ArrayStatsMinPrecision = 1;
    internal const int ArrayStatsMax = 2;
    internal const int ArrayStatsMaxPrecision = 3;
    internal const int ArrayStatsSum = 4;
    internal const int ArrayStatsIsSorted = 5;
    internal const int ArrayStatsIsStrictSorted = 6;
    internal const int ArrayStatsIsConstant = 7;
    internal const int ArrayStatsNullCount = 8;
    internal const int ArrayStatsUncompressedSizeInBytes = 9;
    internal const int ArrayStatsNanCount = 10;

    // table Layout { encoding; row_count; metadata; children; segments; }  layout.fbs
    internal const int LayoutEncoding = 0;
    internal const int LayoutRowCount = 1;
    internal const int LayoutMetadata = 2;
    internal const int LayoutChildren = 3;
    internal const int LayoutSegments = 4;
}
