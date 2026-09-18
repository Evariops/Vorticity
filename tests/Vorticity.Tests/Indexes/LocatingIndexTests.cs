// `vorticity.postings.blocks.v1` and `vorticity.sorted.runs.v1` end to end (docs/10-indexes.md
// §6.1, §6.2, §4.2, §6.6): written chunk by chunk, blocked into segments, probed after the zone maps.
//
// THE BATCHES DO NOT LINE UP WITH THE CHUNKS, on purpose: 5 000 rows at a time into 1 024-row
// blocks, so every chunk close finds carried rows in the builder's log and has to cut it. And the
// segments are small on two columns, so a run has many of them and a probe must pick the right one.
//
// A LOCATING INDEX IS EXACT AT BLOCK GRANULARITY, so unlike a Bloom filter it leaves live exactly
// the blocks that hold the value -- and the tests say so with an equality, not a range.
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
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class LocatingIndexTests
{
    private const int Block = 1_024;
    private const int Blocks = 64;
    private const int Rows = Block * Blocks;
    private const int Batch = 5_000;
    private const int KeySpace = 100_000;

    private static readonly string[] Names = ["key", "name", "price", "opt", "status", "flag"];
    private static readonly string[] Statuses = ["open", "closed", "pending", "void", "held"];

    private static int Key(int row) => (int)((uint)row * 2654435761u % KeySpace);

    private static double Price(int row) => row switch
    {
        5 * Block + 3 => -0.0,
        9 * Block + 7 => 0.0,
        _ => (Key(row) % 5_000) / 4.0,
    };

    private static long? Opt(int row) => row % 7 == 0 ? null : Key(row) % 3_000;

    private static string Status(int row) => Statuses[(row / 300) % Statuses.Length];

    private static WritePolicy Policy() =>
        WritePolicy.None
            .For("key", IndexPolicy.Postings)
            .For("name", IndexPolicy.SortedRuns.WithSegmentEntries(300))
            .For("price", IndexPolicy.SortedRuns)
            .For("opt", IndexPolicy.Postings.WithSegmentEntries(50))
            .For("status", IndexPolicy.SortedRuns)
            .For("flag", IndexPolicy.Postings);

    [Fact]
    public async Task TheWriterMergesTheChunkRunsIntoOneRunAndCountsItsEntries()
    {
        // 13 §6.1 (step 22): a run per chunk is how the builders work, one run per entry is what
        // they write. The rows are a whole number of blocks, so no chunk keeps a run of its own.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        int chunks = written.Report.ChunkRows.Count;
        Assert.True(chunks > 1);
        Assert.Equal(0, Rows % Block);

        foreach (string column in new[] { "key", "name", "price", "opt", "status" })
        {
            string kind = column is "key" or "opt" ? IndexKinds.PostingsBlocks : IndexKinds.SortedRuns;
            IndexWriteReport report = Assert.IsType<IndexWriteReport>(written.Report.Index(column, kind));
            Assert.True(report.Outcome == IndexOutcome.Built, column + ": " + report.Reason);
            Assert.Equal(1, report.Runs);
            Assert.True(report.Bytes > 0);
        }

        // A bool is keyed by nothing: two values have nothing to locate.
        IndexWriteReport flag = Assert.IsType<IndexWriteReport>(written.Report.Index("flag", IndexKinds.PostingsBlocks));
        Assert.Equal(IndexOutcome.Abandoned, flag.Outcome);

        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.Equal(5, directory.Entries.Count);
        foreach (IndexEntry entry in directory.Entries)
        {
            string column = written.File.Schema.GetFieldName((int)entry.ColumnPath[0]);
            ulong entries = 0;
            ulong block = 0;
            foreach (IndexRun run in entry.Runs)
            {
                Assert.Equal(block, run.FirstBlock);
                block = run.EndBlock;

                // Inline or in fence pages past 64 segments (13 §6.3): the same segments either way.
                FenceTable table = Table(written.File, entry, run);
                if (!table.Paged)
                {
                    Assert.Equal(KeyRunOptions.StrideOf(entry.Kind) * table.SegmentCount, run.Payload.Count);
                }

                ulong inSegments = 0;
                for (long s = 0; s < table.SegmentCount; s++)
                {
                    inSegments += (await table.GetAsync(written.File.IndexSourceOf(run), s, default)).Bounds.Entries;
                }

                Assert.Equal(run.EntryCount, inSegments);
                entries += run.EntryCount;
            }

            Assert.Equal((ulong)Blocks, block);
            if (entry.Kind == IndexKinds.SortedRuns)
            {
                // Every non-null row, once.
                Assert.Equal((ulong)Rows, entries);
            }
            else if (column == "opt")
            {
                Assert.True(entries > 0);
            }
        }

        // The small segments really cut the runs.
        IndexEntry name = Assert.Single(directory.Entries, e => written.File.Schema.GetFieldName((int)e.ColumnPath[0]) == "name");
        Assert.True(Table(written.File, name, name.Runs[0]).SegmentCount > 5);
        Assert.Equal(written.Length, written.Report.Bytes.Total);
    }

    /// <summary>A run's segment table, whether inline or in pages.</summary>
    private static FenceTable Table(VortexFile file, IndexEntry entry, IndexRun run)
    {
        Assert.True(KeyLayout.TryOf(file.Schema.GetField((int)entry.ColumnPath[0]), out KeyLayout layout));
        Assert.True(
            FenceTable.TryOpen(run, KeyRunOptions.StrideOf(entry.Kind), layout, out FenceTable? table, out string? reason),
            reason);
        return table!;
    }

    public static TheoryData<string> Filters() => [.. FilterTexts()];

    private static string[] FilterTexts()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        int present = Key(40_000);
        int other = Key(123);
        int absent = FirstAbsent();
        return
        [
            string.Create(c, $"key = {present}"),
            string.Create(c, $"key = {absent}"),
            string.Create(c, $"key in {present},{absent},{other}"),
            string.Create(c, $"key = {present} and key < 50000"),
            string.Create(c, $"key = {present} or key = {other}"),
            string.Create(c, $"key = {present}.0f"),
            string.Create(c, $"key = {present}u"),
            "key = 2.5f",
            string.Create(c, $"name = u{present}"),
            string.Create(c, $"name = u{absent}"),
            string.Create(c, $"name in u{present},u{other},nope"),
            "name = nope",
            "price = 0.0f",
            "price = -0.0f",
            string.Create(c, $"price = {(Key(40_000) % 5_000) / 4.0:R}f"),
            "price = NaNf",
            string.Create(c, $"opt = {Opt(40_001)}"),
            "opt = 2999",
            "opt = 5000",
            "opt in 1,2,3",
            "status = held",
            "status = nope",
            "status = open or key = 5",
        ];
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task AScanReturnsTheSameRowsWithTheIndexesOnAndOff(string text)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        VortexExpr filter = Parse(text);

        long expected = Oracle(text);
        Assert.Equal(expected, await written.File.Scan().Where(filter).CountAsync());
        Assert.Equal(expected, await written.File.Scan().Where(filter).WithIndexes(false).CountAsync());

        List<string> on = await Materialize(written.File.Scan().Where(filter));
        List<string> off = await Materialize(written.File.Scan().Where(filter).WithIndexes(false));
        Assert.Equal(off, on);
        Assert.Equal(expected, on.Count);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("name")]
    public async Task AnEqualityLeavesExactlyTheBlocksThatHoldTheValue(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        int present = Key(40_000);
        string value = present.ToString(CultureInfo.InvariantCulture);
        string text = column == "key" ? "key = " + value : "name = u" + value;

        ScanPlan plan = await written.File.Scan().Where(Parse(text)).ExplainAsync();
        PruningStep zones = Assert.Single(plan.Pruning, step => step.Structure == "zone map");
        PruningStep locating = Assert.Single(plan.Pruning, step => step.Structure == "locating index");
        Assert.Equal(0, zones.BlocksPruned);
        Assert.Equal(HoldingBlocks(row => Matches(text, row)), plan.LiveBlocks);
        Assert.Equal(Blocks - plan.LiveBlocks, locating.BlocksPruned);

        // One segment per run is enough to find a key: the run's table says which.
        int stride = column == "key" ? KeyRunOptions.PostingsStride : KeyRunOptions.SortedStride;
        Assert.True(locating.SegmentsRead <= stride * written.Report.ChunkRows.Count, $"{locating.SegmentsRead} segments read");
    }

    [Fact]
    public async Task AValueEverywhereKillsNothingAndAValueNowhereKillsEverything()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());

        ScanPlan everywhere = await written.File.Scan().Where(Parse("status = held")).ExplainAsync();
        Assert.Equal(HoldingBlocks(row => Status(row) == "held"), everywhere.LiveBlocks);

        ScanPlan nowhere = await written.File.Scan().Where(Parse("status = nope")).ExplainAsync();
        Assert.Equal(0, nowhere.LiveBlocks);

        // A key past every block's maximum is the zone map's to kill, and the index is never asked:
        // cheapest first, and the chain stops at an empty mask.
        ScanPlan outOfRange = await written.File.Scan().Where(Parse("opt = 5000")).ExplainAsync();
        Assert.Equal(0, outOfRange.LiveBlocks);
        Assert.DoesNotContain(outOfRange.Pruning, step => step.Structure == "locating index");
    }

    [Fact]
    public async Task BothFloatZerosAreOneKey()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());

        // Row 5·1024+3 holds -0.0, row 9·1024+7 holds +0.0, and many rows hold key % 5000 == 0.
        foreach (string text in new[] { "price = 0.0f", "price = -0.0f" })
        {
            ScanPlan plan = await written.File.Scan().Where(Parse(text)).ExplainAsync();
            Assert.Equal(HoldingBlocks(row => Price(row) == 0.0), plan.LiveBlocks);
            Assert.True(plan.LiveBlocks < Blocks);
        }
    }

    [Fact]
    public async Task TheBudgetAbandonsALocatingIndexToo()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(), budgetPerMille: 1);
        IndexWriteReport name = Assert.IsType<IndexWriteReport>(written.Report.Index("name", IndexKinds.SortedRuns));
        Assert.Equal(IndexOutcome.Abandoned, name.Outcome);
        Assert.Contains("budget", name.Reason, StringComparison.Ordinal);

        int present = Key(40_000);
        string text = string.Create(CultureInfo.InvariantCulture, $"name = u{present}");
        Assert.Equal(Oracle(text), await written.File.Scan().Where(Parse(text)).CountAsync());
    }

    [Fact]
    public async Task AMalformedRunCostsPruningAndNeverRows()
    {
        Decoders.EnsureRegistered();
        byte[] bytes;
        List<IndexSegment> keySegments = [];
        await using (Written written = await Written.CreateAsync(Policy()))
        {
            bytes = System.IO.File.ReadAllBytes(written.Path);
            IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
            foreach (IndexEntry entry in directory.Entries)
            {
                foreach (IndexRun run in entry.Runs)
                {
                    keySegments.AddRange(run.Payload);
                }
            }
        }

        // Every payload blob's trailing FlatBuffer length made absurd: nothing decodes.
        foreach (IndexSegment segment in keySegments)
        {
            int tail = checked((int)(segment.Offset + segment.Length - 4));
            bytes.AsSpan(tail, 4).Fill(0xFF);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-locating-forged-{Guid.NewGuid():N}.vortex");
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            foreach (string text in FilterTexts())
            {
                Assert.Equal(Oracle(text), await file.Scan().Where(Parse(text)).CountAsync());
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ oracle

    private static int FirstAbsent()
    {
        HashSet<int> keys = [];
        for (int row = 0; row < Rows; row++)
        {
            keys.Add(Key(row));
        }

        for (int candidate = 0; ; candidate++)
        {
            if (!keys.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static int HoldingBlocks(Func<int, bool> predicate)
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

    private static long Oracle(string text)
    {
        long count = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (Matches(text, row))
            {
                count++;
            }
        }

        return count;
    }

    private static bool Matches(string text, int row)
    {
        if (text.Contains(" or ", StringComparison.Ordinal))
        {
            string[] arms = text.Split(" or ");
            return Matches(arms[0], row) || Matches(arms[1], row);
        }

        if (text.Contains(" and ", StringComparison.Ordinal))
        {
            string[] arms = text.Split(" and ");
            return Matches(arms[0], row) && Matches(arms[1], row);
        }

        string[] parts = text.Split(' ');
        string column = parts[0];
        string op = parts[1];
        string operand = parts[2];
        if (op == "in")
        {
            foreach (string item in operand.Split(','))
            {
                if (Matches($"{column} = {item}", row))
                {
                    return true;
                }
            }

            return false;
        }

        switch (column)
        {
            case "key":
                double key = Key(row);
                double wanted = Number(operand);
                return op == "=" ? key == wanted : key < wanted;
            case "name":
                return "u" + Key(row).ToString(CultureInfo.InvariantCulture) == operand;
            case "price":
                return Price(row) == Number(operand);
            case "opt":
                return Opt(row) is { } opt && opt == Number(operand);
            case "status":
                return Status(row) == operand;
            default:
                throw new InvalidOperationException(column);
        }
    }

    private static double Number(string operand) =>
        double.Parse(operand.TrimEnd('f', 'u'), NumberStyles.Float, CultureInfo.InvariantCulture);

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
        FieldExpr field = Expr.Field(parts[0]);
        if (parts[1] == "in")
        {
            List<FilterLiteral> values = [];
            foreach (string item in parts[2].Split(','))
            {
                values.Add(Literal(parts[0], item));
            }

            return Expr.In(field, [.. values]);
        }

        FilterLiteral literal = Literal(parts[0], parts[2]);
        return parts[1] switch
        {
            "=" => Expr.Eq(field, Expr.Literal(literal)),
            "<" => Expr.Lt(field, Expr.Literal(literal)),
            _ => throw new InvalidOperationException(parts[1]),
        };
    }

    private static FilterLiteral Literal(string column, string operand)
    {
        if (column is "name" or "status")
        {
            return FilterLiteral.From(operand);
        }

        if (operand.EndsWith('f'))
        {
            return FilterLiteral.From(double.Parse(operand[..^1], NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (operand.EndsWith('u'))
        {
            return FilterLiteral.From(ulong.Parse(operand[..^1], CultureInfo.InvariantCulture));
        }

        return FilterLiteral.From(long.Parse(operand, CultureInfo.InvariantCulture));
    }

    private static async Task<List<string>> Materialize(ScanBuilder scan)
    {
        List<string> values = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            Values.DescribeRows(batch, values);
        }

        return values;
    }

    // ------------------------------------------------------------------------------ fixture

    private sealed class Written : IAsyncDisposable
    {
        private Written(string path, VortexFile file, WriteReport report, long length)
        {
            Path = path;
            File = file;
            Report = report;
            Length = length;
        }

        internal string Path { get; }

        internal VortexFile File { get; }

        internal WriteReport Report { get; }

        internal long Length { get; }

        internal static async Task<Written> CreateAsync(WritePolicy policy, int budgetPerMille = 1_000_000)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-locating-{Guid.NewGuid():N}.vortex");
            WriteReport report = await WriteAsync(path, new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = null,
                Indexes = policy,
                IndexBudgetPerMille = budgetPerMille,
            });
            long length = new System.IO.FileInfo(path).Length;
            return new Written(path, await VortexFile.OpenAsync(path), report, length);
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(Path);
        }

        private static async Task<WriteReport> WriteAsync(string path, VortexWriteOptions options)
        {
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
            DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
            DType i64n = types.Primitive(PType.I64, Nullability.Nullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType boolean = types.Bool(Nullability.NonNullable);
            DType schema = types.Struct(Names, [i32, utf8, f64, i64n, utf8, boolean], Nullability.NonNullable);

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Batch)
            {
                int count = Math.Min(Batch, Rows - start);
                int[] columns =
                [
                    Fixed<int>(arena, i32, PType.I32, start, count, Key),
                    Strings(arena, utf8, start, count, row => "u" + Key(row).ToString(CultureInfo.InvariantCulture)),
                    Fixed<double>(arena, f64, PType.F64, start, count, Price),
                    NullableLongs(arena, types, i64n, start, count),
                    Strings(arena, utf8, start, count, Status),
                    Bools(arena, boolean, start, count),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            return await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Fixed<T>(CanonicalArena arena, DType dtype, PType ptype, int start, int count, Func<int, T> value)
            where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            VortexBuffer buffer = arena.Allocate(count * size, size, out Span<byte> bytes);
            Span<T> values = MemoryMarshal.Cast<byte, T>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, ptype, buffer);
        }

        private static int NullableLongs(CanonicalArena arena, DTypeArena types, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                long? v = Opt(start + i);
                values[i] = v ?? 12345;
                if (v is not null)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            int mask = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
            return arena.AddPrimitive(dtype, count, Validity.Bitmap(mask), PType.I64, buffer);
        }

        private static int Bools(CanonicalArena arena, DType dtype, int start, int count)
        {
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                if (Key(start + i) % 2 == 0)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return arena.AddBool(dtype, count, Validity.NonNullable, bits, 0);
        }

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
}
