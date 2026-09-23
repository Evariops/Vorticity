# Threads

Configure a session, share what is safe to share, and turn on parallelism where it pays.

```csharp
await using VortexSession session = VortexSession.Create(o =>
{
    o.MemoryPool = new AlignedMemoryPool();
    o.SegmentCache = new SegmentCache(256L * 1024 * 1024);
    o.MaxConcurrentReads = 32;
    o.MaxDegreeOfParallelism = 4;
});

await using VortexFile file = await session.OpenAsync(new FileSegmentSource(path));

Task<long>[] concurrent = new Task<long>[4];
for (int i = 0; i < concurrent.Length; i++)
{
    concurrent[i] = CountRowsAsync(file.Scan<Reading>());
}

Console.WriteLine($"four concurrent scans of one open file: {string.Join(", ", await Task.WhenAll(concurrent))}");
```

```
four concurrent scans of one open file: 1000000, 1000000, 1000000, 1000000
```

One open file, many scans: each task builds its own scan and enumerates it, and nothing is shared
but the file and its session.

## The session

A `VortexSession` holds what would otherwise be process-wide state, so that a host decides it once:

| option | default | what it governs |
|---|---|---|
| `MemoryPool` | `AlignedMemoryPool.Shared` | every batch, segment and builder buffer; disposing the session returns them |
| `SegmentCache` | none | segments kept across scans, one budget for every file of the session |
| `MaxConcurrentReads` | 16 | reads in flight across every scan of every file of the session |
| `MaxDegreeOfParallelism` | 1 | how many chunks a scan decodes and aggregates at once |
| `IndexCacheBytes` | 64 MiB | decoded index runs |
| `Extensions` | empty | extension types the session reads beyond the editions |

The options are set inside `Create` and frozen when it returns; setting one afterwards throws
`InvalidOperationException: The session is created; its options are frozen. Set them inside
VortexSession.Create.` `VortexFile.OpenAsync(path)` uses `VortexSession.Default`, which is immutable:
the shared pool, no cache, 16 reads in flight, no parallelism.

A session is disposed after its files. Disposing it with one still open throws
`InvalidOperationException: The session still has 'readings.vortex' open; dispose every file before
the session.` Two `await using` declarations in that order, as above, do it right.

**The cache and the bound on reads apply to a source that does I/O**: a `FileSegmentSource`, as
above, or your own `ISegmentSource` ([object-store.md](object-store.md)). A path opened with
`session.OpenAsync(path)` is memory-mapped, and a mapping has nothing to bound or to cache. With the
cache, a fifth scan of the file made 51 requests and the cache served all 51; over the five scans
it counted 201 hits and 54 misses, and held 1 459 KiB, the data segments of a 1.5 MB file. The
concurrent scans missed a few more than the file's 51 segments: two scans that ask for one at the
same moment both miss it.

## What is safe to share

| | |
|---|---|
| `VortexSession` | **thread-safe**, and meant to be shared by every request of a process |
| `VortexFile` | **thread-safe**: concurrent scans of one open file are expected |
| `ISegmentSource` | must be thread-safe; the built-in ones are |
| `Scan<TRecord>`, `Scan` | **single-use, one thread**: build it, then run one sink |
| `Columns<TRecord>`, `Column<T>`, `BatchView` | valid inside the loop body only; the compiler holds you to it |
| `RecordBatch` | one consumer at a time; it may be handed to another thread and disposed there |
| `KeyCursor<TKey>` | not thread-safe |
| `VortexFileWriter`, `ColumnsBuilder` | one thread, one writer per file |
| `ScanPlan`, `KeyPlan`, `WriteReport` | immutable |

A second sink on the same scan throws `InvalidOperationException: A scan is single-use: build
another one for another sink.` A scan is cheap to build; build one per question.

## Parallelism

Parallelism is off by default: a library does not take a host's cores without being asked. The host
asks once, with `MaxDegreeOfParallelism` on its session, and one scan can say otherwise:

```csharp
ScanOptions four = new ScanOptions { DegreeOfParallelism = 4 };   // 0, the default, is the session's
```

Measured on the demonstration file, warmed, each variant run in turn, the best of fifteen rounds:

| | degree 1 | degree 4 |
|---|---|---|
| a scan that counts rows | 4.7 ms | 3.6 ms |
| `GroupBy(r => r.City)` with an average | 8.7 ms | 2.9 ms |
| `Where(r => r.Celsius > 20.0).SumAsync(r => r.Celsius)` | 6.2 ms | 1.9 ms |

An aggregate keeps one state per chunk and merges them at the end, so its chunks run side by side:
three times faster at four threads. A scan that hands batches to your loop gains little: it still
delivers them one at a time and in file order (checked, at degree 4), and most of its cost here is
walking 123 blocks. Measure on your own files; the timings vary from run to run on a busy machine,
and the ratios are what to read.

## Prefetch

`ScanOptions.Prefetch` is how many batches are decoded ahead of the loop, 1 by default, so that the
decode of the next batch overlaps your work on this one; the scan holds at most that many batches
more. On a mapped file with a loop that works on every value it changed nothing measurable, 8.0 ms,
8.2 ms and 7.9 ms at 0, 1 and 2. It pays when the source has latency to hide, which a remote one
does.

## Local and remote

The loop is the same for every source: there is no synchronous enumeration to choose. A mapped
segment completes at once, so the `await` costs no suspension; a remote one suspends while it
reads. Inside the body the rule is the compiler's: a `Column<T>` does not live across an `await`. Do
the columnar work, then await what you must, then let the loop move on.

## Watch out

* **A batch's columns must not cross threads.** Their spans point into buffers the scan reuses. To
  hand rows to another thread, take owned batches ([owned-batches.md](owned-batches.md)).
* A `MemoryPool` other than an `AlignedMemoryPool` serves the builders and the owned batches only;
  the engine then decodes into `AlignedMemoryPool.Shared`.
* Two hosts in one process that want different parallelism or caches want two sessions, not a
  setting changed between calls.

The contracts are in [09-contracts.md](../design/09-contracts.md) §1 and §2, and the session in §2
of [14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- threads
```
