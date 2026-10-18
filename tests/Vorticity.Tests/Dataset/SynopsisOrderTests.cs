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
    public void ASliceTableKeepsItsOrderAndReadsTheFirstVersion()
    {
        SliceTable table = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 60, 0, 10), new TimeSlice(60, 120, 10, 5)], [3, 1, 4]);
        SliceTable read = SliceTable.FromBytes(table.ToBytes())!;
        Assert.Equal([3, 1, 4], read.Order);
        Assert.Equal(table.Slices, read.Slices);

        // A table of the first version, with no order: rows in time order.
        byte[] first = new SliceTable(TimeUnit.Microseconds, table.Slices).ToBytes();
        first[0] = 1;
        SliceTable old = SliceTable.FromBytes(first[..^1])!;
        Assert.Empty(old.Order);
        Assert.Equal(table.Slices, old.Slices);
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
