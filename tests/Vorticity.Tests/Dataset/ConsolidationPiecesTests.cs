// A consolidation larger than an object runs a piece at a time, each committed: the keys below its cut to level 1, the
// rows it took marked in the inputs of level 0 it leaves there. Between two pieces, a read gives every row once; the last
// piece leaves level 0 empty; a handle opened afresh between two pieces goes on where the last one stopped.
//
// THE ORACLE IS THE ROWS: every row read back once, after each piece and at the end, and level 0 empty at the end.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ConsolidationPiecesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A day of rows merged by size class into objects that keep a sparse index, then consolidated under objects of 256 KiB:
    /// the consolidation runs in several pieces, each committed, and leaves every row in level 1.
    /// </summary>
    [Fact]
    public async Task AConsolidationLargerThanAnObjectRunsInPieces()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 32, rows: 2_048);
        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);

        Assert.True(results.Count(r => r.Piece) >= 1, $"{results.Count} consolidations, none a piece");
        Assert.Equal(0, dataset.Levels[0].Entries);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// The same consolidation a piece at a time, a handle opened afresh before each, as a maintainer that restarts: every
    /// row reads back once after each piece, and the pieces end with level 0 empty.
    /// </summary>
    [Fact]
    public async Task EachPieceLeavesEveryRowOnceAndAFreshHandleGoesOn()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        List<Row> all;
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(), Ct))
        {
            all = await AppendAsync(dataset, appends: 32, rows: 2_048);
            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        int pieces = 0;
        for (int step = 0; step < 64; step++)
        {
            await using VortexDataset handle = await VortexDataset.OpenAsync(store, Options(), Ct);
            CompactionPlan plan = await CompactionPolicy.PlanConsolidationsAsync(handle, Ct);
            if (plan.Jobs.FirstOrDefault(j => j.Trigger == CompactionTrigger.Consolidation) is not { } job)
            {
                break;
            }

            CompactionResult result = await DatasetCompactor.RunAsync(handle, job, Ct);
            pieces += result.Piece ? 1 : 0;
            Assert.Equal(all.Count, await handle.ScanBuilder().CountAsync(Ct));
            Assert.Equal(all.OrderBy(r => r.Sequence), (await handle.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
            if (!result.Piece)
            {
                Assert.Equal(0, handle.Levels[0].Entries);
                break;
            }
        }

        Assert.True(pieces >= 1, "no piece ran");
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Sequence))],
            ObjectBytes = 256 << 10,
            MergeFanout = 4,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,

        // Pieces of one object's size, so that a day of a few objects runs in several.
        PieceObjects = 1,
    };

    /// <summary><paramref name="appends"/> appends of <paramref name="rows"/> rows of one day, every one spanning every service.</summary>
    private static async Task<List<Row>> AppendAsync(VortexDataset dataset, int appends, int rows)
    {
        Random random = new Random(41);
        List<Row> all = [];
        for (int a = 0; a < appends; a++)
        {
            Row[] batch = [.. Enumerable.Range(0, rows).Select(i => new Row(
                0, $"svc-{random.Next(0, 64):D2}", all.Count + i, $"/api/route-{random.Next(0, 500)}/{random.NextInt64():x16}"))];
            all.AddRange(batch);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(batch, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return all;
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, string Route);
}
