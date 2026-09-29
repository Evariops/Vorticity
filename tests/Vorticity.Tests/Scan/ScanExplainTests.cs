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
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
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
        CancellationToken ct = TestContext.Current.CancellationToken;
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
        ScanExplanation plan = await file.ScanBuilder().Project("monotone").Where(narrow).ExplainAsync(ct);
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
        Assert.True(metrics.ValuesDecoded >= rows, "every row delivered was decoded");

        // The zone map the plan read stays with the open file, so the scan asks for the data alone.
        long data = plan.SegmentsToRead - plan.Pruning[0].SegmentsRead;
        Assert.True(metrics.SegmentRequests >= data, $"{metrics.SegmentRequests} requests against {data} planned data segments");
        Assert.True(metrics.BytesRequested >= plan.BytesToRead - plan.Pruning[0].BytesRead);

        // And the asking is what the source saw: every request the scan made went to it.
        Assert.True(
            counting.Requested.Count == metrics.SegmentRequests,
            $"the source saw {counting.Requested.Count} specs ({counting.ReadManyCalls} ReadMany, {counting.ReadCalls} ReadAsync, " +
            $"{counting.ReadRangeCalls} ReadRange); the sink counted {metrics.SegmentRequests} over {metrics.Batches} batches");
    }

    [Fact]
    public async Task TheZoneMapsOfAWideFileAreEachReadOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();

        // More columns than a file keeps maps for in its short list, so that the maps kept first
        // move to the map by node, and must all be found there.
        const int Columns = 12;
        const int Rows = 4_096;
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        CanonicalArena arena = new CanonicalArena();
        int[] columns = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = "c" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[c] = i64;
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = ((long)c * Rows) + row;
            }

            columns[c] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, columns);
        System.IO.MemoryStream written = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(written), schema, new VortexWriteOptions { RowBlockSize = 1_024 }))
        {
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await using VortexFile file = await VortexFile.OpenAsync(new MemorySegmentSource(written.ToArray()), new VortexOpenOptions(), ct);
        for (int round = 0; round < 2; round++)
        {
            for (int c = 0; c < Columns; c++)
            {
                // The first round reads each column's zone map, the second finds each one kept.
                VortexExpr equal = Expr.Eq(Expr.Field(names[c]), Expr.Literal(FilterLiteral.From(((long)c * Rows) + 1_500)));
                ScanExplanation plan = await file.ScanBuilder().Where(equal).ExplainAsync(ct);
                Assert.Equal(1, plan.LiveBlocks);
                Assert.Equal("zone map", plan.Pruning[0].Structure);
                Assert.True(
                    round == 0 ? plan.Pruning[0].SegmentsRead > 0 : plan.Pruning[0].SegmentsRead == 0,
                    $"round {round}, column {c}: {plan.Pruning[0].SegmentsRead} zone map segments read");
            }
        }
    }

    /// <summary>
    /// Scans keeping zone maps at once, across the short list's move to the map by node, lose
    /// none: each node is kept by one thread alone, so a keep lost to a race is a node missing,
    /// and every node is found afterwards with its thread's map.
    /// </summary>
    [Fact]
    public async Task ZoneMapsKeptAtOnceAcrossTheMoveToTheMapAreAllFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Nodes = 64;
        const int Keepers = 8;
        for (int round = 0; round < 200; round++)
        {
            await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), ct);
            ZoneColumn[] columns = new ZoneColumn[Keepers];
            for (int k = 0; k < Keepers; k++)
            {
                columns[k] = new ZoneColumn(Expr.Field("monotone"), 1_024, 1_024, []);
            }

            // Threads of their own rather than the pool's, so that all of them wait at the start
            // together however few pool threads there are.
            using Barrier start = new Barrier(Keepers);
            Thread[] keepers = new Thread[Keepers];
            for (int k = 0; k < Keepers; k++)
            {
                int keeper = k;
                keepers[k] = new Thread(() =>
                {
                    start.SignalAndWait(ct);
                    for (int node = keeper; node < Nodes; node += Keepers)
                    {
                        file.KeepZones(node, columns[keeper]);
                    }
                });
                keepers[k].Start();
            }

            foreach (Thread keeper in keepers)
            {
                keeper.Join();
            }

            for (int node = 0; node < Nodes; node++)
            {
                Assert.Same(columns[node % Keepers], file.DecodedZones(node));
            }
        }
    }

    [Fact]
    public async Task ThePlanOfAnUnfilteredScanPrunesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanExplanation plan = await file.ScanBuilder().Project("monotone").ExplainAsync(ct);

        Assert.Equal(plan.Blocks, plan.LiveBlocks);
        Assert.Equal(plan.Splits, plan.LiveSplits);
        Assert.Empty(plan.Pruning);
        Assert.True(plan.FileMayMatch);
        Assert.True(plan.BytesToRead > 0 && plan.BytesToRead <= plan.FileBytes);

        // Narrowed to a range, the plan narrows with it.
        ScanExplanation half = await file.ScanBuilder().Project("monotone").Rows(new RowRange(0, 32_768)).ExplainAsync(ct);
        Assert.Equal(32_768, half.RowCount);
        Assert.True(half.LiveSplits < plan.LiveSplits);
        Assert.True(half.BytesToRead < plan.BytesToRead);
    }
}
