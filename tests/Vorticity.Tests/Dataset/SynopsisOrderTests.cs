// The order inside a slice: the synopsis columns, those whose values repeat, by their distinct values,
// fewest first, the schema's order breaking ties; never the instant nor an identifier; the same order
// for the same rows; and the slice table that records it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisOrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheOrderTakesFewestValuesFirstAndColumnsThatRepeat()
    {
        // 1 000 rows: a column of 600 values repeats less than twice on average and is left out.
        int[] order = SynopsisOrder.Choose([(1, 500), (2, 5), (3, 600), (4, 5), (5, 40), (6, 0)], rows: 1_000);
        Assert.Equal([2, 4, 5, 1], order);
    }

    /// <summary>
    /// A route of three a service, nested in its service of ten, beside a method of twenty: the order by
    /// distinct values puts the method second, the order by runs added the route, which adds 30 runs where
    /// the method adds 200, and so 640 runs in all against 810. Independent columns keep the order of their
    /// distinct values, and a sample of the rows gives the order every row gives.
    /// </summary>
    [Fact]
    public void TheOrderByRunsAddedPutsANestedColumnAfterItsParent()
    {
        const int rows = 60_000;
        Random random = new Random(3);
        int[] service = [.. Enumerable.Range(0, rows).Select(_ => random.Next(10))];
        int[] method = [.. Enumerable.Range(0, rows).Select(_ => random.Next(20))];
        int[] route = [.. service.Select(s => (s * 3) + random.Next(3))];
        (int[] Codes, int Distinct)[] columns = [(service, 10), (method, 20), (route, 30)];

        Assert.Equal([0, 2, 1], SliceSort.Greedy(columns, rows));
        Assert.Equal([0, 2, 1], SliceSort.Greedy(columns, rows, stride: 16));
        Assert.Equal(640, SliceSort.Runs([service, route, method], rows));
        Assert.Equal(810, SliceSort.Runs([service, method, route], rows));

        int[] region = [.. Enumerable.Range(0, rows).Select(_ => random.Next(5))];
        Assert.Equal([1, 0, 2], SliceSort.Greedy([(method, 20), (region, 5), (route, 30)], rows));
    }

    /// <summary>The same rows counted twice give the same order: status, region, host, never the id nor the instant.</summary>
    [Fact]
    public async Task TheSameRowsGiveTheSameOrder()
    {
        Random random = new Random(3);
        DateTimeOffset noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Span[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Span(
            noon.AddMilliseconds(i * 10), $"req-{i}", 200 + (random.Next(5) * 100), $"region-{random.Next(3)}", $"host-{random.Next(4_000)}", i % 7 == 0))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-synopsis-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Span>(path))
            {
                await writer.WriteAsync<Span>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            DType schema = file.DType;
            ClusteringKey key = ClusteringKey.Declared([nameof(Span.At)], schema, acceptsNullable: false)!;
            int[] candidates = SynopsisOrder.Candidates(schema, key, [nameof(Span.RequestId)]);
            Assert.DoesNotContain(schema.IndexOfField(nameof(Span.At)), candidates);
            Assert.DoesNotContain(schema.IndexOfField(nameof(Span.RequestId)), candidates);

            int[] first = await OrderAsync(file, candidates, rows.Length);
            int[] second = await OrderAsync(file, candidates, rows.Length);
            Assert.Equal(first, second);
            Assert.Equal(
                [schema.IndexOfField(nameof(Span.Error)), schema.IndexOfField(nameof(Span.Region)), schema.IndexOfField(nameof(Span.Status)), schema.IndexOfField(nameof(Span.Host))],
                first);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ASliceTableKeepsItsOrderAndRefusesAnEarlierVersion()
    {
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [new TimeSlice(0, 60, 0, 10), new TimeSlice(60, 120, 10, 5)],
            [3, -2, SynopsisPath.Entry(0), 1],
            [
                new SynopsisPath(1, "http.route", Vorticity.Types.Variants.VariantClass.String),
                new SynopsisPath(1, "duration", Vorticity.Types.Variants.VariantClass.Double, Floats: true),
                new SynopsisPath(1, "retries", Vorticity.Types.Variants.VariantClass.Double, Residual: true, Floats: true),
            ]);
        SliceTable read = SliceTable.FromBytes(table.ToBytes())!;
        Assert.Equal([3, -2, SynopsisPath.Entry(0), 1], read.Order);
        Assert.Equal(table.Slices, read.Slices);
        Assert.Equal(table.Paths, read.Paths);

        // A table of a version before the one written, which no store holds, does not decode.
        byte[] earlier = table.ToBytes();
        earlier[0] = 3;
        Assert.Null(SliceTable.FromBytes(earlier));
    }

    private static async Task<int[]> OrderAsync(VortexFile file, int[] candidates, long rows)
    {
        CardinalitySketch[] sketches = [.. candidates.Select(_ => new CardinalitySketch())];
        await foreach (RecordBatch batch in file.Scan<Span>().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                SynopsisOrder.Count(batch, candidates, sketches);
            }
        }

        return SynopsisOrder.Choose([.. candidates.Select((field, i) => (field, sketches[i].Estimate()))], rows);
    }

    [VortexRecord]
    public partial record struct Span(DateTimeOffset At, string RequestId, int Status, string Region, string Host, bool Error);
}
