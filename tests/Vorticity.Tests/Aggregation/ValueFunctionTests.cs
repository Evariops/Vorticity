using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// <c>Truncate</c> and <c>Bucket</c>: a function of a column as a key, a filter and an input,
/// against the same function written with .NET's calendar over the same rows — every unit, before
/// and after the epoch, a zone's calendar across its transitions, a null staying null and a NaN NaN.
/// </summary>
public sealed partial class ValueFunctionTests
{
    private const int Rows = 20_000;

    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    [Theory]
    [InlineData(CalendarUnit.Minute)]
    [InlineData(CalendarUnit.Hour)]
    [InlineData(CalendarUnit.Day)]
    [InlineData(CalendarUnit.Week)]
    [InlineData(CalendarUnit.Month)]
    [InlineData(CalendarUnit.Quarter)]
    [InlineData(CalendarUnit.Year)]
    public async Task AnInstantTruncatesAsStoredAndADateByItsDays(CalendarUnit unit)
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<DateTime> instants = await file.Scan<Reading>().Select(r => r.At.Truncate(unit)).ToListAsync(Ct);
            Assert.Equal(rows.Select(r => Truncate(r.At, unit).Ticks), instants.Select(t => t.Ticks));

            if (unit >= CalendarUnit.Day)
            {
                List<DateOnly> days = await file.Scan<Reading>().Select(r => r.Day.Truncate(unit)).ToListAsync(Ct);
                Assert.Equal(rows.Select(r => DateOnly.FromDateTime(Truncate(r.Day.ToDateTime(TimeOnly.MinValue), unit))), days);
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan<Reading>().Select(r => r.Day.Truncate(unit)));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFunctionGroupsFiltersAndFeedsAnAggregate()
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // The instants are sorted: their hours too, so the group by streams.
            Vorticity.Aggregation byHour = file.Scan<Reading>()
                .GroupBy(r => r.At.Truncate(CalendarUnit.Hour))
                .Select(g => (g.Key, g.Count(), g.Max(x => x.At.Truncate(CalendarUnit.Minute)), g.Sum(x => x.Level.Bucket(10))));
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)byHour.Query) >= 0);
            List<HourLevels> hours = await ListAsync(byHour.As<HourLevels>());
            Assert.Equal(
                rows.GroupBy(r => Truncate(r.At, CalendarUnit.Hour)).Select(g => new HourLevels(g.Key, g.Count(), Truncate(g.Max(r => r.At), CalendarUnit.Minute), g.Sum(r => (long)Bucket(r.Level, 10)))),
                hours.Select(h => h with { Hour = new DateTime(h.Hour.Ticks), LastMinute = new DateTime(h.LastMinute!.Value.Ticks) }));

            // A filter on the function, and the column itself beside it: two values.
            DateTime day = Truncate(rows[rows.Length / 2].At, CalendarUnit.Day);
            Assert.Equal(
                rows.Count(r => Truncate(r.At, CalendarUnit.Day) == day),
                await file.Scan<Reading>().Where(r => r.At.Truncate(CalendarUnit.Day) == day).CountAsync(Ct));
            Assert.Equal(
                2,
                ((AggregationQuery)file.Scan<Reading>().GroupBy(r => r.Station).Select(g => (g.Max(x => x.At), g.Max(x => x.At.Truncate(CalendarUnit.Day)))).Query).Plan.Aggregates.Length);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AZonesCalendarHoldsItsTransitions()
    {
        // Around both of 2026's transitions in Paris: a 23-hour day in March, a 25-hour day in
        // October whose 02:00 comes twice.
        Reading[] rows = Readings(new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(7))
            .Concat(Readings(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(7)))
            .ToArray();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            foreach (CalendarUnit unit in new[] { CalendarUnit.Hour, CalendarUnit.Day, CalendarUnit.Week, CalendarUnit.Month })
            {
                List<DateTimeOffset> local = await file.Scan<Reading>().Select(r => r.Local.Truncate(unit)).ToListAsync(Ct);
                List<DateTime> explicitZone = await file.Scan<Reading>().Select(r => r.At.Truncate(unit, Paris)).ToListAsync(Ct);
                long[] expected = [.. rows.Select(r => InZone(r.At, unit, Paris))];
                Assert.Equal(expected, local.Select(t => t.UtcTicks));
                Assert.Equal(expected, explicitZone.Select(t => t.Ticks));
            }

            // The repeated hour is two buckets; its day, one.
            long autumn = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;
            List<HourCount> hours = await ListAsync(file.Scan<Reading>()
                .Where(r => r.At >= new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc) & r.At < new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc))
                .GroupBy(r => r.Local.Truncate(CalendarUnit.Hour))
                .Select(g => (g.Key, g.Count()))
                .As<HourCount>());
            Assert.Equal(5, hours.Count);
            Assert.Equal(2, hours.Count(h => TimeZoneInfo.ConvertTime(h.Hour, Paris).Hour == 2));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ABucketCountsFromTheEpochAndANumberFromZero()
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            TimeSpan quarter = TimeSpan.FromMinutes(15);
            List<DateTime> instants = await file.Scan<Reading>().Select(r => r.At.Bucket(quarter)).ToListAsync(Ct);
            Assert.Equal(rows.Select(r => FloorTicks(r.At.Ticks - DateTime.UnixEpoch.Ticks, quarter.Ticks) + DateTime.UnixEpoch.Ticks), instants.Select(t => t.Ticks));

            List<int> levels = await file.Scan<Reading>().Select(r => r.Level.Bucket(25)).ToListAsync(Ct);
            Assert.Equal(rows.Select(r => Bucket(r.Level, 25)), levels);

            List<double?> prices = await file.Scan<Reading>().Select(r => r.Price.Bucket(0.25)).ToListAsync(Ct);
            Assert.Equal(rows.Select(r => r.Price is double p ? Math.Floor(p / 0.25) * 0.25 : (double?)null), prices);

            Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan<Reading>().Select(r => r.At.Bucket(TimeSpan.Zero)));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan<Reading>().Select(r => r.Seconds.Bucket(TimeSpan.FromMilliseconds(1_500))));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan<Reading>().Select(r => r.Level.Bucket(-3)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFunctionPrunesAsTheRangeItStandsFor()
    {
        // Small blocks, so that a day is a few blocks among many.
        Reading[] rows = Readings();
        string path = Path.Combine(AppContext.BaseDirectory, "value-functions", $"pruned-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, new VortexWriteOptions { RowBlockSize = 128 }))
        {
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            DateTime day = Truncate(rows[rows.Length / 3].At, CalendarUnit.Day);
            DateTime month = Truncate(rows[rows.Length / 8].At, CalendarUnit.Month);
            DateTime late = Truncate(rows[rows.Length * 7 / 8].At, CalendarUnit.Day);
            (Func<Probe<Reading>, Predicate> Function, Func<Probe<Reading>, Predicate> Range)[] pairs =
            [
                (r => r.At.Truncate(CalendarUnit.Day) == day, r => r.At >= day & r.At < day.AddDays(1)),
                (r => r.At.Truncate(CalendarUnit.Month) < month, r => r.At < month),
                (r => r.At.Bucket(TimeSpan.FromHours(6)) >= late, r => r.At >= late),
                (r => r.At.Truncate(CalendarUnit.Hour).Bucket(TimeSpan.FromHours(6)) > late, r => r.At >= late.AddHours(6)),
            ];

            foreach ((Func<Probe<Reading>, Predicate> function, Func<Probe<Reading>, Predicate> range) in pairs)
            {
                ScanPlan byFunction = await file.Scan<Reading>().Where(function).ExplainAsync(Ct);
                ScanPlan byRange = await file.Scan<Reading>().Where(range).ExplainAsync(Ct);
                Assert.Equal(byRange.LiveBlocks, byFunction.LiveBlocks);
                Assert.Equal(byRange.Pruning.Select(s => (s.Structure, s.BlocksPruned)), byFunction.Pruning.Select(s => (s.Structure, s.BlocksPruned)));
                Assert.Equal(await file.Scan<Reading>().Where(range).CountAsync(Ct), await file.Scan<Reading>().Where(function).CountAsync(Ct));
            }

            // A day is a block or two of the sorted instants, located like the range it stands for.
            ScanPlan oneDay = await file.Scan<Reading>().Where(pairs[0].Function).ExplainAsync(Ct);
            Assert.True(oneDay.LiveBlocks <= 2, $"{oneDay.LiveBlocks} of {oneDay.Blocks} blocks live");

            // A scan's order and a key cursor walk a column's key source, which a function has not.
            Assert.Throws<ArgumentException>(() => file.Scan<Reading>().OrderBy(r => r.At.Truncate(CalendarUnit.Day)));
            Assert.Throws<ArgumentException>(() => file.Scan<Reading>().Keys(r => r.At.Truncate(CalendarUnit.Day)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AComparisonOfAFunctionSelectsWhatTheFunctionDoes()
    {
        // Each comparison with literals on the grid, between two of its points, before and after
        // the data and at the storage's ends, and its negation, which leaves a null row out too.
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            DateTime day = Truncate(rows[rows.Length / 3].At, CalendarUnit.Day);
            DateTime[] instants = [day, day.AddHours(1), day.AddHours(6), Truncate(rows[0].At, CalendarUnit.Month), new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddTicks(-9)];
            await SameRowsAsync(file, rows, r => r.At.Truncate(CalendarUnit.Day), r => Truncate(r.At, CalendarUnit.Day), instants);
            await SameRowsAsync(file, rows, r => r.At.Bucket(TimeSpan.FromHours(6)), r => new DateTime(FloorTicks(r.At.Ticks - DateTime.UnixEpoch.Ticks, TimeSpan.TicksPerHour * 6) + DateTime.UnixEpoch.Ticks, DateTimeKind.Utc), instants);
            await SameRowsAsync(file, rows, r => r.At.Truncate(CalendarUnit.Hour).Bucket(TimeSpan.FromHours(6)), r => new DateTime(FloorTicks(r.At.Ticks - DateTime.UnixEpoch.Ticks, TimeSpan.TicksPerHour * 6) + DateTime.UnixEpoch.Ticks, DateTimeKind.Utc), instants);
            await SameRowsAsync(file, rows, r => r.Seconds.Truncate(CalendarUnit.Minute), r => Truncate(r.Seconds, CalendarUnit.Minute), instants);
            await SameRowsAsync(file, rows, r => r.Seen.Truncate(CalendarUnit.Month), r => r.Seen is { } seen ? Truncate(seen, CalendarUnit.Month) : null, [.. instants.Select(t => (DateTime?)t)]);
            await SameRowsAsync(file, rows, r => r.Local.Truncate(CalendarUnit.Day), r => new DateTimeOffset(InZone(r.At, CalendarUnit.Day, Paris), TimeSpan.Zero), [.. instants.Take(6).Select(t => new DateTimeOffset(t)), new DateTimeOffset(InZone(day, CalendarUnit.Day, Paris), TimeSpan.Zero)]);
            await SameRowsAsync(file, rows, r => r.Day.Truncate(CalendarUnit.Week), r => DateOnly.FromDateTime(Truncate(r.Day.ToDateTime(TimeOnly.MinValue), CalendarUnit.Week)), [.. instants.Take(6).Select(DateOnly.FromDateTime), DateOnly.MinValue, DateOnly.MaxValue]);
            await SameRowsAsync(file, rows, r => r.Level.Bucket(25), r => Bucket(r.Level, 25), [0, 25, 13, -13, -500, 499, int.MinValue, int.MaxValue]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AFunctionIsAFloorOverItsWholeStorage()
    {
        // Monotone and never above its argument at the storage's ends too, where the floor would
        // fall below the least value, and the least value whose image reaches a literal is found.
        ValueFunction[] functions =
        [
            new InstantTruncate(CalendarUnit.Day, TimeUnit.Nanoseconds, null),
            new InstantTruncate(CalendarUnit.Year, TimeUnit.Seconds, null),
            new InstantTruncate(CalendarUnit.Minute, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Hour, TimeUnit.Nanoseconds, Paris),
            new DateTruncate(CalendarUnit.Quarter),
            new InstantBucket(7_000, TimeSpan.FromMilliseconds(7)),
            new NumberBucket(PType.I8, 10, 10, "10"),
            new NumberBucket(PType.U16, 1_000, 1_000, "1000"),
        ];

        Random random = new Random(7);
        foreach (ValueFunction function in functions)
        {
            (Int128 min, Int128 max) = ValueFunction.Integers(function.Storage);
            List<Int128> values = [];
            for (int i = 0; i < 64; i++)
            {
                values.Add(min + i);
                values.Add(max - i);
                values.Add(min + ((max - min) * random.Next(1_000) / 1_000));
            }

            values.Add(0);
            values.Sort();
            Int128 before = min;
            foreach (Int128 value in values)
            {
                Int128 image = Image(function, value);
                Assert.True(image <= value && image >= before, $"{function.Text} sends {value} to {image}, after {before}");
                before = image;

                foreach (Int128 literal in new[] { image, image + 1, value })
                {
                    if (function.TryLeast(literal, out Int128 least))
                    {
                        Assert.True(Image(function, least) >= literal, $"{function.Text}: {least} for {literal}");
                        Assert.True(least == min || Image(function, least - 1) < literal, $"{function.Text}: {least} is not the least for {literal}");
                    }
                    else
                    {
                        Assert.True(Image(function, max) < literal, $"{function.Text} reaches {literal}");
                    }
                }
            }
        }
    }

    private static Int128 Image(ValueFunction function, Int128 value)
    {
        Assert.True(function.TryMap(function.Literal(value), out FilterLiteral image));
        Assert.True(ValueFunction.TryInteger(image, out Int128 integer));
        return integer;
    }

    /// <summary>Each comparison of the function with each literal, and its negation, counts the rows the oracle's does; a null row is in neither.</summary>
    private static async Task SameRowsAsync<T>(VortexFile file, Reading[] rows, Func<Probe<Reading>, Sym<T>> function, Func<Reading, T> oracle, T[] literals)
    {
        (Func<Sym<T>, T, Predicate> Predicate, Func<int, bool> Holds)[] comparisons =
        [
            ((s, c) => s == c, order => order == 0),
            ((s, c) => s != c, order => order != 0),
            ((s, c) => s < c, order => order < 0),
            ((s, c) => s <= c, order => order <= 0),
            ((s, c) => s > c, order => order > 0),
            ((s, c) => s >= c, order => order >= 0),
        ];

        foreach (T literal in literals)
        {
            for (int i = 0; i < comparisons.Length; i++)
            {
                (Func<Sym<T>, T, Predicate> predicate, Func<int, bool> holds) = comparisons[i];
                int selected = rows.Count(r => oracle(r) is { } v && holds(Comparer<T>.Default.Compare(v, literal)));
                int refused = rows.Count(r => oracle(r) is { } v && !holds(Comparer<T>.Default.Compare(v, literal)));
                Assert.True(selected == await file.Scan<Reading>().Where(r => predicate(function(r), literal)).CountAsync(Ct), $"comparison {i} with {literal}");
                Assert.True(refused == await file.Scan<Reading>().Where(r => !predicate(function(r), literal)).CountAsync(Ct), $"negated comparison {i} with {literal}");
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Truncate(DateTime t, CalendarUnit unit) => unit switch
    {
        CalendarUnit.Minute => new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerMinute), t.Kind),
        CalendarUnit.Hour => new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerHour), t.Kind),
        CalendarUnit.Day => t.Date,
        CalendarUnit.Week => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
        CalendarUnit.Month => new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind),
        CalendarUnit.Quarter => new DateTime(t.Year, ((t.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, t.Kind),
        _ => new DateTime(t.Year, 1, 1, 0, 0, 0, t.Kind),
    };

    /// <summary>The start of the unit on a zone's calendar, as UTC ticks: an hour by the value's own offset, a day from its local midnight.</summary>
    private static long InZone(DateTime utc, CalendarUnit unit, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), zone);
        DateTime start = Truncate(local.DateTime, unit);
        return unit <= CalendarUnit.Hour
            ? (start - local.Offset).Ticks
            : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone).Ticks;
    }

    private static long FloorTicks(long value, long width) => (long)Math.Floor((double)value / width) * width;

    private static int Bucket(int value, int width) => (int)Math.Floor(value / (double)width) * width;

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

    /// <summary>Readings every few minutes from before the epoch, sorted by instant.</summary>
    private static Reading[] Readings() => Readings(new DateTime(1969, 12, 20, 13, 17, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(37));

    private static Reading[] Readings(DateTime start, TimeSpan step)
    {
        Random random = new Random(start.Minute + 1);
        Reading[] rows = new Reading[Rows / 4];
        for (int row = 0; row < rows.Length; row++)
        {
            DateTime at = start + (step * row) + TimeSpan.FromSeconds(row % 50);
            rows[row] = new Reading(
                row % 3 == 0 ? "north" : "south",
                at,
                new DateTimeOffset(at, TimeSpan.Zero),
                DateOnly.FromDateTime(at),
                random.Next(-500, 500),
                row % 13 == 0 ? null : row % 17 == 0 ? double.NaN : Math.Round(random.NextDouble() * 10, 3),
                at,
                row % 11 == 0 ? null : at);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Reading[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "value-functions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"readings-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Reading(
        string Station,
        DateTime At,
        [property: VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset Local,
        DateOnly Day,
        int Level,
        double? Price,
        [property: VortexColumn(Unit = TimeUnit.Seconds)] DateTime Seconds,
        DateTime? Seen);

    [VortexRecord]
    public partial record struct HourLevels(DateTime Hour, long Count, DateTime? LastMinute, long Levels);

    [VortexRecord]
    public partial record struct HourCount(DateTimeOffset Hour, long Count);
}
