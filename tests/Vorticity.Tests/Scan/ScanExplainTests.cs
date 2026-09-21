// Explain and ScanMetrics: the plan without executing, and the same quantities after execution,
// without which nobody can tell whether an index earns its bytes.
//
// Three things are held. `ExplainAsync` reads nothing but the zone maps a filter can use (the
// counting source says how many segments it asked for). Its plan agrees with the mask: as many live
// splits as live blocks on a file whose splits are its blocks, one pruning step for the zone map
// with exactly the blocks it killed, bytes to read below the file's. And the metrics of the scan
// that follows agree with what a caller counts by hand -- rows and batches exactly, requests and
// bytes at least the plan's (a segment is asked for once per batch that needs it, the plan counts
// it once), decoded values equal to the reader's own process-wide counter.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

[Collection(nameof(AllocationCollection))]
public sealed class ScanExplainTests
{
    /// <summary>65536 rows in 64 zones of 1024, over {monotone, banded, strs, nulls, nans}.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task ThePlanOfAPrunedScanReadsOnlyTheZoneMapsAndAgreesWithTheMask()
    {
        Decoders.EnsureRegistered();

        // The narrow band of ZonePruningTests: ~100 rows of a sorted column, most zones dead.
        VortexExpr narrow = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        await using MemoryMappedSegmentSource inner = MemoryMappedSegmentSource.Open(Corpus.Path(Zoned));
        RecordingSegmentSource counting = new RecordingSegmentSource(inner);
        await using VortexFile file = await VortexFile.OpenAsync(
            counting, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

        counting.ResetCounters();
        ScanExplanation plan = await file.ScanBuilder().Project("monotone").Where(narrow).ExplainAsync();
        int askedToExplain = counting.Requested.Count;

        // Nothing but the zone map of the one filtered column, and the plan says what that cost.
        Assert.True(askedToExplain >= 1, "Explain must have read the zone map to build its mask");
        Assert.Single(plan.Pruning);
        Assert.Equal("zone map", plan.Pruning[0].Structure);
        Assert.Equal(askedToExplain, plan.Pruning[0].SegmentsRead);
        Assert.True(plan.Pruning[0].BytesRead > 0);

        Assert.Equal(65_536, plan.RowCount);
        Assert.Equal(1024, plan.BlockRows);
        Assert.Equal(64, plan.Blocks);
        Assert.True(plan.LiveBlocks > 0 && plan.LiveBlocks * 4 < plan.Blocks, $"{plan.LiveBlocks} live blocks of {plan.Blocks}");
        Assert.Equal(64, plan.Splits);
        Assert.Equal(plan.LiveBlocks, plan.LiveSplits);
        Assert.Equal(plan.Blocks - plan.LiveBlocks, plan.Pruning[0].BlocksPruned);
        Assert.Equal(0, plan.RowsSelectedByIndex);
        Assert.True(plan.SegmentsToRead > plan.Pruning[0].SegmentsRead, "the live splits' data segments come on top of the zone map's");
        Assert.True(plan.BytesToRead > 0 && plan.BytesToRead < plan.FileBytes, $"{plan.BytesToRead} of {plan.FileBytes} bytes");
        Assert.True(plan.FileMayMatch);

        // The scan that follows, with its metrics set against what a caller counts by hand.
        ScanMetrics metrics = new ScanMetrics();
        FlatLayoutReader.ValuesDecoded = 0;
        counting.ResetCounters();
        long rows = 0;
        long batches = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("monotone").Where(narrow).WithMetrics(metrics).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            batches++;
        }

        Assert.True(rows > 0);
        Assert.Equal(rows, metrics.Rows);
        Assert.True(metrics.Batches >= batches, "the enumerator produces at least the batches the caller sees");
        Assert.Equal(FlatLayoutReader.ValuesDecoded, metrics.ValuesDecoded);
        Assert.True(metrics.SegmentRequests >= plan.SegmentsToRead, $"{metrics.SegmentRequests} requests against {plan.SegmentsToRead} planned segments");
        Assert.True(metrics.BytesRequested >= plan.BytesToRead);

        // And the asking is what the source saw: every request the scan made went to it.
        Assert.True(
            counting.Requested.Count == metrics.SegmentRequests,
            $"the source saw {counting.Requested.Count} specs ({counting.ReadManyCalls} ReadMany, {counting.ReadCalls} ReadAsync, " +
            $"{counting.ReadRangeCalls} ReadRange); the sink counted {metrics.SegmentRequests} over {metrics.Batches} batches");
    }

    [Fact]
    public async Task ThePlanOfAnUnfilteredScanPrunesNothing()
    {
        Decoders.EnsureRegistered();

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanExplanation plan = await file.ScanBuilder().Project("monotone").ExplainAsync();

        Assert.Equal(plan.Blocks, plan.LiveBlocks);
        Assert.Equal(plan.Splits, plan.LiveSplits);
        Assert.Empty(plan.Pruning);
        Assert.True(plan.FileMayMatch);
        Assert.True(plan.BytesToRead > 0 && plan.BytesToRead <= plan.FileBytes);

        // Narrowed to a range, the plan narrows with it.
        ScanExplanation half = await file.ScanBuilder().Project("monotone").Rows(new RowRange(0, 32_768)).ExplainAsync();
        Assert.Equal(32_768, half.RowCount);
        Assert.True(half.LiveSplits < plan.LiveSplits);
        Assert.True(half.BytesToRead < plan.BytesToRead);
    }
}
