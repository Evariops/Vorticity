using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A key's function of a column the zones give: on a reading a second from sixteen sources over one hour, sorted by the source
/// then the instant, every zone of 256 rows lies within the hour, so that a count by the source and the hour under a filter
/// on another column takes the hour from the zones and reads no instant; in splits of a zone and of four, which give each
/// zone's least instant over its rows, on one lane and on several, with the answers .NET gives.
/// </summary>
/// <remarks>
/// A file's chunk is read whole: a zone gives its value only when every zone of its chunk does, and the dataset's objects,
/// read by their granules, take it zone by zone (<c>DatasetBlockAnswerTests.AnHoursGranulesTakeTheHourFromTheirZones</c>).
/// </remarks>
public sealed partial class ZoneKeyReadsTests
{
    private const int Sources = 16;
    private const int PerSource = 3_600;
    private const int Block = 256;

    private static readonly DateTime Start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AnHoursZonesGiveTheHourAndNoInstantIsRead()
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            (string Source, DateTime Hour, long Count)[] expected = [.. rows.Where(r => r.Level > 0)
                .GroupBy(r => (r.Source, Hour(r.At))).Select(g => (g.Key.Source, g.Key.Item2, (long)g.Count()))
                .OrderBy(g => g.Source, StringComparer.Ordinal).ThenBy(g => g.Item2)];

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Scan<Reading> keyed = file.Scan<Reading>().Where(r => r.Level > 0);
            Assert.Equal(expected, Sorted(await HoursAsync(keyed)));

            // The same groups with the instants' latest, which reads every instant.
            Scan<Reading> whole = file.Scan<Reading>().Where(r => r.Level > 0);
            List<SourceHourLast> lasts = await ListAsync(whole.GroupBy(r => (r.Source, r.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Source, g.Key.Item2, g.Count(), g.Max(r => r.At))).As<SourceHourLast>());
            Assert.Equal(expected, Sorted(lasts.Select(l => new SourceHour(l.Source, l.Hour, l.Count))));
            Assert.True(
                keyed.Metrics.BytesRequested < whole.Metrics.BytesRequested,
                $"{keyed.Metrics.BytesRequested} bytes read for the hours, {whole.Metrics.BytesRequested} with the instants");

            // Splits of four zones: runs of four least instants, one hour.
            Scan<Reading> wide = file.Scan<Reading>().With(new ScanOptions { BatchRows = 4 * Block }).Where(r => r.Level > 0);
            Assert.Equal(expected, Sorted(await HoursAsync(wide)));
            Assert.True(wide.Metrics.BytesRequested < whole.Metrics.BytesRequested, $"{wide.Metrics.BytesRequested} bytes read in wide splits");

            // On several lanes, the same answers, the instants unread.
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile shared = await session.OpenAsync(path, cancellationToken: Ct);
            Scan<Reading> lanes = shared.Scan<Reading>().Where(r => r.Level > 0);
            Assert.Equal(expected, Sorted(await HoursAsync(lanes)));
            Assert.True(lanes.Metrics.BytesRequested < whole.Metrics.BytesRequested, $"{lanes.Metrics.BytesRequested} bytes read on four lanes");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Hour(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private static Task<List<SourceHour>> HoursAsync(Scan<Reading> scan) =>
        ListAsync(scan.GroupBy(r => (r.Source, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Source, g.Key.Item2, g.Count())).As<SourceHour>());

    private static (string Source, DateTime Hour, long Count)[] Sorted(IEnumerable<SourceHour> hours) =>
        [.. hours.Select(h => (h.Source, new DateTime(h.Hour.Ticks, DateTimeKind.Utc), h.Count))
            .OrderBy(h => h.Source, StringComparer.Ordinal).ThenBy(h => h.Item2)];

    /// <summary>A reading a second from each source over one hour, sorted by the source then the instant.</summary>
    private static Reading[] Readings()
    {
        Random random = new Random(17);
        Reading[] rows = new Reading[Sources * PerSource];
        for (int source = 0; source < Sources; source++)
        {
            for (int second = 0; second < PerSource; second++)
            {
                rows[(source * PerSource) + second] = new Reading($"source-{source}", Start.AddSeconds(second), random.Next(-500, 500));
            }
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Reading[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"zone-keys-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, new VortexWriteOptions { RowBlockSize = Block });
        await writer.WriteAsync<Reading>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
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

    [VortexRecord]
    public partial record struct Reading(string Source, DateTime At, int Level);

    [VortexRecord]
    public partial record struct SourceHour(string Source, DateTime Hour, long Count);

    [VortexRecord]
    public partial record struct SourceHourLast(string Source, DateTime Hour, long Count, DateTime? Last);
}
