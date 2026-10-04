using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The order of groups: by an aggregate or a key, ascending or descending, nulls last in both
/// directions, NaN after +∞, text bytewise, ties broken by ThenBy and then by the key; a top-k that
/// is the full sort's first k; the same order at every degree; and the order of a result's scan.
/// </summary>
public sealed partial class GroupOrderTests
{
    private const int Rows = 120_000;

    private static readonly string[] Cities = ["Arles", "Brest", "Caen", "Dax", "Évry", "Foix", "Gap", "Hyères", "Ivry"];

    private static readonly double?[] Readings = [double.NaN, -0.0, 0.0, double.PositiveInfinity, double.NegativeInfinity, null, 1.5, -2.5];

    [Fact]
    public async Task GroupsComeInTheOrderOfAnAggregateThenOfTheirKey()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityDayCount> expected = [.. rows.GroupBy(r => (r.City, r.Day))
                .Select(g => new CityDayCount(g.Key.City, g.Key.Day, g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.City, StringComparer.Ordinal).ThenBy(g => g.Day)];
            List<CityDayCount> ordered = await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                .OrderByDescending(g => g.Count()).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>());
            Assert.Equal(expected, ordered);

            // The query syntax, with a ThenBy, and a top-k that is the full sort's first ten.
            Vorticity.Aggregation query =
                from r in file.Scan<Visit>()
                group r by (r.City, r.Day) into g
                orderby g.Key.Day descending, g.Count(), g.Key.City
                select (g.Key.City, g.Key.Day, g.Count());
            List<CityDayCount> written = await ListAsync(query.As<CityDayCount>());
            List<CityDayCount> sorted = [.. expected.OrderByDescending(g => g.Day).ThenBy(g => g.Count).ThenBy(g => g.City, StringComparer.Ordinal)];
            Assert.Equal(sorted, written);
            Assert.Equal(
                sorted.Take(10),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                    .OrderByDescending(g => g.Key.Day).ThenBy(g => g.Count()).ThenBy(g => g.Key.City).Take(10)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>()));
            Assert.Equal(
                sorted.Skip(5).Take(3),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day))
                    .OrderByDescending(g => g.Key.Day).ThenBy(g => g.Count()).ThenBy(g => g.Key.City)
                    .Select(g => (g.Key.City, g.Key.Day, g.Count())).Skip(5).Take(3).As<CityDayCount>()));

            // A composite key orders by its components in turn; text bytewise.
            Assert.Equal(
                expected.OrderBy(g => g.City, StringComparer.Ordinal).ThenBy(g => g.Day),
                await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day)).OrderBy(g => g.Key).Select(g => (g.Key.City, g.Key.Day, g.Count())).As<CityDayCount>()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task NullsComeLastAndNaNAfterInfinityInBothDirections()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<double?> ascending = await file.Scan<Visit>().GroupBy(r => r.Reading).OrderBy(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal([double.NegativeInfinity, -2.5, 0.0, 1.5, double.PositiveInfinity, double.NaN, null], ascending.Select(Zero));
            List<double?> descending = await file.Scan<Visit>().GroupBy(r => r.Reading).OrderByDescending(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal([double.NaN, double.PositiveInfinity, 1.5, 0.0, -2.5, double.NegativeInfinity, null], descending.Select(Zero));

            // A null mean is last, ascending and descending.
            List<string> byMean = await file.Scan<Visit>().GroupBy(r => r.City).OrderByDescending(g => g.Average(x => x.Score)).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal("Ivry", byMean[^1]);
            List<string> byMeanUp = await file.Scan<Visit>().GroupBy(r => r.City).OrderBy(g => g.Average(x => x.Score)).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal("Ivry", byMeanUp[^1]);
            Assert.Equal(
                rows.Where(r => r.City != "Ivry").GroupBy(r => r.City).OrderBy(g => g.Average(r => r.Score)).Select(g => g.Key),
                byMeanUp.SkipLast(1));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheOrderIsTheSameAtEveryDegreeAndPastOnePartOfTheSort()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            List<long>[] orders = new List<long>[2];
            int[] degrees = [1, 4];
            for (int d = 0; d < degrees.Length; d++)
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degrees[d]);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                orders[d] = await file.Scan<Visit>().GroupBy(r => r.Id).OrderBy(g => g.Count()).Select(g => g.Key).ToListAsync(Ct);
            }

            // More groups than one part of the sort holds: runs sorted apart, then merged.
            Assert.Equal(rows.Select(r => r.Id).Distinct().Count(), orders[0].Count);
            Assert.True(orders[0].Count > 65_536);
            Assert.Equal(rows.GroupBy(r => r.Id).OrderBy(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key), orders[0]);
            Assert.Equal(orders[0], orders[1]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultsScanIsSortedByItsColumn()
    {
        (Visit[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityDayCount> sorted = await ListAsync(file.Scan<Visit>().GroupBy(r => (r.City, r.Day)).Select(g => (g.Key.City, g.Key.Day, g.Count()))
                .As<CityDayCount>().OrderByDescending(g => g.Count));
            Assert.Equal(
                rows.GroupBy(r => (r.City, r.Day)).Select(g => (long)g.Count()).OrderByDescending(c => c),
                sorted.Select(g => g.Count));

            List<CityReading> readings = await ListAsync(file.Scan<Visit>().Where(r => r.Id < 40).Select(r => (r.City, r.Reading)).As<CityReading>().OrderBy(r => r.Reading));
            Assert.Equal(
                [.. rows.Where(r => r.Id < 40).Select(r => r.Reading).OrderBy(r => r is null ? 2 : double.IsNaN(r.Value) ? 1 : 0).ThenBy(r => r ?? 0).Select(Zero)],
                readings.Select(r => Zero(r.Reading)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AStateHasNoOrder()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Throws<ArgumentException>(() => file.Scan<Visit>().GroupBy(r => r.City).OrderBy(g => g.Aggregate<long, UnitsTotal, long>(x => x.Id)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A zero whatever its sign, which an order holds equal.</summary>
    private static double? Zero(double? value) => value == 0 ? 0.0 : value;

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

    private static async Task<(Visit[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-orders");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"visits-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Visit[] rows = new Visit[Rows];
        for (int row = 0; row < Rows; row++)
        {
            string city = Cities[row / 5 % Cities.Length];

            // Ivry scores nothing: its mean is null.
            rows[row] = new Visit(city, row % 37, Readings[row % Readings.Length], city == "Ivry" ? null : row % 97, row % 90_000);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            await writer.WriteAsync<Visit>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Visit(string City, int Day, double? Reading, double? Score, long Id);

    [VortexRecord]
    public partial record struct CityDayCount(string City, int Day, long Count);

    [VortexRecord]
    public partial record struct CityReading(string City, double? Reading);
}
