// Where an append's rows begin in a dataset partitioned with no key: past the end of every object's positions. The end of
// each subtree is kept by its page (VortexDataset.SubtreeEndAsync), which no version rewrites, so that an append reads the
// pages its commit wrote again and no other. Over a tree of several pages, with early days expired, the end comes from a
// subtree kept: it is what a walk of every entry finds.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetAppendEndTests
{
    private const int Days = 2_100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheEndOfThePositionsIsEveryEntrysWhenTheLastDaysExpire()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Logged.Day)) },
        }, Ct);

        long sequence = 0;
        Logged[] rows = new Logged[4];
        for (int day = 0; day < Days; day++)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Logged(day, sequence++);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Logged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        Assert.True(dataset.Snapshot.Levels[0].Depth > 1, $"a tree of {Days} objects is {dataset.Snapshot.Levels[0].Depth} page(s) deep");
        Assert.Equal(await WalkedEndAsync(dataset), await dataset.EndAsync(Ct));

        // Early days expired, one at a time: the rows left count fewer than the positions run to, and the greatest position
        // lies in the last page, which the expiry did not rewrite.
        for (int day = 3; day < 300; day += 59)
        {
            await dataset.ExpireAsync(Expr.Eq(Expr.Field(nameof(Logged.Day)), Expr.Literal(FilterLiteral.From(day))), Ct);
            long walked = await WalkedEndAsync(dataset);
            Assert.True(walked > dataset.Snapshot.RowCount);
            Assert.Equal(walked, await dataset.EndAsync(Ct));
        }
    }

    /// <summary>The end every entry's place and rows give, walked whole.</summary>
    private static async Task<long> WalkedEndAsync(VortexDataset dataset)
    {
        DatasetSnapshot version = dataset.Snapshot;
        long end = version.RowCount;
        foreach ((int _, DatasetTree tree) in version.Levels.Occupied())
        {
            await foreach (PositionedEntry held in tree.WalkAsync(version.Pages, 0, long.MaxValue, null, Ct))
            {
                end = Math.Max(end, VortexDataset.PositionAt(held.Entry.Key.Span) + held.Entry.Rows);
            }
        }

        return end;
    }

    [VortexRecord]
    public partial record struct Logged(int Day, long Sequence);
}
