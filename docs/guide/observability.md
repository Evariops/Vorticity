# Observability

Watch what scans and writes do: process-wide counters, an activity per operation, and the figures of
a single scan.

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

The library reports through the standard .NET instrumentation APIs, all under the single name
`"Vorticity"`:

* a `Meter` (`System.Diagnostics.Metrics`), whose name `VortexDiagnostics.MeterName` holds
* an `ActivitySource` (`System.Diagnostics.Activity`), whose name `VortexDiagnostics.ActivitySourceName` holds
* an `EventSource` with polling counters for the engine's internals (see [below](#the-event-counters))

Anything that listens to those APIs sees it: OpenTelemetry with `AddMeter("Vorticity")` and
`AddSource("Vorticity")`, `dotnet-counters monitor --counters Vorticity`, or a listener of your own
like the one above. The library takes no dependency for any of this.

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

These are eight `long` counters with no tags, adding up every scan and every write in the process. A
scan adds its figures when its sink ends, and a writer adds its bytes as they reach the sink, at each
flush and at completion. After the sample's two filtered scans, a count on the tool path and a small
write:

```
total vortex.cache.hits = 8
total vortex.cache.misses = 19
total vortex.scan.blocks_decoded = 151
total vortex.scan.blocks_pruned = 218
total vortex.scan.bytes_requested = 690916
total vortex.scan.requests = 27
total vortex.scan.rows = 200000
total vortex.write.bytes = 3020
```

`vortex.scan.rows` counts rows delivered, so a count, which delivers none, adds requests and bytes
but no rows.

The stores of the package Vorticity.ObjectStorage add their own instruments to the same meter, once
the package is loaded. Their tags name the operation, the provider and the status class, never a key,
a bucket or a token.

| instrument | what it says |
|---|---|
| `vortex.store.requests` | the requests sent, by operation as the service's API names it (`GetObject`, `PutObject`, `ListObjectsV2` on S3), provider and status class |
| `vortex.store.bytes_read`, `vortex.store.bytes_written` | the bytes that crossed the network |
| `vortex.store.retries`, `vortex.store.throttles` | how much the retries and the services' throttling cost |
| `vortex.store.first_byte`, a histogram in milliseconds | the latency of the services' first byte, by which the deadlines are set |

The connections themselves are the platform's: `System.Net.Http` reports the connections open and
their durations, and the requests and their durations, which OpenTelemetry collects as it does for
any other client.

The package Vorticity.Dataset adds seven instruments to the same meter, the first time a dataset
commits, compacts, vacuums or reads:

```
instrument vortex.dataset.commits ({commit}): Commits of datasets, by operation and outcome.
instrument vortex.dataset.commit_attempts ({attempt}): Conditional creations of a version that commits sent, by operation; those past the commits are rebases.
instrument vortex.dataset.compactions ({compaction}): Compactions of datasets, by trigger and outcome.
instrument vortex.dataset.compaction.bytes_read (By): Bytes the compactions that ran to their commit read, by trigger.
instrument vortex.dataset.compaction.bytes_written (By): Bytes the compactions that ran to their commit wrote, by trigger.
instrument vortex.dataset.vacuum.objects_deleted ({object}): Objects vacuums deleted.
instrument vortex.dataset.clustering.avoidable_bytes (By): Bytes the objects read as rows under a filter would have been spared by a clustering of the filter's column, by column (ClusteringAdvice).
```

A commit is tagged with `vortex.operation` (`create`, `append`, `import`, `replace`, `delete`, `update`,
`schema`, `index`, `compaction`, `repack`, `rekey`) and `vortex.outcome` (`applied`, `nothing_to_do`,
`lost`, `cancelled`, `failed`), and a compaction with `vortex.trigger` and its outcome (`applied`,
`abandoned`, `cancelled`, `failed`). The attempts past the applied commits are the rebases, the
measure of contention between writers, and the abandoned compactions measure compactions that
compete. After the sample creates a dataset, appends three objects, compacts them and runs a dry
vacuum:

```
total vortex.dataset.commit_attempts = 5
total vortex.dataset.commits = 5
total vortex.dataset.compaction.bytes_read = 10725
total vortex.dataset.compaction.bytes_written = 2956
total vortex.dataset.compactions = 1
```

A dry run deletes nothing and counts nothing. No tag of these counters names a dataset: a process can
hold a hundred thousand of them, and a tag per location would make a series per dataset. The
location is on the activities instead. The clustering counter is tagged with `vortex.column`, a
column a filter read, a set as small as a schema. It adds the bytes each object read as rows whole
under that filter would have been spared had the dataset been clustered by the column, the figure
`VortexDataset.RecommendClustering` ranks for one handle (see
[the key and the clustering](datasets.md#the-key-and-the-clustering)). Summed over the processes, it
says which column the questions keep paying for. A dataset's compaction lag has no counter, since
the library keeps no list of the handles a process holds, so export `VortexDataset.Lag` for the
datasets you care about.

## The activities

Each scan gets one activity, named `vortex.scan.typed` or `vortex.scan.tool` after the path it took,
and each write gets one named `vortex.write`. Each carries what the operation did as tags, set when it
ends:

```
activity vortex.scan.typed [Unset]: vortex.rows=100000, vortex.batches=5, vortex.requests=6, vortex.bytes_requested=180368, vortex.blocks_decoded=14, vortex.blocks_pruned=109
activity vortex.scan.tool [Unset]: vortex.rows=0, vortex.batches=0, vortex.requests=15, vortex.bytes_requested=330180, vortex.blocks_decoded=123, vortex.blocks_pruned=0
activity vortex.write [Unset]: vortex.rows=2, vortex.bytes=3020, vortex.completed=True
activity vortex.write [Error]: vortex.rows=1, vortex.bytes=0, vortex.completed=False
```

A write that is abandoned, or disposed without `CompleteAsync`, ends its activity with
`vortex.completed=False` and the status `Error`, described as `abandoned`. Activities are children of
whatever activity is current when the scan or the writer starts, so a request's trace shows the
scans it ran. `ExplainAsync` reads statistics and zone maps without starting one.

A dataset's commit, compaction and vacuum get one activity each, `vortex.dataset.commit` over all of
a commit's attempts, `vortex.dataset.compaction`, whose passes and commit are its children, and
`vortex.dataset.vacuum`. A compaction's passes are `vortex.dataset.compaction.write`, which reads the
inputs' rows and writes the outputs, and before it, for a job that writes key slices over a variant
column, `vortex.dataset.compaction.plan`, which counts each clustering value's paths. A handle opened
at a location tags the others with it:

```
activity vortex.dataset.commit [Unset]: vortex.operation=create, vortex.location=observed-dataset, vortex.outcome=applied, vortex.attempts=1, vortex.version=1
activity vortex.dataset.commit [Unset]: vortex.operation=append, vortex.location=observed-dataset, vortex.outcome=applied, vortex.attempts=1, vortex.version=2
activity vortex.dataset.commit [Unset]: vortex.operation=append, vortex.location=observed-dataset, vortex.outcome=applied, vortex.attempts=1, vortex.version=3
activity vortex.dataset.commit [Unset]: vortex.operation=append, vortex.location=observed-dataset, vortex.outcome=applied, vortex.attempts=1, vortex.version=4
activity vortex.dataset.compaction.write [Unset]: 
activity vortex.dataset.commit [Unset]: vortex.operation=compaction, vortex.location=observed-dataset, vortex.outcome=applied, vortex.attempts=1, vortex.version=5
activity vortex.dataset.compaction [Unset]: vortex.trigger=level_zero_ceiling, vortex.level.from=0, vortex.level.to=1, vortex.objects_in=3, vortex.bytes_in=10725, vortex.location=observed-dataset, vortex.outcome=applied, vortex.objects_out=1, vortex.objects_moved=0, vortex.bytes_out=2956, vortex.rows=6, vortex.version=5
activity vortex.dataset.vacuum [Unset]: vortex.dry_run=True, vortex.location=observed-dataset, vortex.deleted=0, vortex.young=0, vortex.locked=0, vortex.retained=5
```

A commit that loses all its attempts, is cancelled or fails ends with the status `Error` and is
counted with that outcome, as is a compaction that raises. A compaction its commit abandons, because
another took an input first, ends without an error: it is the price of two compactions on one
dataset, which its outcome counts.

## The event counters

The `EventSource` named `Vorticity` publishes eleven incrementing counters about the engine's inner
work: `segments-requested`, `bytes-requested`, `zones-pruned`, `zones-total`, `index-runs-read`,
`cursor-seeks`, `cursor-steps`, `count-blocks-proven`, `count-blocks-decoded`, `key-order-windows` and
`key-order-window-splits`. They answer in production what a single scan's metrics answer in a
test, above all whether an index earns the bytes it costs. Watch them with
`dotnet-counters monitor --counters Vorticity`. Nothing is paid until a listener attaches: every hook
is one flag test, and the counters are only created once a listener asks for them.

## The figures of one scan

The counters cover the whole process, while `Metrics` on a scan covers that scan alone and is
valid once its sink has run:

```csharp
Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
long rows = 0;
await foreach (Columns<Reading> batch in scan)
{
    rows += batch.RowCount;
}

ScanMetrics stats = scan.Metrics;
Console.WriteLine($"scan {run}: {stats.Rows} rows in {stats.Batches} batches, {stats.Requests} requests, " +
    $"{stats.BytesRequested} bytes, {stats.BlocksDecoded} blocks decoded, {stats.BlocksPruned} pruned, {stats.CacheHits} cache hits");
```

```
scan 1: 100000 rows in 5 batches, 6 requests, 180368 bytes, 14 blocks decoded, 109 pruned, 0 cache hits
scan 2: 100000 rows in 5 batches, 6 requests, 180368 bytes, 14 blocks decoded, 109 pruned, 6 cache hits
```

`ScanMetrics` is a `readonly record struct` with seven `long`s (`Rows`, `Batches`, `Requests`,
`BytesRequested`, `BlocksDecoded`, `BlocksPruned` and `CacheHits`) plus `Grouping`, which a group by
fills with a `GroupMetrics`: the groups found and the most held at once, the peak memory, the
lanes, what was spilled and the time to the first batch. `Grouping` is null for a scan without a
group by.

`Requests` counts the segments the scan asked for, each once, including those the cache then served.
The second scan finds all 6 of its requests in the cache, where the first found none. Neither asks
for the zone maps it consults, nor for the other 6 segments its rows lie in, because those sit in the
tail the open already read and the file serves them itself. The same metrics exist on the tool
scan and on a grouped aggregation. What the scan will do before it runs is `ExplainAsync`, and
[statistics-and-pruning.md](statistics-and-pruning.md) puts the two side by side.

## The segment cache

The cache hits above come from a session with a cache, opening the file through a
`FileSegmentSource`:

```csharp
await using VortexSession session = VortexSession.Create(o => o.SegmentCache = new SegmentCache(64L * 1024 * 1024));
await using (VortexFile file = await session.OpenAsync(new FileSegmentSource(path)))
```

```
cache: 8 hits, 19 misses, 0 bytes held
```

A path passed to `OpenAsync` is memory-mapped by its first scan, unless the session's `MapFiles` is
false, and a mapping has nothing to cache: its reads never reach the cache or its counters.
`SegmentCache` exposes `CapacityBytes`, `HeldBytes`, `Hits` and `Misses` for the whole session. A file's
entries leave the cache when the file is disposed, which is why nothing is held at the end.
[threads.md](threads.md) explains when a cache is worth its memory.

## What it costs

Without a listener it costs nothing. A counter with no listener costs a flag check, and an activity
is only created when a listener samples the source. A scan's own `Metrics` are counted either way, since
the counters are built from them.

## Watch out

* The meter's counters carry no tags. To tell two files or two tenants apart, use one session each
  and the scan's `Metrics`, or the activities, which nest under your own.
* The scan counters are added when the scan ends.
* A scan's `Metrics.CacheHits` counts the segments the cache served to that scan alone, even while
  other scans of the session run. The cache's own `Hits` counts the whole session's.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- observability
```
