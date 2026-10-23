// A dataset compacted by periods: what it declares at creation, what every header then carries, what
// creation refuses, and the lateness a handle reports.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasetOptions Options() => new DatasetOptions
    {
        ClusteringKey = [nameof(Request.At)],
        Periods = new PeriodOptions { Lateness = TimeSpan.FromHours(1) },
        Identifiers = [nameof(Request.RequestId)],
    };

    [Fact]
    public async Task EveryHeaderCarriesThePeriodsAndTheIdentifiers()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(), Ct))
        {
            await AppendAsync(dataset, Noon, 10, Ct);
        }

        (ulong version, CommitObject? latest) = await DatasetCommitter.LatestAsync(store, Ct);
        Assert.Equal(2UL, version);
        Assert.Equal(TimeSpan.FromHours(1), latest!.Header.Lateness);
        Assert.Equal([nameof(Request.RequestId)], latest.Header.Identifiers);
        Assert.Equal(DatasetFeatures.Periods | DatasetFeatures.Identifiers, latest.Header.Features & (DatasetFeatures.Periods | DatasetFeatures.Identifiers));
        Assert.Equal(CommitFormat.WithFeatures, CommitFormat.For(latest.Header));

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        Assert.Equal(TimeSpan.FromHours(1), reopened.Options.Periods!.Lateness);
        Assert.Equal([nameof(Request.RequestId)], reopened.Snapshot.Schema.Identifiers);

        // A dataset without periods states none, and its header names no such feature.
        await using MemoryObjectStore plain = new MemoryObjectStore();
        await using VortexDataset leveled = await VortexDataset.CreateAsync(plain, Request.Schema, new DatasetOptions { ClusteringKey = [nameof(Request.At)] }, Ct);
        Assert.Null(leveled.Options.Periods);
        Assert.Empty(leveled.Snapshot.Schema.Identifiers);
        (_, CommitObject? first) = await DatasetCommitter.LatestAsync(plain, Ct);
        Assert.Null(first!.Header.Lateness);
        Assert.Equal(DatasetFeatures.None, first.Header.Features);
    }

    [Fact]
    public async Task APeriodsDatasetLeadsItsKeyWithAnInstantInUtc()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Request.Schema, Options() with { ClusteringKey = null }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Request.Schema, Options() with { ClusteringKey = [nameof(Request.Status), nameof(Request.At)] }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Zoned.Schema, Options() with { ClusteringKey = [nameof(Zoned.At)], Identifiers = null }, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodOptions { Lateness = TimeSpan.FromSeconds(-1) });
        Assert.Equal(TimeSpan.FromSeconds(2), new PeriodOptions { Lateness = TimeSpan.FromSeconds(1.2) }.Lateness);

        // A naive timestamp is read as UTC.
        await using MemoryObjectStore naive = new MemoryObjectStore();
        await using VortexDataset created = await VortexDataset.CreateAsync(
            naive, Naive.Schema, Options() with { ClusteringKey = [nameof(Naive.At)], Identifiers = null }, Ct);
        Assert.NotNull(created.Options.Periods);
    }

    [Fact]
    public async Task IdentifiersNameColumnsOfTheSchemaOnce()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Request.Schema, Options() with { Identifiers = ["Missing"] }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Request.Schema, Options() with { Identifiers = [nameof(Request.RequestId), nameof(Request.RequestId)] }, Ct));
    }

    /// <summary>
    /// A handle reports how far behind the newest instant it appended the oldest row of each append
    /// came: nothing for its first append, the gap for rows that came late, zero for rows in order.
    /// </summary>
    [Fact]
    public async Task AHandleReportsTheLatenessItSees()
    {
        ConcurrentQueue<double> lateness = new();
        using MeterListener listener = new MeterListener
        {
            InstrumentPublished = (instrument, meters) =>
            {
                if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name == "vortex.dataset.lateness")
                {
                    meters.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => lateness.Enqueue(value));
        listener.Start();

        // The listener hears every dataset of the process, the tests beside this one included, so the
        // values are told apart: 39 and 0 minutes are this test's alone.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(), Ct);
        await AppendAsync(dataset, Noon.AddDays(3), 10, Ct);
        Assert.DoesNotContain(lateness, value => value == 39 * 60.0);

        // Rows from 11:30 after rows up to 12:09: the oldest is 39 minutes late.
        await AppendAsync(dataset, Noon.AddDays(3).AddMinutes(-30), 50, Ct);
        Assert.Contains(39 * 60.0, lateness);
        await AppendAsync(dataset, Noon.AddDays(3).AddHours(1), 5, Ct);
        Assert.Contains(0.0, lateness);
    }

    /// <summary>
    /// A clustering key that leads with an instant works as any key: rows that came late are merged
    /// into the order, a range of instants reads its rows, and the rows come back in key order. A seek
    /// key of a timestamp is its storage's, as a row of the column is.
    /// </summary>
    [Fact]
    public async Task ADatasetKeyedByAnInstantCompactsAndReadsInItsOrder()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(), Ct);
        await AppendAsync(dataset, Noon, 60, Ct);
        await AppendAsync(dataset, Noon.AddMinutes(-90), 40, Ct);
        await AppendAsync(dataset, Noon.AddMinutes(30), 60, Ct);
        while (await dataset.CompactAsync(null, Ct) is { Outcome: OperationOutcome.Applied })
        {
        }

        DateTimeOffset from = Noon.AddMinutes(-60);
        DateTimeOffset to = Noon.AddMinutes(45);
        List<DateTimeOffset> ranged = await dataset.Scan<Request>().Where(r => r.At >= from & r.At < to).Select(r => r.At).ToListAsync(Ct);
        List<DateTimeOffset> all = await dataset.Scan<Request>().Select(r => r.At).ToListAsync(Ct);
        Assert.Equal(160, all.Count);
        Assert.Equal(all.Where(at => at >= from && at < to).Order(), ranged.Order());
        Assert.Equal(10 + 45 + 15, ranged.Count);
        Assert.Equal(all.Order(), await dataset.Scan<Request>().OrderBy(r => r.At).Select(r => r.At).ToListAsync(Ct));
    }

    /// <summary>Appends <paramref name="rows"/> requests a minute apart from <paramref name="from"/>, as one object.</summary>
    private static async Task AppendAsync(VortexDataset dataset, DateTimeOffset from, int rows, CancellationToken ct)
    {
        Request[] requests = [.. Enumerable.Range(0, rows).Select(i => new Request(from.AddMinutes(i), $"r-{from.ToUnixTimeSeconds()}-{i}", 200 + i))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Request>(requests, ct);
        await dataset.AppendAsync(draft, ct);
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string RequestId, long Status);

    [VortexRecord]
    public partial record struct Zoned([property: VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset At, long Status);

    [VortexRecord]
    public partial record struct Naive(DateTime At, long Status);
}
