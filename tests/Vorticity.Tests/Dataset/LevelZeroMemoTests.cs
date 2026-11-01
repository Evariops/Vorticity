// The memo of level 0: a query node shreds an object of level 0 in memory the first time a question reads
// keys of its variant column, and serves every later question on those keys from that copy, until the
// object leaves level 0; a memory budget that refuses the copy leaves each question to parse.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class LevelZeroMemoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three objects of level 0 whose attributes hold a route and a status: five questions on the route
    /// shred each object once and serve the four others from the copies; a question on the status shreds
    /// each again with both keys, and the route then reads from those copies too. Every answer is the rows'.
    /// Once the objects leave level 0, the next question lets their copies go.
    /// </summary>
    [Fact]
    public async Task AKeyOfLevelZeroIsParsedOncePerObjectAndNode()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))] } }, Ct);
        List<Doc> rows = await AppendAsync(dataset);
        LevelZeroMemo memo = dataset.LevelZero;

        // The first question parses the objects as they lie, their copies made beside it; the four after read the copies.
        for (int question = 0; question < 5; question++)
        {
            Assert.Equal(Routes(rows), await RoutesAsync(dataset));
            await dataset.LevelZero.WhenMadeAsync();
        }

        Assert.Equal(3, memo.Shredded);
        Assert.Equal(12, memo.Served);
        Assert.Equal(3, memo.Held);

        Assert.Equal(rows.Select(r => r.Attributes["status"]?.AsInt64()).Order(), (await dataset.Scan<Doc>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)).Order());
        await dataset.LevelZero.WhenMadeAsync();
        Assert.Equal(6, memo.Shredded);
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(6, memo.Shredded);

        // The objects leave level 0: the next question lets the copies go, and parses nothing of theirs.
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(0, memo.Held);
        Assert.Equal(6, memo.Shredded);
    }

    /// <summary>
    /// A budget too small for a copy: the memory asked before one is made, as large as the object, is refused, so each
    /// question parses the object as it lies, no copy made, and answers alike.
    /// </summary>
    [Fact]
    public async Task ACopyTheBudgetRefusesIsNeverMade()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(static options => options.MemoryBudget = new QueryMemoryBudget(1_024));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))] }, Session = session }, Ct);
        List<Doc> rows = await AppendAsync(dataset);

        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        await dataset.LevelZero.WhenMadeAsync();
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        await dataset.LevelZero.WhenMadeAsync();
        Assert.Equal(0, dataset.LevelZero.Held);
        Assert.Equal(6, dataset.LevelZero.Refused);
        Assert.Equal(0, dataset.LevelZero.Shredded);
    }

    /// <summary>
    /// Three objects of level 0, a question on the rows of the first and one on the rows of the last: while every read of
    /// the first object is held, the question on the last answers, its object's copy made beside it; the first answers
    /// once its reads go on. Neither waits on the other, nor on a copy.
    /// </summary>
    [Fact]
    public async Task TwoQuestionsOnTwoObjectsOfLevelZeroWaitForNoOther()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        HoldingStore store = new HoldingStore(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))] } }, Ct);
        List<Doc> rows = await AppendAsync(dataset);
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            if (held.Entry.Summaries.TryGet(nameof(Doc.Id), out Vorticity.Scanning.ColumnSummary summary) && summary.Min.SignedValue == 0)
            {
                store.Holding = held.Entry.Key;
            }
        }

        Assert.NotNull(store.Holding);
        Task<List<string?>> first = RoutesAsync(dataset, r => r.Id < 2_000);
        try
        {
            await store.Reached.Task.WaitAsync(Ct);
            Assert.Equal(Routes([.. rows.Where(r => r.Id >= 4_000)]), await RoutesAsync(dataset, r => r.Id >= 4_000).WaitAsync(TimeSpan.FromMinutes(1), Ct));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            // Let go whatever happened, so that the dataset closes behind the question held.
            store.Release.TrySetResult();
        }

        Assert.Equal(Routes([.. rows.Where(r => r.Id < 2_000)]), await first);
        await dataset.LevelZero.WhenMadeAsync();
        Assert.Equal(2, dataset.LevelZero.Shredded);
    }

    /// <summary>
    /// An object of level 0 large enough that its granules are read by their marks, a service's call by row and attributes of
    /// a status and a text: a question on the status, shredded in a copy written again in memory, reads that copy whole. The
    /// object's marks and sparse index lie in the object, not in the copy, whose chunks are its own; read through it, they
    /// named bytes past its end, or others than theirs. The answer is LINQ's.
    /// </summary>
    [Fact]
    public async Task ACopyIsReadWithNoneOfTheObjectsRegions()
    {
        Random random = new Random(11);
        Call[] calls = [.. Enumerable.Range(0, 200_000).Select(i => new Call(
            $"svc-{i * 16 / 200_000:D2}", i, Variant.Object(
                ("status", Variant.From(random.Next(20) == 0 ? 500 + random.Next(4) : 200 + random.Next(4))),
                ("text", Variant.From($"call {random.Next(1 << 30)} of a route {random.Next(40)}")))))];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Id))], IndexedPrefix = 1 },
            CostProfile = CostProfile.Memory,
        }, Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Call>(calls, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        string[] expected = [.. calls.Where(static c => c.Attributes["status"]?.AsInt64() >= 500).GroupBy(static c => c.Service)
            .Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)];
        // The first question parses the object as it lies, its copy made beside it; the second reads the copy.
        for (int asked = 0; asked < 2; asked++)
        {
            List<ServiceCount> answers = await dataset.Scan<Call>().Where(c => c.Attributes["status"].AsInt64() >= 500)
                .GroupBy(c => c.Service).Select(g => (g.Key, g.Count())).As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(expected, answers.Select(static a => $"{a.Service}:{a.Count}").Order(StringComparer.Ordinal));
            await dataset.LevelZero.WhenMadeAsync();
        }

        Assert.Equal(1, dataset.LevelZero.Shredded);
        Assert.Equal(1, dataset.LevelZero.Served);
    }

    /// <summary>
    /// An object of level 0, larger than the tail its open reads, copied for a question on a key of its attributes: its
    /// keys are counted, then its rows written again, two passes over its rows, over its bytes read once; they read it
    /// twice through its open before.
    /// </summary>
    [Fact]
    public async Task AnObjectIsReadOnceForItsCopy()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))] } }, Ct);
        Random random = new Random(13);
        List<Doc> rows = [.. Enumerable.Range(0, 100_000).Select(i => new Doc(i, Variant.Object(
            ("route", Variant.From($"/r/{random.Next(7)}/{random.Next(1 << 20)}")), ("status", Variant.From(200 + random.Next(4))))))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Doc>([.. rows], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        // A rent alone starts the copy beside it: what the copy reads, once it is made, the rent's open aside.
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        long bytes = held.Entry.Bytes;
        store.Reset();
        ObjectLease lease = await dataset.LevelZero.RentAsync(dataset, dataset.Snapshot, held.Entry, new Dictionary<string, string[]> { [nameof(Doc.Attributes)] = ["route"] }, Ct);
        await lease.DisposeAsync();
        await dataset.LevelZero.WhenMadeAsync();
        long read = store.BytesRead;
        Assert.Equal(1, dataset.LevelZero.Shredded);
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(1, dataset.LevelZero.Served);
        Console.WriteLine($"LEVEL ZERO COPY: {read} bytes read to copy an object of {bytes}");
        Assert.True(read < bytes * 3 / 2, $"{read} bytes read to copy an object of {bytes}");
    }

    /// <summary>
    /// An object of level 0 whose copy cannot be made, every whole read of it held: the first question on a key of it
    /// answers all the same, the object parsed as it lies, its copy left to be made beside it; once the reads go on, the
    /// copy is made, and the next question reads it. A copy made on the question's path kept the question waiting on it.
    /// </summary>
    [Fact]
    public async Task AFirstQuestionWaitsOnNoCopy()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        HoldingStore store = new HoldingStore(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))] } }, Ct);
        Random random = new Random(17);
        List<Doc> rows = [.. Enumerable.Range(0, 20_000).Select(i => new Doc(i, Variant.Object(
            ("route", Variant.From($"/r/{random.Next(7)}/{random.Next(1 << 20)}")), ("status", Variant.From(200 + random.Next(4))))))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Doc>([.. rows], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        (store.Holding, store.WholeLength) = (held.Entry.Key, held.Entry.Bytes);
        try
        {
            Assert.Equal(Routes(rows), await RoutesAsync(dataset).WaitAsync(TimeSpan.FromMinutes(1), Ct));
            await store.Reached.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            Assert.Equal(0, dataset.LevelZero.Shredded);
        }
        finally
        {
            store.Release.TrySetResult();
        }

        await dataset.LevelZero.WhenMadeAsync();
        Assert.Equal(1, dataset.LevelZero.Shredded);
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(1, dataset.LevelZero.Served);
    }

    [VortexRecord]
    public partial record struct Call(string Service, long Id, Variant Attributes);

    [VortexRecord]
    public partial record struct ServiceCount(string? Service, long Count);

    private static async Task<List<Doc>> AppendAsync(VortexDataset dataset)
    {
        Random random = new Random(9);
        List<Doc> rows = [];
        for (int part = 0; part < 3; part++)
        {
            Doc[] docs = [.. Enumerable.Range(part * 2_000, 2_000).Select(i => new Doc(i, Variant.Object(
                ("route", Variant.From($"/r/{random.Next(7)}")), ("status", Variant.From(200 + random.Next(4))))))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(docs, Ct);
            await dataset.AppendAsync(draft, Ct);
            rows.AddRange(docs);
        }

        return rows;
    }

    private static List<string?> Routes(List<Doc> rows) => [.. rows.Select(r => r.Attributes["route"]?.AsString()).Order(StringComparer.Ordinal)];

    private static async Task<List<string?>> RoutesAsync(VortexDataset dataset) =>
        [.. (await dataset.Scan<Doc>().Select(r => r.Attributes["route"].AsString()).ToListAsync(Ct)).Order(StringComparer.Ordinal)];

    private static async Task<List<string?>> RoutesAsync(VortexDataset dataset, Func<Probe<Doc>, Predicate> where) =>
        [.. (await dataset.Scan<Doc>().Where(where).Select(r => r.Attributes["route"].AsString()).ToListAsync(Ct)).Order(StringComparer.Ordinal)];

    /// <summary>A store that holds every read of one object until the test lets them go on.</summary>
    private sealed class HoldingStore(Vorticity.IO.IObjectStore inner) : ForwardingStore(inner)
    {
        /// <summary>The key of the object whose reads are held; null holds none.</summary>
        internal string? Holding { get; set; }

        /// <summary>The length of the reads held, the object whole for a copy's; 0 holds every read.</summary>
        internal long WholeLength { get; set; }

        /// <summary>Set once a read of the object is held.</summary>
        internal TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set by the test to let the reads go on.</summary>
        internal TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<Vorticity.IO.ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            if (WholeLength == 0 || (offset == 0 && destination.Length == WholeLength))
            {
                await HoldAsync(key, cancellationToken);
            }

            return await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }

        public override async ValueTask<Vorticity.IO.ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (WholeLength == 0)
            {
                await HoldAsync(key, cancellationToken);
            }

            return await Inner.ReadTailAsync(key, destination, cancellationToken);
        }

        private async Task HoldAsync(string key, CancellationToken cancellationToken)
        {
            if (key == Holding)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
