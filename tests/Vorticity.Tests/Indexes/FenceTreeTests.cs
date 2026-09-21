// Hierarchical fences: a long run's segment table written as a tree of fence pages.
//
// WHAT IS HELD: a fence's size is computed as its page writes it; a synthetic run of 10⁸ entries is
// found through one fence page and one of 10¹⁰ through two, and the fan-out of the default pages
// covers 10¹² entries in two levels; keys larger than a page deepen the tree and never stop it; a
// long run written with its table in pages reads, walks and prunes exactly as the same run written
// inline, through a tree several levels deep; the directory keeps its size whatever the run's
// length; an append merges a paged run back; and a torn page claims nothing for its run and makes a
// key source refuse.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Serialization.Protobuf;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class FenceTreeTests
{
    private const int Block = 256;

    /// <summary>A sorted run's segment in the synthetic runs, at the default size.</summary>
    private const int SegmentEntries = 65_536;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["k", "s"],
        [Types.Primitive(PType.I64, Nullability.NonNullable), Types.Utf8(Nullability.NonNullable)],
        Nullability.NonNullable);

    private static readonly KeyLayout Unsigned = new KeyLayout(KeyShape.Unsigned, 8, PType.U64);

    private static readonly KeyLayout Signed = new KeyLayout(KeyShape.Signed, 8, PType.I64);

    private static readonly KeyLayout Bytes = new KeyLayout(KeyShape.Bytes, 0, default);

    /// <summary>A tree several levels deep over a few hundred segments.</summary>
    private static FenceShape Deep => new FenceShape(InlineFences: 4, PageBytes: 512);

    /// <summary>No run is ever paged: the oracle file.</summary>
    private static FenceShape Never => new FenceShape(int.MaxValue, 64 << 10);

    private static long K(int row) => (row * 7919L) % 100_003;

    private static string S(int row) => "s" + ((row * 31) % 509).ToString(CultureInfo.InvariantCulture);

    /// <summary>A sorted run of 16 entries a segment, postings of 8.</summary>
    private static WritePolicy Policy => WritePolicy.None
        .For("k", IndexSpec.SortedRuns.WithSegmentEntries(16))
        .For("s", IndexSpec.Postings.WithSegmentEntries(8));

    // ------------------------------------------------------------------------------ the pages

    [Fact]
    public void AFencesSizeIsTheBytesItsPageWrites()
    {
        Random random = new Random(23);
        foreach (int level in (int[])[0, 1, 3])
        {
            for (int count = 1; count < 60; count += 11)
            {
                int stride = level == 0 ? KeyRunOptions.PostingsStride : 1;
                string[] keys = [.. Enumerable.Range(0, count * 2)
                    .Select(_ => new string('k', random.Next(0, 300)) + random.Next().ToString(CultureInfo.InvariantCulture))
                    .Order(StringComparer.Ordinal)];
                KeySegment[] bounds = new KeySegment[count];
                IndexSegment[][] regions = new IndexSegment[count][];
                long[] segments = new long[count];
                for (int i = 0; i < count; i++)
                {
                    bounds[i] = new KeySegment(
                        (ulong)random.NextInt64(1, 1L << 50), Encoding.UTF8.GetBytes(keys[2 * i]), Encoding.UTF8.GetBytes(keys[(2 * i) + 1]));
                    regions[i] = new IndexSegment[stride];
                    for (int a = 0; a < stride; a++)
                    {
                        regions[i][a] = new IndexSegment(
                            (ulong)random.NextInt64(0, 1L << 60),
                            (uint)random.Next(),
                            (byte)(random.Next(2) * 16),
                            random.Next(2) == 0 ? null : (ulong)random.NextInt64());
                    }

                    segments[i] = level == 0 ? 1 : random.NextInt64(1, 1L << 40);
                }

                FencePage page = FencePage.Of(level, bounds, regions, segments);
                byte[] written = page.ToBytes();
                int expected = FencePage.HeaderBytes(level);
                for (int i = 0; i < count; i++)
                {
                    expected += FencePage.FenceBytes(bounds[i], regions[i], segments[i], level);
                }

                Assert.Equal(expected, written.Length);

                FencePage back = FencePage.Read(new ProtoReader(written), stride, level, Bytes);
                Assert.Equal(level, back.Level);
                Assert.Equal(page.EntryStarts, back.EntryStarts);
                Assert.Equal(page.SegmentStarts, back.SegmentStarts);
                for (int i = 0; i < count; i++)
                {
                    AssertSame(bounds[i], back.Bounds[i]);
                    Assert.Equal(regions[i], back.Regions[i]);
                }
            }
        }
    }

    [Fact]
    public async Task ARunOfAHundredMillionEntriesIsFoundThroughOnePage()
    {
        // 10⁸ entries at 65 536 a segment: 1 526 fences of 74 bytes, two leaf pages, a root of two.
        Synthetic run = Synthetic.Build(1_526, KeyRunOptions.SortedStride, FenceShape.Default);
        Assert.Equal(1, run.Root.Level);
        Assert.Equal((2, 2), (run.Pages, run.Root.Count));
        Assert.Equal(74, FencePage.FenceBytes(run.Bounds[700], run.Regions[700], 1, 0));
        await AssertLookupsAsync(run, pages: 1);
    }

    [Fact]
    public async Task ARunOfTenBillionEntriesIsFoundThroughTwoPages()
    {
        // 10¹⁰ entries: 152 588 postings fences of 98 bytes, 229 leaf pages under one page of
        // pages, a root of one.
        Synthetic run = Synthetic.Build(152_588, KeyRunOptions.PostingsStride, FenceShape.Default);
        Assert.Equal(2, run.Root.Level);
        Assert.Equal((230, 1), (run.Pages, run.Root.Count));
        Assert.Equal(98, FencePage.FenceBytes(run.Bounds[700], run.Regions[700], 1, 0));
        await AssertLookupsAsync(run, pages: 2);
    }

    [Fact]
    public void TheDefaultPagesCoverATrillionEntriesInTwoLevels()
    {
        // The largest fences a run of 10¹² entries writes -- offsets past 2⁴⁴, arrays of a
        // mebibyte, entry counts at their widest -- still leave pages of hundreds: a root of 64
        // over two levels of them holds 10¹² entries.
        FenceShape shape = FenceShape.Default;
        byte[] widest = Key(ulong.MaxValue);
        foreach (int stride in (int[])[KeyRunOptions.SortedStride, KeyRunOptions.PostingsStride])
        {
            IndexSegment payload = new IndexSegment(1UL << 44, 1U << 20, 16, ulong.MaxValue);
            int leaf = FencePage.FenceBytes(
                new KeySegment(SegmentEntries, widest, widest), [.. Enumerable.Repeat(payload, stride)], 1, 0);
            long leavesPerPage = (shape.PageBytes - FencePage.HeaderBytes(0)) / leaf;

            IndexSegment page = new IndexSegment(1UL << 44, (uint)shape.PageBytes, 0, ulong.MaxValue);
            int node = FencePage.FenceBytes(
                new KeySegment((ulong)(leavesPerPage * SegmentEntries), widest, widest), [page], leavesPerPage, 1);
            long nodesPerPage = (shape.PageBytes - FencePage.HeaderBytes(1)) / node;

            long entries = shape.InlineFences * nodesPerPage * leavesPerPage * SegmentEntries;
            Assert.True(
                entries >= 1_000_000_000_000L,
                $"stride {stride}: fences of {leaf} and {node} bytes, {leavesPerPage} and {nodesPerPage} a page, {entries} entries in two levels");
        }
    }

    [Fact]
    public async Task KeysLargerThanAPageDeepenTheTreeAndItStillEnds()
    {
        // Every page takes two fences whatever their size, so every level halves: 40, 20, 10, 5,
        // 3, then a root of 2.
        FenceShape shape = new FenceShape(InlineFences: 2, PageBytes: 512);
        List<KeySegment> bounds = [];
        List<IndexSegment[]> regions = [];
        for (int s = 0; s < 40; s++)
        {
            bounds.Add(new KeySegment(10, LongKey(s, 'a'), LongKey(s, 'z')));
            regions.Add([new IndexSegment((ulong)(s * 100), 50, 0, (ulong)s), new IndexSegment((ulong)(s * 100) + 50, 50, 0, (ulong)s)]);
        }

        Synthetic run = Synthetic.Write(bounds, regions, KeyRunOptions.SortedStride, shape, Bytes);
        Assert.Equal(5, run.Root.Level);
        Assert.Equal(20 + 10 + 5 + 3 + 2, run.Pages);

        FenceTable oracle = FenceTable.InMemory([.. bounds], Bytes);
        for (int s = 0; s < 40; s++)
        {
            foreach (byte[] key in (byte[][])[LongKey(s, 'a'), LongKey(s, 'm'), LongKey(s, '~')])
            {
                Assert.True(FenceTable.TryOpen(run.Run, KeyRunOptions.SortedStride, Bytes, out FenceTable? table, out string? reason), reason);
                long expected = await oracle.LowerBoundAsync(run.Source, new Probe(Bytes, key), default);
                Assert.Equal(expected, await table!.LowerBoundAsync(run.Source, new Probe(Bytes, key), default));
                Assert.True(table.PagesRead <= 5);
            }
        }
    }

    // ------------------------------------------------------------------------------ in a file

    [Fact]
    public async Task ALongRunInPagesReadsAsTheSameRunInline()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        Guid identity = Guid.NewGuid();
        (byte[] paged, WriteReport pagedReport) = await WriteAsync(rows, Deep, identity);
        (byte[] inline, WriteReport inlineReport) = await WriteAsync(rows, Never, identity);

        await using VortexFile file = await OpenAsync(paged);
        await using VortexFile oracle = await OpenAsync(inline);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
        IndexDirectory inlineDirectory = (await oracle.ReadIndexDirectoryAsync())!;
        Assert.Equal(2, directory.Entries.Count);
        int depth = 0;
        foreach (IndexEntry entry in directory.Entries)
        {
            int stride = KeyRunOptions.StrideOf(entry.Kind);
            KeyLayout layout = entry.Kind == IndexKinds.SortedRuns ? Signed : Bytes;
            IndexRun run = Assert.Single(entry.Runs);
            IndexRun inlineRun = Assert.Single(inlineDirectory.Entries.Single(e => e.Kind == entry.Kind).Runs);

            // The table is in pages: a root inline, its children listed, no dtype per region.
            Assert.False(KeyRunOptions.TryParseRun(run.OptionBytes, out _));
            Assert.True(KeyRunOptions.TryParsePagedRun(run.OptionBytes, stride, layout, out FencePage? root, out byte[][]? dtypes));
            Assert.True(root!.Level >= 2, $"{entry.Kind}: a root of level {root.Level}");
            Assert.Equal(root.Count, run.Payload.Count);
            Assert.Empty(run.PayloadDTypes);
            Assert.Equal(stride, dtypes!.Length);
            Assert.Equal((inlineRun.FirstBlock, inlineRun.BlockCount, inlineRun.EntryCount), (run.FirstBlock, run.BlockCount, run.EntryCount));
            depth = entry.Kind == IndexKinds.SortedRuns ? root.Level : depth;

            // Every fence, read through the pages, is the inline file's: the payloads went out
            // before any page, so even their regions are the same.
            Assert.True(FenceTable.TryOpen(run, stride, layout, out FenceTable? table, out string? reason), reason);
            Assert.True(FenceTable.TryOpen(inlineRun, stride, layout, out FenceTable? expected, out reason), reason);
            Assert.True(table!.Paged);
            Assert.False(expected!.Paged);
            Assert.Equal(expected.SegmentCount, table.SegmentCount);
            Assert.Equal(expected.Entries, table.Entries);
            Assert.Equal(expected.WideRows, table.WideRows);
            for (long s = 0; s < table.SegmentCount; s++)
            {
                Fence got = await table.GetAsync(file.IndexSourceOf(run), s, default);
                Fence want = await expected.GetAsync(oracle.IndexSourceOf(inlineRun), s, default);
                Assert.Equal((want.Index, want.Start), (got.Index, got.Start));
                AssertSame(want.Bounds, got.Bounds);
                Assert.Equal(want.Regions, got.Regions);
                Assert.Equal(s, (await table.OfPositionAsync(file.IndexSourceOf(run), got.Start, default)).Index);
            }
        }

        // 640 segments in pages of 512 bytes: three levels of pages under the root.
        Assert.Equal(3, depth);

        // The pages count as index bytes, and only the tail differs.
        Assert.True(
            pagedReport.Index("k", IndexKinds.SortedRuns)!.Bytes > inlineReport.Index("k", IndexKinds.SortedRuns)!.Bytes);
        Assert.Equal(pagedReport.Bytes.Data, inlineReport.Bytes.Data);

        await AssertWalkAsync(file, rows);
        await AssertDistinctAsync(file, rows);

        // A key strictly inside its segment: the descent reads a page a level, then the keys and
        // the rows; inline, the keys and the rows alone. The last segment of a leaf page ends past
        // its key, so the next page is not read either.
        List<long> sorted = [.. Enumerable.Range(0, rows).Select(K).Order()];
        (IndexSegment firstLeaf, _) = (await PagesAsync(paged)).First(p => p.Level == 0);
        int perLeaf = FencePage.Read(
            new ProtoReader(paged.AsSpan((int)firstLeaf.Offset, (int)firstLeaf.Length)), KeyRunOptions.SortedStride, 0, Signed).Count;
        Assert.InRange(perLeaf, 2, 639);
        foreach (int at in (int[])[5, ((perLeaf - 1) * 16) + 7, (300 * 16) + 7, (639 * 16) + 3])
        {
            VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(sorted[at])));
            Assert.Equal(depth + 2, await SegmentsReadAsync(file, equal));
            Assert.Equal(2, await SegmentsReadAsync(oracle, equal));
            Assert.Equal(1, await CountRowsAsync(file, equal, indexes: true));
        }

        foreach (int row in (int[])[0, 4_242, rows - 1])
        {
            VortexExpr text = Expr.Eq(Expr.Field("s"), Expr.Literal(FilterLiteral.From(S(row))));
            Assert.Equal(await CountRowsAsync(file, text, indexes: false), await CountRowsAsync(file, text, indexes: true));
        }

        VortexExpr absent = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(100_004L)));
        Assert.Equal(0, await CountRowsAsync(file, absent, indexes: true));
    }

    [Fact]
    public async Task TheDefaultShapePagesARunPastSixtyFourSegmentsAndALookupReadsOnePage()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        (byte[] bytes, _) = await WriteAsync(rows, FenceShape.Default);
        await using VortexFile file = await OpenAsync(bytes);
        IndexEntry entry = (await file.ReadIndexDirectoryAsync())!.Entries.Single(e => e.Kind == IndexKinds.SortedRuns);
        IndexRun run = Assert.Single(entry.Runs);
        Assert.True(KeyRunOptions.TryParsePagedRun(run.OptionBytes, KeyRunOptions.SortedStride, Signed, out FencePage? root, out _));
        Assert.Equal(1, root!.Level);
        Assert.All(run.Payload, page => Assert.InRange(page.Length, 1U, (uint)FenceShape.Default.PageBytes));

        // 64 segments or fewer stay inline: the postings run of 64 segments.
        IndexEntry postings = (await file.ReadIndexDirectoryAsync())!.Entries.Single(e => e.Kind == IndexKinds.PostingsBlocks);
        Assert.True(KeyRunOptions.TryParseRun(Assert.Single(postings.Runs).OptionBytes, out List<KeySegment> segments));
        Assert.Equal(64, segments.Count);

        long key = K(1_234);
        VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(key)));
        Assert.Equal(3, await SegmentsReadAsync(file, equal));
        Assert.Equal(1, await CountRowsAsync(file, equal, indexes: true));
        await AssertWalkAsync(file, rows);
    }

    [Fact]
    public async Task TheDirectoryKeepsItsSizeWhateverTheRunsLength()
    {
        Decoders.EnsureRegistered();
        int[] sizes = new int[3];
        int[] inline = new int[3];
        int[] blocks = [20, 80, 320];
        for (int i = 0; i < blocks.Length; i++)
        {
            sizes[i] = await DirectoryBytesAsync(Block * blocks[i], Deep);
            inline[i] = await DirectoryBytesAsync(Block * blocks[i], Never);
        }

        // Inline, the directory grows with the segments; paged, its root holds at most four fences
        // whatever their number, and the directory stays within a few hundred bytes of itself.
        Assert.True(inline[2] > 16 * inline[0] / 2, $"inline: {string.Join(", ", inline)}");
        Assert.True(sizes.Max() - sizes.Min() < 400, $"paged: {string.Join(", ", sizes)}");
        Assert.True(sizes[2] * 20 < inline[2], $"paged {sizes[2]} against inline {inline[2]}");
    }

    [Fact]
    public async Task AnAppendMergesAPagedRunBackAndKeepsTheOthersPaged()
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-fences-{Guid.NewGuid():N}.vortex");
        try
        {
            int rows = 1_000;
            (byte[] first, _) = await WriteAsync(rows, Deep);
            await System.IO.File.WriteAllBytesAsync(path, first);
            int paged = 0;
            for (int append = 0; append < 7; append++)
            {
                int added = 900 + (append * 53);
                VortexWriteOptions options = Options(Deep, null);
                await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options))
                {
                    await FeedAsync(writer, rows, rows + added);
                    await writer.CompleteAsync();
                }

                rows += added;
                await using VortexFile file = await VortexFile.OpenAsync(path);
                IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
                IndexEntry entry = directory.Entries.Single(e => e.Kind == IndexKinds.SortedRuns);
                Assert.InRange(entry.Runs.Count, 1, KeyIndexBuilder.MaxRuns);
                long entries = 0;
                foreach (IndexRun run in entry.Runs)
                {
                    Assert.True(FenceTable.TryOpen(run, KeyRunOptions.SortedStride, Signed, out FenceTable? table, out string? reason), reason);
                    Assert.Equal(Deep.Pages((int)table!.SegmentCount), table.Paged);
                    paged += table.Paged ? 1 : 0;
                    entries += table.Entries;
                }

                Assert.Equal(rows, entries);
                await AssertWalkAsync(file, rows);
                await AssertDistinctAsync(file, rows);
            }

            Assert.True(paged > 7, $"{paged} paged runs over seven versions");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATornPageClaimsNothingAndMakesAKeySourceRefuse()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        (byte[] original, _) = await WriteAsync(rows, Deep);
        List<(IndexSegment Page, int Level)> pages = await PagesAsync(original);
        Assert.True(pages.Count > 20, $"only {pages.Count} pages");

        VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(K(777))));
        foreach (int level in (int[])[pages.Max(p => p.Level), 0])
        {
            (IndexSegment page, _) = pages.First(p => p.Level == level);
            byte[] torn = (byte[])original.Clone();
            torn[(int)page.Offset + ((int)page.Length / 2)] ^= 0x5A;

            await using VortexFile file = await OpenAsync(torn);
            Assert.Equal(1, await CountRowsAsync(file, equal, indexes: true));
            await Assert.ThrowsAsync<VortexFormatException>(() => AssertWalkAsync(file, rows));
        }
    }

    [Fact]
    public async Task MutatedPagesFailCleanlyAndNeverLoseARow()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        (byte[] original, _) = await WriteAsync(rows, Deep);
        List<(IndexSegment Page, int Level)> pages = await PagesAsync(original);
        List<long> sorted = [.. Enumerable.Range(0, rows).Select(K).Order()];
        Random random = new Random(20260917);
        int refused = 0;
        for (int i = 0; i < 60; i++)
        {
            byte[] bytes = (byte[])original.Clone();
            (IndexSegment page, _) = pages[random.Next(pages.Count)];
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
            {
                bytes[(int)page.Offset + random.Next((int)page.Length)] ^= (byte)random.Next(1, 256);
            }

            await using VortexFile file = await OpenAsync(bytes);
            long key = sorted[random.Next(rows)];
            VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(key)));
            Assert.Equal(1, await CountRowsAsync(file, equal, indexes: true));
            try
            {
                await AssertWalkAsync(file, rows);
            }
            catch (VortexFormatException)
            {
                refused++;
            }
        }

        // A full walk reads every page, and every mutation is caught by a checksum.
        Assert.Equal(60, refused);
    }

    // ------------------------------------------------------------------------------ synthetic runs

    /// <summary>A run whose table was written into memory, and the bounds it was built over.</summary>
    private sealed record Synthetic(
        MemorySegmentSource Source, IndexRun Run, FencePage Root, KeySegment[] Bounds, IndexSegment[][] Regions,
        int Pages, KeyLayout Layout, int Stride)
    {
        /// <summary>
        /// <paramref name="segments"/> segments of 65 536 entries: keys 0 to 999 in the first,
        /// 2 000 to 2 999 in the second, and so on.
        /// </summary>
        internal static Synthetic Build(int segments, int stride, FenceShape shape)
        {
            List<KeySegment> bounds = new List<KeySegment>(segments);
            List<IndexSegment[]> regions = new List<IndexSegment[]>(segments);
            for (long s = 0; s < segments; s++)
            {
                bounds.Add(new KeySegment(SegmentEntries, Key((ulong)(s * 2_000)), Key((ulong)((s * 2_000) + 999))));
                IndexSegment[] arrays = new IndexSegment[stride];
                for (int a = 0; a < stride; a++)
                {
                    arrays[a] = new IndexSegment(
                        (ulong)(1_000_000_000_000L + (s * 400_000) + (a * 130_000)), 130_000, 16, (ulong)((s * 31) + a));
                }

                regions.Add(arrays);
            }

            return Write(bounds, regions, stride, shape, Unsigned);
        }

        internal static Synthetic Write(
            List<KeySegment> bounds, List<IndexSegment[]> regions, int stride, FenceShape shape, KeyLayout layout)
        {
            FenceTreeWriter tree = new FenceTreeWriter(shape, bounds, regions);
            using MemoryStream file = new MemoryStream();
            while (tree.TryTake(out byte[] page))
            {
                long offset = file.Position;
                file.Write(page);
                tree.Placed(IndexSegment.Of(offset, page, 0));
            }

            FencePage root = tree.Root!;
            IndexSegment[] children = [.. root.Regions.Select(r => r[0])];
            byte[][] dtypes = [.. Enumerable.Range(0, stride).Select(_ => DTypeFlatBuffers.Serialize(Types.Primitive(PType.U32, Nullability.NonNullable)))];
            ulong entries = 0;
            foreach (KeySegment segment in bounds)
            {
                entries += segment.Entries;
            }

            IndexRun run = new IndexRun(0, (uint)bounds.Count, children, [], entries, KeyRunOptions.PagedRun(root, dtypes));
            return new Synthetic(
                new MemorySegmentSource(file.ToArray()), run, root, [.. bounds], [.. regions], tree.Pages, layout, stride);
        }
    }

    private static async Task AssertLookupsAsync(Synthetic run, int pages)
    {
        FenceTable oracle = FenceTable.InMemory(run.Bounds, run.Layout);
        Random random = new Random(29);
        ulong top = (ulong)(run.Bounds.Length * 2_000L);
        for (int i = 0; i < 300; i++)
        {
            byte[] key = Key(i switch
            {
                0 => 0,
                1 => top - 1_001,
                2 => top,
                _ => (ulong)random.NextInt64(0, (long)top + 10),
            });

            // A table opened afresh: its page cache is empty.
            Assert.True(FenceTable.TryOpen(run.Run, run.Stride, run.Layout, out FenceTable? table, out string? reason), reason);
            Assert.Equal(run.Bounds.Length, table!.SegmentCount);
            long expected = await oracle.LowerBoundAsync(run.Source, new Probe(run.Layout, key), default);
            long found = await table.LowerBoundAsync(run.Source, new Probe(run.Layout, key), default);
            Assert.Equal(expected, found);
            if (found == table.SegmentCount)
            {
                // Past the last key: the root says so without a page.
                Assert.Equal(0, table.PagesRead);
                continue;
            }

            Assert.Equal(pages, table.PagesRead);

            // The segment the descent found is read from the pages it already holds.
            Fence fence = await table.GetAsync(run.Source, found, default);
            Assert.Equal(pages, table.PagesRead);
            AssertSame(run.Bounds[found], fence.Bounds);
            Assert.Equal(run.Regions[found], fence.Regions);
            Assert.Equal(found * SegmentEntries, fence.Start);
            Fence byPosition = await table.OfPositionAsync(run.Source, fence.Start + 777, default);
            Assert.Equal(found, byPosition.Index);
        }
    }

    private readonly struct Probe(KeyLayout layout, byte[] key) : IFenceProbe
    {
        public bool Below(ReadOnlySpan<byte> max) => layout.Compare(max, key) < 0;
    }

    private static byte[] Key(ulong value)
    {
        byte[] key = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, value);
        return key;
    }

    /// <summary>A key of a kibibyte and more: segment <paramref name="s"/>'s, ordered by it.</summary>
    private static byte[] LongKey(int s, char tail) =>
        Encoding.ASCII.GetBytes(s.ToString("D4", CultureInfo.InvariantCulture) + new string('x', 1_100) + tail);

    private static void AssertSame(KeySegment expected, KeySegment actual)
    {
        Assert.Equal(expected.Entries, actual.Entries);
        Assert.Equal(expected.Min, actual.Min);
        Assert.Equal(expected.Max, actual.Max);
    }

    // ------------------------------------------------------------------------------ oracles

    /// <summary>Every fence page of the sorted run's tree, with its level, read from the file's bytes.</summary>
    private static async Task<List<(IndexSegment Page, int Level)>> PagesAsync(byte[] bytes)
    {
        await using VortexFile file = await OpenAsync(bytes);
        IndexEntry entry = (await file.ReadIndexDirectoryAsync())!.Entries.Single(e => e.Kind == IndexKinds.SortedRuns);
        List<(IndexSegment Page, int Level)> pages = [];
        foreach (IndexRun run in entry.Runs)
        {
            Assert.True(KeyRunOptions.TryParsePagedRun(run.OptionBytes, KeyRunOptions.SortedStride, Signed, out FencePage? root, out _));
            Collect(root!);
        }

        return pages;

        void Collect(FencePage parent)
        {
            if (parent.Level == 0)
            {
                return;
            }

            foreach (IndexSegment[] child in parent.Regions)
            {
                pages.Add((child[0], parent.Level - 1));
                ReadOnlySpan<byte> region = bytes.AsSpan((int)child[0].Offset, (int)child[0].Length);
                Collect(FencePage.Read(new ProtoReader(region), KeyRunOptions.SortedStride, parent.Level - 1, Signed));
            }
        }
    }

    private static async Task AssertWalkAsync(VortexFile file, int rows)
    {
        List<(long Key, long Row)> expected = [.. Enumerable.Range(0, rows).Select(r => (K(r), (long)r)).Order()];
        await using KeyCursor cursor = await file.Keys("k").WithSource(KeySourceKind.SortedRuns).OpenAsync();
        List<(long Key, long Row)> walked = [];
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            walked.Add((cursor.Key.SignedValue, cursor.Row));
        }

        Assert.Equal(expected, walked);

        // Backward, and seeks into the middle.
        for (int at = 0; at < rows; at += rows / 7)
        {
            long key = expected[at].Key;
            Assert.True(await cursor.SeekAsync(FilterLiteral.From(key), SeekOp.AtOrAfter));
            Assert.Equal(expected[at].Row, cursor.Row);
        }

        Assert.True(await cursor.SeekLastAsync());
        Assert.Equal(expected[^1].Row, cursor.Row);
        Assert.True(await cursor.PrevAsync());
        Assert.Equal(expected[^2].Row, cursor.Row);
        Assert.Equal(rows, cursor.EntryCount);

        // A rank is a position in the run: the descent by entries.
        foreach (int rank in (int[])[0, 17, rows / 3, rows - 1])
        {
            Assert.True(await cursor.SeekRankAsync(rank));
            Assert.Equal(expected[rank].Row, cursor.Row);
        }
    }

    private static async Task AssertDistinctAsync(VortexFile file, int rows)
    {
        List<string> expected = [.. Enumerable.Range(0, rows).Select(S).Distinct().Order(StringComparer.Ordinal)];
        await using KeyCursor cursor = await file.Keys("s").Distinct().WithSource(KeySourceKind.Postings).OpenAsync();
        List<string> walked = [];
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            walked.Add(Encoding.UTF8.GetString(cursor.KeyBytes));
        }

        Assert.Equal(expected, walked);
    }

    private static async Task<int> SegmentsReadAsync(VortexFile file, VortexExpr filter)
    {
        ScanExplanation plan = await file.ScanBuilder().Where(filter).ExplainAsync();
        return Assert.Single(plan.Pruning, step => step.Structure == "locating index").SegmentsRead;
    }

    private static async Task<long> CountRowsAsync(VortexFile file, VortexExpr filter, bool indexes)
    {
        long count = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).WithIndexes(indexes).WithPruning(indexes).ExecuteAsync())
        {
            count += batch.RowCount;
        }

        return count;
    }

    // ------------------------------------------------------------------------------ the file

    private static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);

    private static async Task<int> DirectoryBytesAsync(int rows, FenceShape shape)
    {
        (byte[] bytes, _) = await WriteAsync(rows, shape);
        await using VortexFile file = await OpenAsync(bytes);
        return (await file.ReadIndexDirectoryAsync())!.ToBytes().Length;
    }

    private static VortexWriteOptions Options(FenceShape shape, Guid? identity) => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = null,
        WritePolicy = Policy,
        IndexBudgetPerMille = 1_000_000,
        Identity = identity,
        Fences = shape,
    };

    private static async Task<(byte[] Bytes, WriteReport Report)> WriteAsync(int rows, FenceShape shape, Guid? identity = null)
    {
        using MemoryStream stream = new MemoryStream();
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, Options(shape, identity)))
        {
            await FeedAsync(writer, 0, rows);
            report = await writer.CompleteAsync();
        }

        return (stream.ToArray(), report);
    }

    /// <summary>Rows <c>[start, end)</c>, a block at a time.</summary>
    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        for (int from = start; from < end; from += Block)
        {
            int count = Math.Min(Block, end - from);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                int root = arena.AddStruct(
                    Schema, count, Validity.NonNullable, [Longs(arena, from, count), Strings(arena, from, count)]);
                using RecordBatch batch = new RecordBatch(arena, root, from);
                await writer.WriteAsync(batch);
            }
            finally
            {
                arena.Reset();
            }
        }
    }

    private static int Longs(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = K(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Strings(CanonicalArena arena, int start, int count)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(S(start + i));
            Span<byte> view = bytes.Slice(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(Schema.GetField(1), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
