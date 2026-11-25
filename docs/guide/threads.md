# Threads

Configure a session, share what is safe to share, and turn on parallelism where it pays.

```csharp
await using VortexSession session = VortexSession.Create(o =>
{
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
| `SegmentCache` | the process's, `SegmentCache.Shared`, with no budget of its own: what the work leaves, given back above a floor when the work needs the room or no scan read it of late; none for a session with a `Keyring` | segments kept across scans, one budget for every file of the sessions that share it whose reads do I/O. A cache of 0 bytes keeps none |
| `MaxConcurrentReads` | 128 | reads in flight across every scan of every file of the session whose reads do I/O |
| `MaxDegreeOfParallelism` | 1 | how many chunks a scan decodes and aggregates at once, and how many threads a writer uses |
| `IndexCacheBytes` | 64 MiB | decoded index runs kept per open file, in blocks of the pool given back when the file is disposed |
| `MappedFileCacheCount` | 64 | files opened from a path that stay mapped after they are closed, so the next open reuses the mapping |
| `MapFiles` | true | whether a file opened from a path is mapped by its first scan. When false, it is read positionally, and a file truncated during a scan fails the read instead of the process |
| `MemoryBudget` | the process's | the memory the session's queries share with every session given the same `QueryMemoryBudget`: their group tables, their merges and the batches of their results until they are delivered. Past it, a group by spills to `ScratchDirectory`, or fails with `VortexMemoryException` |
| `JobMemoryBudget` | the process's | the memory the session's writers and a dataset's jobs share with every session given the same `QueryMemoryBudget`: their builders, their pages and the bytes their pipes hold. Past it, a write fails with `VortexMemoryException` rather than the process being killed |
| `ScratchDirectory` | the system's temporary directory, or the user's cache when that lies in memory | where a group by writes what its memory budget cannot hold, and where a sort and a writer write their runs. A file system in memory is refused: where `/tmp` is one, as on Ubuntu 26.04, the default is `$XDG_CACHE_HOME/vorticity`, or `~/.cache/vorticity`, which the `Vorticity` event source says once |
| `ScratchBudget` | none, a tenth of the directory's free space is kept free | the scratch space the session's queries share with every session given the same `ScratchBudget`. Past it, `VortexMemoryException`, and no file is left behind |
| `Extensions` | empty | extension types the session reads beyond the editions |

The options are set inside `Create` and frozen when it returns. Setting one afterwards throws
`InvalidOperationException: The session is created; its options are frozen. Set them inside
VortexSession.Create.` `VortexFile.OpenAsync(path)` uses `VortexSession.Default`, which is
immutable: the shared pool, no cache, 128 reads in flight, no parallelism, and 64 closed files kept
mapped.

Up to version 0.13, `MemoryPool` took an `AlignedMemoryPool` or any `MemoryPool<byte>` for the session's buffers, and `AlignedMemoryPool` was public. Both are gone: every block the library rents comes from its one pool, which the memory limit governs ([The memory limit](#the-memory-limit)). A store, a sink or a transport written against the public surface rents its buffers from `VortexBytePool.Instance`, an `ArrayPool<byte>` over the same pool, kept and let go as the library's blocks are.

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

Every block a scan, a read or a write gives back is parked in the process-wide pool for the next rent, in size classes of powers of two from 4 KiB to 32 MiB, and one block of up to 256 MiB on its own. The arrays a table, a decoder or a writer grows, which need no fixed address, come from the pool's typed face, in classes of powers of two up to 64 KiB and eight sizes an octave past it, so that a block wastes an eighth of itself at most. The pool has no budget of its own and reads no processor count: it keeps every block given back, so it never holds more than the work rented at once, and the process takes back what it keeps above a floor of 256 KiB a class.

It lets go in three ways. After each full collection, every class lets go of the blocks no rent took since the collection before: under pressure the collector runs more often, and the pool empties faster. Every 30 seconds a slow timer does the same, for the process that neither allocates nor collects: at rest, the pool is back to its floors within a minute. And when a reservation finds no room, or the machine's memory load is high at a collection, the process takes back everything kept above the floors, in a fixed order: the pool's blocks first, then the segments a cache keeps, then the objects a dataset keeps open, then its pages, what costs no request to rebuild before what does. In a container, the runtime measures the memory load against the container's memory limit. Two full collections with no work between them, as a benchmark that collects before each round runs, empty the pool down to its floors: the next work rents its blocks anew.

A host that collects every few seconds for its own work lets go of its blocks as often, and a slab emptied so goes back with its region whole, which the next slab takes again. Measured with the host scenario (`--micro host`) under the server collector, a full collection every second and a follower reading every row every ten seconds: the pool made no slab past its first eight in three minutes, the floors and the blocks of the handles keeping its slabs alive between the reads.

Keeping every block given back serves 32 questions at once, which each hold dozens of blocks of the same few classes: with 1 GiB parked in all, one block in four was freed as it came back, and the next rent took it from the system again, every page faulted anew. The floors keep what the next question rents first: on an Apple M4 Pro, the 448 rents of 4 to 256 KiB a question makes took 25 to 27 µs from blocks parked, 64 to 68 µs cut from a slab kept and 84 to 92 µs from new slabs. A turn of the slow timer took 1.1 to 1.6 ms with 40 dataset handles open, whose floors kept everything they read: the questions asked after two turns made no request to the store.

The blocks parked are managed arrays the runtime keeps pinned: they count in the managed heap, a heap dump shows them on the pinned object heap, and `vortex.memory.retained` tags them `pool` ([observability.md](observability.md#the-memory)). There is one pool in the process. What a session's work holds of it while it runs is counted in its budgets: a query's blocks under `MemoryBudget` until its result is delivered, a writer's under `JobMemoryBudget` until it is disposed. To bound them, give the session budgets of its own:

```csharp
await using VortexSession session = VortexSession.Create(o =>
{
    o.MemoryBudget = new QueryMemoryBudget(512L * 1024 * 1024);
    o.JobMemoryBudget = new QueryMemoryBudget(256L * 1024 * 1024);
});
```

What the pool keeps parked between rents is counted in no budget: it is the process's, kept above its floors only for as long as rents take it, and read by the budgets with the rest of the process.

## The memory limit

The library holds its work to the process's limit. In a container, the limit is the container's, read from its cgroup, v2 then v1, when the process starts and again every second. Outside a container, nothing holds the process but the collector, whose limit is the machine's memory unless the host configured the heap's. A host that wants its process held to less sets the limit itself, in code or in its environment:

```csharp
VortexMemory.SetLimit(4L << 30);   // as a container's limit of 4 GiB would hold the process
```

```
VORTICITY_MEMORY_LIMIT=4g          # read when the library first needs a limit; 4294967296, 4096m and 4Gi say the same
```

A value of the variable that is no size fails each need of the limit with the same `InvalidOperationException`, which names the variable and its value. A limit set is taken as a container's limit of that size would be, and in a container the least of the two holds. The collector's heap is held to three quarters of it, what the runtime gives itself in a container: near that, the collector runs full collections before an allocation fails. The library's own limit is seven eighths of the heap's, and its budgets read what the process uses every second, its resident memory outside a container. The questions reserve within it, and the jobs within their share, half of it by default, each block of a job asking the process too. A block is reserved before it is allocated, so a block the budget refuses never reaches the heap. The quarter of the limit the heap leaves is for what the collector does not hold: the runtime, the threads' stacks, the native allocator. Measured with the backlog bench, what the process held past its committed heap peaked at 345 MiB under 2 GiB, within that quarter of 512 MiB, and what no reservation counts grew by 155 MiB in a second at most, within the eighth the library keeps as a margin.

| limit | the collector's heap | the library's limit | the jobs' budget |
|---|---|---|---|
| 2 GiB | 1 536 MiB | 1 344 MiB | 672 MiB |
| 4 GiB | 3 072 MiB | 2 688 MiB | 1 344 MiB |
| 8 GiB | 6 144 MiB | 5 376 MiB | 2 688 MiB |

Measured with the backlog bench (`--micro backlog --memory-limit`), a maintainer compacting 300 appends of metric records, 9 million rows, 194 MB, on an Apple M4 Pro:

| limit | jobs | time | peak resident | written over the backlog |
|---|---|---|---|---|
| 2 GiB | 11 | 50 s | 1 240 MiB | 0.69 |
| 4 GiB | 5 | 48 s | 2 013 MiB | 0.61 |
| 8 GiB | 1 | 35 s | 1 711 MiB | 0.25 |

A tighter limit makes the jobs narrower: more of them, each rewriting part of the backlog before the last consolidates it. A job refused once by its budget plans narrower and runs; none failed. An append is held the same way: an append of 324 000 rows, whose columns end in blocks of 64 MiB, is refused by its budget under 1 GiB rather than failing the heap ([frequent-appends.md](frequent-appends.md)).

The blocks the library rents are pieces of pinned slabs, and a slab fills the region of the pinned heap the collector opens for it, but for its last page: given back, it gives its region back whole, which any heap and any generation then takes. The pinned heap is never compacted, and its limit counts what it keeps committed, so any free space between its arrays stays counted until an array of a size that fits comes along. Blocks of every size, each a pinned array of its own, left 452 MB of such holes under 2 GiB, and a compaction failed. Slabs of 32 MiB less 64 bytes, the first size tried, took a region of 64 MiB two at a time, and a small pinned array the host made, laid in the end of such a region, kept it alive once its slabs were gone: a host answering for a day under the server collector kept 2.9 GB of holes in a pinned heap of 4.2 GB.

So a slab is a large region of the collector less a page and 80 bytes, 32 MiB but under a small heap shared by many heaps, which the library works out from what the collector reports. A block of a class more than half a region, 32 MiB, is the first half of a slab twice its size, whose second half serves the smaller blocks. A block past the classes is a pinned array as long as the region it takes. And the library pins nothing else: a sealed key lies in a cell of a page of the pool, an arena's tree is an ordinary array, a dictionary's values are a block of the pool. The first slab of a process can lie in the first region of a heap's pinned heap, beside the runtime's own pinned objects: once gone it leaves a hole of its size, which the next slab of that heap fits exactly.

A slab goes back to the collector with its last piece, so the pool lets go of its blocks slab by slab. Before each letting go it counts what each slab holds, and orders each class's blocks so that a rent takes those of the slab most in use and a letting go those of the slab least in use: the slabs the work no longer needs empty, and go. Asked for room, it gives back first the block it keeps past its classes, then every slab whose pieces it keeps all, then the blocks of the slabs least in use. A class's floor stays only on a slab that rented blocks keep alive anyway, or on one slab: 256 KiB of floor left alone on a slab would keep 32 MiB. What the slabs hold free between their pieces is the pool's, counted under `pool` in the memory kept: the next blocks are cut from it without the heap growing. The budget still counts it with the rest of the process, since the arrays a query's tables grow cannot use it.

Measured with the same bench, after the jobs, two turns of the slow timer and a full collection that gives the collector's free regions back, the maintainer's handle still open:

| Limit | Slabs left | Resident | What the questions get |
|---|---|---|---|
| 2 GiB | 288 MiB, 275 of it free | 539 MiB | 919 MiB of 1 344 |
| 4 GiB | 32 MiB | 228 MiB | 2 105 MiB of 2 688 |
| 8 GiB | 32 MiB | 171 MiB | 5 307 MiB of 5 376 |

Under 2 GiB the eleven narrower jobs leave 11 MiB of blocks the handle keeps, its open objects and pages, spread over nine slabs, which they hold alive: a slab goes with its last piece. The pool letting go class by class, before the slabs were let go whole, left 608 MiB of slabs for 5 MiB of blocks under 2 GiB, and the process 782 MiB resident.

Set the limit when the process starts, before it holds data: `SetLimit` refuses a limit whose heap the collector already passes after a full collection, and the limit set before stays. `VortexMemory.LimitBytes` says the limit set, and `VortexDiagnostics.GetMemoryReport().LimitBytes` the library's own.

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

The session's degree is the number of cores its work takes beside its callers' own threads, all of it together: the writers' helpers and the parts of `WriteRecordsAsync`, the scans' decodes ahead of their loops, a count's lanes, a dataset's objects answered side by side, an aggregation's lanes and its merge, a Parquet file's pages and columns, and the compaction jobs. Every one of them past the first, which runs on your thread, starts only on a lane of the session's that is free, and holds it until it ends: a group by and a job run together never take more cores than the degree beside your thread. Your thread is one of a scan's degree: a scan at 4 decodes on three pool threads at most beside your loop. A compaction job has no caller, and holds one of the session's lanes for its own flow, its reads' decodes and its writer's helpers taking the others; its flow gives that lane back while an object it writes waits for the store, and takes it again after: at a degree of 8, a maintainer's jobs compute on eight threads at most, however many run at once. What waits on the store or the disk and computes little, the objects a question opens, a top's reads of its best granules, the reads of a local file, the writes of the synopsis cache, goes beside the degree, since a lane held while a request is out does nothing the degree is for. The runtime's own work comes on top: the garbage collector, and the thread pool's idle spin (below). Whatever holds a lane takes another only when one is free, or does the work itself, so that a session never waits on its own degree; a question's work that must run whatever the lanes, an object of a dataset answered beside the others, waits for a lane holding none, as a job does before it starts.

A writer uses the session's degree too, and `VortexWriteOptions.DegreeOfParallelism` overrides it
for one file. When a batch of a block or more comes in, the writer summarizes its columns side by
side: each column's statistics, plus a text column's bounds and the distinct values a dictionary is
priced from. It then compresses each column's chunk as a task of its own, on a free lane while the
rows after the chunk go through, the chunks of one column one after another, and writes them in
column order as the column's next chunk leaves; a writer that builds indexes writes them before the
chunk closes, its columns still compressed side by side. A column whose previous chunk was tried as
zstd on a quarter of a megabyte of values or more has its zstd frames compressed in parallel too, one
frame per block, beside the other columns' work. A column with a chunk target of its own, as a
dataset's objects have, goes the same way, chunk by chunk. The file is byte for byte the same
whatever the degree. A write that builds indexes summarizes its first block on one
thread, which tells its index builders what the statistics say of it, and every block after it side by
side; one that indexes a nested column summarizes every block on one thread.

A writer of a table of several columns cut into chunks by their bytes, the default, takes your rows a block at a time whatever the calls you write them in, records and batches alike: a call of a few rows waits, copied, in the block it falls in, and a call of many is cut into blocks where it lies, each copied as it goes through. From its fifth block on, on more than one lane, it sends up to four blocks through at once, each column's work on them in the order of the blocks, so that a column quick to take a block runs ahead of the slowest one rather than waiting for it at every block. A call returns once its blocks went through, so that you may reuse what its batch lies in. The file is the same bytes at any degree, and calls of a block or more write the bytes of one call; calls of a few rows of text write the same rows, a text that repeats the row before shared within a call alone. A compaction job's writer does the same from its first block when its object is aimed at 32 blocks or more, and its objects are the same bytes at any degree: the job reads what an object holds at an end of a group of rows, once its blocks in flight landed. A block that makes a whole chunk goes through with the others too; one longer than a chunk, which the writer must cut, goes through alone. Each block of a consolidation of metric records made a chunk, and going through alone they held the job at 11.1 s at fourteen lanes; through the line, 9.2 s, and 10.0 at eight.

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

When your loop is the larger part of the work, run it on the lanes too. A fold that keeps a state goes through `AggregateAsync<TAggregator, TState>()` with an `IBatchAggregator` of your own ([aggregates.md](aggregates.md#an-aggregator-of-your-own)). Work that keeps no state, an export or a copy into another store, takes the scan in parts: `SplitAsync(n)`, on a typed scan or an untyped one, cuts it into at most `n` scans of disjoint rows, a file's where every column it reads starts a chunk and a dataset's at its objects, and each part is a scan you enumerate on a task of your own.

```csharp
IReadOnlyList<Scan<Reading>> parts = await file.Scan<Reading>().SplitAsync(4);
await Parallel.ForEachAsync(parts, async (part, ct) =>
{
    await foreach (Columns<Reading> batch in part.WithCancellation(ct))
    {
        Export(batch);   // on this task's thread, beside the other parts'
    }
});
```

A writer has the same limit the other way: you fill its builder on one thread. Fill it in parts instead, `writer.CreatePartAsync<TRecord>()`, each a builder of its own on a thread of yours, written in the order the parts were created, or hand it records already in memory with `writer.WriteRecordsAsync(records)`, which fills a part a lane ([write-rows.md](write-rows.md)).

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

## The garbage collector

Which garbage collector the host runs matters as much as its cores. Measured with the paths bench's
throughput run (`--qps`) over a warm mix of 11 questions on a short store, four hours of spans and
logs, each question at one lane, on the same 16-core machine:

| clients | workstation GC, q/s | time paused | server GC, q/s | time paused |
|---|---|---|---|---|
| 8 | 571-599 | 5 % | 517-527 | 1 % |
| 16 | 804-824 | 10-11 % | 908-914 | 2 % |
| 32 | 742-749 | 16-17 % | 856-864 | 3 % |

The workstation collector, a console app's default, collects the youngest generation on one thread
while every other thread waits. The more questions in flight, the longer each one lasts, the more of
what it holds is still alive at a collection, and the longer every thread waits: past 16 questions at
once on 16 cores, the throughput falls. The server collector, an ASP.NET Core app's default, collects
on every core and pauses the process a fraction as long.

* Run the server collector in a process that answers many questions at once:
  `<ServerGarbageCollection>true</ServerGarbageCollection>` in the project, or `DOTNET_gcServer=1`.
  Since .NET 9 it sizes its heaps to the load.
* Keep the questions a process runs at once near its physical cores when they read local or cached
  data, with a semaphore or a concurrency limiter (`System.Threading.RateLimiting`): 16 at once answered
  more than 32 under both collectors. Questions that wait on a remote store want more in flight to hide
  its latency.
* A host that keeps the workstation collector can give its youngest generation a larger budget:
  `DOTNET_GCgen0size=0x8000000` (128 MiB) answered 900 questions a second at 16 clients and 829 at 32,
  paused 3 % and 7 % of the time.

### The pinned heap under each collector

The pinned heap holds every array that must not move: the library's slabs, and the host's own pinned buffers, a web server's pool of 4 KiB blocks among them. It is never compacted, and the heap's limit counts what it keeps committed, so free space between its arrays stays counted until an array that fits comes along. The server collector splits it in a heap a core, each allocating in its own regions, and sends a pinned allocation to the heap with the most budget left, not to the one that has a hole of its size. The library's slabs fill their regions and go back whole, so they leave no hole under any collector.

Measured with the host scenario (`bench/host.sh`): four sealed datasets of 2.4 million log records each, a follower reading every row every five seconds, three page clients asking counts by service, and the host's own pinned blocks and garbage, five minutes under a limit of 3 GiB on an Apple M4 Pro, before and after the slabs filled their regions:

| collector | pinned heap free, before | after | full collections, before and after | time paused, before and after |
|---|---|---|---|---|
| server, its heaps adapted to the load | 93 to 99 MiB | 1 to 3 MiB | 472, 450 | 20.4 s, 16.3 s |
| workstation | 33 MiB | 4 MiB | 4 154, 4 096 | 72.2 s, 72.3 s |
| server, two heaps | 7 MiB | 1 MiB | 216, 208 | 4.8 s, 4.7 s |

The questions answered were the same within 2 %, and none was refused. Under the server collector the free space no longer depends on the number of heaps, so a host keeps its collector for its throughput, with no setting for the library's sake. A host whose process allocates no native memory, as the library allocates none, can raise the collector's share of a container, three quarters by default, with `DOTNET_GCHeapHardLimitPercent`: at 90, a container of 10 GiB leaves the library 1.3 GiB more. The library keeps an eighth of the collector's limit as a margin for the collector's own work.

## The thread pool's idle spin

A thread of .NET's pool that runs out of work spins for a while before it sleeps, in case more arrives. A writer or a scan on many lanes hands the pool short items in waves, so its idle threads spin between them: 17 % of a write's processor time at 14 lanes, in a profile of a file of ten million rows on an M4 Pro (2026-11-17). The spin does no work, and the cores it takes are ones the write had left idle.

A host that runs the library on many lanes, a maintainer compacting a dataset or a batch job writing files, can turn it off with `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0` in its environment. Measured on that write at 14 lanes, two runs each, the time stayed the same (58.7 and 69.4 ms a round with the spin, 59.1 and 63.7 without), and the processor time fell by a quarter (406.8 and 483.4 ms a round, against 313.2 and 361.8). Keep the default in a process whose threads answer requests one at a time, where a thread that spins answers the next one sooner.

## Prefetch

`ScanOptions.Prefetch` is the number of batches read ahead of the loop, 1 by default, so that the
next batch's reads overlap your work on the current one. The scan holds at most that many extra
batches. A batch ahead is decoded within the degree, of which your thread is one: at the default degree
of 1 it is decoded on your thread when your loop asks for it, its reads already in, and nothing runs on
the pool beside you; at a degree of 4, up to three pool threads decode ahead of your loop, each on a
lane of the session's. On a mapped file with a loop that works on every value it changed nothing
measurable, about 3.1 ms at 0, 1 and 2. It pays off when the source has latency to hide, as a remote
one does.

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
