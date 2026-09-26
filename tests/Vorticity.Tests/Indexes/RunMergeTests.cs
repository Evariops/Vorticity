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
using System.Threading;
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
        .For("k", IndexSpec.SortedRuns.WithSegmentEntries(1_000))
        .For("s", IndexSpec.Postings.WithSegmentEntries(64));

    [Fact]
    public async Task HundredsOfChunksWriteOneRunPerEntryAndALookupReadsOneSegment()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        int rows = Block * 300;
        (byte[] bytes, WriteReport report) = await WriteAsync(rows);
        Assert.Equal(300, report.ChunkRows.Length);
        Assert.Equal(1, report.Index("k", IndexKinds.SortedRuns)!.Runs);
        Assert.Equal(1, report.Index("s", IndexKinds.PostingsBlocks)!.Runs);

        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(ct))!;
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
        ScanExplanation plan = await file.ScanBuilder().Where(equal).ExplainAsync(ct);
        PruningStep locating = Assert.Single(plan.Pruning, step => step.Structure == "locating index");
        Assert.Equal(3, locating.SegmentsRead);
        Assert.Equal(await OracleCountAsync(file, equal), await file.ScanBuilder().Where(equal).CountAsync(ct));

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
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken))!;
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
            Assert.Equal(chunks, report.ChunkRows.Length);

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
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        int rows = Block * 40;
        (byte[] bytes, _) = await WriteAsync(rows, wideRowsAbove: Block * 3);

        await using VortexFile file = await OpenAsync(bytes);
        IndexEntry entry = (await file.ReadIndexDirectoryAsync(ct))!.Entries.Single(e => e.Kind == IndexKinds.SortedRuns);
        IndexRun run = Assert.Single(entry.Runs);
        Assert.True(KeyRunOptions.WideRows(run, 1));

        await AssertWalkAsync(file, rows);
        VortexExpr range = Expr.And(
            Expr.Ge(Expr.Field("k"), Expr.Literal(FilterLiteral.From(20_000L))),
            Expr.Lt(Expr.Field("k"), Expr.Literal(FilterLiteral.From(20_400L))));
        Assert.Equal(await OracleCountAsync(file, range), await file.ScanBuilder().Where(range).CountAsync(ct));
        VortexExpr equal = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(K(7_777))));
        Assert.Equal(await OracleCountAsync(file, equal), await CountRowsAsync(file, equal, indexes: true));
    }

    [Fact]
    public async Task AnEntryKeepsAtMostKRunsAcrossManyAppends()
    {
        // An append that would pass K runs reads the old tail back and merges it into its
        // own run. Every piece ends inside a block, so every version has a last chunk of its own.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-kruns-{Guid.NewGuid():N}.vortex");
        try
        {
            int rows = 1_000;
            (byte[] first, _) = await WriteAsync(rows);
            await System.IO.File.WriteAllBytesAsync(path, first, ct);
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
                    WritePolicy = Policy,
                    IndexBudgetPerMille = 1_000_000,
                };
                await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options, ct))
                {
                    await FeedAsync(writer, rows, rows + added);
                    await writer.CompleteAsync(ct);
                }

                rows += added;
                await using VortexFile file = await VortexFile.OpenAsync(path, ct);
                IndexDirectory directory = (await file.ReadIndexDirectoryAsync(ct))!;
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
    public async Task AScratchOnDiskIsNoOneElsesToReadAndLeavesNoFileBehind()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows deletes a delete-on-close file itself, however the process ends");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using RunScratch scratch = new RunScratch(memoryBudget: 5_000, directory);
            await scratch.AppendAsync(new byte[8_000], ct);
            Assert.True(scratch.OnDisk);

            // The keys a spill holds are column values: the file is its owner's alone, and has no
            // name to be opened by, or to be left behind by a process killed before it closes.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, global::System.IO.File.GetUnixFileMode(FileOf(scratch)!));
            }

            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_file")]
    private static extern ref Microsoft.Win32.SafeHandles.SafeFileHandle? FileOf(RunScratch scratch);

    [Fact]
    public async Task TheScratchMovesToDiskPastItsBudgetAndReadsBack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            long[] values = [.. Enumerable.Range(0, 1_000).Select(i => (long)i * 3)];
            byte[] bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            byte[] tail = Encoding.UTF8.GetBytes("the last bytes");
            using (RunScratch scratch = new RunScratch(memoryBudget: 5_000, directory))
            {
                ValueTask<long> held = scratch.AppendAsync(bytes.AsMemory(0, 4_000), ct);
                Assert.True(held.IsCompletedSuccessfully);
                long first = await held;
                Assert.False(scratch.OnDisk);
                long second = await scratch.AppendAsync(bytes.AsMemory(4_000), ct);
                Assert.True(scratch.OnDisk);
                long third = await scratch.AppendAsync(tail, ct);

                // Named in the directory on Windows, which deletes it on close; nameless elsewhere.
                Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, Directory.GetFiles(directory).Length);

                // On disk, the synchronous members refuse rather than block on the file.
                Assert.Throws<InvalidOperationException>(() => scratch.Append(tail));
                Assert.Throws<InvalidOperationException>(() => scratch.Read(third, new byte[tail.Length]));

                byte[] read = new byte[bytes.Length];
                await scratch.ReadAsync(first, read.AsMemory(0, 4_000), ct);
                await scratch.ReadAsync(second, read.AsMemory(4_000), ct);
                Assert.Equal(bytes, read);
                byte[] back = new byte[tail.Length];
                await scratch.ReadAsync(third, back, ct);
                Assert.Equal(tail, back);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                    async () => await scratch.ReadAsync(third, new byte[tail.Length + 1], ct));
            }

            Assert.Empty(Directory.GetFiles(directory));

            // Across page boundaries, in memory, where the asynchronous read completes at once.
            using RunScratch pages = new RunScratch(memoryBudget: long.MaxValue, directory);
            byte[] big = new byte[RunScratch.PageBytes + 1_000];
            new Random(7).NextBytes(big);
            pages.Append(big.AsSpan(0, 700));
            long at = pages.Append(big.AsSpan(700));
            byte[] again = new byte[big.Length - 700];
            pages.Read(at, again);
            Assert.Equal(big.AsSpan(700).ToArray(), again);
            Array.Clear(again);
            ValueTask copied = pages.ReadAsync(at, again, ct);
            Assert.True(copied.IsCompletedSuccessfully);
            await copied;
            Assert.Equal(big.AsSpan(700).ToArray(), again);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 8)]
    [InlineData(false, 0)]
    [InlineData(false, 8)]
    public async Task AMergeOfWindowsReadBackFromDiskGivesWhatTheSameMergeGivesInMemory(bool hasRows, int keyWidth)
    {
        // Five runs sharing most of their keys, a few windows each, merged into a laid run that is
        // merged again: windows fill and are flushed, and are spent and refilled with a key open.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            List<string> expected = ExpectedMerge(hasRows);
            Assert.Equal(expected, await MergeTwiceAsync(hasRows, keyWidth, long.MaxValue, directory, ct));
            Assert.Equal(expected, await MergeTwiceAsync(hasRows, keyWidth, 0, directory, ct));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnAppendThatMergesOverAScratchOnDiskWritesTheBytesOfOneInMemory()
    {
        // The old runs an append reads back are laid in its scratch and merged from there: on disk,
        // every append writes the bytes the same append writes in memory.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-absorb-{Guid.NewGuid():N}");
        string scratch = Path.Combine(directory, "scratch");
        Directory.CreateDirectory(scratch);
        try
        {
            Guid identity = Guid.NewGuid();
            string inMemory = Path.Combine(directory, "memory.vortex");
            string onDisk = Path.Combine(directory, "disk.vortex");
            int rows = 1_000;
            (byte[] first, _) = await WriteAsync(rows, identity);
            await System.IO.File.WriteAllBytesAsync(inMemory, first, ct);
            await System.IO.File.WriteAllBytesAsync(onDisk, first, ct);
            bool merged = false;
            for (int append = 0; append < 6; append++)
            {
                int added = 700 + (append * 37);
                await AppendAsync(inMemory, rows, added, identity, IndexWriter.DefaultScratchMemoryBytes, scratch);
                await AppendAsync(onDisk, rows, added, identity, 0, scratch);
                rows += added;
                Assert.Equal(await System.IO.File.ReadAllBytesAsync(inMemory, ct), await System.IO.File.ReadAllBytesAsync(onDisk, ct));
                Assert.Empty(Directory.GetFiles(scratch));

                await using VortexFile file = await VortexFile.OpenAsync(onDisk, ct);
                IndexDirectory entries = (await file.ReadIndexDirectoryAsync(ct))!;
                merged |= append >= 2 && entries.Entries.Any(e => e.Runs.Count == 2);
                await AssertWalkAsync(file, rows);
                await AssertDistinctAsync(file, rows);
            }

            Assert.True(merged, "no append merged its entry's runs");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------ raw runs

    private const int MergedRuns = 5;

    private const int RunKeys = 9_000;

    private static bool Holds(int run, int key) => key >= run * 1_000 && key < (run * 1_000) + RunKeys;

    /// <summary>A sorted run's rows for a key: two for every third key, so that ties cross windows too.</summary>
    private static long[] RowsOf(int run, int key)
    {
        long row = (run * 1_000_000L) + (2L * key);
        return key % 3 == 0 ? [row, row + 1] : [row];
    }

    /// <summary>A postings run's list for a key, inside the run's eight blocks.</summary>
    private static uint[] BlocksOf(int run, int key)
    {
        uint first = (uint)(run * 8);
        uint block = first + (uint)(key % 8);
        return key % 5 == 0 && key % 8 != 7 ? [block, first + 7] : [block];
    }

    /// <summary>Byte keys share their first eight bytes over long stretches, so a full comparison settles their order.</summary>
    private static byte[] KeyOf(int key, int keyWidth)
    {
        if (keyWidth == 0)
        {
            return Encoding.ASCII.GetBytes("key-" + key.ToString("D6", CultureInfo.InvariantCulture));
        }

        byte[] bytes = new byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, key);
        return bytes;
    }

    private static long KeyOf(ReadOnlySpan<byte> key, int keyWidth) =>
        keyWidth == 0
            ? long.Parse(Encoding.ASCII.GetString(key[4..]), CultureInfo.InvariantCulture)
            : System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(key);

    private static List<string> ExpectedMerge(bool hasRows)
    {
        List<string> lines = [];
        for (int key = 0; key < ((MergedRuns - 1) * 1_000) + RunKeys; key++)
        {
            int k = key;
            IEnumerable<int> runs = Enumerable.Range(0, MergedRuns).Where(r => Holds(r, k));
            if (hasRows)
            {
                lines.AddRange(runs.SelectMany(r => RowsOf(r, k)).Order().Select(row => $"{k}:{row}"));
            }
            else
            {
                lines.Add($"{k}:{string.Join(',', runs.SelectMany(r => BlocksOf(r, k)))}");
            }
        }

        return lines;
    }

    /// <summary>Lays the raw runs, merges them into one laid run, and merges that one into a recorder.</summary>
    private static async Task<List<string>> MergeTwiceAsync(
        bool hasRows, int keyWidth, long memoryBudget, string directory, CancellationToken ct)
    {
        KeyLayout layout = keyWidth == 0
            ? new KeyLayout(KeyShape.Bytes, 0, default)
            : new KeyLayout(KeyShape.Signed, sizeof(long), PType.I64);
        using RunScratch scratch = new RunScratch(memoryBudget, directory);
        List<RunCursor> cursors = [];
        for (int run = 0; run < MergedRuns; run++)
        {
            RawRun raw = await LayRunAsync(scratch, hasRows, keyWidth, run, ct);
            Assert.True(raw.Windows.Count > 2);
            cursors.Add(new RunCursor(new RawWindowSource(scratch, raw), layout, hasRows, run));
        }

        Assert.Equal(memoryBudget == 0, scratch.OnDisk);
        using RawRunWriter pass = new RawRunWriter(scratch, hasRows, keyWidth, 0, MergedRuns * 8);
        await RunMerger.MergeAsync(cursors, layout, hasRows, pass, ct);
        RawRun merged = await pass.FinishAsync(ct);
        Assert.True(merged.Windows.Count > 2);

        Recorder recorder = new Recorder(keyWidth);
        await RunMerger.MergeAsync(
            [new RunCursor(new RawWindowSource(scratch, merged), layout, hasRows, 0)], layout, hasRows, recorder, ct);
        return recorder.Lines;
    }

    private static async Task<RawRun> LayRunAsync(RunScratch scratch, bool hasRows, int keyWidth, int run, CancellationToken ct)
    {
        using RawRunWriter writer = new RawRunWriter(scratch, hasRows, keyWidth, run * 8, 8);
        for (int key = run * 1_000; key < (run * 1_000) + RunKeys; key++)
        {
            byte[] bytes = KeyOf(key, keyWidth);
            if (hasRows)
            {
                foreach (long row in RowsOf(run, key))
                {
                    if (writer.Add(bytes, row))
                    {
                        await writer.FlushAsync(ct);
                    }
                }

                continue;
            }

            writer.BeginKey(bytes);
            writer.AddBlocks(BlocksOf(run, key));
            if (writer.EndKey())
            {
                await writer.FlushAsync(ct);
            }
        }

        return await writer.FinishAsync(ct);
    }

    /// <summary>Each entry a merge hands it, as a line; it never asks for a flush.</summary>
    private sealed class Recorder(int keyWidth) : IRunSink
    {
        private readonly List<uint> _blocks = [];
        private long _key;

        internal List<string> Lines { get; } = [];

        public bool Add(ReadOnlySpan<byte> key, long row)
        {
            Lines.Add($"{KeyOf(key, keyWidth)}:{row}");
            return false;
        }

        public void BeginKey(ReadOnlySpan<byte> key)
        {
            _key = KeyOf(key, keyWidth);
            _blocks.Clear();
        }

        public void AddBlocks(ReadOnlySpan<uint> blocks) => _blocks.AddRange(blocks);

        public bool EndKey()
        {
            Lines.Add($"{_key}:{string.Join(',', _blocks)}");
            return false;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
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
        await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).WithIndexes(indexes).WithPruning(indexes).ExecuteAsync())
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
            WritePolicy = Policy,
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

    /// <summary>Appends rows <c>[start, start + added)</c> to the file at <paramref name="path"/>.</summary>
    private static async Task AppendAsync(
        string path, int start, int added, Guid identity, long scratchMemoryBytes, string scratchDirectory)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            WritePolicy = Policy,
            IndexBudgetPerMille = 1_000_000,
            Identity = identity,
            ScratchDirectory = scratchDirectory,
            ScratchMemoryBytes = scratchMemoryBytes,
        };
        await using VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options, ct);
        await FeedAsync(writer, start, start + added);
        await writer.CompleteAsync(ct);
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
