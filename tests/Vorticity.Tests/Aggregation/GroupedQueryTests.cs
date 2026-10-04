using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The surface of a group by: keys named by their tuple, of any length, results that are a key or
/// an aggregate and nothing else, one state for one aggregate however often it is written, and the
/// query syntax building the plan the method chain builds.
/// </summary>
public sealed class GroupedQueryTests
{
    private const int Rows = 50_000;

    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille", "Brest"];

    [Fact]
    public async Task AKeyIsNamedByItsTuple()
    {
        (Grouped[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Dictionary<(string, int), (long, double?)> grouped = [];
            await foreach ((string city, int day, long count, double? mean) in file.Scan<Grouped>()
                .GroupBy(r => (r.City, r.Day))
                .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(r => r.Score)))
                .As<CityDay>()
                .ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                grouped.Add((city, day), (count, mean));
            }

            Dictionary<(string, int), (long, double?)> expected = rows
                .GroupBy(r => (r.City, r.Day))
                .ToDictionary(g => g.Key, g => ((long)g.Count(), (double?)g.Average(r => r.Score)));
            Assert.Equal(expected.Count, grouped.Count);
            foreach (((string, int) key, (long count, double? mean)) in expected)
            {
                Assert.Equal(count, grouped[key].Item1);
                Assert.Equal(mean!.Value, grouped[key].Item2!.Value, 9);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // Past the four columns the arity overloads stopped at, and with nulls in two of them.
    [Fact]
    public async Task AKeyOfFiveColumnsGroupsAsTheRowsDo()
    {
        (Grouped[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Dictionary<(string, int, bool, int?, long?), long> grouped = [];
            await foreach ((string city, int day, bool flag, int? code, long? bucket, long count) in file.Scan<Grouped>()
                .GroupBy(r => (r.City, r.Day, r.Flag, r.Code, r.Bucket))
                .Select(g => (g.Key.City, g.Key.Day, g.Key.Flag, g.Key.Code, g.Key.Bucket, g.Count()))
                .As<FiveKeys>()
                .ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                grouped.Add((city, day, flag, code, bucket), count);
            }

            Dictionary<(string, int, bool, int?, long?), long> expected = rows
                .GroupBy(r => (r.City, r.Day, r.Flag, r.Code, r.Bucket))
                .ToDictionary(g => g.Key, g => (long)g.Count());
            Assert.Equal(expected.OrderBy(p => p.Key), grouped.OrderBy(p => p.Key));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AKeyComponentThatIsNotASymbolIsRefused()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            // Through a method, which VX1009 does not see into: the refusal at run time is what is tested.
            ArgumentException refused = Assert.Throws<ArgumentException>(() => file.Scan<Grouped>().GroupBy(r => WithAConstant(r.City)));
            Assert.Contains("Component 2", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // A column symbol captured from outside the group is neither a component of the key nor an aggregate.
    [Fact]
    public async Task AResultThatIsNeitherAKeyNorAnAggregateIsRefused()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Sym<string> outside = default;
            file.Scan<Grouped>().Where(r =>
            {
                outside = r.City;
                return r.Day >= 0;
            });
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => file.Scan<Grouped>().GroupBy(r => r.City).Select(g => (outside, g.Count())));
            Assert.Contains("neither an aggregate nor a component of the key", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task OneAggregateWrittenTwiceIsOneState()
    {
        (Grouped[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Vorticity.Aggregation twice = file.Scan<Grouped>()
                .GroupBy(r => r.City)
                .Select(g => (g.Key, g.Count(), g.Count(), g.Average(r => r.Score), g.Average(r => r.Score)));
            Assert.Equal(2, twice.Plan.Aggregates.Length);
            await foreach ((string _, long count, long again, double? mean, double? same) in twice.As<Twice>().ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(count, again);
                Assert.Equal(mean, same);
            }

            Assert.Equal(rows.Length, rows.GroupBy(r => r.City).Sum(g => g.Count()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheQuerySyntaxBuildsTheMethodChainsPlan()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Vorticity.Aggregation query =
                from r in file.Scan<Grouped>()
                where r.Day >= 10
                group r by (r.City, r.Day) into g
                select (g.Key.City, g.Key.Day, g.Count(), g.Average(x => x.Score));
            Vorticity.Aggregation chain = file.Scan<Grouped>()
                .Where(r => r.Day >= 10)
                .GroupBy(r => (r.City, r.Day))
                .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(x => x.Score)));

            Assert.Equal(Shape(chain.Plan), Shape(query.Plan));
            Assert.Equal(await RowsAsync(chain), await RowsAsync(query));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static (Sym<string>, int) WithAConstant(Sym<string> city) => (city, 42);

    private static string Shape(AggregationPlan plan) =>
        $"keys {string.Join(", ", plan.Keys.Select(k => k.Path))}; aggregates {string.Join(", ", plan.Aggregates.Select(a => a.Identity))}";

    private static async Task<List<CityDay>> RowsAsync(Vorticity.Aggregation aggregation)
    {
        List<CityDay> rows = [];
        await foreach (CityDay row in aggregation.As<CityDay>().ToRecordsAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        rows.Sort((a, b) => (a.City, a.Day).CompareTo((b.City, b.Day)));
        return rows;
    }

    private static async Task<(Grouped[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "grouped-queries");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"grouped-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Grouped[] rows = new Grouped[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Grouped(
                Cities[row / 3 % Cities.Length],
                row / 1_000,
                row % 4 == 0,
                row % 9 == 0 ? null : row % 6,
                row % 13 == 0 ? null : row % 5 * 1_000_000_000L,
                row % 100 / 4.0);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Grouped>(path))
        {
            await writer.WriteAsync<Grouped>(rows, TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return (rows, path);
    }
}

[VortexRecord]
public partial record struct Grouped(string City, int Day, bool Flag, int? Code, long? Bucket, double Score);

/// <summary>A city, a day, its rows and their mean score.</summary>
[VortexRecord]
public partial record struct CityDay(string City, int Day, long Count, double? Mean);

/// <summary>A key of five columns and its rows.</summary>
[VortexRecord]
public partial record struct FiveKeys(string City, int Day, bool Flag, int? Code, long? Bucket, long Count);

/// <summary>A count and a mean, each written twice.</summary>
[VortexRecord]
public partial record struct Twice(string City, long Count, long Again, double? Mean, double? Same);
