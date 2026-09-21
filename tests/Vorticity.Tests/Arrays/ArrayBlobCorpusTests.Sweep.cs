// One sweep over the whole corpus. The per-entry theory above is the readable oracle; this is the
// one that would catch a systematic blob bug that happens to miss the seventeen sampled files.
// Value-by-value comparison against the sidecars belongs to the conformance component; this
// compares structure only, which is all the blob spine produces.
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

public sealed class ArrayBlobCorpusSweepTests
{
    [Fact]
    public void EveryArrayBlobInTheCorpusParsesToTheShapeTheSidecarRecords()
    {
        string[] files = Directory.GetFiles(CorpusBlobs.CorpusRoot, "*.vortex", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        Assert.True(files.Length > 800, "the golden corpus is checked in");

        ArrayNodeArena arena = new ArrayNodeArena();
        int blobs = 0;
        List<string> failures = [];

        foreach (string file in files)
        {
            string entry = System.IO.Path.GetRelativePath(CorpusBlobs.CorpusRoot, file);
            entry = entry[..^".vortex".Length].Replace(System.IO.Path.DirectorySeparatorChar, '/');

            CorpusBlobs parsed;
            List<ShapeNode> expected;
            try
            {
                parsed = CorpusBlobs.Load(entry);
                expected = Shapes(entry);
            }
            catch (Exception error)
            {
                failures.Add($"{entry}: walk failed: {error.Message}");
                continue;
            }

            if (expected.Count != parsed.Leaves.Count)
            {
                failures.Add($"{entry}: {parsed.Leaves.Count} flat leaves, sidecar has {expected.Count}");
                continue;
            }

            ArrayEncodingId[] encodings = new ArrayEncodingId[parsed.ArrayEncodingIds.Length];
            for (int i = 0; i < encodings.Length; i++)
            {
                encodings[i] = EncodingRegistry.ResolveArray(
                    Encoding.UTF8.GetBytes(parsed.ArrayEncodingIds[i]));
            }

            for (int i = 0; i < expected.Count; i++)
            {
                FlatLeaf leaf = parsed.Leaves[i];
                using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(leaf.Segment, 64);
                try
                {
                    if (leaf.InlinedArrayTree is null)
                    {
                        ArrayBlobReader.Load(arena, owner.Buffer, encodings);
                    }
                    else
                    {
                        ArrayBlobReader.Load(arena, leaf.InlinedArrayTree, owner.Buffer, encodings);
                    }

                    Compare(arena, arena.RootIndex, expected[i], parsed.ArrayEncodingIds, $"{entry}#{i}", failures);
                    blobs++;
                }
                catch (Exception error)
                {
                    failures.Add($"{entry}#{i}: {error.GetType().Name}: {error.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(blobs > 2000, $"only {blobs} array blobs were parsed");
    }

    private static void Compare(
        ArrayNodeArena arena,
        int index,
        ShapeNode expected,
        string[] ids,
        string path,
        List<string> failures)
    {
        ArrayNode node = arena.GetNode(index);
        string actualId = node.EncodingSpecIndex < ids.Length ? ids[node.EncodingSpecIndex] : "?";

        if (actualId != expected.Id ||
            node.ChildCount != expected.ChildCount ||
            node.BufferCount != expected.BufferCount ||
            node.Metadata.Length != expected.MetadataLength)
        {
            failures.Add(
                $"{path}: got ({actualId}, {node.ChildCount}, {node.BufferCount}, {node.Metadata.Length}); " +
                $"expected ({expected.Id}, {expected.ChildCount}, {expected.BufferCount}, {expected.MetadataLength})");
            return;
        }

        for (int i = 0; i < node.BufferCount; i++)
        {
            _ = node.GetBuffer(i).Length;
        }

        for (int i = 0; i < expected.ChildCount; i++)
        {
            Compare(arena, node.GetChild(i).Index, expected.Children[i], ids, path + "/" + i, failures);
        }
    }

    private static List<ShapeNode> Shapes(string entry)
    {
        List<ShapeNode> trees = [];
        foreach (string line in System.IO.File.ReadLines(CorpusBlobs.Path(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "layout")
            {
                continue;
            }

            Collect(root.GetProperty("tree"), trees);
            break;
        }

        return trees;
    }

    private static void Collect(JsonElement node, List<ShapeNode> trees)
    {
        if (node.TryGetProperty("encoding_id", out JsonElement id) &&
            id.GetString() == "vortex.flat" &&
            node.TryGetProperty("array_tree", out JsonElement tree))
        {
            trees.Add(ShapeNode.Read(tree));
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                Collect(child, trees);
            }
        }
    }

    private sealed class ShapeNode
    {
        private ShapeNode(string id, int childCount, int bufferCount, int metadataLength, ShapeNode[] children)
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

        internal ShapeNode[] Children { get; }

        internal static ShapeNode Read(JsonElement element)
        {
            List<ShapeNode> children = [];
            if (element.TryGetProperty("children", out JsonElement kids))
            {
                foreach (JsonElement kid in kids.EnumerateArray())
                {
                    children.Add(Read(kid));
                }
            }

            return new ShapeNode(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("nchildren").GetInt32(),
                element.GetProperty("nbuffers").GetInt32(),
                element.GetProperty("metadata_len").GetInt32(),
                children.ToArray());
        }
    }
}
