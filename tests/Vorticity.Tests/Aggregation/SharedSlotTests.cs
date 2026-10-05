using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A mean shares the slot of the sum of its column and its rows: the column is folded once for
/// both, and the answers are those of a mean of its own, filtered, compared after the group by, and
/// in a group by that streams.
/// </summary>
public sealed partial class SharedSlotTests
{
    private const int Rows = 30_000;

    [Fact]
    public async Task AMeanReadsTheSlotOfTheSumOfItsColumn()
    {
        Sample[] rows = Samples();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation query = file.Scan<Sample>()
                .GroupBy(r => r.Hour)
                .Where(g => g.Average(x => x.Load) > 40.0)
                .Select(g => (
                    g.Key,
                    g.Sum(x => x.Load),
                    g.Average(x => x.Load),
                    g.Sum(x => x.Price),
                    g.Average(x => x.Price),
                    g.Where(x => x.Load > 50).Sum(x => x.Load),
                    g.Where(x => x.Load > 50).Average(x => x.Load),
                    g.Average(x => x.Count)));

            // Seven aggregates written, four folded: each mean of a summed column reads the sum's
            // slot, the mean of a column nothing sums keeps its own.
            Assert.Equal(4, ((AggregationQuery)query.Query).Plan.Aggregates.Length);

            List<Shared> hours = await ListAsync(query.As<Shared>());
            List<Shared> expected = [.. rows.GroupBy(r => r.Hour).OrderBy(g => g.Key)
                .Select(g =>
                {
                    Sample[] of = [.. g];
                    Sample[] high = [.. of.Where(r => r.Load > 50)];
                    return new Shared(
                        g.Key, of.Sum(r => (long)r.Load), of.Average(r => r.Load), of.Sum(r => r.Price), of.Average(r => r.Price),
                        high.Sum(r => (long)r.Load), high.Length == 0 ? null : high.Average(r => r.Load), of.Average(r => r.Count));
                })
                .Where(g => g.LoadMean > 40.0)];
            Assert.Equal(expected.Count, hours.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Hour, hours[i].Hour);
                Assert.Equal(expected[i].Load, hours[i].Load);
                Assert.Equal(expected[i].LoadMean!.Value, hours[i].LoadMean!.Value, 9);
                Assert.Equal(expected[i].Price, hours[i].Price, 6);
                Assert.Equal(expected[i].PriceMean!.Value, hours[i].PriceMean!.Value, 9);
                Assert.Equal(expected[i].HighLoad, hours[i].HighLoad);
                Assert.Equal(expected[i].HighMean, hours[i].HighMean);
                Assert.Equal(expected[i].CountMean!.Value, hours[i].CountMean!.Value, 9);
            }

            // The hours are sorted: the group by streams, the shared mean with it.
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)query.Query) >= 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static Sample[] Samples()
    {
        Random random = new Random(5);
        Sample[] rows = new Sample[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Sample(row / 1_000, random.Next(0, 100), Math.Round(random.NextDouble() * 1_000, 3), random.Next(1, 9));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Sample[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shared-slots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"samples-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Sample>(path))
        {
            await writer.WriteAsync<Sample>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Sample(int Hour, int Load, double Price, int Count);

    [VortexRecord]
    public partial record struct Shared(int Hour, long Load, double? LoadMean, double Price, double? PriceMean, long HighLoad, double? HighMean, double? CountMean);
}
