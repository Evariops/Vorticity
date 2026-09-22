// Reading real files through the layout readers: register, one ReadManyAsync, execute.
//
// The oracle is COMPOSITION. A row-major digest of a whole column must equal the digests of any
// partition of it glued together, so every split point exercises the flat reader's slice, the
// chunked reader's binary search and its concatenation against a read that used none of them. The
// split points are the corpus's own boundary row counts - 0, 1, 1023, 1024, 1025, 8191, 8192,
// 8193 - because 1024 is the FastLanes block and 8192 the default row block, and that is where the
// bugs are.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LayoutExecutionTests
{
    /// <summary>
    /// A deterministic sample: the first fully decodable file for each distinct
    /// (row count, layout shape) the corpus offers, so every boundary row count and every layout
    /// combination is exercised without reading all 819 files eleven times each.
    /// </summary>
    public static TheoryData<string> Files
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
            {
                if (!LayoutExecutor.IsFullyDecodable(entry))
                {
                    continue;
                }

                string[] layouts = (string[])entry.LayoutIds.Clone();
                Array.Sort(layouts, StringComparer.Ordinal);
                string key = entry.RowCount.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(',', layouts);
                if (keys.Add(key))
                {
                    data.Add(entry.Id);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task RangeSplitsComposeToTheWholeRead(string id)
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync(id);
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        long rowCount = file.RowCount;
        byte[] whole = await DigestAsync(file, tree, context, new RowRange(0, rowCount));

        foreach (long split in SplitPoints(rowCount))
        {
            byte[] head = await DigestAsync(file, tree, context, new RowRange(0, split));
            byte[] tail = await DigestAsync(file, tree, context, new RowRange(split, rowCount));

            byte[] glued = new byte[head.Length + tail.Length];
            head.CopyTo(glued, 0);
            tail.CopyTo(glued, head.Length);

            Assert.True(
                whole.AsSpan().SequenceEqual(glued),
                $"{id}: splitting at {split.ToString(CultureInfo.InvariantCulture)} changed the values " +
                $"({whole.Length.ToString(CultureInfo.InvariantCulture)} vs " +
                $"{glued.Length.ToString(CultureInfo.InvariantCulture)} digest bytes).");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task ThreeWaySplitComposesToTheWholeRead(string id)
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync(id);
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        long rowCount = file.RowCount;
        if (rowCount < 3)
        {
            return;
        }

        long a = rowCount / 3;
        long b = (2 * rowCount) / 3;

        byte[] whole = await DigestAsync(file, tree, context, new RowRange(0, rowCount));
        byte[] p0 = await DigestAsync(file, tree, context, new RowRange(0, a));
        byte[] p1 = await DigestAsync(file, tree, context, new RowRange(a, b));
        byte[] p2 = await DigestAsync(file, tree, context, new RowRange(b, rowCount));

        List<byte> glued = new List<byte>(whole.Length);
        glued.AddRange(p0);
        glued.AddRange(p1);
        glued.AddRange(p2);

        Assert.True(whole.AsSpan().SequenceEqual(glued.ToArray()), $"{id}: a three-way split changed the values.");
    }

    [Fact]
    public async Task EmptyRangeProducesAnEmptyBatch()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        int root = await LayoutExecutor.ReadAsync(
            file, tree, context, RowRange.Empty, FieldMask.All, TestContext.Current.CancellationToken);
        Assert.Equal(0, context.Canonical.GetNode(root).Length);
        Assert.Equal(CanonicalKind.Struct, context.Canonical.GetNode(root).Kind);
        Assert.Equal(2, context.Canonical.GetNode(root).FieldCount);
    }

    [Fact]
    public async Task SingleRowRangesReadTheRightRow()
    {
        // Three chunks of 100 rows: every chunk boundary and its neighbours are reachable.
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/chunked_stream_3");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        Assert.Equal(LayoutEncodingId.Chunked, tree.Root.Encoding);
        Assert.Equal(3, tree.Root.ChildCount);

        long rowCount = file.RowCount;
        byte[] whole = await DigestAsync(file, tree, context, new RowRange(0, rowCount));

        foreach (long row in new long[] { 0, 1, 98, 99, 100, 101, 198, 199, 200, 201, 298, 299 })
        {
            byte[] single = await DigestAsync(file, tree, context, new RowRange(row, row + 1));
            byte[] prefix = await DigestAsync(file, tree, context, new RowRange(0, row));
            byte[] suffix = await DigestAsync(file, tree, context, new RowRange(row + 1, rowCount));

            byte[] glued = new byte[prefix.Length + single.Length + suffix.Length];
            prefix.CopyTo(glued, 0);
            single.CopyTo(glued, prefix.Length);
            suffix.CopyTo(glued, prefix.Length + single.Length);

            Assert.True(
                whole.AsSpan().SequenceEqual(glued),
                $"row {row.ToString(CultureInfo.InvariantCulture)} read alone does not match the whole read.");
        }
    }

    [Fact]
    public async Task RangeBeyondTheRootIsACallerError()
    {
        await using VortexFile file = await LayoutExecutor.OpenAsync("containers/uncompressed_canonical");
        LayoutTree tree = LayoutTree.Parse(file);
        using ScanContext context = new ScanContext(file);

        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Get(root.Encoding, root.EncodingIdText);
        RowRange beyond = new RowRange(0, file.RowCount + 1);
        FieldMask all = FieldMask.All;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => reader.RegisterSegments(in root, beyond, in all, context.Segments));
    }

    private static IEnumerable<long> SplitPoints(long rowCount)
    {
        HashSet<long> points = new HashSet<long>();
        foreach (long candidate in new long[] { 0, 1, 1023, 1024, 1025, 8191, 8192, 8193, rowCount / 2, rowCount - 1, rowCount })
        {
            if (candidate >= 0 && candidate <= rowCount)
            {
                points.Add(candidate);
            }
        }

        return points;
    }

    private static async ValueTask<byte[]> DigestAsync(
        VortexFile file, LayoutTree tree, ScanContext context, RowRange rows)
    {
        try
        {
            int root = await LayoutExecutor.ReadAsync(file, tree, context, rows, FieldMask.All);
            Assert.Equal(rows.Length, context.Canonical.GetNode(root).Length);
            return CanonicalDigest.Of(context, root);
        }
        finally
        {
            context.ResetBatch();
        }
    }
}
