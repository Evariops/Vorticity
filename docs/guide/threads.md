# Threads

What is safe to share, what is not, and how to turn on parallel decoding.

## What is safe to share

| | |
|---|---|
| `VortexFile` | **Thread-safe.** Concurrent scans on one open file are expected: the footer and layout tree are immutable after the open |
| `ISegmentSource` | Must be thread-safe, and the built-in ones are |
| decoders and kernels | Pure functions over borrowed memory |
| `ScanPlan`, `KeyPlan` and the other plans | Immutable |
| the scan builder | **Not** thread-safe: build on one thread, then enumerate |
| `IAsyncEnumerator<RecordBatch>` | One consumer, as the language requires |
| `RecordBatch` | **Affine to its consumer.** Not thread-safe, and it must be disposed on the flow that consumed it |
| `KeyCursor` and its builder | Not thread-safe |
| `VortexFileWriter`, including an append | One writer per file |

So the shape that works is one open file, many scans:

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
Task<long>[] scans = new Task<long>[4];
for (int i = 0; i < scans.Length; i++)
{
    scans[i] = CountAsync(file);
}

long[] counts = await Task.WhenAll(scans);   // 500000, 500000, 500000, 500000
```

Each task builds its own scan and enumerates it. Nothing is shared but the file.

## Parallel decoding

Decoding is sequential by default. A library has no business taking over its host's thread pool: a
server running two hundred requests does not want each scan fanning out.

```csharp
file.Scan().WithDegreeOfParallelism(4)          // this scan
ScanBuilder.DefaultDegreeOfParallelism = 4;     // every builder made from here
```

The static is thread-safe, a builder reads it once when it is constructed, and a per-scan
`WithDegreeOfParallelism` always wins over it. It exists for a host that decides its threading at
start-up rather than at each of a hundred call sites; it is **1** unless you set it.

I/O concurrency is a separate thing and is always on: the reader issues overlapping reads whatever
the decode degree, because hiding latency is the point on an object store.

## What to expect from it

Six columns of 500 000 rows, three passes each, the fastest kept, on one machine:

| degree | |
|---|---|
| 1 | 5 ms |
| 2 | 5 ms |
| 4 | 6 ms |
| 8 | 6 ms |

Nothing, and slightly worse past 4. That is the honest result on this file, and it is what to expect
whenever decoding is not the bottleneck: these columns decode at hundreds of millions of values a
second, so five milliseconds is mostly the cost of walking 62 splits, which spreading over threads
does not reduce.

Raise the degree when decoding dominates — wide rows, heavy encodings, large blocks — and measure
rather than assume. `ExplainAsync` gives the splits a scan will walk, which is what the degree has
to spread; a scan with two of them has nothing to gain from eight threads.

## Watch out

* **A batch must not cross threads.** Its spans point into buffers the scan will recycle; hand over
  copied values, not columns.
* A column is a `ref struct`, so it cannot be captured by a lambda or held across an `await`. The
  compiler enforces this — see [scan-a-table.md](scan-a-table.md).
* Setting `DefaultDegreeOfParallelism` disturbs no builder already made and no enumeration already
  running.
* Parallel decoding does not reorder batches: they still arrive in file order.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- threads
```
