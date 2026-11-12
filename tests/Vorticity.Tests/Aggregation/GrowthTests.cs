using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The growth of a counter over a group's points in the order of a column, and its first and last points: against an
/// oracle that sorts the points and sums the rises in exact arithmetic, rounded once, at every degree, over rows in the
/// order of the series, of the instants, or of none.
/// </summary>
public sealed partial class GrowthTests
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheGrowthOfACounterAddsItsRisesAndWhatItHeldBeforeAFall()
    {
        // 100, 130, 160, 5, 20 grows 30 + 30 + 5 + 15; 5, 7, 5, 9 grows 11 where 5, 6, 8, 9 grows 4, the same ends and extremes.
        Point[] rows =
        [
            .. Series("a", 100, 130, 160, 5, 20),
            .. Series("b", 5, 7, 5, 9),
            .. Series("c", 5, 6, 8, 9),
            .. Series("d", 42),

            // Points of one instant in no order of their values: 100, then 120 and 130, then 3 and 5, then 20, grow
            // 20 + 10 + 3 + 2 + 15.
            new Point("e", Start, 0, 100),
            new Point("e", Start.AddMinutes(1), 1, 130),
            new Point("e", Start.AddMinutes(1), 1, 120),
            new Point("e", Start.AddMinutes(2), 2, 5),
            new Point("e", Start.AddMinutes(2), 2, 3),
            new Point("e", Start.AddMinutes(3), 3, 20),
        ];
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<Grown> grown = await ListAsync(file.Scan<Point>()
                .GroupBy(r => r.Series)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)))
                .As<Grown>());
            Assert.Equal(
                [new Grown("a", 80, 100, 20), new Grown("b", 11, 5, 9), new Grown("c", 4, 5, 9), new Grown("d", 0, 42, 42), new Grown("e", 50, 100, 20)],
                grown);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(Layout.BySeries, 1)]
    [InlineData(Layout.BySeries, 4)]
    [InlineData(Layout.BySeries, 8)]
    [InlineData(Layout.ByInstant, 1)]
    [InlineData(Layout.ByInstant, 8)]
    [InlineData(Layout.Shuffled, 1)]
    [InlineData(Layout.Shuffled, 8)]
    public async Task TheGrowthByHourIsTheOraclesToTheBit(Layout layout, int degree)
    {
        Point[] rows = Arrange(Points(seed: 7), layout);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation hourly = file.Scan<Point>()
                .GroupBy(r => (r.Series, Hour: r.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Series, g.Key.Hour, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));
            List<Hourly> read = await ListAsync(hourly.As<Hourly>());

            Dictionary<(string, DateTimeOffset), Hourly> expected = Oracle(rows).ToDictionary(h => (h.Series, h.Hour));
            Assert.Equal(expected.Count, read.Count);
            foreach (Hourly hour in read)
            {
                Hourly wanted = expected[(hour.Series, hour.Hour)];
                Assert.True(Same(wanted, hour), $"{hour} where the oracle gives {wanted}");
            }

            // Rows in the order of the series or of the instants read nothing again on one lane, points of one instant in no
            // order of their values included; on several, a group whose points of one instant two pieces share, now and
            // then. Rows in no order read their groups again.
            long again = ((AggregationQuery)hourly.Query).Plan.LastGroupsReadAgain;
            if (layout == Layout.Shuffled)
            {
                Assert.True(again > 0, "rows in no order read no group again");
            }
            else if (degree == 1)
            {
                Assert.Equal(0, again);
            }
            else
            {
                Assert.True(again * 50 < expected.Count, $"{again} groups of {expected.Count} read again");
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ADatasetGrowsAsTheOracleWithLatePointsBeforeAndAfterItsCompaction(int degree)
    {
        // Six hours of points appended ten minutes an object, as a pipeline writes them, then the points held back as
        // late, each in an object of its own: level 0, objects that cross, then their compaction.
        Point[] rows = Points(seed: 13);
        Random random = new Random(29);
        bool[] late = [.. rows.Select(_ => random.Next(150) == 0)];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset.CreateAsync(store, Point.Schema, new Vorticity.Dataset.DatasetOptions
        {
            Session = session,
            Layout = new Vorticity.Dataset.DatasetLayout
            {
                Partition = Vorticity.Dataset.LayoutValue.Column(nameof(Point.At)).Truncate(CalendarUnit.Hour),
                SortKey = [Vorticity.Dataset.LayoutValue.Column(nameof(Point.Series)), Vorticity.Dataset.LayoutValue.Column(nameof(Point.At))],
                Organization = Vorticity.Dataset.LayoutOrganization.Leveled,
            },
            Write = new VortexWriteOptions { RowBlockSize = 512 },
        }, Ct);

        foreach (IGrouping<int, Point> part in rows.Where((_, i) => !late[i]).GroupBy(r => r.Minute / 10))
        {
            await AppendAsync(dataset, [.. part]);
        }

        Point[] held = [.. rows.Where((_, i) => late[i])];
        for (int at = 0; at < held.Length; at += held.Length / 3)
        {
            await AppendAsync(dataset, held[at..Math.Min(held.Length, at + (held.Length / 3))]);
        }

        Dictionary<(string, DateTimeOffset), Hourly> expected = Oracle(rows).ToDictionary(h => (h.Series, h.Hour));
        long readAgain = await CheckAsync();
        Assert.True(readAgain > 0, "late points read no group again");

        while (await dataset.CompactAsync(new Vorticity.Dataset.CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
        {
        }

        Assert.Equal(0, await CheckAsync());

        async Task<long> CheckAsync()
        {
            Vorticity.Aggregation hourly = dataset.Scan<Point>()
                .GroupBy(r => (r.Series, Hour: r.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Series, g.Key.Hour, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));
            List<Hourly> read = await ListAsync(hourly.As<Hourly>());
            Assert.Equal(expected.Count, read.Count);
            Assert.All(read, hour => Assert.True(Same(expected[(hour.Series, hour.Hour)], hour), $"{hour} where the oracle gives {expected[(hour.Series, hour.Hour)]}"));
            return ((AggregationQuery)hourly.Query).Plan.LastGroupsReadAgain;
        }
    }

    [Fact]
    public async Task ObjectsWrittenInTimeOrderReadNothingAgain()
    {
        Point[] rows = Points(seed: 19);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset.CreateAsync(store, Point.Schema, new Vorticity.Dataset.DatasetOptions
        {
            Layout = new Vorticity.Dataset.DatasetLayout
            {
                SortKey = [Vorticity.Dataset.LayoutValue.Column(nameof(Point.Series)), Vorticity.Dataset.LayoutValue.Column(nameof(Point.At))],
            },
        }, Ct);
        foreach (IGrouping<int, Point> part in rows.GroupBy(r => r.Minute / 5))
        {
            await AppendAsync(dataset, [.. part]);
        }

        Vorticity.Aggregation daily = dataset.Scan<Point>()
            .GroupBy(r => r.Series)
            .Select(g => (g.Key, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));
        List<Grown> read = await ListAsync(daily.As<Grown>());
        Dictionary<string, Hourly> expected = Oracle(rows, hourly: false).ToDictionary(h => h.Series);
        Assert.All(read, g => Assert.True(
            Bits(g.Increase) == Bits(expected[g.Series].Increase) && Bits(g.First) == Bits(expected[g.Series].First) && Bits(g.Last) == Bits(expected[g.Series].Last),
            $"{g} where the oracle gives {expected[g.Series]}"));
        Assert.Equal(0, ((AggregationQuery)daily.Query).Plan.LastGroupsReadAgain);
    }

    [Fact]
    public async Task ATakeSettlesTheGroupsItDeliversAndAnOrderOnTheGrowthSettlesEveryOne()
    {
        // Rows in no order: every group's runs cross.
        Point[] rows = Arrange(Points(seed: 31), Layout.Shuffled);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Hourly> expected = [.. Oracle(rows).OrderBy(h => h.Series, StringComparer.Ordinal).ThenBy(h => h.Hour)];

            Vorticity.Aggregation first = file.Scan<Point>()
                .GroupBy(r => (r.Series, Hour: r.At.Truncate(CalendarUnit.Hour)))
                .OrderBy(g => g.Key.Series)
                .ThenBy(g => g.Key.Hour)
                .Take(5)
                .Select(g => (g.Key.Series, g.Key.Hour, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));
            List<Hourly> five = await ListAsync(first.As<Hourly>());
            Assert.Equal(expected.Take(5).Select(Show), five.Select(Show));
            Assert.Equal(5, ((AggregationQuery)first.Query).Plan.LastGroupsReadAgain);

            Vorticity.Aggregation steepest = file.Scan<Point>()
                .GroupBy(r => (r.Series, Hour: r.At.Truncate(CalendarUnit.Hour)))
                .OrderByDescending(g => g.OrderedBy(r => r.At).Increase(r => r.Value))
                .Take(5)
                .Select(g => (g.Key.Series, g.Key.Hour, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));
            List<Hourly> top = await ListAsync(steepest.As<Hourly>());
            Assert.Equal(expected.OrderByDescending(h => h.Increase).Take(5).Select(Show), top.Select(Show));
            Assert.Equal(expected.Count, ((AggregationQuery)steepest.Query).Plan.LastGroupsReadAgain);
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        static string Show(Hourly h) => $"{h.Series} {h.Hour:O} {Bits(h.Increase)} {Bits(h.First)} {Bits(h.Last)}";
    }

    [Fact]
    public async Task UnderATightBudgetTheTablesSpillAndTheGroupsAreReadAgainInHalves()
    {
        // Twenty thousand series of a dozen points in no order: every group read again, its points past the budget.
        Random random = new Random(37);
        Point[] rows = [.. Enumerable.Range(0, 20_000)
            .SelectMany(s => Enumerable.Range(0, 12).Select(i => new Point($"s{s:D5}", Start.AddSeconds(15 * i), i, random.Next(4) == 0 ? random.Next(10) : 1_000 + (i * 3.5))))
            .OrderBy(_ => random.Next())];
        Dictionary<string, Hourly> expected = Oracle(rows, hourly: false).ToDictionary(h => h.Series);
        string path = await WriteAsync(rows);
        try
        {
            QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
            await using (VortexSession measure = VortexSession.Create(options => { options.MaxDegreeOfParallelism = 1; options.MemoryBudget = large; }))
            await using (VortexFile file = await measure.OpenAsync(path, cancellationToken: Ct))
            {
                Check(await ListAsync(Growths(file).As<Grown>()));
            }

            foreach (int percent in (int[])[50, 25])
            {
                string scratch = System.IO.Path.Combine(AppContext.BaseDirectory, "growth", $"scratch-{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(scratch);
                try
                {
                    QueryMemoryBudget budget = new QueryMemoryBudget(large.PeakBytes / 100 * percent);
                    await using VortexSession session = VortexSession.Create(options =>
                    {
                        options.MaxDegreeOfParallelism = 4;
                        options.MemoryBudget = budget;
                        options.ScratchDirectory = scratch;
                    });
                    await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                    Vorticity.Aggregation growths = Growths(file);
                    growths.Plan.LanesRetire = false;
                    Check(await ListAsync(growths.As<Grown>()));
                    Assert.Equal(0, budget.ReservedBytes);
                    AggregationPlan plan = growths.Plan;
                    Assert.True(plan.LastRun!.SpilledRuns > 0 || plan.LastRowsReadAgain > rows.Length, $"at {percent} %, nothing spilled nor read again in halves");
                }
                finally
                {
                    System.IO.Directory.Delete(scratch, recursive: true);
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        static Vorticity.Aggregation Growths(VortexFile file) => file.Scan<Point>()
            .GroupBy(r => r.Series)
            .Select(g => (g.Key, g.OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.OrderedBy(r => r.At).Last(r => r.Value)));

        void Check(List<Grown> read)
        {
            Assert.Equal(expected.Count, read.Count);
            Assert.All(read, g => Assert.True(
                Bits(g.Increase) == Bits(expected[g.Series].Increase) && Bits(g.First) == Bits(expected[g.Series].First) && Bits(g.Last) == Bits(expected[g.Series].Last),
                $"{g} where the oracle gives {expected[g.Series]}"));
        }
    }

    private static async Task AppendAsync(Vorticity.Dataset.VortexDataset dataset, Point[] rows)
    {
        await using Vorticity.Dataset.ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Point>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [Fact]
    public async Task AWholeScanAndAFilteredGroupGrowAsTheOracle()
    {
        Point[] rows = Arrange(Points(seed: 11), Layout.ByInstant);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            double? whole = await file.Scan<Point>().Where(r => r.Series == "s3").AggregateAsync(a => a.OrderedBy(r => r.At).Increase(r => r.Value), Ct);
            Hourly expected = Oracle([.. rows.Where(r => r.Series == "s3")], hourly: false).Single();
            Assert.Equal(expected.Increase, whole);

            // A filtered group keeps its own points: the first three hours alone.
            List<Grown> filtered = await ListAsync(file.Scan<Point>()
                .GroupBy(r => r.Series)
                .Select(g => (g.Key, g.Where(r => r.Minute < 180).OrderedBy(r => r.At).Increase(r => r.Value), g.OrderedBy(r => r.At).First(r => r.Value), g.Where(r => r.Minute < 180).OrderedBy(r => r.At).Last(r => r.Value)))
                .As<Grown>());
            Dictionary<string, Hourly> even = Oracle([.. rows.Where(r => r.Minute < 180)], hourly: false).ToDictionary(h => h.Series);
            Dictionary<string, Hourly> all = Oracle(rows, hourly: false).ToDictionary(h => h.Series);
            Assert.All(filtered, g =>
            {
                Assert.Equal(even[g.Series].Increase, g.Increase);
                Assert.Equal(all[g.Series].First, g.First);
                Assert.Equal(even[g.Series].Last, g.Last);
            });
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task IntegerCountersGrowExactly()
    {
        Random random = new Random(5);
        // Counters that wrap now and then, read in no order: every group read again.
        Counted[] rows = [.. Enumerable.Range(0, 20_000).Select(i =>
        {
            long requests = (i / 7) * 1_000_003L % 5_000_000_000_000L;
            return new Counted($"s{i % 7}", i / 7, i % 211 == 0 ? null : requests, ((ulong)requests * 3) + (ulong)random.Next(3));
        }).OrderBy(_ => random.Next())];
        string path = await WriteRowsAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<CountedGrowth> read = await ListAsync(file.Scan<Counted>()
                .GroupBy(r => r.Series)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.OrderedBy(r => r.Tick).Increase(r => r.Requests), g.OrderedBy(r => r.Tick).Increase(r => r.Bytes)))
                .As<CountedGrowth>());
            List<CountedGrowth> expected = [.. rows.GroupBy(r => r.Series).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new CountedGrowth(
                g.Key,
                (long?)IntegerGrowth([.. g.Where(r => r.Requests is not null).Select(r => (r.Tick, (BigInteger)r.Requests!.Value))]),
                (ulong?)IntegerGrowth([.. g.Select(r => (r.Tick, (BigInteger)r.Bytes))])))];
            Assert.Equal(expected, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AQuestionInPartsAndAnEstimateRefuseAGrowth()
    {
        Point[] rows = Points(seed: 3);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation growth = file.Scan<Point>().GroupBy(r => r.Series).Select(g => (g.Key, g.OrderedBy(r => r.At).Increase(r => r.Value)));
            await Assert.ThrowsAsync<NotSupportedException>(async () => await growth.PartsAsync(2, Ct));
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (AggregationEstimate _ in growth.EstimateAsync(cancellationToken: Ct))
                {
                }
            });
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    public enum Layout
    {
        BySeries,
        ByInstant,
        Shuffled,
    }

    /// <summary>A series' points a minute apart.</summary>
    private static IEnumerable<Point> Series(string series, params double[] values) =>
        values.Select((value, i) => new Point(series, Start.AddMinutes(i), i, value));

    /// <summary>
    /// Forty counters read every fifteen seconds for six hours: rises of fractions no float holds exactly, falls to a small
    /// value now and then, nulls, NaNs, infinities, a −0, and points that share an instant with another value.
    /// </summary>
    private static Point[] Points(int seed)
    {
        Random random = new Random(seed);
        List<Point> rows = [];
        for (int s = 0; s < 40; s++)
        {
            double value = random.Next(1_000_000) * 0.1;
            for (int i = 0; i < 6 * 240; i++)
            {
                DateTimeOffset at = Start.AddSeconds(i * 15);
                value = random.Next(300) == 0 ? random.Next(50) * 0.3 : value + (random.Next(1_000) * 0.01);
                double? kept = random.Next(97) switch
                {
                    0 => null,
                    1 => double.NaN,
                    2 when random.Next(5) == 0 => double.PositiveInfinity,
                    3 when random.Next(5) == 0 => -0.0,
                    _ => value,
                };
                rows.Add(new Point($"s{s}", at, i / 4, kept));
                if (random.Next(250) == 0)
                {
                    rows.Add(new Point($"s{s}", at, i / 4, value - (random.Next(1, 100) * 0.7)));
                }
            }
        }

        return [.. rows];
    }

    private static Point[] Arrange(Point[] rows, Layout layout)
    {
        Random shuffle = new Random(17);
        return layout switch
        {
            Layout.BySeries => [.. rows.OrderBy(r => r.Series, StringComparer.Ordinal).ThenBy(r => r.At)],
            Layout.ByInstant => [.. rows.OrderBy(r => r.At).ThenBy(r => r.Series, StringComparer.Ordinal)],
            _ => [.. rows.OrderBy(_ => shuffle.Next())],
        };
    }

    /// <summary>The growth, first and last point of each series' hours, or of each series with <paramref name="hourly"/> false.</summary>
    private static IEnumerable<Hourly> Oracle(Point[] rows, bool hourly = true) =>
        rows.GroupBy(r => (r.Series, Hour: hourly ? new DateTimeOffset(r.At.Year, r.At.Month, r.At.Day, r.At.Hour, 0, 0, TimeSpan.Zero) : default))
            .Select(g =>
            {
                (DateTimeOffset At, double Value)[] points = [.. g
                    .Where(r => r.Value is double v && double.IsFinite(v))
                    .Select(r => (r.At, r.Value!.Value + 0.0))
                    .OrderBy(p => p.At).ThenBy(p => p.Item2)];
                return points.Length == 0
                    ? new Hourly(g.Key.Series, g.Key.Hour, null, null, null)
                    : new Hourly(g.Key.Series, g.Key.Hour, FloatGrowth([.. points.Select(p => p.Value)]), points[0].Value, points[^1].Value);
            });

    /// <summary>The growth of values in their order, exactly: the last less the first, plus each value a fall came from, rounded once.</summary>
    private static double FloatGrowth(double[] values)
    {
        List<double> terms = [values[^1], -values[0]];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                terms.Add(values[i - 1]);
            }
        }

        return ExactSum(terms);
    }

    /// <summary>The growth of integers in the order of their ticks, then of their values.</summary>
    private static BigInteger? IntegerGrowth((long Tick, BigInteger Value)[] points)
    {
        if (points.Length == 0)
        {
            return null;
        }

        BigInteger[] values = [.. points.OrderBy(p => p.Tick).ThenBy(p => p.Value).Select(p => p.Value)];
        BigInteger growth = 0;
        for (int i = 1; i < values.Length; i++)
        {
            growth += values[i] >= values[i - 1] ? values[i] - values[i - 1] : values[i];
        }

        return growth;
    }

    /// <summary>The exact sum of doubles, rounded once to the nearest, ties to even.</summary>
    private static double ExactSum(List<double> terms)
    {
        int lowest = int.MaxValue;
        List<(BigInteger Mantissa, int Exponent)> parts = [];
        foreach (double term in terms)
        {
            if (term == 0)
            {
                continue;
            }

            long bits = BitConverter.DoubleToInt64Bits(term);
            int field = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & ((1L << 52) - 1);
            BigInteger mantissa = field == 0 ? fraction : fraction | (1L << 52);
            int exponent = (field == 0 ? 1 : field) - 1075;
            parts.Add((term < 0 ? -mantissa : mantissa, exponent));
            lowest = Math.Min(lowest, exponent);
        }

        BigInteger sum = 0;
        foreach ((BigInteger mantissa, int exponent) in parts)
        {
            sum += mantissa << (exponent - lowest);
        }

        if (sum.IsZero)
        {
            return 0;
        }

        bool negative = sum.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(sum);
        int length = (int)magnitude.GetBitLength();
        int shift = Math.Max(0, length - 53);
        BigInteger kept = magnitude >> shift;
        if (shift > 0)
        {
            BigInteger rest = magnitude - (kept << shift);
            BigInteger half = BigInteger.One << (shift - 1);
            if (rest > half || (rest == half && !kept.IsEven))
            {
                kept += 1;
            }
        }

        double rounded = Math.ScaleB((double)kept, shift + lowest);
        return negative ? -rounded : rounded;
    }

    /// <summary>Whether two hours hold the same bits.</summary>
    private static bool Same(Hourly a, Hourly b) =>
        a.Series == b.Series && a.Hour == b.Hour && Bits(a.Increase) == Bits(b.Increase) && Bits(a.First) == Bits(b.First) && Bits(a.Last) == Bits(b.Last);

    private static long? Bits(double? value) => value is double v ? BitConverter.DoubleToInt64Bits(v) : null;

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

    /// <summary>Chunks of blocks of 512 rows: a file of many ranges, read on several lanes.</summary>
    private static Task<string> WriteAsync(Point[] rows) => WriteRowsAsync(rows);

    private static async Task<string> WriteRowsAsync<T>(T[] rows)
        where T : IVortexRecord<T>
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "growth");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"points-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(path, new VortexWriteOptions { RowBlockSize = 512, ChunkTargetBytes = 1 << 12 }))
        {
            await writer.WriteAsync<T>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Point(string Series, DateTimeOffset At, int Minute, double? Value);

    [VortexRecord]
    public partial record struct Grown(string Series, double? Increase, double? First, double? Last);

    [VortexRecord]
    public partial record struct Hourly(string Series, DateTimeOffset Hour, double? Increase, double? First, double? Last);

    [VortexRecord]
    public partial record struct Counted(string Series, long Tick, long? Requests, ulong Bytes);

    [VortexRecord]
    public partial record struct CountedGrowth(string Series, long? Requests, ulong? Bytes);
}
