// The consolidation in ranges of the key (spec A, 6.4; A6): a partition consolidated by the lanes of a session side by
// side, each a range of the key, as one lane consolidates it in one piece.
//
// THE ORACLE IS THE ONE PIECE AND THE INVARIANTS: the same appends and the same delete into a dataset of four lanes and one
// of one; both consolidate a day each, hold every live row and no deleted one, at level 1 alone, and verify (each object one
// day's, sorted, the level of a day disjoint).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetRangedConsolidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight appends of 15 000 rows, most of a first day, the rest of a second, merged, a status of the first day deleted,
    /// then each day consolidated: on four lanes in ranges of the key, on one in one piece. Both run two consolidations,
    /// keep the live rows alone at level 1, and hold the layout's invariants.
    /// </summary>
    [Fact]
    public async Task ADayConsolidatedInRangesHoldsWhatOnePieceHolds()
    {
        (List<Row> appended, List<Row> ranged) = await ConsolidateAsync(lanes: 4);
        (List<Row> appendedAgain, List<Row> single) = await ConsolidateAsync(lanes: 1);
        Assert.Equal(appended, appendedAgain);
        List<Row> live = [.. appended.Where(static row => !(row.Day == 0 && row.Status == 7))];
        Assert.Equal(live, ranged.OrderBy(static row => row.Sequence));
        Assert.Equal(live, single.OrderBy(static row => row.Sequence));
    }

    private static async Task<(List<Row> Appended, List<Row> Read)> ConsolidateAsync(int lanes)
    {
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 8; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 15_000).Select(i => new Row(random.Next(0, 8) == 0 ? 1 : 0, $"svc-{random.Next(0, 400):D3}", all.Count + i, random.Next(0, 100), random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.DeleteAsync<Row>(static row => row.Day == 0 & row.Status == 7, Ct);
        Assert.Equal(2, (await dataset.ConsolidateAsync(Ct)).Count);
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(held.Level == 1 && !held.Entry.HasDeletions, $"{lanes} lanes: '{held.Entry.Key}' at level {held.Level}, deletions {held.Entry.HasDeletions}");
        }

        DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(verified.Holds, $"{lanes} lanes:\n{string.Join('\n', verified.Problems)}");
        return (all, await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct));
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Value);
}
