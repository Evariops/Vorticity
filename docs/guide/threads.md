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

One open file can serve many scans. Each task builds its own scan and enumerates it, and nothing is
shared but the file and its session.

## The session

A `VortexSession` holds what would otherwise be process-wide state, so a host decides it once:

| option | default | what it governs |
|---|---|---|
| `MemoryPool` | `AlignedMemoryPool.Shared` | every batch and builder buffer, and the segments read by a source of yours. The file sources of the library read into `AlignedMemoryPool.Shared` |
| `SegmentCache` | none | segments kept across scans, one budget for every file of the session whose reads do I/O |
| `MaxConcurrentReads` | 128 | reads in flight across every scan of every file of the session whose reads do I/O |
| `MaxDegreeOfParallelism` | 1 | how many chunks a scan decodes and aggregates at once, and how many threads a writer uses |
| `IndexCacheBytes` | 64 MiB | decoded index runs kept per open file |
| `MappedFileCacheCount` | 64 | files opened from a path that stay mapped after they are closed, so the next open reuses the mapping |
| `MapFiles` | true | whether a file opened from a path is mapped by its first scan. When false, it is read positionally, and a file truncated during a scan fails the read instead of the process |
| `MemoryBudget` | the process's | the memory the session's queries share with every session given the same `QueryMemoryBudget`: their group tables and their merges. Past it, a group by spills to `ScratchDirectory`, or fails with `VortexMemoryException` |
| `ScratchDirectory` | the system's temporary directory, or the user's cache when that lies in memory | where a group by writes what its memory budget cannot hold, and where a sort and a writer write their runs. A file system in memory is refused: where `/tmp` is one, as on Ubuntu 26.04, the default is `$XDG_CACHE_HOME/vorticity`, or `~/.cache/vorticity`, which the `Vorticity` event source says once |
| `ScratchBudget` | none, a tenth of the directory's free space is kept free | the scratch space the session's queries share with every session given the same `ScratchBudget`. Past it, `VortexMemoryException`, and no file is left behind |
| `Extensions` | empty | extension types the session reads beyond the editions |

The options are set inside `Create` and frozen when it returns. Setting one afterwards throws
`InvalidOperationException: The session is created; its options are frozen. Set them inside
VortexSession.Create.` `VortexFile.OpenAsync(path)` uses `VortexSession.Default`, which is
immutable: the shared pool, no cache, 128 reads in flight, no parallelism, and 64 closed files kept
mapped.

A session is disposed after its files. Disposing it while one is still open throws
`InvalidOperationException: The session still has 'readings.vortex' open; dispose every file before
the session.` Two `await using` declarations in that order, as above, get it right.

The cache and the limit on reads in flight apply to sources that do I/O: a `FileSegmentSource`, as
above, or your own `ISegmentSource` ([object-store.md](object-store.md)). A path opened with
`session.OpenAsync(path)` is memory-mapped by its first scan, and a mapping has nothing to limit or
cache, unless the session sets `MapFiles` to false, in which case the path is read like a
`FileSegmentSource`, through both. With the cache in place, a fifth scan of the file made 45 requests
and the cache served all 45. Over the five scans, this run counted 173 hits and 52 misses, and the
cache held 1 431 KiB: the data segments of the 1.5 MB file except the six that lie in the tail read by
the open, which the file serves itself. The concurrent scans miss more often than a single scan
would, because two scans asking for the same segment at the same moment both miss it.

## The memory the pool keeps

Every block a scan, a read or a write gives back is parked in the process-wide pool for the next rent,
in size classes of powers of two from 4 KiB to 32 MiB. At rest a class keeps a few blocks. Under
demand a class up to 8 MiB keeps up to 64 MiB per processor, as long as every class together parks
less than 256 MiB per processor: up to 8 GiB on a machine of 32 processors. A class above 8 MiB keeps
16 MiB per processor, and one block of up to 256 MiB is kept on its own.

At each full collection, the pool frees what a class keeps once no rent has asked for it for a minute,
and everything it keeps when the machine's memory load is high. In a container, the runtime measures
that load against the container's memory limit.

It keeps that much because 32 questions at once each hold dozens of blocks of the same few classes.
With 1 GiB parked in all, one block in four was freed as it came back, and the next rent took it from
the system again, every page faulted anew: the declared spans and logs at a tenth of the design's
rate answered about half as many questions a second as once the blocks were kept. Sealing an hour
at the design's rate peaks at 9.8 GB instead of 8.8.

The blocks parked are native memory: they count in the process's private bytes, not in the managed
heap's counters, and a heap dump does not show them. To bound what a session's batches, builders and
decodes keep, give it a pool of its own:

```csharp
await using VortexSession session = VortexSession.Create(o =>
    o.MemoryPool = new AlignedMemoryPool(maxRetainedBytes: 512L * 1024 * 1024));
```

The reads of the library's own sources, its files, its object stores and its caches, still go through
the process-wide pool, whose budgets grow with `Environment.ProcessorCount`, which a container's CPU
limit lowers.

## What is safe to share

| type | contract |
|---|---|
| `VortexSession` | thread-safe, and meant to be shared by every request of a process |
| `VortexFile` | thread-safe. Concurrent scans of one open file are expected, so dispose it once none is running |
| `ISegmentSource` | must be thread-safe, and the built-in ones are |
| `Scan<TRecord>`, `Scan` | single-use and single-threaded: build it, then run one sink |
| `Columns<TRecord>`, `Column<T>`, `BatchView` | valid inside the loop body only, which the compiler enforces |
| `RecordBatch` | one consumer at a time. It may be handed to another thread and disposed there |
| `KeyCursor<TKey>` | not thread-safe |
| `VortexFileWriter`, `ColumnsBuilder` | one thread, and one writer per file |
| `ScanPlan`, `KeyPlan`, `WriteReport` | immutable |

A second sink on the same scan throws `InvalidOperationException: A scan is single-use: build another
one for another sink.` A scan is cheap to build, so build one per question.

## Parallelism

Parallelism is off by default, because a library should not take a host's cores without being asked.
The host asks once, with `MaxDegreeOfParallelism` on its session, and a single scan can say otherwise:

```csharp
ScanOptions four = new ScanOptions { DegreeOfParallelism = 4 };   // 0, the default, is the session's
```

A writer uses the session's degree too, and `VortexWriteOptions.DegreeOfParallelism` overrides it
for one file. When a batch of a block or more comes in, the writer summarizes its columns side by
side: each column's statistics, plus a text column's bounds and the distinct values a dictionary is
priced from. It then chooses and compresses a chunk's columns side by side, a column with a chunk
target of its own included, and a column's zstd frames in parallel, one frame per block, once the
column holds a quarter of a megabyte of values. The file is byte for byte the same whatever the
degree. A write that builds indexes summarizes its columns on one thread.

Measured on the demonstration file, warmed up, each variant run in turn, the best of fifteen rounds in
a process and the median of seven processes:

| | degree 1 | degree 4 |
|---|---|---|
| a scan that counts rows | 1.6 ms | 0.9 ms |
| `GroupBy(r => r.City)` with an average | 4.5 ms | 2.4 ms |
| `Where(r => r.Celsius > 20.0).SumAsync(r => r.Celsius)` | 3.9 ms | 1.6 ms |

An aggregate keeps one state per chunk and merges them at the end, so its chunks run side by side. It
is about twice as fast at four threads, and two and a half times for the filtered sum. A scan that
hands batches to your loop gains little: it still delivers them one at a time and in file order
(which the sample checks at degree 4), and most of its cost here is walking 123 blocks. Measure on
your own files. Timings vary from run to run on a busy machine, so read the ratios.

## Many questions at once

A process answers as many questions at once as it has cores. Past its physical cores, extra clients
add little: what hyperthreading gives two threads that share a core. One process is as good as two:
the questions share its caches, which a second process would fill again.

Measured with the paths bench's throughput run (`--qps`), on a 16-core processor with 32 logical
cores. Each question ran at one lane over two hours of spans and logs, its segments in a 4 GiB cache:

| clients | short questions, q/s | cores busy | mixed questions, q/s | cores busy |
|---|---|---|---|---|
| 8 | 337 | 8.4 | 8.9 | 6.3 |
| 16 | 531 | 16.5 | 16.7 | 13.8 |
| 24 | 561 | 24.3 | 21.2 | 21.9 |
| 32 | 561 | 30.0 | 22.9 | 27.0 |

The short questions take 0.2 to 60 ms each, answered from the cache with no request to the store.
The mixed ones add full scans of 3 to 4 seconds. Two processes of 16 clients each answered 537 short
questions a second together, on the same 30 cores as one process of 32 clients, which answered 548.
The limit is the cores, not the process. Over a 10 Gb/s link, the mixed questions stop at 14.7 a
second from 16 clients on, the link full at about 1 GB/s. Size the clients of a process to its
physical cores, or to its link when its questions read the store.

## Prefetch

`ScanOptions.Prefetch` is the number of batches decoded ahead of the loop, 1 by default, so that
decoding the next batch overlaps your work on the current one. The scan holds at most that many extra
batches. The batch ahead is decoded on the thread pool, so a scan at the default degree of 1 uses one
pool thread beside yours, and `Prefetch = 0` keeps everything on your thread. On a mapped file with a
loop that works on every value it changed nothing measurable, about 3.1 ms at 0, 1 and 2. It pays
off when the source has latency to hide, as a remote one does.

A batch that costs less to decode than the trip to a pool thread and back is decoded on your thread
instead, whatever the degree and the prefetch: a column of a few thousand values read from a local
file costs a few microseconds, about what the trip adds. The scan starts that way when its first batch
looks small, and hands the batches after it to the pool as soon as one costs more.

## Local and remote

The loop is the same for every source, and there is no synchronous enumeration to choose. A read from
a mapped file completes at once, so the `await` costs no suspension, while a remote read suspends
until the data arrives. Inside the body the compiler's rule applies: a `Column<T>` cannot live across
an `await`. Do the columnar work, then await what you need, then let the loop move on.

## Watch out

* A batch's columns must not cross threads. Their spans point into buffers the scan reuses. To hand
  rows to another thread, take owned batches ([owned-batches.md](owned-batches.md)).
* A `MemoryPool` other than an `AlignedMemoryPool` only serves the builders and the owned batches,
  and the engine then decodes into `AlignedMemoryPool.Shared`.
* Two hosts in one process that want different parallelism or caches need two sessions, not a
  setting changed between calls.
* In a container, the free space a disk reports is not the real limit. A Kubernetes pod's writable
  layer and its `emptyDir` volumes count against its `ephemeral-storage`, which the kubelet enforces
  by evicting the pod while the disk still shows room. Without a `ScratchBudget`, a group by that
  spills only keeps a tenth of that free space, and a large one can get its pod evicted. Give its
  sessions a `ScratchBudget` under the pod's limit, minus whatever else the pod writes: a query that
  would exceed it then fails with `VortexMemoryException` instead, and its files are removed.

The contracts are described in
[the concurrency design](../design/09-contracts.md#1-concurrency-and-thread-safety) and
[the parallelism design](../design/09-contracts.md#2-parallelism), and the session in
[the public API design](../design/14-public-api.md#2-the-session).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- threads
```
