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
/// A group by on several lanes cuts the rows its structures leave alive, not the file's, and hands
/// them to its lanes from a queue: under a filter that keeps a tenth of a sorted key's rows, every
/// lane groups some of them, and the answers are .NET's whatever lane took which range.
/// </summary>
public sealed partial class GroupQueueTests
{
    private const int Rows = 65_536;

    [Fact]
    public async Task UnderASelectiveFilterEveryLaneGroupsLiveRows()
    {
        Visit[] rows = [.. Enumerable.Range(0, Rows).Select(row => new Visit(row / 64, Cities[row % Cities.Length], row % 97))];
        string path = Path.Combine(Path.GetTempPath(), $"group-queue-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            // Chunks of a block: ranges are cut where chunks end, which a chunk's segments are read by.
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path, new VortexWriteOptions { RowBlockSize = 1_024, ChunkTargetBytes = 1 << 12 }))
            {
                await writer.WriteAsync<Visit>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // A tenth of the days, contiguous: six blocks of sixty-four, the rows of one lane of four
            // had the file been cut in four.
            int from = 500;
            int until = 600;
            Vorticity.Aggregation byCity = file.Scan<Visit>()
                .Where(v => v.Day >= from & v.Day < until)
                .GroupBy(v => v.City)
                .Select(g => (g.Key, g.Count(), g.Sum(v => v.Pages)));
            List<CityPages> cities = [.. (await ListAsync(byCity.As<CityPages>())).OrderBy(c => c.City, StringComparer.Ordinal)];
            Assert.Equal(
                rows.Where(v => v.Day >= from && v.Day < until).GroupBy(v => v.City).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new CityPages(g.Key, g.Count(), g.Sum(v => (long)v.Pages))),
                cities);

            // The live chunks cut into ranges, one or two blocks each, rather than the file in four:
            // which lane takes which is the queue's, and a lane may finish before another starts.
            AggregationRun run = ((AggregationQuery)byCity.Query).Plan.LastRun!;
            Assert.Equal(4, run.Lanes.Length);
            Assert.True(run.Lanes.Sum(lane => lane.Ranges) >= 6, $"{run.Lanes.Sum(lane => lane.Ranges)} ranges");
            Assert.All(run.Lanes, lane => Assert.True(lane.Ranges == 0 || lane.Groups > 0, $"a lane of {lane.Ranges} ranges and {lane.Groups} groups"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Lille"];

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

    [VortexRecord]
    public partial record struct Visit(int Day, string City, int Pages);

    [VortexRecord]
    public partial record struct CityPages(string City, long Count, long Pages);
}
