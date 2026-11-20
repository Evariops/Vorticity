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
    public async Task SmallAppendsMergeAsTheirClassesFillAndCalmPartitionsConsolidate()
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

    /// <summary>
    /// Four appends of one day that weigh together 99 % of the size the day's objects aim at, merged by tiers: one object,
    /// and no job after it. Their rows interleave in the key's order, each append's status and measure its own number,
    /// which an append writes once and the merge a few bits a row, so that the merge writes more than it reads and its data
    /// passes the size at a group's end of its writer. A job whose plan chose its inputs to make one object
    /// (<see cref="CompactionJob.OneObject"/>) ends no object by its size: one that did ended the object there and wrote
    /// others of its inputs' size class after it, which the plan took again.
    /// </summary>
    [Fact]
    public async Task AMergeOfInputsThatWeighTheSizeAtMostWritesOneObject()
    {
        long top = await TopOfFourAppendsAsync(percent: 99, interleaved: true);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FourAppendsAsync(store, top, ObjectBound.OutPerIn, ObjectSegmentSink.DefaultStreamedFromBytes, interleaved: true);

        CompactionResult job = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: Ct));
        string ran = $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut}, {job.BytesIn}->{job.BytesOut} B under a size of {top}";
        Assert.True(job.Trigger == CompactionTrigger.Tier && job.ObjectsIn == 4, ran);

        // Within the size, and past what the bound's margin leaves under it.
        Assert.True(job.BytesIn <= top && job.BytesIn * ObjectBound.OutPerIn > top, ran);
        Assert.True(job.ObjectsOut == 1, ran);
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(4 * CompressibleRows, await dataset.Scan<Row>().CountAsync(Ct));
    }

    /// <summary>
    /// Four appends that weigh 90 % of the size, merged into an object the store takes as it is written, announced at half
    /// of what the merge reads: the object runs past that bound and the job starts again under twice as much, which passes
    /// the bound of inputs of the size, so that it ends an object as it reaches the size. It has every column measured
    /// before it ends one, finds its object under the size, and writes one. Ended on the rate a column is guessed to
    /// encode at before its first chunk, four times what these do, it wrote several, as many as it read.
    /// </summary>
    [Fact]
    public async Task AMergeStartedAgainUnderADoubledBoundEndsNoObjectOnAGuess()
    {
        long top = await TopOfFourAppendsAsync(percent: 90);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FourAppendsAsync(store, top, outPerIn: 0.5, streamedFrom: 64 << 10);

        CompactionResult job = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: Ct));
        string ran = $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut}, {job.BytesIn}->{job.BytesOut} B under a size of {top}, {job.BoundRestarts} restarts";
        Assert.True(job.Trigger == CompactionTrigger.Tier && job.ObjectsIn == 4, ran);
        Assert.True(job.BoundRestarts >= 1, ran);
        Assert.True(job.ObjectsOut == 1 && job.BytesOut <= top, ran);
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(4 * CompressibleRows, await dataset.Scan<Row>().CountAsync(Ct));
    }

    /// <summary>
    /// Sixteen appends of one day consolidated into objects aimed at 512 KiB, in blocks of 256 rows, whose indexes and
    /// zone maps weigh a fair share of an object: the data of every object but the last holds its size within a group of
    /// rows, the first's as the others', whatever the object writes past its data, which its size does not count. An
    /// object's size once counted what the objects before it wrote past their chunks, which the first had none of to go by,
    /// and an index kept in one object and given up in the next moved that tenfold.
    /// </summary>
    [Fact]
    public async Task AnObjectsDataReachesItsSizeWhateverItsEndWeighs()
    {
        const long Top = 512 << 10;
        Random random = new Random(47);
        List<(long Bytes, long Data, long Rows)> objects = await ConsolidatedAsync(Top, chunkBytes: 1 << 20, (first, count) => Rows(random, count, first, day: 0));
        string sizes = string.Join(", ", objects);
        Assert.True(objects.Count >= 5, sizes);
        HoldsItsSizeWithinAGroup(objects, Top);
        Assert.All(objects, held => Assert.True(held.Bytes > held.Data, sizes));
    }

    /// <summary>
    /// A quarter of the rows under services of their own, each a name of 26 characters that encodes to some ten times what
    /// the other rows do, and which sort first: an object of those rows, then one that holds the last of them and the first
    /// of the light ones, then the light ones. The data of every object but the last holds its size within a group of rows:
    /// no reading placed by what the rows before it weighed seals an object, which the light rows past the heavy ones would
    /// leave short.
    /// </summary>
    [Fact]
    public async Task AnObjectNeverEndsOnAnEstimate()
    {
        const long Top = 256 << 10;
        List<(long Bytes, long Data, long Rows)> objects = await ConsolidatedAsync(
            Top,
            chunkBytes: 1 << 20,
            static (first, count) => [.. Enumerable.Range(0, count).Select(i => (first + i) % 4 == 0
                ? new Row(0, $"a-{(uint)((first + i) * 2_654_435_761L):x8}{(uint)((first + i) * 40_503L):x8}{first + i:x8}", first + i, 0, 0)
                : new Row(0, $"b-{(first + i) % 3}", first + i, (int)((first + i) / 48), 0))]);
        Assert.True(objects.Count >= 4, string.Join(", ", objects));
        HoldsItsSizeWithinAGroup(objects, Top);
    }

    /// <summary>
    /// The same over rows of some thousand keys whose day, status and measure encode to a hundredth of their bytes, in
    /// chunks of 4 KiB, so that every column writes chunks within an object: the data of every object but the last holds
    /// its size within a group of rows. A column counted at a sixteenth of its bytes at least, the rate its chunk is due by,
    /// counted these several times over, and the objects ended short of their size. Its sequence, 19 bits a row, was
    /// written as it was in a chunk of 256 rows an end of a group cut, and the column wrote every chunk after it so, 8.25
    /// bytes a row where it wrote 2.4, the plan of that short chunk remembered: an object then passed its size by a half.
    /// </summary>
    [Fact]
    public async Task AColumnThatEncodesToAlmostNothingCountsAtWhatItEncodesTo()
    {
        const long Top = 96 << 10;
        List<(long Bytes, long Data, long Rows)> objects = await ConsolidatedAsync(
            Top, chunkBytes: 4 << 10, static (first, count) => [.. Enumerable.Range(0, count).Select(i => new Row(0, $"svc-{i % 3}", first + i, i / 48, i / 4_096))]);
        Assert.True(objects.Count >= 3, string.Join(", ", objects));
        HoldsItsSizeWithinAGroup(objects, Top);
    }

    /// <summary>
    /// The bytes and the bytes of data of the objects a consolidation of sixteen appends of 20 000 rows of one day writes,
    /// in the order of their rows, into objects aimed at <paramref name="top"/> bytes of data, in blocks of 256 rows and
    /// column chunks aimed at <paramref name="chunkBytes"/>; every row read back.
    /// </summary>
    private static async Task<List<(long Bytes, long Data, long Rows)>> ConsolidatedAsync(long top, int chunkBytes, Func<long, int, Row[]> rows)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = top,
                MergeFanout = 4,
                CalmAfter = null,
            },
            Write = new VortexWriteOptions { BlockRows = 256 },
            ChunkBytes = chunkBytes,
            ContentsObjectBytes = 0,
            GroupRows = GroupRows,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 16; a++)
        {
            await AppendAsync(dataset, all, rows(all.Count, 20_000));
        }

        IReadOnlyList<CompactionResult> jobs = await dataset.ConsolidateAsync(Ct);
        Assert.NotEmpty(jobs);
        List<(long Bytes, long Data, long Rows)> objects = [];
        await foreach (PositionedObject written in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects.Add((written.Entry.Bytes, jobs.Select(job => job.ObjectDataBytes.GetValueOrDefault(written.Entry.Key, -1)).Max(), written.Entry.Rows));
        }

        await HoldsAsync(dataset, all);
        return objects;
    }

    /// <summary>
    /// The rows of a group a job's writer ends by itself (<see cref="DatasetOptions.GroupRows"/>): sixteen blocks of 256
    /// rows, so that an object of these tests holds several, each where its job reads what the object holds.
    /// </summary>
    private const long GroupRows = 4_096;

    /// <summary>
    /// Asserts that every object but the last holds its size's data within a group of rows: at its size less the slack at
    /// least, and past it by no more than a group of its rows weighs, which its job reads it at.
    /// </summary>
    private static void HoldsItsSizeWithinAGroup(List<(long Bytes, long Data, long Rows)> objects, long top)
    {
        string sizes = string.Join(", ", objects);
        Assert.All(objects.SkipLast(1), held =>
            Assert.True(held.Data >= top * (1 - DatasetOptions.DefaultObjectEndSlack) && held.Data <= top + (held.Data * GroupRows / held.Rows), sizes));
    }

    private const int CompressibleRows = 40_000;

    /// <summary>
    /// The size four appends of <see cref="CompressibleRows"/> rows weigh <paramref name="percent"/> % of together, each
    /// then within a quarter of it, one size class under the top's.
    /// </summary>
    private static async Task<long> TopOfFourAppendsAsync(int percent, bool interleaved = false)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset sized = await FourAppendsAsync(store, 1L << 30, ObjectBound.OutPerIn, ObjectSegmentSink.DefaultStreamedFromBytes, interleaved);
        long largest = 0;
        await foreach (PositionedObject appended in sized.ScanBuilder().ObjectsAsync(Ct))
        {
            largest = Math.Max(largest, appended.Entry.Bytes);
        }

        return largest * 4 * 100 / percent;
    }

    /// <summary>
    /// A day of four appends of rows that encode to a small share of their bytes, far under the quarter a column is guessed
    /// to encode at before its first chunk, in a dataset whose objects aim at <paramref name="top"/> bytes, go to the store
    /// as they are written from <paramref name="streamedFrom"/> bytes, and whose rewrites announce
    /// <paramref name="outPerIn"/> bytes a byte they read. <paramref name="interleaved"/> keys them by the service then the
    /// sequence, which interleaves the appends' rows, each append's status and measure its own number, written once an
    /// append and a few bits a row by a merge, whose writer ends a group each 4 096 rows.
    /// </summary>
    private static async Task<VortexDataset> FourAppendsAsync(IObjectStore store, long top, double outPerIn, long streamedFrom, bool interleaved = false)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(interleaved ? nameof(Row.Sequence) : nameof(Row.Status))],
                ObjectBytes = top,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            StreamedObjectBytes = streamedFrom,
            BoundOutPerIn = outPerIn,
            GroupRows = interleaved ? GroupRows : new DatasetOptions().GroupRows,
        }, Ct);
        for (int a = 0; a < 4; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, CompressibleRows).Select(i => interleaved
                ? new Row(0, $"svc-{i % 3}", ((long)i * 4) + a, a, a)
                : new Row(0, $"svc-{i % 3}", ((long)a * CompressibleRows) + i, i % 5, i / 4_096))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
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
