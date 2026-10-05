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
/// A group by on a key the statistics do not say is sorted, but whose zones nearly are: a group whose
/// key lies below the smallest key the zones still to read hold is final, so the groups go out in key
/// order as the read goes, holding those the zones overlap, at every degree; shuffled, the same query
/// holds every group to the end, as before, and answers the same.
/// </summary>
public sealed partial class ZoneFinalityTests
{
    private const int Rows = 400_000;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ANearlySortedKeyStreamsItsGroupsAsTheZonesProveThemFinal(int degree)
    {
        Event[] rows = Events(shuffled: false);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<AtTotal> expected = Expected(rows);

            // In batches of a zone, so that what a batch adds stays below the overlap's groups.
            Vorticity.Aggregation byTime = file.Scan<Event>().With(new ScanOptions { BatchRows = 8_192 })
                .GroupBy(r => r.At).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            AggregationQuery query = (AggregationQuery)byTime.Query;
            Assert.Equal(-1, StreamingGroupBatches.Streaming(query));
            Assert.True(ZoneFinality.Candidate(query));
            Assert.Equal(expected, await ListAsync(byTime.As<AtTotal>()));
            // The groups the zones overlap, and those a batch adds, or on several lanes a range of
            // the file's chunks: a part of the groups where the blocking pass holds them all.
            Assert.True(query.PeakGroups < expected.Count / 2, $"{query.PeakGroups} groups held of {expected.Count}");

            // The first five, in key order: the read stops once they are out.
            Scan<Event> scan = file.Scan<Event>();
            List<AtTotal> five = await ListAsync(scan.GroupBy(r => r.At).OrderBy(g => g.Key).Take(5).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value))).As<AtTotal>());
            Assert.Equal(expected.Take(5), five);

            // On one lane; on four, this file's few chunks are all in flight before the first range
            // is followed.
            Assert.True(degree > 1 || scan.Statistics.Rows < Rows / 2, $"{scan.Statistics.Rows} rows read for five groups");

            // Down, the key's zones prove nothing: every group held, the same answers.
            List<AtTotal> down = await ListAsync(file.Scan<Event>().GroupBy(r => r.At).OrderByDescending(g => g.Key).Take(3).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value))).As<AtTotal>());
            Assert.Equal(expected.Where(e => e.At is not null).Reverse().Take(3), down);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AShuffledKeyHoldsEveryGroupAndAnswersTheSame()
    {
        Event[] rows = Events(shuffled: true);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<AtTotal> expected = Expected(rows);
            Vorticity.Aggregation byTime = file.Scan<Event>().GroupBy(r => r.At).OrderBy(g => g.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            Assert.Equal(expected, await ListAsync(byTime.As<AtTotal>()));
            Assert.Equal(expected.Count, ((AggregationQuery)byTime.Query).PeakGroups);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What LINQ makes of the rows: the groups by key, the null group last.</summary>
    private static List<AtTotal> Expected(Event[] rows) =>
        [.. rows.GroupBy(r => r.At).OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Key).Select(g => new AtTotal(g.Key, g.Count(), g.Sum(r => r.Value)))];

    /// <summary>
    /// Instants a row's place sets, each up to 2 500 late, one row in a thousand without one; or the
    /// same values in no order at all.
    /// </summary>
    private static Event[] Events(bool shuffled)
    {
        Event[] rows = new Event[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E3779B97F4A7C15UL;
            rows[row] = new Event(row % 1_000 == 999 ? null : (row / 4) + (long)((mix >> 40) % 2_500), row % 97);
        }

        if (shuffled)
        {
            new Random(5).Shuffle(rows);
        }

        return rows;
    }

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

    private static async Task<string> WriteAsync(Event[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "zone-finality");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"events-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Event>(path))
        {
            await writer.WriteAsync<Event>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Event(long? At, long Value);

    [VortexRecord]
    public partial record struct AtTotal(long? At, long Count, long Sum);
}
