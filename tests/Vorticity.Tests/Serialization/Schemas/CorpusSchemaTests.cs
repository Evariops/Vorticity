// The accessors, pointed at reality: all 819 golden files written by Vortex 0.86.1, cross-checked
// against what tests/Vorticity.Conformance/corpus/manifest.json records about each of them.
//
// This is the test that catches a transcribed field id being off by one. A wrong id reads a
// neighbouring slot and returns a plausible number, so agreeing with our own writer proves
// nothing; agreeing with 819 files another implementation wrote does.
//
// Each of these has an independent record in the manifest and is asserted below:
//   postscript_bytes, size_bytes, file_format_version   -> the EOF marker
//   has_dtype_segment, has_file_statistics              -> Postscript.dtype / .statistics presence
//   metadata_segments[].key / .len                      -> PostscriptMetadata + PostscriptSegment
//   declared_layout_ids, layout_ids                     -> Footer.layout_specs
//   declared_array_ids                                  -> Footer.array_specs
//   row_count                                           -> Layout.row_count
//   array_node_shapes                                   -> ArrayNode.encoding/.children/.buffers
//                                                          resolved through Footer.array_specs
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vorticity;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class CorpusSchemaTests
{
    private const int EofSize = 8;
    private static ReadOnlySpan<byte> Magic => "VTXF"u8;

    [Fact]
    public void Every_corpus_file_agrees_with_the_manifest()
    {
        string corpus = CorpusDirectory();
        using JsonDocument manifest = JsonDocument.Parse(global::System.IO.File.ReadAllBytes(Path.Combine(corpus, "manifest.json")));
        JsonElement files = manifest.RootElement.GetProperty("files");

        int checkedFiles = 0;
        int checkedSegments = 0;
        int checkedStats = 0;
        foreach (JsonElement entry in files.EnumerateArray())
        {
            string id = entry.GetProperty("id").GetString()!;
            byte[] data = global::System.IO.File.ReadAllBytes(Path.Combine(corpus, entry.GetProperty("path").GetString()!));
            checkedSegments += CheckOneFile(id, data, entry, ref checkedStats);
            checkedFiles++;
        }

        // A path failure that quietly found nothing would otherwise pass every assertion above.
        Assert.Equal(manifest.RootElement.GetProperty("coverage").GetProperty("files").GetInt32(), checkedFiles);
        Assert.Equal(files.GetArrayLength(), checkedFiles);
        Assert.True(checkedSegments > 2000, $"only {checkedSegments} segments walked");
        Assert.True(checkedStats > 5000, $"only {checkedStats} array stats tables read");
    }

    private static int CheckOneFile(string id, byte[] data, JsonElement entry, ref int statsSeen)
    {
        Assert.Equal(entry.GetProperty("size_bytes").GetInt32(), data.Length);
        Assert.True(data.AsSpan(0, 4).SequenceEqual(Magic), $"{id}: missing leading magic");
        Assert.True(data.AsSpan(data.Length - 4, 4).SequenceEqual(Magic), $"{id}: missing trailing magic");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - EofSize, 2));
        ushort postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - EofSize + 2, 2));
        Assert.Equal(entry.GetProperty("file_format_version").GetInt32(), version);
        Assert.Equal(entry.GetProperty("postscript_bytes").GetInt32(), postscriptLength);
        Assert.True(postscriptLength <= VortexLimits.MaxPostscriptSize, $"{id}: postscript over the cap");

        // ---- postscript -------------------------------------------------------------------
        SegmentSpec dtypeSpec = default;
        SegmentSpec layoutSpec;
        SegmentSpec statisticsSpec = default;
        SegmentSpec footerSpec;
        bool hasStatistics;
        {
            using AlignedBytes bytes = AlignedBytes.Copy(
                data.AsSpan(data.Length - EofSize - postscriptLength, postscriptLength));
            int budget = VortexLimits.MaxFlatBufferTables;
            PostscriptView ps = PostscriptView.Root(bytes.Span, ref budget);

            Assert.Equal(entry.GetProperty("has_dtype_segment").GetBoolean(), ps.HasDType);
            hasStatistics = ps.HasStatistics;
            Assert.Equal(entry.GetProperty("has_file_statistics").GetBoolean(), hasStatistics);

            if (ps.HasDType)
            {
                dtypeSpec = ps.DType.ToSegmentSpec();
            }

            layoutSpec = ps.Layout.ToSegmentSpec();
            if (hasStatistics)
            {
                statisticsSpec = ps.Statistics.ToSegmentSpec();
            }

            footerSpec = ps.Footer.ToSegmentSpec();

            JsonElement metadata = entry.GetProperty("metadata_segments");
            Assert.Equal(metadata.GetArrayLength(), ps.MetadataCount);
            for (int i = 0; i < ps.MetadataCount; i++)
            {
                PostscriptMetadataView slot = ps.GetMetadata(i);
                Assert.Equal(
                    metadata[i].GetProperty("key").GetString(),
                    Encoding.UTF8.GetString(slot.KeyUtf8));
                Assert.Equal((uint)metadata[i].GetProperty("len").GetInt32(), slot.Segment.Length);
            }
        }

        Assert.True(EndsInsideFile(layoutSpec, data.Length), $"{id}: layout segment escapes the file");
        Assert.True(EndsInsideFile(footerSpec, data.Length), $"{id}: footer segment escapes the file");
        if (dtypeSpec.Length != 0)
        {
            Assert.True(EndsInsideFile(dtypeSpec, data.Length), $"{id}: dtype segment escapes the file");
        }

        // ---- footer -----------------------------------------------------------------------
        string[] arrayIds;
        SegmentSpec[] segments;
        {
            using AlignedBytes bytes = AlignedBytes.Copy(
                data.AsSpan((int)footerSpec.Offset, (int)footerSpec.Length));
            int budget = VortexLimits.MaxFlatBufferTables;
            FooterView footer = FooterView.Root(bytes.Span, ref budget);

            // The manifest records 0 declared array ids for the 107 zero-row files - the writer
            // still pre-populates the dictionary, but the generator only counts it once an array
            // has been walked ("no encoding is claimed"). Everywhere else the counts must agree.
            int declaredArrays = entry.GetProperty("declared_array_ids").GetInt32();
            long rowCount = entry.GetProperty("row_count").GetInt64();
            if (rowCount > 0)
            {
                Assert.Equal(declaredArrays, footer.ArraySpecCount);
            }

            arrayIds = new string[footer.ArraySpecCount];
            for (int i = 0; i < arrayIds.Length; i++)
            {
                arrayIds[i] = Encoding.UTF8.GetString(footer.GetArraySpecIdUtf8(i));
                Assert.NotEqual(string.Empty, arrayIds[i]);
            }

            Assert.Equal(entry.GetProperty("declared_layout_ids").GetInt32(), footer.LayoutSpecCount);
            string[] layoutIds = new string[footer.LayoutSpecCount];
            for (int i = 0; i < layoutIds.Length; i++)
            {
                layoutIds[i] = Encoding.UTF8.GetString(footer.GetLayoutSpecIdUtf8(i));
            }

            Array.Sort(layoutIds, StringComparer.Ordinal);
            JsonElement expectedLayoutIds = entry.GetProperty("layout_ids");
            Assert.Equal(expectedLayoutIds.GetArrayLength(), layoutIds.Length);
            for (int i = 0; i < layoutIds.Length; i++)
            {
                Assert.Equal(expectedLayoutIds[i].GetString(), layoutIds[i]);
            }

            ReadOnlySpan<SegmentSpec> specs = footer.SegmentSpecs;
            segments = specs.ToArray();

            // The reference reader rejects an unordered segment map ("Segment offsets are not
            // ordered"); every corpus file satisfies it.
            for (int i = 1; i < segments.Length; i++)
            {
                Assert.True(segments[i - 1].Offset <= segments[i].Offset, $"{id}: segments unordered");
            }

            Assert.Equal(0, footer.CompressionSpecCount);
            Assert.Equal(0, footer.EncryptionSpecCount);
        }

        // ---- layout -----------------------------------------------------------------------
        {
            using AlignedBytes bytes = AlignedBytes.Copy(
                data.AsSpan((int)layoutSpec.Offset, (int)layoutSpec.Length));
            int budget = VortexLimits.MaxFlatBufferTables;
            LayoutView layout = LayoutView.Root(bytes.Span, ref budget);
            Assert.Equal((ulong)entry.GetProperty("row_count").GetInt64(), layout.RowCount);
            CheckLayoutSegments(id, layout, segments.Length);
        }

        // ---- file statistics --------------------------------------------------------------
        if (hasStatistics)
        {
            using AlignedBytes bytes = AlignedBytes.Copy(
                data.AsSpan((int)statisticsSpec.Offset, (int)statisticsSpec.Length));
            int budget = VortexLimits.MaxFlatBufferTables;
            FileStatisticsView stats = FileStatisticsView.Root(bytes.Span, ref budget);
            Assert.True(stats.FieldStatsCount > 0, $"{id}: statistics segment with no field stats");
            for (int i = 0; i < stats.FieldStatsCount; i++)
            {
                ArrayStatsView field = stats.GetFieldStats(i);
                Assert.False(field.IsNull);
                _ = field.MinBytes.Length;
                _ = field.MaxBytes.Length;
                _ = field.TryGetNullCount(out _);
            }
        }

        // ---- every array blob ---------------------------------------------------------------
        SortedSet<string> shapes = new(StringComparer.Ordinal);
        for (int i = 0; i < segments.Length; i++)
        {
            SegmentSpec spec = segments[i];
            Assert.True(EndsInsideFile(spec, data.Length), $"{id}: segment {i} escapes the file");
            ReadOnlySpan<byte> segment = data.AsSpan((int)spec.Offset, (int)spec.Length);

            // The format puts the FlatBuffer's length in the blob's last 4 bytes.
            Assert.True(segment.Length >= 4, $"{id}: segment {i} is too short for a blob");
            uint flatBufferLength = BinaryPrimitives.ReadUInt32LittleEndian(segment[^4..]);
            Assert.True(
                flatBufferLength <= (uint)segment.Length - 4,
                $"{id}: segment {i} declares a {flatBufferLength}-byte FlatBuffer");

            using AlignedBytes blob = AlignedBytes.Copy(
                segment.Slice(segment.Length - 4 - (int)flatBufferLength, (int)flatBufferLength));
            int budget = VortexLimits.MaxFlatBufferTables;
            ArrayView array = ArrayView.Root(blob.Span, ref budget);
            ReadOnlySpan<BufferSpec> buffers = array.Buffers;
            CollectShapes(
                array.Root_, arrayIds, buffers.Length, shapes, 0,
                entry.GetProperty("row_count").GetUInt64(), ref statsSeen);
        }

        JsonElement expectedShapes = entry.GetProperty("array_node_shapes");
        Assert.Equal(expectedShapes.GetArrayLength(), shapes.Count);
        int shapeIndex = 0;
        foreach (string shape in shapes)
        {
            Assert.Equal(expectedShapes[shapeIndex].GetString(), shape);
            shapeIndex++;
        }

        return segments.Length;
    }

    private static void CollectShapes(
        ArrayNodeView node,
        string[] arrayIds,
        int bufferCount,
        SortedSet<string> shapes,
        int depth,
        ulong fileRowCount,
        ref int statsSeen)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "array");

        int encoding = node.Encoding;
        Assert.True(encoding < arrayIds.Length, $"encoding {encoding} outside the {arrayIds.Length} declared ids");

        ReadOnlySpan<ushort> indices = node.BufferIndices;
        for (int i = 0; i < indices.Length; i++)
        {
            Assert.True(indices[i] < bufferCount, $"buffer index {indices[i]} outside {bufferCount} buffers");
        }

        int children = node.ChildCount;
        shapes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{arrayIds[encoding]}/c{children}/b{indices.Length}"));

        if (node.HasStats)
        {
            statsSeen++;
            CheckStats(node.Stats, fileRowCount);
        }

        for (int i = 0; i < children; i++)
        {
            CollectShapes(
                node.GetChild(i), arrayIds, bufferCount, shapes, depth + 1, fileRowCount, ref statsSeen);
        }
    }

    /// <summary>
    /// Invariants that hold across all 7560 <c>ArrayStats</c> tables in the corpus and that a
    /// shifted stats field id breaks. A coherent shift of every stats id survives a round trip
    /// through our own writer, so these are the only assertions that pin them.
    /// </summary>
    private static void CheckStats(ArrayStatsView stats, ulong fileRowCount)
    {
        // A precision read off the wrong slot lands on a uoffset or a count byte, which is
        // essentially never 0 or 1.
        Assert.True(
            stats.MinPrecision is StatPrecision.Inexact or StatPrecision.Exact,
            $"min_precision {(byte)stats.MinPrecision} is outside the enum");
        Assert.True(
            stats.MaxPrecision is StatPrecision.Inexact or StatPrecision.Exact,
            $"max_precision {(byte)stats.MaxPrecision} is outside the enum");

        bool hasSorted = stats.TryGetIsSorted(out bool sorted);
        bool hasStrict = stats.TryGetIsStrictSorted(out bool strict);
        if (hasSorted && hasStrict && strict)
        {
            Assert.True(sorted, "is_strict_sorted is set but is_sorted is not");
        }

        if (stats.TryGetNullCount(out ulong nulls))
        {
            Assert.True(nulls <= fileRowCount, $"null_count {nulls} exceeds the file's {fileRowCount} rows");
        }

        if (stats.TryGetNanCount(out ulong nans))
        {
            Assert.True(nans <= fileRowCount, $"nan_count {nans} exceeds the file's {fileRowCount} rows");
        }

        _ = stats.MinBytes.Length;
        _ = stats.MaxBytes.Length;
        _ = stats.SumBytes.Length;
        _ = stats.TryGetIsConstant(out _);
        _ = stats.TryGetUncompressedSizeInBytes(out _);
    }

    private static void CheckLayoutSegments(string id, LayoutView layout, int segmentCount)
    {
        ReadOnlySpan<uint> segments = layout.Segments;
        for (int i = 0; i < segments.Length; i++)
        {
            Assert.True(segments[i] < segmentCount, $"{id}: layout names segment {segments[i]}");
        }

        int children = layout.ChildCount;
        for (int i = 0; i < children; i++)
        {
            CheckLayoutSegments(id, layout.GetChild(i), segmentCount);
        }
    }

    private static bool EndsInsideFile(SegmentSpec spec, int fileLength) =>
        spec.End <= (ulong)fileLength;

    /// <summary>
    /// Locates <c>tests/Vorticity.Conformance/corpus</c> from this source file's own path, which
    /// survives being compiled into a check project whose output lives outside the repository.
    /// </summary>
    private static string CorpusDirectory([CallerFilePath] string thisFile = "")
    {
        string? directory = Path.GetDirectoryName(thisFile);
        for (int i = 0; i < 4 && directory is not null; i++)
        {
            directory = Path.GetDirectoryName(directory);
        }

        string corpus = Path.Combine(directory ?? ".", "tests", "Vorticity.Conformance", "corpus");
        if (global::System.IO.File.Exists(Path.Combine(corpus, "manifest.json")))
        {
            return corpus;
        }

        // Fallback for a build whose sources were relocated: walk up from the output directory.
        string? probe = AppContext.BaseDirectory;
        while (probe is not null)
        {
            string candidate = Path.Combine(probe, "tests", "Vorticity.Conformance", "corpus");
            if (global::System.IO.File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }

            probe = Path.GetDirectoryName(probe);
        }

        throw new FileNotFoundException($"Golden corpus not found; looked under '{corpus}'.");
    }
}
