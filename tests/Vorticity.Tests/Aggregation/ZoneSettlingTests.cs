using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// Groups the zone maps settle: on minutes sorted by time, a day's blocks between two midnights hold
/// one key through <c>Truncate</c>, and their counts and extremes come from the zone maps, so that a
/// group by reads the blocks that straddle midnight and nothing else -- streaming, blocking, on
/// several lanes and under a filter that keeps the blocks whole -- with the answers .NET gives.
/// </summary>
public sealed partial class ZoneSettlingTests
{
    private const int Rows = 14_400;
    private const int Block = 256;

    private static readonly DateTime Start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(-333);

    [Fact]
    public async Task ADaysBlocksBetweenTwoMidnightsAreReadFromTheirZones()
    {
        Tick[] rows = Ticks();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            int straddling = Straddling(rows, t => Day(t.At));
            DayRange[] expected = [.. rows.GroupBy(t => Day(t.At)).Select(g => new DayRange(g.Key, g.Count(), g.Min(t => t.Level), g.Max(t => t.At), g.Max(t => t.Price)))];

            // Streaming: the instants are sorted, so are their days.
            Scan<Tick> streamed = file.Scan<Tick>();
            List<DayRange> days = await ListAsync(streamed.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count(), g.Min(t => t.Level), g.Max(t => t.At), g.Max(t => t.Price))).As<DayRange>());
            Assert.Equal(expected, days.Select(Utc));
            Assert.Equal(straddling, streamed.Metrics.BlocksDecoded);

            // Blocking, in the reverse order of the days, and on several lanes.
            Scan<Tick> blocking = file.Scan<Tick>();
            days = await ListAsync(blocking.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).OrderByDescending(g => g.Key).Select(g => (g.Key, g.Count(), g.Min(t => t.Level), g.Max(t => t.At), g.Max(t => t.Price))).As<DayRange>());
            Assert.Equal(expected.Reverse(), days.Select(Utc));
            Assert.Equal(straddling, blocking.Metrics.BlocksDecoded);

            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile shared = await session.OpenAsync(path, cancellationToken: Ct);
            Scan<Tick> lanes = shared.Scan<Tick>();
            days = await ListAsync(lanes.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).OrderByDescending(g => g.Key).Select(g => (g.Key, g.Count(), g.Min(t => t.Level), g.Max(t => t.At), g.Max(t => t.Price))).As<DayRange>());
            Assert.Equal(expected.Reverse(), days.Select(Utc));
            Assert.Equal(straddling, lanes.Metrics.BlocksDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task OnlyAFilterThatKeepsTheBlocksWholeLeavesThemToTheZones()
    {
        Tick[] rows = Ticks();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            int straddling = Straddling(rows, t => Day(t.At));

            // Every level is at least -500, which every zone proves: the blocks settle as before.
            Scan<Tick> whole = file.Scan<Tick>().Where(t => t.Level >= -500);
            List<DayCount> days = await ListAsync(whole.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())).As<DayCount>());
            Assert.Equal(rows.GroupBy(t => Day(t.At)).Select(g => (g.Key, (long)g.Count())), days.Select(d => (new DateTime(d.Day.Ticks, DateTimeKind.Utc), d.Count)));
            Assert.Equal(straddling, whole.Metrics.BlocksDecoded);

            // A level above 0 is in every zone and not all of one: every block is read and filtered.
            Scan<Tick> some = file.Scan<Tick>().Where(t => t.Level > 0);
            days = await ListAsync(some.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())).As<DayCount>());
            Assert.Equal(rows.Where(t => t.Level > 0).GroupBy(t => Day(t.At)).Select(g => (g.Key, (long)g.Count())), days.Select(d => (new DateTime(d.Day.Ticks, DateTimeKind.Utc), d.Count)));
            Assert.Equal((Rows + Block - 1) / Block, some.Metrics.BlocksDecoded);

            // A range of days: the blocks of the days it keeps whole settle, the others are pruned.
            DateTime from = Day(Start).AddDays(3);
            Scan<Tick> range = file.Scan<Tick>().Where(t => t.At.Truncate(CalendarUnit.Day) >= from & t.At.Truncate(CalendarUnit.Day) < from.AddDays(2));
            days = await ListAsync(range.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())).As<DayCount>());
            Tick[] kept = [.. rows.Where(t => Day(t.At) >= from && Day(t.At) < from.AddDays(2))];
            Assert.Equal(kept.GroupBy(t => Day(t.At)).Select(g => (g.Key, (long)g.Count())), days.Select(d => (new DateTime(d.Day.Ticks, DateTimeKind.Utc), d.Count)));
            Assert.True(range.Metrics.BlocksDecoded <= 3, $"{range.Metrics.BlocksDecoded} blocks decoded");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnAggregateTheZonesDoNotHoldReadsEveryBlock()
    {
        Tick[] rows = Ticks();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // A sum is in no zone map, and a key with nulls, or without a function, settles nothing.
            Scan<Tick> summed = file.Scan<Tick>();
            List<DaySum> sums = await ListAsync(summed.GroupBy(t => t.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count(), g.Sum(t => t.Level))).As<DaySum>());
            Assert.Equal(rows.GroupBy(t => Day(t.At)).Select(g => (g.Key, (long)g.Count(), (long)g.Sum(t => t.Level))), sums.Select(d => (new DateTime(d.Day.Ticks, DateTimeKind.Utc), d.Count, d.Levels)));
            Assert.Equal((Rows + Block - 1) / Block, summed.Metrics.BlocksDecoded);

            Scan<Tick> nullable = file.Scan<Tick>();
            List<SeenCount> seen = await ListAsync(nullable.GroupBy(t => t.Seen.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())).As<SeenCount>());
            Assert.Equal(
                rows.GroupBy(t => t.Seen is { } s ? Day(s) : (DateTime?)null).Select(g => (g.Key, (long)g.Count())).OrderBy(g => g.Key ?? DateTime.MaxValue),
                seen.Select(s => (s.Day is { } d ? new DateTime(d.Ticks, DateTimeKind.Utc) : (DateTime?)null, s.Count)).OrderBy(g => g.Item1 ?? DateTime.MaxValue));
            Assert.True(nullable.Metrics.BlocksDecoded > (Rows / Block) - 2, $"{nullable.Metrics.BlocksDecoded} blocks decoded");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Day(DateTime t) => new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DayRange Utc(DayRange day) => day with
    {
        Day = new DateTime(day.Day.Ticks, DateTimeKind.Utc),
        Last = day.Last is { } last ? new DateTime(last.Ticks, DateTimeKind.Utc) : null,
    };

    /// <summary>The blocks whose first and last row fall on two keys: those the pass reads.</summary>
    private static int Straddling(Tick[] rows, Func<Tick, DateTime> key)
    {
        int count = 0;
        for (int start = 0; start < rows.Length; start += Block)
        {
            int last = Math.Min(start + Block, rows.Length) - 1;
            count += key(rows[start]) != key(rows[last]) ? 1 : 0;
        }

        return count;
    }

    /// <summary>A reading a minute, from before a midnight, every seventh with no price and every fifth unseen.</summary>
    private static Tick[] Ticks()
    {
        Random random = new Random(11);
        Tick[] rows = new Tick[Rows];
        for (int row = 0; row < Rows; row++)
        {
            DateTime at = Start.AddMinutes(row);
            rows[row] = new Tick(at, random.Next(-500, 500), row % 7 == 0 ? null : Math.Round(random.NextDouble() * 100, 2), row % 5 == 0 ? null : at);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Tick[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"zone-settling-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Tick>(path, new VortexWriteOptions { RowBlockSize = Block });
        await writer.WriteAsync<Tick>(rows, Ct);
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
    public partial record struct Tick(DateTime At, int Level, double? Price, DateTime? Seen);

    [VortexRecord]
    public partial record struct DayRange(DateTime Day, long Count, int? Lowest, DateTime? Last, double? Dearest);

    [VortexRecord]
    public partial record struct DayCount(DateTime Day, long Count);

    [VortexRecord]
    public partial record struct DaySum(DateTime Day, long Count, long Levels);

    [VortexRecord]
    public partial record struct SeenCount(DateTime? Day, long Count);
}
