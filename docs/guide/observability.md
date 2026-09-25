# Observability

Watch what scans and writes do: the process-wide counters, an activity per operation, and the
numbers of one scan.

```csharp
ConcurrentDictionary<string, long> totals = new(StringComparer.Ordinal);
using MeterListener meters = new MeterListener();
meters.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name == VortexDiagnostics.MeterName)
    {
        listener.EnableMeasurementEvents(instrument);
        Console.WriteLine($"instrument {instrument.Name} ({instrument.Unit}): {instrument.Description}");
    }
};
meters.SetMeasurementEventCallback<long>((instrument, value, tags, state) => totals.AddOrUpdate(instrument.Name, value, (_, total) => total + value));
meters.Start();

List<string> finished = [];
using ActivityListener activities = new ActivityListener
{
    ShouldListenTo = source => source.Name == VortexDiagnostics.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => finished.Add(
        $"{activity.OperationName} [{activity.Status}]: {string.Join(", ", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))}"),
};
ActivitySource.AddActivityListener(activities);
```

The library reports through the two instrumentation APIs of the base class library,
`System.Diagnostics.Metrics` and `System.Diagnostics.Activity`, under one name, `"Vorticity"`,
which `VortexDiagnostics.MeterName` and `VortexDiagnostics.ActivitySourceName` hold. Anything that
listens to those APIs sees it: OpenTelemetry with `AddMeter("Vorticity")` and
`AddSource("Vorticity")`, `dotnet-counters monitor --counters Vorticity`, or a listener of your
own like the one above. No package is involved on the library's side.

## The meter

```
instrument vortex.scan.rows ({row}): Rows delivered by scans.
instrument vortex.scan.requests ({request}): Segment requests made by scans.
instrument vortex.scan.bytes_requested (By): Bytes requested by scans.
instrument vortex.scan.blocks_decoded ({block}): Blocks decoded by scans.
instrument vortex.scan.blocks_pruned ({block}): Blocks skipped by statistics, zone maps or indexes.
instrument vortex.cache.hits ({segment}): Segments served by a session's cache.
instrument vortex.cache.misses ({segment}): Segments a session's cache did not hold.
instrument vortex.write.bytes (By): Bytes written by writers.
```

Eight counters of `long`, with no tags: they add up every scan and every write of the process.
A scan adds its figures when its sink ends; a writer adds its bytes as they reach the sink, at
each flush and at completion. After the sample's two filtered scans, a count on the tool path and
a small write:

```
total vortex.cache.hits = 16
total vortex.cache.misses = 27
total vortex.scan.blocks_decoded = 151
total vortex.scan.blocks_pruned = 218
total vortex.scan.bytes_requested = 759732
total vortex.scan.requests = 43
total vortex.scan.rows = 200000
total vortex.write.bytes = 3012
```

`vortex.scan.rows` counts rows delivered, so a count, which delivers none, adds requests and
bytes but no rows.

## The activities

One activity per scan, named `vortex.scan.typed` or `vortex.scan.tool` after the path it took,
and one per write, `vortex.write`. Each carries what the operation did as tags, set when it ends:

```
activity vortex.scan.typed [Unset]: vortex.rows=100000, vortex.batches=5, vortex.requests=13, vortex.bytes_requested=210980, vortex.blocks_decoded=14, vortex.blocks_pruned=109
activity vortex.scan.tool [Unset]: vortex.rows=0, vortex.batches=0, vortex.requests=18, vortex.bytes_requested=339896, vortex.blocks_decoded=123, vortex.blocks_pruned=0
activity vortex.write [Unset]: vortex.rows=2, vortex.bytes=3012, vortex.completed=True
activity vortex.write [Error]: vortex.rows=1, vortex.bytes=0, vortex.completed=False
```

A write that is abandoned, or disposed without `CompleteAsync`, ends its activity with
`vortex.completed=False` and the status `Error`, described `abandoned`. The activities are
children of whatever activity is current when the scan or the writer starts, so a request's trace
shows the scans it ran. `ExplainAsync` reads statistics and zone maps without starting one.

## The numbers of one scan

The counters are the process; `Statistics` on a scan is that scan alone, read once its sink has
run:

```csharp
Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
long rows = 0;
await foreach (Columns<Reading> batch in scan)
{
    rows += batch.RowCount;
}

ScanStatistics stats = scan.Statistics;
Console.WriteLine($"scan {run}: {stats.Rows} rows in {stats.Batches} batches, {stats.Requests} requests, " +
    $"{stats.BytesRequested} bytes, {stats.BlocksDecoded} blocks decoded, {stats.BlocksPruned} pruned, {stats.CacheHits} cache hits");
```

```
scan 1: 100000 rows in 5 batches, 13 requests, 210980 bytes, 14 blocks decoded, 109 pruned, 0 cache hits
scan 2: 100000 rows in 5 batches, 12 requests, 208856 bytes, 14 blocks decoded, 109 pruned, 12 cache hits
```

`ScanStatistics` is a `readonly record struct` of seven `long`s: `Rows`, `Batches`, `Requests`,
`BytesRequested`, `BlocksDecoded`, `BlocksPruned` and `CacheHits`. `Requests` counts the segments
the scan asked for, each once, the ones the cache then served included: the second scan finds all
12 of its requests in the cache, where the first found none, and asks one fewer, because the file
kept the zone maps the first one read. The same
record is on the tool scan and on a grouped aggregation. What the scan was going to do, before it
runs, is `ExplainAsync`: [statistics-and-pruning.md](statistics-and-pruning.md) puts the two side
by side.

## The segment cache

The cache hits above come from a session with a cache, opening the file through a
`FileSegmentSource`:

```csharp
await using VortexSession session = VortexSession.Create(o => o.SegmentCache = new SegmentCache(64L * 1024 * 1024));
await using (VortexFile file = await session.OpenAsync(new FileSegmentSource(path)))
```

```
cache: 16 hits, 27 misses, 0 bytes held
```

A path handed to `OpenAsync` is memory-mapped by its first scan, unless the session's `MapFiles`
is false, and a mapping has nothing to cache: its reads never reach the cache and never touch its
counters. `SegmentCache` exposes `Capacity`, `Size`, `Hits` and
`Misses` for the session as a whole; a file's entries leave the cache when the file is disposed,
which is why nothing is held at the end. [threads.md](threads.md) says when a cache is worth its
memory.

## What it costs

Nothing without a listener. A counter with no listener is a check of a flag, and an activity is
created only when some listener samples the source; the scan's own `Statistics` are counted
either way, since they are what the counters are made of.

## Watch out

* **The counters carry no tags.** Tell two files or two tenants apart with one session each and
  the scan's `Statistics`, or with the activities, which nest under your own.
* The scan counters are added when the scan ends, each for whoever listens to it.
* A scan's `Statistics.CacheHits` is the session cache's hits between the scan's start and its end:
  a scan running beside another on the same session counts the other's hits too.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- observability
```
