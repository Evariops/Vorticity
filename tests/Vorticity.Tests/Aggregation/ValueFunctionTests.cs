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

    /// <summary>
    /// Levels from 3 to 97 grouped by their buckets of ten: the first bucket, 0, lies below the least level, so a key of a
    /// function of the column is not bounded by the column's own statistics, at one lane as at four.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AFunctionOfAColumnIsGroupedPastTheColumnsBounds(int degree)
    {
        Random random = new Random(5);
        Reading[] rows = [.. Readings().Select(r => r with { Level = random.Next(3, 98) })];
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<LevelCount> buckets = await ListAsync(
                file.Scan<Reading>().GroupBy(r => r.Level.Bucket(10)).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).As<LevelCount>());
            Assert.Equal(
                rows.GroupBy(r => Bucket(r.Level, 10)).OrderBy(g => g.Key).Select(g => new LevelCount(g.Key, g.Count())),
                buckets);
            Assert.Equal(0, buckets[0].Bucket);
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
                ScanPlan byFunction = await file.Scan<Reading>().Where(function).ExplainAsync(analyze: true, Ct);
                ScanPlan byRange = await file.Scan<Reading>().Where(range).ExplainAsync(analyze: true, Ct);
                Assert.True(byFunction.LiveBlocks < byFunction.Blocks / 4, $"{byFunction.LiveBlocks} of {byFunction.Blocks} blocks live: {string.Join(", ", byFunction.Pruning)}");
                Assert.Equal(byRange.LiveBlocks, byFunction.LiveBlocks);
                Assert.Equal(byRange.Pruning.Select(s => (s.Structure, s.BlocksPruned)), byFunction.Pruning.Select(s => (s.Structure, s.BlocksPruned)));
                Assert.Equal(await file.Scan<Reading>().Where(range).CountAsync(Ct), await file.Scan<Reading>().Where(function).CountAsync(Ct));
            }

            // A day is a block or two of the sorted instants, which the zone maps keep alone.
            ScanPlan oneDay = await file.Scan<Reading>().Where(pairs[0].Function).ExplainAsync(analyze: true, Ct);
            Assert.True(oneDay.LiveBlocks <= 2, $"{oneDay.LiveBlocks} of {oneDay.Blocks} blocks live");
            Assert.Equal(oneDay.Blocks - oneDay.LiveBlocks, Assert.Single(oneDay.Pruning, step => step.Structure == "zone map").BlocksPruned);

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
            new InstantTruncate(CalendarUnit.Hour, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Week, TimeUnit.Milliseconds, null),
            new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Hour, TimeUnit.Nanoseconds, Paris),
            new DateTruncate(CalendarUnit.Quarter),
            new DateTruncate(CalendarUnit.Week),
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

    /// <summary>
    /// A bucket, and a truncation to a minute, an hour or a day as stored, over blocks of values in time order, in
    /// none, backwards, repeated, around zero and at the storage's ends: each value's image in the block is the one
    /// the function gives it alone, whose run of one bucket a block keeps rather than dividing again.
    /// </summary>
    [Fact]
    public void ABlockMapsEachValueAsTheFunctionMapsItAlone()
    {
        ValueFunction[] functions =
        [
            new InstantBucket(7_000, TimeSpan.FromMilliseconds(7)),
            new InstantBucket(43_200_000_000, TimeSpan.FromHours(12)),
            new InstantTruncate(CalendarUnit.Minute, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Hour, TimeUnit.Nanoseconds, null),
            new InstantTruncate(CalendarUnit.Day, TimeUnit.Nanoseconds, null),
            new InstantTruncate(CalendarUnit.Day, TimeUnit.Seconds, null),

            // The calendar's units and a zone's, whose block keeps the bucket of the value before while values stay in it.
            new InstantTruncate(CalendarUnit.Week, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Quarter, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Year, TimeUnit.Microseconds, null),
            new InstantTruncate(CalendarUnit.Minute, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Hour, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Day, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Week, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, Paris),
            new InstantTruncate(CalendarUnit.Year, TimeUnit.Microseconds, Paris),
        ];

        Random random = new Random(11);
        long[] ordered = [.. Enumerable.Range(0, 5_000).Select(i => 1_760_000_000_000_000L + (i * 37_000_000L))];
        long[] ends = [long.MinValue, long.MinValue + 1, long.MinValue + 999, -1, 0, 1, long.MaxValue - 1, long.MaxValue, long.MinValue, 0, 0];

        // Every seven minutes across Paris's spring and autumn transitions of 2025, in microseconds, and every 37 hours
        // over two years: a zone's short and long days and hours, months and years in time order.
        long[] transitions = [.. Enumerable.Range(0, 1_000).Select(i => 1_743_285_600_000_000L + (i * 420_000_000L))
            .Concat(Enumerable.Range(0, 1_000).Select(i => 1_761_440_400_000_000L + (i * 420_000_000L)))];
        long[] years = [.. Enumerable.Range(0, 1_000).Select(i => 1_700_000_000_000_000L + (i * 133_200_000_000L))];
        long[][] blocks =
        [
            ordered,
            [.. ordered.OrderBy(_ => random.Next())],
            [.. ordered.Reverse()],
            ends,
            [.. ordered.Concat(ends).Concat(ordered)],
            transitions,
            years,
            [.. years.Reverse()],
            [.. transitions.Concat(years).Concat(transitions)],
        ];

        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);
        foreach (ValueFunction function in functions)
        {
            foreach (long[] values in blocks)
            {
                Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
                Vorticity.Buffers.VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
                values.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes));
                int column = arena.AddPrimitive(dtype, values.Length, Vorticity.Arrays.Validity.NonNullable, PType.I64, buffer);
                ReadOnlySpan<long> mapped = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(arena.GetNode(function.Apply(arena, column)).Values.Span);
                for (int i = 0; i < values.Length; i++)
                {
                    Assert.True(Image(function, values[i]) == mapped[i], $"{function.Text} sends {values[i]} to {mapped[i]} in a block, {Image(function, values[i])} alone");
                }

                // Its blocks given back once read, the arena held until then.
                arena.Reset();
            }
        }
    }

    /// <summary>
    /// A zone's truncation keeps the bucket of the value before while values stay in it, which a truncation that is not
    /// monotone, a clock set back across a unit's boundary, could fool: around every transition of every zone the system
    /// knows, from 1970 to 2040, up to eight of each zone's kinds of change, every unit maps a block of instants, in time
    /// order, reversed, shuffled and in a zigzag about the transition, as it maps each instant alone.
    /// </summary>
    [Fact]
    public void ABlockMapsAsTheFunctionAloneAroundEveryZonesTransitions()
    {
        Random random = new Random(37);
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);
        CalendarUnit[] units = [CalendarUnit.Minute, CalendarUnit.Hour, CalendarUnit.Day, CalendarUnit.Week, CalendarUnit.Month];
        long[] spans = [180, 3 * 3_600, 3 * 86_400, 9 * 86_400, 40 * 86_400];
        int transitions = 0;
        int zones = 0;

        // And zones the system may not hold, whose clocks go back across a unit's boundary: 17 minutes at local midnight,
        // 30 minutes at ten past midnight, two hours at one, and forward 45 minutes at ten to midnight.
        TimeZoneInfo Made(string id, int baseMinutes, int deltaMinutes, int backHour, int backMinute)
        {
            TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 4, 1);
            TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, backHour, backMinute, 0), 10, 1);
            TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(1970, 1, 1), new DateTime(2039, 12, 31), TimeSpan.FromMinutes(deltaMinutes), start, end);
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromMinutes(baseMinutes), id, id, id, [rule]);
        }

        TimeZoneInfo[] made =
        [
            Made("Back-17-at-midnight", 20, 17, 0, 0),
            Made("Back-30-at-00:10", 90, 30, 0, 10),
            Made("Back-120-at-01:00", -150, 120, 1, 0),
            Made("Forward-45-at-23:50", 345, 45, 23, 50),
        ];

        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones().Concat(made))
        {
            List<long> found = Transitions(zone);
            if (found.Count == 0)
            {
                continue;
            }

            zones++;
            foreach (long transition in found)
            {
                transitions++;
                for (int u = 0; u < units.Length; u++)
                {
                    InstantTruncate function = new InstantTruncate(units[u], TimeUnit.Seconds, zone);
                    long span = spans[u];
                    long step = Math.Max(1, span / 200);
                    // Over a few units about the transition, and a minute apart over three hours either side of it, where the
                    // instants a change of offset sends back or forward lie.
                    long[] around = [.. Enumerable.Range(0, 401).Select(i => transition - span + (i * step) + random.Next((int)step))
                        .Concat(Enumerable.Range(-180, 361).Select(m => transition + (m * 60L) + random.Next(60)))
                        .Order()];
                    long[] zigzag = new long[around.Length];
                    for (int i = 0; i < zigzag.Length; i++)
                    {
                        zigzag[i] = around[i % 2 == 0 ? i / 2 : around.Length - 1 - (i / 2)];
                    }

                    // And blocks of three that go past the transition, back before it, then between: a bucket held past it
                    // and stretched down to an instant before it must not answer for one the change sent elsewhere.
                    List<long[]> blocks = [around, [.. around.Reverse()], [.. around.OrderBy(_ => random.Next())], zigzag];
                    foreach (long after in (long[])[60, 600, 1_800, 5_400, 9_000])
                    {
                        foreach (long before in (long[])[9_000, 5_400, 2_700, 300])
                        {
                            foreach (long between in (long[])[3_600, 1_800, 60])
                            {
                                if (between < before)
                                {
                                    blocks.Add([transition + after, transition - before, transition - between]);
                                }
                            }
                        }
                    }

                    foreach (long[] values in blocks)
                    {
                        Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
                        Vorticity.Buffers.VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
                        values.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes));
                        int column = arena.AddPrimitive(dtype, values.Length, Vorticity.Arrays.Validity.NonNullable, PType.I64, buffer);
                        ReadOnlySpan<long> mapped = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(arena.GetNode(function.Apply(arena, column)).Values.Span);
                        for (int i = 0; i < values.Length; i++)
                        {
                            Int128 alone = Image(function, values[i]);
                            if (alone != mapped[i])
                            {
                                Assert.Fail($"{function.Text} sends {values[i]} to {mapped[i]} in a block, {alone} alone (transition at {transition})");
                            }
                        }

                        arena.Reset();
                    }
                }
            }
        }

        Console.Out.Write(FormattableString.Invariant($"ZONE BLOCKS: {transitions} transitions of {zones} zones, five units, four orders and blocks of three each.\n"));
        Assert.True(zones > 10, $"only {zones} zones with transitions");
    }

    /// <summary>
    /// Up to eight transitions of <paramref name="zone"/> between 1970 and 2040, in Unix seconds, one of each change of offset
    /// it makes (from an offset to another), found a day at a time and then to the second.
    /// </summary>
    private static List<long> Transitions(TimeZoneInfo zone)
    {
        List<long> found = [];
        HashSet<(TimeSpan, TimeSpan)> kinds = [];
        DateTime day = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan offset = zone.GetUtcOffset(day);
        while (day.Year < 2040 && found.Count < 8)
        {
            DateTime next = day.AddDays(1);
            TimeSpan now = zone.GetUtcOffset(next);
            if (now != offset)
            {
                if (kinds.Add((offset, now)))
                {
                    DateTime from = day;
                    DateTime to = next;
                    while ((to - from).TotalSeconds > 1)
                    {
                        DateTime middle = from.AddSeconds(Math.Floor((to - from).TotalSeconds / 2));
                        (from, to) = zone.GetUtcOffset(middle) == offset ? (middle, to) : (from, middle);
                    }

                    found.Add(new DateTimeOffset(to).ToUnixTimeSeconds());
                }

                offset = now;
            }

            day = next;
        }

        return found;
    }

    /// <summary>
    /// The floor by a width's reciprocal against the processor's division: every width from 1 to 1,000, the powers of
    /// two, a minute, an hour and a day in nanoseconds, widths around 2^31, 2^32 and 2^62, the greatest, and widths drawn
    /// at random; over values around zero and the width's multiples, at the storage's ends, and drawn at random.
    /// </summary>
    [Fact]
    public void AFloorByAReciprocalIsTheDivisionsFloor()
    {
        Random random = new Random(13);
        List<long> units = [.. Enumerable.Range(1, 1_000).Select(u => (long)u)];
        units.AddRange([60_000_000_000L, 3_600_000_000_000L, 86_400_000_000_000L, int.MaxValue, (long)int.MaxValue + 2, uint.MaxValue, (long)uint.MaxValue + 2, (1L << 62) - 1, (1L << 62) + 1, long.MaxValue - 1, long.MaxValue]);
        for (int bit = 0; bit < 63; bit++)
        {
            units.Add(1L << bit);
        }

        for (int i = 0; i < 300; i++)
        {
            units.Add(random.NextInt64(1, long.MaxValue));
        }

        foreach (long unit in units)
        {
            FloorDivisor divisor = new FloorDivisor(unit);
            List<long> values = [0, 1, -1, 2, -2, long.MinValue, long.MinValue + 1, long.MaxValue, long.MaxValue - 1];
            foreach (long around in new[] { unit, -unit, unit * 3, -unit * 3 })
            {
                values.AddRange([around - 1, around, around + 1]);
            }

            long reach = unit > long.MaxValue / 4 ? long.MaxValue / 4 : 4 * unit;
            for (int i = 0; i < 64; i++)
            {
                values.Add(random.NextInt64(long.MinValue, long.MaxValue));
                values.Add(random.NextInt64(-reach, reach));
            }

            foreach (long value in values)
            {
                Assert.True(Civil.FloorTo(value, unit) == divisor.Floor(value), $"{value} floored by {unit}: {divisor.Floor(value)}, {Civil.FloorTo(value, unit)} dividing");
                Assert.True((ulong)value / (ulong)unit == divisor.Quotient((ulong)value), $"{(ulong)value} over {unit}");
            }
        }
    }

    /// <summary>
    /// A bucket of integers over a block of every storage, signed and not, of widths from one to beyond the storage:
    /// each value's image in the block is the one the function gives it alone, floored below the storage's least value
    /// to that value.
    /// </summary>
    [Fact]
    public void ABucketOfIntegersMapsABlockOfEveryStorageAsEachValueAlone()
    {
        Random random = new Random(17);
        PType[] storages = [PType.I8, PType.I16, PType.I32, PType.I64, PType.U8, PType.U16, PType.U32, PType.U64];
        long[] widths = [1, 3, 10, 1_000, 1 << 20, 1_000_000_000_007, long.MaxValue];
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        foreach (PType storage in storages)
        {
            int width = storage.ByteWidth();
            (Int128 min, Int128 max) = ValueFunction.Integers(storage);
            foreach (long bucket in widths)
            {
                NumberBucket function = new NumberBucket(storage, bucket, bucket, bucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
                const int Count = 512;
                Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
                Vorticity.Buffers.VortexBuffer buffer = arena.Allocate(Count * width, width, out Span<byte> bytes);
                random.NextBytes(bytes);
                Int128[] ends = [min, min + 1, max, max - 1, 0, 1];
                for (int e = 0; e < ends.Length; e++)
                {
                    Write(bytes.Slice(e * width, width), storage, ends[e]);
                }

                int column = arena.AddPrimitive(types.Primitive(storage, Nullability.NonNullable), Count, Vorticity.Arrays.Validity.NonNullable, storage, buffer);
                ReadOnlySpan<byte> mapped = arena.GetNode(function.Apply(arena, column)).Values.Span;
                for (int i = 0; i < Count; i++)
                {
                    Int128 value = Read(bytes.Slice(i * width, width), storage);
                    Int128 image = Read(mapped.Slice(i * width, width), storage);
                    Assert.True(Image(function, value) == image, $"Bucket({bucket}) of {storage} sends {value} to {image} in a block, {Image(function, value)} alone");
                }

                arena.Reset();
            }
        }

        static Int128 Read(ReadOnlySpan<byte> bytes, PType storage) => storage switch
        {
            PType.I8 => (sbyte)bytes[0],
            PType.I16 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes),
            PType.I32 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes),
            PType.I64 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes),
            PType.U8 => bytes[0],
            PType.U16 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            PType.U32 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        };

        static void Write(Span<byte> bytes, PType storage, Int128 value)
        {
            Span<byte> wide = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(wide, value);
            wide[..bytes.Length].CopyTo(bytes);
            _ = storage;
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

    [VortexRecord]
    public partial record struct LevelCount(int Bucket, long Count);
}
