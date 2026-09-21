// The integration seam from open to node tree, exercised once end to end on real corpus files:
//
//     VortexFile.OpenAsync                                      (file-open)
//   -> LayoutView walk of RootLayoutBytes                       (fb-schemas)
//   -> SegmentSpec of a vortex.flat leaf
//   -> ISegmentSource.ReadAsync / ReadManyAsync                 (io)
//   -> ArrayBlobReader.Load into an ArrayNodeArena              (array-blob)
//   -> the serialized node tree, with every node's metadata run through its codec
//
// It stops at the node tree on purpose: decoding is tested on its own, and the point of this file
// is to prove the components above COMPOSE rather than merely link. Here file-open's encoding
// table feeds the blob reader, the layout's segment id indexes file-open's segment map, and the
// bytes come off a real ISegmentSource instead of a byte[] copy.
//
// The oracle is the Rust-produced sidecar: its `layout` record carries the layout tree (encoding
// id, row count, segment ids) and, on every vortex.flat leaf, the serialized array's
// (id, nchildren, nbuffers, metadata_b64) shape. Agreeing with our own writer would prove nothing.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Arrays;
using Vorticity.Tests.File;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Integration;

public sealed class Phase1CompositionTests
{
    /// <summary>
    /// One entry per layout shape the corpus has, plus the array shapes whose metadata codecs are
    /// worth driving from real bytes.
    /// </summary>
    public static TheoryData<string> Entries =>
    [
        "types/i64_nonnull_r1024",                            // zoned layout over flat leaves
        "types/struct_nested_deep_nonnull_r1024",             // struct layout, nested
        "containers/chunked_layout_rowblock1024",             // chunked layout
        "containers/dict_layout",                             // dict layout: shared value segments
        "containers/flat_inline_array_node",                  // Array flatbuffer inlined in metadata
        "containers/all_null_i64_explicit_validity_r1025",    // materialized all-invalid validity
        "encodings/varbinview",
        "encodings/fastlanes_bitpacked",
        "encodings/runend",
        "encodings/sparse",
        "encodings/sequence",
        "encodings/fsst",
        "encodings/null_r0",                                  // zero rows
        "types/uuid_nullable_r1024",                          // extension dtype
    ];

    // ---------------------------------------------------------------------------------------
    // The composition itself.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task OpenWalkReadParse(string entry)
    {
        string path = CorpusBlobs.Path(entry, ".vortex");
        SidecarLayout expectedRoot = SidecarLayoutTree(entry);

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        // file-open agreed with the sidecar on the one number both of them state independently.
        Assert.Equal((long)expectedRoot.RowCount, file.RowCount);

        // Walking the layout is synchronous and span-based: LayoutView is a ref struct, so the
        // plan is materialized first and every await happens after the walk has finished.
        List<FlatLeafPlan> plan = PlanFlatLeaves(file, expectedRoot);

        // A zero-row file is a chunked layout with no children and no segments at all
        // (encodings/null_r0), so "no leaves" is a legal shape, not a walk that lost its way.
        Assert.Equal(CountFlatLeaves(expectedRoot), plan.Count);

        ArrayEncodingId[] encodings = ArrayEncodingTable(file);
        ArrayNodeArena arena = new ArrayNodeArena();

        for (int i = 0; i < plan.Count; i++)
        {
            FlatLeafPlan leaf = plan[i];

            // The segment id the LAYOUT carried, indexing the segment map FILE-OPEN parsed.
            SegmentSpec spec = SegmentSpecAt(file, leaf.SegmentIndex);

            // Read through the real ISegmentSource the file opened over its own path.
            SegmentOwner owner = await file.Segments
                .ReadAsync(spec, CancellationToken.None);
            try
            {
                Assert.Equal((int)spec.Length, owner.Length);

                if (leaf.InlinedArrayTree is null)
                {
                    ArrayBlobReader.Load(arena, owner.Buffer, encodings);
                }
                else
                {
                    ArrayBlobReader.Load(arena, leaf.InlinedArrayTree, owner.Buffer, encodings);
                }

                AssertNodeTree(file, arena, arena.RootIndex, leaf.ExpectedArrayTree, $"{entry}#{i}");
            }
            finally
            {
                // Exactly one release for the one reference ReadAsync handed us.
                owner.Release();
            }
        }
    }

    /// <summary>
    /// The same walk driven the way a scan will drive it: one <see cref="ScanContext"/>, every
    /// segment registered in its <see cref="SegmentRequestSet"/>, one coalesced
    /// <see cref="ISegmentSource.ReadManyAsync"/>, then the blobs parsed out of the populated set.
    /// Proves that a batch's segments live until its reset, against a real source and a real file.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public async Task OneBatchThroughScanContextReleasesEverySegmentExactlyOnce(string entry)
    {
        string path = CorpusBlobs.Path(entry, ".vortex");
        SidecarLayout expectedRoot = SidecarLayoutTree(entry);

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        using ScanContext scan = new ScanContext(file);

        // The scan's encoding table is file-open's, copied in - not a second resolution pass.
        Assert.Equal(file.ArrayEncodingCount, scan.ArrayEncodingCount);
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            Assert.Equal(file.GetArrayEncoding(i), scan.ArrayEncodings[i]);
        }

        List<FlatLeafPlan> plan = PlanFlatLeaves(file, expectedRoot);

        int[] slots = new int[plan.Count];
        for (int i = 0; i < plan.Count; i++)
        {
            slots[i] = scan.Segments.Add(SegmentSpecAt(file, plan[i].SegmentIndex));
        }

        await file.Segments.ReadManyAsync(scan.Segments, CancellationToken.None);
        Assert.True(scan.Segments.IsPopulated);

        // Count the references this batch took, per distinct owner. A memory-mapped source hands
        // every slot a view into ONE owner, so the sum below is what ResetBatch must give back.
        Dictionary<SegmentOwner, int> taken = new Dictionary<SegmentOwner, int>(
            ReferenceEqualityComparer.Instance as IEqualityComparer<SegmentOwner>);
        Dictionary<SegmentOwner, int> before = new Dictionary<SegmentOwner, int>(
            ReferenceEqualityComparer.Instance as IEqualityComparer<SegmentOwner>);
        for (int i = 0; i < plan.Count; i++)
        {
            SegmentOwner owner = scan.Segments.GetOwner(slots[i]);
            taken[owner] = taken.TryGetValue(owner, out int n) ? n + 1 : 1;
            before[owner] = owner.RefCount;
        }

        for (int i = 0; i < plan.Count; i++)
        {
            FlatLeafPlan leaf = plan[i];
            VortexBuffer segment = scan.Segments.GetBuffer(slots[i]);

            if (leaf.InlinedArrayTree is null)
            {
                scan.Decode.LoadBlob(segment);
            }
            else
            {
                scan.Decode.LoadBlob(leaf.InlinedArrayTree, segment);
            }

            AssertNodeTree(file, scan.Nodes, scan.Nodes.RootIndex, leaf.ExpectedArrayTree, $"{entry}#{i}");
        }

        scan.ResetBatch();

        Assert.Equal(0, scan.Segments.Count);
        Assert.False(scan.Segments.IsPopulated);
        Assert.Equal(0, scan.Nodes.NodeCount);
        Assert.Equal(0, scan.Canonical.NodeCount);

        foreach (KeyValuePair<SegmentOwner, int> pair in taken)
        {
            Assert.Equal(before[pair.Key] - pair.Value, pair.Key.RefCount);
        }

        // The arenas survive the reset and a second batch runs through the same objects.
        for (int i = 0; i < plan.Count; i++)
        {
            slots[i] = scan.Segments.Add(SegmentSpecAt(file, plan[i].SegmentIndex));
        }

        await file.Segments.ReadManyAsync(scan.Segments, CancellationToken.None);
        if (plan.Count > 0)
        {
            scan.Decode.LoadBlob(scan.Segments.GetBuffer(slots[0]));
            Assert.True(scan.Nodes.NodeCount > 0);
        }

        scan.ResetBatch();
    }

    /// <summary>
    /// The same composition over every golden file. The theory above is the readable oracle; this
    /// is the one that catches a systematic seam bug that happens to miss the sampled fourteen.
    /// </summary>
    [Fact]
    public async Task EveryCorpusFileOpensWalksReadsAndParses()
    {
        string[] files = global::System.IO.Directory.GetFiles(
            CorpusBlobs.CorpusRoot, "*.vortex", global::System.IO.SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        Assert.True(files.Length > 800, "the golden corpus is checked in");

        List<string> failures = [];
        int leaves = 0;

        foreach (string file in files)
        {
            string entry = global::System.IO.Path
                .GetRelativePath(CorpusBlobs.CorpusRoot, file)[..^".vortex".Length]
                .Replace(global::System.IO.Path.DirectorySeparatorChar, '/');

            try
            {
                leaves += await SweepOneAsync(entry);
            }
            catch (Exception error)
            {
                failures.Add($"{entry}: {error.GetType().Name}: {error.Message}");
            }
        }

        Assert.True(leaves > 2000, $"only {leaves} flat leaves were read");
        Assert.Empty(failures);
    }

    private static async Task<int> SweepOneAsync(string entry)
    {
        SidecarLayout expectedRoot = SidecarLayoutTree(entry);
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusBlobs.Path(entry, ".vortex"), OpenOptionsFor(entry), CancellationToken.None);

        Assert.Equal((long)expectedRoot.RowCount, file.RowCount);

        List<FlatLeafPlan> plan = PlanFlatLeaves(file, expectedRoot);
        Assert.Equal(CountFlatLeaves(expectedRoot), plan.Count);

        using ScanContext scan = new ScanContext(file);
        int[] slots = new int[plan.Count];
        for (int i = 0; i < plan.Count; i++)
        {
            slots[i] = scan.Segments.Add(SegmentSpecAt(file, plan[i].SegmentIndex));
        }

        await file.Segments.ReadManyAsync(scan.Segments, CancellationToken.None);

        for (int i = 0; i < plan.Count; i++)
        {
            FlatLeafPlan leaf = plan[i];
            VortexBuffer segment = scan.Segments.GetBuffer(slots[i]);
            if (leaf.InlinedArrayTree is null)
            {
                scan.Decode.LoadBlob(segment);
            }
            else
            {
                scan.Decode.LoadBlob(leaf.InlinedArrayTree, segment);
            }

            AssertNodeTree(file, scan.Nodes, scan.Nodes.RootIndex, leaf.ExpectedArrayTree, $"{entry}#{i}");
        }

        scan.ResetBatch();
        return plan.Count;
    }

    /// <summary>
    /// Opening a file never throws for an encoding we do not implement, and the id text survives
    /// open so the eventual <c>VortexUnsupportedException</c> can name it.
    /// </summary>
    [Fact]
    public async Task OpenClassifiesEveryDeclaredEncodingAndThrowsForNone()
    {
        string path = CorpusBlobs.Path("types/i64_nonnull_r1024", ".vortex");
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        // EVERY DECLARED ID RESOLVES: the corpus writer declares every id its editions permit, and
        // each of them has a decoder. The open's tolerance of an Unknown id is real and tested,
        // over a forged id in `ScanContextTests`; what this file can check is that the
        // classification happens at open, for every entry.
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            Assert.NotEmpty(file.GetArrayEncodingId(i));
            Assert.NotEqual(ArrayEncodingId.Unknown, file.GetArrayEncoding(i));
        }

        for (int i = 0; i < file.LayoutEncodingCount; i++)
        {
            Assert.NotEmpty(file.GetLayoutEncodingId(i));
        }
    }

    // ---------------------------------------------------------------------------------------
    // The layout walk. Synchronous and ref-struct bound: it produces a plan, never a LayoutView.
    // ---------------------------------------------------------------------------------------

    private sealed class FlatLeafPlan
    {
        internal FlatLeafPlan(int segmentIndex, byte[]? inlinedArrayTree, SidecarArrayNode expected)
        {
            SegmentIndex = segmentIndex;
            InlinedArrayTree = inlinedArrayTree;
            ExpectedArrayTree = expected;
        }

        internal int SegmentIndex { get; }

        internal byte[]? InlinedArrayTree { get; }

        internal SidecarArrayNode ExpectedArrayTree { get; }
    }

    private static List<FlatLeafPlan> PlanFlatLeaves(VortexFile file, SidecarLayout expectedRoot)
    {
        List<FlatLeafPlan> leaves = [];
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        LayoutView root = LayoutView.Root(file.RootLayoutBytes.Span, ref tableBudget);
        WalkLayout(file, root, expectedRoot, leaves, 0);
        return leaves;
    }

    private static void WalkLayout(
        VortexFile file,
        LayoutView node,
        SidecarLayout expected,
        List<FlatLeafPlan> leaves,
        int depth)
    {
        Assert.True(depth < VortexLimits.MaxLayoutDepth, "layout depth");

        // file-open resolved the id; fb-schemas supplied the index. Both must agree with Rust.
        Assert.Equal(expected.EncodingId, file.GetLayoutEncodingId(node.Encoding));
        Assert.Equal(expected.RowCount, node.RowCount);

        ReadOnlySpan<uint> segments = node.Segments;
        Assert.Equal(expected.SegmentIds.Length, segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            Assert.Equal(expected.SegmentIds[i], (int)segments[i]);
        }

        if (file.GetLayoutEncoding(node.Encoding) == LayoutEncodingId.Flat)
        {
            Assert.Equal("vortex.flat", expected.EncodingId);
            Assert.NotNull(expected.ArrayTree);
            Assert.Equal(1, segments.Length);

            byte[]? inlined = null;
            ReadOnlySpan<byte> metadata = node.Metadata;
            if (!metadata.IsEmpty)
            {
                FlatLayoutMetadata parsed = FlatLayoutMetadata.Read(metadata);
                if (parsed.HasArrayEncodingTree)
                {
                    inlined = parsed.ArrayEncodingTree.ToArray();
                }
            }

            leaves.Add(new FlatLeafPlan((int)segments[0], inlined, expected.ArrayTree!));
        }

        Assert.Equal(expected.Children.Length, node.ChildCount);
        for (int i = 0; i < node.ChildCount; i++)
        {
            WalkLayout(file, node.GetChild(i), expected.Children[i], leaves, depth + 1);
        }
    }

    /// <summary>
    /// <c>types/no_dtype_segment</c> is the corpus's only out-of-band-schema file: with no dtype
    /// segment and no supplied DType the open is a <c>VortexFormatException</c>,
    /// so the sweep supplies the schema the way a caller would. The donor is a real file with the
    /// identical schema, not 33 hand-written arena calls.
    /// </summary>
    private static VortexOpenOptions OpenOptionsFor(string entry) =>
        CorpusManifest.Find(entry).HasDTypeSegment
            ? VortexOpenOptions.Default
            : new VortexOpenOptions { DType = OutOfBandSchema.Value };

    private static readonly Lazy<DType> OutOfBandSchema = new Lazy<DType>(static () =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                CorpusBlobs.Path("types/user_metadata_segments", ".vortex"),
                VortexOpenOptions.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        // The arena the DType handle points into outlives the file object, so the schema stays
        // usable after the donor is closed.
        DType schema = donor.Schema;
        donor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return schema;
    });

    private static int CountFlatLeaves(SidecarLayout node)
    {
        int count = node.EncodingId == "vortex.flat" ? 1 : 0;
        foreach (SidecarLayout child in node.Children)
        {
            count += CountFlatLeaves(child);
        }

        return count;
    }

    private static SegmentSpec SegmentSpecAt(VortexFile file, int index)
    {
        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        Assert.InRange(index, 0, specs.Length - 1);
        return specs[index];
    }

    private static ArrayEncodingId[] ArrayEncodingTable(VortexFile file)
    {
        ArrayEncodingId[] table = new ArrayEncodingId[file.ArrayEncodingCount];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = file.GetArrayEncoding(i);
        }

        return table;
    }

    // ---------------------------------------------------------------------------------------
    // The node tree, checked against the sidecar and pushed through the metadata codecs.
    // ---------------------------------------------------------------------------------------

    private static void AssertNodeTree(
        VortexFile file,
        ArrayNodeArena arena,
        int index,
        SidecarArrayNode expected,
        string path)
    {
        ArrayNode node = arena.GetNode(index);

        Assert.Equal(expected.Id, file.GetArrayEncodingId(node.EncodingSpecIndex));
        Assert.Equal(file.GetArrayEncoding(node.EncodingSpecIndex), node.Encoding);
        Assert.Equal(expected.ChildCount, node.ChildCount);
        Assert.Equal(expected.BufferCount, node.BufferCount);
        Assert.Equal(expected.MetadataLength, node.Metadata.Length);
        Assert.True(
            node.Metadata.SequenceEqual(expected.Metadata),
            $"{path}: metadata bytes differ from the sidecar for {expected.Id}");

        // Every buffer the node claims resolves to real bytes inside the segment we just read.
        for (int i = 0; i < node.BufferCount; i++)
        {
            VortexBuffer buffer = node.GetBuffer(i);
            Assert.True(buffer.Length >= 0, path);
            if (buffer.Length > 0)
            {
                // Touching the span is the point: a bad offset would be caught here, not later.
                Assert.Equal(buffer.Length, buffer.Span.Length);
            }
        }

        ParseMetadata(node.Encoding, expected.Id, node.Metadata);

        for (int i = 0; i < expected.ChildCount; i++)
        {
            AssertNodeTree(file, arena, node.GetChild(i).Index, expected.Children[i], path + "/" + i);
        }
    }

    /// <summary>
    /// Runs one node's metadata through the codec that owns it. Real bytes, off a real segment,
    /// through the same entry point a decoder will use.
    /// </summary>
    private static void ParseMetadata(ArrayEncodingId id, string idText, ReadOnlySpan<byte> metadata)
    {
        switch (id)
        {
            // The encodings whose metadata is empty. Note vortex.constant is NOT here: upstream
            // ignores its metadata field entirely.
            case ArrayEncodingId.Null:
            case ArrayEncodingId.Primitive:
            case ArrayEncodingId.VarBinView:
            case ArrayEncodingId.Struct:
            case ArrayEncodingId.Chunked:
            case ArrayEncodingId.Masked:
            case ArrayEncodingId.FixedSizeList:
            case ArrayEncodingId.Extension:
            case ArrayEncodingId.ByteBool:
            case ArrayEncodingId.ZigZag:
                EncodingMetadata.RequireEmpty(metadata, idText);
                break;

            case ArrayEncodingId.Bool:
                Assert.InRange(BoolMetadata.Read(metadata).Offset, 0u, 7u);
                break;

            case ArrayEncodingId.Decimal:
                Assert.True(Enum.IsDefined(DecimalMetadata.Read(metadata).ValuesType));
                break;

            case ArrayEncodingId.VarBin:
                Assert.True(PTypeExtensions.IsDefined(VarBinMetadata.Read(metadata).OffsetsPType));
                break;

            case ArrayEncodingId.List:
                Assert.True(PTypeExtensions.IsDefined(ListMetadata.Read(metadata).OffsetPType));
                break;

            case ArrayEncodingId.ListView:
            {
                ListViewMetadata parsed = ListViewMetadata.Read(metadata);
                Assert.True(PTypeExtensions.IsDefined(parsed.OffsetPType));
                Assert.True(PTypeExtensions.IsDefined(parsed.SizePType));
                break;
            }

            case ArrayEncodingId.Dict:
                Assert.True(PTypeExtensions.IsDefined(DictMetadata.Read(metadata).CodesPType));
                break;

            case ArrayEncodingId.RunEnd:
                Assert.True(PTypeExtensions.IsDefined(RunEndMetadata.Read(metadata).EndsPType));
                break;

            case ArrayEncodingId.FastLanesBitPacked:
                Assert.InRange(BitPackedMetadata.Read(metadata).BitWidth, 0u, 64u);
                break;

            case ArrayEncodingId.FastLanesRle:
                Assert.True(PTypeExtensions.IsDefined(RleMetadata.Read(metadata).IndicesPType));
                break;

            case ArrayEncodingId.Sparse:
                Assert.True(PTypeExtensions.IsDefined(SparseMetadata.Read(metadata).Patches.IndicesPType));
                break;

            case ArrayEncodingId.FastLanesFor:
            {
                ScalarStore store = new ScalarStore();
                DTypeArena types = new DTypeArena();
                Assert.False(EncodingMetadata.ReadReferenceScalar(metadata, store, types).IsAbsent);
                break;
            }

            case ArrayEncodingId.Sequence:
            {
                ScalarStore store = new ScalarStore();
                DTypeArena types = new DTypeArena();
                _ = SequenceMetadata.Read(metadata, store, types);
                break;
            }

            case ArrayEncodingId.Constant:
            case ArrayEncodingId.Unknown:
            default:
                // vortex.constant carries its scalar in buffer 0; an Unknown id is not an error
                // until a projected column needs it.
                break;
        }
    }

    // ---------------------------------------------------------------------------------------
    // The sidecar oracle.
    // ---------------------------------------------------------------------------------------

    private sealed class SidecarLayout
    {
        private SidecarLayout(
            string encodingId,
            ulong rowCount,
            int[] segmentIds,
            SidecarArrayNode? arrayTree,
            SidecarLayout[] children)
        {
            EncodingId = encodingId;
            RowCount = rowCount;
            SegmentIds = segmentIds;
            ArrayTree = arrayTree;
            Children = children;
        }

        internal string EncodingId { get; }

        internal ulong RowCount { get; }

        internal int[] SegmentIds { get; }

        internal SidecarArrayNode? ArrayTree { get; }

        internal SidecarLayout[] Children { get; }

        internal static SidecarLayout Read(JsonElement element)
        {
            List<int> segments = [];
            if (element.TryGetProperty("segment_ids", out JsonElement ids))
            {
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    segments.Add(id.GetInt32());
                }
            }

            List<SidecarLayout> children = [];
            if (element.TryGetProperty("children", out JsonElement kids))
            {
                foreach (JsonElement kid in kids.EnumerateArray())
                {
                    children.Add(Read(kid));
                }
            }

            SidecarArrayNode? tree = element.TryGetProperty("array_tree", out JsonElement arrayTree)
                ? SidecarArrayNode.Read(arrayTree)
                : null;

            return new SidecarLayout(
                element.GetProperty("encoding_id").GetString() ?? string.Empty,
                element.GetProperty("row_count").GetUInt64(),
                segments.ToArray(),
                tree,
                children.ToArray());
        }
    }

    private sealed class SidecarArrayNode
    {
        private SidecarArrayNode(
            string id,
            int childCount,
            int bufferCount,
            int metadataLength,
            byte[] metadata,
            SidecarArrayNode[] children)
        {
            Id = id;
            ChildCount = childCount;
            BufferCount = bufferCount;
            MetadataLength = metadataLength;
            Metadata = metadata;
            Children = children;
        }

        internal string Id { get; }

        internal int ChildCount { get; }

        internal int BufferCount { get; }

        internal int MetadataLength { get; }

        internal byte[] Metadata { get; }

        internal SidecarArrayNode[] Children { get; }

        internal static SidecarArrayNode Read(JsonElement element)
        {
            List<SidecarArrayNode> children = [];
            if (element.TryGetProperty("children", out JsonElement kids))
            {
                foreach (JsonElement kid in kids.EnumerateArray())
                {
                    children.Add(Read(kid));
                }
            }

            byte[] metadata = element.TryGetProperty("metadata_b64", out JsonElement b64)
                ? Convert.FromBase64String(b64.GetString() ?? string.Empty)
                : [];

            return new SidecarArrayNode(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("nchildren").GetInt32(),
                element.GetProperty("nbuffers").GetInt32(),
                element.GetProperty("metadata_len").GetInt32(),
                metadata,
                children.ToArray());
        }
    }

    private static SidecarLayout SidecarLayoutTree(string entry)
    {
        foreach (string line in global::System.IO.File.ReadLines(CorpusBlobs.Path(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "layout")
            {
                return SidecarLayout.Read(root.GetProperty("tree"));
            }
        }

        throw new global::System.IO.InvalidDataException($"No `layout` record in the sidecar for {entry}.");
    }
}
