// Rows a delete marks in an object whose counts answer: the delete counts them, by each column the object counts, and a
// question of counts, sums, means or distinct values subtracts them from the object's own counts.
//
// THE ORACLE IS THE ROWS: every question gives the same answer through the counts as through the rows, after a delete,
// after a second on the same objects, and after a repack moved the counts of the deleted rows; the counts answer the
// marked objects, and verify counts their deleted rows again.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetNegativesTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Keys, results and a filter: counts, sums, means and distinct values, which subtract, then extremes, which do not.
    private static readonly (string[] Keys, string[] Results, string? Filter)[] Questions =
    [
        (["Service"], ["Service", "count()"], null),
        (["Service"], ["Service", "count()", "sum(Size)", "avg(Latency)"], null),
        (["Status"], ["Status", "count()", "sum(Size)"], null),
        ([], ["count()", "sum(Size)", "avg(Latency)"], null),
        ([], ["count_distinct(Host)"], null),
        (["Host"], ["Host", "count()"], "Host in ('h-1-0', 'h-2-2')"),
        (["Service"], ["Service", "min(Latency)", "max(Size)"], null),
    ];

    [Fact]
    public async Task TheCountsOfAMarkedObjectLessThoseOfItsDeletedRowsAnswerAsItsRowsDo()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options());

        // A few rows of every object, as an erasure takes them: marked, not rewritten.
        RowChangeResult first = await dataset.DeleteAsync<Hit>(h => h.Size < 3_000, Ct);
        Assert.True(first.ObjectsMarked >= 2, $"{first.ObjectsMarked} objects marked, {first.ObjectsOut} rewritten");
        await AnswersAsItsRowsDoAsync(dataset);
        Assert.True(await MarkedAnsweredAsync(dataset) >= 2);

        // A second delete on the same objects adds its rows to theirs.
        RowChangeResult second = await dataset.DeleteAsync<Hit>(h => h.Size >= 3_000 & h.Size < 5_000, Ct);
        Assert.True(second.ObjectsMarked >= 2);
        await AnswersAsItsRowsDoAsync(dataset);
        Assert.True(await MarkedAnsweredAsync(dataset) >= 2);
        Assert.True((await dataset.VerifyAsync(cancellationToken: Ct)).Holds);

        // Opened afresh, the counts of the deleted rows are read from the commit object that holds them.
        await using VortexDataset fresh = await VortexDataset.OpenAsync(store, Options(), Ct);
        await AnswersAsItsRowsDoAsync(fresh);
    }

    /// <summary>
    /// Measures by service over marked objects read their counts and those of their deleted rows: a fraction of the bytes
    /// their rows take, which a question reading them as rows would.
    /// </summary>
    [Fact]
    public async Task AMarkedObjectsCountsReadAFractionOfItsRowsBytes()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using (VortexDataset dataset = await CreateAsync(memory, Options()))
        {
            Assert.True((await dataset.DeleteAsync<Hit>(h => h.Size < 3_000, Ct)).ObjectsMarked >= 2);
        }

        await using CountingObjectStore store = new CountingObjectStore(memory);
        long[] bytes = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options() with { Session = session }, Ct);
            store.Reset();
            await DatasetPlannerTests.AskAsync(dataset, ["Service"], ["Service", "count()", "sum(Size)", "avg(Latency)"], null, synopses);
            bytes[synopses ? 1 : 0] = store.BytesRead;
        }

        Console.WriteLine($"NEGATIVES measures by service over marked objects: {bytes[1]:N0} bytes through their counts, {bytes[0]:N0} through their rows");
        Assert.True(bytes[1] * 4 < bytes[0], $"{bytes[1]} bytes through the counts, {bytes[0]} through the rows");
    }

    /// <summary>
    /// Counts by service and status, which no column's counts answer, settle the blocks of a marked object that hold no
    /// marked row from their statistics, and read the others as rows: the rows' answer, in fewer bytes than the rows take.
    /// </summary>
    [Fact]
    public async Task AMarkedObjectsBlocksAnswerWhereNoDeleteTouchedThem()
    {
        string[] keys = ["Service", "Status"];
        string[] results = ["Service", "Status", "count()"];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using (VortexDataset dataset = await CreateAsync(memory, Options()))
        {
            Assert.True((await dataset.DeleteAsync<Hit>(h => h.Service == "svc-1" & h.Size < 5_000, Ct)).ObjectsMarked >= 1);
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, null, synopses: false);
            Assert.Equal(rows, await DatasetPlannerTests.AskAsync(dataset, keys, results, null, synopses: true));

            HashSet<string> marked = [.. (await EntriesAsync(dataset)).Where(e => e.HasDeletions).Select(e => e.Key)];
            ScanPlan plan = await Scan(dataset).GroupBy(h => (h.Service, h.Status)).Select(g => (g.Key.Service, g.Key.Status, g.Count())).ExplainAsync(Ct);
            Assert.Contains(plan.States, step => step.Read == "blocks" && marked.Contains(step.Object));
        }

        await using CountingObjectStore store = new CountingObjectStore(memory);
        long[] bytes = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options() with { Session = session }, Ct);
            store.Reset();
            await DatasetPlannerTests.AskAsync(dataset, keys, results, null, synopses);
            bytes[synopses ? 1 : 0] = store.BytesRead;
        }

        Console.WriteLine($"NEGATIVES counts by service and status over marked objects: {bytes[1]:N0} bytes through their blocks, {bytes[0]:N0} through their rows");
        Assert.True(bytes[1] * 2 < bytes[0], $"{bytes[1]} bytes through the blocks, {bytes[0]} through the rows");
    }

    [Fact]
    public async Task AQuestionOfExtremesReadsAMarkedObjectsRows()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options());
        Assert.True((await dataset.DeleteAsync<Hit>(h => h.Size < 3_000, Ct)).ObjectsMarked >= 2);

        ScanPlan plan = await Scan(dataset).GroupBy(h => h.Service).Select(g => (g.Key, g.Max(h => h.Size))).ExplainAsync(Ct);
        Assert.DoesNotContain(plan.States, step => step.Detail.Contains("less those of its deleted rows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCountsOfDeletedRowsKeepTheirCommitObjectUntilARepackMovesThem()
    {
        ManualClock clock = new ManualClock(Noon);
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        DatasetOptions options = Options() with { RetentionWindow = TimeSpan.FromHours(1), TimeProvider = clock };
        await using VortexDataset dataset = await CreateAsync(store, options);

        // The first delete writes the counts of its rows and the leaf; the second writes the leaf again, so that the first's
        // commit object holds nothing live but those counts.
        await dataset.DeleteAsync<Hit>(h => h.Size < 3_000, Ct);
        ulong first = dataset.Version;
        await dataset.DeleteAsync<Hit>(h => h.Size >= 999_000, Ct);
        PageReference[] negatives = [.. (await EntriesAsync(dataset)).Where(e => e.HasNegatives).Select(e => e.NegativesAt)];
        Assert.NotEmpty(negatives);
        Assert.All(negatives, at => Assert.Equal(dataset.Version, at.Version));

        // Each delete's counts lie in its own commit, merged with those before: a third, on one service, leaves the others'.
        await dataset.DeleteAsync<Hit>(h => h.Service == "svc-1" & h.Size >= 500_000 & h.Size < 502_000, Ct);
        ulong kept = (await EntriesAsync(dataset)).First(e => e.HasNegatives && e.NegativesAt.Version != dataset.Version).NegativesAt.Version;
        Assert.NotEqual(first, kept);

        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult vacuumed = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock, RepackBelow = 1.0 }, Ct);
        Assert.DoesNotContain(CommitKey.For(kept), vacuumed.Deleted);
        Assert.Contains(kept, vacuumed.Sparse);

        (ulong repacked, OperationOutcome outcome) = await dataset.RepackAsync([kept], Ct);
        Assert.Equal(OperationOutcome.Applied, outcome);
        Assert.DoesNotContain(await EntriesAsync(dataset), e => e.HasNegatives && e.NegativesAt.Version == kept);
        Assert.Contains(await EntriesAsync(dataset), e => e.HasNegatives && e.NegativesAt.Version == repacked);
        Assert.True((await dataset.VerifyAsync(cancellationToken: Ct)).Holds);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Contains(CommitKey.For(kept), (await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock }, Ct)).Deleted);
        await using VortexDataset fresh = await VortexDataset.OpenAsync(store, options, Ct);
        await AnswersAsItsRowsDoAsync(fresh);
        Assert.True(await MarkedAnsweredAsync(fresh) >= 2);
    }

    [Fact]
    public async Task CountsOfDeletedRowsTornInTheirCommitObjectAreNamedByVerify()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options());
        await dataset.DeleteAsync<Hit>(h => h.Size < 3_000, Ct);
        PageReference negatives = (await EntriesAsync(dataset)).First(e => e.HasNegatives).NegativesAt;

        // A byte of the counts flipped where they lie.
        string key = CommitKey.For(negatives.Version);
        long length = (await store.HeadAsync(key, Ct))!.Value.Length;
        byte[] bytes;
        using (ObjectRange range = await store.GetRangeAsync(key, 0, (int)length, Ct))
        {
            bytes = range.Bytes.ToArray();
        }

        CommitObject commit = CommitObject.Open(bytes, bytes.Length);
        bytes[(int)(commit.HeaderEnd + negatives.Offset)] ^= 0x01;
        await store.DeleteAsync([key], Ct);
        await store.PutIfAbsentAsync(key, bytes, Ct);

        await using VortexDataset cold = await VortexDataset.OpenAsync(store, Options(), Ct);
        await Assert.ThrowsAsync<TornCommitException>(() => DatasetPlannerTests.AskAsync(cold, ["Service"], ["Service", "count()"], null, synopses: true));
        DatasetVerification verified = await cold.VerifyAsync(cancellationToken: Ct);
        Assert.False(verified.Holds);
        Assert.Contains(verified.Problems, problem => problem.Contains(FormattableString.Invariant($"its counts of deleted rows in version {negatives.Version}"), StringComparison.Ordinal));
    }

    [Fact]
    public void AnEntryNamesTheCountsOfItsDeletedRowsByReference()
    {
        ObjectEntry marked = new ObjectEntry(CommitKey.ForData("0000000c"), 9, 200, 4_096, 3)
            .WithDeletions(DeletionVector.Of([.. Enumerable.Range(0, 60).Select(row => (long)row * 3)]));
        DeletedCounts counts = Counts(60);

        // Counts of other rows than the entry deletes are refused; counts no commit placed are not written.
        Assert.Throws<ArgumentException>(() => marked.WithNegatives(Counts(59)));
        ObjectEntry pending = marked.WithNegatives(counts);
        Assert.True(pending.NegativesPending);
        Assert.Throws<InvalidOperationException>(() => pending.ToBytes());

        byte[] encoded = pending.EncodedNegatives.ToArray();
        PageReference at = new PageReference(11, 200, encoded.Length, XxHash128.HashToUInt128(encoded));
        byte[] bytes = pending.WithNegativesAt(at).ToBytes();
        ObjectEntry read = ObjectEntry.FromBytes(bytes.AsMemory());
        Assert.Equal((at, false, true), (read.NegativesAt, read.NegativesPending, read.HasFields));
        Assert.True(ObjectEntry.NamesAny(bytes, [11]));
        Assert.False(ObjectEntry.NamesAny(bytes, [10]));
        Assert.True(read.NamesAny([11]));

        // A fragment attached keeps them; rows deleted again drop them, which count other rows.
        Assert.Equal(at, ObjectEntry.FromBytes(read.With(new PageReference(12, 0, 64, 5)).ToBytes().AsMemory()).NegativesAt);
        ObjectEntry again = read.WithDeletions(DeletionVector.Of([.. Enumerable.Range(0, 61).Select(row => (long)row * 3)]));
        Assert.False(again.HasNegatives);
        Assert.False(ObjectEntry.FromBytes(again.ToBytes().AsMemory()).HasNegatives);
    }

    /// <summary>Counts of <paramref name="rows"/> deleted rows of one column, all of one value, carrying no measure.</summary>
    private static DeletedCounts Counts(long rows)
    {
        byte[] column = new ColumnCounts([], [], [new CountGroup(CountedValue.Value, [1], 0, 1)], [rows]).ToBytes();
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(1);
            writer.WriteVarint(1);
            writer.WriteVarint(2);
            writer.WriteVarint((ulong)column.Length);
            writer.WriteRawBytes(column);
            return DeletedCounts.From(writer.WrittenSpan);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Asks each question through the counts and through the rows, which give the same answer.</summary>
    private static async Task AnswersAsItsRowsDoAsync(VortexDataset dataset)
    {
        foreach ((string[] keys, string[] results, string? filter) in Questions)
        {
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: false);
            List<string> counted = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: true);
            Assert.NotEmpty(rows);
            Assert.True(rows.SequenceEqual(counted), $"{string.Join(", ", results)} under {filter}: {string.Join(" | ", counted)} through the counts, {string.Join(" | ", rows)} through the rows");
        }
    }

    /// <summary>The marked objects whose counts, less those of their deleted rows, answer a question of measures by service.</summary>
    private static async Task<int> MarkedAnsweredAsync(VortexDataset dataset)
    {
        ScanPlan plan = await Scan(dataset).GroupBy(h => h.Service).Select(g => (g.Key, g.Count(), g.Sum(h => h.Size), g.Average(h => h.Latency))).ExplainAsync(Ct);
        return plan.States.Count(step => step.Read == "states" && step.Detail.Contains("less those of its deleted rows", StringComparison.Ordinal));
    }

    private static Scan<Hit> Scan(VortexDataset dataset) => dataset.Scan<Hit>().With(new ScanOptions { UseSynopses = true });

    private static async Task<List<ObjectEntry>> EntriesAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            entries.Add(held.Entry);
        }

        return entries;
    }

    /// <summary>Marks rows of objects of any size, and keeps a table of contents, sheets and counts for every object.</summary>
    private static DatasetOptions Options() => new DatasetOptions { ContentsObjectBytes = 1, SheetObjectBytes = 0, MarkedObjectBytes = 1 };

    /// <summary>
    /// Four services, each 20 000 hits over two hours from noon, from three hosts of its own, keyed by the service then the
    /// instant, appended in parts and compacted into objects of 256 KiB.
    /// </summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store, DatasetOptions options)
    {
        Random random = new Random(0xD7);
        List<Hit> rows = [];
        for (int service = 0; service < 4; service++)
        {
            for (int i = 0; i < 20_000; i++)
            {
                rows.Add(new Hit(
                    $"svc-{service}",
                    Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                    $"h-{service}-{random.Next(3)}",
                    (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                    random.Next(4_000) / 4.0,
                    random.Next(1_000_000)));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Hit.Schema, options with { ClusteringKey = [nameof(Hit.Service), nameof(Hit.At)], Clustering = new ClusteringOptions { ObjectBytes = 256 << 10 } }, Ct);
        for (int from = 0; from < rows.Count; from += 20_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hit>(rows.Skip(from).Take(20_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Hit(string Service, DateTimeOffset At, string Host, short Status, double Latency, long Size);
}
