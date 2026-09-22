using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The read promises a scan makes, over the table the samples read: a segment asked
/// for once, the plan the execution, a pruned block not decoded, a question the statistics answer
/// read from nothing else, an encoded aggregate not decoded.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class ReadContractTests
{
    public static TheoryData<string> Queries =>
        ["unfiltered", "filtered, pruned", "filtered, not pruned", "range", "take", "projection"];

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task EachSegmentIsRequestedOnceAndTheSourceSeesWhatTheStatisticsCount(string query)
    {
        CountingSource source = new CountingSource(new MemoryMappedSegmentSource(await ContractFile.PathAsync()));
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        // What the plan reads to work itself out is not the scan's; the scan starts after it.
        int planned = 0;
        (ScanPlan plan, ScanStatistics statistics) = await RunAsync(file, query, () => planned = source.Ranges.Length);
        SegmentRange[] asked = source.Ranges[planned..];

        Assert.True(asked.Length > 0, $"{query}: the scan asked for nothing, so there is nothing to count");
        Assert.Equal(asked.Length, asked.Distinct().Count());
        Assert.Equal(plan.Segments, statistics.Requests);
        Assert.Equal(statistics.Requests, asked.Length);
        Assert.Equal(statistics.BytesRequested, asked.Sum(range => (long)range.Length));
    }

    /// <summary>
    /// Sorted runs are both a locating index, which the pruning reads, and an exact cover, which
    /// the scan's first step could ask again through another reader: an equality reads them once.
    /// </summary>
    [Fact]
    public async Task AnEqualityOnSortedRunsRequestsEachSegmentOnce()
    {
        string path = await ContractFile.IndexedPathAsync();

        // The scan runs on a file just opened, and the plan on another: an open file keeps the runs
        // it decoded, so a plan asked first would have served the scan's second consult from memory.
        CountingSource source = new CountingSource(new MemoryMappedSegmentSource(path));
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        int opened = source.Ranges.Length;
        Scan<Reading> scan = file.Scan<Reading>().Where(r => r.City == "Lyon");
        long rows = 0;
        await foreach (Columns<Reading> columns in scan.WithCancellation(TestContext.Current.CancellationToken))
        {
            rows += columns.RowCount;
        }

        SegmentRange[] asked = source.Ranges[opened..];
        await using VortexFile planned = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
        ScanPlan plan = await planned.Scan<Reading>().Where(r => r.City == "Lyon").ExplainAsync(TestContext.Current.CancellationToken);
        long expected = 0;
        for (int row = 0; row < ContractFile.Rows; row++)
        {
            expected += ContractFile.Row(row).City == "Lyon" ? 1 : 0;
        }

        Assert.Equal(expected, rows);
        Assert.True(
            plan.Pruning.Any(step => step.Structure == "locating index"),
            "the plan consults no locating index: " + string.Join(", ", plan.Pruning) + "; indexes: "
                + string.Join(", ", (await file.GetIndexesAsync(TestContext.Current.CancellationToken)).Select(index => $"{index.Column} {index.Kind}")));
        Assert.Equal(asked.Length, asked.Distinct().Count());
        Assert.Equal(plan.Segments, scan.Statistics.Requests);
        Assert.Equal(scan.Statistics.Requests, asked.Length);
        Assert.Equal(plan.BytesToRead, scan.Statistics.BytesRequested);
        Assert.Equal(scan.Statistics.BytesRequested, asked.Sum(range => (long)range.Length));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task WhatAScanRequestsIsWhatItsPlanSaid(string query)
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        (ScanPlan plan, ScanStatistics statistics) = await RunAsync(file, query);

        Assert.Equal(plan.Segments, statistics.Requests);
        Assert.Equal(plan.BytesToRead, statistics.BytesRequested);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task APrunedBlockIsNotDecoded(string query)
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        (ScanPlan plan, ScanStatistics statistics) = await RunAsync(file, query);

        Assert.Equal(plan.LiveBlocks, statistics.BlocksDecoded);
        Assert.Equal(plan.Blocks, plan.LiveBlocks + plan.Pruning.Sum(step => step.BlocksPruned));
        Assert.Equal(plan.Blocks - plan.LiveBlocks, statistics.BlocksPruned);
    }

    /// <summary>The pruned query has to prune, and the unpruned one not, or the two say nothing.</summary>
    [Fact]
    public async Task TheQueriesPruneWhatTheyAreNamedFor()
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        (ScanPlan pruned, _) = await RunAsync(file, "filtered, pruned");
        (ScanPlan unpruned, _) = await RunAsync(file, "filtered, not pruned");

        Assert.True(pruned.LiveBlocks > 0 && pruned.LiveBlocks < pruned.Blocks, $"the pruned query keeps {pruned.LiveBlocks} of {pruned.Blocks} blocks");
        Assert.Equal(unpruned.Blocks, unpruned.LiveBlocks);
    }

    [Fact]
    public async Task AQuestionTheStatisticsAnswerReadsNothing()
    {
        CountingSource source = new CountingSource(new MemoryMappedSegmentSource(await ContractFile.PathAsync()));
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        int opened = source.Ranges.Length;

        Scan<Reading> count = file.Scan<Reading>();
        Assert.Equal(ContractFile.Rows, await count.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, count.Statistics.Requests);

        // A filter the file statistics refute: neither its plan nor its count reads a zone map.
        ScanPlan refuted = await file.Scan<Reading>().Where(r => r.Day > 1_000_000).ExplainAsync(TestContext.Current.CancellationToken);
        Assert.False(refuted.MayMatch);
        Assert.Equal(0, refuted.Segments);
        Assert.Equal(0, refuted.LiveBlocks);
        Assert.Equal(refuted.Blocks, refuted.Pruning.Sum(step => step.BlocksPruned));

        Scan<Reading> none = file.Scan<Reading>().Where(r => r.Day > 1_000_000);
        Assert.Equal(0, await none.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, none.Statistics.Requests);

        Scan<Reading> min = file.Scan<Reading>();
        Assert.Equal(0, await min.MinAsync(r => r.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0, min.Statistics.Requests);

        Scan<Reading> max = file.Scan<Reading>();
        Assert.Equal((ContractFile.Rows - 1) / 1_000, await max.MaxAsync(r => r.Day, TestContext.Current.CancellationToken));
        Assert.Equal(0, max.Statistics.Requests);

        Scan<Reading> coldest = file.Scan<Reading>();
        Assert.Equal(10.1, await coldest.MinAsync(r => r.Celsius, TestContext.Current.CancellationToken));
        Assert.Equal(0, coldest.Statistics.Requests);

        Scan<Reading> hottest = file.Scan<Reading>();
        Assert.Equal(49.9, await hottest.MaxAsync(r => r.Celsius, TestContext.Current.CancellationToken));
        Assert.Equal(0, hottest.Statistics.Requests);

        Assert.Equal(opened, source.Ranges.Length);
    }

    [Fact]
    public async Task ADictionaryOrRunEndAggregateDoesNotDecode()
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        // The premise: each column is written in the form the promise is about.
        await foreach (Columns<Reading> columns in file.Scan<Reading>().WithCancellation(TestContext.Current.CancellationToken))
        {
            Assert.Equal(ColumnEncoding.RunEnd, columns.Column<int>(0).Encoding);
            Assert.Equal(ColumnEncoding.Dictionary, columns.Column<string>(2).Encoding);
            break;
        }

        Aggregation<(string, long)> groups = file.Scan<Reading>().GroupBy(r => r.City).AggAsync(g => (g.Key, g.Count()));
        long grouped = 0;
        await foreach ((string, long) group in groups.WithCancellation(TestContext.Current.CancellationToken))
        {
            grouped += group.Item2;
        }

        Assert.Equal(ContractFile.Rows, grouped);
        Assert.True(groups.Statistics.Batches > 0, "the groups were answered without reading a batch");
        Assert.Equal(0, groups.Statistics.BlocksDecoded);

        Scan<Reading> sum = file.Scan<Reading>();
        long expected = 0;
        for (int row = 0; row < ContractFile.Rows; row++)
        {
            expected += ContractFile.Row(row).Day;
        }

        Assert.Equal(expected, await sum.SumAsync(r => r.Day, TestContext.Current.CancellationToken));
        Assert.True(sum.Statistics.Batches > 0, "the sum was answered without reading a batch");
        Assert.Equal(0, sum.Statistics.BlocksDecoded);
    }

    /// <summary>One query over the file: its plan, then what running it did.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="query">The query's name, one of <see cref="Queries"/>.</param>
    /// <param name="planned">Called between the plan and the run, when given.</param>
    private static async Task<(ScanPlan Plan, ScanStatistics Statistics)> RunAsync(VortexFile file, string query, Action? planned = null)
    {
        if (query == "projection")
        {
            Vorticity.Scan tool = file.Scan("City");
            ScanPlan toolPlan = await tool.ExplainAsync(TestContext.Current.CancellationToken);
            planned?.Invoke();
            await foreach (BatchView batch in tool.WithCancellation(TestContext.Current.CancellationToken))
            {
                Assert.Single(batch.Schema);
            }

            return (toolPlan, tool.Statistics);
        }

        Scan<Reading> scan = query switch
        {
            "unfiltered" => file.Scan<Reading>(),
            "filtered, pruned" => file.Scan<Reading>().Where(r => r.Day < 20),
            "filtered, not pruned" => file.Scan<Reading>().Where(r => r.Celsius > 30.0),
            "range" => file.Scan<Reading>().Rows(new RowRange(10_000, 90_000)),
            "take" => file.Scan<Reading>().Rows(5, 70_000, 150_001, 249_999),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query, "no such query"),
        };

        ScanPlan plan = await scan.ExplainAsync(TestContext.Current.CancellationToken);
        planned?.Invoke();
        await foreach (Columns<Reading> columns in scan.WithCancellation(TestContext.Current.CancellationToken))
        {
            Assert.True(columns.RowCount > 0);
        }

        return (plan, scan.Statistics);
    }
}
