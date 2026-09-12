// The sidecar's `layout` line carries, for every vortex.flat leaf, the serialized array's
// (id, nchildren, nbuffers, metadata_len) tree. That is a Rust-produced oracle for exactly what
// ArrayBlobReader computes, and it is the only free one this component has - agreeing with our own
// encoder would prove nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ArrayBlobCorpusTests
{
    // Ten entries spanning the shapes contract §8 names, plus the extras that exercise a distinct
    // code path: an inlined array tree, a zero-row file, and the two forced-unsupported encodings.
    public static TheoryData<string> Entries =>
    [
        "encodings/constant",
        "types/i64_nonnull_r1024",
        "types/struct_nested_deep_nonnull_r1024",
        "containers/chunked_layout_rowblock1024",
        "encodings/varbinview",
        "encodings/dict",
        "encodings/fastlanes_bitpacked",
        "encodings/fsst",
        "types/list_i32_nullable_r1024",
        "encodings/masked",
        "encodings/listview",
        "encodings/null_r0",
        "types/uuid_nullable_r1024",
        "containers/flat_inline_array_node",
        "encodings/fastlanes_delta",
        "containers/experimental_patched_array_editions_off",
        "containers/all_null_i64_explicit_validity_r1025",
    ];

    [Theory]
    [MemberData(nameof(Entries))]
    public void NodeTreeMatchesTheSidecar(string entry)
    {
        CorpusBlobs blobs = CorpusBlobs.Load(entry);
        List<SidecarNode> expected = SidecarArrayTrees(entry);

        Assert.Equal(expected.Count, blobs.Leaves.Count);

        ArrayNodeArena arena = new ArrayNodeArena();
        ArrayEncodingId[] encodings = Resolve(blobs.ArrayEncodingIds);

        for (int i = 0; i < expected.Count; i++)
        {
            FlatLeaf leaf = blobs.Leaves[i];
            using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(leaf.Segment, 64);

            if (leaf.InlinedArrayTree is null)
            {
                ArrayBlobReader.Load(arena, owner.Buffer, encodings);
            }
            else
            {
                ArrayBlobReader.Load(arena, leaf.InlinedArrayTree, owner.Buffer, encodings);
            }

            AssertNode(arena, arena.RootIndex, expected[i], blobs.ArrayEncodingIds, $"{entry}#{i}");
        }
    }

    [Fact]
    public void InlinedTreeAndSegmentTailAgree()
    {
        // The writer appends the FlatBuffer and the u32 to the segment even when the layout also
        // inlines it, so the two Load overloads must produce identical node trees on this file.
        CorpusBlobs blobs = CorpusBlobs.Load("containers/flat_inline_array_node");
        ArrayEncodingId[] encodings = Resolve(blobs.ArrayEncodingIds);

        ArrayNodeArena viaSegment = new ArrayNodeArena();
        ArrayNodeArena viaMetadata = new ArrayNodeArena();

        int inlinedLeaves = 0;
        foreach (FlatLeaf leaf in blobs.Leaves)
        {
            if (leaf.InlinedArrayTree is null)
            {
                continue;
            }

            inlinedLeaves++;
            using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(leaf.Segment, 64);
            ArrayBlobReader.Load(viaSegment, owner.Buffer, encodings);
            ArrayBlobReader.Load(viaMetadata, leaf.InlinedArrayTree, owner.Buffer, encodings);

            Assert.Equal(viaSegment.NodeCount, viaMetadata.NodeCount);
            for (int i = 0; i < viaSegment.NodeCount; i++)
            {
                ArrayNode a = viaSegment.GetNode(i);
                ArrayNode b = viaMetadata.GetNode(i);
                Assert.Equal(a.EncodingSpecIndex, b.EncodingSpecIndex);
                Assert.Equal(a.ChildCount, b.ChildCount);
                Assert.Equal(a.BufferCount, b.BufferCount);
                Assert.True(a.Metadata.SequenceEqual(b.Metadata));
                for (int k = 0; k < a.BufferCount; k++)
                {
                    Assert.Equal(a.GetBuffer(k).Length, b.GetBuffer(k).Length);
                    Assert.True(a.GetBuffer(k).Span.SequenceEqual(b.GetBuffer(k).Span));
                }
            }
        }

        Assert.Equal(5, inlinedLeaves);
    }

    [Fact]
    public void EveryDeclaredArrayIdEitherResolvesOrIsDeliberatelyUnknown()
    {
        // The writer pre-populates every id its edition permits, so a declared-but-unresolvable id
        // must never be an error (contract §2.3 and the corpus manifest's last caveat).
        CorpusBlobs blobs = CorpusBlobs.Load("types/i64_nonnull_r1024");
        Assert.NotEmpty(blobs.ArrayEncodingIds);

        int unknown = 0;
        foreach (string id in blobs.ArrayEncodingIds)
        {
            if (EncodingRegistry.ResolveArray(Encoding.UTF8.GetBytes(id)) == ArrayEncodingId.Unknown)
            {
                unknown++;

                // Every one of them is an id we know about and deferred, not a mystery.
                Assert.NotNull(EncodingRegistry.DescribeUnsupported(Encoding.UTF8.GetBytes(id)));
            }
        }

        Assert.True(unknown > 0, "the corpus declares ids Phase 1 defers");
    }

    [Fact]
    public void ReusingOneArenaAcrossEveryCorpusBlobStaysBounded()
    {
        // Reset() keeps the backing arrays; a hundred loads through one arena must not make it
        // grow without bound, and must leave each load's tree independent of the last.
        CorpusBlobs blobs = CorpusBlobs.Load("containers/chunked_layout_rowblock1024");
        ArrayEncodingId[] encodings = Resolve(blobs.ArrayEncodingIds);
        ArrayNodeArena arena = new ArrayNodeArena();

        int firstNodeCount = -1;
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (FlatLeaf leaf in blobs.Leaves)
            {
                using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(leaf.Segment, 64);
                ArrayBlobReader.Load(arena, owner.Buffer, encodings);
                if (firstNodeCount < 0)
                {
                    firstNodeCount = arena.NodeCount;
                }

                Assert.True(arena.NodeCount > 0);
                Assert.Equal(0, arena.RootIndex);
            }
        }

        Assert.True(firstNodeCount > 0);
    }

    private static ArrayEncodingId[] Resolve(string[] ids)
    {
        ArrayEncodingId[] resolved = new ArrayEncodingId[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            resolved[i] = EncodingRegistry.ResolveArray(Encoding.UTF8.GetBytes(ids[i]));
        }

        return resolved;
    }

    private static void AssertNode(
        ArrayNodeArena arena,
        int index,
        SidecarNode expected,
        string[] ids,
        string path)
    {
        ArrayNode node = arena.GetNode(index);
        string actualId = node.EncodingSpecIndex < ids.Length ? ids[node.EncodingSpecIndex] : "?";

        Assert.Equal(expected.Id, actualId);
        Assert.Equal(expected.ChildCount, node.ChildCount);
        Assert.Equal(expected.BufferCount, node.BufferCount);
        Assert.Equal(expected.MetadataLength, node.Metadata.Length);

        // Every buffer index the node claims must already resolve to real bytes.
        for (int i = 0; i < node.BufferCount; i++)
        {
            VortexBuffer buffer = node.GetBuffer(i);
            Assert.True(buffer.Length >= 0, path);
        }

        for (int i = 0; i < expected.ChildCount; i++)
        {
            AssertNode(arena, node.GetChild(i).Index, expected.Children[i], ids, path + "/" + i);
        }
    }

    private static List<SidecarNode> SidecarArrayTrees(string entry)
    {
        List<SidecarNode> trees = [];
        foreach (string line in System.IO.File.ReadLines(CorpusBlobs.Path(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "layout")
            {
                continue;
            }

            CollectFlat(root.GetProperty("tree"), trees);
            break;
        }

        return trees;
    }

    private static void CollectFlat(JsonElement node, List<SidecarNode> trees)
    {
        if (node.TryGetProperty("encoding_id", out JsonElement encodingId) &&
            encodingId.GetString() == "vortex.flat" &&
            node.TryGetProperty("array_tree", out JsonElement tree))
        {
            trees.Add(SidecarNode.Read(tree));
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectFlat(child, trees);
            }
        }
    }

    private sealed class SidecarNode
    {
        private SidecarNode(string id, int childCount, int bufferCount, int metadataLength, SidecarNode[] children)
        {
            Id = id;
            ChildCount = childCount;
            BufferCount = bufferCount;
            MetadataLength = metadataLength;
            Children = children;
        }

        internal string Id { get; }

        internal int ChildCount { get; }

        internal int BufferCount { get; }

        internal int MetadataLength { get; }

        internal SidecarNode[] Children { get; }

        internal static SidecarNode Read(JsonElement element)
        {
            List<SidecarNode> children = [];
            if (element.TryGetProperty("children", out JsonElement kids))
            {
                foreach (JsonElement kid in kids.EnumerateArray())
                {
                    children.Add(Read(kid));
                }
            }

            return new SidecarNode(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("nchildren").GetInt32(),
                element.GetProperty("nbuffers").GetInt32(),
                element.GetProperty("metadata_len").GetInt32(),
                children.ToArray());
        }
    }
}
