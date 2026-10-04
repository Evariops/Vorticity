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
/// A group by on a key the statistics say is sorted: the groups go out as they close, in key order,
/// the null group last, the same at every degree as LINQ over the rows; it holds the groups still
/// open whatever the number it delivers, answers before the end of the file, and stops reading
/// once a Take is served.
/// </summary>
public sealed partial class StreamingGroupTests
{
    private const int Rows = 300_000;

    // The nullable sorted key's nulls come first: the order a sorted column holds them in.
    private const int Nulls = 700;

    private static readonly string[] Desks = ["rates", "fx", "credit", "equity", "commodities"];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task GroupsGoOutAsTheyCloseInKeyOrderTheNullGroupLast(int degree)
    {
        (Tick[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation byHour = file.Scan<Tick>().GroupBy(r => r.Hour).Select(g => (g.Key, g.Count(), g.Average(x => x.Price)));
            List<HourStats> hours = await ListAsync(byHour.As<HourStats>());
            List<HourStats> expected = [.. rows.GroupBy(r => r.Hour)
                .OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Key)
                .Select(g => new HourStats(g.Key, g.Count(), g.Average(r => r.Price)))];
            Assert.Equal(expected.Count, hours.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Hour, hours[i].Hour);
                Assert.Equal(expected[i].Count, hours[i].Count);
                Assert.Equal(expected[i].Mean!.Value, hours[i].Mean!.Value, 9);
            }

            // The text key, sorted too.
            Assert.Equal(
                rows.GroupBy(r => r.Name).Select(g => g.Key).Order(StringComparer.Ordinal),
                await file.Scan<Tick>().GroupBy(r => r.Name).Select(g => g.Key).ToListAsync(Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ItHoldsTheOpenGroupsWhateverTheNumberItDelivers()
    {
        (Tick[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // A key of a group every three rows, against one of a group per hour: the most groups
            // held is a batch's, not the file's.
            Aggregation<long> fine = file.Scan<Tick>().GroupBy(r => r.Second).Select(g => g.Count());
            Assert.Equal(rows.Select(r => r.Second).Distinct().Count(), (await fine.ToListAsync(Ct)).Count);
            long peak = ((AggregationQuery)fine.Query).PeakGroups;
            Assert.True(peak <= 65_536 + 2, $"the streaming group by held {peak} groups at once");
            Assert.True(peak * 3 < rows.Select(r => r.Second).Distinct().Count(), $"the streaming group by held {peak} groups at once");

            // The same groups by an unsorted key are held all at once.
            Aggregation<long> held = file.Scan<Tick>().GroupBy(r => r.Shuffled).Select(g => g.Count());
            Assert.Equal(rows.Select(r => r.Shuffled).Distinct().Count(), (await held.ToListAsync(Ct)).Count);
            Assert.Equal(rows.Select(r => r.Shuffled).Distinct().Count(), ((AggregationQuery)held.Query).PeakGroups);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ItAnswersBeforeTheEndAndATakeStopsTheRead()
    {
        (Tick[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Aggregation<long> all = file.Scan<Tick>().GroupBy(r => r.Hour).Select(g => g.Count());
            await all.ToListAsync(Ct);
            long batches = all.Statistics.Batches;

            // The first group closes with the first batch: the answer does not wait for the file.
            Aggregation<long> first = file.Scan<Tick>().GroupBy(r => r.Hour).Select(g => g.Count());
            await using (IAsyncEnumerator<long> counts = first.GetAsyncEnumerator(Ct))
            {
                Assert.True(await counts.MoveNextAsync());
                Assert.True(batches >= 4 && first.Statistics.Batches <= 2, $"{first.Statistics.Batches} batches read for the first group of {batches}");
            }

            Aggregation<long> three = file.Scan<Tick>().GroupBy(r => r.Hour).Take(3).Select(g => g.Count());
            Assert.Equal(rows.Where(r => r.Hour is not null).GroupBy(r => r.Hour).OrderBy(g => g.Key).Take(3).Select(g => (long)g.Count()), await three.ToListAsync(Ct));
            Assert.True(three.Statistics.Batches <= 2, $"{three.Statistics.Batches} batches read for three groups of {batches}");

            // A filter and an order on the key stream with it; the order the other way does not, and agrees.
            List<int?> kept = await file.Scan<Tick>().GroupBy(r => r.Hour).Where(g => g.Count() > 3_000).OrderBy(g => g.Key).Skip(2).Take(4).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(rows.Where(r => r.Hour is not null).GroupBy(r => r.Hour).Where(g => g.Count() > 3_000).OrderBy(g => g.Key).Skip(2).Take(4).Select(g => g.Key), kept);
            List<int?> descending = await file.Scan<Tick>().GroupBy(r => r.Hour).OrderByDescending(g => g.Key).Take(3).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(rows.Where(r => r.Hour is not null).Select(r => r.Hour).Distinct().OrderDescending().Take(3), descending);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ACompositeKeyStreamsOnItsSortedComponent(int degree)
    {
        (Tick[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<HourDesk> expected = [.. rows.GroupBy(r => (r.Hour, r.Desk))
                .OrderBy(g => g.Key.Hour is null ? 1 : 0).ThenBy(g => g.Key.Hour).ThenBy(g => g.Key.Desk, StringComparer.Ordinal)
                .Select(g => new HourDesk(g.Key.Hour, g.Key.Desk, g.Count()))];

            // In key order when asked: the hours as they stream, the desks of an hour sorted with it.
            Vorticity.Aggregation ordered = file.Scan<Tick>().GroupBy(r => (r.Hour, r.Desk)).OrderBy(g => g.Key).Select(g => (g.Key.Hour, g.Key.Desk, g.Count()));
            Assert.Equal(expected, await ListAsync(ordered.As<HourDesk>()));
            Assert.True(((AggregationQuery)ordered.Query).PeakGroups < expected.Count / 4, $"the streaming group by held {((AggregationQuery)ordered.Query).PeakGroups} groups of {expected.Count}");

            // Without an order, the hours still in order, the desks of each as they were met.
            List<HourDesk> met = await ListAsync(file.Scan<Tick>().GroupBy(r => (r.Hour, r.Desk)).Select(g => (g.Key.Hour, g.Key.Desk, g.Count())).As<HourDesk>());
            Assert.Equal(expected.Select(g => g.Hour), met.Select(g => g.Hour));
            Assert.Equal(expected.OrderBy(g => g.Hour).ThenBy(g => g.Desk, StringComparer.Ordinal), met.OrderBy(g => g.Hour).ThenBy(g => g.Desk, StringComparer.Ordinal));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ADistinctOnASortedColumnHoldsABatchOfValues()
    {
        (Tick[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Aggregation<int> seconds = file.Scan<Tick>().Select(r => r.Second).Distinct();
            List<int> expected = [.. rows.Select(r => r.Second).Distinct()];
            Assert.Equal(expected, await seconds.ToListAsync(Ct));
            long peak = ((DistinctQuery)seconds.Query).PeakValues;
            Assert.True(peak * 3 < expected.Count, $"the distinct held {peak} values of {expected.Count}");

            // Its nulls first, then the values in order; the null comes once.
            Assert.Equal(rows.Select(r => r.Hour).Distinct(), await file.Scan<Tick>().Select(r => r.Hour).Distinct().ToListAsync(Ct));

            // A column that does not stream holds every value it met.
            Aggregation<int> shuffled = file.Scan<Tick>().Select(r => r.Shuffled).Distinct();
            Assert.Equal(rows.Select(r => r.Shuffled).Distinct().Count(), (await shuffled.ToListAsync(Ct)).Count);
            Assert.Equal(rows.Select(r => r.Shuffled).Distinct().Count(), ((DistinctQuery)shuffled.Query).PeakValues);
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

    private static async Task<(Tick[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "streaming-groups");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"ticks-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Tick[] rows = new Tick[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Tick(
                row < Nulls ? null : row / 3_600,
                row / 3,
                $"n{row / 20_000:D3}",
                row % 50 + (row % 7 / 10.0),
                (int)((row * 7_919L) % 100_000),
                Desks[row % 7 % Desks.Length]);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Tick>(path))
        {
            await writer.WriteAsync<Tick>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Tick(int? Hour, int Second, string Name, double Price, int Shuffled, string Desk);

    [VortexRecord]
    public partial record struct HourDesk(int? Hour, string Desk, long Count);

    [VortexRecord]
    public partial record struct HourStats(int? Hour, long Count, double? Mean);
}
