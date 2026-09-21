// The filter tree: per-block Bloom filters gathered under nodes of fan-out sixteen.
//
// WHAT IS HELD: a node reads back as written and nothing else reads as one; the tree has the shape
// its blocks call for, and no filter anywhere in it misses a value beneath it; a point probe reads
// one region a level, a value absent from the file reads the root alone; a node past its ceiling has
// no filter and the probe goes through it; `Auto` gives up a column whose first generation passes a
// node; an append cuts the old tree and probes both; a torn or mutated region costs pruning and
// never a row; and the directory does not grow with the blocks.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class BloomTreeTests
{
    /// <summary>Eight rows a block: a block of unique keys holds exactly the floor's eight values.</summary>
    private const int Block = 8;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["k"], [Types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);

    /// <summary>Unique, and scattered so that no zone map prunes a key.</summary>
    private static long K(int row) => (row * 2_654_435_761L) % 4_294_967_311L;

    /// <summary>A value in the middle of the keys' range that no row of these files holds: the zone maps keep it.</summary>
    private static readonly long Absent = FindAbsent();

    private static long FindAbsent()
    {
        HashSet<long> keys = [.. Enumerable.Range(0, 40_000).Select(K)];
        long value = 2_147_483_647;
        while (keys.Contains(value))
        {
            value++;
        }

        return value;
    }

    /// <summary>A rate low enough that a descent of three levels meets no false positive here.</summary>
    private static IndexSpec Policy(int maxBlocks = IndexSpec.DefaultMaxBlocks, int resolutions = 2) =>
        IndexSpec.Bloom(falsePositivePpm: 100, resolutions: resolutions, maxBlocks: maxBlocks);

    // ------------------------------------------------------------------------------ the node

    [Fact]
    public void ANodeReadsBackAsWrittenAndRefusesWhatItIsNot()
    {
        IndexSegment region = new IndexSegment(0x1_2345_6789, 4_000, 6, 0xDEAD_BEEF_0123_4567);
        uint[] filter = [.. Enumerable.Range(1, 16).Select(i => (uint)(i * 0x01010101))];
        int[] children = [30, 30, 25];
        uint[] words = new uint[BloomNode.WordsOf(children.Length, 2)];
        BloomNode.Write(words, 2, 40, region, children, filter);

        Assert.True(BloomNode.TryRead(words, 0, words.Length, 2, 40, out BloomNode? node));
        Assert.Equal((2, 40L, 3, 2), (node!.Level, node.Leaves, node.ChildCount, node.FilterBlocks));
        Assert.Equal(region, node.Children);
        Assert.Equal([30u, 30u, 25u], node.ChildWords.ToArray());
        Assert.Equal(filter, node.Filter.ToArray());

        // Anything its parent does not say it is.
        Assert.False(BloomNode.TryRead(words, 0, words.Length, 1, 40, out _));
        Assert.False(BloomNode.TryRead(words, 0, words.Length, 2, 41, out _));
        Assert.False(BloomNode.TryRead(words, 0, words.Length - 1, 2, 40, out _));
        Assert.False(BloomNode.TryRead(words, 1, words.Length - 1, 2, 40, out _));
        foreach ((int at, uint value) in (ReadOnlySpan<(int, uint)>)[(0, words[0] + 1), (0, words[0] | (2u << 24)), (0, words[0] ^ (1u << 16)), (2, 3), (5, 3), (9, 3)])
        {
            uint[] lie = (uint[])words.Clone();
            lie[at] = value;
            Assert.False(BloomNode.TryRead(lie, 0, lie.Length, 2, 40, out _), $"word {at} = {value:X}");
        }

        // A bare level-1 node lists no child, names no region, and a non-bare one lists a word.
        int[] none = new int[16];
        uint[] bare = new uint[BloomNode.WordsOf(0, 1)];
        BloomNode.Write(bare, 1, 16, null, none, filter.AsSpan(0, 8));
        Assert.True(BloomNode.TryRead(bare, 0, bare.Length, 1, 16, out BloomNode? leafless));
        Assert.True(leafless!.ChildWords.IsEmpty);
        Assert.Null(leafless.Children);
        Assert.Equal(16, leafless.ChildCount);
        uint[] named = (uint[])bare.Clone();
        named[3] = 64;
        Assert.False(BloomNode.TryRead(named, 0, named.Length, 1, 16, out _));
        uint[] listed = new uint[BloomNode.WordsOf(16, 1)];
        BloomNode.Write(listed, 1, 16, null, [8, .. new int[15]], filter.AsSpan(0, 8));
        Assert.False(BloomNode.TryRead(listed, 0, listed.Length, 1, 16, out _), "a leaf's words with no region");
        listed[BloomNode.HeaderWords] = 0;
        Assert.False(BloomNode.TryRead(listed, 0, listed.Length, 1, 16, out _), "an all-zero list");
        listed[BloomNode.HeaderWords] = 7;
        Assert.False(BloomNode.TryRead(listed, 0, listed.Length, 1, 16, out _), "a leaf of seven words");

        // The shape's arithmetic.
        Assert.Equal((1, 1, 2, 2, 3), (BloomNode.LevelFor(1), BloomNode.LevelFor(16), BloomNode.LevelFor(17), BloomNode.LevelFor(256), BloomNode.LevelFor(257)));
        Assert.Equal((16, 1), (BloomNode.ChildrenOf(1, 16), BloomNode.ChildrenOf(2, 16)));
        Assert.Equal((16L, 9L), (BloomNode.ChildLeaves(2, 41, 1), BloomNode.ChildLeaves(2, 41, 2)));
    }

    // ------------------------------------------------------------------------------ the tree

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(256)]
    [InlineData(300)]
    public async Task TheTreeHasTheShapeItsBlocksCallForAndMissesNoValue(int blocks)
    {
        Decoders.EnsureRegistered();
        (byte[] bytes, WriteReport report) = await WriteAsync(blocks * Block, Policy());
        await using VortexFile file = await OpenAsync(bytes);
        IndexRun run = Assert.Single(Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries).Runs);
        Assert.Equal((0UL, (uint)blocks), (run.FirstBlock, run.BlockCount));

        List<Walked> nodes = await WalkAsync(file, run);
        Walked root = nodes[0];
        Assert.Equal(BloomNode.LevelFor(blocks), root.Node.Level);
        Assert.Equal(blocks, root.Node.Leaves);

        // Every level holds ceil(blocks / 16^level) nodes, each with its filter.
        int internals = 0;
        for (int level = 1; level <= root.Node.Level; level++)
        {
            long span = 1L << (4 * level);
            Assert.Equal((blocks + span - 1) / span, nodes.Count(n => n.Node.Level == level));
            internals += (int)((blocks + span - 1) / span);
        }

        IndexWriteReport index = Assert.IsType<IndexWriteReport>(report.Index("k", IndexKinds.BloomSbbf));
        Assert.Equal((1, internals), (index.Runs, index.Generations));

        // No filter misses a value under it: a node's range, a leaf's block.
        foreach (Walked walked in nodes)
        {
            for (long b = walked.FirstBlock; b < walked.FirstBlock + walked.Node.Leaves; b++)
            {
                for (int row = (int)b * Block; row < (b + 1) * Block; row++)
                {
                    Assert.True(Holds(walked.Node.Filter, K(row)), $"level {walked.Node.Level} misses row {row}");
                    if (walked.Node.Level == 1)
                    {
                        Assert.True(Holds(walked.Leaves[b - walked.FirstBlock], K(row)), $"leaf {b} misses row {row}");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task APointProbeReadsOneRegionALevelAndAnAbsentValueTheRootAlone()
    {
        Decoders.EnsureRegistered();
        int blocks = 4_096;
        (byte[] bytes, _) = await WriteAsync(blocks * Block, Policy());
        await using VortexFile file = await OpenAsync(bytes);

        // Three levels above the leaves: the root, the sixteen nodes under it, the sixteen under the
        // one that holds the key, then its sixteen leaves.
        foreach (int row in (int[])[0, 12_345, (blocks * Block) - 1])
        {
            ScanExplanation plan = await file.ScanBuilder().Where(Equal(K(row))).ExplainAsync();
            PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");
            Assert.Equal(4, bloom.SegmentsRead);
            Assert.Equal(1, plan.LiveBlocks);
            Assert.Equal(1, await CountAsync(file, Equal(K(row)), indexes: true));
        }

        ScanExplanation absent = await file.ScanBuilder().Where(Equal(Absent)).ExplainAsync();
        Assert.Equal(1, Assert.Single(absent.Pruning, step => step.Structure == "bloom filter").SegmentsRead);
        Assert.Equal(0, absent.LiveBlocks);
        Assert.True(file.MayMatch(Equal(Absent)));
        Assert.False(await file.MayMatchAsync(Equal(Absent)));
        Assert.True(await file.MayMatchAsync(Equal(K(7))));

        // Three keys far apart: the descent is as wide as the output, never wider.
        VortexExpr three = Expr.In(Expr.Field("k"), [FilterLiteral.From(K(3)), FilterLiteral.From(K(9_000)), FilterLiteral.From(K(30_000))]);
        ScanExplanation wide = await file.ScanBuilder().Where(three).ExplainAsync();
        Assert.InRange(Assert.Single(wide.Pruning, step => step.Structure == "bloom filter").SegmentsRead, 4, 1 + 1 + 3 + 3);
        Assert.Equal(3, wide.LiveBlocks);
        Assert.Equal(3, await CountAsync(file, three, indexes: true));
    }

    [Fact]
    public async Task ANodePastItsCeilingHasNoFilterAndTheProbeGoesThroughIt()
    {
        Decoders.EnsureRegistered();
        int blocks = 4_096;

        // 64 blocks hold about 780 values at 100 ppm: a generation of 128 fits, the 2 048 of a node
        // above it do not, nor the root's 32 768.
        (byte[] bytes, WriteReport report) = await WriteAsync(blocks * Block, Policy(maxBlocks: 64));
        Assert.Equal(blocks / 16, report.Index("k", IndexKinds.BloomSbbf)!.Generations);
        await using VortexFile file = await OpenAsync(bytes);
        IndexRun run = Assert.Single(Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries).Runs);
        List<Walked> nodes = await WalkAsync(file, run);
        Assert.All(nodes, n => Assert.Equal(n.Node.Level == 1, n.Node.FilterBlocks > 0));

        // The root, the level-2 nodes, all sixteen groups of generations, one generation's leaves.
        ScanExplanation plan = await file.ScanBuilder().Where(Equal(K(12_345))).ExplainAsync();
        Assert.Equal(1 + 1 + 16 + 1, Assert.Single(plan.Pruning, step => step.Structure == "bloom filter").SegmentsRead);
        Assert.Equal(1, plan.LiveBlocks);
        Assert.Equal(1, await CountAsync(file, Equal(K(12_345)), indexes: true));

        // With one resolution nothing above the leaves has a filter.
        (byte[] leavesOnly, WriteReport one) = await WriteAsync(64 * Block, Policy(resolutions: 1));
        Assert.Equal(0, one.Index("k", IndexKinds.BloomSbbf)!.Generations);
        await using VortexFile flat = await OpenAsync(leavesOnly);
        Assert.Equal(1, await CountAsync(flat, Equal(K(100)), indexes: true));
        Assert.True(await flat.MayMatchAsync(Equal(Absent)));
    }

    [Fact]
    public async Task AutoGivesUpAColumnWhoseFirstGenerationPassesANode()
    {
        // Under Auto, a column whose first generation passes a node's capacity is given up. Wide
        // unique strings keep a block filter under Auto's share, and fourteen blocks of them hold
        // more values than a node of 4 096 blocks holds at 1 %.
        Decoders.EnsureRegistered();
        DType schema = Types.Struct(["s"], [Types.Utf8(Nullability.NonNullable)], Nullability.NonNullable);
        const int rowsPerBlock = 8_192;
        const int blocks = 14;
        foreach (bool auto in (bool[])[true, false])
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = rowsPerBlock,
                DataBlockTargetBytes = null,
                WritePolicy = auto ? WritePolicy.Auto : WritePolicy.None.For("s", IndexSpec.Bloom()),
                IndexBudgetPerMille = 1_000_000,
            };
            WriteReport report;
            await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(Stream.Null), schema, options))
            {
                for (int block = 0; block < blocks; block++)
                {
                    CanonicalArena arena = new CanonicalArena();
                    int column = WideStrings(arena, schema.GetField(0), block * rowsPerBlock, rowsPerBlock);
                    using RecordBatch batch = new RecordBatch(
                        arena, arena.AddStruct(schema, rowsPerBlock, Validity.NonNullable, [column]), block * rowsPerBlock);
                    await writer.WriteAsync(batch);
                    arena.Reset();
                }

                report = await writer.CompleteAsync();
            }

            IndexWriteReport bloom = Assert.IsType<IndexWriteReport>(report.Index("s", IndexKinds.BloomSbbf));
            if (auto)
            {
                Assert.Equal(IndexOutcome.Abandoned, bloom.Outcome);
                Assert.Contains("first generation", bloom.Reason, StringComparison.Ordinal);
                Assert.Contains("a sorted-runs index serves such a column", bloom.Reason, StringComparison.Ordinal);
            }
            else
            {
                // Asked for by name, the leaves stay; the generation, one node for all fourteen
                // blocks, has no filter.
                Assert.True(bloom.Outcome == IndexOutcome.Built, bloom.Reason);
                Assert.Equal((1, 0), (bloom.Runs, bloom.Generations));
            }
        }
    }

    [Fact]
    public async Task AnAppendCutsTheOldTreeAndProbesBoth()
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-bloomtree-{Guid.NewGuid():N}.vortex");
        try
        {
            // A short last block every time -- 5, 7, 1, 3 rows -- which the next append writes again,
            // and which the tree before it must not answer for.
            int rows = (300 * Block) + 5;
            (byte[] first, _) = await WriteAsync(rows, Policy());
            await System.IO.File.WriteAllBytesAsync(path, first);
            for (int append = 0; append < 3; append++)
            {
                int added = (70 * Block) + 2;
                await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, Options(Policy())))
                {
                    await FeedAsync(writer, rows, rows + added);
                    await writer.CompleteAsync();
                }

                int boundary = rows / Block;
                rows += added;
                await using VortexFile file = await VortexFile.OpenAsync(path);
                IndexEntry entry = Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries);
                Assert.Equal(append + 2, entry.Runs.Count);
                IndexRun cut = entry.Runs[^2];
                Assert.Equal((ulong)boundary, cut.EndBlock);
                Assert.True((await WalkAsync(file, cut))[0].Node.Leaves > cut.BlockCount, "the old tree was not cut");
                Assert.Equal((ulong)boundary, entry.Runs[^1].FirstBlock);
                Assert.Equal((ulong)((rows + Block - 1) / Block), entry.Runs[^1].EndBlock);

                // Keys before the cut, in the block written again, and after it.
                foreach (int row in (int[])[0, (boundary * Block) - 1, boundary * Block, (boundary * Block) + 4, rows - 1])
                {
                    Assert.Equal(1, await CountAsync(file, Equal(K(row)), indexes: true));
                    ScanExplanation plan = await file.ScanBuilder().Where(Equal(K(row))).ExplainAsync();
                    Assert.Equal(1, plan.LiveBlocks);
                }

                Assert.Equal(0, (await file.ScanBuilder().Where(Equal(Absent)).ExplainAsync()).LiveBlocks);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATornRegionClaimsNothingBeneathIt()
    {
        // Two hundred blocks: a root of level 2, whose children are the one group of generations.
        Decoders.EnsureRegistered();
        int blocks = 200;
        (byte[] original, _) = await WriteAsync(blocks * Block, Policy());
        List<(IndexSegment Region, int Level)> regions;
        await using (VortexFile file = await OpenAsync(original))
        {
            regions = await RegionsAsync(file);
        }

        // The group of level-1 nodes under the root: every generation below it goes unread.
        (IndexSegment generations, _) = regions.First(r => r.Level == 1);
        byte[] torn = (byte[])original.Clone();
        torn[(int)generations.Offset + ((int)generations.Length / 3)] ^= 0x40;
        await using VortexFile opened = await OpenAsync(torn);
        ScanExplanation plan = await opened.ScanBuilder().Where(Equal(K(1_000))).ExplainAsync();
        Assert.Equal(0, Assert.Single(plan.Pruning, step => step.Structure == "bloom filter").BlocksPruned);
        Assert.Equal(1, await CountAsync(opened, Equal(K(1_000)), indexes: true));

        // A value absent from the file is still refused by the root, which is intact.
        Assert.Equal(0, (await opened.ScanBuilder().Where(Equal(Absent)).ExplainAsync()).LiveBlocks);
    }

    [Fact]
    public async Task MutatedRegionsNeverLoseARow()
    {
        Decoders.EnsureRegistered();
        int blocks = 300;
        (byte[] original, _) = await WriteAsync(blocks * Block, Policy());
        List<(IndexSegment Region, int Level)> regions;
        await using (VortexFile file = await OpenAsync(original))
        {
            regions = await RegionsAsync(file);
        }

        Assert.True(regions.Count > 20, $"only {regions.Count} regions");
        Random random = new Random(20260917);
        int pruned = 0;
        for (int i = 0; i < 80; i++)
        {
            byte[] bytes = (byte[])original.Clone();
            (IndexSegment region, _) = regions[random.Next(regions.Count)];
            for (int f = random.Next(1, 4); f > 0; f--)
            {
                bytes[(int)region.Offset + random.Next((int)region.Length)] ^= (byte)random.Next(1, 256);
            }

            await using VortexFile file = await OpenAsync(bytes);
            int row = random.Next(blocks * Block);
            Assert.Equal(1, await CountAsync(file, Equal(K(row)), indexes: true));
            pruned += (await file.ScanBuilder().Where(Equal(K(row))).ExplainAsync()).LiveBlocks < blocks ? 1 : 0;
        }

        // Most mutations leave the probe's path alone, and those still prune.
        Assert.True(pruned > 0);
    }

    [Fact]
    public async Task TheDirectoryDoesNotGrowWithTheBlocks()
    {
        Decoders.EnsureRegistered();
        int[] sizes = new int[3];
        int at = 0;
        foreach (int blocks in (int[])[20, 300, 4_000])
        {
            (byte[] bytes, _) = await WriteAsync(blocks * Block, Policy());
            await using VortexFile file = await OpenAsync(bytes);
            sizes[at++] = (await file.ReadIndexDirectoryAsync())!.ToBytes().Length;
        }

        // A varint or two of the root's size and the run's blocks, nothing else.
        Assert.True(sizes.Max() - sizes.Min() <= 8, string.Join(", ", sizes));
    }

    // ------------------------------------------------------------------------------ the walk

    /// <summary>A node read from a file, where its blocks start, and a level-1 node's leaves.</summary>
    private sealed record Walked(BloomNode Node, long FirstBlock, uint[][] Leaves);

    /// <summary>Every node of a run's tree, root first, and every level-1 node's leaves.</summary>
    private static async Task<List<Walked>> WalkAsync(VortexFile file, IndexRun run)
    {
        uint[] rootWords = await WordsAsync(file, run.Payload[0], BloomTreeRun.RootWords(run.OptionBytes));
        Assert.True(BloomNode.TryRead(rootWords, 0, rootWords.Length, (int)((rootWords[0] >> 8) & 0xFF), rootWords[1], out BloomNode? root));
        List<Walked> nodes = [];
        await AddAsync(root!, (long)run.FirstBlock);
        return nodes;

        async Task AddAsync(BloomNode node, long first)
        {
            uint[] children = node.Children is { } region
                ? await WordsAsync(file, region, (int)node.ChildWords.ToArray().Sum(w => (long)w))
                : [];
            uint[][] leaves = new uint[node.Level == 1 ? node.ChildCount : 0][];
            nodes.Add(new Walked(node, first, leaves));
            int at = 0;
            for (int c = 0; c < node.ChildCount; c++)
            {
                int length = node.ChildWords.IsEmpty ? 0 : (int)node.ChildWords[c];
                if (node.Level == 1)
                {
                    leaves[c] = children.AsSpan(at, length).ToArray();
                }
                else
                {
                    Assert.True(BloomNode.TryRead(children, at, length, node.Level - 1, BloomNode.ChildLeaves(node.Level, node.Leaves, c), out BloomNode? child));
                    await AddAsync(child!, first + (c * BloomNode.ChildSpan(node.Level)));
                }

                at += length;
            }
        }
    }

    /// <summary>Every region under a column's roots, with the level of the nodes it holds (0: leaves).</summary>
    private static async Task<List<(IndexSegment Region, int Level)>> RegionsAsync(VortexFile file)
    {
        List<(IndexSegment, int)> regions = [];
        foreach (IndexRun run in Assert.Single((await file.ReadIndexDirectoryAsync())!.Entries).Runs)
        {
            foreach (Walked walked in await WalkAsync(file, run))
            {
                if (walked.Node.Children is { } region)
                {
                    regions.Add((region, walked.Node.Level - 1));
                }
            }
        }

        return regions;
    }

    private static async Task<uint[]> WordsAsync(VortexFile file, IndexSegment region, int words)
    {
        using SegmentRequestSet requests = new SegmentRequestSet(1);
        int slot = requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
        // The file's own runs: origin 0.
        await file.IndexSourceOf(0).ReadManyAsync(requests, CancellationToken.None);
        Assert.True(region.Holds(requests.GetBuffer(slot).Span));
        using ScanContext context = file.CreateIndexContext(0);
        context.Decode.LoadBlob(requests.GetBuffer(slot));
        ArrayNode root = context.Nodes.Root;
        int node = context.Decode.DecodeRoot(in root, Types.Primitive(PType.U32, Nullability.NonNullable), words);
        return context.Canonical.GetNode(node).Values.Cast<uint>()[..words].ToArray();
    }

    private static bool Holds(ReadOnlySpan<uint> filter, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return filter.IsEmpty || SplitBlockBloom.Contains(filter, SplitBlockBloom.Hash(bytes, BloomHash.XxHash3));
    }

    // ------------------------------------------------------------------------------ the file

    private static VortexExpr Equal(long value) => Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(value)));

    private static async Task<long> CountAsync(VortexFile file, VortexExpr filter, bool indexes)
    {
        long count = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).WithIndexes(indexes).WithPruning(indexes).ExecuteAsync())
        {
            count += batch.RowCount;
        }

        return count;
    }

    private static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);

    private static VortexWriteOptions Options(IndexSpec policy) => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = null,
        WritePolicy = WritePolicy.None.For("k", policy),
        IndexBudgetPerMille = 1_000_000,
    };

    private static async Task<(byte[] Bytes, WriteReport Report)> WriteAsync(int rows, IndexSpec policy)
    {
        using MemoryStream stream = new MemoryStream();
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, Options(policy)))
        {
            await FeedAsync(writer, 0, rows);
            report = await writer.CompleteAsync();
        }

        return (stream.ToArray(), report);
    }

    /// <summary>Rows <c>[start, end)</c>, in batches of a thousand blocks.</summary>
    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        for (int from = start; from < end; from += 1_000 * Block)
        {
            int count = Math.Min(1_000 * Block, end - from);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < count; i++)
                {
                    values[i] = K(from + i);
                }

                int column = arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
                using RecordBatch batch = new RecordBatch(
                    arena, arena.AddStruct(Schema, count, Validity.NonNullable, [column]), from);
                await writer.WriteAsync(batch);
            }
            finally
            {
                arena.Reset();
            }
        }
    }

    /// <summary>
    /// 200-byte unique strings, out of line, in no order -- a sorted column is given up for that --
    /// and of letters no compressor can shorten much, so that a block filter stays under Auto's share.
    /// </summary>
    private static int WideStrings(CanonicalArena arena, DType dtype, int start, int count)
    {
        const int width = 200;
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        VortexBuffer data = arena.Allocate(count * width, 1, out Span<byte> heap);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> view);
        view.Clear();
        for (int i = 0; i < count; i++)
        {
            Span<byte> value = heap.Slice(i * width, width);
            ulong noise = (ulong)K(start + i) * 0x9E37_79B9_7F4A_7C15UL;
            for (int b = 12; b < width; b++)
            {
                noise ^= noise << 13;
                noise ^= noise >> 7;
                noise ^= noise << 17;
                value[b] = (byte)letters[(int)(noise & 63)];
            }

            Encoding.ASCII.GetBytes(K(start + i).ToString("D12", CultureInfo.InvariantCulture), value);
            BinaryPrimitives.WriteInt32LittleEndian(view[(i * 16)..], width);
            value[..4].CopyTo(view[((i * 16) + 4)..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[((i * 16) + 8)..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[((i * 16) + 12)..], i * width);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
    }
}
