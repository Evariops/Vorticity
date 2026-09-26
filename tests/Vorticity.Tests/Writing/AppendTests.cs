// Appending. Write-then-append equals one write in rows returned, zone map content and index
// answers; a torn append is repaired by `vxdump --repair` and reads as the pre-append file.
//
// THE ORACLE IS THE SAME ROWS WRITTEN ONCE. Each case writes the rows in one file and, in another,
// in pieces -- the first written, the others appended one at a time -- at boundaries that fall on a
// block and inside one (the re-opened chunk), and compares what a reader sees: every value, the zone
// map of every column, the file statistics, and the answers of every index kind.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class AppendTests
{
    private const int Rows = 20_000;
    private const int Block = 1_024;

    private static readonly string[] Names = ["id", "v", "s", "f"];

    private static long Id(int row) => row;

    private static int? V(int row) => row % 17 == 0 ? null : (int)((row * 7919L) % 1_000);

    private static string S(int row) => "s" + ((row * 31) % 211).ToString(CultureInfo.InvariantCulture);

    private static double F(int row) => ((row * 13) % 101) / 4.0;

    private static WritePolicy Policy => WritePolicy.Auto
        .For("v", IndexSpec.SortedRuns.WithSegmentEntries(512))
        .For("s", IndexSpec.Postings)
        .For("f", IndexSpec.Bloom(resolutions: 3));

    public static TheoryData<int[]> Splits() => new()
    {
        new[] { 8_192 },           // a whole number of blocks: nothing is read again
        new[] { 5_000 },           // inside a block: the last chunk is re-opened
        new[] { 3_000, 9_000 },    // two appends
        new[] { 1_024, 2_048, 3_000, 4_096, 5_000, 6_500, 8_192, 10_000, 13_333, 17_000 }, // eleven pieces
    };

    [Theory]
    [MemberData(nameof(Splits))]
    public async Task AnAppendedFileReadsAsOneWrite(int[] cuts)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string once = TempPath();
        string pieces = TempPath();
        try
        {
            await WriteAsync(once, 0, Rows);
            int start = 0;
            long length = 0;
            foreach (int cut in (int[])[.. cuts, Rows])
            {
                if (start == 0)
                {
                    await WriteAsync(pieces, 0, cut);
                }
                else
                {
                    length = new FileInfo(pieces).Length;
                    await using VortexFileWriter writer = await VortexFileWriter.AppendAsync(pieces, cancellationToken: ct);
                    await FeedAsync(writer, start, cut);
                    WriteReport report = await writer.CompleteAsync(ct);
                    Assert.All(report.Indexes, r => Assert.True(
                        r.Outcome == IndexOutcome.Built || r.Kind == IndexKinds.DictProbe || r.Kind == IndexKinds.BloomSbbf && r.Column != "f",
                        $"{r.Column} {r.Kind}: {r.Reason}"));
                }

                start = cut;
            }

            await using VortexFile expected = await VortexFile.OpenAsync(once, ct);
            await using VortexFile actual = await VortexFile.OpenAsync(pieces, ct);
            Assert.Equal(Rows, actual.RowCount);
            Assert.Equal(await RowsOf(expected), await RowsOf(actual));
            await AssertSameZones(expected, actual);
            AssertSameStatistics(expected, actual);
            await AssertSameAnswers(expected, actual);

            IndexDirectory? directory = await actual.ReadIndexDirectoryAsync(ct);
            Assert.NotNull(directory);
            Assert.Equal((ulong)length, directory.PreviousEof);
            Assert.Equal(Policy.Columns.Count, directory.Policy.Columns.Count);
        }
        finally
        {
            System.IO.File.Delete(once);
            System.IO.File.Delete(pieces);
        }
    }

    [Fact]
    public async Task AnIndexTheAppendAbandonedCoversPartOfTheFileAndNoWalkTrustsIt()
    {
        // Found by this file's first run: the append's builders were abandoned for the budget, the
        // old runs stayed, and the exact cover counted the old rows only. A partial index still
        // prunes; a key source over it is refused, and every answer falls back to the data.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 8_192);
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(
                path, new VortexWriteOptions { IndexBudgetPerMille = 1, WritePolicy = Policy }, ct))
            {
                await FeedAsync(writer, 8_192, Rows);
                WriteReport report = await writer.CompleteAsync(ct);
                Assert.Contains(report.Indexes, r => r.Column == "v" && r.Outcome == IndexOutcome.Abandoned);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            KeyPlan plan = await file.Keys("v").ExplainAsync(ct);
            Assert.Equal(KeySourceKind.None, plan.Source);
            Assert.Contains(plan.Rejected, r => r.Source == KeySourceKind.SortedRuns && r.Reason.Contains("cover", StringComparison.Ordinal));

            VortexExpr filter = Expr.Eq(Expr.Field("v"), Expr.Literal(FilterLiteral.From(417L)));
            int expected = 0;
            for (int row = 0; row < Rows; row++)
            {
                expected += V(row) == 417 ? 1 : 0;
            }

            Assert.Equal(expected, await file.ScanBuilder().Where(filter).CountAsync(ct));
            List<string> rows = await FilteredRows(file, filter);
            Assert.Equal(expected, rows.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheFirstAppendedChunkConsultsTheLastChunksPlan()
    {
        // Plan memory is seeded from the last chunk's encoding tree. The cut falls on a
        // block, so nothing is re-opened and re-priced, and the append is one block -- one chunk,
        // the first -- which without the seed has no memory to consult and no distinct table.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            WriteReport before = await WriteAsync(path, 0, 8_192);
            WriteReport after;
            long fromTable;
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, cancellationToken: ct))
            {
                await FeedAsync(writer, 8_192, 8_192 + Block);
                after = await writer.CompleteAsync(ct);
                fromTable = writer.ChunksFromTable;
            }

            Assert.Equal(new int[] { Block }, after.ChunkRows.Skip(after.ChunkRows.Length - 1));
            int seeded = 0;
            int dictionaries = 0;
            for (int field = 0; field < Names.Length; field++)
            {
                string last = before.Columns[field].Encodings[^1];
                ColumnWriteReport appended = after.Columns[field];
                bool scheme = last is not nameof(EncodingHint.Canonical);
                Assert.True(
                    appended.PlansPriced == (scheme ? 1 : 0),
                    $"{Names[field]}: last chunk {last}, {appended.PlansPriced} plans priced");
                seeded += scheme ? 1 : 0;
                dictionaries += last == nameof(EncodingHint.Dictionary) && appended.Encodings[^1] == last ? 1 : 0;
            }

            // The sequence and the dictionary, at least; and each dictionary kept was read off the
            // table the seed turned on.
            Assert.Equal(nameof(ColumnScheme.Sequence), before.Columns[0].Encodings[^1]);
            Assert.Equal(nameof(EncodingHint.Dictionary), before.Columns[2].Encodings[^1]);
            Assert.True(seeded >= 2, $"{seeded} columns seeded");
            Assert.True(dictionaries >= 1);
            Assert.Equal(dictionaries, fromTable);

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(8_192 + Block, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ASecondAppendToAFileBeingAppendedIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 8_192);
            await using (VortexFileWriter first = await VortexFileWriter.AppendAsync(path, cancellationToken: ct))
            {
                // The first holds the file alone until it completes, a lock every writer of this
                // library takes: the second is refused, before it reads a byte.
                await Assert.ThrowsAsync<IOException>(async () => await VortexFileWriter.AppendAsync(path, cancellationToken: ct));
                await FeedAsync(first, 8_192, 8_192 + Block);
                await first.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(8_192 + Block, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATornAppendIsRepairedToTheFileBeforeIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 5_000);
            long before = new FileInfo(path).Length;
            List<string> rows;
            await using (VortexFile file = await VortexFile.OpenAsync(path, ct))
            {
                rows = await RowsOf(file);
            }

            // A valid file is left alone.
            VortexRepairResult untouched = await VortexFileRepair.RepairAsync(path, ct);
            Assert.False(untouched.Truncated);
            Assert.Equal(before, untouched.Length);

            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, cancellationToken: ct))
            {
                await FeedAsync(writer, 5_000, 12_000);
                await writer.CompleteAsync(ct);
            }

            // The tear: the append's last bytes never reached the disk.
            long after = new FileInfo(path).Length;
            await using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(after - 37);
            }

            // The torn file opens at the version before the append and says so; refusing
            // is an option, and nothing is written behind the tear.
            await using (VortexFile torn = await VortexFile.OpenAsync(path, ct))
            {
                Assert.Equal((after - 37, before), (torn.TornTail!.FileLength, torn.TornTail.ValidLength));
                Assert.Equal(before, torn.FileLength);
                Assert.Equal(rows, await RowsOf(torn));
            }

            await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(
                path, new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse }, ct));
            VortexFormatException refused = await Assert.ThrowsAsync<VortexFormatException>(
                async () => await VortexFileWriter.AppendAsync(path, cancellationToken: ct));
            Assert.Contains("torn tail", refused.Message, StringComparison.Ordinal);

            VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path, ct);
            Assert.True(repaired.Truncated);
            Assert.Equal(before, repaired.Length);
            await using VortexFile back = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(5_000, back.RowCount);
            Assert.Equal(rows, await RowsOf(back));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileOfAnotherShapeIsRefused()
    {
        Decoders.EnsureRegistered();

        // A reference file whose column is a dictionary layout, not chunks of flat segments.
        string path = TempPath();
        try
        {
            System.IO.File.Copy(Corpus.Path("containers/dict_layout"), path);
            VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
                async () => await VortexFileWriter.AppendAsync(path, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("Rewrite", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileIndexedAfterTheFactAnswersAsOneIndexedWriteAndKeepsItsData()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string once = TempPath();
        string later = TempPath();
        try
        {
            await WriteAsync(once, 0, Rows);
            await WriteAsync(later, 0, Rows, WritePolicy.None);
            byte[] before = await System.IO.File.ReadAllBytesAsync(later, ct);

            IReadOnlyList<IndexWriteReport> reports = await VortexFileIndexer.AppendIndexesAsync(
                later, Policy, new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 }, ct);
            Assert.Contains(reports, r => r.Column == "v" && r.Kind == IndexKinds.SortedRuns && r.Outcome == IndexOutcome.Built);
            Assert.Contains(reports, r => r.Column == "s" && r.Kind == IndexKinds.PostingsBlocks && r.Outcome == IndexOutcome.Built);
            Assert.Contains(reports, r => r.Column == "f" && r.Kind == IndexKinds.BloomSbbf && r.Outcome == IndexOutcome.Built);

            // Not a data byte moved: the old file is a prefix of the new one.
            byte[] after = await System.IO.File.ReadAllBytesAsync(later, ct);
            Assert.True(after.Length > before.Length);
            Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before));

            await using (VortexFile expected = await VortexFile.OpenAsync(once, ct))
            await using (VortexFile actual = await VortexFile.OpenAsync(later, ct))
            {
                Assert.Equal(await RowsOf(expected), await RowsOf(actual));
                await AssertSameAnswers(expected, actual);
                IndexDirectory? directory = await actual.ReadIndexDirectoryAsync(ct);
                Assert.Equal((ulong)before.Length, directory!.PreviousEof);
            }

            // An append after the fact continues the indexes it added.
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(later, cancellationToken: ct))
            {
                await FeedAsync(writer, Rows, Rows + 3_000);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile grown = await VortexFile.OpenAsync(later, ct);
            await using KeyCursor cursor = await grown.Keys("v").OpenAsync(ct);
            Assert.Equal(Rows + 3_000 - ((Rows + 3_000 + 16) / 17), cursor.EntryCount);
        }
        finally
        {
            System.IO.File.Delete(once);
            System.IO.File.Delete(later);
        }
    }

    [Fact]
    public async Task AFragmentIndexesAFileItLeavesAloneAndIsRefusedOnceTheFileChanges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string once = TempPath();
        string plain = TempPath();
        try
        {
            await WriteAsync(once, 0, Rows);
            await WriteAsync(plain, 0, Rows, WritePolicy.None);
            byte[] before = await System.IO.File.ReadAllBytesAsync(plain, ct);
            IndexFragment fragment;
            await using (VortexFile unindexed = await VortexFile.OpenAsync(plain, ct))
            {
                fragment = await VortexFileIndexer.BuildFragmentAsync(
                    unindexed, Policy, new RowRange(0, unindexed.RowCount),
                    options: new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 }, cancellationToken: ct);
            }
            Assert.True((await System.IO.File.ReadAllBytesAsync(plain, ct)).AsSpan().SequenceEqual(before));

            VortexOpenOptions withFragment = new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = [fragment.Bytes] } };
            await using (VortexFile expected = await VortexFile.OpenAsync(once, ct))
            await using (VortexFile actual = await VortexFile.OpenAsync(plain, withFragment, ct))
            {
                Assert.True(actual.HasIndexDirectory);
                await AssertSameAnswers(expected, actual);
                KeyPlan plan = await actual.Keys("v").ExplainAsync(ct);
                Assert.Equal(KeySourceKind.SortedRuns, plan.Source);
            }

            // Without the option, nothing is looked for.
            await using (VortexFile bare = await VortexFile.OpenAsync(plain, ct))
            {
                Assert.False(bare.HasIndexDirectory);
            }

            // The file changes under the fragment -- an append that writes no directory of its own,
            // so the fragment is still the one looked at: refused as stale, and the scan still answers.
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(
                plain, new VortexWriteOptions { WritePolicy = WritePolicy.None }, ct))
            {
                await FeedAsync(writer, Rows, Rows + 100);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile changed = await VortexFile.OpenAsync(plain, withFragment, ct);
            Assert.Null(await changed.ReadIndexDirectoryAsync(ct));
            Assert.Contains("stale", Assert.Single(changed.IndexFragmentRefusals), StringComparison.Ordinal);
            VortexExpr filter = Expr.Eq(Expr.Field("v"), Expr.Literal(FilterLiteral.From(417L)));
            Assert.Equal(
                await changed.ScanBuilder().Where(filter).WithIndexes(false).CountAsync(ct),
                await changed.ScanBuilder().Where(filter).CountAsync(ct));
        }
        finally
        {
            System.IO.File.Delete(once);
            System.IO.File.Delete(plain);
        }
    }

    // ------------------------------------------------------------------------------ comparisons

    private static async Task<List<string>> RowsOf(VortexFile file)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    private static async Task AssertSameZones(VortexFile expected, VortexFile actual)
    {
        foreach (string name in Names)
        {
            ZoneColumn? a = await ZonesOf(expected, name);
            ZoneColumn? b = await ZonesOf(actual, name);
            Assert.Equal(a is null, b is null);
            if (a is null)
            {
                continue;
            }

            Assert.Equal(a.ZoneLength, b!.ZoneLength);
            ZoneRange zones = a.Zones(new RowRange(0, Rows));
            Assert.Equal(zones, b.Zones(new RowRange(0, Rows)));
            for (int z = zones.Start; z < zones.End; z++)
            {
                ZoneBounds x = a.Bounds(z);
                ZoneBounds y = b.Bounds(z);
                Assert.True(
                    x.HasMin == y.HasMin && x.HasMax == y.HasMax && x.NullCount == y.NullCount
                    && (!x.HasMin || x.Min.Equals(y.Min)) && (!x.HasMax || x.Max.Equals(y.Max)),
                    $"{name} zone {z}: {Describe(x)} against {Describe(y)}");
            }
        }
    }

    private static string Describe(ZoneBounds b) =>
        $"[{(b.HasMin ? b.Min.ToString() : "-")}, {(b.HasMax ? b.Max.ToString() : "-")}] nulls {b.NullCount}";

    private static async Task<ZoneColumn?> ZonesOf(VortexFile file, string name) =>
        (await ZonePruningPlan.PlanAsync(file, file.LayoutTree, Expr.IsNotNull(Expr.Field(name)), CancellationToken.None))
            .Zones?.Column(name);

    private static void AssertSameStatistics(VortexFile expected, VortexFile actual)
    {
        Assert.True(expected.HasFileStatistics);
        Assert.True(actual.HasFileStatistics);
        for (int field = 0; field < Names.Length; field++)
        {
            FieldStatistics a = expected.FileStatistics.GetField(field);
            FieldStatistics b = actual.FileStatistics.GetField(field);
            string name = Names[field];
            Assert.True(a.HasMin == b.HasMin && a.HasMax == b.HasMax, $"{name}: bounds present differ");
            if (a.HasMin)
            {
                Assert.Equal(a.Min.ToString(), b.Min.ToString());
                Assert.Equal(a.Max.ToString(), b.Max.ToString());
            }

            Assert.Equal(a.TryGetStoredNullCount(out ulong na) ? na : ulong.MaxValue, b.TryGetStoredNullCount(out ulong nb) ? nb : ulong.MaxValue);
            bool hasA = a.TryGetIsSorted(out bool sa);
            bool hasB = b.TryGetIsSorted(out bool sb);
            Assert.True(!hasB || (hasA && sa == sb), $"{name}: is_sorted {hasB}/{sb} against {hasA}/{sa}");
            bool hasSa = a.TryGetIsStrictSorted(out bool ta);
            bool hasSb = b.TryGetIsStrictSorted(out bool tb);
            Assert.True(!hasSb || (hasSa && ta == tb), $"{name}: is_strict_sorted {hasSb}/{tb} against {hasSa}/{ta}");
        }

        // The id column is what an append must keep stating: sorted across every seam.
        Assert.True(actual.FileStatistics.GetField(0).TryGetIsSorted(out bool sorted) && sorted);
        Assert.True(actual.FileStatistics.GetField(0).TryGetIsStrictSorted(out bool strict) && strict);
    }

    private static async Task AssertSameAnswers(VortexFile expected, VortexFile actual)
    {
        VortexExpr[] filters =
        [
            Expr.Eq(Expr.Field("v"), Expr.Literal(FilterLiteral.From(417L))),
            Expr.Eq(Expr.Field("s"), Expr.Literal(FilterLiteral.From("s42"))),
            Expr.Eq(Expr.Field("f"), Expr.Literal(FilterLiteral.From(12.5))),
            Expr.Eq(Expr.Field("f"), Expr.Literal(FilterLiteral.From(99.75))),
            Expr.And(
                Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(7_000L))),
                Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(9_000L)))),
            Expr.In(Expr.Field("v"), [FilterLiteral.From(1L), FilterLiteral.From(999L)]),
        ];

        foreach (VortexExpr filter in filters)
        {
            long count = await expected.ScanBuilder().Where(filter).WithIndexes(false).CountAsync();
            Assert.Equal(count, await actual.ScanBuilder().Where(filter).CountAsync());
            Assert.Equal(count, await actual.ScanBuilder().Where(filter).WithTiers(TerminalTiers.Decode).CountAsync());
            if (count > 0)
            {
                Assert.True(await actual.MayMatchAsync(filter), "a matching file said it cannot match");
            }
            List<string> a = await FilteredRows(expected, filter);
            List<string> b = await FilteredRows(actual, filter);
            Assert.Equal(a, b);
        }

        // The runs of every piece walk as one index.
        await using KeyCursor x = await expected.Keys("v").OpenAsync();
        await using KeyCursor y = await actual.Keys("v").OpenAsync();
        Assert.Equal(x.EntryCount, y.EntryCount);
        bool left = await x.SeekFirstAsync();
        bool right = await y.SeekFirstAsync();
        while (left && right)
        {
            Assert.Equal(x.Row, y.Row);
            Assert.Equal(x.Key, y.Key);
            left = await x.NextAsync();
            right = await y.NextAsync();
        }

        Assert.Equal(left, right);

        // The postings of every piece give the same distinct keys.
        await using KeyCursor p = await actual.Keys("s").Distinct().OpenAsync();
        HashSet<string> keys = [];
        for (bool ok = await p.SeekFirstAsync(); ok; ok = await p.NextAsync())
        {
            Assert.True(keys.Add(Encoding.UTF8.GetString(p.KeyBytes)));
        }

        Assert.Equal(211, keys.Count);
    }

    private static async Task<List<string>> FilteredRows(VortexFile file, VortexExpr filter)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).ExecuteAsync())
        {
            Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    // An append reads the zones of every column it keeps through one filter over all of them, which
    // must not nest a level per column: a filter nests no deeper than the evaluator's depth.
    [Fact]
    public async Task AFileWithMoreColumnsThanAFilterNestsLevelsIsAppendedTo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int Columns = FilterEvaluator.MaxDepth * 2;
        VortexField[] fields = new VortexField[Columns];
        for (int c = 0; c < Columns; c++)
        {
            fields[c] = new VortexField("c" + c.ToString(CultureInfo.InvariantCulture), VortexType.Int64);
        }

        VortexSchema schema = VortexSchema.Create(fields);
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = Block };
        long[] values = new long[3 * Block];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i;
        }

        string path = TempPath();
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, schema, options))
            {
                await FeedColumnsAsync(writer, Columns, values, ct);
                await writer.CompleteAsync(ct);
            }

            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options, ct))
            {
                await FeedColumnsAsync(writer, Columns, values, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(2L * values.Length, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task FeedColumnsAsync(VortexFileWriter writer, int columns, long[] values, CancellationToken ct)
    {
        ColumnsBuilder builder = writer.Builder();
        for (int c = 0; c < columns; c++)
        {
            builder.Column<long>(c).Append(values);
        }

        await writer.WriteAsync(builder, ct);
    }

    // ------------------------------------------------------------------------------ the files

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-append-{Guid.NewGuid():N}.vortex");

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        Names,
        [
            Types.Primitive(PType.I64, Nullability.NonNullable),
            Types.Primitive(PType.I32, Nullability.Nullable),
            Types.Utf8(Nullability.NonNullable),
            Types.Primitive(PType.F64, Nullability.NonNullable),
        ],
        Nullability.NonNullable);

    private static async Task<WriteReport> WriteAsync(string path, int start, int end, WritePolicy? policy = null)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = 1L << 14,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = policy ?? Policy,
        };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, options);
        await FeedAsync(writer, start, end);
        return await writer.CompleteAsync();
    }

    /// <summary>Rows <c>[start, end)</c> in ragged batches, so pieces and blocks never line up by accident.</summary>
    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        int row = start;
        int size = 700;
        while (row < end)
        {
            int count = Math.Min(size, end - row);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                int root = arena.AddStruct(
                    Schema,
                    count,
                    Validity.NonNullable,
                    [Longs(arena, row, count), Ints(arena, row, count), Strings(arena, row, count), Doubles(arena, row, count)]);
                using RecordBatch batch = new RecordBatch(arena, root, row);
                await writer.WriteAsync(batch);
            }
            finally
            {
                arena.Reset();
            }

            row += count;
            size = size == 700 ? 1_531 : 700;
        }
    }

    private static int Longs(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = Id(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Doubles(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = F(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(3), count, Validity.NonNullable, PType.F64, buffer);
    }

    private static int Ints(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 4, 4, out Span<byte> bytes);
        Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
        VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < count; i++)
        {
            int? v = V(start + i);
            values[i] = v ?? 0;
            if (v is not null)
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int mask = arena.AddBool(Types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
        return arena.AddPrimitive(Schema.GetField(1), count, Validity.Bitmap(mask), PType.I32, buffer);
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

        return arena.AddVarBinView(Schema.GetField(2), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
