// The memo of level 0: a query node shreds an object of level 0 in memory the first time a question reads
// keys of its variant column, and serves every later question on those keys from that copy, until the
// object leaves level 0; a memory budget that refuses the copy leaves each question to parse.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
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
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { ClusteringKey = [nameof(Doc.Id)] }, Ct);
        List<Doc> rows = await AppendAsync(dataset);
        LevelZeroMemo memo = dataset.LevelZero;

        for (int question = 0; question < 5; question++)
        {
            Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        }

        Assert.Equal(3, memo.Shredded);
        Assert.Equal(12, memo.Served);
        Assert.Equal(3, memo.Held);

        Assert.Equal(rows.Select(r => r.Attributes["status"]?.AsInt64()).Order(), (await dataset.Scan<Doc>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)).Order());
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
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { ClusteringKey = [nameof(Doc.Id)], Session = session }, Ct);
        List<Doc> rows = await AppendAsync(dataset);

        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(0, dataset.LevelZero.Held);
        Assert.Equal(6, dataset.LevelZero.Refused);
        Assert.Equal(0, dataset.LevelZero.Shredded);
    }

    /// <summary>
    /// Three objects of level 0, a question on the rows of the first and one on the rows of the last: while every read of
    /// the first object is held, the question on the last shreds its own object and answers; the first answers once its
    /// reads go on. Neither waits on the other.
    /// </summary>
    [Fact]
    public async Task TwoQuestionsOnTwoObjectsOfLevelZeroWaitForNoOther()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        HoldingStore store = new HoldingStore(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { ClusteringKey = [nameof(Doc.Id)] }, Ct);
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
        Assert.Equal(2, dataset.LevelZero.Shredded);
    }

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

        /// <summary>Set once a read of the object is held.</summary>
        internal TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set by the test to let the reads go on.</summary>
        internal TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<Vorticity.IO.ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            await HoldAsync(key, cancellationToken);
            return await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }

        public override async ValueTask<Vorticity.IO.ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            await HoldAsync(key, cancellationToken);
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
