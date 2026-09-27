// The writer side of the indexes: the policy, the directory, the report, and the first
// kind, `vorticity.dict.probe.v1`, which costs nothing because the writer already knows which
// chunks it dictionary-encoded.
//
// THREE PROMISES, EACH A TEST. A file written without asking carries no index directory, and one
// written at `Profile = Fastest` is byte for byte that file whatever the policy says. Every index
// a policy asked for is in the report, built or abandoned with its reason. And the directory the
// writer emits is the one the reader's own checks accept.
using System;
using System.Collections.Generic;
using System.Globalization;
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
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class IndexWriteTests
{
    private const int Rows = 20_000;
    private const int Batch = 5_000;
    private const int Block = 1_024;

    private static readonly string[] Statuses = ["open", "closed", "pending", "void"];

    [Fact]
    public async Task WritePolicyNoneWritesNoDirectory()
    {
        Decoders.EnsureRegistered();
        (byte[] bytes, WriteReport report) = await WriteAsync(Options());

        Assert.Empty(report.Indexes);
        await using VortexFile file = await OpenAsync(bytes);
        Assert.False(file.HasIndexDirectory);
        Assert.Null(await file.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken));
        Assert.Null(file.IndexDirectoryRefusal);
    }

    [Fact]
    public async Task FastestIsByteIdenticalToTheDefaultsWhateverThePolicySays()
    {
        // UNDER ONE IDENTITY: every postscript carries its own, so two writes of the same
        // rows differ in those sixteen bytes and in nothing else.
        Decoders.EnsureRegistered();
        Guid identity = Guid.NewGuid();
        (byte[] plain, _) = await WriteAsync(Options(identity: identity));
        (byte[] fastest, WriteReport report) = await WriteAsync(
            Options(WritePolicy.Auto, WriteProfile.Fastest, identity));

        Assert.Empty(report.Indexes);
        Assert.Equal(plain, fastest);
    }

    [Fact]
    public async Task AutoRecordsTheDictionaryProbeOverExactlyTheDictionaryChunks()
    {
        Decoders.EnsureRegistered();
        (byte[] bytes, WriteReport report) = await WriteAsync(Options(WritePolicy.Auto));

        // The oracle is the report's own ledger: the probe must claim the blocks of every chunk the
        // writer dictionary-encoded, and no other block.
        ColumnWriteReport status = Column(report, "status");
        Assert.Contains(nameof(EncodingHint.Dictionary), status.Encodings);
        List<(ulong First, ulong End)> expected = DictionaryBlocks(report, status.Encodings);

        IndexWriteReport built = Assert.IsType<IndexWriteReport>(report.Index("status", IndexKinds.DictProbe));
        Assert.Equal(IndexOutcome.Built, built.Outcome);
        Assert.Null(built.Reason);
        Assert.Equal(0, built.Bytes);
        Assert.Equal(expected.Count, built.Runs);

        // A progression is never a dictionary, and the report says so rather than staying silent.
        IndexWriteReport id = Assert.IsType<IndexWriteReport>(report.Index("id", IndexKinds.DictProbe));
        Assert.Equal(IndexOutcome.Abandoned, id.Outcome);
        Assert.Contains("dictionary", id.Reason, StringComparison.Ordinal);

        await using VortexFile file = await OpenAsync(bytes);
        Assert.True(file.HasIndexDirectory);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(directory);
        Assert.Null(file.IndexDirectoryRefusal);
        Assert.Equal((ulong)Rows, directory!.RowCount);
        Assert.Equal(0UL, directory.PreviousEof);
        Assert.Equal(IndexSpec.Auto, directory.Policy.Default);

        IndexEntry entry = Assert.Single(directory.Entries);
        Assert.Equal(IndexKinds.DictProbe, entry.Kind);
        Assert.Equal(new uint[] { (uint)file.DType.IndexOfField("status") }, entry.ColumnPath);
        Assert.Equal((ulong)Block, entry.BlockLength);
        List<(ulong First, ulong End)> actual = [];
        foreach (IndexRun run in entry.Runs)
        {
            Assert.Empty(run.Payload);
            actual.Add((run.FirstBlock, run.EndBlock));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AnIndexedFileReadsExactlyAsItsUnindexedTwin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        (byte[] plain, _) = await WriteAsync(Options());
        (byte[] indexed, _) = await WriteAsync(Options(WritePolicy.Auto));
        Assert.NotEqual(plain.Length, indexed.Length);

        await using VortexFile a = await OpenAsync(plain);
        await using VortexFile b = await OpenAsync(indexed);
        Assert.Equal(a.RowCount, b.RowCount);
        Assert.Equal(await Dump(a), await Dump(b));

        VortexExpr closed = Expr.Eq(Expr.Field("status"), Expr.Literal(FilterLiteral.From("closed")));
        Assert.Equal(await a.ScanBuilder().Where(closed).CountAsync(ct), await b.ScanBuilder().Where(closed).CountAsync(ct));
        Assert.Equal(Rows / Statuses.Length, await b.ScanBuilder().Where(closed).CountAsync(ct));
    }

    [Fact]
    public async Task AnIndexAbandonedEverywhereIsReportedWithItsReasonAndKeepsItsPolicy()
    {
        Decoders.EnsureRegistered();
        WritePolicy policy = WritePolicy.None
            .For("label", IndexSpec.Bloom())
            .For("id", IndexSpec.SortedRuns);
        (byte[] bytes, WriteReport report) = await WriteAsync(Options(policy));

        Assert.Equal(2, report.Indexes.Length);
        foreach (IndexWriteReport index in report.Indexes)
        {
            Assert.Equal(IndexOutcome.Abandoned, index.Outcome);
            Assert.False(string.IsNullOrEmpty(index.Reason));
        }

        Assert.Null(report.Index("status", IndexKinds.DictProbe));

        // Asked and abandoned everywhere is not "never asked": the directory carries the policy an
        // append will need, and no entry.
        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(directory);
        Assert.Empty(directory!.Entries);
        Assert.Equal(IndexPolicyKind.SortedRuns, directory.Policy.Of("id").Kind);
        Assert.Equal(IndexPolicyKind.None, directory.Policy.Of("status").Kind);
    }

    [Fact]
    public async Task TheIndexPayloadsOfOneFlushGoOutColumnAfterColumn()
    {
        // Too little data for the budget to judge before the end, so every payload waits for the
        // one flush of the completion, which takes a column's payloads before the next column's.
        Decoders.EnsureRegistered();
        WritePolicy policy = WritePolicy.None
            .For("id", IndexSpec.Bloom())
            .For("label", IndexSpec.Bloom());
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            WritePolicy = policy,
            IndexBudgetPerMille = 1_000_000,
        };
        (byte[] bytes, WriteReport report) = await WriteAsync(options);
        Assert.All(report.Indexes, index => Assert.Equal(IndexOutcome.Built, index.Outcome));

        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken))!;
        long previous = -1;
        foreach (uint field in (uint[])[0, 2])
        {
            IndexEntry entry = Assert.Single(directory.Entries, e => e.ColumnPath.Count == 1 && e.ColumnPath[0] == field);
            long first = long.MaxValue;
            long last = 0;
            foreach (IndexRun run in entry.Runs)
            {
                foreach (IndexSegment region in run.Payload)
                {
                    first = Math.Min(first, (long)region.Offset);
                    last = Math.Max(last, (long)region.Offset);
                }
            }

            Assert.True(first > previous, $"column {field}'s payloads start at {first}, before the previous column's end at {previous}");
            previous = last;
        }
    }

    [Fact]
    public async Task TheReportAccountsForEveryByteOfTheFile()
    {
        Decoders.EnsureRegistered();
        foreach (WritePolicy policy in new[] { WritePolicy.None, WritePolicy.Auto })
        {
            (byte[] bytes, WriteReport report) = await WriteAsync(Options(policy));
            Assert.Equal(bytes.Length, report.Bytes.Total);
            Assert.Equal((long)Rows, report.RowCount);
            Assert.True(report.Bytes.Data > 0 && report.Bytes.ZoneMaps > 0 && report.Bytes.Statistics > 0);
            Assert.Equal(3, report.Columns.Length);

            await using VortexFile file = await OpenAsync(bytes);
            long directory = file.HasIndexDirectory
                && file.TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index)
                ? file.GetMetadataSegment(index).Length
                : 0;
            Assert.Equal(directory, report.Bytes.Indexes);
        }
    }

    [Fact]
    public async Task EveryColumnReportsOneEncodingPerChunk()
    {
        Decoders.EnsureRegistered();
        (_, WriteReport report) = await WriteAsync(Options());

        // Each batch of 5 000 rows emits the whole blocks it completes: 4, 5, 5, 5 blocks, then the
        // short tail at close.
        Assert.Equal(new int[] { 4_096, 5_120, 5_120, 5_120, 544 }, report.ChunkRows);
        int chunks = report.ChunkRows.Length;
        foreach (ColumnWriteReport column in report.Columns)
        {
            Assert.Equal(chunks, column.Encodings.Length);
            Assert.InRange(column.PlansHeld, 0, column.PlansPriced);
            Assert.InRange(column.PlanMemoryHitRate, 0.0, 1.0);
        }

        // A progression is priced once and then held on every chunk after the first.
        ColumnWriteReport id = Column(report, "id");
        Assert.All(id.Encodings, e => Assert.Equal(nameof(ColumnScheme.Sequence), e));
        Assert.Equal(chunks - 1, id.PlansPriced);
        Assert.Equal(1.0, id.PlanMemoryHitRate);
    }

    [Fact]
    public void TheProbeClaimsTheDictionaryChunksAndLeavesTheOthersLive()
    {
        // The branch no written fixture can force: dictionary and other chunks alternating, with a
        // run of two dictionaries to merge and a dictionary last. A block no run covers is live,
        // which is the right answer for a chunk that has no dictionary.
        ColumnWriter column = new ColumnWriter();
        for (int block = 0; block < 10; block++)
        {
            column.CloseBlock();
        }

        ColumnPlan dict = ColumnPlan.Dictionary([0], [0]);
        ColumnPlan none = ColumnPlan.Canonical;
        column.Remember(in dict, 10, firstBlock: 0, blockCount: 2);
        column.Remember(in none, 10, firstBlock: 2, blockCount: 1);
        column.Remember(in dict, 10, firstBlock: 3, blockCount: 1);
        column.Remember(in dict, 10, firstBlock: 4, blockCount: 3);
        column.Remember(in none, 10, firstBlock: 7, blockCount: 2);
        column.Remember(in dict, 10, firstBlock: 9, blockCount: 1);
        Assert.Equal(ColumnScheme.Dict, column.SchemeAt(5));
        Assert.Equal(ColumnScheme.None, column.SchemeAt(8));
        Assert.Null(column.SchemeAt(10));

        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(["c"], [types.Utf8(Nullability.NonNullable)], Nullability.NonNullable);
        IndexWriter writer = new IndexWriter(WritePolicy.Auto, schema, isTabular: true, fieldCount: 1);
        long[] chunkRows = [2L * Block, Block, Block, 3L * Block, 2L * Block, Block - 7];
        writer.Close([column], chunkRows, Block);

        Assert.True(IndexDirectory.TryParse(writer.Directory(10 * Block)!, 10UL * Block, ulong.MaxValue, out IndexDirectory? directory, out string? reason), reason);
        IndexEntry entry = Assert.Single(directory!.Entries, e => e.Kind == IndexKinds.DictProbe);
        List<(ulong, ulong)> runs = [];
        foreach (IndexRun run in entry.Runs)
        {
            runs.Add((run.FirstBlock, run.EndBlock));
        }

        Assert.Equal(new List<(ulong, ulong)> { (0, 2), (3, 7), (9, 10) }, runs);
        Assert.Equal(3, Assert.Single(writer.Reports, r => r.Kind == IndexKinds.DictProbe).Runs);
    }

    /// <summary>
    /// The block ranges of the maximal runs of consecutive dictionary chunks, computed from the
    /// chunk sizes alone: a chunk starting at row <c>r</c> of <c>n</c> rows covers blocks
    /// <c>[r / B, ceil((r + n) / B))</c>.
    /// </summary>
    private static List<(ulong First, ulong End)> DictionaryBlocks(WriteReport report, IReadOnlyList<string> encodings)
    {
        Assert.Equal(report.ChunkRows.Length, encodings.Count);
        Assert.Equal(Block, report.BlockRows);
        List<(ulong, ulong)> runs = [];
        long row = 0;
        long open = -1;
        long end = -1;
        for (int chunk = 0; chunk < encodings.Count; chunk++)
        {
            long first = row / Block;
            row += report.ChunkRows[chunk];
            long last = (row + Block - 1) / Block;
            if (encodings[chunk] != nameof(EncodingHint.Dictionary))
            {
                if (open >= 0)
                {
                    runs.Add(((ulong)open, (ulong)end));
                    open = -1;
                }

                continue;
            }

            open = open < 0 ? first : open;
            end = last;
        }

        if (open >= 0)
        {
            runs.Add(((ulong)open, (ulong)end));
        }

        Assert.Equal((long)Rows, row);
        return runs;
    }

    private static ColumnWriteReport Column(WriteReport report, string path)
    {
        foreach (ColumnWriteReport column in report.Columns)
        {
            if (column.Path == path)
            {
                return column;
            }
        }

        throw new InvalidOperationException("no column " + path);
    }

    /// <summary>One chunk per block: the byte target off, so the chunking is the block length alone.</summary>
    private static VortexWriteOptions Options(
        WritePolicy? indexes = null, WriteProfile profile = WriteProfile.Default, Guid? identity = null) =>
        new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            WritePolicy = indexes ?? WritePolicy.None,
            Profile = profile,
            Identity = identity,
        };

    private static async Task<string> Dump(VortexFile file)
    {
        List<string> values = [];
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            Values.DescribeRows(batch, values);
        }

        Assert.Equal(Rows, values.Count);
        return string.Join('\n', values);
    }

    private static async Task<VortexFile> OpenAsync(byte[] bytes)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-index-{Guid.NewGuid():N}.vortex");
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        // Unix lets a mapped file be unlinked; the mapping keeps the bytes alive.
        System.IO.File.Delete(path);
        return file;
    }

    private static async Task<(byte[] Bytes, WriteReport Report)> WriteAsync(VortexWriteOptions options)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["id", "status", "label"], [i64, utf8, utf8], Nullability.NonNullable);

        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options))
        {
            for (int start = 0; start < Rows; start += Batch)
            {
                int count = Math.Min(Batch, Rows - start);
                int[] columns =
                [
                    Longs(arena, i64, start, count),
                    Strings(arena, utf8, start, count, i => Statuses[i % Statuses.Length]),
                    Strings(arena, utf8, start, count, i => "L" + ((i * 7919) % Rows).ToString("D8", CultureInfo.InvariantCulture)),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            report = await writer.CompleteAsync(CancellationToken.None);
        }

        return (stream.ToArray(), report);
    }

    private static int Longs(CanonicalArena arena, DType dtype, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = 1_000L + start + i;
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>Short strings only: every view is inline.</summary>
    private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value(start + i));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
