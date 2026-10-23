# Object storage: S3, R2, OVHcloud, Scaleway, Azure and Google Cloud Storage, natively

Vorticity reads and writes files and datasets straight on Amazon S3, on Cloudflare R2, OVHcloud and
Scaleway, which speak S3's API, on Azure Blob Storage and on Google Cloud Storage, through clients
written in this repository over the platform's HTTP client. A program that opens
`/data/readings.vortex` today opens `s3://lake/readings.vortex` with the same call, and a dataset
whose store is a directory moves to a bucket by changing the store and nothing else. This document
is the design of that support: where it lives, what it changes in the store interface, how a request
is built, sent, retried and counted, what each provider does differently, and what each operation
costs in requests.

It is built, in the core's store interface and in the package `Vorticity.ObjectStorage`, and held by
the published vectors of each protocol, by emulators of each service under the HTTP client, and by
the store contract run against a service's own bucket when the environment names one. Where the code
departs from this design, [the last section](#19-where-the-code-departs-from-this-design) says so, and where the design changes
a rule that another document states, it says so. The clients live in a package of their own, which
neither the core nor the dataset package references, so both talk to a store through its interface
alone. Two rules of [the store abstraction](13-dataset.md#11-the-store-abstraction) do
change: a store may now replace an object when a put asks it to, which a dataset never does, and a
listing may lag, by less than the retention window
([the store interface, revised](#4-the-store-interface-revised) explains both). Encrypting what is
stored is the subject of [18-encryption.md](18-encryption.md), and this document only touches it
where the two meet.

## 1. What it must hold

Five requirements, each of which becomes a test or a ratchet once built:

| requirement | what it means, measured |
|---|---|
| the same API | a file opens, scans and is written with the calls it has on a path, given a location instead, though an object takes no append. A dataset takes a store, or a location. Nothing else in the public API changes, whichever provider holds the bytes |
| few round trips | every operation has a count of requests and of dependent steps, written in [the budget](#5-requests-per-operation), and counting tests hold it. Opening a file is one request on a store that reads suffixes, and opening an object of a dataset is one request on any store. Nothing that a response already said is asked again |
| the bandwidth of the link | reads run in parallel on many connections, coalesced into ranges of up to 16 MiB, and large objects go up in parts sent in parallel |
| bounded allocation, one copy | a request allocates a constant amount, never a buffer the size of its body, and this library's own code allocates nothing per batch. A response body reaches the block the scan decodes from with no copy but the one TLS makes |
| secure by default | HTTPS only, credentials from the platform's identity where the platform offers one, no secret in a message, a log or a trace, and a conditional write whose outcome cannot be proven reported as unknown, never as lost |

The constraint the project started with still holds: no third-party dependency.
[What is built and what is taken from .NET](#2-what-is-built-and-what-is-taken-from-net) draws the
line.

## 2. What is built and what is taken from .NET

Everything specific to object storage is written here. What belongs to the platform is taken from
the base class library, which ships with the runtime and is not a dependency in the sense of the
rule, or from `System.IO.Hashing`, a package of the runtime's own repository that the core already
references.

| built in this repository | taken from .NET |
|---|---|
| the transport's policy over HTTP: deadlines and stall detection, retries and their budget, the requests a throttled service is sent at once, and the outcome of a conditional write whose answer was lost | HTTP (`HttpClient` over `SocketsHttpHandler`): connections and their pool, name resolution, proxies, chunked bodies, `100-continue` |
| the S3 dialect: Signature Version 4, ranged and suffix reads, conditional writes, multipart uploads, `DeleteObjects`, `ListObjectsV2`, checksums, and the profiles of R2, OVHcloud and Scaleway | TLS (`SslStream`, under `SocketsHttpHandler`), which runs the operating system's TLS library and validates certificates against the system's trust store |
| the Azure dialect: Shared Key signing, shared access signatures, bearer tokens, `Put Block List`, `Blob Batch`, `List Blobs` | XML (`XmlReader`, `XmlWriter`), for the services' answers and the few documents their requests carry |
| the Cloud Storage dialect: generation preconditions, the JSON API's listings, metadata and batches, the XML API's reads and multipart uploads | JSON parsing (`Utf8JsonReader`), for the token endpoints, the instance metadata and Cloud Storage's JSON API |
| the credential chains of AWS, Azure and Google Cloud, and their refresh | SHA-256, HMAC-SHA-256 and MD5 (`SHA256.HashData`, `HMACSHA256.HashData`, `MD5.HashData`), for the signatures and the `Content-MD5` some requests need |
| | RSA signatures (`RSA`), for the token requests of a Google service account key, and entropy (`RandomNumberGenerator`) |
| | CRC-64/NVME, CRC-32C, CRC-32 and XXH3-128 (`System.IO.Hashing`, whose version 11 takes the parameters of any CRC) |

The line falls at HTTP. What is specific to object storage, the signatures, the operations, the
retries and what they mean for a commit, is this library's, and it is where every requirement of
[what it must hold](#1-what-it-must-hold) is met. Below that, HTTP and TLS are infrastructure the
platform already does well, and a client of this repository would buy none of the requirements, as
[why the platform's client](#61-why-the-platforms-client) shows.

The signatures hash and sign through the platform's static span methods, which allocate nothing, and
the encryption design takes its cipher from the platform the same way
([the cipher, from the platform](18-encryption.md#5-the-cipher-from-the-platform)). MD5 serves only
the `Content-MD5` a service asks for, on a batch delete and on a put to a locked bucket of a cloud
that takes no other checksum, never security. On a host whose operating system forbids MD5, as a
FIPS configuration does, the batch delete to Amazon S3 sends a CRC-32 checksum header instead, as the
AWS SDKs do, and a locked dataset on OVHcloud or Scaleway cannot be written.

## 3. Where it lives

### 3.1 Packages

| package | holds | depends on |
|---|---|---|
| `Vorticity` | the store interface, moved from the dataset package, and the locations a session resolves | as today |
| `Vorticity.ObjectStorage` (new) | `S3ObjectStore` and its profiles, `AzureBlobObjectStore`, `GoogleCloudStorageObjectStore`, their options and credentials, the transport over `HttpClient` | `Vorticity` |
| `Vorticity.Dataset` | the dataset, unchanged but for the store calls listed in [the store interface, revised](#4-the-store-interface-revised) | `Vorticity` |

The store interface moves into the core, in `Vorticity.IO` next to `ISegmentSource`, because a single
file now opens and is written through it, and a file is the core's business. The dataset package is
experimental, so the move breaks no stable contract. `ObjectHead`, `PutOutcome` and the two store
exceptions move with it, `ObjectRange` too but internal, since `ObjectRead` is the one result a
read has in public, and `MemoryObjectStore`, `FileObjectStore` and `CountingObjectStore` stay where
they are.

The new package is not experimental by nature, since none of the services is this project's format,
but it ships at `0.x` until it has run against each of them in production.

### 3.2 Locations

A location is a string that names an object, and a session knows which store serves it:

| location | store |
|---|---|
| `s3://bucket/key` | the S3 store registered for that bucket or one of its prefixes, Amazon's or a cloud's that speaks its API |
| `gs://bucket/key` | the Cloud Storage store registered for that bucket or one of its prefixes |
| `az://container/key` | the Azure store registered for that container, its account given at registration |
| `https://account.blob.core.windows.net/container/key` | the Azure store registered for that account and container |
| anything else | a local path, as today |

A session resolves locations with the stores its options hold, since connections and credentials are
a host's decisions. A store registered there stays the caller's, who may give it to several sessions
and disposes it after them, and a store the session builds for a scheme is the session's. The longest
registered prefix wins, so a bucket can be served by one store and one of its prefixes by another,
with other credentials. A registration's prefix is a name and not a protocol, and a bucket's name is
unique within a provider but not across providers, so a scheme of the caller's choosing keeps two of
them apart: `r2://lake` beside `s3://lake`.

```csharp
StoreCredentials environment = StoreCredentials.FromEnvironment();
await using S3ObjectStore lake = S3ObjectStore.FromEnvironment("lake", environment);
await using S3ObjectStore media = S3ObjectStore.ForR2(accountId, "media", StoreCredentials.AccessKey(r2KeyId, r2Secret));
await using GoogleCloudStorageObjectStore warehouse = GoogleCloudStorageObjectStore.FromEnvironment("warehouse", environment);
await using AzureBlobObjectStore archive = new AzureBlobObjectStore(new AzureBlobStoreOptions
{
    Account = "contosoarchive",
    Container = "archive",
    Credentials = environment,
});

await using VortexSession session = VortexSession.Create(o =>
{
    o.ObjectStores.Add("s3://lake", lake);
    o.ObjectStores.Add("r2://media", media);
    o.ObjectStores.Add("gs://warehouse", warehouse);
    o.ObjectStores.Add("az://archive", archive);
});
```

`VortexSession.Default` holds no store, and a location with a scheme fails there with an exception
that names the scheme and says how to register a store. Resolving locations from the environment
without being asked would open network connections and read credentials on a host that never chose
to, which is the reasoning that keeps the default session single-threaded
([parallelism](09-contracts.md#2-parallelism)). One line opts in:

```csharp
await using VortexSession session = VortexSession.Create(o => o.ObjectStores.UseEnvironment());
```

`UseEnvironment` builds a store on the first use of each bucket or container, keeps it for the
session's life and disposes it with the session. An `s3://` location takes the AWS environment, and
its endpoint from `AWS_ENDPOINT_URL_S3` or `AWS_ENDPOINT_URL` when one is set, the variables the AWS
SDKs read, which is how a host points at R2, OVHcloud or Scaleway, the profile following the
endpoint's host. A `gs://` location takes Google's endpoint or the emulator the environment names,
and an `az://` one Azure's account. Every store it builds shares one set of the environment's
credentials (see [credentials](#12-credentials)), so that each service's keys or tokens are asked
for once, however many buckets the session reads. This choice is one of the
[decisions to take](#17-decisions-to-take).

### 3.3 What a caller writes

Reading and writing a file, as on a path:

```csharp
await using VortexFile file = await session.OpenAsync("s3://lake/readings/2026-10.vortex");
double? mean = await file.Scan<Reading>().Where(r => r.Day >= 900).AverageAsync(r => r.Celsius);

await using (VortexFileWriter writer = session.CreateWriter<Reading>("gs://warehouse/readings/2026-10.vortex"))
{
    await writer.WriteAsync<Reading>(readings);
    WriteReport report = await writer.CompleteAsync();
}
```

A dataset, by its location or by a store:

```csharp
await using VortexDataset dataset = await VortexDataset.OpenAsync(
    "r2://media/tables/readings/", new DatasetOptions { Session = session });

await using VortexDataset same = await VortexDataset.OpenAsync(
    session.ObjectStores.Resolve("r2://media/tables/readings/"), new DatasetOptions { Session = session });
```

`Resolve` returns a view of the bucket's store under the prefix, sharing its connections, and the
dataset's keys (`commit/`, `data/`) are relative to it as they are to a directory today.
A location that names a local directory opens a `FileObjectStore` there, so a program that takes
its location from configuration runs on a laptop and on any of the six services without a line
changing.

## 4. The store interface, revised

The interface is the seam every store implements, the ones this package ships and any other a
caller writes. It changes in five places, each for a request saved, a copy avoided or a correctness
gap closed:

| change | today | revised | what it buys |
|---|---|---|---|
| a read fills the caller's buffer | `GetRangeAsync` returns bytes the store owns, and the dataset's object source copies each segment out of them into an aligned block (`ObjectSegmentSource.cs`, `Aligned`) | `ReadAsync(key, offset, destination, ifToken)` reads into memory the caller gives and returns the bytes read, the object's length and its token | the body lands in the block the scan decodes from, and the per-segment copy disappears |
| the end of an object, without its length | the open asks the length first (`GetLengthAsync`, a head), then reads the tail (`VortexFile.cs`, the open path) | `ReadTailAsync(key, destination)` reads the last bytes and returns the object's length with them | one request instead of two on a store that reads suffixes. Another store answers with a head and a read, as today |
| a condition on the token | a range's token is compared with the first one after the bytes arrived (`ObjectSegmentSource.cs`, `Note`) | the token travels with the read as a condition, and the store answers 412 rather than bytes of another object | an object replaced under a reader fails the next read, and its bytes are never downloaded |
| a put says its condition | `PutIfAbsentAsync` only, and an object is never overwritten | `PutAsync(key, content, length, condition)`, where the condition is absent, matching a token, or none, and `PutOutcome` gains `Changed` for a token that no longer matches | a file written to a location replaces the object as a path's rename does, and a caller that read an object replaces it only if nothing else did in between |
| a listing says what it knows | keys only, so vacuum asks for a head per candidate (`DatasetVacuum.cs`, one awaited after another) | `ListAsync` yields each key with its length, token and creation time, and the lock when the store's listing reports it | vacuum stops asking for heads, except for the locks that S3 and the clouds speaking its API report only in a head |

`GetRangeAsync` and `PutIfAbsentAsync` remain as extension methods over the new members, the first
internal to the dataset, so the dataset's code changes only where it gains something. A store also declares what it can do, so that
a caller chooses the cheaper path without trying the other:

| capability | S3 | R2, OVHcloud, Scaleway, another S3 service | Azure | Cloud Storage | file | memory |
|---|---|---|---|---|---|---|
| reads a suffix without the length | yes | to check | no | to check | yes | yes |
| lists in ordinal order, after a key | yes | yes, OVHcloud's to check | yes | yes | yes | yes |
| replaces on a token's match | yes | yes | yes | yes | no | yes |
| keeps locks, as S3 Object Lock does | yes | to check, never on R2 or in an OVHcloud Local Zone | yes | yes | no | yes |
| creates a key only while it is free, in one step | yes | yes on R2, OVHcloud and Scaleway, to check on another service | yes | yes | yes | yes |

A listing says on each entry whether it gave the object's lock: Azure's, Cloud Storage's and the
memory store's do, the file store's does by saying none, and S3's never does, so a head tells there.

A capability marked to check is taken as absent until the contract tests prove it against the
service, so a store whose suffix reads are not proven opens a file with a head and a read, as today.
The file store cannot replace on a token's match, since no file system compares and renames in one
step across processes. It never needs to, because a single file on a local path is written by the
core's own path code, and a dataset never replaces an object.

Objects stay immutable for a dataset: its keys end with a fresh uid or name a version, and it puts
each of them once. The interface's rule that an object is never overwritten becomes a rule on the
caller, since only a put whose condition says so replaces an object, and the token still changes
whenever the bytes under a key do. A file opened from a location keeps the token of its first read,
so an object replaced under an open file fails that file's next read rather than mixing two objects.

The interface also asks less of a listing. [The latest version](13-dataset.md#83-the-latest-version-in-one-request)
takes the first key of `commit/` for the latest version, which needs a listing that shows every
commit at once. A listing that lags costs a dataset freshness, never correctness. A handle that
misses the newest commits reads an older version, and its commit then finds the key taken and
rebases on the version that won, since commits are conditional creations. Vacuum, which keeps every
unreferenced object younger than its window and always the latest version it sees, loses nothing to
a lag shorter than that window. The revised rule asks a store for a listing that lags by less than
the retention window, and still for reads and heads that see every write that completed, and for
atomic conditional creations.

S3's directory buckets list in no particular order and refuse `start-after`, both checked in the
`ListObjectsV2` reference. The first key of `commit/` means nothing in such a listing, so a
directory bucket could host single files and never a dataset, and the store refuses one when it is
built ([directory buckets](#73-directory-buckets)).

The token stays an opaque string, compared ordinally: an ETag on S3 and Azure, the generation, a
number the service assigns at each creation, on Cloud Storage. To keep a read free of allocation, the
store compares the token a response carries with the one it was given byte by byte, and hands back
the caller's instance when they match. A string is created once per object, when its first read
learns the token.

## 5. Requests per operation

On an object store, a request costs 10 to 100 ms whatever its size below a few megabytes, so the
budget is counted in dependent round trips first and in requests second, as
[the read-path budget](13-dataset.md#9-the-read-path-budget) of the dataset does.

| operation | asked of the store by the code today | S3, and the clouds that speak it | Azure | Cloud Storage |
|---|---|---|---|---|
| open a single file of unknown length | 2 dependent: a head, then the tail | 1: a suffix range, whose answer says the length. 2 on a cloud whose suffix reads are not yet proven | 2: the properties, then the tail. 1 when the caller gives the length | 1 once its suffix reads are proven, 2 until then |
| open an object of a dataset | 2 dependent: a head, then the tail, although the leaf entry records the length (`ObjectCache.cs`, the open) | 1 | 1 | 1 |
| a batch of a scan | 1 dependent step, one request per coalesced range | 1, the same | 1, the same | 1, the same |
| commit, uncontended | 1 to 3 dependent | the same | the same | the same |
| the latest version | 1 listing page | the same | the same | the same |
| vacuum, per commit and per unreferenced data object | 1 head each, awaited one after another | 0, the listing says length, token and date. Under Object Lock, 1 head per object about to be deleted | 0, locks included | 0, retention and holds included |
| vacuum, the deletes | one call per batch | 1 request per 1 000 keys | 1 request per 256 keys | 1 request per 100 keys, billed as 100 |
| create an object up to the part threshold | 1 | 1 | 1 | 1 |
| create a larger object | 1 | 3 dependent: start the upload, the parts in parallel, complete. On R2 a conditional put goes whole, in 1 | 2 dependent: the blocks in parallel, the block list | 3 dependent, and a conditional put goes whole, in 1 (see [Cloud Storage](#9-google-cloud-storage)) |

The first two rows are the savings this design is worth even before a byte goes to a service. The
open of a dataset's object passes the length its entry records, which any store benefits from, and
the listing with heads removes vacuum's serial heads. On a dataset of a thousand commits on S3, at
20 ms a head, that is twenty seconds of vacuum spent on heads, and none with the listing.

A question a dataset answers from its synopses reads each object's tables with no open of it, in one
request an object, and the objects side by side. With a `SynopsisCache` on the session, the second time
reads them from the local disk and asks the store nothing, and a question whose window cuts slices asks
it only for the chunks of those slices. `WarmAsync` reads a window's tables into the cache ahead of the
first question. The requests a store refuses past its limits (thousands a second per prefix on S3, fewer
on smaller services) are what this saves at a page's scale: a cold month is one request an object, a warm
one none.

## 6. The HTTP layer

### 6.1 Why the platform's client

Requests go through `HttpClient` over `SocketsHttpHandler`. A client of this repository, an HTTP/1.1
client over `SslStream` restricted to what object stores use, was weighed and set aside, because none
of its gains survives a look at what the platform's client does:

| the gain it would have brought | what `SocketsHttpHandler` already does |
|---|---|
| a body read without a copy | it reads a response body from the TLS stream straight into the caller's memory whenever its own buffer is empty (`HttpConnection.ReadAsync` in dotnet/runtime), so only the bytes that arrived with the headers, a few kilobytes at most, are copied. The copy this design removes is the store interface's (see [the store interface, revised](#4-the-store-interface-revised)), not HTTP's |
| no allocation per request | it allocates per request the request and response messages, their headers, a few strings and the state of its calls, a few kilobytes in all, a figure to measure. That is a constant per request, never proportional to the body. In ranges of a mebibyte, it is under half a percent of the bytes moved, objects that die young, which the young generation collects for almost nothing |
| control of connections and deadlines | `MaxConnectionsPerServer`, `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime` and `Expect100ContinueTimeout` cover the connections, and a cancellation token covers every deadline. Retries and the outcome of a lost conditional write are logic above HTTP, written here whichever client sends the bytes |

A client of our own would cost what the platform's has already paid for. A connection reused after a
body was only partly read hands its leftover bytes to the next request, which is silent corruption.
Chunked bodies, `Connection: close`, `100-continue` and proxies that ask for authentication each have
their edge cases. The platform's HTTP metrics and traces, which OpenTelemetry collects without an
adapter, would be lost. And each fix the runtime ships for its client would have to be found and made
again here. The platform's client also makes the store testable without a network: a test gives it a
message handler that answers with delays, resets, throttling and refusals.

The allocations that matter are this library's own. The dataset's object source allocates on every
batch today: a list of slots, an array of tasks and a task per read, an array of ranges and a sorting
delegate (`ObjectSegmentSource.cs`, `ReadManyAsync`). They go, replaced by pooled arrays and value
tasks, so that what a scan over a store allocates per batch is the platform's constant per request and
nothing else.

### 6.2 How it is configured

| setting | value |
|---|---|
| handler | one `SocketsHttpHandler` per store, shared by the store's views under its prefixes |
| connections | `MaxConnectionsPerServer`, 64 by default. A request waits for a free connection rather than opening one past the cap. S3's guidance is to spread requests over many connections, which the pool's connections do |
| lifetime | `PooledConnectionIdleTimeout` of 20 s and `PooledConnectionLifetime` of 5 minutes, so the pool follows the service's name resolution, and a connection takes the addresses the platform resolves |
| TLS | TLS 1.2 or 1.3, the server's certificate checked against the system's trust store. Plain HTTP only for an endpoint marked as an emulator on loopback |
| turned off | cookies, automatic decompression and redirects, since S3 answers a request sent to the wrong region with a redirect that names the right one, and the store signs the request again for it |
| proxies | the environment's (`HTTPS_PROXY`, `NO_PROXY`), as the platform reads them, or one given in the options |
| large puts | `Expect: 100-continue` on a put larger than a part, so a request refused on its headers, a denied one or a bad signature, costs no body |
| draining | none (`MaxResponseDrainSize` of 0): a body abandoned before its end, a stalled read, closes its connection instead of being read to the end for nothing |
| tracing | the platform's metrics and traces stay on. The trace header it adds is outside every service's signature, and a store can turn its propagation off |

### 6.3 A request

The store builds each request: its URI, kept for an object across its reads, its headers added
without validation, and its signature or token added last (see
[S3](#7-s3-and-the-clouds-that-speak-its-api), [Azure](#8-azure-blob-storage) and
[Cloud Storage](#9-google-cloud-storage)). Every body the store sends, a small object or a part, is
whole in memory before it is sent, so its checksum is known in time for a header and no trailer is
needed. The body is an `HttpContent` of this library that writes that `ReadOnlySequence` straight to
the request stream with its length declared, so nothing is buffered twice.

The request is sent with `HttpCompletionOption.ResponseHeadersRead`. The headers are read from the
response's non-validated view, without parsing, and the body is read into the destination block until
its announced length.

### 6.4 Timeouts, retries, and writes whose outcome is unknown

Every attempt has a deadline for the first byte of its answer, counted from the end of its body when
it sends one, and a body, sent or received, is abandoned when it goes the stall deadline without a
byte. A stalled connection is abandoned rather than awaited, and a slow one, which makes progress,
slows a put rather than failing it: the body is written in slices of 256 KiB, each of which pushes the
deadline back. An attempt abandoned before its body ends is cancelled, which closes its connection
rather than draining it.

| outcome | what the client does |
|---|---|
| 500, 502, 503, 504, 429, a connection refused or reset, a deadline missed | retried with exponential backoff and full jitter, from 50 ms to 20 s, honouring `Retry-After`, while the store's retry budget allows |
| 503 `SlowDown` (S3), 503 `ServerBusy` (Azure), 429 (R2, Cloud Storage) | the same, and the store halves the requests it sends at once until the service has been calm for 10 seconds, once per episode: the requests in flight when it halved do not halve it again |
| 400 `RequestTimeout` (S3, a body too slow) | retried once |
| a request dated more than five minutes off the service's `Date`, as the refused request itself says (S3 `RequestTimeTooSkewed`, an Azure date refused) | the offset from the response's `Date` is applied to the signing clock, and the request sent again, dated anew. A request refused for another reason is not, even on a host whose clock is off |
| an expired token | the credentials refreshed once, and the request retried once |
| 404, 412, 416, 401, 403 and the other 4xx | answers, not failures: they are mapped as [errors](#13-errors) says |

The retry budget is a token bucket per store: a retry spends a token, a success returns a fraction
of one, and an empty bucket fails fast. A store that is down then costs its callers one attempt
each, rather than a storm of retries that keeps it down.

A conditional write needs more care, because the dataset gives `Exists` a meaning: another writer
won the version, and the commit is rebuilt on top of that writer's version
(`DatasetCommitter.cs`, the conditional creation). Suppose a put whose body was sent loses its
connection before the answer, and the retry is told the key exists. That existence may be our own
first attempt. Answered as `Exists`, the commit would be rebuilt on its own version and applied
twice. So a conditional put is never retried blindly:

1. Every conditional put carries a random put id in the object's metadata: `x-amz-meta-vx-put` on S3
   and the clouds that speak it, `x-ms-meta-vxput` on Azure, `x-goog-meta-vxput` on Cloud Storage.
2. When an attempt fails after its body may have reached the service, the store asks for the
   object's head before anything else. A completion of parts is such an attempt, and so is its
   retry when the service answers that the upload no longer exists. An attempt the service
   throttled, or whose body was cut short before its end, was never applied, since a service waits
   for the whole of a body's declared length: it goes again without the head.
3. If the object carries this put's id, the answer is `Created`. If it carries another id, the answer
   is `Exists`. If there is no object, the put is sent again. If the head fails too, the store throws
   an `ObjectStoreException` that says the outcome is unknown, and the caller decides.
4. Once an attempt has lost its answer, a later refusal on the condition is checked the same way. A
   slow service may apply the lost attempt after the head that found nothing, and the attempt sent
   again then finds the key taken by this put itself: the head finds its id, and the answer is
   `Created`.

That costs a head, two at most, only in the rare case where the network failed at the worst moment.
The protocol is written once for the three stores (`Http/Upload.cs`), and the S3 emulator can apply
a write after the head that follows its lost answer, which is the case the tests hold it to.

### 6.5 Hedged reads

A read that has not seen its first byte when most reads have finished is probably on a slow path,
and a second request is likely to take another one, as S3's performance guidance says of aggressive
timeouts and retries. Hedging was built, a second request sent past a percentile of the first-byte
latency, and removed: each hedge is a request that costs money, the request ratchets had to count
hedges apart, and nothing measured what a hedge gains against a service. It comes back with that
measurement.

## 7. S3, and the clouds that speak its API

### 7.1 Requests and signing

Requests are addressed to `bucket.s3.region.amazonaws.com`, to the endpoint of a cloud that speaks
the API (see [Cloudflare R2, OVHcloud and Scaleway](#74-cloudflare-r2-ovhcloud-and-scaleway)), or by
path to one given for an emulator or for Ceph. A store's region comes from its options or from the environment,
and otherwise, on Amazon S3, from the `x-amz-bucket-region` header of one request at creation.

Every request is signed with Signature Version 4. The signing key
is derived once a day for the region and the service, four HMACs, and each request costs one SHA-256
of its canonical request, a few hundred bytes, and one HMAC. A body of data is sent unsigned
(`UNSIGNED-PAYLOAD`) over TLS, its integrity carried by a checksum instead, so no byte of data is
hashed with SHA-256. The small XML bodies the client builds itself are signed.

### 7.2 Operations

| store operation | S3 request |
|---|---|
| `ReadAsync` | `GET` with `Range: bytes=a-b`, and `If-Match` when the token is known. S3 serves one range per request, so a coalesced range is one request |
| `ReadTailAsync` | `GET` with `Range: bytes=-n`, whose `Content-Range` says the object's length in the same answer, on a service whose suffix reads are proven. Elsewhere a `HEAD`, then the range |
| `HeadAsync` | `HEAD`, which also says the Object Lock retention date and the legal hold |
| `PutAsync`, up to the part threshold | `PUT` with `If-None-Match: *` when the key must be absent, `If-Match` when the token must match, and the content's checksum in a header (see [writing](#11-writing)) |
| `PutAsync`, above it | `CreateMultipartUpload` with the checksum algorithm, `UploadPart` in parallel, then `CompleteMultipartUpload` with the condition and the whole object's checksum |
| `DeleteAsync` | `DeleteObjects`, 1 000 keys per request, in quiet mode, with the `Content-MD5` its reference asks of general purpose buckets, or a CRC-32 checksum header on a host that forbids MD5 |
| `ListAsync` | `ListObjectsV2` with `prefix`, `start-after`, `max-keys` of 1 000 and `encoding-type=url`. Each entry carries the key, the size, the ETag and the date |

Some answers have a meaning of their own. A conditional `PUT` answered 409 lost a race with a delete
and may be sent again, while a `CompleteMultipartUpload` answered 409 must start a new upload, which
the store does when it can send the content again (see [writing](#11-writing)). An
upload in progress does not hold its key: another writer may create it meanwhile, and the completion
is then refused with 412. Both cases are written in the conditional writes guide.

S3 also deletes conditionally, on an ETag, and the store does not use it. Vacuum deletes keys that
no one creates again: a data object's key ends with a fresh uid, and a superseded commit's version
is never written twice.

### 7.3 Directory buckets

S3 Express One Zone's directory buckets answer in single-digit milliseconds, sign with a session
token that `CreateSession` issues, and accept appends. They list in no order and without
`start-after`, so they could host single files and never datasets. Their sessions and appends were
built and removed, for want of a user whose files need that latency. The store refuses a bucket
whose name ends in `--x-s3` when it is built, with a message that says why.

### 7.4 Cloudflare R2, OVHcloud and Scaleway

Three clouds speak S3's API closely enough to share the store, each through a profile that says
where it differs. A factory picks the profile, or the store recognises it from the endpoint's host,
so the endpoint variable of [locations](#32-locations) is enough to point `UseEnvironment` at any of
them.

```csharp
await using S3ObjectStore media = S3ObjectStore.ForR2(accountId, "media", StoreCredentials.AccessKey(r2KeyId, r2Secret));
await using S3ObjectStore archive = S3ObjectStore.ForOvhcloud("gra", "archive", StoreCredentials.AccessKey(ovhKeyId, ovhSecret));
await using S3ObjectStore exports = S3ObjectStore.ForScaleway("fr-par", "exports", StoreCredentials.AccessKey(scwKeyId, scwSecret));
```

| | Amazon S3 | Cloudflare R2 | OVHcloud | Scaleway |
|---|---|---|---|---|
| endpoint | `bucket.s3.region.amazonaws.com` | `account.r2.cloudflarestorage.com`, signed for the region `auto` | `bucket.s3.region.io.cloud.ovh.net` | `bucket.s3.region.scw.cloud` |
| create only if absent, in one request | yes | yes | yes | yes |
| create only if absent, in parts | yes | not documented, taken as no | yes | yes |
| replace only on a token's match | yes | yes | yes | yes |
| listing ordered, after a key | yes | yes | `ListObjectsV2` listed, its order and `start-after` to check | yes |
| listing strongly consistent | yes | yes | not documented | not documented, reads after writes are |
| suffix reads | yes | to check | to check | to check |
| parts | 10 000 at most, 5 MiB to 5 GiB | 10 000 at most, 5 MiB to 5 GiB, all of one size but the last | 10 000 at most, 5 MiB to 5 GiB | 1 000 at most, 5 MB to 5 GB |
| Object Lock in a head | yes | no Object Lock header | yes in regions, not in Local Zones, to check | yes, in compliance mode, to check |
| checksum sent with a put | CRC-64/NVME | none by default, `Content-MD5` as an option | none by default, `Content-MD5` as an option | none by default, `Content-MD5` as an option |
| limits to live with | 3 500 writes and 5 500 reads a second per prefix | one write a second to the same key, answered 429 | 300 writes and 900 reads a second per bucket, soft limits, and 1 Gb/s per connection | none documented beyond the parts |

Each of them creates a key only if it is absent in one request, which is what a commit needs: a
commit object is small, far below the 5 GiB that one request takes on all four. On R2, which takes
no condition on parts, a conditional put goes whole whatever its size, held in memory up to
`MaxHeldBytes` when it is read from a stream, or spooled on disk, and one past 5 GiB is refused. A
data object's key ends with a fresh uid, so its put needs no condition, and a large one goes up in
parts on every profile, R2 included.

OVHcloud documents neither the consistency of its listings nor suffix reads, and lists
`ListObjectsV2` without saying that it honours `start-after` and the order. Scaleway documents that
reads follow writes, and says nothing of its listings or of suffix reads. A listing that lags costs
a dataset freshness only, as [the store interface, revised](#4-the-store-interface-revised) shows.
The order and `start-after` are another matter, since without them the first key of `commit/` is not
the latest version, so a dataset is allowed on OVHcloud once the contract tests have checked both.

Locks are taken as kept on Amazon S3 alone until the contract tests prove them elsewhere, so a dataset
created with `LockedStore` refuses an OVHcloud or Scaleway store until `S3StoreOptions.KeepsLocks`
says that the bucket keeps them. The option stays false on R2, which accepts no Object Lock header,
and on OVHcloud's Local Zones, which keep no retention or legal hold. R2 answers 429 to a
second write to the same key within a second. A dataset writes each key once, so only a retry meets
it, and a retry waits as [the retries](#64-timeouts-retries-and-writes-whose-outcome-is-unknown)
say. OVHcloud's 1 Gb/s per connection means that a 10 Gb/s link needs ten connections in flight at
least, which the ranges a batch issues together provide when they are many
([the reads in flight](#101-the-reads-in-flight)).

The three clouds share no checksum but `Content-MD5`, and R2 documents no other. The store sends none
by default, since TLS already protects the bytes on their way, and MD5 would cost a pass over every
byte in a hash no processor accelerates. `S3StoreOptions.Checksum` turns `Content-MD5` on, and the
contract tests establish what else each cloud verifies. A bucket under Object Lock needs it, since
S3's Object Lock refuses a put that carries no checksum: a store for a locked dataset on OVHcloud or
Scaleway is given `PutChecksum.ContentMd5`, and the refusal of a put without it names the option. The
store never changes what it sends on the text of a refusal, which a provider may reword.

### 7.5 Other stores that speak the API

A service that speaks S3's API without a profile here, Ceph for instance, is taken to list in order
and to do nothing that a test has not proven. Its options say the rest rather than a detection at run
time: whether it reads suffixes, keeps locks, creates a key only while it is free or wants
`Content-MD5`, and whether it addresses buckets by path. A dataset refuses to write to a store that
does not declare that it creates conditionally (`ObjectStoreCapabilities.CreatesConditionally`), as
[the store abstraction](13-dataset.md#11-the-store-abstraction) requires, and reads from it all the
same. `S3StoreOptions.CreatesConditionally` declares it once the contract tests pass against the
service. The two that matter are a second creation of one key answered `Exists`, and sixteen
creations of one key at once of which only one is `Created`. A service that checked whether the key
is there and then wrote would pass the first and fail the second only when the puts meet, so the
second is run many times over before the option is set. The contract tests run against such a
service as against the others, once the environment names one of its buckets.

## 8. Azure Blob Storage

### 8.1 Requests and signing

Requests go to `account.blob.core.windows.net/container/blob`, or to an endpoint given for an emulator
or a sovereign cloud, with an `x-ms-version` the store pins, 2025-01-05, which covers `startFrom` in
listings (2023-05-03) and the immutability policy in listings and heads (2020-06-12).

A request is authorized by a bearer token from Microsoft Entra ID, which Microsoft recommends, by a
shared access signature, or by Shared Key, an HMAC-SHA-256 of a canonical string with the account
key. Microsoft recommends disallowing Shared Key on the account, and the store supports it for the
emulator and for accounts that still use it.

### 8.2 Operations

| store operation | Azure request |
|---|---|
| `ReadAsync` | `Get Blob` with `x-ms-range: bytes=a-b`, and `If-Match` when the token is known |
| `ReadTailAsync` | Azure accepts `bytes=a-` and `bytes=a-b` only, never a suffix, so `Get Blob Properties` then `Get Blob`, unless the caller gives the length |
| `HeadAsync` | `Get Blob Properties`, which also says `x-ms-immutability-policy-until-date` and `x-ms-legal-hold` |
| `PutAsync`, up to the part threshold | `Put Blob` with `If-None-Match: *` or `If-Match`, and `x-ms-content-crc64`, or `Content-MD5` or nothing as the store's options say |
| `PutAsync`, above it | `Put Block` in parallel, then `Put Block List` with the condition |
| `DeleteAsync` | `Blob Batch` scoped to the container, up to 256 `Delete Blob` per request and 4 MB of body, each one authorized in the batch |
| `ListAsync` | `List Blobs` with `prefix`, `startFrom` (inclusive, so a key equal to `startAfter` is skipped), `maxresults` of 5 000, and `include=immutabilitypolicy,legalhold` on a locked store |

Azure drops uncommitted blocks a week after the last `Put Block`, so an abandoned upload needs no
clean-up. On an account with a hierarchical namespace, a recursive listing sorts `/` before every
other character. The keys under `commit/` and `data/` contain no further `/`, so the listings whose
order a dataset relies on keep it.

### 8.3 Checksums

Azure's CRC-64 is CRC-64/NVME, the polynomial S3 uses for `CRC64NVME`, so one implementation serves
both services. A `Put Blob` or `Put Block` carries `x-ms-content-crc64`, which the service checks.
A read could ask for a structured body, in which the service cuts the bytes into segments of 4 MiB,
each followed by its CRC-64. Its reader was built and removed until its layout is checked against
the service, so a read from Azure relies on TLS and the format's checks, as on S3.

## 9. Google Cloud Storage

### 9.1 Why its own APIs

Cloud Storage also answers S3's API, through an interoperability mode with HMAC keys. The store
speaks Cloud Storage's own APIs instead, because what a dataset relies on lies there:

- A creation conditioned on the object's generation, a generation of 0 meaning that no object may
  have the name.
- A listing that says each object's retention and holds, so vacuum asks no head.
- Deletes in batches.
- The platform's identities, a service account or a workload's, rather than HMAC keys.

### 9.2 Requests and authorization

Data goes through the XML API, `storage.googleapis.com/bucket/object`, and metadata, listings and
deletes through the JSON API, both with an OAuth 2.0 token from Google's application default
credentials (see [credentials](#12-credentials)). An object's token is its generation, and a read
pinned to it with `x-goog-if-generation-match` is refused with 412 if the object was replaced.

### 9.3 Operations

| store operation | Cloud Storage request |
|---|---|
| `ReadAsync` | XML `GET` with `Range: bytes=a-b`, and `x-goog-if-generation-match` once the generation is known |
| `ReadTailAsync` | XML `GET` with `Range: bytes=-n` once the contract tests prove it, and until then the object's metadata, then the range |
| `HeadAsync` | the JSON API's metadata, limited by `fields` to the size, the generation, the creation time, the retention and the holds |
| `PutAsync`, up to the part threshold | XML `PUT` with `x-goog-if-generation-match: 0` when the key must be absent, or the generation when it must match, and `x-goog-hash: crc32c=…`, which the service verifies |
| `PutAsync`, above it | an XML multipart upload, its parts in parallel. The service takes no precondition on it, so a put that must be conditional goes whole, as one XML `PUT` with its precondition, which Cloud Storage takes up to its 5 TiB limit on an object |
| `DeleteAsync` | JSON batches of up to 100 deletes and under 10 MiB |
| `ListAsync` | the JSON API's `objects.list` with `prefix`, `startOffset` (inclusive, so a key equal to `startAfter` is skipped), `maxResults` of 1 000, and `fields` limited to the name, size, generation, creation time, retention and holds |

### 9.4 Locks, deletes and checksums

The listing is ordered by name and strongly consistent, and it carries `retentionExpirationTime`,
the earliest date an object may be deleted under its own retention and its bucket's, and the
temporary and event-based holds. An object's `RetainUntil` is that date and its `LegalHold` either
hold, so vacuum asks no head on Cloud Storage, locked or not.

Each call inside a batch is billed as a request, so a batch saves round trips and not money, and its
calls run in any order and not atomically, which deletes do not need. A deleted object stays in the
bucket, billed, for the bucket's soft-delete period, 7 days by default: vacuum frees storage a week
later there than on the other services, unless the bucket's period is set to 0.

The service records a CRC-32C for every object, multipart and composite ones included, which have no
MD5. The store sends the CRC-32C of what it puts, computed by `System.IO.Hashing`. Reads return the
whole object's hashes only, so a ranged read relies on TLS and the format's checks, as on S3.

## 10. Reading at the speed of the link

### 10.1 The reads in flight

The session's bound on reads in flight counts batch reads, 128 by default, on every store: a batch
takes one slot, and the source then issues every coalesced range of the batch at once
(`SessionReader.cs`, `ReadManyAsync`, and `ObjectSegmentSource.cs`, the reads issued together). Over
HTTP/1.1 a request in flight is a connection, and the store sends at most `MaxConnections` requests
at once, so the memory the reads hold and the connections they open are both bounded.

What a link needs in flight is its bandwidth times its latency. At 10 Gb/s and 30 ms that is about
37 MB, three ranges of 16 MiB or 36 of 1 MiB. A window in bytes, sized from the throughput and the
first-byte latency the store measured, was built and removed: as it sized itself, it fell to one
request in flight once ranges passed a few megabytes, it bounded again what the connections and the
session already bound, and nothing measured it against a service. It comes back with a measurement
of a scan against the services, if the session's count falls short of a link. What a scan keeps in
flight is sized in bytes instead, by a fixed target: a pass reads ahead as many splits as hold
16 MiB, between sixteen and sixty-four, and so does a plain scan of one lane, both reserved in the
memory budget ([admission](16-queries.md)). Sixteen in flight, the session's bound until then, left a
store 5 ms away to bound a scan of rows by its latency.

### 10.2 Bytes land once

Today a range arrives in a buffer the store owns, and each segment is then copied into an aligned
block of its own (`ObjectSegmentSource.cs`, `Aligned`). Revised, the dataset's source rents one
64-byte aligned block from the engine pool for each coalesced range, the store reads the body into
it, and each segment is a slice of that block, its owner shared by a reference count as the session
cache's results already are. Coalesced ranges start on a 64-byte boundary, so each segment keeps its
alignment ([I/O](03-architecture.md#35-io)). One copy remains, from the buffer `SslStream` decrypts
into to the block, which any client over `SslStream` makes.

### 10.3 The first batch

A scan's read-ahead runs on every lane, so a single object's first batch at degree 14 issues about 14
requests, against 2 at degree 1, each of which is latency on an object store. Starting a scan over
the network on two lanes, widened once the consumer asks for the next batch, was built with the
window and removed with it. Both are the scan's, and come back measured against a service.

### 10.4 Integrity of what is read

S3 returns a checksum for a whole object, or for a range aligned on one of its parts, never for an
arbitrary range, and neither do R2, OVHcloud, Scaleway or Cloud Storage. A read from them relies on
TLS for the transport and on the format's own checks for the rest: the dataset's pages and headers
carry XXH3, the index regions carry checksums, and a file's structures are validated as they are
parsed. Azure's structured body would verify a read segment by segment, and waits for its layout to
be checked against the service ([Azure's checksums](#83-checksums)).

### 10.5 The synopsis cache on the local disk

A session may keep on the local disk what questions read of a dataset's objects without their data
(`VortexSessionOptions.SynopsisCache`): their synopsis tables, their slices' chunk directories, their
index directories and their variant columns' path directories, and the fence pages and run segments a
lookup of an identifier reads; and the pages of the dataset's tree, by the hash of their bytes ([the
pages a handle keeps](13-dataset.md#3-objects-and-keys)), unless the dataset is sealed. Only a region
of an index enters it, never the data nor the footer: one
an object's table of contents declares, with a checksum, or a page or a segment of a run its reader
names with the checksum the directory or the parent page gave. An object never changes, so an entry is
never stale: its key is the object's uid, the region's place and length and its checksum, all known
before the region is read. Warm, a lookup asks the store for the chunks of the blocks it found alone: a
count per service and per 12 hours of every row but one request's, over 3 objects of 180 M rows, asked
for 11 regions in 23 ms, a fence page and two segments of each object's postings beside the chunks of
the block that holds the id, and asks for those 2 chunks in 12.7 ms. An object the dataset opens to
read its rows reads the same regions through the cache: the rows of one request id over the same
objects take 2 requests in one round trip warm, against 7 in 5, 14.6 ms against 37.4.

An entry is a file named by that key, in directories sharded by the uid, written under a temporary
name and then renamed, so a reader never sees half a file. It is written behind the question that read
its region: the buffer the region was read into is held until one of two writers of the cache's own has
written it, and a read of the region meanwhile is served from that buffer. A file costs about 1.7 ms to
create, close and rename on Windows, which the question paid for each region it read: an unaligned
window over 3 objects of 180 M rows, which read 104 MB of tables, took 276 ms cold against 159 written
behind, and a count per service over 48 hourly objects 50 ms against 38 at one lane, 44 against 27 at
sixteen. Past 256 MiB waiting, a region is written before its reader goes on, and the cache's dispose
writes what waits. A sealed object's frames are written at once. The cache walks its directory when it opens,
and the entries an earlier cache left are its own. One cache owns a directory, through a lock of the
operating system. On Windows, an entry's file is opened at its first read and mapped, and kept so while
the cache keeps up to 4 096 files open: a region read from the disk is a view of the mapping, with no
read of the system and no copy, and its batches decode from it. A read took a fifth of a warm question
over 48 hourly objects' tables, 5.8 ms a question against 4.6 mapped. A file the cache maps is one no
other writer changes while it does, as the system refuses to write a mapped file. Elsewhere a region is
read, a read of the system's cache costing less, and a file cut short under a mapping faulting its
reader where a read finds it torn. Every read from the disk
checks the checksum, and a region that fails it is dropped, its mapping let go, and read again from the
store; a mapping checks it once, its bytes holding it for every next read of the same mapping, which
no writer changes: a region hashed at each read, then again by the decode of its rows, took a sixth
of a question at sixteen lanes, whose reads of the disk's cache contend for the memory's bandwidth
(11 ms a question against 5.7 once). Questions that miss the same region at once wait for one read.

A sealed object leaves no plaintext on the disk. The cache keeps the bytes the store holds: the
object's trailer, which names its data key wrapped and its epochs, and the frames that hold a region its
table of contents declares, each a file named by the object's uid and the frame's place. A read with no
open of the object opens it from the trailer kept, with no request, and reads from the disk every group
of frames the cache keeps whole. The reader decrypts them as it decrypts the store's: a frame whose tag
fails, or a region whose plaintext fails its checksum, is dropped with its group and read again from the
store, and a trailer that does not open the object is dropped and read again too. No key reaches the disk:
the session unwraps the data key as it does for a read from the store, from its keyring or from the keys
it holds in memory. A frame of 64 KiB is the unit kept, so a small region costs the disk its frame. On six
hours of sealed requests, a count per service from the synopses takes 12 requests in two round trips cold
and none warm, where every question read them again before. Questions that miss the same frames of a
sealed object at once read them each.

Past its capacity it drops entries as S3-FIFO does, counting bytes. A new region waits in a small
queue of a tenth of the capacity, and only one read again there moves on to the main queue, which
gives an entry another pass for each read it had, up to three. The names of the regions the small
queue dropped are kept, and one asked again soon goes straight to the main queue. A sweep of thirteen
months thus passes through without pushing out the tables of the last days. The policy was chosen by
replaying the regions 2 000 questions asked through SIEVE, S3-FIFO and Window TinyLFU: S3-FIFO served
the most bytes at every capacity up to half of what the regions weigh, and as many at half
(`vortex-queries --micro cachetrace`, [bench/README.md](../../bench/README.md)).

What it saves the store is what keeps a deployment under the services' limits, 5 500 reads a second
per prefix on S3 and 900 per bucket on OVHcloud. On the same questions, a question sends 9.7 reads to
the store without the cache and 5.7 with every region held, the rest being data, which the cache never
holds. `WarmAsync` reads a window's regions into the cache before the first question, a sealed object's
frames and trailer as well. The cache reports
on the library's meter, under `vortex.synopsis_cache.*`: hits, misses, bytes from the disk and from the
store, evictions, refused admissions, corrupt regions and bytes held.

### 10.6 Several processes on one machine

A directory has one cache, and a cache is one process's: the queues that choose what leaves, the
names of what the small queue dropped and the bytes held live in its memory, rebuilt by a walk of the
directory when it opens. Two processes on one directory would each drop the files the other reads and
count the bytes twice, so the second is refused (`InvalidOperationException`).

Processes of one machine share a root instead (`SynopsisCache.OpenShared`). Each takes the first
directory under the root no other cache holds, `p0`, `p1` and on up to 64, through the same lock of the
system, and owns it as a cache of its own does: it writes there what the store sent it, and drops
there what its capacity no longer holds. On a miss it looks for the entry in the other directories
before it asks the store, by its file's name and length: a region read whole and checked against its
checksum, a sealed object's frames and trailer and a tree's pages handed to their readers, which check
them as they check the local cache's. What it reads there it does not keep again, so a region the
processes read lies once on the disk, unless two of them read it from the store before either kept it;
a sealed frame another directory holds counts as held, and is not kept again either. The root's own
lock is held shared by every cache of the root, which a cache of its own, holding it alone, refuses and
is refused by: a root is shared or owned, never both.

Nothing else passes between the caches. A cache lists the other directories at most once a second,
open or not: a directory whose cache closed is read until the next cache of the root takes it, with
what it holds. An entry never changes and is checked at each read, so a file another cache drops under
a read is read as it was, or missed and read again from the store. A cache opens another's file to read
it whole, letting the system delete it meanwhile, and never maps it, so it never holds another's
deletion back. Each cache's queues count its own reads only: an entry the others read often and
its owner never leaves its owner's small queue, and the next of the others to miss it reads it from the
store and keeps it in its own directory, where its reads count. `vortex.synopsis_cache.peer_hits`
counts the hits a cache served from another's directory.

A look in the others' directories costs a failed open of a file name per directory on each miss, and a
hit costs an open, a read and a checksum, where the owner reads a view of a mapping it keeps. A process
whose working set lies in another's directory pays that at each read, and the disk holds the set once.
The throughput runs of the query bench take `--shared-cache ROOT` (`vortex-queries --micro paths --qps`)
to measure it with two processes on one root.

Three ways to share were weighed, and the third built:

| | how | what it costs | what it leaves |
|---|---|---|---|
| a companion process | one process owns the directory and answers the others over a local socket: which of a batch of regions it holds, each lent while the asker maps it, and the regions they read from the store, which it adopts by a rename | a round trip of the socket per batch, tens of microseconds, against a warm question of a few milliseconds | a process to run and watch beside the application, and the cache gone while it restarts |
| an index on the disk | the queues and the bytes held in a mapped file every process updates under a lock of the system | every hit takes the lock to move its entry along the queues | the lock's contention at sixteen lanes in each process, and a file left half-updated by a process that dies holding it |
| peers read, owners write | each process writes and evicts its own directory, and on a miss looks for the region in its peers' directories before the store: an entry never changes and is checked by its checksum, so a file a peer drops under a read is a miss, read again from the store | a look in each peer's directory per miss, a few microseconds a file name | each process's share of the disk still its own: a region two processes read lies twice |

The third needs no process to run beside the application and no lock beyond each directory's, and keeps
every guarantee an entry has.

### 10.7 A cache several machines share

Machines that answer questions over the same datasets each read from the store the regions the others
hold. A store of keys and values they share, such as Redis, Memcached or a cloud's cache service, can
hold those regions for all of them, as a second tier: the local disk first, then the shared store,
then the object store. Not built: it comes with the first store a deployment names.

The tier is not the local cache's storage. The local cache reads its entries synchronously, a view of a
mapping or a read of the system's cache, inside the paths that decide what a question reads, and its
queues choose what leaves by its own process's reads (§10.5). A store of keys and values answers over
the network, and evicts as its operator configures it, for every machine at once. So the local cache
keeps its files and its queues, and the tier sits where the object store is asked, in the three readers
that ask it for what the local cache keeps: the object's reader, for the regions its table of contents
declares and the pages and segments of an index's runs (`SynopsisCacheReader`); the sealed reader, for
a sealed object's frames and trailer; and the tree's page source, for its pages. For the entries of a
batch the local cache missed, each reader asks the tier in one round trip, then the object store for the
rest. It keeps what the tier sent in the local cache, as it keeps what the object store sends, and
offers the tier what the object store sent, written behind the question as the local cache writes.

The store implements two calls: a read of a batch of keys, each into a destination whose length its key
names, which says which keys it held; and a write of a key's value, whose failure is ignored. A key is
the local cache's file name: an object's uid with a region's place, length and checksum, or with a
frame's place and length, or a page's hash and length. A value never changes, so nothing is ever
invalidated, and no write of one machine can contradict another's.

Every value is checked as a local file is: a region against its checksum, a frame and a trailer by
their reader's authentication, a page against its hash. A store that loses, cuts or alters a value
costs a read from the object store, never a wrong answer. A sealed dataset's values are the bytes its
object store holds, encrypted, so the shared store sees no plaintext and no key. A plain dataset's are
its indexes in the clear, tables of counts, sketches and postings: the shared store stands inside the
boundary the object store does. A store that fails or answers past a deadline is passed over: the batch
goes to the object store, as it would without the tier.

What it costs: a round trip to the shared store for each batch the local cache missed, about a
millisecond inside a region of a cloud against tens to an object store, the values' bytes, and a write
for each region the object store sent. What it holds is the store's to bound, by its memory and its
policy. A store on each machine could serve its processes in place of a shared root, at the price of a
process to run beside the application, the companion weighed in §10.6.

## 11. Writing

### 11.1 A file written to a location

`CreateWriter(location)` writes into a pipe whose reader is the store's upload. Up to the part
threshold, the bytes are held and sent in one put at completion, with their checksum in a header.
Past it, the upload starts a multipart upload, sends each part as it fills and lets the pipe's
backpressure slow the writer when the link is the bottleneck. The parts in flight, four by default,
are held in buffers reserved through the memory governor. Completion sends the last part and
completes the upload with the whole object's checksum. A writer that is abandoned cancels the
upload at once, its parts in flight included, and aborts it under a deadline of its own, 30 seconds,
so that a caller who gave up is not held by the abort, and an upload the abort could not reach is
left to the bucket's lifecycle rule.

The object appears whole or not at all, which is what the rename gives a path, and it replaces the
object at that key, as a path's file is replaced. The part threshold is 32 MiB and the part size
16 MiB by default, starting points to set by measurement. Every service takes parts of at least
5 MiB but the last, and at most as many as its profile allows: 10 000 on S3, R2, OVHcloud and Cloud
Storage, and 1 000 on Scaleway. The store sizes the parts of an object of known length from
that number, 16 MiB or the object's size divided by it and rounded up to a mebibyte, whichever is
larger: they grow past 16 MiB from about 16 GiB on Scaleway and from about 156 GiB on S3. The parts
of an object of unknown length, a file written to a location, double every tenth of the parts the
service takes, up to eight times, so that they reach about 1 TiB on S3 and 100 GiB on Scaleway with
128 MiB parts at the end. R2 wants every part but the last of one size, so its parts never grow, and
an object of unknown length past about 156 GiB is refused there at its 10 001st part.

Every store's put goes through one upload (`Http/Upload.cs`), which the three stores give their
requests to: the whole object, the start of an upload in parts, a part, the completion, the abort and
the head that settles a lost answer. What a put holds to send again depends on its content:

- Bytes already in memory, `PutAsync(key, ReadOnlySequence<byte>, condition)`, are sent where they
  lie, whole or as slices, and sent again from there by any retry. A dataset's objects go this way.
- A stream, the `PipeReader` overload, is held a part at a time while the parts are in flight, or
  whole when it is small enough to go in one request. A conditional put to a service that takes a
  condition only on a whole object, R2 or Cloud Storage, is held whole in memory up to
  `StoreTransportOptions.MaxHeldBytes`, 256 MiB by default, and refused past it before a byte goes.
- A stream on a store given `StoreTransportOptions.SpoolDirectory` is written whole to a file of
  that directory, mapped, and sent from the mapping as bytes in memory are, then the file is deleted.
  Its upload starts once the stream has ended, and it holds no pooled buffer, only the pages the
  system reads back as it sends them.
- A stream whose length is unknown but bounded, the overload that takes a `maxLength`, as a
  compaction's output is by its inputs, is sized by its bound: held whole and sent in one request
  when the bound is under the part threshold, and otherwise sent in parts sized as an object of
  that length's would be, of one size on R2, so that no part has to grow and no spool is needed.
  Only a bound past what the service's parts reach goes to the spool, or is refused before a byte
  goes when there is none, and content past its bound is refused and its upload given up. The
  stores that only hold bytes, in memory or in a directory, take the bound as an unknown length.

A completion that S3 refuses with 409 asks for the upload to be made again: the upload starts anew
from its first part when the content can be sent again, held in memory or spooled, and the put throws
for a stream sent as it was read, its message pointing at the spool.

### 11.2 The objects of a dataset

A dataset holds each object it writes up to the part threshold, 32 MiB, since it reads the object's
statistics and first key from memory before the put, which costs no request (`VortexDataset.cs`,
`SealAsync`). The put goes at the commit, from that buffer where it lies, in one request, and any
retry is sent from the same bytes, so nothing is copied on its way.

An object that passes the threshold is sent as it is written (`ObjectSegmentSink`): what was held goes
into a pipe whose reader is the store's put of unknown length, and the rest follows as the writer
writes it. The store sends it in parts as they fill, its parts in flight in its own buffers, and the
writer waits on the pipe when the link is slower, so a writer holds a few mebibytes whatever the
object's size, and the cap of 1 GiB an object once had is gone (`DatasetOptions.MaxObjectBytes` now
bounds what the store keeps, unbounded by default). The commit completes the pipe, which completes the
upload.

A rewrite, a compaction's or a delete's that keeps some of an object's rows, announces a bound with
that put (`ObjectBound`): what it reads, times 1.03, the most bytes a rewrite wrote for each byte it read
on the lifecycle bench (1.024 at worst over periods of a day and of an hour and the leveled style,
1.000 for a seal). Without it, parts sized for an unknown length reach about a terabyte on S3, but
168 GB on R2, whose parts keep one size, and 106 GB on Scaleway, which takes a thousand parts, short of
a period of a day at a few thousand rows a second, which would fail at its end, every time it ran. With
it, the parts are sized for the bound from the first. Content that runs past the bound is what the
store refuses, so the sink checks it at each write, before the put has sent the bytes past it, and the
rewrite gives its objects up and starts again under a bound twice as large; a delete does so without
taking one of its attempts, since no other writer caused it. An append has no inputs to bound it and
goes with an unknown length, so an append of one object past those reaches needs the spool there. The
parts in flight are the bound's share of the service's parts: a period of 173 GB on Scaleway sends parts
of about 180 MB, four at a time by default, where a period of an hour, 7 GB, keeps the parts of 16 MiB. The object's statistics and first key are then read from the store, its footer in one request
when its entry records the footer's length, which an object past the threshold takes as a few requests
among the dozens its parts took. An object given up completes its pipe with an error, and the store
gives its upload up. A data object's key ends with a fresh uid and is never taken, so its put carries
no condition, whole or in parts, on every service, and a lost answer needs no head: sent again, it
writes the same bytes. A commit object keeps its condition everywhere.

### 11.3 Checksums

The sink computes the XXH3-128 of every object as it writes it, and the leaf entry records it
(`ObjectSegmentSink.cs`). S3 accepts XXHash128 as an object checksum since April 2026, for an object
sent in one request. A dataset's object sent in one request to Amazon S3 therefore carries
`x-amz-checksum-xxhash128`, and S3 verifies the very hash the dataset records. A sealed object is
the exception, since the entry's hash is of its plaintext: the sealing stage hashes the sealed bytes
as they leave it, and the header carries that hash
([datasets](18-encryption.md#63-datasets)). The service expects
the big-endian value in base64. That S3's XXHash128 is the XXH3-128 of `System.IO.Hashing`, and in
which byte order that type writes it, are checked by the contract tests before the header is relied
on. A multipart upload accepts XXH3-128 only as a composite of its parts, so it uses CRC-64/NVME
over the whole object instead, kept as the parts are produced, in order. Azure checks the
CRC-64 of each request, and Cloud Storage the CRC-32C of each put. R2, OVHcloud and Scaleway are sent
none by default, as [their profiles](#74-cloudflare-r2-ovhcloud-and-scaleway) explain. `PutChecksum`
chooses the same way on the three stores: the service's own by default, `Content-MD5`, or none.

### 11.4 Appends

A Vortex append writes its blocks after the old end and a new tail after them
([write strategy](11-write-strategy.md)), and an object cannot grow. Rewriting the object with the
old bytes copied on the service, `UploadPartCopy` on S3, `Put Block From URL` on Azure, composition
on Cloud Storage, was built and removed: six paths, one per service, each with its exceptions, for a
need no user had stated, and none run against a service. `OpenWriterAsync(location)` throws a
`NotSupportedException`. A file at a location is written again, and rows that keep coming go to a
dataset, which is made for them.

## 12. Credentials

One public type, `StoreCredentials`, says how a store authorizes its requests, whichever service it
speaks to. Its factories cover what a host meets: keys or an account key given in code, keys and
tokens from a source of the host's, a shared access signature, none, and the environment's
credentials, which resolve for each service the chain its own SDKs follow. A store takes the part its
service understands and refuses, when it is built, credentials it cannot use, so that a token given to
an S3 store fails at once rather than at the first request.

| factory | S3 | Azure | Cloud Storage |
|---|---|---|---|
| `FromEnvironment()`, the chains below | yes | yes | yes |
| `AccessKey(accessKeyId, secretAccessKey, sessionToken)` | yes | | |
| `AccessKey(source)`, keys from a source, renewed before their expiry | yes | | |
| `SharedKey(account, accountKey)` | | yes | |
| `SharedAccessSignature(signature)` | | yes | |
| `Token(source)`, bearer tokens from a source, renewed before their expiry | | yes | yes |
| `Anonymous()`, an emulator, or a bucket or container anyone may read | yes | yes | yes |

A source is how a host plugs in what this package does not resolve: the AWS SDK's own credentials for
single sign-on or a credential process, `Azure.Identity` or Google's libraries for the identities
their chains know and these do not.

### 12.1 AWS, and the clouds that speak S3

The AWS SDKs standardize the sources of credentials, though each orders them its own way. The chain
takes them in the order of the SDK for Java's default chain, and keeps the first source the
environment configures:

1. The environment: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and `AWS_SESSION_TOKEN`.
2. A web identity: `AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN`, exchanged with STS by
   `AssumeRoleWithWebIdentity`, the file read again at each refresh. This is how a Kubernetes service
   account reaches a role on EKS. STS is asked in its region's partition, `amazonaws.com.cn` in China.
3. A profile of the shared configuration and credentials files, with static keys or a role to
   assume by web identity.
4. The container endpoint: `AWS_CONTAINER_CREDENTIALS_FULL_URI` or `_RELATIVE_URI`, with the
   authorization token or its file, which serves ECS tasks and EKS Pod Identity. A full URI is taken
   over HTTPS, or over HTTP on the loopback and the container hosts of ECS and EKS, `169.254.170.2`,
   `169.254.170.23` and `fd00:ec2::23`, as the SDKs restrict it, so that the token never leaves the
   host.
5. The instance metadata service, version 2 only: a session token first, then the role's
   credentials.

A profile that takes its keys from IAM Identity Center, from a credential process, or from a role
assumed with another profile's or the host's credentials is refused with a message that names what it
asks, rather than passed over for the metadata service. A process run to fetch credentials is a host's
decision, and single sign-on needs a browser: the AWS SDK's credentials serve both through
`AccessKey(source)`.

R2, OVHcloud and Scaleway issue S3 key pairs, an access key and a secret, which come from code or
from the same environment variables, the endpoint from `AWS_ENDPOINT_URL_S3`. None of them offers an
identity that the later steps of the chain would find.

### 12.2 Azure

1. A client secret in the environment: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
   `AZURE_CLIENT_SECRET`.
2. A workload identity: `AZURE_FEDERATED_TOKEN_FILE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
   `AZURE_AUTHORITY_HOST`, the file's token exchanged as a client assertion at the version 2 token
   endpoint for the scope `https://storage.azure.com/.default`. The file is read again at each
   exchange, as Microsoft's documentation asks, since Kubernetes rotates it in place.
3. The storage credentials Azure's tools read: a connection string in
   `AZURE_STORAGE_CONNECTION_STRING`, with an account key or a shared access signature, or
   `AZURE_STORAGE_KEY` with `AZURE_STORAGE_ACCOUNT`, or `AZURE_STORAGE_SAS_TOKEN`.
4. A managed identity, through the `IDENTITY_ENDPOINT` and `IDENTITY_HEADER` of App Service and
   Functions, or the instance metadata endpoint.

### 12.3 Google Cloud

The chain follows the order of Google's application default credentials:

1. `GOOGLE_APPLICATION_CREDENTIALS`, naming a JSON file. A service account key requests its tokens
   with a JWT it signs with its RSA key, through the platform's `RSA`. A workload identity federation
   configuration exchanges its external token, from a file or a URL, at Google's security token
   service, then for the service account it impersonates, if it names one. A configuration whose
   token comes from an AWS identity is refused with a message that says so.
2. The file `gcloud auth application-default login` writes: `$HOME/.config/gcloud/application_default_credentials.json`,
   or under `%APPDATA%\gcloud` on Windows.
3. The metadata server of Compute Engine, GKE and Cloud Run (`metadata.google.internal`, with the
   header `Metadata-Flavor: Google`), which serves the attached service account's tokens, and on GKE
   the pod's workload identity.

### 12.4 Refresh and hygiene

Credentials that expire are renewed five minutes before they do, by one caller while the others keep
using the current ones, and a failed renewal is retried until the old credentials actually expire.
A request never waits for a renewal unless its credentials have expired. A refusal for expired keys or
an expired token renews only the ones the refused request carried, read back from it, so that the
refusal of a request signed before a renewal leaves the renewed credentials alone.

Credentials hold no resource and are never disposed. Stores share them, and stores that share a set
renew its keys and tokens once for all of them. A secret stays in managed memory while its
credentials live, as it does in the platform's and the cloud SDKs' own clients: the string an
environment variable or a file first gave it in stays on the managed heap until it is collected, which
no .NET code can prevent, so a copy kept elsewhere would not shorten its life. Secrets never appear in
an exception, a log, a trace or a `ToString`, and neither does a request's signature. Errors carry the
kind of credentials, the access key id on S3, and the service's code and request id, which is what
support asks for and reveals nothing.

## 13. Errors

| answer | becomes |
|---|---|
| 404 (`NoSuchKey`, `BlobNotFound`, Cloud Storage's 404) on a read or a head | `ObjectNotFoundException` on a read, `null` from a head, as today |
| 412 on a conditional put | `PutOutcome.Exists` when the key had to be absent, `PutOutcome.Changed` when its token had to match |
| 412 on a read with a token | `VortexFormatException`, saying the object changed under the reader, as the token check says today |
| 416 on a read | the `ArgumentOutOfRangeException` the interface already throws for an offset past the end |
| 403, 401 after a renewal | `ObjectStoreException`, with the service's code and request id, and the identity used, never its secret |
| a 5xx or a 429 after the budget is spent | `ObjectStoreException`, with the attempts made and the last answer |
| a conditional put whose outcome cannot be proven | `ObjectStoreException` saying the outcome is unknown (see [writes whose outcome is unknown](#64-timeouts-retries-and-writes-whose-outcome-is-unknown)) |

## 14. Observability

The stores report through the existing meter and activity source, `Vorticity`, and cost nothing when
nobody listens ([observability](09-contracts.md#5-observability)):

| instrument | diagnoses |
|---|---|
| `vortex.store.requests`, tagged by operation, provider and status class | what was asked, of whom, and how much of it failed |
| `vortex.store.bytes_read`, `vortex.store.bytes_written` | traffic, against what the plans said |
| `vortex.store.retries`, `vortex.store.throttles` | how much latency and money the retries cost, and how often a service asked to slow down |
| `vortex.store.first_byte`, a histogram in milliseconds | the latency of a service, the figure any sizing of the reads in flight would start from |

Tags never carry a key, a bucket name or a token, so their cardinality stays bounded. Below them, the
platform's HTTP client reports its own requests, connections and durations through `System.Net.Http`,
which OpenTelemetry already collects. `CountingObjectStore` wraps any store as before and still counts
dependent steps, which no meter can.

## 15. How it is tested

- The store contract, `ObjectStoreContractTests`, extended with the revised members, runs against
  the memory store, the file store and the emulators of this repository on every run. It runs as well
  against any bucket the environment names, of S3, R2, OVHcloud, Scaleway, Azure or Cloud Storage,
  with credentials of its own. `LiveServiceTests` adds there what only a service proves: an upload in
  parts it assembles, a suffix read, a dataset in a bucket. Those runs settle every capability this
  document marks to check, and a profile changes only when they do.
- The protocol is tested against published vectors: AWS's Signature Version 4 test suite, the Shared
  Key examples of the Azure reference, and the CRC-64/NVME and CRC-32C check values.
- A message handler given to the store's `HttpClient` in the tests answers with delays, resets, 503s,
  429s, 409s and 412s, short bodies and wrong `Content-Range`s, so every row of [errors](#13-errors)
  and of the retry table is exercised without a network, including the put whose answer is lost after
  the object was created.
- The reading of the services' XML and JSON answers is fuzzed: no input crashes it, hangs it, or
  makes it allocate past its bounds.
- Ratchets: requests and dependent steps per operation, held by `CountingObjectStore` as the dataset's
  budget tests are, bytes allocated per request, a constant held by a ceiling, and nothing allocated
  per batch by this library's code.
- Throughput is measured against each service by hand, one component at a time, never the full
  benchmark matrix.

## 16. The order of the work

1. The interface in the core, revised, with the memory, file and counting stores. The dataset passes
   the entry's length when it opens an object, and vacuum uses the listing's heads. These two save
   requests on every store, whatever the service.
2. The transport over `HttpClient` and the S3 read path: reads, suffix reads, heads, listings,
   Signature Version 4, credentials from code and the environment, and `s3://` locations. The bytes
   a request allocates are measured here, and their ceiling becomes a ratchet.
3. The S3 write path: puts, multipart uploads, conditions, checksums, deletes, and datasets on S3
   with the contract tests against S3.
4. The profiles of R2, OVHcloud and Scaleway, with their contract tests, which settle what this
   document marks to check.
5. Azure, every operation, its signatures and tokens.
6. Cloud Storage: its generation preconditions, its JSON listings, metadata and batches, its XML
   reads and multipart uploads.
7. The credential chains: web identity, container credentials, instance metadata, workload and
   managed identities, and Google's application default credentials.
8. The store contract against each provider's bucket, and the capabilities it proves turned on in
   the profiles.
9. Each of what was built and removed, once a measurement or a user asks for it: the reads in flight
   in bytes, the first batch on two lanes, hedging, Azure's structured body, appends to stored files
   and directory buckets.

## 17. Decisions to take

| decision | recommended | why |
|---|---|---|
| the store interface moves into the core | yes | a single file needs it, and the dataset package is experimental, so nothing stable breaks |
| the package's name | `Vorticity.ObjectStorage` | it says what it holds, and leaves `Storage` free for anything local |
| the default session resolves locations from the environment | no, one line opts in | a library does not open connections or read credentials on a host that did not ask, as it does not take its cores |
| the platform's `HttpClient` rather than a client of this repository | the platform's | it reads bodies without an extra copy and exposes every control the stores need. Its allocation per request is a constant, and a client of our own would carry the risks the platform's has already paid for |
| R2, OVHcloud and Scaleway | profiles of the S3 store | they speak the API, and differ in a few capabilities that a profile states and the contract tests check |
| Cloud Storage | its own APIs rather than its S3 interoperability | generation preconditions, a listing that carries retention and holds, deletes in batches, and the platform's identities |
| checksums on the clouds that share only `Content-MD5` | none by default, `Content-MD5` as an option | TLS already protects the bytes on their way, and MD5 costs a pass over every byte |
| a capability of an S3 service that no test against it has proven | taken as absent, an option sets it | a wrong absence costs a request, a wrong presence a failed open or a lock the dataset believes kept |
| a locked dataset on S3 elsewhere than on Amazon | refused until `KeepsLocks` is set | R2 and an OVHcloud Local Zone keep no retention or legal hold where a head reports it, and the others are to check |
| the listing returns heads, a change to the interface | yes | vacuum stops asking for a head per object, one awaited after another |
| a listing that lags, on a store that does not document its consistency | allowed, by less than the retention window | a lag costs a dataset freshness and never a version, so OVHcloud and Scaleway can host datasets |
| hedged reads | not built until measured | each hedge is a request that costs money, and the request ratchets must stay exact |
| part threshold and size | 32 MiB and 16 MiB, then measured | every service's minimum part is 5 MiB, and parallel parts are what fill a link |
| directory buckets | refused | they list without order, which the dataset's discovery needs, and no user asked for their latency |
| appends to stored files | refused | an object cannot grow, and a path per service to copy the old bytes is a lot of code for a need no user stated |

## 18. What this rests on

Each claim is either read in this repository's code, taken from a source checked on 2026-10-10, or
a proposal of this design, to be measured.

| claim | where it comes from |
|---|---|
| a dataset's object opens with a head then the tail, although its entry knows the length | code: `ObjectCache.cs` (the open passes no length), `ObjectSegmentSource.cs` (`GetLengthAsync` asks a head), `VortexFile.cs` (the length before the tail) |
| vacuum asks a head for each commit and each unreferenced data object, one after another | code: `DatasetVacuum.cs` |
| the session's bound counts a batch read once, and the object source issues all its ranges together | code: `SessionReader.cs`, `ObjectSegmentSource.cs` |
| each segment of a range is copied into an aligned block of its own | code: `ObjectSegmentSource.cs`, `Aligned` |
| a refused commit is rebuilt on the version that exists | code: `DatasetCommitter.cs`, the conditional creation |
| vacuum keeps every unreferenced object younger than its window, and the latest version it lists | code: `DatasetVacuum.cs` |
| the sink hashes every object with XXH3-128 | code: `ObjectSegmentSink.cs` |
| the dataset's object source allocates per batch: a list, task arrays, a task per read, an array of ranges, a delegate | code: `ObjectSegmentSource.cs`, `ReadManyAsync` |
| `System.IO.Hashing` 11 takes the parameters of any CRC, CRC-64/NVME and CRC-32C among them, where 10's `Crc64` was ECMA-182's alone | code: `Crc64ParameterSet.cs` and `Crc64.cs` in dotnet/runtime, and the documentation of `System.IO.Hashing` 10.0.12 |
| `SocketsHttpHandler` reads a response body straight from the TLS stream into the caller's memory when its own buffer is empty, which starts at 4 KiB | code: dotnet/runtime, `HttpConnection.ReadAsync(Memory<byte>)` and `ReadBufferedAsync`, read on 2026-10-10 |
| one range per S3 `GET`, suffix ranges, Object Lock headers on reads and heads | source: Amazon S3 API reference, `GetObject` |
| a put to a bucket under Object Lock needs `Content-MD5` or a checksum header | source: Amazon S3 API reference, `PutObject` |
| conditional writes on `PutObject`, `CompleteMultipartUpload` and `CopyObject`, 412 and 409, uploads in progress not considered | source: Amazon S3 user guide, conditional writes |
| conditional deletes, by an ETag per key in `DeleteObjects` | source: Amazon S3 API reference, `DeleteObjects` |
| no extra charge for conditional reads and writes | source: Amazon S3 user guide, conditional requests |
| `DeleteObjects` takes 1 000 keys and requires `Content-MD5` on general purpose buckets | source: Amazon S3 API reference, `DeleteObjects` |
| the AWS SDKs send a CRC-32 checksum header instead of `Content-MD5` on `DeleteObjects` | source: GitLab's container registry, issue 2309, a lead only, checked by the contract tests |
| `ListObjectsV2`: 1 000 keys a page, ordered on general purpose buckets, unordered and without `start-after` on directory buckets | source: Amazon S3 API reference, `ListObjectsV2` |
| XXH3-128 and four other checksums accepted since 2026-04-23, composite only for multipart, CRC-64/NVME full object | source: Amazon S3 user guide, checking object integrity, and the announcement of 2026-04-23 |
| spreading requests over connections, byte-range fetches, aggressive retries | source: Amazon S3 user guide, performance guidelines |
| S3's 3 500 writes and 5 500 reads a second per prefix | source: Amazon S3 user guide, optimizing performance |
| S3's general purpose endpoints answer in HTTP/1.1 | source: an AWS re:Post answer, a lead only, checked by the contract tests |
| `AWS_ENDPOINT_URL` and `AWS_ENDPOINT_URL_S3` | source: AWS SDKs and Tools reference guide, service-specific endpoints |
| the AWS credential sources and their environment variables, each SDK ordering them its own way | source: AWS SDKs and Tools reference guide, standardized credential providers, and the SDK for Java's default chain |
| R2: conditional headers on puts and reads, `start-after`, `UploadPartCopy` by ranges, no Object Lock header, `Content-MD5` and no checksum algorithm | source: Cloudflare R2 documentation, S3 API compatibility, updated 2026-07-31 |
| R2: 5 GiB in one request, 10 000 parts of 5 MiB to 5 GiB of one size but the last, one write a second to a key answered 429 | source: Cloudflare R2 documentation, limits and multipart objects |
| R2 is strongly consistent, listings included | source: Cloudflare R2 documentation, consistency model |
| R2's endpoint `account.r2.cloudflarestorage.com`, the region `auto` | source: Cloudflare R2 documentation, the S3 API |
| OVHcloud: `If-None-Match` on `PutObject` and `CompleteMultipartUpload`, 412, 404 and 409 | source: OVHcloud documentation, conditional writes, 2026-06-09 |
| OVHcloud: Object Lock configuration everywhere, retention and legal hold in regions only, multipart operations without `UploadPartCopy` listed | source: OVHcloud documentation, S3 compliance |
| OVHcloud: 10 000 parts of 5 MiB to 5 GiB, 5 GiB in one request, 300 writes and 900 reads a second per bucket as soft limits, 1 Gb/s per connection | source: OVHcloud documentation, technical limitations, 2026-03-13 |
| OVHcloud's endpoints `s3.region.io.cloud.ovh.net`, buckets addressed by host, Signature Version 4 | source: OVHcloud documentation, endpoints and geoavailability, 2026-09-24 |
| Scaleway: conditional writes on `PutObject`, `CopyObject` and `CompleteMultipartUpload` | source: Scaleway documentation, using conditional writes, validated 2026-07-03 |
| Scaleway: `DeleteObjects` of 1 000 keys with `Content-MD5`, `start-after`, Object Lock in compliance mode, `UploadPartCopy` | source: Scaleway documentation, object operations, validated 2025-08-11 |
| Scaleway: 1 000 parts of 5 MB to 5 GB, objects up to 5 TB | source: Scaleway documentation, multipart uploads, validated 2025-07-30 |
| Scaleway's endpoints `s3.region.scw.cloud` | source: Scaleway documentation and libcloud's Scaleway driver, a lead |
| Scaleway: reads after writes and deletes consistent in every region, listings not mentioned | source: Scaleway documentation, Object Storage FAQ |
| suffix reads on R2, OVHcloud, Scaleway and Cloud Storage, and the order and `start-after` of OVHcloud's listings | not documented, checked by the contract tests |
| the consistency of OVHcloud's and Scaleway's listings | not documented, and not needed: a lag shorter than the retention window costs freshness only |
| Azure ranges are `bytes=a-` and `bytes=a-b` only | source: Azure Storage REST reference, specifying the range header |
| `Get Blob`: CRC-64 of a range up to 4 MiB, the structured body, immutability and legal hold headers, HTTP/1.0 and 1.1 | source: Azure Storage REST reference, `Get Blob` |
| `Put Blob` up to 5 000 MiB, blocks up to 4 000 MiB, 50 000 blocks, uncommitted blocks kept a week | source: Azure Storage REST reference, `Put Blob` and `Put Block List` |
| `List Blobs`: 5 000 a page, `startFrom` inclusive since 2023-05-03, locks in the listing, alphabetical order, `/` first on a hierarchical namespace | source: Azure Storage REST reference, `List Blobs` |
| `Blob Batch`: 256 sub-requests, 4 MB, each authorized | source: Azure Storage REST reference, `Blob Batch` |
| conditional headers, 412 on writes, `If-None-Match: *` creates only if absent | source: Azure Storage REST reference, conditional headers |
| Azure's CRC-64 is CRC-64/NVME, the structured body's layout | source: Azure Storage REST reference, structured body format |
| the workload identity's variables, the version 2 endpoint, the token file read at each exchange | source: Azure Kubernetes Service documentation, workload identity |
| Cloud Storage: a generation precondition of 0 creates only if absent, preconditions on writes, deletes and composition, 412 when one fails | source: Cloud Storage documentation, request preconditions |
| Cloud Storage: `objects.list` ordered by name, `startOffset` inclusive, about 1 000 a page | source: Cloud Storage JSON API reference, `objects.list` |
| Cloud Storage: an object's generation, creation time, CRC-32C, retention, retention expiration and holds | source: Cloud Storage JSON API reference, the objects resource |
| Cloud Storage: strongly consistent reads after writes and deletes, and listings | source: Cloud Storage documentation, consistency |
| Cloud Storage: XML multipart uploads take no precondition, and multipart objects have no MD5 | source: Cloud Storage documentation, XML API multipart uploads |
| Cloud Storage: batches of up to 100 calls under 10 MiB, unordered, not atomic, billed per call | source: Cloud Storage documentation, batch requests |
| Cloud Storage: 10 000 parts of 5 MiB to 5 GiB, objects up to 5 TiB whatever the way they are written | source: Cloud Storage documentation, quotas and limits |
| Cloud Storage: soft delete on by default for 7 days, the deleted objects billed meanwhile | source: Cloud Storage documentation, soft delete, updated 2026-10-07 |
| Google's application default credentials and their order | source: Google Cloud documentation, application default credentials |
| the platform's client allocates a few kilobytes per request | estimate, measured in the second phase of the work |
| a single object's first batch issues about 14 requests at degree 14, against 2 at degree 1 | measured in this repository in October 2026 |
| part threshold, part size, parts in flight, connection limits | proposal, starting points to set by measurement |

## 19. Where the code departs from this design

The code follows this design but for what the table below lists. Most departures wait on the tests
against the services: a behaviour the documentation leaves open is taken as absent until a test shows
it, and an option turns it on once it does.

| design | code | why |
|---|---|---|
| S3 is sent XXH3-128 in one request | CRC-64/NVME on every put | the byte order of `x-amz-checksum-xxhash128` is to check against the service first, as [the checksums](#113-checksums) ask |
| `x-ms-content-crc64` as Azure's reference shows it | low byte first, as Azure's SDK sends it | to check against the service |
| the parts in flight reserved through the memory governor | bounded by `PartsInFlight`, and a conditional put held whole by `MaxHeldBytes` | the governor's reservation of upload buffers comes in a change of its own |
| a dataset sends its data object's parts as it writes | the object goes up at the commit, from the dataset's buffer | the dataset holds the object until then anyway, and the gain of an early start is to measure against a service |
| a completion answered 409 starts a new upload | it does when the content can be sent again, held in memory or spooled, and throws for a stream sent as it was read | a stream is read once |
| Cloud Storage takes a conditional put up to 5 TiB | from bytes in memory or spooled on disk, and from a stream held in memory up to `MaxHeldBytes` | the service takes no precondition on the parts of an upload |
| `https://account.blob.core.windows.net/container/key` locations | `az://` locations | a registered prefix serves the same need |
| the Azure chain's order | the storage credentials of the environment come before a managed identity | a host that sets them never waits on a metadata service it does not have |
| an external account of Google that exchanges an AWS identity | a subject token from a file or a URL | the AWS source signs a request of its own, left for a host that needs it |
