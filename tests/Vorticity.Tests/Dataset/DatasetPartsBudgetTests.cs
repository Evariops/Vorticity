// The parts of a question of many groups under a budget of query memory: each part gives its states through the core
// and its spills as it gives its answers, and the merge folds them under the same budget, to the answers of the whole
// question granted everything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Aggregation;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(CorePressureCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class DatasetPartsBudgetTests
{
    private const int Objects = 4;
    private const int PerObject = 200_000;
    private const int Keys = 600_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 800 000 rows over keys 0 to 600 000 at random, by key, with a count, a sum and a greatest: under a sixth of what the
    /// whole question holds granted everything, every part and the merge hold back groups, through the core or a spill,
    /// and the merge of the three parts answers every group as the whole question does.
    /// </summary>
    [Fact]
    public async Task PartsAndTheirMergeUnderABudgetAnswerAsTheWholeQuestion()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await FillAsync(store);
        QueryMemoryBudget large = new QueryMemoryBudget(4L << 30);
        List<KeyTotal> expected = await AskAsync(store, large, null, async question => await question.As<KeyTotal>().ToRecordsAsync(Ct).ToListAsync(Ct));
        Assert.True(expected.Count > Keys / 2, $"{expected.Count} groups");

        string scratch = TestScratch.Create("vorticity-dataset-parts-");
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(large.PeakBytes / 6);
            RowRange[] parts = await AskAsync(store, budget, scratch, question => question.PartsAsync(3, Ct).AsTask());
            Assert.Equal(3, parts.Length);
            List<AggregationPartial> partials = [];
            foreach (RowRange part in parts)
            {
                partials.Add(await AskAsync(store, budget, scratch, async question =>
                {
                    AggregationPartial partial = await question.PartialAsync(Ct);
                    Assert.True(question.Plan.LastRun is { SpilledRuns: > 0 } or { Core: not null }, "the budget held nothing of a part back");
                    return partial;
                }, part));
            }

            List<KeyTotal> merged = await AskAsync(store, budget, scratch, async question =>
            {
                Vorticity.Aggregation merge = question.Merge(partials);
                List<KeyTotal> answers = await merge.As<KeyTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
                Assert.True(merge.Plan.LastRun is { SpilledRuns: > 0 } or { Core: not null }, "the budget held nothing of the merge back");
                return answers;
            });

            Assert.Equal(expected.OrderBy(k => k.Key), merged.OrderBy(k => k.Key));
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Four objects of 200 000 rows, their keys at random among 600 000.</summary>
    private static async Task FillAsync(IObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Counted.Schema, new DatasetOptions { ClusteringKey = [nameof(Counted.Id)] }, Ct);
        Random random = new Random(0x9A_27);
        for (int o = 0; o < Objects; o++)
        {
            Counted[] rows = new Counted[PerObject];
            for (int i = 0; i < rows.Length; i++)
            {
                long at = ((long)o * PerObject) + i;
                rows[i] = new Counted(at, random.Next(Keys), random.NextInt64(-1_000_000, 1_000_000), random.NextDouble() * 1e6);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Counted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }
    }

    /// <summary>What <paramref name="ask"/> makes of the question, over the rows of <paramref name="part"/> or every row, under <paramref name="budget"/> at four lanes.</summary>
    private static async Task<T> AskAsync<T>(IObjectStore store, QueryMemoryBudget budget, string? scratch, Func<Vorticity.Aggregation, Task<T>> ask, RowRange? part = null)
    {
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 4;
            options.MemoryBudget = budget;
            if (scratch is not null)
            {
                options.ScratchDirectory = scratch;
            }
        });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        Scan<Counted> scan = part is { } rows ? dataset.Scan<Counted>().Rows(rows) : dataset.Scan<Counted>();
        return await ask(scan.GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value), g.Max(r => r.Level))));
    }

    [VortexRecord]
    public partial record struct Counted(long Id, long Key, long Value, double Level);

    [VortexRecord]
    public partial record struct KeyTotal(long Key, long Count, long Sum, double? Greatest);
}
