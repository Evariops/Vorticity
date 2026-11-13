// A merge by size class that takes what is due: the whole class when its bounds hold it, neighbours in the tree's order
// otherwise, the fan-out at least; a calm partition consolidated in one job rather than merged by class first; and a
// writer's inline compaction cut to its bound rather than left to another driver.
//
// THE ORACLE IS THE PLAN AND THE ROWS: the jobs the plan gives, their inputs, and every row read back after them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class WideTierMergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Forty small appends of one day, none merged as they came: one merge takes the forty, where merges of the fan-out at a
    /// time took thirteen jobs and rewrote rows of the same class again.
    /// </summary>
    [Fact]
    public async Task AClassBehindIsMergedWholeInOneJob()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 40);

        CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: Ct);
        Assert.NotNull(plan.Job);
        Assert.Equal(CompactionTrigger.Tier, plan.Job.Trigger);
        Assert.Equal(40, plan.Job.ObjectKeys.Length);

        CompactionResult? merged = await dataset.CompactAsync(cancellationToken: Ct);
        Assert.Equal(40, merged!.ObjectsIn);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// The same class under a jobs' budget that holds some twelve inputs open: a merge takes the fan-out at least and as many
    /// more as the budget holds, the first in the tree's order, and the plan after it merges the rest.
    /// </summary>
    [Fact]
    public async Task AMergeTakesNeighboursWithinItsBudget()
    {
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(12 * DatasetCompactor.InputFloorBytes));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 40);
        List<DataObject> objects = await dataset.ListObjectsAsync(cancellationToken: Ct).ToListAsync(Ct);

        CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: Ct);
        Assert.NotNull(plan.Job);
        Assert.InRange(plan.Job.ObjectKeys.Length, 4, 12);
        Assert.Equal(objects.Take(plan.Job.ObjectKeys.Length).Select(o => o.Key), plan.Job.ObjectKeys);

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// A calm day whose size class is full is consolidated in one job, its forty objects to level 1, rather than merged by
    /// class first and consolidated after.
    /// </summary>
    [Fact]
    public async Task ACalmPartitionIsConsolidatedRatherThanMergedFirst()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Row.Schema, Options(VortexSession.Default, TimeSpan.FromHours(1), clock), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 40);
        clock.Advance(TimeSpan.FromHours(2));

        CompactionResult? job = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.Equal(CompactionTrigger.Consolidation, job!.Trigger);
        Assert.Equal(40, job.ObjectsIn);
        Assert.Equal(0, dataset.Levels[0].Entries);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// A writer whose inline compaction bound holds a quarter of what level 0 owes merges what its bound holds, the fan-out
    /// at least, rather than leaving the whole merge to another driver.
    /// </summary>
    [Fact]
    public async Task AnInlineCompactionIsCutToItsBound()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset quiet = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default), Ct);
        await AppendAsync(quiet, appends: 39);
        long owed = (await quiet.PlanCompactionAsync(cancellationToken: Ct)).Job!.Bytes;

        await using VortexDataset writer = await VortexDataset.OpenAsync(store, new DatasetOptions { InlineCompactionBytes = owed / 4 }, Ct);
        List<Row> last = await AppendAsync(writer, appends: 1, first: 39 * 20);

        Assert.Null(writer.LastInlineCompactionFailure);
        Assert.True(writer.Levels[0].Entries < 40, $"{writer.Levels[0].Entries} objects left at level 0");
        Assert.Equal(40 * 20, await writer.ScanBuilder().CountAsync(Ct));
    }

    private static DatasetOptions Options(VortexSession session, TimeSpan? calmAfter = null, TimeProvider? clock = null) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
            ObjectBytes = 64 << 20,
            MergeFanout = 4,
            CalmAfter = calmAfter,
        },
        ContentsObjectBytes = 0,
        TimeProvider = clock ?? TimeProvider.System,
    };

    /// <summary><paramref name="appends"/> appends of 20 rows of one day, none merged as they came.</summary>
    private static async Task<List<Row>> AppendAsync(VortexDataset dataset, int appends, long first = 0)
    {
        Random random = new Random(19 + (int)first);
        List<Row> all = [];
        for (int a = 0; a < appends; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 20).Select(i => new Row(0, $"svc-{random.Next(0, 12)}", first + all.Count + i, random.Next(0, 100), random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return all;
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Measure);
}
