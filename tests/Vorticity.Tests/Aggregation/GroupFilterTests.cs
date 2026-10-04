using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// <c>Where</c> on groups: a predicate over the groups' results in three-valued logic, its conjuncts
/// on the key alone moved to the rows, where they prune as a filter does, and the operators applied
/// in the order written; each against LINQ to objects over the same rows.
/// </summary>
public sealed partial class GroupFilterTests
{
    private const int Rows = 60_000;

    private static readonly string[] Cities = ["Arles", "Brest", "Caen", "Dax", "Évry", "Foix", "Gap", "Hyères"];

    [Fact]
    public async Task AFilterOnAggregatesKeepsTheGroupsItHoldsFor()
    {
        (Measure[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(
                Expected(rows.GroupBy(r => (r.City, r.Day)).Where(g => g.Count() > 120)),
                Sorted(await ListAsync(file.Scan<Measure>().GroupBy(r => (r.City, r.Day)).Where(g => g.Count() > 120L).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>())));

            // A null mean is unknown: neither above 30 nor not above it, and only == null holds for it.
            Assert.Equal(
                rows.GroupBy(r => r.City).Where(g => g.Average(r => r.Value) > 30.0).Select(g => g.Key).Order(StringComparer.Ordinal),
                (await file.Scan<Measure>().GroupBy(r => r.City).Where(g => g.Average(x => x.Value) > 30.0).Select(g => g.Key).ToListAsync(Ct)).Order(StringComparer.Ordinal));
            Assert.Equal(
                rows.GroupBy(r => r.City).Where(g => !(g.Average(r => r.Value) > 30.0) && g.Average(r => r.Value) is not null).Select(g => g.Key).Order(StringComparer.Ordinal),
                (await file.Scan<Measure>().GroupBy(r => r.City).Where(g => !(g.Average(x => x.Value) > 30.0)).Select(g => g.Key).ToListAsync(Ct)).Order(StringComparer.Ordinal));
            Assert.Equal(
                rows.GroupBy(r => r.City).Where(g => g.Average(r => r.Value) is null).Select(g => g.Key).Order(StringComparer.Ordinal),
                (await file.Scan<Measure>().GroupBy(r => r.City).Where(g => g.Average(x => x.Value) == null).Select(g => g.Key).ToListAsync(Ct)).Order(StringComparer.Ordinal));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilterOnTheKeyAloneFiltersTheRowsAndPrunesAsTheirFilterDoes()
    {
        (Measure[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation having = file.Scan<Measure>().GroupBy(r => (r.City, r.Day)).Where(g => g.Key.Day >= 40 & g.Key.City != "Caen").Select(g => (g.Key.City, g.Key.Day, g.Count()));
            Vorticity.Aggregation filtered = file.Scan<Measure>().Where(r => r.Day >= 40 & r.City != "Caen").GroupBy(r => (r.City, r.Day)).Select(g => (g.Key.City, g.Key.Day, g.Count()));
            ScanPlan pushed = await having.ExplainAsync(Ct);
            ScanPlan written = await filtered.ExplainAsync(Ct);
            Assert.Equal(written.LiveBlocks, pushed.LiveBlocks);
            Assert.True(pushed.LiveBlocks < pushed.Blocks, "the filter on the key pruned nothing");

            Assert.Equal(
                Expected(rows.Where(r => r.Day >= 40 && r.City != "Caen").GroupBy(r => (r.City, r.Day))),
                Sorted(await ListAsync(having.As<CityDayCount>())));

            // A conjunct on the key beside one on an aggregate: the first filters rows, the second groups.
            Assert.Equal(
                Expected(rows.GroupBy(r => (r.City, r.Day)).Where(g => g.Key.Day < 10 && g.Count() > 120)),
                Sorted(await ListAsync(file.Scan<Measure>().GroupBy(r => (r.City, r.Day)).Where(g => g.Key.Day < 10 && g.Count() > 120L)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>())));

            // A disjunction of the two is decided on the groups.
            Assert.Equal(
                Expected(rows.GroupBy(r => (r.City, r.Day)).Where(g => g.Key.City == "Gap" || g.Count() > 124)),
                Sorted(await ListAsync(file.Scan<Measure>().GroupBy(r => (r.City, r.Day)).Where(g => g.Key.City == "Gap" || g.Count() > 124L)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>())));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheOperatorsApplyInTheOrderWritten()
    {
        (Measure[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<int> days = await file.Scan<Measure>().GroupBy(r => r.Day).Select(g => g.Key).ToListAsync(Ct);

            // A Where after a Take filters the groups taken; before it, the groups it takes.
            List<int> takenThenFiltered = await file.Scan<Measure>().GroupBy(r => r.Day).Take(10).Where(g => g.Key >= 5).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(days.Take(10).Where(d => d >= 5), takenThenFiltered);
            List<int> filteredThenTaken = await file.Scan<Measure>().GroupBy(r => r.Day).Where(g => g.Key >= 5).Take(10).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(days.Where(d => d >= 5).Take(10), filteredThenTaken);
            Assert.Equal(days.Skip(3).Take(4), await file.Scan<Measure>().GroupBy(r => r.Day).Skip(3).Take(4).Select(g => g.Key).ToListAsync(Ct));

            // The query syntax is the same chain.
            List<long> counts = await (
                from r in file.Scan<Measure>()
                group r by r.City into g
                where g.Count() > 7_000L
                select g.Count()).ToListAsync(Ct);
            Assert.Equal(rows.GroupBy(r => r.City).Select(g => (long)g.Count()).Where(c => c > 7_000).Order(), counts.Order());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task WhatAFilterOnGroupsCannotCompareIsRefused()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Sym<double?> outside = default;
            file.Scan<Measure>().Where(r =>
            {
                outside = r.Value;
                return r.Day >= 0;
            });

            InvalidOperationException column = Assert.Throws<InvalidOperationException>(
                () => file.Scan<Measure>().GroupBy(r => r.City).Where(g => outside > 3.0));
            Assert.Contains("neither a component of the key nor an aggregate", column.Message, StringComparison.Ordinal);

            Sym<long> count = default;
            file.Scan<Measure>().GroupBy(r => r.City).Where(g =>
            {
                count = g.Count();
                return Predicate.All;
            });
            InvalidOperationException rows = Assert.Throws<InvalidOperationException>(() => file.Scan<Measure>().Where(r => count > 3L));
            Assert.Contains("filtered by a Where after the GroupBy", rows.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<CityDayCount> Expected(IEnumerable<IGrouping<(string City, int Day), Measure>> groups) =>
        Sorted(groups.Select(g => new CityDayCount(g.Key.City, g.Key.Day, g.Count())));

    private static List<CityDayCount> Sorted(IEnumerable<CityDayCount> groups) =>
        [.. groups.OrderBy(g => g.City, StringComparer.Ordinal).ThenBy(g => g.Day)];

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

    private static async Task<(Measure[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-filters");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"measures-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Measure[] rows = new Measure[Rows];
        for (int row = 0; row < Rows; row++)
        {
            string city = Cities[row / 3 % Cities.Length];

            // Foix measures nothing: its mean is null.
            double? value = city == "Foix" ? null : row % 11 == 0 ? null : row % 70;
            rows[row] = new Measure(city, row / 1_000, value);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Measure>(path))
        {
            await writer.WriteAsync<Measure>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Measure(string City, int Day, double? Value);

    [VortexRecord]
    public partial record struct CityDayCount(string City, int Day, long Count);
}
