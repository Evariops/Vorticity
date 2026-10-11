using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The stores' instruments, on the library's meter (<see cref="VortexDiagnostics.MeterName"/>). Tags
/// never carry a key, a bucket or a token, so their cardinality stays bounded; without a listener an
/// instrument costs a check.
/// </summary>
internal static class StoreTelemetry
{
    private static readonly Meter Meter = new Meter(VortexDiagnostics.MeterName);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("vortex.store.requests", "{request}", "Requests sent to object stores, by operation, provider and status class.");
    private static readonly Counter<long> BytesRead = Meter.CreateCounter<long>("vortex.store.bytes_read", "By", "Bytes read from object stores.");
    private static readonly Counter<long> BytesWritten = Meter.CreateCounter<long>("vortex.store.bytes_written", "By", "Bytes sent to object stores.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("vortex.store.retries", "{request}", "Requests sent again after a failure.");
    private static readonly Counter<long> Throttles = Meter.CreateCounter<long>("vortex.store.throttles", "{request}", "Answers by which a service asked to slow down.");
    private static readonly Counter<long> Hedges = Meter.CreateCounter<long>("vortex.store.hedges", "{request}", "Second requests sent for a read on a slow path.");
    private static readonly Counter<long> Connections = Meter.CreateCounter<long>("vortex.store.connections_opened", "{connection}", "Connections opened to object stores.");
    private static readonly Histogram<double> FirstByte = Meter.CreateHistogram<double>("vortex.store.first_byte", "ms", "Time to the first byte of an answer.");

    internal static void Request(string operation, string provider, int status)
    {
        if (Requests.Enabled)
        {
            Requests.Add(
                1,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("provider", provider),
                new KeyValuePair<string, object?>("status", StatusClass(status)));
        }
    }

    internal static void Read(long bytes) => BytesRead.Add(bytes);

    internal static void Written(long bytes) => BytesWritten.Add(bytes);

    internal static void Retried() => Retries.Add(1);

    internal static void Throttled() => Throttles.Add(1);

    internal static void Hedged() => Hedges.Add(1);

    internal static void ConnectionOpened() => Connections.Add(1);

    internal static void FirstByteAfter(double milliseconds) => FirstByte.Record(milliseconds);

    private static string StatusClass(int status) => status switch
    {
        0 => "failed",
        < 300 => "2xx",
        < 400 => "3xx",
        < 500 => "4xx",
        _ => "5xx",
    };
}
