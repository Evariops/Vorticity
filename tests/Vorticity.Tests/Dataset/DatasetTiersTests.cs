// The tiers (spec A, 6; A6): small appends merged a fan-out at a time by size class within each partition, so that no class
// holds k objects of a partition once the due merges are done; a partition calm past CalmAfter consolidated into level 1,
// disjoint by key; late rows into a consolidated partition, and ConsolidateAsync on demand; the writes a row costs.
//
// THE ORACLE IS THE PLAN, THE INVARIANTS AND THE CLASSES: the plan owes no merge past k - 1 objects a class (its lag); a full
// verification holds (one partition an object, sorted, level 1 of a partition disjoint); a row is written once by its
// append, once a class it climbs, once more consolidated; every row reads back.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTiersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two days of small appends a minute apart, merged four at a time, each due merge run after each append: the plan owes
    /// no merge past three objects a class of a day, and the layout's invariants hold; two hours later both days are calm and
    /// consolidated, level 0 empty; then late rows of the first day and a fresh day, merged, and ConsolidateAsync consolidates
    /// both at once; every row reads back, and a lookup returns LINQ's rows.
    /// </summary>
    [Fact]
    public async Task SmallAppendsMergeAFanOutAtATimeAndCalmPartitionsConsolidate()
    {
        const int Fanout = 4;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Random random = new Random(41);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 2 << 20,
                MergeFanout = Fanout,
                CalmAfter = TimeSpan.FromHours(1),
                LateShare = 8,
            },
            TimeProvider = clock,
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 48; a++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await AppendAsync(dataset, all, Rows(random, 1_500, all.Count, day: a % 2));
            await CompactAllAsync(dataset);
            CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: Ct);
            Assert.All(plan.Partitions, partition => Assert.True(partition.Lag == 0, $"day {partition.Partition} owes {partition.Lag} objects past k - 1 a class"));
        }

        await HoldsAsync(dataset, all);

        // An hour and more later both days are calm: their level 0 joins level 1.
        clock.Advance(TimeSpan.FromHours(2));
        await CompactAllAsync(dataset);
        await HoldsAsync(dataset, all);
        Assert.Equal(0, await LevelZeroAsync(dataset));

        // Late rows of the first day, then a fresh day: ConsolidateAsync takes both at once.
        clock.Advance(TimeSpan.FromMinutes(1));
        await AppendAsync(dataset, all, Rows(random, 9_000, all.Count, day: 0));
        await AppendAsync(dataset, all, Rows(random, 3_000, all.Count, day: 2));
        await CompactAllAsync(dataset);
        Assert.Equal(2, (await dataset.ConsolidateAsync(Ct)).Count);
        await HoldsAsync(dataset, all);
        Assert.Equal(0, await LevelZeroAsync(dataset));
        Assert.Equal(all.Count(r => r.Day == 0 && r.Service == "svc-5"), await dataset.Scan<Row>().Where(r => r.Day == 0 && r.Service == "svc-5").ToRecordsAsync(Ct).CountAsync(Ct));
    }

    /// <summary>
    /// The writes a row costs over its life (spec A, 6.3), at the scale of the specification's case (appends of a sixteenth
    /// of the top, merged eight at a time, a class of one): two days of sixteen appends each, their due merges run, then the
    /// days calm and consolidated. Every row is written by its append, once by the merge of its class, and once more by the
    /// consolidation: 2 writes a row, then 3, to the row.
    /// </summary>
    [Fact]
    public async Task ARowIsWrittenOnceAClassItClimbsAndOnceMoreConsolidated()
    {
        const int Fanout = 8;
        const long Top = 1 << 20;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Random random = new Random(43);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = Top,
                MergeFanout = Fanout,
                CalmAfter = TimeSpan.FromHours(1),
            },
            TimeProvider = clock,
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        long merged = 0;
        int classes = -1;
        for (int a = 0; a < 32; a++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await AppendAsync(dataset, all, Rows(random, 3_000, all.Count, day: a % 2));
            if (classes < 0)
            {
                PositionedObject appended = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
                classes = ObjectTally.ClassOf(appended.Entry.Bytes, Top, Fanout);
            }

            merged += await CompactAllAsync(dataset);
        }

        clock.Advance(TimeSpan.FromHours(2));
        long consolidated = await CompactAllAsync(dataset);
        Console.WriteLine($"TIERS: {all.Count} rows appended, {merged} rewritten by merges, {consolidated} by the consolidation");
        Assert.Equal(1, classes);
        Assert.Equal((long)all.Count * classes, merged);
        Assert.Equal(all.Count, consolidated);
        await HoldsAsync(dataset, all);
    }

    /// <summary>
    /// A day whose rows come in the key's order, appended in small parts and merged by class: its objects hold disjoint
    /// keys, under the top's class 0, and its consolidation merges them into one object rather than move each whole, so that
    /// the day reads from one object as a day whose keys came in any order does.
    /// </summary>
    [Fact]
    public async Task APartitionWhoseKeysCameInOrderConsolidatesIntoOneObject()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Sequence))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = TimeSpan.FromHours(1),
            },
            TimeProvider = clock,
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 10; a++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await AppendAsync(dataset, all, Rows(random, 1_000, all.Count, day: 0));
            await CompactAllAsync(dataset);
        }

        Assert.True(await LevelZeroAsync(dataset) > 1, "the day's merges left several objects");
        clock.Advance(TimeSpan.FromHours(2));
        long consolidated = await CompactAllAsync(dataset);
        Assert.Equal(all.Count, consolidated);
        Assert.Equal(0, await LevelZeroAsync(dataset));
        Assert.Equal(1, await dataset.ScanBuilder().ObjectsAsync(Ct).CountAsync(Ct));
        await HoldsAsync(dataset, all);
    }

    /// <summary>
    /// Late rows on both sides of a consolidated day's keys, each object of its class 0, so no run of small ones: level 1
    /// holds an object between them, which a move of the two would leave inside its range, a job the commit abandons each
    /// time it is planned; their consolidation merges the three, and applies.
    /// </summary>
    [Fact]
    public async Task LateObjectsAroundAConsolidatedOneMergeWithIt()
    {
        const int objectBytes = 64 << 10;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Random random = new Random(53);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Sequence))],
                ObjectBytes = objectBytes,
                MergeFanout = 4,
                CalmAfter = TimeSpan.FromHours(1),
            },
            TimeProvider = clock,
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        await AppendAsync(dataset, all, Rows(random, 4_000, 4_000, day: 0));
        clock.Advance(TimeSpan.FromHours(2));
        await CompactAllAsync(dataset);
        Assert.Equal(0, await LevelZeroAsync(dataset));

        await AppendAsync(dataset, all, Rows(random, 4_000, 0, day: 0));
        await AppendAsync(dataset, all, Rows(random, 4_000, 8_000, day: 0));
        foreach (PositionedObject held in await dataset.ScanBuilder().ObjectsAsync(Ct).Where(held => held.Level == 0).ToListAsync(Ct))
        {
            Assert.True(held.Entry.Bytes >= objectBytes / 4, $"a late object of {held.Entry.Bytes} bytes is under class 0");
        }

        clock.Advance(TimeSpan.FromHours(2));
        int jobs = 0;
        while (await dataset.CompactAsync(cancellationToken: Ct) is { } done)
        {
            Assert.Equal(OperationOutcome.Applied, done.Outcome);
            Assert.True(++jobs < 10, "the jobs end");
        }

        Assert.Equal(0, await LevelZeroAsync(dataset));
        all.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        await HoldsAsync(dataset, all);
    }

    /// <summary>
    /// A consolidation of a calm day held at its first object's put while another handle appends rows to that day and to
    /// another (spec A, 12): the consolidation still applies, the rows appended meanwhile stay at level 0, and every row
    /// reads back with the layout's invariants holding.
    /// </summary>
    [Fact]
    public async Task AnAppendDuringAConsolidationDoesNotAbandonIt()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Random random = new Random(45);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        GatedStore store = new GatedStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = TimeSpan.FromHours(1),
            },
            TimeProvider = clock,
            ContentsObjectBytes = 0,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct);
        List<Row> all = [];
        for (int a = 0; a < 6; a++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await AppendAsync(dataset, all, Rows(random, 2_000, all.Count, day: 0));
        }

        clock.Advance(TimeSpan.FromHours(2));
        store.Arm();
        Task<IReadOnlyList<CompactionResult>> consolidating = dataset.ConsolidateAsync(Ct).AsTask();
        await store.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await using (VortexDataset other = await VortexDataset.OpenAsync(memory, options, Ct))
        {
            await AppendAsync(other, all, Rows(random, 1_000, all.Count, day: 0));
            await AppendAsync(other, all, Rows(random, 1_000, all.Count, day: 1));
        }

        store.Release.SetResult();
        IReadOnlyList<CompactionResult> done = await consolidating;
        Assert.NotEmpty(done);
        Assert.All(done, result => Assert.Equal(OperationOutcome.Applied, result.Outcome));
        await dataset.RefreshAsync(Ct);
        Assert.Equal(2, await LevelZeroAsync(dataset));
        await HoldsAsync(dataset, all);
    }

    /// <summary>A store that holds the first put of a data object once armed, until released.</summary>
    private sealed class GatedStore(IObjectStore inner) : ForwardingStore(inner)
    {
        private int _armed;

        internal TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Arm() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<PutOutcome> PutAsync(
            string key, System.IO.Pipelines.PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            if (key.StartsWith("data/", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await Inner.PutAsync(key, content, length, condition, cancellationToken);
        }
    }

    /// <summary>The rows the due jobs rewrote, a job that moves objects rewriting none.</summary>
    private static async Task<long> CompactAllAsync(VortexDataset dataset)
    {
        long rows = 0;
        while (await dataset.CompactAsync(cancellationToken: Ct) is { } done)
        {
            rows += done.ObjectsMoved > 0 ? 0 : done.Rows;
        }

        return rows;
    }

    /// <summary>The layout's invariants hold, and every row appended reads back.</summary>
    private static async Task HoldsAsync(VortexDataset dataset, List<Row> all)
    {
        DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(verified.Holds, string.Join('\n', verified.Problems));
        List<Row> read = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(all, read.OrderBy(r => r.Sequence));
    }

    private static async Task<long> LevelZeroAsync(VortexDataset dataset) =>
        await dataset.ScanBuilder().ObjectsAsync(Ct).CountAsync(held => held.Level == 0, Ct);

    private static Row[] Rows(Random random, int count, long first, int day) =>
        [.. Enumerable.Range(0, count).Select(i => new Row(day, $"svc-{random.Next(0, 12)}", first + i, random.Next(0, 100), random.NextDouble()))];

    private static async Task AppendAsync(VortexDataset dataset, List<Row> all, Row[] rows)
    {
        all.AddRange(rows);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Measure);
}
