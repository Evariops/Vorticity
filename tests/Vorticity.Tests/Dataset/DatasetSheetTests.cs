// The column sheets of a dataset's sealed objects, and the objects they pass over.
//
// WHAT IS HELD: an object a job writes in time slices keeps a sheet of each column, which a read with no open of the
// object fetches for every column in one request; the sheet of a column whose values repeat names its most frequent
// values with their exact rows, and that of an identifier none; a question on a value an object lacks passes the
// object over unopened, its answer the same, on one lane as on eight; and an object too small for a question to read
// its sheets before it keeps none.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSheetTests : IClassFixture<DatasetSheetTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search", "media"];

    private const int Hours = 3;

    private const int RowsPerHour = 30_000;

    private readonly Fixture _fixture;

    public DatasetSheetTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASealedObjectKeepsASheetOfEachColumnReadInOneRequest()
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        int[] fields = [.. Enumerable.Range(0, dataset.Schema.Count)];
        int sealedObjects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { Sheets.Count: > 0 })
            {
                continue;
            }

            sealedObjects++;
            store.Reset();
            ColumnSheet?[] sheets = await dataset.ReadSheetsAsync(held.Entry, fields, Ct);
            Assert.Equal(1, store.Requests);

            // The rows of the object's hour, which its first row's instant names.
            int hour = (int)(held.FirstRow / RowsPerHour);
            Call[] rows = _fixture.Rows[(hour * RowsPerHour)..((hour + 1) * RowsPerHour)];
            Assert.Equal(rows.Length, held.Entry.Rows);

            ColumnSheet service = sheets[1]!;
            Assert.True(service.IsExact);
            Assert.Equal(Services.Length, service.Distinct);
            Assert.Equal(
                rows.GroupBy(c => c.Service).Select(g => (g.Key, (long)g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal),
                service.Top.Select(t => (System.Text.Encoding.UTF8.GetString(t.Value), t.Rows)));

            ColumnSheet id = sheets[4]!;
            Assert.Equal(rows.Length, id.Distinct);
            Assert.Empty(id.Top);
            Assert.Null(id.Filter);
            Assert.NotNull(sheets[3]!.Filter);
        }

        Assert.True(sealedObjects >= 2, $"{sealedObjects} sealed objects");
    }

    [Fact]
    public async Task AValueAnObjectLacksPassesItOverUnopened()
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, Options(), Ct);
        VortexExpr rare = Expr.Eq(Expr.Field("Route"), Expr.Literal(FilterLiteral.From("/rare")));
        DatasetScanCounters metrics = new DatasetScanCounters();
        long rows = 0;
        await foreach (RecordBatch batch in dataset.ScanBuilder().Where(rare).WithMetrics(metrics).ExecuteAsync(Ct))
        {
            rows += batch.SelectedRows;
        }

        long expected = _fixture.Rows.Count(c => c.Route == "/rare");
        Assert.True(expected > 0);
        Assert.Equal(expected, rows);
        Assert.True(metrics.ObjectsRefutedBySheets >= 1, $"{metrics.ObjectsRefutedBySheets} objects refuted by their sheets");

        // The same answer through the engine, on one lane and on eight.
        foreach (int lanes in new[] { 1, 8 })
        {
            List<ServiceCount> counts = await dataset.Scan<Call>().With(new ScanOptions { DegreeOfParallelism = lanes })
                .Where(c => c.Route == "/rare").GroupBy(c => c.Service).Select(g => (g.Key, g.Count()))
                .As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
            long counted = counts.Sum(c => c.Count);

            Assert.Equal(expected, counted);
        }
    }

    [Fact]
    public async Task AnObjectUnderAMegabyteKeepsNone()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
        }, Ct);
        for (int part = 0; part < 24; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(_fixture.Rows.AsSpan(part * (RowsPerHour / 12), RowsPerHour / 12), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int sealedObjects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is { } contents)
            {
                sealedObjects++;
                Assert.True(held.Entry.Bytes < 1 << 20, $"{held.Entry.Bytes} bytes");
                Assert.Empty(contents.Sheets);
            }
        }

        Assert.True(sealedObjects >= 1, $"{sealedObjects} sealed objects");
    }

    private static DatasetOptions Options() => new DatasetOptions { SheetObjectBytes = 0 };

    /// <summary>
    /// Three hours of calls from noon, appended in parts of five minutes and compacted: each hour an object in time slices,
    /// a route held by a few calls of the second hour alone.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore Store { get; } = new MemoryObjectStore();

        internal Call[] Rows { get; } = new Call[Hours * RowsPerHour];

        public async ValueTask InitializeAsync()
        {
            Random random = new Random(71);
            for (int row = 0; row < Rows.Length; row++)
            {
                int service = random.Next(Services.Length);
                bool rare = row / RowsPerHour == 1 && row % 1_000 == 7;
                Rows[row] = new Call(
                    Noon.AddSeconds(row * 3_600.0 / RowsPerHour),
                    Services[service],
                    rare ? "/rare" : $"/{Services[service]}/{random.Next(500)}",
                    random.Next(10) switch { < 7 => (short)200, 7 => (short)404, 8 => (short)500, _ => (short)503 },
                    $"r-{row}",
                    random.NextInt64(1_000, 5_000_000));
            }

            // Its objects, of a few hundred kilobytes, keep sheets as objects of a megabyte or more do.
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Call.Schema, Options() with
            {
                ClusteringKey = [nameof(Call.At)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            });
            for (int part = 0; part < Hours * 12; part++)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(Rows.AsSpan(part * (RowsPerHour / 12), RowsPerHour / 12));
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
            {
            }
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Service, string Route, short Status, string RequestId, long DurationNs);

    [VortexRecord]
    public partial record struct ServiceCount(string Service, long Count);
}
