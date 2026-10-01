using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>A row whose clustering key may be null, which no key source holds.</summary>
[VortexRecord]
public partial record struct NullableKeyRow(long? Key, int Payload, string Tag);

/// <summary>
/// Rows a delete marks in the objects that hold them, instead of rewriting those objects: every read
/// goes around them as if the objects had been rewritten. The oracle is a second dataset taking the
/// same changes by rewrites, whose objects keep their rows in the same order: every answer, rows by
/// position and in key order included, is the same on both.
/// </summary>
public sealed class DatasetDeletionVectorTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public async Task MarkedRowsReadAsARewriteWouldLeaveThem(bool clustered, bool compacted, bool outOfLine)
    {
        // Out of line, every vector lies in the commit object that wrote it and each read fetches it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore markedStore = new MemoryObjectStore();
        await using MemoryObjectStore rewrittenStore = new MemoryObjectStore();
        await using VortexDataset marked = await CreateAsync(markedStore, Options(clustered, marking: true) with { InlineVectorBytes = outOfLine ? 1 : 256 }, ct);
        await using VortexDataset rewritten = await CreateAsync(rewrittenStore, Options(clustered, marking: false), ct);
        if (compacted)
        {
            await DrainAsync(marked, ct);
            await DrainAsync(rewritten, ct);
        }

        // Scattered rows, a run of keys, and every row of one city under a bound: marks of every shape.
        Func<Probe<ChangeRow>, Predicate>[] deletes =
        [
            r => r.Measure > 90.0,
            r => r.Key >= 1_040 & r.Key < 1_090,
            r => r.City == "Nice" & r.Key < 3_000,
        ];
        foreach (Func<Probe<ChangeRow>, Predicate> delete in deletes)
        {
            RowChangeResult byMarks = await marked.DeleteAsync(delete, ct);
            RowChangeResult byRewrites = await rewritten.DeleteAsync(delete, ct);
            Assert.Equal(byRewrites.Rows, byMarks.Rows);
            Assert.Equal(0, byMarks.ObjectsOut);
            Assert.True(byMarks.ObjectsMarked > 0, "the rows are marked, not rewritten");
            Assert.Equal(0, byRewrites.ObjectsMarked);
        }

        Assert.True((await marked.ObjectsAsync(ct).ToListAsync(ct)).Exists(held => held.DeletedRows > 0));
        Assert.All(
            (await marked.ObjectsAsync(ct).ToListAsync(ct)).Where(held => held.DeletedRows > 0),
            held => Assert.Equal(outOfLine || held.Entry!.VectorBytes > 256, held.Entry!.VectorAt.Exists));
        await AgreeAsync(marked, rewritten, clustered, ct);
        Assert.True((await marked.VerifyAsync(cancellationToken: ct)).Holds);

        // A handle that never read the vectors reads them where they lie.
        await using (VortexDataset cold = await VortexDataset.OpenAsync(markedStore, Options(clustered, marking: true), ct))
        {
            await AgreeAsync(cold, rewritten, clustered, ct);
        }

        // An update marks the rows it changes where they were, and appends them changed.
        RowChangeResult update = await marked.UpdateAsync<ChangeRow>(r => r.Key >= 2_000 & r.Key < 2_150, row => row with { Measure = -1.0 }, ct);
        RowChangeResult updateByRewrite = await rewritten.UpdateAsync<ChangeRow>(r => r.Key >= 2_000 & r.Key < 2_150, row => row with { Measure = -1.0 }, ct);
        Assert.Equal(updateByRewrite.Rows, update.Rows);
        Assert.True(update.ObjectsMarked > 0);
        await AgreeAsync(marked, rewritten, clustered, ct);

        // Compaction writes the live rows only, and the marks end with the objects that held them.
        await DrainAsync(marked, ct, new CompactionOptions { LevelZeroCeiling = 0, TargetBytesAtLevelOne = 1 << 20 });
        await DrainAsync(rewritten, ct, new CompactionOptions { LevelZeroCeiling = 0, TargetBytesAtLevelOne = 1 << 20 });
        Assert.All(await marked.ObjectsAsync(ct).ToListAsync(ct), held => Assert.Equal(0, held.DeletedRows));
        Assert.Equal(Sorted(await RowsAsync(rewritten, ct)), Sorted(await RowsAsync(marked, ct)));
        Assert.True((await marked.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task ACursorOnAnotherColumnLeavesMarkedRowsOutOfItsKeysAndRanks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options(clustered: true, marking: true), ct);

        // Every Nice row goes, and some Lyon rows: a key none of whose rows is left, and a key some are.
        RowChangeResult deleted = await dataset.DeleteAsync<ChangeRow>(r => r.City == "Nice" | (r.City == "Lyon" & r.Key < 1_500), ct);
        Assert.True(deleted.ObjectsMarked > 0);
        List<ChangeRow> model = await RowsAsync(dataset, ct);
        Assert.DoesNotContain(model, row => row.City == "Nice");

        await using KeyCursor<string> distinct = await dataset.Scan<ChangeRow>().Keys(r => r.City).Distinct().OpenAsync(ct);
        List<string> cities = [];
        for (bool ok = await distinct.SeekFirstAsync(ct); ok; ok = await distinct.NextAsync(ct))
        {
            cities.Add(distinct.Key);
        }

        Assert.Equal(model.Select(row => row.City).Distinct().Order(StringComparer.Ordinal), cities);

        await using KeyCursor<string> cursor = await dataset.Scan<ChangeRow>().Keys(r => r.City).OpenAsync(ct);
        Assert.Equal(model.Count(row => string.CompareOrdinal(row.City, "Lyon") < 0), await cursor.RankAsync("Lyon", ct));
        Assert.True(await cursor.SeekAsync("Lyon", SeekOp.Exact, ct));
        Assert.Equal(model.Count(row => row.City == "Lyon"), await cursor.KeyCountAsync(ct));
        Assert.False(await cursor.SeekAsync("Nice", SeekOp.Exact, ct));

        List<string> walked = [];
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
        {
            walked.Add(cursor.Key);
        }

        Assert.Equal(model.Select(row => row.City).Order(StringComparer.Ordinal), walked);
        for (long rank = 0; rank < model.Count; rank += 97)
        {
            Assert.True(await cursor.SeekRankAsync(rank, ct));
            Assert.Equal(walked[(int)rank], cursor.Key);
        }
    }

    [Fact]
    public async Task NullKeysComeLastAroundMarksInBothDirections()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore markedStore = new MemoryObjectStore();
        await using MemoryObjectStore rewrittenStore = new MemoryObjectStore();
        VortexDataset marked = await NullableAsync(markedStore, marking: true, ct);
        VortexDataset rewritten = await NullableAsync(rewrittenStore, marking: false, ct);
        await using (marked)
        await using (rewritten)
        {
            // Some rows of each kind go: keyed ones and null ones, so the tail loses rows too.
            RowChangeResult byMarks = await marked.DeleteAsync<NullableKeyRow>(r => r.Payload < 60 | (r.Payload >= 200 & r.Payload < 230) | r.Tag == "Nice", ct);
            RowChangeResult byRewrites = await rewritten.DeleteAsync<NullableKeyRow>(r => r.Payload < 60 | (r.Payload >= 200 & r.Payload < 230) | r.Tag == "Nice", ct);
            Assert.Equal(byRewrites.Rows, byMarks.Rows);
            Assert.True(byMarks.ObjectsMarked > 0);

            // Rows of one key -- null ones above all -- come in the order of the objects holding them,
            // which a rewrite changes: the keys come in order, and each key's rows are the same rows.
            List<NullableKeyRow> live = await marked.Scan<NullableKeyRow>().ToRecordsAsync(ct).ToListAsync(ct);
            Assert.Equal(
                live.Select(row => row.Payload).Order(),
                (await rewritten.Scan<NullableKeyRow>().ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Payload).Order());
            foreach (bool descending in (bool[])[false, true])
            {
                foreach (bool late in (bool[])[false, true])
                {
                    List<NullableKeyRow> kept = late ? live.FindAll(row => row.Payload >= 100) : live;
                    List<NullableKeyRow> expected = descending
                        ? [.. kept.Where(row => row.Key is not null).OrderByDescending(row => row.Key), .. kept.Where(row => row.Key is null)]
                        : [.. kept.Where(row => row.Key is not null).OrderBy(row => row.Key), .. kept.Where(row => row.Key is null)];
                    foreach (VortexDataset dataset in (VortexDataset[])[marked, rewritten])
                    {
                        Scan<NullableKeyRow> scan = late ? dataset.Scan<NullableKeyRow>().Where(r => r.Payload >= 100) : dataset.Scan<NullableKeyRow>();
                        List<NullableKeyRow> ordered = await scan.OrderBy(r => r.Key, descending).ToRecordsAsync(ct).ToListAsync(ct);
                        Assert.Equal(expected.Select(row => row.Key), ordered.Select(row => row.Key));
                        Assert.Equal(
                            expected.GroupBy(row => row.Key).Select(group => (group.Key, string.Join(',', group.Select(row => row.Payload).Order()))),
                            ordered.GroupBy(row => row.Key).Select(group => (group.Key, string.Join(',', group.Select(row => row.Payload).Order()))));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task ACompactionThatReadAnObjectBeforeRowsWereMarkedInItIsAbandoned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(clustered: true, marking: true);
        await using VortexDataset compactor = await CreateAsync(store, options, ct);
        await using VortexDataset deleter = await VortexDataset.OpenAsync(store, options, ct);

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 };
        CompactionJob job = (await compactor.PlanCompactionAsync(compaction, ct)).Job!;

        // The delete lands first and marks rows in objects the compaction has already chosen: the
        // objects stay under their keys, so only the rows they name tell the compaction it read another.
        RowChangeResult deleted = await deleter.DeleteAsync<ChangeRow>(r => r.Key >= 1_000 & r.Key < 1_100, ct);
        Assert.True(deleted.ObjectsMarked > 0);

        CompactionResult result = await DatasetCompactor.RunAsync(compactor, job, ct);
        Assert.Equal(OperationOutcome.Abandoned, result.Outcome);

        await compactor.RefreshAsync(ct);
        Assert.DoesNotContain(await RowsAsync(compactor, ct), row => row.Key >= 1_000 && row.Key < 1_100);
        Assert.NotNull(await compactor.CompactAsync(compaction, ct));
        Assert.DoesNotContain(await RowsAsync(compactor, ct), row => row.Key >= 1_000 && row.Key < 1_100);
    }

    [Fact]
    public async Task MarksPastTheirShareOfAnObjectRewriteIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options(clustered: true, marking: true) with { MarkedShare = 8 }, ct);

        // Ten keys of each 200-row object: under an eighth, so marked.
        RowChangeResult first = await dataset.DeleteAsync(EachObject(0, 10), ct);
        Assert.Equal(5, first.ObjectsMarked);
        Assert.Equal(0, first.ObjectsIn);

        // Twenty more of each: thirty of 200 is past an eighth, so each object is rewritten without them.
        RowChangeResult second = await dataset.DeleteAsync(EachObject(10, 30), ct);
        Assert.Equal(0, second.ObjectsMarked);
        Assert.Equal(5, second.ObjectsIn);
        Assert.Equal(5, second.ObjectsOut);
        Assert.All(await dataset.ObjectsAsync(ct).ToListAsync(ct), held => Assert.Equal(0, held.DeletedRows));
        Assert.Equal(5 * 170, dataset.RowCount);
        Assert.DoesNotContain(await RowsAsync(dataset, ct), row => row.Key % 1_000 < 30);
    }

    [Fact]
    public async Task AnObjectMarkedWholeIsRemovedAndAnExtremeOfMarkedRowsIsNotAnswered()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options(clustered: true, marking: true), ct);

        // The largest key of every object goes, then the smallest measure of the whole dataset.
        long maxKey = (await RowsAsync(dataset, ct)).Max(row => row.Key);
        Assert.True((await dataset.DeleteAsync<ChangeRow>(r => r.Key == maxKey, ct)).ObjectsMarked == 1);
        Assert.Equal(maxKey - 1, await dataset.Scan<ChangeRow>().MaxAsync(r => r.Key, ct));
        Assert.Equal(maxKey - 1, (await dataset.ScanBuilder().MaxAsync("Key", ct)).SignedValue);

        double minMeasure = (await RowsAsync(dataset, ct)).Where(row => row.Measure is not null).Min(row => row.Measure!.Value);
        await dataset.DeleteAsync<ChangeRow>(r => r.Measure == minMeasure, ct);
        List<ChangeRow> rows = await RowsAsync(dataset, ct);
        Assert.Equal(rows.Where(row => row.Measure is not null).Min(row => row.Measure!.Value), (await dataset.ScanBuilder().MinAsync("Measure", ct)).FloatValue);
        Assert.False(await dataset.Scan<ChangeRow>().Where(r => r.Measure == minMeasure).AnyAsync(ct));
        Assert.Equal(0, await dataset.Scan<ChangeRow>().Where(r => r.Measure == minMeasure).CountAsync(ct));

        // Every row of the object holding keys 3_000 up goes: it is removed, not marked.
        RowChangeResult whole = await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 3_000 & r.Key < 4_000, ct);
        Assert.Equal(1, whole.ObjectsIn);
        Assert.Equal(0, whole.ObjectsOut);
        Assert.Equal(0, whole.ObjectsMarked);
    }

    [Fact]
    public async Task AnObjectSortedOnTheKeyAnswersItsExtremesFromItsLiveEnds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore markedStore = new MemoryObjectStore();
        await using MemoryObjectStore rewrittenStore = new MemoryObjectStore();
        await using VortexDataset marked = await CreateAsync(markedStore, Options(clustered: true, marking: true), ct);
        await using VortexDataset rewritten = await CreateAsync(rewrittenStore, Options(clustered: true, marking: false), ct);
        await DrainAsync(marked, ct);
        await DrainAsync(rewritten, ct);

        // A row inside, then each end, then a run at each end: the extremes follow the live ends.
        Func<Probe<ChangeRow>, Predicate>[] deletes =
        [
            r => r.Key == 2_100,
            r => r.Key == 0 | r.Key == 4_199,
            r => r.Key < 40 | r.Key >= 4_150,
        ];
        foreach (Func<Probe<ChangeRow>, Predicate> delete in deletes)
        {
            Assert.True((await marked.DeleteAsync(delete, ct)).ObjectsMarked > 0);
            await rewritten.DeleteAsync(delete, ct);
            Assert.Equal(await rewritten.ScanBuilder().MinAsync("Key", ct), await marked.ScanBuilder().MinAsync("Key", ct));
            Assert.Equal(await rewritten.ScanBuilder().MaxAsync("Key", ct), await marked.ScanBuilder().MaxAsync("Key", ct));
            Assert.Equal(await rewritten.Scan<ChangeRow>().MaxAsync(r => r.Key, ct), await marked.Scan<ChangeRow>().MaxAsync(r => r.Key, ct));
        }

        Assert.Equal(40, (await marked.ScanBuilder().MinAsync("Key", ct)).SignedValue);
        Assert.Equal(4_149, (await marked.ScanBuilder().MaxAsync("Key", ct)).SignedValue);
    }

    [Fact]
    public async Task ARangeTooLongForTheVectorsWorstCaseIsMarkedAsTheFewRunsItIs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore markedStore = new MemoryObjectStore();
        await using MemoryObjectStore rewrittenStore = new MemoryObjectStore();
        await using VortexDataset marked = await CreateAsync(markedStore, Options(clustered: true, marking: true), ct);
        await using VortexDataset rewritten = await CreateAsync(rewrittenStore, Options(clustered: true, marking: false), ct);
        await DrainAsync(marked, ct);
        await DrainAsync(rewritten, ct);

        // 450 rows in a row: at a run a row their vector would pass a kilobyte, and as the one run
        // they are it takes a few bytes.
        RowChangeResult deleted = await marked.DeleteAsync<ChangeRow>(r => r.Key < 2_050, ct);
        Assert.Equal((450L, 1L, 0L), (deleted.Rows, deleted.ObjectsMarked, deleted.ObjectsOut));
        await rewritten.DeleteAsync<ChangeRow>(r => r.Key < 2_050, ct);

        // An update of 350 rows is marked the same way, and its rows are written once, changed: the
        // pass that finds their places hands them to no one, and the one that marks them does.
        RowChangeResult updated = await marked.UpdateAsync<ChangeRow>(r => r.Key >= 3_000 & r.Key < 4_150, row => row with { Measure = -2.0 }, ct);
        Assert.Equal((350L, 1L), (updated.Rows, updated.ObjectsMarked));
        await rewritten.UpdateAsync<ChangeRow>(r => r.Key >= 3_000 & r.Key < 4_150, row => row with { Measure = -2.0 }, ct);

        await AgreeAsync(marked, rewritten, clustered: true, ct);
        Assert.Equal(350, await marked.Scan<ChangeRow>().Where(r => r.Measure == -2.0).CountAsync(ct));
        Assert.True((await marked.VerifyAsync(cancellationToken: ct)).Holds);
    }

    /// <summary>
    /// An object whose marks reach half a delete's bounds is rewritten by compaction, alone and in its
    /// level, before a delete has to: by the share of its rows marked, which one range takes, or by the
    /// bytes of its vector, which scattered rows take.
    /// </summary>
    [Theory]
    [InlineData("share")]
    [InlineData("vector")]
    public async Task AnObjectWhoseMarksAreDueIsRewrittenAloneInItsLevel(string due)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore markedStore = new MemoryObjectStore();
        await using MemoryObjectStore rewrittenStore = new MemoryObjectStore();
        await using VortexDataset marked = await CreateAsync(markedStore, Options(clustered: true, marking: true), ct);
        await using VortexDataset rewritten = await CreateAsync(rewrittenStore, Options(clustered: true, marking: false), ct);
        await DrainAsync(marked, ct);
        await DrainAsync(rewritten, ct);
        DataObject before = Assert.Single(await marked.ObjectsAsync(ct).ToListAsync(ct));

        // Half the rows in one run; or every other row of three hundred, in two deletes, each of which
        // a delete still marks, whose vector passes half a kilobyte.
        List<VortexExpr> deletes = due == "share"
            ? [Expr.Lt(Expr.Field("Key"), Expr.Literal(FilterLiteral.From(2_100L)))]
            : [Keys(0, 200), Keys(200, 300)];
        foreach (VortexExpr delete in deletes)
        {
            Assert.Equal(1, (await marked.DeleteAsync(delete, ct)).ObjectsMarked);
            await rewritten.DeleteAsync(delete, ct);
        }

        DataObject held = Assert.Single(await marked.ObjectsAsync(ct).ToListAsync(ct));
        Assert.True(due == "share" ? held.DeletedRows * 2 >= held.Rows + held.DeletedRows : held.Entry!.VectorBytes * 2 >= 1 << 10);

        CompactionPlan plan = await marked.PlanCompactionAsync(null, ct);
        CompactionJob job = Assert.IsType<CompactionJob>(plan.Job);
        Assert.Equal((CompactionTrigger.Marks, before.Level, before.Level), (job.Trigger, job.FromLevel, job.ToLevel));
        Assert.Equal([held.Key], job.Objects);

        CompactionResult purged = Assert.IsType<CompactionResult>(await marked.CompactAsync(null, ct));
        Assert.Equal((OperationOutcome.Applied, CompactionTrigger.Marks, 1L, 1L, held.Rows), (purged.Outcome, purged.Trigger, purged.ObjectsIn, purged.ObjectsOut, purged.Rows));
        DataObject after = Assert.Single(await marked.ObjectsAsync(ct).ToListAsync(ct));
        Assert.Equal((before.Level, 0L, held.Rows), (after.Level, after.DeletedRows, after.Rows));
        Assert.Null((await marked.PlanCompactionAsync(null, ct)).Job);

        await AgreeAsync(marked, rewritten, clustered: true, ct);
        Assert.True((await marked.VerifyAsync(cancellationToken: ct)).Holds);
    }

    /// <summary>
    /// A leaf whose objects carry marks grows with every mark: carried by every header, it would cost
    /// each commit what only those that change its level have to pay. It rides in the commit that
    /// wrote it, where the read opening that commit holds it; a later header names it, says where its
    /// version's pages start, and a reader reads it in one request.
    /// </summary>
    [Fact]
    public async Task AMarkedLeafRidesOnlyInTheCommitsThatWriteIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, Options(clustered: true, marking: true), ct);
        await DrainAsync(dataset, ct);
        int level = Assert.Single(await dataset.ObjectsAsync(ct).ToListAsync(ct)).Level;

        // Unmarked, the leaf rides in the header of a commit that does not change its level.
        await AppendAsync(dataset, 9_000, ct);
        Assert.NotEmpty(LevelOf(await LatestAsync(store, dataset, ct), level).Inlined);

        Assert.Equal(1, (await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 1_040 & r.Key < 1_090, ct)).ObjectsMarked);
        ulong marking = dataset.Version;
        for (int append = 0; append < 2; append++)
        {
            // Twice: the second header learns where the pages start from the first, as every later one does.
            await AppendAsync(dataset, 9_100 + (append * 100), ct);
            CommitHeader header = await LatestAsync(store, dataset, ct);
            CommitLevel carried = LevelOf(header, level);
            Assert.Equal(marking, carried.Top.Version);
            Assert.Empty(carried.Inlined);
            Assert.Contains(header.Starts, start => start.Version == marking);
        }

        // The writer kept the leaf: its summaries prove a key absent, and a walk reads it, without a
        // request. A reader opening the version reads it in one, and proves as much once it has it.
        store.Reset();
        Assert.False(dataset.MayMatch<ChangeRow>(r => r.Key == 50_000));
        int objects = (await dataset.ObjectsAsync(ct).ToListAsync(ct)).Count;
        Assert.Equal(0, store.Requests);
        await using VortexDataset cold = await VortexDataset.OpenAsync(store, Options(clustered: true, marking: true), ct);
        store.Reset();
        Assert.Equal(objects, (await cold.ObjectsAsync(ct).ToListAsync(ct)).Count);
        Assert.Equal(1, store.Requests);
        Assert.False(cold.MayMatch<ChangeRow>(r => r.Key == 50_000));
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    /// <summary>The level a header records, by number.</summary>
    private static CommitLevel LevelOf(CommitHeader header, int level) => header.Levels.Single(recorded => recorded.Level == level);

    /// <summary>The header of the version the handle reads, as a reader opening it finds it.</summary>
    private static async Task<CommitHeader> LatestAsync(IObjectStore store, VortexDataset dataset, CancellationToken ct) =>
        (await CommitObject.OpenAsync(store, CommitKey.For(dataset.Version), ct)).Header;

    /// <summary>Appends ten rows from key <paramref name="from"/>, at level 0.</summary>
    private static async Task AppendAsync(VortexDataset dataset, long from, CancellationToken ct)
    {
        ChangeRow[] rows = [.. Enumerable.Range(0, 10).Select(i => new ChangeRow(from + i, 1.0, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        await dataset.AppendAsync(draft, ct);
    }

    /// <summary>The filter taking every other key of the dataset's sorted keys, from the <paramref name="from"/>-th pair to the <paramref name="to"/>-th.</summary>
    private static VortexExpr Keys(int from, int to)
    {
        List<FilterLiteral> keys = [];
        for (int i = from; i < to; i++)
        {
            keys.Add(FilterLiteral.From((i / 100 * 1_000L) + (2L * (i % 100))));
        }

        return Expr.In(Expr.Field("Key"), [.. keys]);
    }

    [Fact]
    public async Task MarksRunUpToTheirBoundAndTheDeleteThatWouldPassItRewrites()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options(clustered: true, marking: true) with { MarkedVectorBytes = 256 }, ct);
        await DrainAsync(dataset, ct);

        // One row at a time, two live rows apart, so that each is a run of its own.
        List<long> keys = [.. (await RowsAsync(dataset, ct)).Select(row => row.Key).Order()];
        long marked = 0;
        for (int i = 1; ; i += 3)
        {
            long key = keys[i];
            RowChangeResult deleted = await dataset.DeleteAsync<ChangeRow>(r => r.Key == key, ct);
            if (deleted.ObjectsMarked == 0)
            {
                Assert.Equal((1, 1), (deleted.ObjectsIn, deleted.ObjectsOut));
                break;
            }

            marked = (await dataset.ObjectsAsync(ct).ToListAsync(ct)).Single(held => held.DeletedRows > 0).Entry!.Deletions.EncodedBytes;
        }

        // The last marks fit the bound, and one run more, at two varints of the object's row
        // count, would not have: the bound counts the vector's own bytes, not a guess at them.
        Assert.InRange(marked, 256 - (2 * 2) + 1, 256);
        Assert.All(await dataset.ObjectsAsync(ct).ToListAsync(ct), held => Assert.Equal(0, held.DeletedRows));
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public void AVectorIsItsRunsAndItsQueriesAreArithmeticOnThem()
    {
        DeletionVector vector = DeletionVector.Of([3, 4, 5, 9, 20]).With([6, 7, 21, 40]);
        Assert.Equal(9, vector.Count);
        Assert.Equal(4, vector.Runs);
        Assert.Equal((3L, 8L), (vector.StartOf(0), vector.EndOf(0)));
        Assert.Equal((20L, 22L), (vector.StartOf(2), vector.EndOf(2)));

        bool[] deleted = new bool[50];
        foreach (long row in (long[])[3, 4, 5, 6, 7, 9, 20, 21, 40])
        {
            deleted[row] = true;
        }

        long live = 0;
        for (long row = 0; row < deleted.Length; row++)
        {
            Assert.Equal(deleted[row], vector.Contains(row));
            Assert.Equal(row - live, vector.DeletedBefore(row));
            Assert.Equal(live, vector.Logical(row));
            if (!deleted[row])
            {
                Assert.Equal(row, vector.Physical(live));
                live++;
            }
        }

        Assert.Equal(vector, DeletionVector.FromBytes(vector.ToBytes()));
        Assert.Equal(vector.ToBytes().Length, vector.EncodedBytes);
        Assert.Equal(DeletionVector.Of([3, 4, 5, 6, 7, 9, 20, 21, 40]).ToBytes(), vector.ToBytes());
        Assert.Throws<ArgumentException>(() => vector.With([5]));
        Assert.Throws<ArgumentException>(() => DeletionVector.Of([2, 1]));

        // A run that meets the one before it is two encodings of one vector, which the format refuses.
        Assert.Throws<CommitFormatException>(() => DeletionVector.FromBytes([2, 3, 0, 0, 1]));
        Assert.Throws<CommitFormatException>(() => DeletionVector.FromBytes([1, 3, 0, 7]));
    }

    [Fact]
    public void AnEntryCarriesItsMarksAfterEverythingElseAndLoosensItsSummaries()
    {
        ObjectSummaries summaries = ObjectSummaries.From([
            new Vorticity.Scanning.ColumnSummary("Key", FilterLiteral.From(0L), true, FilterLiteral.From(99L), true, IsExact: true, 0, true),
            new Vorticity.Scanning.ColumnSummary("Measure", FilterLiteral.From(1.0), true, FilterLiteral.From(9.0), true, IsExact: true, 5, true),
            new Vorticity.Scanning.ColumnSummary("Note", FilterLiteral.Null, false, FilterLiteral.Null, false, IsExact: false, 100, true),
        ]);
        ObjectEntry plain = new ObjectEntry("data/a.vortex", 7, 100, 4_000, 9, summaries);
        byte[] bytes = plain.ToBytes();
        Assert.Equal(plain, ObjectEntry.FromBytes(bytes));

        ObjectEntry marked = plain.WithDeletions(DeletionVector.Of([10, 11, 12, 50]));
        Assert.Equal(96, marked.Rows);
        Assert.Equal(100, marked.PhysicalRows);
        Assert.Equal(marked, ObjectEntry.FromBytes(marked.ToBytes()));

        // Bounds stay, exactness and positive null counts go, and a column that was all nulls says nothing.
        Assert.True(marked.Summaries.TryGet("Key", out Vorticity.Scanning.ColumnSummary key) && !key.IsExact && key.HasNullCount && key.NullCount == 0);
        Assert.True(marked.Summaries.TryGet("Measure", out Vorticity.Scanning.ColumnSummary measure) && measure.HasMin && measure.HasMax && !measure.HasNullCount);
        Assert.False(marked.Summaries.TryGet("Note", out _));

        // Everything before the marks is the entry it was, and the marks run to its end.
        ObjectEntry sameSummaries = plain with { Rows = 96, Summaries = marked.Summaries };
        Assert.Equal(sameSummaries.ToBytes(), marked.ToBytes().AsSpan(0, sameSummaries.ToBytes().Length).ToArray());

        // An entry read from a page counts its marks without decoding them, is written back as it
        // was read, and decodes them when they are asked for.
        ObjectEntry read = ObjectEntry.FromBytes(marked.ToBytes().AsMemory());
        Assert.Equal((96L, 4L, 100L), (read.Rows, read.DeletedRows, read.PhysicalRows));
        Assert.Equal(marked.ToBytes(), read.ToBytes());
        Assert.True(read.SameDeletions(ObjectEntry.FromBytes(marked.ToBytes())));
        Assert.Equal(marked.Deletions, read.Deletions);

        byte[] torn = marked.ToBytes();
        Assert.Throws<CommitFormatException>(() => ObjectEntry.FromBytes(torn.AsSpan(0, torn.Length - 1)));

        // A vector of rows the object does not hold is refused when it is decoded, which a read of
        // the object and a verify do.
        ObjectEntry beyond = ObjectEntry.FromBytes((plain with { Rows = 2 }).WithDeletions(DeletionVector.Of([50])).ToBytes());
        Assert.Equal(1, beyond.DeletedRows);
        Assert.Throws<CommitFormatException>(() => beyond.Deletions);
    }

    /// <summary>
    /// The two datasets hold the same rows and answer every question alike. Objects that tie on their
    /// smallest key are ordered by their uids, which a rewrite changes, so rows by position are checked
    /// against each dataset's own scan, and everything else across the two.
    /// </summary>
    private static async Task AgreeAsync(VortexDataset marked, VortexDataset rewritten, bool clustered, CancellationToken ct)
    {
        List<ChangeRow> expected = await RowsAsync(rewritten, ct);
        Assert.Equal(Sorted(expected), Sorted(await RowsAsync(marked, ct)));
        Assert.Equal(rewritten.RowCount, marked.RowCount);

        // Counts and existence under filters the entries cannot settle, a null compared included.
        foreach (Func<Probe<ChangeRow>, Predicate> filter in (Func<Probe<ChangeRow>, Predicate>[])
            [r => r.Measure > 50.0, r => r.City == "Lyon", r => r.Key >= 1_000 & r.Key < 2_500, r => r.Measure.IsNull, r => r.Measure > 1_000.0])
        {
            Assert.Equal(await rewritten.Scan<ChangeRow>().Where(filter).CountAsync(ct), await marked.Scan<ChangeRow>().Where(filter).CountAsync(ct));
            Assert.Equal(await rewritten.Scan<ChangeRow>().Where(filter).AnyAsync(ct), await marked.Scan<ChangeRow>().Where(filter).AnyAsync(ct));
            Assert.Equal(
                Sorted(await rewritten.Scan<ChangeRow>().Where(filter).ToRecordsAsync(ct).ToListAsync(ct)),
                Sorted(await marked.Scan<ChangeRow>().Where(filter).ToRecordsAsync(ct).ToListAsync(ct)));
        }

        foreach (string column in (string[])["Key", "Measure", "City"])
        {
            Assert.Equal(await rewritten.ScanBuilder().MinAsync(column, ct), await marked.ScanBuilder().MinAsync(column, ct));
            Assert.Equal(await rewritten.ScanBuilder().MaxAsync(column, ct), await marked.ScanBuilder().MaxAsync(column, ct));
        }

        // Aggregates, which read an object's blocks whole with the deleted rows deselected.
        Assert.Equal(await rewritten.Scan<ChangeRow>().SumAsync(r => r.Key, ct), await marked.Scan<ChangeRow>().SumAsync(r => r.Key, ct));
        Assert.Equal(
            await rewritten.Scan<ChangeRow>().Where(r => r.Measure > 50.0).SumAsync(r => r.Key, ct),
            await marked.Scan<ChangeRow>().Where(r => r.Measure > 50.0).SumAsync(r => r.Key, ct));
        Assert.Equal(await rewritten.Scan<ChangeRow>().CountDistinctAsync(r => r.City, ct), await marked.Scan<ChangeRow>().CountDistinctAsync(r => r.City, ct));
        Assert.Equal(await GroupsAsync(rewritten, ct), await GroupsAsync(marked, ct));

        foreach (VortexDataset dataset in (VortexDataset[])[marked, rewritten])
        {
            await PositionsAsync(dataset, ct);
        }

        if (!clustered)
        {
            return;
        }

        foreach (bool descending in (bool[])[false, true])
        {
            Assert.Equal(
                await rewritten.Scan<ChangeRow>().OrderBy(r => r.Key, descending).ToRecordsAsync(ct).ToListAsync(ct),
                await marked.Scan<ChangeRow>().OrderBy(r => r.Key, descending).ToRecordsAsync(ct).ToListAsync(ct));
            Assert.Equal(
                await rewritten.Scan<ChangeRow>().Where(r => r.City != "Paris").OrderBy(r => r.Key, descending).ToRecordsAsync(ct).ToListAsync(ct),
                await marked.Scan<ChangeRow>().Where(r => r.City != "Paris").OrderBy(r => r.Key, descending).ToRecordsAsync(ct).ToListAsync(ct));
        }

        // The key cursor: every entry up and down, its ranks, its seeks and its selections; each
        // entry's row holds its key in its own dataset.
        List<ChangeRow> scanned = await RowsAsync(marked, ct);
        await using KeyCursor<long> byMarks = await marked.Scan<ChangeRow>().Keys(r => r.Key).OpenAsync(ct);
        await using KeyCursor<long> byRewrites = await rewritten.Scan<ChangeRow>().Keys(r => r.Key).OpenAsync(ct);
        foreach (bool up in (bool[])[true, false])
        {
            List<(long Key, long Row)> walked = await WalkAsync(byMarks, up, ct);
            Assert.Equal((await WalkAsync(byRewrites, up, ct)).Select(entry => entry.Key), walked.Select(entry => entry.Key));
            Assert.All(walked, entry => Assert.Equal(entry.Key, scanned[(int)entry.Row].Key));
        }

        foreach (long key in (long[])[0, 1_040, 1_089, 2_001, 3_500, 4_199, 9_999])
        {
            Assert.Equal(await byRewrites.RankAsync(key, ct), await byMarks.RankAsync(key, ct));
            foreach (SeekOp op in (SeekOp[])[SeekOp.Exact, SeekOp.AtOrAfter, SeekOp.After, SeekOp.AtOrBefore, SeekOp.Before])
            {
                bool found = await byRewrites.SeekAsync(key, op, ct);
                Assert.Equal(found, await byMarks.SeekAsync(key, op, ct));
                if (found)
                {
                    Assert.Equal(byRewrites.Key, byMarks.Key);
                    Assert.Equal(byMarks.Key, scanned[(int)byMarks.Row].Key);
                }
            }
        }

        for (long rank = 0; rank < expected.Count; rank += 131)
        {
            Assert.True(await byMarks.SeekRankAsync(rank, ct));
            Assert.True(await byRewrites.SeekRankAsync(rank, ct));
            Assert.Equal(byRewrites.Key, byMarks.Key);
            Assert.Equal(await byRewrites.KeyCountAsync(ct), await byMarks.KeyCountAsync(ct));
        }
    }

    /// <summary>Each city's rows and the sum of their keys, by city.</summary>
    private static async Task<List<(string City, long Rows, long Keys)>> GroupsAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<(string City, long Rows, long Keys)> groups = [];
        await foreach ((string city, long rows, long keys) in dataset.Scan<ChangeRow>()
            .GroupBy(r => r.City)
            .AggAsync(g => (g.Key, g.Count(), g.Sum(r => r.Key)))
            .WithCancellation(ct))
        {
            groups.Add((city, rows, keys));
        }

        groups.Sort();
        return groups;
    }

    /// <summary>
    /// Rows by position are a dataset's own scan's rows there: a range, a set of indices, and the
    /// first row each batch of a whole scan says it starts at.
    /// </summary>
    private static async Task PositionsAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<ChangeRow> scanned = await RowsAsync(dataset, ct);
        for (long from = 0; from < scanned.Count; from += 173)
        {
            RowRange range = RowRange.FromLength(from, 211);
            Assert.Equal(
                scanned.Skip((int)from).Take(211),
                await dataset.Scan<ChangeRow>().Rows(range).ToRecordsAsync(ct).ToListAsync(ct));
        }

        long[] taken = [.. Enumerable.Range(0, 40).Select(i => (long)(i * 37 % Math.Max(scanned.Count, 1))).Distinct().Order()];
        Assert.Equal(
            taken.Select(row => scanned[(int)row]),
            await dataset.Scan<ChangeRow>().Rows(taken).ToRecordsAsync(ct).ToListAsync(ct));

        long next = 0;
        await foreach (RecordBatch batch in dataset.ScanBuilder().ExecuteAsync())
        {
            Assert.Equal(next, batch.StartRow);
            next += batch.RowCount;
        }

        Assert.Equal(scanned.Count, next);
    }

    private static async Task<List<(long Key, long Row)>> WalkAsync(KeyCursor<long> cursor, bool up, CancellationToken ct)
    {
        List<(long Key, long Row)> entries = [];
        for (bool ok = up ? await cursor.SeekFirstAsync(ct) : await cursor.SeekLastAsync(ct);
            ok;
            ok = up ? await cursor.NextAsync(ct) : await cursor.PrevAsync(ct))
        {
            entries.Add((cursor.Key, cursor.Row));
        }

        return entries;
    }

    private static DatasetOptions Options(bool clustered, bool marking) => new DatasetOptions
    {
        Seed = 0xDE1_E7ED,
        ClusteringKey = clustered ? ["Key"] : null,
        Write = new VortexWriteOptions
        {
            RowBlockSize = 64,
            DataBlockTargetBytes = 2 << 10,
            Indexes = IndexPolicy.Auto.SortedRuns("City", true),
        },
        MarkedObjectBytes = marking ? 0 : long.MaxValue,
        MarkedShare = 1,
    };

    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        foreach (int part in (int[])[3, 0, 4, 1, 2])
        {
            ChangeRow[] rows = new ChangeRow[200];
            for (int i = 0; i < rows.Length; i++)
            {
                // Keys of each object in an order of their own, so that an object of level 0 is not sorted.
                long key = (part * 1_000L) + ((i * 37) % rows.Length);
                rows[i] = new ChangeRow(key, key % 7 == 3 ? null : key % 100 + ((key / 100) % 3 * 0.25), Cities[(int)(key % Cities.Length)]);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        return dataset;
    }

    private static async Task<VortexDataset> NullableAsync(IObjectStore store, bool marking, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xDE1_E7ED,
            ClusteringKey = ["Key"],
            Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
            MarkedObjectBytes = marking ? 0 : long.MaxValue,
            MarkedShare = 1,
        };
        VortexDataset dataset = await VortexDataset.CreateAsync(store, NullableKeyRow.Schema, options, ct);
        for (int part = 0; part < 3; part++)
        {
            NullableKeyRow[] rows = new NullableKeyRow[150];
            for (int i = 0; i < rows.Length; i++)
            {
                int payload = (part * rows.Length) + i;
                long? key = payload % 4 == 1 ? null : (payload * 13) % 400;
                rows[i] = new NullableKeyRow(key, payload, Cities[payload % Cities.Length]);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<NullableKeyRow>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        return dataset;
    }

    /// <summary>The keys <c>[from, to)</c> past the first of every object's thousand.</summary>
    private static Func<Probe<ChangeRow>, Predicate> EachObject(int from, int to) => r =>
    {
        Predicate any = r.Key >= from & r.Key < to;
        for (int part = 1; part < 5; part++)
        {
            any |= r.Key >= (part * 1_000) + from & r.Key < (part * 1_000) + to;
        }

        return any;
    };

    private static async Task DrainAsync(VortexDataset dataset, CancellationToken ct, CompactionOptions? options = null)
    {
        options ??= new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1 << 20 };
        while (await dataset.CompactAsync(options, ct) is not null)
        {
        }
    }

    private static async Task<List<ChangeRow>> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct);

    private static List<ChangeRow> Sorted(IEnumerable<ChangeRow> rows) => [.. rows.OrderBy(row => row.Key).ThenBy(row => row.Measure)];
}
