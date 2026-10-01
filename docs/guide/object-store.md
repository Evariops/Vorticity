# Object store

Implement the store seam for the service you use, measure what an operation asks of it, and open
a single file out of one.

```csharp
await using MemoryObjectStore memory = new MemoryObjectStore();
PutOutcome first = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);
PutOutcome again = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);

ObjectHead? head = await memory.HeadAsync("files/readings.vortex", ct);

using (ObjectRange tail = await memory.GetRangeAsync("files/readings.vortex", file.Length - 8, 64, ct))
{
    Console.WriteLine($"range: asked 64 bytes 8 before the end, got {tail.Length}, token {tail.Token}, contiguous {tail.IsContiguous}");
}

List<string> page = await memory.ListAsync("logs/", "logs/01", ct).Take(2).ToListAsync(ct);
await memory.DeleteAsync(["logs/00", "logs/01", "logs/99"], ct);
```

```
put: Created, then Exists; the store holds 1 object, 1508212 bytes
head: 1508212 bytes, token 1; a missing key: null
range: asked 64 bytes 8 before the end, got 8, token 1, contiguous True
list under logs/ after logs/01, first two: logs/02, logs/03
deleted a batch of three keys, one absent: 4 objects left
```

`IObjectStore` is the seam a dataset reads and writes through. It lives in `Vorticity.Dataset`,
which is experimental (see [datasets.md](datasets.md)), and it depends on no cloud SDK: talking
to S3, Azure Blob Storage or Google Cloud Storage is the implementation's business, and so are
retries, pooling and credentials.

```csharp
public interface IObjectStore : IAsyncDisposable
{
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken);
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken cancellationToken);
    ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);
}
```

## What each member must promise

| | |
|---|---|
| `PutIfAbsentAsync` | **the one that must be exact.** `Created` when the key was free and now holds the bytes, `Exists` when it was taken, and never `Created` for two racing callers: this is what makes a commit atomic. The content is a `PipeReader` of announced `length`, read as it is sent, so an object never has to fit in memory and a store can choose a multipart upload by the length; content that ends early or runs past it is refused and nothing is created. A cancelled put leaves the key created or absent, never half written |
| `GetRangeAsync` | the bytes of a range and the object's token **in one answer**, because a token read in a separate call proves nothing about the bytes. The length is clamped to the object, which is how a reader opens a file by its tail without knowing its length; a missing key throws `ObjectNotFoundException` |
| `HeadAsync` | length, token and creation time, or `null` for a missing key, not an exception; and, on a store that keeps the object under a retention lock, the date before which it refuses to delete it (`RetainUntil`) and whether a legal hold keeps it (`LegalHold`) |
| `ListAsync` | the keys under a prefix, in ordinal order, after `startAfter`. The implementation fetches pages as the enumeration advances, so a caller that stops early pays for one page |
| `DeleteAsync` | a batch of keys, in any order; an absent key is not an error, and a store whose service caps a batch splits it. It reports nothing, since a store cannot say which keys existed |

An object is never overwritten, and its token changes whenever the bytes under its key change,
which for immutable objects means the key was deleted and created again. Implementations must be
thread-safe and list with strong consistency. S3, Azure Blob Storage and Google Cloud Storage all
offer the conditional put `PutIfAbsentAsync` needs; a store that cannot promise it cannot host a
dataset, because two writers would both believe they committed.

A store under a retention lock, S3 Object Lock, Azure immutable blob storage or a GCS bucket lock,
reports each object's lock in its head and refuses a delete before the date. A dataset on such a store
says so once, `DatasetOptions.LockedStore` at creation, and then writes as few bytes as it can and
vacuums only what the lock has let go; `MemoryObjectStore.RetainFor` and `Hold` play such a store in
tests.

`ObjectRange` holds its bytes as a `SegmentLease`, so a store can hand back a pooled buffer, memory
it keeps, or a response that arrived in pieces, and gets it back when the caller disposes the
range. `Bytes` is always valid; `Memory` only when `IsContiguous`.

## The three that ship

```csharp
await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));   // a directory
await using MemoryObjectStore memory = new MemoryObjectStore();               // for tests
await using CountingObjectStore counting = new CountingObjectStore(memory);   // around any store
```

`FileObjectStore` maps keys to files under a root directory; its `Durable` option flushes every
put to the device before it returns, for a store that is the system of record. Claiming a key is
atomic there, but writing the content is not: a process that dies mid-put leaves a short object
under a taken key, which the format detects when the object is read. A read that meets a put still
writing waits for it; a commit left short throws `TornCommitException`, which names its version, and
`VortexDataset.RemoveTornCommitAsync` removes it. `MemoryObjectStore` is a test double with the seams to prove your
own error paths: `Fails` refuses a chosen operation, `CrashesAfterPut` throws after a put has
landed, so the object exists and its writer never learned it, `Latency` delays every call, and
`TimeProvider` stamps the objects it creates.

```
a store told to fail: ObjectStoreException: The store was told to fail GetRange on 'files/readings.vortex'.
a key that is not there: ObjectNotFoundException, key 'files/nothing'
```

`ObjectStoreException` is a store that refused or failed, `ObjectNotFoundException` a key that is
not there; both derive from `VortexException`, and both are yours to throw from an implementation.

## Pricing what you do

`CountingObjectStore` wraps any store and counts what passes through: `Requests`, `CountOf` each
`ObjectOperation`, `BytesRead`, `BytesWritten`, `KeysListed`, and `DependentSteps`, a lower bound
on the round trips that waited one after another, which is what decides latency on a store where
a request costs tens of milliseconds. `Reset` starts again.

```
creating a dataset: 5 requests (1 get, 0 head, 1 put, 3 list), 5 dependent steps, 123 bytes read, 123 written
opening it: 2 requests (1 get, 0 head, 0 put, 1 list), 2 dependent steps, 123 bytes read, 0 written
```

[dataset-maintenance.md](dataset-maintenance.md) prices compaction, verification and vacuum the
same way.

## Opening one file out of a store

A single file needs no dataset. `ISegmentSource`, the core's I/O seam, is two reads and a length,
and a store object is one in a dozen lines:

```csharp
private sealed class StoreSegmentSource(IObjectStore store, string key, long length) : ISegmentSource
{
    public long Length => length;

    public async ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
    {
        ObjectRange bytes = await store.GetRangeAsync(key, range.Offset, range.Length, cancellationToken);
        return new SegmentLease(bytes.Bytes, bytes);
    }

    public async ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
    {
        // one GetRangeAsync per range, issued together, and every lease disposed if one fails
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

```csharp
await using (VortexFile opened = await VortexSession.Default.OpenAsync(new StoreSegmentSource(counting, "files/readings.vortex", known.Length)))
```

```
open from the store: 1000000 rows; 2 requests (1 get, 1 head, 0 put, 0 list), 2 dependent steps, 65588 bytes read, 0 written
mean of Celsius for Day >= 900: 30.000; 4 requests (4 get, 0 head, 0 put, 0 list), 4 dependent steps, 151392 bytes read, 0 written
a full scan, 1000000 rows: 45 requests (45 get, 0 head, 0 put, 0 list), 40 dependent steps, 1465556 bytes read, 0 written
a full scan through a session with a segment cache, 1000000 rows: 45 requests (45 get, 0 head, 0 put, 0 list), 45 dependent steps, 1465556 bytes read, 0 written
```

The open costs one ranged read of the tail, plus the head the sample asks for the length. From
there every projection and filter of the guide works, and each saves requests, not just bytes: a
filtered mean fetches 4 of the file's 51 segments. The session's `MaxConcurrentReads` bounds
the reads in flight across every file it opens this way, and its segment cache applies too. A
scan fetches each segment once, even a chunk that spans several blocks, and none that lies in the
tail the open read: 45 requests and 1.47 MB for a 1.51 MB file, with a cache or without. The cache
pays across scans: a second scan through the same session fetches nothing
([open-a-file.md](open-a-file.md)).

The source receives the ranges of a batch together, so a source over a network store can coalesce
neighbouring ranges into one request; the sample's does not, to stay short. Because the reads of a
scan overlap, `DependentSteps` moves a little from one run to the next; the request and byte counts
do not.

## Watch out

* **The seam is experimental** with the rest of the package: pin the version.
* `ListAsync` must honour `startAfter` and ordinal order, or a dataset's listing of its commits
  goes wrong. Test an implementation against `MemoryObjectStore`: it is the reference behaviour.
* An `ObjectRange` owns its buffer. Dispose it once, and do not read its bytes after.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- object-store
```
