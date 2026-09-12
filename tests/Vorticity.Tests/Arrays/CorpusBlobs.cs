// A deliberately minimal, test-only walk from a .vortex file down to its array blobs, built on the
// Phase 0 spine plus fb-schemas alone. It exists because the sidecar's `layout` line carries each
// vortex.flat node's `array_tree` - the (id, nchildren, nbuffers, metadata_len) shape of the
// serialized array - and that is a free oracle for the blob parser that nothing else in Phase 1
// has. `file-open` (contract §7) and `layouts` (§11) do this properly; this does just enough of it
// to reach the segments, and never claims to validate anything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.Arrays;

/// <summary>One <c>vortex.flat</c> leaf found in a corpus file.</summary>
internal sealed class FlatLeaf
{
    internal FlatLeaf(byte[] segment, byte[]? inlinedArrayTree)
    {
        Segment = segment;
        InlinedArrayTree = inlinedArrayTree;
    }

    /// <summary>The whole array-blob segment, copied out of the file.</summary>
    internal byte[] Segment { get; }

    /// <summary>The layout's inlined <c>Array</c> FlatBuffer, or null in the normal case.</summary>
    internal byte[]? InlinedArrayTree { get; }
}

/// <summary>A corpus file, walked far enough to reach its array blobs.</summary>
internal sealed class CorpusBlobs
{
    private CorpusBlobs(string[] arrayEncodingIds, List<FlatLeaf> leaves)
    {
        ArrayEncodingIds = arrayEncodingIds;
        Leaves = leaves;
    }

    /// <summary>The footer's <c>array_specs</c>, in order.</summary>
    internal string[] ArrayEncodingIds { get; }

    /// <summary>Every <c>vortex.flat</c> leaf, in depth-first layout order - the sidecar's order.</summary>
    internal List<FlatLeaf> Leaves { get; }

    /// <summary>The corpus root, located from this source file's compile-time path.</summary>
    internal static string CorpusRoot { get; } = LocateCorpus();

    /// <summary>Absolute path of a corpus entry, e.g. <c>"encodings/constant"</c>.</summary>
    internal static string Path(string entry, string extension) =>
        System.IO.Path.Combine(CorpusRoot, entry.Replace('/', System.IO.Path.DirectorySeparatorChar) + extension);

    internal static CorpusBlobs Load(string entry)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(Path(entry, ".vortex"));
        return Parse(bytes);
    }

    private static CorpusBlobs Parse(byte[] bytes)
    {
        int n = bytes.Length;
        CorpusAssert.GreaterOrEqual(n, 8, "file length");

        ushort version = BitConverter.ToUInt16(bytes, n - 8);
        ushort postscriptLength = BitConverter.ToUInt16(bytes, n - 6);
        CorpusAssert.Equal(1, version, "format version");
        CorpusAssert.True(
            bytes[n - 4] == (byte)'V' && bytes[n - 3] == (byte)'T' &&
            bytes[n - 2] == (byte)'X' && bytes[n - 1] == (byte)'F',
            "EOF magic");

        int budget = VortexLimits.MaxFlatBufferTables;
        ReadOnlySpan<byte> postscript = bytes.AsSpan(n - 8 - postscriptLength, postscriptLength);
        PostscriptView ps = PostscriptView.Root(postscript, ref budget);

        SegmentSpec footerSpec = ps.Footer.ToSegmentSpec();
        SegmentSpec layoutSpec = ps.Layout.ToSegmentSpec();

        // The footer and layout segments must be handed to the FlatBuffers reader ALIGNED: it
        // reinterprets `Footer.segment_specs` and `Layout.segments` in place and tests the element
        // address, so a span starting at an arbitrary offset of a managed array is refused. This is
        // what `file-open` does with the tail buffer; here a pinned copy stands in for it.
        using PinnedArraySegmentOwner footerOwner =
            PinnedArraySegmentOwner.CopyOf(Slice(bytes, footerSpec), 64);
        using PinnedArraySegmentOwner layoutOwner =
            PinnedArraySegmentOwner.CopyOf(Slice(bytes, layoutSpec), 64);

        int footerBudget = VortexLimits.MaxFlatBufferTables;
        FooterView footer = FooterView.Root(footerOwner.Buffer.Span, ref footerBudget);

        string[] arrayIds = new string[footer.ArraySpecCount];
        for (int i = 0; i < arrayIds.Length; i++)
        {
            arrayIds[i] = Encoding.UTF8.GetString(footer.GetArraySpecIdUtf8(i));
        }

        string[] layoutIds = new string[footer.LayoutSpecCount];
        for (int i = 0; i < layoutIds.Length; i++)
        {
            layoutIds[i] = Encoding.UTF8.GetString(footer.GetLayoutSpecIdUtf8(i));
        }

        // SegmentSpecs is a span over the footer bytes; copy it so the walk below can allocate.
        ReadOnlySpan<SegmentSpec> specSpan = footer.SegmentSpecs;
        SegmentSpec[] specs = specSpan.ToArray();

        List<FlatLeaf> leaves = [];
        int layoutBudget = VortexLimits.MaxFlatBufferTables;
        LayoutView root = LayoutView.Root(layoutOwner.Buffer.Span, ref layoutBudget);
        Walk(root, layoutIds, specs, bytes, leaves, 0);

        return new CorpusBlobs(arrayIds, leaves);
    }

    private static void Walk(
        LayoutView node,
        string[] layoutIds,
        SegmentSpec[] specs,
        byte[] file,
        List<FlatLeaf> leaves,
        int depth)
    {
        CorpusAssert.True(depth < VortexLimits.MaxLayoutDepth, "layout depth");

        ushort encoding = node.Encoding;
        string id = encoding < layoutIds.Length ? layoutIds[encoding] : "?";
        if (id == "vortex.flat")
        {
            ReadOnlySpan<uint> segments = node.Segments;
            CorpusAssert.Equal(1, segments.Length, "vortex.flat segment count");
            SegmentSpec spec = specs[(int)segments[0]];

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

            leaves.Add(new FlatLeaf(Slice(file, spec).ToArray(), inlined));
        }

        int children = node.ChildCount;
        for (int i = 0; i < children; i++)
        {
            Walk(node.GetChild(i), layoutIds, specs, file, leaves, depth + 1);
        }
    }

    private static ReadOnlySpan<byte> Slice(byte[] file, SegmentSpec spec) =>
        file.AsSpan(checked((int)spec.Offset), checked((int)spec.Length));

    private static string LocateCorpus([CallerFilePath] string thisFile = "")
    {
        // Walk up from this source file rather than from AppContext.BaseDirectory: the check
        // project that builds this component lives outside the repository, so its output directory
        // says nothing about where the corpus is.
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(
                dir.FullName, "tests", "Vorticity.Conformance", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/Vorticity.Conformance/corpus above '{thisFile}'.");
    }
}

/// <summary>Assertions for the corpus walk itself. Named apart from xUnit's Assert on purpose.</summary>
internal static class CorpusAssert
{
    internal static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidDataException($"Corpus walk failed: {what}.");
        }
    }

    internal static void Equal(int expected, int actual, string what)
    {
        if (expected != actual)
        {
            throw new InvalidDataException($"Corpus walk failed: {what} is {actual}, expected {expected}.");
        }
    }

    internal static void GreaterOrEqual(int actual, int minimum, string what)
    {
        if (actual < minimum)
        {
            throw new InvalidDataException($"Corpus walk failed: {what} is {actual}, expected at least {minimum}.");
        }
    }
}
