// The memory the library holds, by sort: the gauges and the report read the same holders, and a handle's caches count
// while the handle is open and leave the count when it is disposed.
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Tests.Buffers;
using Xunit;

namespace Vorticity.Tests.Diagnostics;

[Collection(nameof(SharedPoolCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class MemoryReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// After a question over a dataset, each gauge is the number the library holds: the reservations, the blocks in use, the
    /// bytes each holder keeps, the handles open; and the report says the same.
    /// </summary>
    [Fact]
    public async Task EachGaugeIsWhatTheLibraryHoldsAfterAQuestion()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        List<ServiceCount> counts = await dataset.Scan<Call>().GroupBy(r => r.Service).Select(g => (g.Key, g.Count()))
            .As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(4, counts.Count);

        // Handles of earlier tests that nothing references leave the list once collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Dictionary<string, List<(long Value, string? Holder)>> gauges = Collect();
        MemoryReport report = VortexDiagnostics.GetMemoryReport();
        long handle = ((IHeldMemory)dataset).HeldBytes;

        Assert.Equal(QueryMemoryBudget.Process.ReservedBytes, Single(gauges, "vortex.memory.reserved"));
        Assert.Equal(report.ReservedBytes, Single(gauges, "vortex.memory.reserved"));
        Assert.Equal(MemoryReport.RentedNow(), Single(gauges, "vortex.memory.rented"));
        Assert.Equal(QueryMemoryBudget.ProcessLimit(), Single(gauges, "vortex.memory.limit"));
        Assert.Equal(report.LimitBytes, Single(gauges, "vortex.memory.limit"));
        Assert.Equal(1, Single(gauges, "vortex.dataset.handles"));
        Assert.Equal(1, report.DatasetHandles);

        Dictionary<string, long> retained = gauges["vortex.memory.retained"].ToDictionary(g => g.Holder!, g => g.Value);
        Assert.True(handle > 0, "the handle's caches hold what the question read");
        Assert.Equal(handle, retained["datasets"]);
        Assert.Equal(handle, report.RetainedByHolder["datasets"]);
        Assert.Equal(HeldMemory.BytesOf<BlockPool>() + ArrayShelf.Retained.HeldBytes + Blocks.HeldBytes, retained["pool"]);
        Assert.Equal(report.RetainedByHolder.Values.Sum(), report.RetainedBytes);

        await dataset.DisposeAsync();
        Assert.Equal(0, VortexDiagnostics.GetMemoryReport().DatasetHandles);
        Assert.False(VortexDiagnostics.GetMemoryReport().RetainedByHolder.ContainsKey("datasets"));
    }

    /// <summary>Each memory gauge's measurements, read once, with the holder each is tagged by.</summary>
    private static Dictionary<string, List<(long Value, string? Holder)>> Collect()
    {
        Dictionary<string, List<(long Value, string? Holder)>> seen = new Dictionary<string, List<(long Value, string? Holder)>>(StringComparer.Ordinal);
        using MeterListener listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name.StartsWith("vortex.memory.", StringComparison.Ordinal)
                || instrument.Name == "vortex.dataset.handles")
            {
                meter.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        listener.Start();
        VortexTelemetry.EnsureMemoryGauges();
        listener.RecordObservableInstruments();
        return seen;

        void Add(string name, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? holder = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "holder")
                {
                    holder = (string?)tag.Value;
                }
            }

            if (!seen.TryGetValue(name, out List<(long Value, string? Holder)>? values))
            {
                seen[name] = values = [];
            }

            values.Add((value, holder));
        }
    }

    private static long Single(Dictionary<string, List<(long Value, string? Holder)>> gauges, string name) => Assert.Single(gauges[name]).Value;

    private static async Task<VortexDataset> FilledAsync(MemoryObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, cancellationToken: Ct);
        string[] services = ["api", "auth", "cart", "search"];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Call>([.. Enumerable.Range(0, 20_000).Select(i => new Call(services[i % 4], i))], Ct);
        await dataset.AppendAsync(draft, Ct);
        return dataset;
    }

    [VortexRecord]
    public partial record struct Call(string Service, long Bytes);

    [VortexRecord]
    public partial record struct ServiceCount(string Service, long Count);
}
