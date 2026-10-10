using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// A scan's rows as columns of values, <c>scan.Select(r =&gt; …)</c>: one value or several through a
/// record, the window of <c>Skip</c> and <c>Take</c> and what it reads, the distinct values as they
/// stream, and the query syntax, each against LINQ to objects over the same rows.
/// </summary>
public sealed partial class SelectTests
{
    // Several splits of the default batch, so that a window reads fewer than the file holds.
    private const int Rows = 200_000;

    private static readonly string[] Cities = ["Arles", "Brest", "Caen", "Dax", "Évry", "Foix", "Gap"];

    private static readonly DateTime Origin = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task OneValuePerRowComesAsTheRowsHoldIt()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(rows.Select(r => r.City), await file.Scan<Trip>().Select(r => r.City).ToListAsync(Ct));
            Assert.Equal(rows.Where(r => r.Day >= 20).Select(r => r.Fare), await file.Scan<Trip>().Where(r => r.Day >= 20).Select(r => r.Fare).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.At).ToArray(), await file.Scan<Trip>().Select(r => r.At).ToArrayAsync(Ct));

            long sum = 0;
            await foreach (long id in file.Scan<Trip>().Select(r => r.Id).WithCancellation(Ct))
            {
                sum += id;
            }

            Assert.Equal(rows.Sum(r => r.Id), sum);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task SeveralValuesAreReadThroughTheirRecord()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<CityFare> records = await ListAsync(file.Scan<Trip>().Where(r => r.Day < 5).Select(r => (r.City, r.Fare)).As<CityFare>());
            Assert.Equal(rows.Where(r => r.Day < 5).Select(r => new CityFare(r.City, r.Fare)), records);

            // As batches, the default: the scan's own columns, borrowed.
            long rowsSeen = 0;
            double fares = 0;
            await foreach (Columns<CityFare> batch in file.Scan<Trip>().Select(r => (r.City, r.Fare)).As<CityFare>())
            {
                Column<double?> column = batch.Column<double?>(1);
                for (int i = 0; i < batch.RowCount; i++)
                {
                    fares += column[i] ?? 0;
                }

                rowsSeen += batch.RowCount;
            }

            Assert.Equal(rows.Length, rowsSeen);
            Assert.Equal(rows.Sum(r => r.Fare ?? 0), fares, 6);

            // A member that stores the timestamp in another unit takes it converted.
            List<IdAt> stamps = await ListAsync(file.Scan<Trip>().Select(r => (r.Id, r.At)).Take(1_000).As<IdAt>());
            Assert.Equal(rows.Take(1_000).Select(r => new IdAt(r.Id, r.At)), stamps);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task SkipAndTakeAreTheWindowOfTheRowsKept()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(rows.Skip(70_000).Take(100).Select(r => r.Id), await file.Scan<Trip>().Select(r => r.Id).Skip(70_000).Take(100).ToListAsync(Ct));
            Assert.Equal(rows.Skip(5).Take(3).Skip(1).Select(r => r.Id), await file.Scan<Trip>().Select(r => r.Id).Skip(5).Take(3).Skip(1).ToListAsync(Ct));
            Assert.Equal(
                rows.Where(r => r.City == "Caen").Skip(10).Take(25).Select(r => r.Id),
                await file.Scan<Trip>().Where(r => r.City == "Caen").Select(r => r.Id).Skip(10).Take(25).ToListAsync(Ct));
            Assert.Empty(await file.Scan<Trip>().Select(r => r.Id).Skip(Rows).ToListAsync(Ct));
            Assert.Equal(
                rows.Take(7).Select(r => new CityFare(r.City, r.Fare)),
                await ListAsync(file.Scan<Trip>().Select(r => (r.City, r.Fare)).Take(7).As<CityFare>()));

            // Ten rows read the split that holds them, not the file.
            Projection<long> ten = file.Scan<Trip>().Select(r => r.Id).Take(10);
            Assert.Equal(10, (await ten.ToListAsync(Ct)).Count);
            Projection<long> all = file.Scan<Trip>().Select(r => r.Id);
            Assert.Equal(Rows, (await all.ToListAsync(Ct)).Count);
            Assert.Equal(1, ten.Metrics.Batches);
            Assert.True(ten.Metrics.Requests < all.Metrics.Requests, $"{ten.Metrics.Requests} requests against {all.Metrics.Requests}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task DistinctValuesStreamAsTheyAreMet()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(rows.Select(r => r.City).Distinct(), await file.Scan<Trip>().Select(r => r.City).Distinct().ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Fare).Distinct().Order(), (await file.Scan<Trip>().Select(r => r.Fare).Distinct().ToListAsync(Ct)).Order());
            Assert.Equal(Rows, (await file.Scan<Trip>().Select(r => r.Id).Distinct().ToListAsync(Ct)).Count);

            List<CityDay> pairs = await ListAsync(file.Scan<Trip>().Select(r => (r.City, r.Day)).Distinct().As<CityDay>());
            Assert.Equal(rows.Select(r => new CityDay(r.City, r.Day)).Distinct().OrderBy(p => p.City, StringComparer.Ordinal).ThenBy(p => p.Day),
                pairs.OrderBy(p => p.City, StringComparer.Ordinal).ThenBy(p => p.Day));

            // The first three cities met, without reading past the rows that hold them.
            Aggregation<string> three = file.Scan<Trip>().Select(r => r.City).Distinct().Take(3);
            Assert.Equal(rows.Select(r => r.City).Distinct().Take(3), await three.ToListAsync(Ct));
            Assert.Equal(1, three.Metrics.Batches);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheQuerySyntaxSelectsAsTheMethodsDo()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Projection<string> cities =
                from r in file.Scan<Trip>()
                where r.Day >= 25
                select r.City;
            Assert.Equal(rows.Where(r => r.Day >= 25).Select(r => r.City), await cities.ToListAsync(Ct));

            Projection pairs =
                from r in file.Scan<Trip>()
                where r.Fare > 3.0
                select (r.City, r.Fare);
            Assert.Equal(rows.Where(r => r.Fare > 3.0).Select(r => new CityFare(r.City, r.Fare)), await ListAsync(pairs.As<CityFare>()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AProjectionsScanTakesTheOperatorsOfAScan()
    {
        (Trip[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Scan<CityFare> fares = file.Scan<Trip>().Select(r => (r.City, r.Fare)).As<CityFare>();
            List<CityCount> counted = await ListAsync(fares.Where(f => f.Fare > 2.0).GroupBy(f => f.City).Select(g => (g.Key, g.Count())).As<CityCount>());
            Assert.Equal(
                rows.Where(r => r.Fare > 2.0).GroupBy(r => r.City).Select(g => new CityCount(g.Key, g.Count())).OrderBy(c => c.City, StringComparer.Ordinal),
                counted.OrderBy(c => c.City, StringComparer.Ordinal));
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

    private static async Task<(Trip[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "selects");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trips-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Trip[] rows = new Trip[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Trip(
                Cities[row / 13 % Cities.Length],
                row / 5_000,
                Origin.AddSeconds(row * 7L),
                row % 17 == 0 ? null : row % 64 / 8.0,
                row);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Trip>(path))
        {
            await writer.WriteAsync<Trip>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    [VortexRecord]
    public partial record struct Trip(string City, int Day, [VortexColumn(Unit = TimeUnit.Nanoseconds)] DateTime At, double? Fare, long Id);

    [VortexRecord]
    public partial record struct CityFare(string City, double? Fare);

    [VortexRecord]
    public partial record struct IdAt(long Id, DateTime At);

    [VortexRecord]
    public partial record struct CityDay(string City, int Day);
}
