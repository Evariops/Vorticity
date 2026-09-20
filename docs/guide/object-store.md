# Object store

Implement the store seam for the service you use; also how to open a single file out of one.

`IObjectStore` is in `Vorticity.Dataset`. It is five methods, and no dependency on any cloud SDK —
that part is yours.

```csharp
public interface IObjectStore : IAsyncDisposable
{
    ValueTask<IReadOnlyList<string>> ListAsync(string prefix, string after, int limit, CancellationToken ct);
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken ct);
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken ct);
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct);
    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
}
```

## What each one must promise

| | |
|---|---|
| `PutIfAbsentAsync` | **the one that must be exact.** `Created` when it wrote, `Exists` when the key was already there, and never both for two racing callers. This is what makes a commit atomic |
| `GetRangeAsync` | a byte range, or `ObjectNotFoundException` for a key that is not there. Ranges are how the reader avoids downloading a file to read its footer |
| `HeadAsync` | length and last-modified, or `null` for a missing key — not an exception |
| `ListAsync` | keys under a prefix, after a key, at most `limit` of them, in lexicographic order, for paging |
| `DeleteAsync` | `true` when it deleted, `false` when there was nothing |

Implementations **must be thread-safe**. An `ObjectRange` is disposable and owns its buffer, so
return one the caller can dispose.

S3, Azure Blob and GCS all provide what `PutIfAbsentAsync` needs — a conditional put on
non-existence. A store that cannot promise it cannot host a dataset safely: two writers would both
believe they committed.

## The two that ship

```csharp
await using IObjectStore store = new FileObjectStore("/data/readings");  // a directory
await using IObjectStore memory = new MemoryObjectStore();               // for tests
```

`FileObjectStore` has a `Durable` flag that decides whether it flushes to disk on every put.
`MemoryObjectStore` is a test double with the seams to prove your error paths: `Fails` refuses a
chosen operation, `CrashesAfterPut` drops the process after a write lands, `Latency` slows every
call, and `Clock` drives the timestamps.

```csharp
await using MemoryObjectStore failing = new MemoryObjectStore
{
    Fails = (operation, key) => operation == ObjectOperation.GetRange,
};
// ObjectStoreException: The store was told to fail GetRange on 'readings.vortex'.
```

## Pricing what you do

`CountingObjectStore` wraps any store and counts:

```csharp
await using CountingObjectStore counting = new CountingObjectStore(inner);
await using VortexDataset dataset = await VortexDataset.CreateAsync(counting, schema);
Console.WriteLine($"{counting.Requests} requests, {counting.BytesRead} read, {counting.BytesWritten} written");
```

```
creating a dataset: 5 requests -- 1 get, 1 put, 3 list, 0 head, 0 delete;
                    111 bytes read, 111 written, 5 dependent steps
```

`CountOf(operation)` breaks it down, and `DependentSteps` counts the round trips that could not be
issued in parallel — the number that decides latency on a store where a request costs 30 ms.

## Opening one file out of a store

You do not need a dataset for this. `ObjectSegmentSource` adapts one object to the
`ISegmentSource` a `VortexFile` reads through:

```csharp
await using ObjectSegmentSource source = new ObjectSegmentSource(store, "readings.vortex");
await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default);
```

```
opened from the store: 1000000 rows, 1523369 bytes
scanned 1000000 rows out of the store
```

The open costs one ranged read of the tail, exactly as it does on a local file. From there every
projection and filter in this guide works, and each saves requests rather than just bytes.

`ObjectSegmentSink` is the other direction: a `VortexFileWriter` that writes into an object.

## Watch out

* **A scan asks its source for a segment once per block that needs it** — see
  [scan-a-table.md](scan-a-table.md). Over a network store that is a request each time, so put a
  cache in front of your implementation, or read into memory first.
* `SegmentReadOptions` on the source controls coalescing: nearby ranges merge into one request up to
  `CoalesceGapBytes`, which is what turns a hundred small reads into a handful.
* A key neither starts nor ends with `/`; the store checks and throws `ArgumentException`.
* `ObjectStoreException` is for a store that failed, `ObjectNotFoundException` for a key that is not
  there. Both are yours to throw from an implementation.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- object-store
```
