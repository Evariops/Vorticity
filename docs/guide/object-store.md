# Object store

Implement the store interface for the service you use, measure what an operation asks of it, and
open a single file straight out of a store.

```csharp
await using MemoryObjectStore memory = new MemoryObjectStore();
PutOutcome first = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);
PutOutcome again = await memory.PutIfAbsentAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, ct);

ObjectHead? head = await memory.HeadAsync("files/readings.vortex", ct);

byte[] buffer = new byte[64];
ObjectRead near = await memory.ReadAsync("files/readings.vortex", file.Length - 8, buffer, null, ct);
ObjectRead tail = await memory.ReadTailAsync("files/readings.vortex", buffer, ct);

PutOutcome stale = await memory.PutAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, PutCondition.IfMatch("0"), ct);
PutOutcome replaced = await memory.PutAsync("files/readings.vortex", PipeReader.Create(new ReadOnlySequence<byte>(file)), file.Length, PutCondition.IfMatch(head!.Value.Token), ct);

List<ObjectListing> page = await memory.ListAsync("logs/", "logs/01", ct).Take(2).ToListAsync(ct);
await memory.DeleteAsync(["logs/00", "logs/01", "logs/99"], ct);
```

```
put: Created, then Exists; the store holds 1 object, 1508316 bytes
head: 1508316 bytes, token 1; a missing key: null
read: asked 64 bytes 8 before the end, got 8 of an object of 1508316, token 1
tail: the last 64 bytes, and the length with them, 1508316
replace while the token is 0: Changed; while it is 1: Created, the token now 2
list under logs/ after logs/01, first two: logs/02 (1 byte), logs/03 (1 byte)
deleted a batch of three keys, one absent: 4 objects left
```

`IObjectStore` is the interface a dataset reads and writes through, in `Vorticity.IO` beside the
core's other I/O interfaces. It depends on no cloud SDK. Talking to S3, Azure Blob Storage or Google
Cloud Storage is the implementation's job, and so are retries, pooling and credentials.

```csharp
public interface IObjectStore : IAsyncDisposable
{
    ObjectStoreCapabilities Capabilities { get; }
    ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken);
    ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken);
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);
    ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken);
    ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);
    IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);
}
```

## What each member must promise

| | |
|---|---|
| `PutAsync` | The one that must be exact. Its condition says when it may write. `PutCondition.IfAbsent` writes only when the key is free and answers `Exists` when it is taken, and never `Created` to two racing callers, which is what makes a commit atomic. `PutCondition.IfMatch(token)` writes only while the object still has that token and answers `Changed` otherwise. `PutCondition.None` replaces whatever is there. The content is a `PipeReader` of the announced `length`, read as it is sent, so an object never has to fit in memory and a store can switch to a multipart upload based on the length. Content that ends early or runs long is refused and nothing is written. A cancelled put leaves the key as it was or holding the new bytes whole, never half written |
| `ReadAsync` | Bytes from an offset into the caller's memory, with the object's length and token from the same answer, because a token read in a separate call proves nothing about the bytes. The read stops at the object's end, and an offset at or past it is refused. Given a token, the store refuses to read another object rather than send its bytes. A missing key throws `ObjectNotFoundException` |
| `ReadTailAsync` | The last bytes of an object with its length, which lets a reader open a file by its tail without knowing its length. A store that reads suffixes answers in one request |
| `HeadAsync` | Length, token and creation time, or `null` for a missing key (not an exception). On a store with retention locks, it also gives the date before which a delete is refused (`RetainUntil`) and whether a legal hold applies (`LegalHold`) |
| `ListAsync` | The objects under a prefix, in ordinal order of their keys, after `startAfter`, each with its head. The implementation fetches pages as the enumeration advances, so a caller that stops early pays for one page. `LockReported` says whether the listing gives the lock too, or whether only a head does |
| `DeleteAsync` | A batch of keys, in any order. An absent key is not an error, and a store whose service caps batch sizes splits the batch. It reports nothing, since a store cannot always say which keys existed |
| `Capabilities` | What the store does in one request: a tail read that says the length (`ReadsSuffix`), a listing in order that resumes after a key (`ListsInOrder`), locks in its listing (`ReportsLocksInListing`), a replacement on a token's match (`ReplacesOnTokenMatch`). A caller takes the cheaper path without trying the other |

An object's token changes whenever the bytes under its key change. A dataset puts each of its
objects once, with `IfAbsent`, and never replaces one. Implementations must be thread-safe, and
their reads and heads must see every write that completed. A listing may lag, by less than the
retention window of a dataset the store serves, since a lagging listing costs a dataset freshness and
never a version. S3, Azure Blob Storage and Google Cloud Storage all offer the conditional put that
`IfAbsent` needs. A store that cannot promise it cannot host a dataset, because two writers would
both believe they had committed.

`GetRangeAsync` and `PutIfAbsentAsync` are extension methods over these members, for a caller that
wants a range in a pooled buffer or a plain creation.

A store with retention locks (S3 Object Lock, Azure immutable blob storage, a GCS bucket lock)
reports each object's lock in its head, and in its listing when the service says it there, and
refuses a delete before the date. A dataset on such a store declares it once at creation with
`DatasetOptions.LockedStore`. It then writes as few bytes as it can and vacuums only what the lock has
released, asking a head only for an object old enough to go whose lock the listing did not say.
`MemoryObjectStore.RetainFor` and `Hold` simulate such a store in tests.

`ObjectRange`, what `GetRangeAsync` returns, holds its bytes in a pooled buffer and gives it back
when the caller disposes the range.

## The three that ship

```csharp
await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));   // a directory
await using MemoryObjectStore memory = new MemoryObjectStore();               // for tests
await using CountingObjectStore counting = new CountingObjectStore(memory);   // around any store
```

`FileObjectStore` maps keys to files under a root directory. Its `Durable` option flushes every put
to the device before returning, for a store that is the system of record. Claiming a key is atomic
there, but writing the content is not, so a process that dies mid-put leaves a short object under a
taken key, which the format detects when the object is read. A read that meets a put still in
progress waits for it. A commit left short throws `TornCommitException`, which names its version,
and `VortexDataset.RemoveTornCommitAsync` removes it. A put without a condition writes a temporary
file and renames it over the key, so a reader sees the old object or the new one. No file system
compares and renames in one step across processes, so a put on a token throws `NotSupportedException`
there.

`MemoryObjectStore` is a test double with hooks for exercising your own error paths. `Fails` refuses
a chosen operation, `CrashesAfterPut` throws after a put has landed (so the object exists but its
writer never learned it), `Latency` delays every call, and `TimeProvider` stamps the objects it
creates.

```
a store told to fail: ObjectStoreException: The store was told to fail Read on 'files/readings.vortex'.
a key that is not there: ObjectNotFoundException, key 'files/nothing'
```

`ObjectStoreException` means the store refused or failed, and `ObjectNotFoundException` means a key
is not there. Both derive from `VortexException`, and an implementation should throw them too.

## Pricing what you do

`CountingObjectStore` wraps any store and counts what passes through: `Requests`, `CountOf` each
`ObjectOperation`, `BytesRead`, `BytesWritten`, `KeysListed`, and `DependentSteps`. That last one is
a lower bound on the round trips that had to wait one after another, and it is what sets latency on
a store where each request costs tens of milliseconds. `Reset` starts the counts again.

```
creating a dataset: 5 requests (1 read, 0 head, 1 put, 3 list), 5 dependent steps, 123 bytes read, 123 written
opening it: 2 requests (1 read, 0 head, 0 put, 1 list), 2 dependent steps, 123 bytes read, 0 written
```

[dataset-maintenance.md](dataset-maintenance.md) prices compaction, verification and vacuum the
same way.

## Opening one file out of a store

A single file needs no dataset. A session that holds a store opens a location in it as it opens a
path. The scheme is yours to choose, and the first segment after it names the store's bucket:

```csharp
await using (VortexSession session = VortexSession.Create(o =>
{
    o.SegmentCache = new SegmentCache(64L * 1024 * 1024);
    o.ObjectStores.Add("mem://lake", counting);
}))
{
    await using VortexFile opened = await session.OpenAsync("mem://lake/files/readings.vortex");
    // ...
}
```

```
open from the store: 1000000 rows; 1 requests (1 read, 0 head, 0 put, 0 list), 1 dependent steps, 65600 bytes read, 0 written
mean of Celsius for Day >= 900: 30.000; 2 requests (2 read, 0 head, 0 put, 0 list), 2 dependent steps, 151432 bytes read, 0 written
a full scan, 1000000 rows: 15 requests (15 read, 0 head, 0 put, 0 list), 9 dependent steps, 1315268 bytes read, 0 written
the same scan again, its segments in the session's cache, 1000000 rows: 0 requests (0 read, 0 head, 0 put, 0 list), 0 dependent steps, 0 bytes read, 0 written
```

The open costs one request, since the memory store reads a suffix: the last 64 KiB of the object and
its length come back together, and the open finds its footer there. A store that cannot read a
suffix asks for a head first. From there every projection and filter in the guide works, and each
one saves requests as well as bytes. The reads of a batch are coalesced into ranges and issued
together, each landing in the block the scan decodes from, so the filtered mean costs 2 requests and
a full scan 15 for a 1.51 MB file. The session's segment cache keeps what was read, and the same scan
again fetches nothing. Because the reads of a scan overlap, `DependentSteps` varies a little from one
run to the next, while the request and byte counts do not.

A file at a location takes appends as a file at a path does, through `session.OpenWriterAsync`:

```csharp
await using (VortexFileWriter appending = await session.OpenWriterAsync("mem://lake/files/readings.vortex"))
{
    await appending.WriteAsync<Reading>([new Reading(1_000, 21.5, "Lyon"), new Reading(1_001, 22.0, "Lyon")]);
    await appending.CompleteAsync();
}
```

```
two rows appended at the location: 1000002 rows now; 8 requests (7 read, 0 head, 1 put, 0 list), 8 dependent steps, 3083560 bytes read, 1523077 written
```

An object cannot grow, so the append replaces it, at completion, with its old bytes followed by the
new ones, and only while nobody else replaced it in between. The memory store copies nothing on a
service, so the old bytes are read back and sent again in one put. The stores of the
`Vorticity.ObjectStorage` package copy them on the service instead.

A store registered with a session stays yours: the session never disposes it, so one store can serve
several sessions, and the sample goes on using this one after its session. The stores for S3 and the
clouds that speak its API, Azure and Cloud Storage are in the `Vorticity.ObjectStorage` package (see
[object-storage.md](object-storage.md)).

## Watch out

* The interface may still change until the stores for S3, Azure and Cloud Storage are tested against
  the services themselves, so pin the version.
* A store that declares `ListsInOrder` must honour `startAfter` and ordinal order, or a dataset's
  listing of its commits goes wrong. Test an implementation against `MemoryObjectStore`, which is the
  reference behaviour.
* `ReadAsync` writes into the caller's memory until it returns, so that memory must stay alive and
  untouched until the read completes, even when the caller gives up on it.
* An `ObjectRange` owns its buffer. Dispose it once, and do not read its bytes afterwards.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- object-store
```
