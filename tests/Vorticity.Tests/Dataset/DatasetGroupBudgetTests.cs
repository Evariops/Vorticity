// A group by of millions of groups over a dataset under a budget of query memory: whatever the budget leaves, the lanes'
// tables, the core and their spills keep within it, and the answer is the one a budget that grants everything gives.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Aggregation;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(CorePressureCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class DatasetGroupBudgetTests
{
    /// <summary>The groups of the design's defect: a question of 5.7 M groups held 563 MB.</summary>
    private const int Groups = 5_700_000;

    /// <summary>What the core may hold past the ceiling at most, whatever the ceiling: its slabs and a part's table, growing within a batch.</summary>
    private const long Overdraft = 48L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 5.7 M groups over 6 M rows in twelve objects, by an integer key and a sum, at four lanes, which hold about 1.15 GB
    /// granted all: under a budget of a quarter or a tenth of that, the question gives every byte back, leaves no file in
    /// the scratch, answers every group as before, and holds within the budget but for an overdraft that does not grow
    /// with it: the core's slabs and the key table of a part it applies grow within a batch past the ceiling, rather than
    /// fail the question. Measured on 2026-10-25, 10 to 25 MB past ceilings of 115 and 290 MB; held here to 48 MiB.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task FiveMillionGroupsHoldWithinTheirBudget(int share)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await FillAsync(store);
        QueryMemoryBudget large = new QueryMemoryBudget(4L << 30);
        ((long Groups, long Rows, long Total) expected, _) = await AskAsync(store, large, scratch: null);
        Assert.Equal((Groups, 6_000_000L), (expected.Groups, expected.Rows));

        string scratch = TestScratch.Create("vorticity-dataset-groups-");
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(large.PeakBytes / share);
            ((long, long, long) answer, Aggregating.AggregationRun? run) = await AskAsync(store, budget, scratch);
            Assert.Equal(expected, answer);
            Console.WriteLine(
                $"DATASET GROUPS: {Groups:N0} groups, {large.PeakBytes:N0} bytes at most granted all; under {budget.CeilingBytes:N0}, {budget.PeakBytes:N0} at most, " +
                $"{run?.SpilledRuns ?? 0} runs spilled, the core {(run?.Core is { } core ? $"{core.Reason}, {core.SpilledParts} parts spilled" : "not taken")}");
            Assert.True(run is { SpilledRuns: > 0 } or { Core: not null }, "the budget held nothing back");
            Assert.True(budget.PeakBytes - budget.CeilingBytes <= Overdraft, $"peak {budget.PeakBytes:N0} of {budget.CeilingBytes:N0}");
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Six million rows in twelve objects, keys 0 to 5.7 M at random so that most keys hold one row, and some two.</summary>
    private static async Task FillAsync(IObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Counted.Schema, new DatasetOptions { ClusteringKey = [nameof(Counted.Id)] }, Ct);
        Random random = new Random(0x5_7);
        for (int part = 0; part < 12; part++)
        {
            Counted[] rows = new Counted[500_000];
            for (int i = 0; i < rows.Length; i++)
            {
                long at = ((long)part * rows.Length) + i;
                rows[i] = new Counted(at, at < Groups ? at : random.Next(Groups), i % 1_000);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Counted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }
    }

    /// <summary>The groups, the rows they count and the sum of their sums, asked under <paramref name="budget"/> at four lanes.</summary>
    private static async Task<((long Groups, long Rows, long Total) Answer, Aggregating.AggregationRun? Run)> AskAsync(
        IObjectStore store, QueryMemoryBudget budget, string? scratch)
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
        long groups = 0;
        long rows = 0;
        long total = 0;
        Vorticity.Aggregation grouped = dataset.Scan<Counted>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
        await foreach (KeyTotal group in grouped.As<KeyTotal>().ToRecordsAsync(Ct))
        {
            groups++;
            rows += group.Count;
            total += group.Sum;
        }

        return ((groups, rows, total), grouped.Plan.LastRun);
    }

    [VortexRecord]
    public partial record struct Counted(long Id, long Key, long Value);

    [VortexRecord]
    public partial record struct KeyTotal(long Key, long Count, long Sum);
}
