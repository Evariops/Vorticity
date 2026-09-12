// The one test `vortex.stats` can have (PHASE1-CONTRACTS.md §2.7).
//
// No 0.86.1 writer path constructs a vortex.stats layout, so the corpus has no fixture and never
// will while the corpus records a single-version pin. But `vortex.zoned` and `vortex.stats` are
// both exactly TWELVE BYTES, so overwriting the id in the footer's layout_specs is
// length-preserving: every offset in the file stays valid and the reader takes the legacy path
// against real bytes.
//
// What that proves is exactly what we claim for the layout and no more:
//   (a) the file still opens and its layout tree still parses;
//   (b) the data child reads value-for-value equal to the unpatched original;
//   (c) ZoneMap.IsPruningAvailable is false.
// The zones-table dtype derivation stays untested - the metadata is a zoned one reinterpreted, so
// its bitset is arbitrary - which is why that derivation carries an UNTESTED marker in the source.
using System;
using System.IO;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class StatsLayoutForgedTests
{
    /// <summary>A fully decodable file whose bytes contain <c>vortex.zoned</c> exactly once.</summary>
    private const string SourceId = "distributions/all_null_i64_r1024";

    [Fact]
    public async Task ForgingZonedIntoStatsKeepsTheFileReadable()
    {
        byte[] original = System.IO.File.ReadAllBytes(LayoutCorpus.FullPath(LayoutCorpus.Find(SourceId)));
        byte[] patched = Patch(original, out int offset);
        Assert.True(offset >= 0, "vortex.zoned does not occur in the source file.");
        Assert.Equal(original.Length, patched.Length);

        string directory = Path.Combine(Path.GetTempPath(), "vorticity-layouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "forged_stats.vortex");
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, patched);

            byte[] expected = await DigestAsync(LayoutCorpus.FullPath(LayoutCorpus.Find(SourceId)), expectStats: false);
            byte[] actual = await DigestAsync(path, expectStats: true);

            Assert.True(
                expected.AsSpan().SequenceEqual(actual),
                "the forged vortex.stats layout does not read the same values as the original.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheTwoLayoutIdsAreTheSameLength()
    {
        // The whole trick rests on this, so it is asserted rather than assumed.
        Assert.Equal("vortex.zoned"u8.Length, "vortex.stats"u8.Length);
    }

    private static async ValueTask<byte[]> DigestAsync(string path, bool expectStats)
    {
        LayoutExecutor.EnsureDecoders();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        LayoutNode zoneMapNode = FindZoneMapNode(tree.Root);
        Assert.Equal(
            expectStats ? LayoutEncodingId.Stats : LayoutEncodingId.Zoned,
            zoneMapNode.Encoding);

        Assert.True(zoneMapNode.TryGetZoneMap(out ZoneMap map));
        if (expectStats)
        {
            // Structural only: the legacy zone map never enables pruning (contract §2.7).
            Assert.False(map.IsPruningAvailable);
            Assert.Equal(0, map.AggregateCount);
        }
        else
        {
            Assert.True(map.IsPruningAvailable);
        }

        int root = await LayoutExecutor.ReadAsync(
            file, tree, context, new RowRange(0, file.RowCount), FieldMask.All);
        return CanonicalDigest.Of(context, root);
    }

    private static LayoutNode FindZoneMapNode(LayoutNode node)
    {
        if (node.Encoding is LayoutEncodingId.Zoned or LayoutEncodingId.Stats)
        {
            return node;
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            LayoutNode found = FindZoneMapNode(node.GetChild(i));
            if (found.Tree is not null)
            {
                return found;
            }
        }

        return default;
    }

    /// <summary>Overwrites the single <c>vortex.zoned</c> id in place with <c>vortex.stats</c>.</summary>
    private static byte[] Patch(byte[] original, out int offset)
    {
        ReadOnlySpan<byte> needle = "vortex.zoned"u8;
        offset = original.AsSpan().IndexOf(needle);
        if (offset < 0)
        {
            return original;
        }

        // Exactly one occurrence, so the patch cannot hit a value that merely looks like the id.
        int second = original.AsSpan(offset + needle.Length).IndexOf(needle);
        Assert.True(second < 0, "vortex.zoned occurs more than once; the patch would be ambiguous.");

        byte[] patched = (byte[])original.Clone();
        "vortex.stats"u8.CopyTo(patched.AsSpan(offset));
        return patched;
    }
}
