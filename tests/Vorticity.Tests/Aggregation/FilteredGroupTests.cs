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
/// A filtered group, SQL's <c>FILTER (WHERE …)</c>: its aggregates read the rows its predicate keeps,
/// a row counted where the predicate is true and not where it is unknown; <c>Count(p)</c>,
/// <c>Any</c> and <c>All</c> made of it, in a group by, after it, and over a whole scan, against
/// LINQ over the same rows at every degree.
/// </summary>
public sealed partial class FilteredGroupTests
{
    private const int Rows = 40_000;

    private static readonly string[] Services = ["auth", "billing", "catalog", "search", "upload"];

    private static readonly int[] Statuses = [200, 200, 200, 404, 500, 200, 503, 200, 302];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheAggregatesOfAFilteredGroupReadItsRowsAlone(int degree)
    {
        (Request[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<ServiceErrors> groups = await ListAsync(file.Scan<Request>()
                .GroupBy(r => r.Service)
                .Select(g => (
                    g.Key,
                    g.Count(),
                    g.Count(x => x.Status >= 500),
                    g.Where(x => x.Status >= 500).Average(x => x.DurationMs),
                    g.Where(x => x.Status >= 500).Where(x => x.Region == "eu").Sum(x => x.DurationMs),
                    g.Where(x => x.Status >= 500).Max(x => x.Day),
                    g.Where(x => x.Status == 404).CountDistinct(x => x.Day),
                    g.Any(x => x.Status == 503),
                    g.All(x => x.DurationMs < 300.0),
                    g.All(x => x.Status != 418)))
                .As<ServiceErrors>());

            Assert.Equal(Services.Length, groups.Count);
            foreach (ServiceErrors group in groups)
            {
                Request[] of = [.. rows.Where(r => r.Service == group.Service)];
                Request[] errors = [.. of.Where(r => r.Status >= 500)];
                Assert.Equal(of.Length, group.Requests);
                Assert.Equal(errors.Length, group.Errors);
                Assert.Equal(errors.Average(r => r.DurationMs), group.ErrorMean!.Value, 9);
                Assert.Equal(errors.Where(r => r.Region == "eu").Sum(r => r.DurationMs), group.EuErrorSum, 6);
                Assert.Equal(errors.Max(r => r.Day), group.LastErrorDay);
                Assert.Equal(of.Where(r => r.Status == 404).Select(r => r.Day).Distinct().Count(), group.NotFoundDays);
                Assert.Equal(of.Any(r => r.Status == 503), group.Unavailable);
                Assert.Equal(of.All(r => r.DurationMs < 300.0), group.AllFast);

                // A null status makes the predicate unknown, which is not true: not every row is not a teapot.
                Assert.False(group.NoTeapot);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilteredGroupThatKeepsNoRowHasNoValue()
    {
        (Request[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<EmptyFilter> groups = await ListAsync(file.Scan<Request>()
                .GroupBy(r => r.Service)
                .Select(g => (
                    g.Key,
                    g.Count(x => x.Status == 999),
                    g.Where(x => x.Status == 999).Average(x => x.DurationMs),
                    g.Where(x => x.Status == 999).Min(x => x.Region),
                    g.Any(x => x.Status == 999),
                    g.Where(x => x.Status == 999).All(x => x.Status == 200),
                    g.Count(x => Predicate.None),
                    g.All(x => Predicate.All)))
                .As<EmptyFilter>());

            Assert.Equal(Services.Length, groups.Count);
            Assert.All(groups, g => Assert.Equal(new EmptyFilter(g.Service, 0, null, null, false, true, 0, true), g));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilteredAggregateIsFilteredOnAndOrderedBy()
    {
        (Request[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<DayErrors> days = await ListAsync(file.Scan<Request>()
                .GroupBy(r => (r.Service, r.Day))
                .Where(g => g.Count(x => x.Status >= 500) > 120L)
                .OrderByDescending(g => g.Count(x => x.Status >= 500))
                .Take(5)
                .Select(g => (g.Key.Service, g.Key.Day, g.Count(x => x.Status >= 500)))
                .As<DayErrors>());

            List<DayErrors> expected = [.. rows.GroupBy(r => (r.Service, r.Day))
                .Select(g => new DayErrors(g.Key.Service, g.Key.Day, g.Count(r => r.Status >= 500)))
                .Where(g => g.Errors > 120)
                .OrderByDescending(g => g.Errors).ThenBy(g => g.Service, StringComparer.Ordinal).ThenBy(g => g.Day)
                .Take(5)];
            Assert.Equal(expected, days);

            // A bool of the group is compared as any result is, and ordered with false first.
            List<DayFlags> flags = await ListAsync(file.Scan<Request>()
                .GroupBy(r => r.Day)
                .Where(g => g.Any(x => x.DurationMs > 330.0 & x.Region == "us").IsTrue | g.Count() < 0L)
                .OrderBy(g => g.All(x => x.Status != 302))
                .ThenBy(g => g.Key)
                .Select(g => (g.Key, g.Count(x => x.DurationMs > 330.0 & x.Region == "us"), g.All(x => x.Status != 302)))
                .As<DayFlags>());
            Assert.Equal(
                rows.GroupBy(r => r.Day)
                    .Where(g => g.Any(r => r.DurationMs > 330.0 && r.Region == "us"))
                    .Select(g => new DayFlags(g.Key, g.Count(r => r.DurationMs > 330.0 && r.Region == "us"), g.All(r => r.Status is not null && r.Status != 302)))
                    .OrderBy(g => g.Every).ThenBy(g => g.Day),
                flags);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task OneFilterIsOneAggregateWhereverItIsWritten()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation query = file.Scan<Request>()
                .GroupBy(r => r.Service)
                .Where(g => g.Count(x => x.Status >= 500) > 0L)
                .Select(g => (g.Key, g.Count(), g.Count(x => x.Status >= 500), g.Where(x => x.Status >= 500).Count(), g.Where(x => x.Day < 3).Where(x => x.Status >= 500).Count(), g.Where(x => x.Status >= 500).Where(x => x.Day < 3).Count()));
            Assert.Equal(3, ((AggregationQuery)query.Query).Plan.Aggregates.Length);

            // A filter on a row reads columns, never a result.
            Assert.Throws<InvalidOperationException>(() => file.Scan<Request>().GroupBy(r => r.Service).Select(g => g.Where(x => g.Count() > 3L).Count()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheAggregatesOfAScanAreFilteredToo(int degree)
    {
        (Request[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Totals totals = await file.Scan<Request>()
                .Where(r => r.Day >= 2)
                .AggregateAsync<Totals>(a => (a.Count(), a.Count(x => x.Status >= 500), a.Where(x => x.Region == "eu").Average(x => x.DurationMs), a.Any(x => x.Status == 503), a.All(x => x.Day < 10)), Ct);

            Request[] kept = [.. rows.Where(r => r.Day >= 2)];
            Assert.Equal(kept.Length, totals.Rows);
            Assert.Equal(kept.Count(r => r.Status >= 500), totals.Errors);
            Assert.Equal(kept.Where(r => r.Region == "eu").Average(r => r.DurationMs), totals.EuMean!.Value, 9);
            Assert.Equal(kept.Any(r => r.Status == 503), totals.Unavailable);
            Assert.Equal(kept.All(r => r.Day < 10), totals.Early);

            // A count or an any of one filter alone is the scan's, its filter joined.
            Assert.Equal(
                new Answers(kept.Count(r => r.Status >= 500 && r.Region == "us"), kept.Any(r => r.Status >= 500 && r.Region == "us")),
                await file.Scan<Request>().Where(r => r.Day >= 2).AggregateAsync<Answers>(a => (a.Count(x => x.Status >= 500 & x.Region == "us"), a.Any(x => x.Status >= 500 & x.Region == "us")), Ct));
            Assert.Equal(
                new Answers(0, false),
                await file.Scan<Request>().AggregateAsync<Answers>(a => (a.Count(x => Predicate.None), a.Where(x => x.Day > 5).Any(x => x.Day < 5)), Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AGroupByThatStreamsFiltersItsGroupsAsTheyClose()
    {
        (Request[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation byDay = file.Scan<Request>().GroupBy(r => r.Day).Select(g => (g.Key, g.Count(x => x.Status >= 500), g.All(x => x.Region != "us")));
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)byDay.Query) >= 0);
            Assert.Equal(
                rows.GroupBy(r => r.Day).OrderBy(g => g.Key).Select(g => new DayFlags(g.Key, g.Count(r => r.Status >= 500), g.All(r => r.Region is not null && r.Region != "us"))),
                await ListAsync(byDay.As<DayFlags>()));
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

    private static async Task<(Request[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "filtered-groups");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"requests-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Request[] rows = new Request[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Request(
                Services[row * 7 % Services.Length],
                row / 4_000,
                row % 11 == 0 ? null : Statuses[row % Statuses.Length],
                row % 997 / 3.0,
                row % 7 == 0 ? null : row % 3 == 0 ? "us" : "eu");
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Request>(path))
        {
            await writer.WriteAsync<Request>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Request(string Service, int Day, int? Status, double DurationMs, string? Region);

    [VortexRecord]
    public partial record struct ServiceErrors(
        string Service, long Requests, long Errors, double? ErrorMean, double EuErrorSum, int? LastErrorDay, long NotFoundDays, bool Unavailable, bool AllFast, bool NoTeapot);

    [VortexRecord]
    public partial record struct EmptyFilter(string Service, long Count, double? Mean, string? First, bool Any, bool All, long None, bool Every);

    [VortexRecord]
    public partial record struct DayErrors(string Service, int Day, long Errors);

    [VortexRecord]
    public partial record struct DayFlags(int Day, long Count, bool Every);

    [VortexRecord]
    public partial record struct Totals(long Rows, long Errors, double? EuMean, bool Unavailable, bool Early);

    [VortexRecord]
    public partial record struct Answers(long Count, bool Any);
}
