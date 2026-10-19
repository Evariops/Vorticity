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

    /// <summary>A budget too small for a copy: each question shreds for itself and keeps nothing, and answers alike.</summary>
    [Fact]
    public async Task ACopyTheBudgetRefusesServesItsQuestionAlone()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(static options => options.MemoryBudget = new QueryMemoryBudget(1_024));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions { ClusteringKey = [nameof(Doc.Id)], Session = session }, Ct);
        List<Doc> rows = await AppendAsync(dataset);

        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(Routes(rows), await RoutesAsync(dataset));
        Assert.Equal(0, dataset.LevelZero.Held);
        Assert.Equal(6, dataset.LevelZero.Refused);
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

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
