// `vorticity.postings.hashed.v1` end to end: value to blocks through the value's hash, each run its
// (hash, block) pairs sorted, the hashes cut to the bits its pairs need plus ten, per segment as gaps
// beside the blocks.
//
// WHAT IS HELD: a column written in hundreds of chunks carries one run holding each distinct (value,
// block) pair once; an equality leaves the blocks that hold the value and reads one segment of the
// run; every filter counts what the oracle counts with the index and without; values the column lacks
// leave almost no block live; an append adds runs and never reads the old ones back, since a run keeps
// only the bits of its hashes its own pairs need; and a torn segment costs pruning, never rows.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
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
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class HashedPostingsTests
{
    private const int Block = 256;
    private const int Chunks = 300;
    private const int Rows = Block * Chunks;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["k", "s", "f", "n"],
        [
            Types.Primitive(PType.I64, Nullability.NonNullable),
            Types.Utf8(Nullability.NonNullable),
            Types.Primitive(PType.F64, Nullability.NonNullable),
            Types.Primitive(PType.I64, Nullability.Nullable),
        ],
        Nullability.NonNullable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Distinct in every row.</summary>
    private static long K(int row) => (row * 7919L) % 100_003;

    /// <summary>509 values, each in a block about every other.</summary>
    private static string S(int row) => "s" + ((row * 31) % 509).ToString(CultureInfo.InvariantCulture);

    /// <summary>A quarter of the keys, with -0.0 in one row and +0.0 in another.</summary>
    private static double F(int row) => row switch
    {
        5 * Block + 3 => -0.0,
        9 * Block + 7 => 0.0,
        _ => (K(row) % 25_000) / 4.0 + 1,
    };

    /// <summary>Null every seventh row.</summary>
    private static long? N(int row) => row % 7 == 0 ? null : K(row) % 3_000;

    private static WritePolicy Policy => WritePolicy.None
        .For("k", IndexSpec.HashedPostings.WithSegmentEntries(1_000))
        .For("s", IndexSpec.HashedPostings.WithSegmentEntries(64))
        .For("f", IndexSpec.HashedPostings)
        .For("n", IndexSpec.HashedPostings.WithSegmentEntries(50));

    [Fact]
    public async Task HundredsOfChunksWriteOneRunOfEachDistinctPair()
    {
        (byte[] bytes, WriteReport report) = await WriteAsync(Rows);
        Assert.Equal(Chunks, report.ChunkRows.Length);
        await using VortexFile file = await OpenAsync(bytes);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(Ct))!;
        Assert.Equal(4, directory.Entries.Count);
        foreach (IndexEntry entry in directory.Entries)
        {
            Assert.Equal(IndexKinds.PostingsHashed, entry.Kind);
            string column = file.DType.GetFieldName((int)entry.ColumnPath[0]);
            IndexRun run = Assert.Single(entry.Runs);
            Assert.Equal((0UL, (uint)Chunks), (run.FirstBlock, run.BlockCount));
            Assert.Equal((ulong)Pairs(column, Rows), run.EntryCount);

            // Inline or in fence pages past 64 segments: the same pairs either way.
            Assert.True(FenceTable.TryOpen(run, KeyRunOptions.HashedStride, KeyRunOptions.HashedLayout, out FenceTable? table, out string? reason), reason);
            ulong inSegments = 0;
            for (long s = 0; s < table!.SegmentCount; s++)
            {
                inSegments += (await table.GetAsync(file.IndexSourceOf(run), s, Ct)).Bounds.Entries;
            }

            Assert.Equal(run.EntryCount, inSegments);
            IndexWriteReport written = report.Index(column, IndexKinds.PostingsHashed)!;
            Console.Out.Write(FormattableString.Invariant(
                $"HASHED {column}: {run.EntryCount} pairs, {table.SegmentCount} segments{(table.Paged ? " in pages" : string.Empty)}, {(double)written.Bytes / run.EntryCount:F2} bytes a pair.\n"));

            // Gaps of about 13 bits and blocks of 9: about 3 bytes a pair once a segment's arrays
            // outweigh their headers, which segments of 50 or 64 pairs do not.
            if (column is "k" or "f")
            {
                Assert.InRange((double)written.Bytes / run.EntryCount, 2.5, 4);
            }
        }

        // The small segments page the run of k: 77 of them, past the 64 kept inline.
        IndexEntry k = directory.Entries.Single(e => e.ColumnPath[0] == 0);
        Assert.True(FenceTable.TryOpen(k.Runs[0], KeyRunOptions.HashedStride, KeyRunOptions.HashedLayout, out FenceTable? paged, out _));
        Assert.True(paged!.Paged);
    }

    [Theory]
    [InlineData("k")]
    [InlineData("s")]
    public async Task AnEqualityLeavesTheBlocksThatHoldTheValueAndReadsOneSegment(string column)
    {
        (byte[] bytes, _) = await WriteAsync(Rows);
        await using VortexFile file = await OpenAsync(bytes);
        int row = 12_345;
        VortexExpr equal = column == "k" ? Equal("k", FilterLiteral.From(K(row))) : Equal("s", FilterLiteral.From(S(row)));
        ScanExplanation plan = await file.ScanBuilder().Where(equal).ExplainAsync(Ct);
        PruningStep locating = Assert.Single(plan.Pruning, step => step.Structure == "locating index");
        int holding = Holding(r => column == "k" ? K(r) == K(row) : S(r) == S(row));
        Assert.Equal(holding, plan.LiveBlocks);

        // The fence page that names the segment, when the run is paged, then the segment's two arrays.
        Assert.InRange(locating.SegmentsRead, 2, 3);
        Assert.Equal(Oracle(equal), await file.ScanBuilder().Where(equal).CountAsync(Ct));
    }

    public static TheoryData<string> Filters() =>
    [
        "k = 4242", "k = 100002", "k in 1,2,3,79190", "k = 79190 or s = s17", "k in 7919,15838 and s = s31",
        "s = s508", "s = nope", "s in s1,s2,nope",
        "f = 0.0", "f = -0.0", "f = 1.25", "f = NaN",
        "n = 2999", "n = 4000", "n in 1,2,3",
    ];

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task EveryFilterCountsWhatTheOracleCounts(string text)
    {
        (byte[] bytes, _) = await WriteAsync(Rows);
        await using VortexFile file = await OpenAsync(bytes);
        VortexExpr filter = Parse(text);
        long expected = Oracle(filter);
        Assert.Equal(expected, await CountRowsAsync(file, filter, indexes: true));
        Assert.Equal(expected, await CountRowsAsync(file, filter, indexes: false));
    }

    [Fact]
    public async Task BothFloatZerosAreOneValue()
    {
        (byte[] bytes, _) = await WriteAsync(Rows);
        await using VortexFile file = await OpenAsync(bytes);
        foreach (double zero in new[] { 0.0, -0.0 })
        {
            ScanExplanation plan = await file.ScanBuilder().Where(Equal("f", FilterLiteral.From(zero))).ExplainAsync(Ct);
            Assert.Equal(2, plan.LiveBlocks);
        }
    }

    [Fact]
    public async Task ValuesTheColumnLacksLeaveAlmostNoBlockLive()
    {
        // 500 keys no row holds, each probed alone: a run of 76 800 pairs keeps 27 bits of each hash,
        // so a probe meets a pair of another value about once in 1 750. The zone maps cannot help,
        // the keys lying inside every block's range.
        (byte[] bytes, _) = await WriteAsync(Rows);
        await using VortexFile file = await OpenAsync(bytes);
        HashSet<long> held = [.. Enumerable.Range(0, Rows).Select(K)];
        long[] absent = [.. Enumerable.Range(0, 100_003).Select(v => (long)v).Where(v => !held.Contains(v)).Take(500)];
        int live = 0;
        foreach (long value in absent)
        {
            live += (await file.ScanBuilder().Where(Equal("k", FilterLiteral.From(value))).ExplainAsync(Ct)).LiveBlocks;
        }

        Assert.InRange(live, 0, 5);
        Assert.Equal(27, 64 - KeyRunOptions.HashShift(Rows));
    }

    [Fact]
    public void AShiftKeepsTenBitsBeyondThePairs()
    {
        Assert.Equal(54, KeyRunOptions.HashShift(0));
        Assert.Equal(54, KeyRunOptions.HashShift(1));
        Assert.Equal(53, KeyRunOptions.HashShift(2));
        Assert.Equal(44, KeyRunOptions.HashShift(1_024));
        Assert.Equal(43, KeyRunOptions.HashShift(1_025));
        Assert.Equal(0, KeyRunOptions.HashShift(ulong.MaxValue));
    }

    [Fact]
    public async Task AnAppendAddsRunsAndNeverReadsTheOldOnesBack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-hashed-{Guid.NewGuid():N}.vortex");
        try
        {
            int rows = 1_000;
            (byte[] first, _) = await WriteAsync(rows);
            await System.IO.File.WriteAllBytesAsync(path, first, Ct);
            int before = 0;
            for (int append = 0; append < 6; append++)
            {
                int added = 700 + (append * 37);
                await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, Options(), Ct))
                {
                    await FeedAsync(writer, rows, rows + added);
                    await writer.CompleteAsync(Ct);
                }

                rows += added;
                await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
                IndexDirectory directory = (await file.ReadIndexDirectoryAsync(Ct))!;
                IndexEntry k = directory.Entries.Single(e => e.ColumnPath[0] == 0);
                ulong next = 0;
                foreach (IndexRun run in k.Runs)
                {
                    Assert.Equal(next, run.FirstBlock);
                    next = run.EndBlock;
                }

                Assert.Equal((ulong)((rows + Block - 1) / Block), next);
                Assert.True(k.Runs.Count > before, $"{k.Runs.Count} runs after {before}");
                before = k.Runs.Count;

                foreach (int row in new[] { 17, rows - 3, rows / 2 })
                {
                    VortexExpr equal = Equal("k", FilterLiteral.From(K(row)));
                    Assert.Equal(1, await CountRowsAsync(file, equal, indexes: true));
                    Assert.Equal(1, (await file.ScanBuilder().Where(equal).ExplainAsync(Ct)).LiveBlocks);
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATornSegmentCostsPruningAndNeverRows()
    {
        (byte[] bytes, _) = await WriteAsync(Rows);
        List<IndexSegment> payloads = [];
        await using (VortexFile file = await OpenAsync(bytes))
        {
            IndexDirectory directory = (await file.ReadIndexDirectoryAsync(Ct))!;
            foreach (IndexEntry entry in directory.Entries)
            {
                foreach (IndexRun run in entry.Runs)
                {
                    payloads.AddRange(run.Payload);
                }
            }
        }

        // Every payload blob's trailing FlatBuffer length made absurd: nothing decodes, or its
        // checksum fails first.
        foreach (IndexSegment segment in payloads)
        {
            int tail = checked((int)(segment.Offset + segment.Length - 4));
            bytes.AsSpan(tail, 4).Fill(0xFF);
        }

        await using VortexFile forged = await OpenAsync(bytes);
        foreach (string text in new[] { "k = 4242", "s = s508", "n = 2999" })
        {
            VortexExpr filter = Parse(text);
            Assert.Equal(Oracle(filter), await CountRowsAsync(forged, filter, indexes: true));
        }
    }

    // ------------------------------------------------------------------------------ oracle

    /// <summary>The distinct (hash, block) pairs of a column's first <paramref name="rows"/> rows.</summary>
    private static int Pairs(string column, int rows)
    {
        HashSet<(ulong, int)> pairs = [];
        Span<byte> bytes = stackalloc byte[8];
        for (int row = 0; row < rows; row++)
        {
            ulong hash;
            switch (column)
            {
                case "k":
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, K(row));
                    hash = XxHash3.HashToUInt64(bytes);
                    break;
                case "s":
                    hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(S(row)));
                    break;
                case "f":
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes, F(row));
                    hash = XxHash3.HashToUInt64(bytes);
                    break;
                default:
                    if (N(row) is not { } n)
                    {
                        continue;
                    }

                    BinaryPrimitives.WriteInt64LittleEndian(bytes, n);
                    hash = XxHash3.HashToUInt64(bytes);
                    break;
            }

            pairs.Add((hash, row / Block));
        }

        return pairs.Count;
    }

    private static int Holding(Func<int, bool> predicate)
    {
        HashSet<int> blocks = [];
        for (int row = 0; row < Rows; row++)
        {
            if (predicate(row))
            {
                blocks.Add(row / Block);
            }
        }

        return blocks.Count;
    }

    private static long Oracle(VortexExpr filter)
    {
        long count = 0;
        for (int row = 0; row < Rows; row++)
        {
            count += Holds(filter, row) ? 1 : 0;
        }

        return count;
    }

    private static bool Holds(VortexExpr filter, int row) => filter switch
    {
        LogicalExpr logical => logical.IsAnd
            ? Holds(logical.Left, row) && Holds(logical.Right, row)
            : Holds(logical.Left, row) || Holds(logical.Right, row),
        ComparisonExpr comparison => Is(comparison.Field.Path, comparison.Value, row),
        InExpr @in => @in.Values.Any(v => Is(@in.Field.Path, v, row)),
        _ => throw new InvalidOperationException(filter.ToString()),
    };

    private static bool Is(string column, FilterLiteral value, int row) => column switch
    {
        "k" => value.Kind == FilterLiteralKind.Signed && K(row) == value.SignedValue,
        "s" => value.Kind == FilterLiteralKind.Bytes && S(row) == Encoding.UTF8.GetString(value.BytesValue),
        "f" => value.Kind == FilterLiteralKind.Float && F(row) == value.FloatValue,
        _ => value.Kind == FilterLiteralKind.Signed && N(row) == value.SignedValue,
    };

    private static VortexExpr Equal(string column, FilterLiteral value) => Expr.Eq(Expr.Field(column), Expr.Literal(value));

    private static VortexExpr Parse(string text)
    {
        if (text.Contains(" or ", StringComparison.Ordinal))
        {
            string[] arms = text.Split(" or ");
            return Expr.Or(Parse(arms[0]), Parse(arms[1]));
        }

        if (text.Contains(" and ", StringComparison.Ordinal))
        {
            string[] arms = text.Split(" and ");
            return Expr.And(Parse(arms[0]), Parse(arms[1]));
        }

        string[] parts = text.Split(' ');
        if (parts[1] == "in")
        {
            return Expr.In(Expr.Field(parts[0]), [.. parts[2].Split(',').Select(item => Literal(parts[0], item))]);
        }

        return Equal(parts[0], Literal(parts[0], parts[2]));
    }

    private static FilterLiteral Literal(string column, string operand) => column switch
    {
        "s" => FilterLiteral.From(operand),
        "f" => FilterLiteral.From(double.Parse(operand, NumberStyles.Float, CultureInfo.InvariantCulture)),
        _ => FilterLiteral.From(long.Parse(operand, CultureInfo.InvariantCulture)),
    };

    private static async Task<long> CountRowsAsync(VortexFile file, VortexExpr filter, bool indexes)
    {
        long count = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).WithIndexes(indexes).WithPruning(indexes).ExecuteAsync())
        {
            count += batch.RowCount;
            batch.Dispose();
        }

        return count;
    }

    // ------------------------------------------------------------------------------ the file

    private static VortexWriteOptions Options() => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = null,
        WritePolicy = Policy,
        IndexBudgetPerMille = 1_000_000,
    };

    private static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);

    private static async Task<(byte[] Bytes, WriteReport Report)> WriteAsync(int rows)
    {
        Decoders.EnsureRegistered();
        using MemoryStream stream = new MemoryStream();
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, Options()))
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
                    Schema, count, Validity.NonNullable,
                    [Longs(arena, 0, from, count, K), Strings(arena, from, count), Doubles(arena, from, count), Nullable(arena, from, count)]);
                using RecordBatch batch = new RecordBatch(arena, root, from);
                await writer.WriteAsync(batch);
            }
            finally
            {
                arena.Reset();
            }
        }
    }

    private static int Longs(CanonicalArena arena, int field, int start, int count, Func<int, long> value)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = value(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(field), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Doubles(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = F(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(2), count, Validity.NonNullable, PType.F64, buffer);
    }

    private static int Nullable(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < count; i++)
        {
            if (N(start + i) is { } n)
            {
                values[i] = n;
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        Validity validity = Validity.Bitmap(arena.AddBool(Types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0));
        return arena.AddPrimitive(Schema.GetField(3), count, validity, PType.I64, buffer);
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
