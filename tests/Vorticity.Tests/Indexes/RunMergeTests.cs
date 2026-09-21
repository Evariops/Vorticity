// One run per entry, whatever the number of chunks a file is written in.
//
// WHAT IS HELD: a column written in hundreds of chunks carries one sorted run and one postings run,
// not one per chunk; a lookup on a key uncorrelated with row order reads one segment of it; the last
// chunk keeps a run of its own when the rows are not a whole number of blocks; the merge over more
// runs than one pass takes, over a scratch that has moved to disk, and with rows written at 64 bits
// answers what the oracle answers; and the scratch's move to disk changes no byte of the file.
using System;
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
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class RunMergeTests
{
    private const int Block = 256;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["k", "s"],
        [Types.Primitive(PType.I64, Nullability.NonNullable), Types.Utf8(Nullability.NonNullable)],
        Nullability.NonNullable);

    private static long K(int row) => (row * 7919L) % 100_003;

    private static string S(int row) => "s" + ((row * 31) % 509).ToString(CultureInfo.InvariantCulture);

    private static WritePolicy Policy => WritePolicy.None
        .For("k", IndexPolicy.SortedRuns.WithSegmentEntries(1_000))
        .For("s", IndexPolicy.Postings.WithSegmentEntries(64));

    [Fact]
    public async Task HundredsOfChunksWriteOneRunPerEntryAndALookupReadsOneSegment()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 300;
        (byte[] bytes, WriteReport report) = await WriteAsync(rows);
        Assert.Equal(300, report.ChunkRows.Count);
        Assert.Equal(1, report.Index("k", IndexKinds.SortedRuns)!.Runs);
        Assert.Equal(1, report.Index("s", IndexKinds.PostingsBlocks)!.Runs);

        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
        foreach (IndexEntry entry in directory.Entries)
        {
            IndexRun run = Assert.Single(entry.Runs);
            Assert.Equal(0UL, run.FirstBlock);
            Assert.Equal(300U, run.BlockCount);
        }

        await AssertWalkAsync(file, rows);
        await AssertDistinctAsync(file, rows);

        // A key that occurs once: the pruner reads one fence page -- the run has 77 segments, past
        // the 64 kept inline -- then the one segment whose fences hold it, its keys and
        // its rows, whatever the number of chunks.
        long probe = K(12_345);
        VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(probe)));
        ScanPlan plan = await file.Scan().Where(equal).ExplainAsync();
        PruningStep locating = Assert.Single(plan.Pruning, step => step.Structure == "locating index");
        Assert.Equal(3, locating.SegmentsRead);
        Assert.Equal(await OracleCountAsync(file, equal), await file.Scan().Where(equal).CountAsync());

        VortexExpr text = Expr.Eq(Expr.Field("s"), Expr.Literal(FilterLiteral.From(S(4_242))));
        Assert.Equal(await OracleCountAsync(file, text), await CountRowsAsync(file, text, indexes: true));
    }

    [Fact]
    public async Task TheLastChunkKeepsItsOwnRunWhenTheRowsAreNotWholeBlocks()
    {
        Decoders.EnsureRegistered();
        int rows = (Block * 10) + 37;
        (byte[] bytes, WriteReport report) = await WriteAsync(rows);
        Assert.Equal(2, report.Index("k", IndexKinds.SortedRuns)!.Runs);

        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
        foreach (IndexEntry entry in directory.Entries)
        {
            Assert.Equal(2, entry.Runs.Count);
            Assert.Equal((0UL, 10U), (entry.Runs[0].FirstBlock, entry.Runs[0].BlockCount));
            Assert.Equal((10UL, 1U), (entry.Runs[1].FirstBlock, entry.Runs[1].BlockCount));
        }

        Assert.Equal((ulong)(Block * 10), directory.Entries.Single(e => e.Kind == IndexKinds.SortedRuns).Runs[0].EntryCount);
        await AssertWalkAsync(file, rows);
        await AssertDistinctAsync(file, rows);
    }

    [Fact]
    public async Task MoreRunsThanOnePassTakesMergeInPassesOverAScratchOnDisk()
    {
        Decoders.EnsureRegistered();
        int chunks = (RunMerger.MaxFanIn * 3) + 5;
        int rows = Block * chunks;
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Guid identity = Guid.NewGuid();
            (byte[] spilled, WriteReport report) = await WriteAsync(
                rows, identity, scratchMemoryBytes: 0, scratchDirectory: directory);
            Assert.Equal(chunks, report.ChunkRows.Count);

            // The scratch file is gone with the writer.
            Assert.Empty(Directory.GetFiles(directory));

            // And the move to disk changed no byte.
            (byte[] inMemory, _) = await WriteAsync(rows, identity);
            Assert.Equal(inMemory, spilled);

            await using VortexFile file = await OpenAsync(spilled);
            await AssertWalkAsync(file, rows);
            await AssertDistinctAsync(file, rows);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RowsSpanningMoreThan32BitsAreWrittenAndReadAt64Bits()
    {
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        (byte[] bytes, _) = await WriteAsync(rows, wideRowsAbove: Block * 3);

        await using VortexFile file = await OpenAsync(bytes);
        IndexEntry entry = (await file.ReadIndexDirectoryAsync())!.Entries.Single(e => e.Kind == IndexKinds.SortedRuns);
        IndexRun run = Assert.Single(entry.Runs);
        Assert.True(KeyRunOptions.WideRows(run, 1));

        await AssertWalkAsync(file, rows);
        VortexExpr range = Expr.And(
            Expr.Ge(Expr.Field("k"), Expr.Literal(FilterLiteral.From(20_000L))),
            Expr.Lt(Expr.Field("k"), Expr.Literal(FilterLiteral.From(20_400L))));
        Assert.Equal(await OracleCountAsync(file, range), await file.Scan().Where(range).CountAsync());
        VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(K(7_777))));
        Assert.Equal(await OracleCountAsync(file, equal), await CountRowsAsync(file, equal, indexes: true));
    }

    [Fact]
    public async Task AnEntryKeepsAtMostKRunsAcrossManyAppends()
    {
        // An append that would pass K runs reads the old tail back and merges it into its
        // own run. Every piece ends inside a block, so every version has a last chunk of its own.
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-kruns-{Guid.NewGuid():N}.vortex");
        try
        {
            int rows = 1_000;
            (byte[] first, _) = await WriteAsync(rows);
            await System.IO.File.WriteAllBytesAsync(path, first);
            int merges = 0;
            for (int append = 0; append < 12; append++)
            {
                int added = 700 + (append * 37);
                long before = new FileInfo(path).Length;

                // A chunk per block, so that each append writes a merged run of its own.
                VortexWriteOptions options = new VortexWriteOptions
                {
                    RowBlockSize = Block,
                    DataBlockTargetBytes = null,
                    Indexes = Policy,
                    IndexBudgetPerMille = 1_000_000,
                };
                await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options))
                {
                    await FeedAsync(writer, rows, rows + added);
                    await writer.CompleteAsync();
                }

                rows += added;
                await using VortexFile file = await VortexFile.OpenAsync(path);
                IndexDirectory directory = (await file.ReadIndexDirectoryAsync())!;
                Assert.Equal(2, directory.Entries.Count);
                int blocks = (rows + Block - 1) / Block;
                foreach (IndexEntry entry in directory.Entries)
                {
                    Assert.InRange(entry.Runs.Count, 1, KeyIndexBuilder.MaxRuns);
                    ulong next = 0;
                    foreach (IndexRun run in entry.Runs)
                    {
                        Assert.Equal(next, run.FirstBlock);
                        next = run.EndBlock;
                    }

                    Assert.Equal((ulong)blocks, next);
                    merges += entry.Runs.Count == 2 && append >= 2 ? 1 : 0;
                }

                // A merge reads index bytes only: the file grows by what the append wrote and the
                // runs it rewrote, and every answer holds.
                Assert.True(new FileInfo(path).Length > before);
                await AssertWalkAsync(file, rows);
                await AssertDistinctAsync(file, rows);
            }

            Assert.True(merges > 0, "no append merged its entry's runs");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void TheScratchMovesToDiskPastItsBudgetAndReadsBack()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            long[] values = [.. Enumerable.Range(0, 1_000).Select(i => (long)i * 3)];
            byte[] tail = Encoding.UTF8.GetBytes("the last bytes");
            using (RunScratch scratch = new RunScratch(memoryBudget: 5_000, directory))
            {
                long first = scratch.Append<long>(values.AsSpan(0, 500));
                Assert.False(scratch.OnDisk);
                long second = scratch.Append<long>(values.AsSpan(500));
                Assert.True(scratch.OnDisk);
                long third = scratch.Append(tail);
                Assert.Single(Directory.GetFiles(directory));

                long[] read = new long[1_000];
                scratch.Read<long>(first, read.AsSpan(0, 500));
                scratch.Read<long>(second, read.AsSpan(500));
                Assert.Equal(values, read);
                byte[] bytes = new byte[tail.Length];
                scratch.Read(third, bytes);
                Assert.Equal(tail, bytes);
                Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Read(third, new byte[tail.Length + 1]));
            }

            Assert.Empty(Directory.GetFiles(directory));

            // Across page boundaries, in memory.
            using RunScratch pages = new RunScratch(memoryBudget: long.MaxValue, directory);
            byte[] big = new byte[RunScratch.PageBytes + 1_000];
            new Random(7).NextBytes(big);
            pages.Append(big.AsSpan(0, 700));
            long at = pages.Append(big.AsSpan(700));
            byte[] back = new byte[big.Length - 700];
            pages.Read(at, back);
            Assert.Equal(big.AsSpan(700).ToArray(), back);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------ oracles

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

        // Backward, and a seek into the middle.
        long middle = expected[rows / 2].Key;
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(middle), SeekOp.AtOrAfter));
        Assert.Equal(expected[expected.FindIndex(e => e.Key >= middle)].Row, cursor.Row);
        Assert.True(await cursor.SeekLastAsync());
        Assert.Equal(expected[^1].Row, cursor.Row);
        Assert.Equal(rows, cursor.EntryCount);
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

    private static Task<long> OracleCountAsync(VortexFile file, VortexExpr filter) =>
        CountRowsAsync(file, filter, indexes: false);

    private static async Task<long> CountRowsAsync(VortexFile file, VortexExpr filter, bool indexes)
    {
        long count = 0;
        await foreach (RecordBatch batch in file.Scan().Where(filter).WithIndexes(indexes).WithPruning(indexes).ExecuteAsync())
        {
            count += batch.RowCount;
        }

        return count;
    }

    // ------------------------------------------------------------------------------ the file

    private static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);

    private static async Task<(byte[] Bytes, WriteReport Report)> WriteAsync(
        int rows, Guid? identity = null, long? scratchMemoryBytes = null, string? scratchDirectory = null,
        long? wideRowsAbove = null)
    {
        using MemoryStream stream = new MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            Indexes = Policy,
            IndexBudgetPerMille = 1_000_000,
            Identity = identity,
            ScratchDirectory = scratchDirectory,
            ScratchMemoryBytes = scratchMemoryBytes ?? IndexWriter.DefaultScratchMemoryBytes,
            WideRowsAbove = wideRowsAbove ?? uint.MaxValue,
        };
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, options))
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
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(Schema.GetField(1), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
