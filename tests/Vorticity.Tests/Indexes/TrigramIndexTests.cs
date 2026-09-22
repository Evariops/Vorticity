// `vorticity.bloom.ngram3.v1` and `vorticity.postings.ngram3.v1` end to end: every byte
// trigram of every value, and the string predicates that probe them.
//
// THE FIXTURE PUTS ONE WORD PER BLOCK in a column the zone map cannot prune -- every value starts
// with the same host -- so whatever `Contains('zebra')` prunes is the trigram index's doing. And
// the patterns cover what the text says claims nothing: a run shorter than three bytes, a pattern
// made of wildcards, a NOT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class TrigramIndexTests
{
    private const int Block = 1_024;
    private const int Blocks = 32;
    private const int Rows = Block * Blocks;

    private static readonly string[] Words =
    [
        "apple", "Zebra", "mango", "kiwi", "lemon", "peach", "grape", "melon",
        "cherry", "banana", "papaya", "guava", "lime", "plum", "fig", "date",
    ];

    /// <summary>One word per block, a value per row: the host is shared, so min/max prune nothing.</summary>
    private static string Url(int row) =>
        string.Create(CultureInfo.InvariantCulture, $"host.example/{Words[(row / Block) % Words.Length]}/{row % 97}");

    public static TheoryData<string, bool> Policies() => new()
    {
        { "bloom", false },
        { "bloom", true },
        { "postings", false },
        { "postings", true },
    };

    private static WritePolicy Policy(string kind, bool fold) =>
        WritePolicy.None.For("url", kind == "bloom"
            ? IndexSpec.NgramBloom(resolutions: 2, caseInsensitive: fold)
            : IndexSpec.NgramPostings(fold).WithSegmentEntries(64));

    private static readonly (string Label, StringMatchOp Op, string Pattern)[] Predicates =
    [
        ("contains zebra", StringMatchOp.Contains, "Zebra"),
        ("contains zebra lower", StringMatchOp.Contains, "zebra"),
        ("contains absent", StringMatchOp.Contains, "durian"),
        ("contains short", StringMatchOp.Contains, "le"),
        ("contains across", StringMatchOp.Contains, "le/1"),
        ("starts", StringMatchOp.StartsWith, "host.example/kiwi"),
        ("starts shared", StringMatchOp.StartsWith, "host."),
        ("like two runs", StringMatchOp.Like, "%grape%/4_"),
        ("like escaped", StringMatchOp.Like, "%a\\%b%"),
        ("like wildcards", StringMatchOp.Like, "%_a_%"),
        ("like absent", StringMatchOp.Like, "%xyz%mango%"),
        ("like exact", StringMatchOp.Like, "host.example/fig/3"),
    ];

    public static TheoryData<string, bool, int> Cases()
    {
        TheoryData<string, bool, int> cases = [];
        foreach (string kind in new[] { "bloom", "postings" })
        {
            foreach (bool fold in new[] { false, true })
            {
                for (int i = 0; i < Predicates.Length; i++)
                {
                    cases.Add(kind, fold, i);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task TheWriterBuildsTheTrigramIndex(string kind, bool fold)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(kind, fold));
        string name = kind == "bloom" ? IndexKinds.BloomNgram3 : IndexKinds.PostingsNgram3;
        IndexWriteReport report = Assert.IsType<IndexWriteReport>(written.Report.Index("url", name));
        Assert.True(report.Outcome == IndexOutcome.Built, report.Reason);

        // A trigram index over a number is refused with a reason.
        IndexWriteReport number = Assert.IsType<IndexWriteReport>(written.Report.Index("n", name));
        Assert.Equal(IndexOutcome.Abandoned, number.Outcome);
        Assert.Contains("text", number.Reason, StringComparison.Ordinal);

        IndexDirectory directory = Assert.IsType<IndexDirectory>(await written.File.ReadIndexDirectoryAsync(ct));
        Assert.All(directory.Entries, entry => Assert.Equal(name, entry.Kind));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AStringPredicateReturnsTheSameRowsWithTheIndexOnAndOff(string kind, bool fold, int predicate)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(kind, fold));
        (string label, StringMatchOp op, string pattern) = Predicates[predicate];
        VortexExpr filter = Build(op, pattern);

        long expected = Oracle(op, pattern);
        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).CountAsync(ct));
        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).WithIndexes(false).CountAsync(ct));
        Assert.Equal(
            await Materialize(written.File.ScanBuilder().Where(filter).WithIndexes(false)),
            await Materialize(written.File.ScanBuilder().Where(filter)));

        // And the index never keeps fewer blocks than hold a match, and the postings keep exactly
        // the blocks holding every required trigram.
        ScanExplanation plan = await written.File.ScanBuilder().Where(filter).ExplainAsync(ct);
        int holding = Holding(row => Matches(op, pattern, Url(row)));
        Assert.True(plan.LiveBlocks >= holding, $"{label}: {plan.LiveBlocks} live, {holding} hold a match");
        if (kind == "postings")
        {
            List<byte[]> required = Trigrams.Required((StringMatchExpr)filter, fold);
            int candidates = required.Count == 0 ? Blocks : BlocksHoldingAll(required, fold);
            Assert.Equal(candidates, plan.LiveBlocks);
        }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task ASubstringInOneWordPrunesTheOtherBlocks(string kind, bool fold)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy(kind, fold));
        ScanExplanation plan = await written.File.ScanBuilder().Where(Build(StringMatchOp.Contains, "papaya")).ExplainAsync(ct);

        // Two blocks hold `papaya`; a 1 % filter lets through a stray block now and then.
        Assert.Equal(0, Assert.Single(plan.Pruning, step => step.Structure == "zone map").BlocksPruned);
        Assert.InRange(plan.LiveBlocks, 2, kind == "bloom" ? 5 : 2);
    }

    [Fact]
    public async Task ACaseInsensitiveIndexAnswersACaseSensitivePredicate()
    {
        // `Zebra` folded is `zebra`; a folded index holds it wherever `Zebra` is, so it prunes the
        // same blocks -- and a folded `zebra` is present too, which the scan then rejects row by row.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(Policy("postings", fold: true));
        ScanExplanation upper = await written.File.ScanBuilder().Where(Build(StringMatchOp.Contains, "Zebra")).ExplainAsync(ct);
        ScanExplanation lower = await written.File.ScanBuilder().Where(Build(StringMatchOp.Contains, "zebra")).ExplainAsync(ct);
        Assert.Equal(2, upper.LiveBlocks);
        Assert.Equal(2, lower.LiveBlocks);
        Assert.Equal(0, await written.File.ScanBuilder().Where(Build(StringMatchOp.Contains, "zebra")).CountAsync(ct));

        await using Written exact = await Written.CreateAsync(Policy("postings", fold: false));
        ScanExplanation sensitive = await exact.File.ScanBuilder().Where(Build(StringMatchOp.Contains, "zebra")).ExplainAsync(ct);
        Assert.Equal(0, sensitive.LiveBlocks);
    }

    [Fact]
    public void LiteralRunsSplitAtUnescapedWildcards()
    {
        static string[] Runs(string pattern) =>
            BytePattern.LiteralRuns(Encoding.UTF8.GetBytes(pattern), (byte)'\\').ConvertAll(Encoding.UTF8.GetString).ToArray();

        Assert.Equal(["abc", "de"], Runs("%abc_de%"));
        Assert.Equal(["a%b"], Runs("a\\%b"));
        Assert.Equal(["x_y", "z"], Runs("x\\_y%z"));
        Assert.Empty(Runs("%_%"));
        Assert.Equal(["tail\\"], Runs("tail\\"));
    }

    // ------------------------------------------------------------------------------ oracle

    private static VortexExpr Build(StringMatchOp op, string pattern)
    {
        FieldExpr field = Expr.Field("url");
        FilterLiteral literal = FilterLiteral.From(pattern);
        return op switch
        {
            StringMatchOp.StartsWith => Expr.StartsWith(field, literal),
            StringMatchOp.Contains => Expr.Contains(field, literal),
            _ => Expr.Like(field, literal),
        };
    }

    private static bool Matches(StringMatchOp op, string pattern, string value)
    {
        ReadOnlySpan<byte> v = Encoding.UTF8.GetBytes(value);
        ReadOnlySpan<byte> p = Encoding.UTF8.GetBytes(pattern);
        return op switch
        {
            StringMatchOp.StartsWith => BytePattern.StartsWith(v, p),
            StringMatchOp.Contains => BytePattern.Contains(v, p),
            _ => BytePattern.Like(v, p, (byte)'\\'),
        };
    }

    private static long Oracle(StringMatchOp op, string pattern)
    {
        long count = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (Matches(op, pattern, Url(row)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The blocks whose rows TOGETHER hold every required trigram: exactly what a block-level
    /// postings list can say.
    /// </summary>
    private static int BlocksHoldingAll(List<byte[]> trigrams, bool fold)
    {
        int blocks = 0;
        Span<byte> scratch = stackalloc byte[3];
        for (int block = 0; block < Blocks; block++)
        {
            HashSet<string> present = [];
            for (int row = block * Block; row < (block + 1) * Block; row++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Url(row));
                for (int i = 0; i + 3 <= bytes.Length; i++)
                {
                    Trigrams.Copy(bytes.AsSpan(i, 3), fold, scratch);
                    present.Add(Convert.ToHexString(scratch));
                }
            }

            blocks += trigrams.TrueForAll(t => present.Contains(Convert.ToHexString(t))) ? 1 : 0;
        }

        return blocks;
    }

    /// <summary>The blocks holding at least one row the predicate selects.</summary>
    private static int Holding(Func<int, bool> predicate)
    {
        int blocks = 0;
        for (int block = 0; block < Blocks; block++)
        {
            bool holds = false;
            for (int row = block * Block; row < (block + 1) * Block && !holds; row++)
            {
                holds = predicate(row);
            }

            blocks += holds ? 1 : 0;
        }

        return blocks;
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
        private Written(string path, VortexFile file, WriteReport report)
        {
            Path = path;
            File = file;
            Report = report;
        }

        internal string Path { get; }

        internal VortexFile File { get; }

        internal WriteReport Report { get; }

        internal static async Task<Written> CreateAsync(WritePolicy policy)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-trigram-{Guid.NewGuid():N}.vortex");
            WritePolicy withNumber = policy.For("n", policy.Of("url"));
            WriteReport report = await WriteAsync(path, new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = null,
                WritePolicy = withNumber,
                IndexBudgetPerMille = 1_000_000,
            });
            return new Written(path, await VortexFile.OpenAsync(path), report);
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
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
            DType schema = types.Struct(["url", "n"], [utf8, i32], Nullability.NonNullable);

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            const int Batch = 3_000;
            for (int start = 0; start < Rows; start += Batch)
            {
                int count = Math.Min(Batch, Rows - start);
                int[] columns = [Strings(arena, utf8, start, count), Ints(arena, i32, start, count)];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            return await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Ints(CanonicalArena arena, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            for (int i = 0; i < count; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[(i * 4)..], (start + i) * 31 % 1000);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I32, buffer);
        }

        /// <summary>Out-of-line strings: one data buffer, views pointing into it.</summary>
        private static int Strings(CanonicalArena arena, DType dtype, int start, int count)
        {
            List<byte[]> values = [];
            int heap = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] value = Encoding.UTF8.GetBytes(Url(start + i));
                values.Add(value);
                heap += value.Length;
            }

            VortexBuffer data = arena.Allocate(heap, 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
            viewBytes.Clear();
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] value = values[i];
                Span<byte> view = viewBytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
                value.AsSpan(0, 4).CopyTo(view[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
                value.CopyTo(dataBytes[offset..]);
                offset += value.Length;
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
        }
    }
}
