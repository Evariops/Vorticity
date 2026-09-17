// `vorticity.bloom.sbbf.v1` end to end (docs/10-indexes.md §5.1, §4.3, §5.4, §6.6): written by the
// streaming builder, listed in the directory, probed by the scan's block-mask chain.
//
// THE FIXTURE DEFEATS THE ZONE MAP ON PURPOSE. Every block of `key` spans the whole value range, so
// min/max prune nothing and whatever is pruned is the filter's doing -- which `Explain` then
// credits to the right structure. And the acceptance test is 10 §6.6's: the same query with the
// indexes on and off returns the same rows, over equalities, IN lists, ANDs, ORs, both float zeros,
// literals of every comparison domain, and values that are absent everywhere.
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

public sealed class BloomIndexTests
{
    private const int Block = 1_024;
    private const int Blocks = 64;
    private const int Rows = Block * Blocks;
    private const int Batch = 8_192;
    private const int KeySpace = 100_000;

    private static readonly string[] Names = ["key", "name", "price", "flag", "small"];

    /// <summary>Uniform over [0, 100 000): every block spans the range, so its zone map proves nothing.</summary>
    private static int Key(int row) => (int)((uint)row * 2654435761u % KeySpace);

    /// <summary>Both float zeros, each in exactly one block.</summary>
    private static double Price(int row) => row switch
    {
        5 * Block + 3 => -0.0,
        9 * Block + 7 => 0.0,
        _ => Key(row) / 4.0 + 1.0,
    };

    private static WritePolicy Policy(int resolutions = 3) =>
        WritePolicy.None
            .For("key", IndexPolicy.Bloom(resolutions: resolutions))
            .For("name", IndexPolicy.Bloom(resolutions: resolutions))
            .For("price", IndexPolicy.Bloom(resolutions: resolutions))
            .For("flag", IndexPolicy.Bloom())
            .For("small", IndexPolicy.Bloom());

    [Fact]
    public async Task TheWriterBuildsEveryResolutionAndTheReportSaysSo()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());

        foreach (string column in new[] { "key", "name", "price" })
        {
            IndexWriteReport report = Assert.IsType<IndexWriteReport>(written.Report.Index(column, IndexKinds.BloomSbbf));
            Assert.True(report.Outcome == IndexOutcome.Built, column + ": " + report.Reason);
            Assert.Null(report.Reason);

            // One tree (13 §6.2): four generation nodes under a root that holds the file's filter.
            Assert.Equal(1, report.Runs);
            Assert.Equal((Blocks / BloomBuilder.GenerationBlocks) + 1, report.Generations);
            Assert.True(report.Bytes > 0);
        }

        IndexWriteReport flag = Assert.IsType<IndexWriteReport>(written.Report.Index("flag", IndexKinds.BloomSbbf));
        Assert.Equal(IndexOutcome.Abandoned, flag.Outcome);
        Assert.Contains("two-value", flag.Reason, StringComparison.Ordinal);

        IndexWriteReport small = Assert.IsType<IndexWriteReport>(written.Report.Index("small", IndexKinds.BloomSbbf));
        Assert.Equal(IndexOutcome.Abandoned, small.Outcome);
        Assert.Contains("distinct", small.Reason, StringComparison.Ordinal);

        // One entry per column, one run, one root: nothing in the directory grows with the blocks.
        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.Equal(3, directory.Entries.Count);
        foreach (IndexEntry entry in directory.Entries)
        {
            Assert.Equal(IndexKinds.BloomSbbf, entry.Kind);
            Assert.Equal((ulong)Block, entry.BlockLength);
            Assert.True(BloomIndexOptions.TryParse(entry.Options, out BloomIndexOptions? options));
            Assert.Equal(BloomBuilder.FileMaxBlocks, options!.RootMaxBlocks);
            IndexRun run = Assert.Single(entry.Runs);
            Assert.Equal((0UL, (uint)Blocks), (run.FirstBlock, run.BlockCount));
            IndexSegment segment = Assert.Single(run.Payload);
            Assert.Equal(0UL, segment.Offset % 64);
            Assert.True(segment.Offset + segment.Length <= (ulong)written.Length);
            Assert.True(BloomTreeRun.RootWords(run.OptionBytes) > BloomNode.HeaderWords);
        }

        // The report's bytes still sum to the file, with the runs between the chunks.
        Assert.Equal(written.Length, written.Report.Bytes.Total);
        Assert.True(written.Report.Bytes.Indexes > 0);
    }

    public static TheoryData<string> Filters()
    {
        int present = Key(40_000);
        int alsoPresent = Key(123);
        int absent = -1;
        for (int candidate = 0; candidate < KeySpace; candidate++)
        {
            if (!Contains(candidate))
            {
                absent = candidate;
                break;
            }
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        return
        [
            string.Create(c, $"key = {present}"),
            string.Create(c, $"key = {absent}"),
            string.Create(c, $"key in {present},{absent},{alsoPresent}"),
            string.Create(c, $"key in {absent}"),
            string.Create(c, $"key = {present} and key < 50000"),
            string.Create(c, $"key = {present} or key = {alsoPresent}"),
            string.Create(c, $"key = {absent} or key = {present}"),
            string.Create(c, $"key = {present}.0f"),
            string.Create(c, $"key = {present}u"),
            "key = 2.5f",
            "key = -3",
            string.Create(c, $"name = u{present}"),
            string.Create(c, $"name = u{absent}"),
            "name = nope",
            "price = 0.0f",
            "price = -0.0f",
            string.Create(c, $"price = {(present / 4.0) + 1.0:R}f"),
            string.Create(c, $"price = {present + 1}"),
            "price = NaNf",
            "key != 5",
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
        Assert.Equal(expected, await written.File.Scan().Where(filter).WithPruning(false).CountAsync());

        List<string> on = await Materialize(written.File.Scan().Where(filter));
        List<string> off = await Materialize(written.File.Scan().Where(filter).WithIndexes(false));
        Assert.Equal(off, on);
        Assert.Equal(expected, on.Count);
    }

    [Fact]
    public async Task TheFilterPrunesWhatTheZoneMapCannot()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        int present = Key(40_000);

        ScanPlan plan = await written.File.Scan().Where(Parse($"key = {present}")).ExplainAsync();
        Assert.Equal(Blocks, plan.Blocks);
        PruningStep zones = Assert.Single(plan.Pruning, step => step.Structure == "zone map");
        PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");
        Assert.Equal(0, zones.BlocksPruned);

        // At 1 % per block, one block holds the value and the other 63 each lie with 1 % odds.
        int holding = HoldingBlocks(present);
        Assert.InRange(bloom.BlocksPruned, Blocks - holding - 4, Blocks - holding);
        Assert.Equal(Blocks - bloom.BlocksPruned, plan.LiveBlocks);
        Assert.True(bloom.SegmentsRead > 0);

        ScanPlan off = await written.File.Scan().Where(Parse($"key = {present}")).WithIndexes(false).ExplainAsync();
        Assert.Equal(Blocks, off.LiveBlocks);
        Assert.DoesNotContain(off.Pruning, step => step.Structure == "bloom filter");
    }

    [Fact]
    public async Task AValueAbsentFromTheFileStopsAtTheFileFilter()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        ScanPlan plan = await written.File.Scan().Where(Parse("name = nope")).ExplainAsync();

        PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");
        Assert.True(plan.LiveBlocks == 0, string.Join("; ", plan.Pruning));
        Assert.Equal(Blocks, bloom.BlocksPruned);

        // One segment: the file-level filter. The mask was empty before the generations were asked,
        // and no split is left to read: what the plan reads is what the structures cost.
        Assert.Equal(1, bloom.SegmentsRead);
        Assert.Equal(0, plan.LiveSplits);
        int consulted = 0;
        foreach (PruningStep step in plan.Pruning)
        {
            consulted += step.SegmentsRead;
        }

        Assert.Equal(consulted, plan.SegmentsToRead);
    }

    [Fact]
    public async Task TheFileFilterAnswersTheMultiFileQuestion()
    {
        // 10 §5.4: an engine skips a file before opening a scan. The statistics cannot say anything
        // about `name = nope`; the file-level filter can.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        int present = Key(40_000);

        Assert.True(written.File.MayMatch(Parse("name = nope")));
        Assert.False(await written.File.MayMatchAsync(Parse("name = nope")));
        Assert.True(await written.File.MayMatchAsync(Parse(string.Create(CultureInfo.InvariantCulture, $"name = u{present}"))));
        Assert.False(await written.File.MayMatchAsync(Parse("name = nope and key < 50000")));
        Assert.True(await written.File.MayMatchAsync(Parse("key != 5")));

        // With two resolutions the root is built under the node ceiling, and answers as well; with
        // one, there is no node filter, and the async answer is the statistics' answer.
        await using Written two = await Written.CreateAsync(Policy(resolutions: 2));
        Assert.False(await two.File.MayMatchAsync(Parse("name = nope")));
        await using Written one = await Written.CreateAsync(Policy(resolutions: 1));
        Assert.True(await one.File.MayMatchAsync(Parse("name = nope")));
    }

    [Fact]
    public async Task TwoResolutionsDescendTheTree()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(resolutions: 2));
        int present = Key(40_000);

        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.Equal(3, directory.Entries.Count);

        ScanPlan plan = await written.File.Scan().Where(Parse($"key = {present}")).ExplainAsync();
        PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");

        // The root; the four generation nodes, one region; then the leaves of each generation still
        // live, one region each.
        int holding = HoldingBlocks(present);
        Assert.InRange(bloom.SegmentsRead, 3, 2 + Math.Min(holding + 1, Blocks / BloomBuilder.GenerationBlocks));
        Assert.True(plan.LiveBlocks <= holding + 4);
    }

    [Fact]
    public async Task AMalformedPayloadCostsPruningAndNeverRows()
    {
        // 10 §6.6: a lying index can only slow a scan down. A payload that does not decode to the
        // array its entry declares is ignored for the blocks it covers.
        Decoders.EnsureRegistered();
        byte[] bytes;
        await using (Written written = await Written.CreateAsync(Policy(resolutions: 1)))
        {
            bytes = System.IO.File.ReadAllBytes(written.Path);
            IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
            foreach (IndexEntry entry in directory.Entries)
            {
                foreach (IndexRun run in entry.Runs)
                {
                    // The blob's trailing u32 is its FlatBuffer's length: make it absurd.
                    IndexSegment segment = run.Payload[0];
                    int tail = checked((int)(segment.Offset + segment.Length - 4));
                    bytes.AsSpan(tail, 4).Fill(0xFF);
                }
            }
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-bloom-forged-{Guid.NewGuid():N}.vortex");
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            int present = Key(40_000);
            VortexExpr filter = Parse($"key = {present}");
            Assert.Equal(Oracle($"key = {present}"), await file.Scan().Where(filter).CountAsync());

            ScanPlan plan = await file.Scan().Where(filter).ExplainAsync();
            PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");
            Assert.Equal(0, bloom.BlocksPruned);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheBudgetAbandonsAFilterThatOutweighsTheData()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(), budgetPerMille: 1);
        IndexWriteReport key = Assert.IsType<IndexWriteReport>(written.Report.Index("key", IndexKinds.BloomSbbf));
        Assert.Equal(IndexOutcome.Abandoned, key.Outcome);
        Assert.Contains("budget", key.Reason, StringComparison.Ordinal);

        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.Empty(directory.Entries);

        // What was written before the verdict is dead weight, and the file still reads.
        int present = Key(40_000);
        Assert.Equal(Oracle($"key = {present}"), await written.File.Scan().Where(Parse($"key = {present}")).CountAsync());
    }

    [Fact]
    public async Task TheBudgetDoesNotAbandonAnIndexTheCallerRequired()
    {
        // The budget is a guard against `Auto`'s enthusiasm, not an override of an instruction:
        // docs/13-dataset.md §6.1's mandatory run is an index a structure DEPENDS on, and on a
        // narrow table it is intrinsically comparable in size to the column it indexes, so no file
        // is ever large enough to bring it under a share of the data. `AsRequired` says so, and the
        // optional filters around it are still the first thing the budget takes.
        Decoders.EnsureRegistered();
        WritePolicy policy = Policy().For("key", IndexPolicy.Bloom(resolutions: 3).AsRequired());
        await using Written written = await Written.CreateAsync(policy, budgetPerMille: 1);

        IndexWriteReport key = Assert.IsType<IndexWriteReport>(written.Report.Index("key", IndexKinds.BloomSbbf));
        Assert.Equal(IndexOutcome.Built, key.Outcome);
        IndexWriteReport name = Assert.IsType<IndexWriteReport>(written.Report.Index("name", IndexKinds.BloomSbbf));
        Assert.Equal(IndexOutcome.Abandoned, name.Outcome);
        Assert.Contains("budget", name.Reason, StringComparison.Ordinal);

        // "key" is field 0, and it is the only column left with an entry.
        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.NotEmpty(directory.Entries);
        foreach (IndexEntry entry in directory.Entries)
        {
            Assert.Equal([0u], entry.ColumnPath);
        }

        // And the answer is the answer either way, which is the only thing an index may not change.
        int present = Key(40_000);
        Assert.Equal(Oracle($"key = {present}"), await written.File.Scan().Where(Parse($"key = {present}")).CountAsync());
    }

    [Fact]
    public void AKindlessPolicyCannotBeRequired()
    {
        Assert.Throws<InvalidOperationException>(() => IndexPolicy.Auto.AsRequired());
        Assert.Throws<InvalidOperationException>(() => IndexPolicy.None.AsRequired());
        Assert.True(IndexPolicy.SortedRuns.AsRequired().Required);
        Assert.False(IndexPolicy.SortedRuns.Required);

        // The flag survives the other copy method, which is the one that could drop it.
        Assert.True(IndexPolicy.SortedRuns.AsRequired().WithSegmentEntries(4_096).Required);

        // And it is part of what a policy asks for, so two policies that differ by it differ.
        Assert.NotEqual(IndexPolicy.SortedRuns, IndexPolicy.SortedRuns.AsRequired());
    }

    [Fact]
    public async Task ARequiredPolicySurvivesTheDirectoryRoundTrip()
    {
        // An append reuses the directory's policy rather than being told one again (11 §3.8), so a
        // requirement that did not survive the round trip would hold for the first write and
        // quietly stop holding for every one after it.
        Decoders.EnsureRegistered();
        WritePolicy policy = Policy().For("key", IndexPolicy.Bloom(resolutions: 3).AsRequired());
        await using Written written = await Written.CreateAsync(policy);

        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync());
        Assert.True(directory.Policy.Of("key").Required);
        Assert.False(directory.Policy.Of("name").Required);

        Assert.True(
            IndexDirectory.TryParse(
                directory.ToBytes(), directory.RowCount, (ulong)written.File.FileLength,
                out IndexDirectory? again, out string? reason),
            reason);
        IndexDirectory read = Assert.IsType<IndexDirectory>(again);
        Assert.True(read.Policy.Of("key").Required);
        Assert.False(read.Policy.Of("name").Required);
    }

    [Fact]
    public async Task AnUnindexedScanReadsNoIndexSegment()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy());
        int present = Key(40_000);

        ScanMetrics on = new ScanMetrics();
        ScanMetrics off = new ScanMetrics();
        Assert.Equal(
            await written.File.Scan().Where(Parse($"key = {present}")).WithMetrics(on).CountAsync(),
            await written.File.Scan().Where(Parse($"key = {present}")).WithMetrics(off).WithIndexes(false).CountAsync());

        // With the filters, a handful of filter segments against a full decode of every block.
        Assert.True(on.ValuesDecoded < off.ValuesDecoded / 8, $"{on.ValuesDecoded} decoded against {off.ValuesDecoded}");
    }

    // ------------------------------------------------------------------------------ oracle

    private static bool Contains(int key)
    {
        for (int row = 0; row < Rows; row++)
        {
            if (Key(row) == key)
            {
                return true;
            }
        }

        return false;
    }

    private static int HoldingBlocks(int key)
    {
        HashSet<int> blocks = [];
        for (int row = 0; row < Rows; row++)
        {
            if (Key(row) == key)
            {
                blocks.Add(row / Block);
            }
        }

        return blocks.Count;
    }

    /// <summary>The count a filter selects, from the generator.</summary>
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
        switch (column)
        {
            case "key" when op == "in":
                foreach (string item in operand.Split(','))
                {
                    if (Key(row) == int.Parse(item, CultureInfo.InvariantCulture))
                    {
                        return true;
                    }
                }

                return false;
            case "key":
                double key = Key(row);
                double wanted = Number(operand);
                return op switch
                {
                    "=" => key == wanted,
                    "!=" => key != wanted,
                    "<" => key < wanted,
                    _ => throw new InvalidOperationException(op),
                };
            case "name":
                return "u" + Key(row).ToString(CultureInfo.InvariantCulture) == operand;
            case "price":
                return Price(row) == Number(operand);
            default:
                throw new InvalidOperationException(column);
        }
    }

    private static double Number(string operand) =>
        double.Parse(operand.TrimEnd('f', 'u'), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// The tiny grammar of <see cref="Filters"/>: <c>col op value</c>, joined by one <c>and</c> or
    /// <c>or</c>; a value suffixed <c>f</c> is a float literal, <c>u</c> an unsigned one.
    /// </summary>
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
                values.Add(FilterLiteral.From(long.Parse(item, CultureInfo.InvariantCulture)));
            }

            return Expr.In(field, [.. values]);
        }

        FilterLiteral literal = Literal(parts[0], parts[2]);
        return parts[1] switch
        {
            "=" => Expr.Eq(field, Expr.Literal(literal)),
            "!=" => Expr.Ne(field, Expr.Literal(literal)),
            "<" => Expr.Lt(field, Expr.Literal(literal)),
            _ => throw new InvalidOperationException(parts[1]),
        };
    }

    private static FilterLiteral Literal(string column, string operand)
    {
        if (column == "name")
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

        /// <remarks>
        /// THE BUDGET IS LIFTED BY DEFAULT HERE, and the reason is a measurement: at 1 % per block, a
        /// filter over 1 024 distinct keys is 2 KiB, and ALP and bit-packing put the same block's
        /// data under that -- 1,18 MiB of filters against 0,50 MiB of data over three columns. That
        /// is the case the default budget exists to refuse, and one test says so; the others test
        /// the filters.
        /// </remarks>
        internal static async Task<Written> CreateAsync(WritePolicy policy, int budgetPerMille = 1_000_000)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-bloom-{Guid.NewGuid():N}.vortex");
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
            DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType boolean = types.Bool(Nullability.NonNullable);
            DType schema = types.Struct(Names, [i32, utf8, f64, boolean, u8], Nullability.NonNullable);

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Batch)
            {
                int count = Math.Min(Batch, Rows - start);
                int[] columns =
                [
                    Fixed<int>(arena, i32, PType.I32, start, count, Key),
                    Strings(arena, utf8, start, count),
                    Fixed<double>(arena, f64, PType.F64, start, count, Price),
                    Bools(arena, boolean, start, count),
                    Fixed<byte>(arena, u8, PType.U8, start, count, row => (byte)(row % 5)),
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

        private static int Strings(CanonicalArena arena, DType dtype, int start, int count)
        {
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
            bytes.Clear();
            for (int i = 0; i < count; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes("u" + Key(start + i).ToString(CultureInfo.InvariantCulture));
                Span<byte> view = bytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
                utf8.CopyTo(view[4..]);
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
        }
    }
}
