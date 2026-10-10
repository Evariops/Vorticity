# Object storage: S3, R2, OVHcloud, Scaleway, Azure and Google Cloud Storage, natively

Vorticity reads and writes files and datasets straight on Amazon S3, on Cloudflare R2, OVHcloud and
Scaleway, which speak S3's API, on Azure Blob Storage and on Google Cloud Storage, through clients
written in this repository over the platform's HTTP client. A program that opens
`/data/readings.vortex` today opens `s3://lake/readings.vortex` with the same call, and a dataset
whose store is a directory moves to a bucket by changing the store and nothing else. This document
is the design of that support: where it lives, what it changes in the store interface, how a request
is built, sent, retried and counted, what each provider does differently, and what each operation
costs in requests.

It is a design to build. Nothing in it exists in the code yet, and where it changes a rule that
another document states, it says so. [The scope](01-scope.md#6-what-it-leaves-out) leaves out an S3
client, and [the dataset](13-dataset.md) says that this library never names S3 in code. Both stay
true of the core and of the dataset package, since the clients live in a package of their own, which
neither references. Two rules of [the store abstraction](13-dataset.md#11-the-store-abstraction) do
change: a store may now replace an object when a put asks it to, which a dataset never does, and a
listing may lag, by less than the retention window
([the store interface, revised](#4-the-store-interface-revised) explains both). Encrypting what is
stored is the subject of [18-encryption.md](18-encryption.md), and this document only touches it
where the two meet.

## 1. What it must hold

Five requirements, each of which becomes a test or a ratchet once built:

| requirement | what it means, measured |
|---|---|
| the same API | a file opens, scans, is written and appended with the calls it has on a path, given a location instead. A dataset takes a store, or a location. Nothing else in the public API changes, whichever provider holds the bytes |
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
| the transport's policy over HTTP: deadlines and stall detection, retries and their budget, hedged reads, the spreading of connections over a bucket's addresses, and the outcome of a conditional write whose answer was lost | HTTP (`HttpClient` over `SocketsHttpHandler`): connections and their pool, proxies, chunked bodies, `100-continue` |
| the S3 dialect: Signature Version 4, ranged and suffix reads, conditional writes, multipart uploads, `DeleteObjects`, `ListObjectsV2`, checksums, and the profiles of R2, OVHcloud and Scaleway | TLS (`SslStream`, under `SocketsHttpHandler`), which runs the operating system's TLS library and validates certificates against the system's trust store |
| the Azure dialect: Shared Key signing, shared access signatures, bearer tokens, `Put Block List`, `Blob Batch`, `List Blobs`, the structured body format | name resolution (`Dns`), for the addresses a new connection takes in turn |
| the Cloud Storage dialect: generation preconditions, the JSON API's listings, metadata and batches, the XML API's reads and multipart uploads, composition | JSON parsing (`Utf8JsonReader`), for the token endpoints, the instance metadata and Cloud Storage's JSON API |
| the credential chains of AWS, Azure and Google Cloud, and their refresh | entropy (`RandomNumberGenerator`) |
| an XML reader for the services' responses, and the few writers their requests need | SHA-256, HMAC-SHA-256 and MD5 (`SHA256.HashData`, `HMACSHA256.HashData`, `MD5.HashData`), for the signatures and the `Content-MD5` some requests need |
| CRC-64/NVME, which `System.IO.Hashing` lacks, since its `Crc64` is ECMA-182's, and CRC-32C, a loop over `BitOperations.Crc32C`, which runs the processor's instruction | RSA signatures (`RSA`), for the token requests of a Google service account key |
| | XXH3-128 and CRC-32 (`System.IO.Hashing`) |

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
experimental, so the move breaks no stable contract. `ObjectRange`, `ObjectHead`, `PutOutcome` and
the two store exceptions move with it, and `MemoryObjectStore`, `FileObjectStore` and
`CountingObjectStore` stay where they are.

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

A session holds its stores, as it holds its pool and its cache, because a store owns connections
and credentials, and those are a host's decisions. The longest registered prefix wins, so a bucket
can be served by one store and one of its prefixes by another, with other credentials. A
registration's prefix is a name and not a protocol, and a bucket's name is unique within a provider
but not across providers, so a scheme of the caller's choosing keeps two of them apart: `r2://lake`
beside `s3://lake`.

```csharp
await using VortexSession session = VortexSession.Create(o =>
{
    o.ObjectStores.Add("s3://lake", S3ObjectStore.FromEnvironment("lake"));
    o.ObjectStores.Add("r2://media", S3ObjectStore.ForR2(accountId, "media", S3Credentials.FromEnvironment()));
    o.ObjectStores.Add("gs://warehouse", GoogleCloudStorageObjectStore.FromEnvironment("warehouse"));
    o.ObjectStores.Add("az://archive", new AzureBlobObjectStore(new AzureBlobStoreOptions
    {
        Account = "contosoarchive",
        Container = "archive",
        Credentials = AzureCredentials.FromEnvironment(),
    }));
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

`UseEnvironment` builds a store on the first use of each bucket or container and keeps it for the
session's life (see [credentials](#12-credentials)). An `s3://` location takes the AWS environment,
and its endpoint from `AWS_ENDPOINT_URL_S3` or `AWS_ENDPOINT_URL` when one is set, the variables the
AWS SDKs read, which is how a host points at R2, OVHcloud or Scaleway, the profile following the
endpoint's host. A `gs://` location takes Google's application default credentials, and an `az://`
one Azure's environment. This choice is one of the [decisions to take](#17-decisions-to-take).

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
dataset's keys (`commit/`, `data/`, `leases/`) are relative to it as they are to a directory today.
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
| a put says its condition | `PutIfAbsentAsync` only, and an object is never overwritten | `PutAsync(key, content, length, condition)`, where the condition is absent, matching a token, or none, and `PutOutcome` gains `Changed` for a token that no longer matches | a file written to a location replaces the object as a path's rename does, and an append replaces it only if nothing else did in between |
| a listing says what it knows | keys only, so vacuum asks for a head per candidate (`DatasetVacuum.cs`, one awaited after another) | `ListAsync` yields each key with its length, token and creation time, and the lock when the store's listing reports it | vacuum stops asking for heads, except for the locks that S3 and the clouds speaking its API report only in a head |

`GetRangeAsync` and `PutIfAbsentAsync` remain as extension methods over the new members, so the
dataset's code changes only where it gains something. A store also declares what it can do, so that
a caller chooses the cheaper path without trying the other:

| capability | S3 | R2, OVHcloud, Scaleway | S3 directory buckets | Azure | Cloud Storage | file | memory |
|---|---|---|---|---|---|---|---|
| reads a suffix without the length | yes | to check | yes | no | to check | yes | yes |
| lists in ordinal order, after a key | yes | yes, OVHcloud's to check | no | yes | yes | yes | yes |
| reports locks in a listing | no | no | no | yes | yes | no locks | yes |
| replaces on a token's match | yes | yes | to check | yes | yes | no | yes |

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
directory bucket hosts single files, where its low latency pays, and never a dataset, which refuses
such a store when it opens.

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

## 6. The HTTP layer

### 6.1 Why the platform's client

Requests go through `HttpClient` over `SocketsHttpHandler`. A client of this repository, an HTTP/1.1
client over `SslStream` restricted to what object stores use, was weighed and set aside, because none
of its gains survives a look at what the platform's client does:

| the gain it would have brought | what `SocketsHttpHandler` already does |
|---|---|
| a body read without a copy | it reads a response body from the TLS stream straight into the caller's memory whenever its own buffer is empty (`HttpConnection.ReadAsync` in dotnet/runtime), so only the bytes that arrived with the headers, a few kilobytes at most, are copied. The copy this design removes is the store interface's (see [the store interface, revised](#4-the-store-interface-revised)), not HTTP's |
| no allocation per request | it allocates per request the request and response messages, their headers, a few strings and the state of its calls, a few kilobytes in all, a figure to measure. That is a constant per request, never proportional to the body. In ranges of a mebibyte, it is under half a percent of the bytes moved, objects that die young, which the young generation collects for almost nothing |
| control of connections and deadlines | `MaxConnectionsPerServer`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime`, `ConnectCallback`, which picks the address of each new connection, and `Expect100ContinueTimeout` cover the connections, and a cancellation token covers every deadline. Retries, hedging and the outcome of a lost conditional write are logic above HTTP, written here whichever client sends the bytes |

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
| connections | `MaxConnectionsPerServer`, 64 by default. A request waits for a free connection rather than opening one past the cap |
| spreading | `ConnectCallback` resolves the endpoint and gives each new connection the next of its addresses. S3's guidance is to spread requests over many connections, since the service is a large distributed system and not one endpoint |
| lifetime | `PooledConnectionIdleTimeout` of 20 s and `PooledConnectionLifetime` of 5 minutes, so the pool follows the service's name resolution |
| TLS | TLS 1.2 or 1.3, the server's certificate checked against the system's trust store. Plain HTTP only for an endpoint marked as an emulator on loopback |
| turned off | cookies, automatic decompression and redirects, since S3 answers a request sent to the wrong region with a redirect that names the right one, and the store signs the request again for it |
| proxies | the environment's (`HTTPS_PROXY`, `NO_PROXY`), as the platform reads them, or one given in the options |
| large puts | `Expect: 100-continue` on a put larger than a part, so a request refused on its headers, a denied one or a bad signature, costs no body |
| draining | none (`MaxResponseDrainSize` of 0): a body abandoned before its end, a hedge's loser or a stalled read, closes its connection instead of being read to the end for nothing |
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

Every attempt has a deadline for its first byte and a floor on its throughput once the body flows,
so a stalled connection is abandoned rather than awaited. An attempt abandoned before its body ends
is cancelled, which closes its connection rather than draining it.

| outcome | what the client does |
|---|---|
| 500, 502, 503, 504, 429, a connection refused or reset, a deadline missed | retried with exponential backoff and full jitter, from 50 ms to 20 s, honouring `Retry-After`, while the store's retry budget allows |
| 503 `SlowDown` (S3), 503 `ServerBusy` (Azure), 429 (R2, Cloud Storage) | the same, and the store halves its connections to that endpoint for a while, since the service is throttling |
| 400 `RequestTimeout` (S3, a body too slow) | retried once |
| a clock skew (S3 `RequestTimeTooSkewed`, an Azure date refused) | the offset from the response's `Date` is applied to the signing clock, and the request retried once |
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
   retry when the service answers that the upload no longer exists.
3. If the object carries this put's id, the answer is `Created`. If it carries another id, the answer
   is `Exists`. If there is no object, the put is sent again. If the head fails too, the store throws
   an `ObjectStoreException` that says the outcome is unknown, and the caller decides.

That costs one head, only in the rare case where the network failed at the worst moment.

### 6.5 Hedged reads

A read that has not seen its first byte when most reads have finished is probably on a slow path,
and a second request is likely to take another one. S3's performance guidance makes the same point
about aggressive timeouts and retries. The client tracks the first-byte latency of each endpoint in
a small histogram and, for reads only, sends a second request when the first passes a percentile of
it, keeps whichever answers first and closes the other's connection.

A hedge costs a request, and requests cost money. Hedging is therefore off by default, a store's
option with a budget (`HedgeAfterPercentile`, 99 by default once enabled, and at most 2 % of reads
hedged), and counted apart from the reads (see [observability](#14-observability)), so the request
ratchets stay exact. Whether it should be on by default is one of the
[decisions to take](#17-decisions-to-take).

## 7. S3, and the clouds that speak its API

### 7.1 Requests and signing

Requests are addressed to `bucket.s3.region.amazonaws.com`, to the endpoint of a cloud that speaks
the API (see [Cloudflare R2, OVHcloud and Scaleway](#74-cloudflare-r2-ovhcloud-and-scaleway)), or by
path to one given for MinIO or Ceph. A store's region comes from its options or from the environment,
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
| `ReadTailAsync` | `GET` with `Range: bytes=-n`. `Content-Range` says the object's length in the same answer |
| `HeadAsync` | `HEAD`, which also says the Object Lock retention date and the legal hold |
| `PutAsync`, up to the part threshold | `PUT` with `If-None-Match: *` when the key must be absent, `If-Match` when the token must match, and the content's checksum in a header (see [writing](#11-writing)) |
| `PutAsync`, above it | `CreateMultipartUpload` with the checksum algorithm, `UploadPart` in parallel, then `CompleteMultipartUpload` with the condition and the whole object's checksum |
| `DeleteAsync` | `DeleteObjects`, 1 000 keys per request, in quiet mode, with the `Content-MD5` its reference asks of general purpose buckets, or a CRC-32 checksum header on a host that forbids MD5 |
| `ListAsync` | `ListObjectsV2` with `prefix`, `start-after`, `max-keys` of 1 000 and `encoding-type=url`. Each entry carries the key, the size, the ETag and the date |

Some answers have a meaning of their own. A conditional `PUT` answered 409 lost a race with a delete
and may be sent again, while a `CompleteMultipartUpload` answered 409 must start a new upload. An
upload in progress does not hold its key: another writer may create it meanwhile, and the completion
is then refused with 412. Both cases are written in the conditional writes guide.

S3 also deletes conditionally, on an ETag, and the store does not use it. Vacuum deletes keys that
no one creates again: a data object's key ends with a fresh uid, a superseded commit's version is
never written twice, and a lease's key names a span of time that has ended.

### 7.3 Directory buckets

S3 Express One Zone's directory buckets answer in single-digit milliseconds, sign with a session
token that `CreateSession` issues and that the store renews before it expires, and accept appends
(`x-amz-write-offset-bytes`, up to 10 000 parts per object). They list in no order and without
`start-after`, so they host single files and never datasets. The store detects a directory bucket by
its name's `--x-s3` suffix.

### 7.4 Cloudflare R2, OVHcloud and Scaleway

Three clouds speak S3's API closely enough to share the store, each through a profile that says
where it differs. A factory picks the profile, or the store recognises it from the endpoint's host,
so the endpoint variable of [locations](#32-locations) is enough to point `UseEnvironment` at any of
them.

```csharp
o.ObjectStores.Add("r2://media", S3ObjectStore.ForR2(accountId, "media", S3Credentials.FromEnvironment()));
o.ObjectStores.Add("ovh://archive", S3ObjectStore.ForOvhcloud("gra", "archive", S3Credentials.FromEnvironment()));
o.ObjectStores.Add("scw://exports", S3ObjectStore.ForScaleway("fr-par", "exports", S3Credentials.FromEnvironment()));
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
| a part copied from an object, for appends | yes | yes | not listed | yes |
| Object Lock in a head | yes | no Object Lock header | yes in regions, not in Local Zones | yes, in compliance mode |
| checksum sent with a put | XXHash128 in one request, CRC-64/NVME in parts | none by default | none by default | none by default |
| limits to live with | 3 500 writes and 5 500 reads a second per prefix | one write a second to the same key, answered 429 | 300 writes and 900 reads a second per bucket, soft limits, and 1 Gb/s per connection | none documented beyond the parts |

Each of them creates a key only if it is absent in one request, which is what a commit needs: a
commit object is small, far below the 5 GiB that one request takes on all four. On R2, which takes
no condition on parts, a conditional put goes whole whatever its size, and one past 5 GiB is
refused. A data object's key ends with a fresh uid, so its put needs no condition, and a large one
goes up in parts on every profile, R2 included.

OVHcloud documents neither the consistency of its listings nor suffix reads, and lists
`ListObjectsV2` without saying that it honours `start-after` and the order. Scaleway documents that
reads follow writes, and says nothing of its listings or of suffix reads. A listing that lags costs
a dataset freshness only, as [the store interface, revised](#4-the-store-interface-revised) shows.
The order and `start-after` are another matter, since without them the first key of `commit/` is not
the latest version, so a dataset is allowed on OVHcloud once the contract tests have checked both.

R2 accepts no Object Lock header, so a dataset created with `LockedStore` refuses an R2 store, and
the same holds on OVHcloud's Local Zones, which keep no retention or legal hold. R2 answers 429 to a
second write to the same key within a second. A dataset writes each key once, so only a retry meets
it, and a retry waits as [the retries](#64-timeouts-retries-and-writes-whose-outcome-is-unknown)
say. OVHcloud's 1 Gb/s per connection means that a 10 Gb/s link needs ten connections in flight at
least, which the window of [the reads in flight](#101-the-reads-in-flight) provides.

The three clouds share no checksum but `Content-MD5`, and R2 documents no other. The store sends none
by default, since TLS already protects the bytes on their way, and MD5 would cost a pass over every
byte in a hash no processor accelerates. A store can turn `Content-MD5` on, and the contract tests
establish what else each cloud verifies. A bucket under Object Lock is the exception, since S3's
Object Lock refuses a put that carries no checksum: a store created for a locked dataset on OVHcloud
or Scaleway sends `Content-MD5` with every put.

### 7.5 Other stores that speak the API

A store that speaks S3's API without a profile here, MinIO or Ceph for instance, declares its
differences in its options rather than being detected at run time: whether it honours conditional
writes, which checksums it accepts, whether it needs `Content-MD5` on deletes, how many parts it
takes, and whether it addresses buckets by path. A dataset refuses a store that cannot create
conditionally, as [the store abstraction](13-dataset.md#11-the-store-abstraction) requires. The
contract tests run against MinIO in CI for that reason, besides the services themselves.

## 8. Azure Blob Storage

### 8.1 Requests and signing

Requests go to `account.blob.core.windows.net/container/blob`, or to an endpoint given for Azurite
or a sovereign cloud, with an `x-ms-version` the store pins. The version must be 2025-01-05 or later,
for the structured body, which also covers `startFrom` in listings (2023-05-03) and the immutability
policy in listings and heads (2020-06-12).

A request is authorized by a bearer token from Microsoft Entra ID, which Microsoft recommends, by a
shared access signature, or by Shared Key, an HMAC-SHA-256 of a canonical string with the account
key. Microsoft recommends disallowing Shared Key on the account, and the store supports it for the
emulator and for accounts that still use it.

### 8.2 Operations

| store operation | Azure request |
|---|---|
| `ReadAsync` | `Get Blob` with `x-ms-range: bytes=a-b`, `If-Match` when the token is known, and the structured body when the store verifies reads (see [reading](#10-reading-at-the-speed-of-the-link)) |
| `ReadTailAsync` | Azure accepts `bytes=a-` and `bytes=a-b` only, never a suffix, so `Get Blob Properties` then `Get Blob`, unless the caller gives the length |
| `HeadAsync` | `Get Blob Properties`, which also says `x-ms-immutability-policy-until-date` and `x-ms-legal-hold` |
| `PutAsync`, up to the part threshold | `Put Blob` with `If-None-Match: *` or `If-Match`, and `x-ms-content-crc64` |
| `PutAsync`, above it | `Put Block` in parallel, then `Put Block List` with the condition |
| `DeleteAsync` | `Blob Batch` scoped to the container, up to 256 `Delete Blob` per request and 4 MB of body, each one authorized in the batch |
| `ListAsync` | `List Blobs` with `prefix`, `startFrom` (inclusive, so a key equal to `startAfter` is skipped), `maxresults` of 5 000, and `include=immutabilitypolicy,legalhold` on a locked store |

Azure drops uncommitted blocks a week after the last `Put Block`, so an abandoned upload needs no
clean-up. On an account with a hierarchical namespace, a recursive listing sorts `/` before every
other character. The keys under `commit/` and `data/` contain no further `/`, so the listings whose
order a dataset relies on keep it.

### 8.3 Checksums

Azure's CRC-64 is CRC-64/NVME, the polynomial S3 uses for `CRC64NVME`, so one implementation serves
both services. A `Put Blob` or `Put Block` carries `x-ms-content-crc64`, which the service checks. A
read can ask for a structured body, in which the service cuts the bytes into segments of 4 MiB, each
followed by its CRC-64, and ends with the CRC-64 of the whole. The client reads the segment headers
apart and the data straight into the destination, so verifying a read costs a CRC pass and no copy.

## 9. Google Cloud Storage

### 9.1 Why its own APIs

Cloud Storage also answers S3's API, through an interoperability mode with HMAC keys. The store
speaks Cloud Storage's own APIs instead, because what a dataset relies on lies there:

- A creation conditioned on the object's generation, a generation of 0 meaning that no object may
  have the name.
- A listing that says each object's retention and holds, so vacuum asks no head.
- Deletes in batches.
- The platform's identities, a service account or a workload's, rather than HMAC keys.
- Composition, which assembles objects on the server.

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
MD5. The store sends the CRC-32C of what it puts, computed with the processor's instruction through
`BitOperations.Crc32C`. Reads return the whole object's hashes only, so a ranged read relies on TLS
and the format's checks, as on S3.

## 10. Reading at the speed of the link

### 10.1 The reads in flight

Today the session's bound on reads in flight counts batch reads: a batch takes one slot, and the
dataset's object source then issues every coalesced range of the batch at once
(`SessionReader.cs`, `ReadManyAsync`, and `ObjectSegmentSource.cs`, the reads issued together). Over
HTTP/1.1 a request in flight is a connection, so the number of connections a session opens is not
bounded by anything the session states. Revised, the bound counts requests: each range takes a slot
for the time its request is in flight, and the store's connections are the slots.

What a link needs in flight is its bandwidth times its latency. At 10 Gb/s and 30 ms that is about
37 MB, three ranges of 16 MiB or 36 of 1 MiB. The session's fixed count of 16 reads fills such a
link only when the ranges are large. The window becomes bytes, shared by the lanes of a query and
sized from the throughput and the first-byte latency the store has measured. Its buffers are
reserved through the memory governor when the query is admitted, a fixed cost as the governor's
other working memory is, so a window never grows past what the process can hold.

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
requests today, against 2 at degree 1, each of which is latency on an object store. A scan over a
source that does network I/O starts on two lanes and widens to its degree once the consumer asks for
the next batch. A lane of a parallel aggregation also takes its next range one step ahead and starts
its reads then, so the first read of each range stops waiting on the critical path. Both changes are
the scan's, and are measured against the source with injected latency that the query tests already
use.

### 10.4 Integrity of what is read

S3 returns a checksum for a whole object, or for a range aligned on one of its parts, never for an
arbitrary range, and neither do R2, OVHcloud, Scaleway or Cloud Storage. A read from them relies on
TLS for the transport and on the format's own checks for the rest: the dataset's pages and headers
carry XXH3, the index regions carry checksums, and a file's structures are validated as they are
parsed. A read from Azure asks for the structured body and verifies each segment's CRC-64 as it
arrives, which costs a pass of carry-less multiplications at several gigabytes per second per core,
a figure to measure.

## 11. Writing

### 11.1 A file written to a location

`CreateWriter(location)` writes into a pipe whose reader is the store's upload. Up to the part
threshold, the bytes are held and sent in one put at completion, with their checksum in a header.
Past it, the upload starts a multipart upload, sends each part as it fills and lets the pipe's
backpressure slow the writer when the link is the bottleneck. The parts in flight, four by default,
are held in buffers reserved through the memory governor. Completion sends the last part and
completes the upload with the whole object's checksum. A writer that is abandoned aborts the
upload.

The object appears whole or not at all, which is what the rename gives a path, and it replaces the
object at that key, as a path's file is replaced. The part threshold is 32 MiB and the part size
16 MiB by default, starting points to set by measurement. Every service takes parts of at least
5 MiB but the last, and at most as many as its profile allows: 10 000 on S3, R2, OVHcloud and Cloud
Storage, and 1 000 on Scaleway. The store sizes parts from
that number, 16 MiB or the object's size divided by it and rounded up to a mebibyte, whichever is
larger. Parts grow past 16 MiB from about 16 GiB on Scaleway and from about 156 GiB on S3. R2 wants
every part but the last of one size, which these fixed parts already are.

### 11.2 The objects of a dataset

A dataset buffers each object it writes, since it reads the object's statistics and first key from
memory before the put, which costs no request (`VortexDataset.cs`, `SealAsync`). The buffer stays,
and the upload changes: once the buffer passes the part threshold, its full parts are uploaded while
the writer goes on, and kept until the commit, so the put at the end waits only for the last part and
the completion. Puts stay conditional where the service allows it at no cost, as S3 does. A data
object's key ends with a fresh uid and is never taken, so where a service takes no condition on parts,
on R2 and Cloud Storage, its large put goes up without one.

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
over the whole object instead, the parts' CRCs combined by multiplication in GF(2). Azure checks the
CRC-64 of each request, and Cloud Storage the CRC-32C of each put. R2, OVHcloud and Scaleway are sent
none by default, as [their profiles](#74-cloudflare-r2-ovhcloud-and-scaleway) explain.

### 11.4 Appends

`OpenWriterAsync(location)` appends to a file in a store. A Vortex append writes its blocks after the
old end and a new tail after them ([write strategy](11-write-strategy.md)), and an object cannot
grow, so the object is rewritten without its bytes passing through the client twice:

| service | how the old bytes stay on the server |
|---|---|
| S3, Scaleway | the old object is copied as the first part (`UploadPartCopy`), the new bytes follow as parts, and the completion carries `If-Match` with the old token. A copied part must be at least 5 MiB, so an old object smaller than that is read and sent again |
| R2 | the same, but its parts are of one size: the old object is copied in parts of the part size, by ranges, and its last partial part is read and sent again with the new bytes |
| OVHcloud | its compatibility table does not list `UploadPartCopy`, so the old object is read and sent again, until a contract test shows the copy works |
| directory buckets | a native append |
| Azure | `Put Block From URL` takes the old bytes, and `Put Block List` commits with `If-Match` |
| Cloud Storage | the new bytes go up as a temporary object, which is composed after the old one under the object's name, conditioned on the old generation, then deleted. Composition takes up to 32 sources and runs on the server |

Appends to stored files are the last phase of the work.

## 12. Credentials

### 12.1 AWS, and the clouds that speak S3

The AWS SDKs standardize the sources of credentials, though each orders them its own way. The chain
takes them in the order of the SDK for Java's default chain, and stops at the first source that
answers:

1. Credentials given in code.
2. The environment: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and `AWS_SESSION_TOKEN`.
3. A web identity: `AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN`, exchanged with STS by
   `AssumeRoleWithWebIdentity`, the file read again at each refresh. This is how a Kubernetes service
   account reaches a role on EKS.
4. A profile of the shared configuration and credentials files, with static keys or a role to
   assume by web identity.
5. The container endpoint: `AWS_CONTAINER_CREDENTIALS_FULL_URI` or `_RELATIVE_URI`, with the
   authorization token or its file, which serves ECS tasks and EKS Pod Identity.
6. The instance metadata service, version 2 only: a session token first, then the role's
   credentials.

Single sign-on and `credential_process` come later. A process run to fetch credentials is a host's
decision, and single sign-on needs a browser.

R2, OVHcloud and Scaleway issue S3 key pairs, an access key and a secret, which come from code or
from the same environment variables, the endpoint from `AWS_ENDPOINT_URL_S3`. None of them offers an
identity that the later steps of the chain would find.

### 12.2 Azure

1. Credentials given in code: a token source, a client secret, a shared access signature, or an
   account key.
2. A client secret in the environment: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
   `AZURE_CLIENT_SECRET`.
3. A workload identity: `AZURE_FEDERATED_TOKEN_FILE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
   `AZURE_AUTHORITY_HOST`, the file's token exchanged as a client assertion at the version 2 token
   endpoint for the scope `https://storage.azure.com/.default`. The file is read again at each
   exchange, as Microsoft's documentation asks, since Kubernetes rotates it in place.
4. A managed identity, through the instance metadata endpoint, or the `IDENTITY_ENDPOINT` and
   `IDENTITY_HEADER` of App Service and Functions.
5. A connection string in `AZURE_STORAGE_CONNECTION_STRING`, with an account key or a shared access
   signature.

### 12.3 Google Cloud

The chain follows the order of Google's application default credentials:

1. Credentials given in code: a token source, a service account key, or an external account's
   configuration.
2. `GOOGLE_APPLICATION_CREDENTIALS`, naming a JSON file. A service account key requests its tokens
   with a JWT it signs with its RSA key, through the platform's `RSA`. A workload identity federation
   configuration exchanges its external token, from a file, a URL or an AWS identity, at Google's
   security token service, then for the service account it impersonates, if it names one.
3. The file `gcloud auth application-default login` writes: `$HOME/.config/gcloud/application_default_credentials.json`,
   or under `%APPDATA%\gcloud` on Windows.
4. The metadata server of Compute Engine, GKE and Cloud Run (`metadata.google.internal`, with the
   header `Metadata-Flavor: Google`), which serves the attached service account's tokens, and on GKE
   the pod's workload identity.

### 12.4 Refresh and hygiene

Credentials that expire are renewed five minutes before they do, by one caller while the others keep
using the current ones, and a failed renewal is retried until the old credentials actually expire.
A request never waits for a renewal unless its credentials have expired. Secret keys are copied into
native memory as soon as they are read and wiped when the store is disposed. The string an
environment variable or a file first gave them in stays on the managed heap until it is collected,
which no .NET code can prevent, so the native copy narrows the window rather than closing it. Secrets
never appear in an exception, a log, a trace or a `ToString`, and neither does a request's signature.
Errors carry the service's code and request id, which is what support asks for and reveals nothing.

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
| `vortex.store.retries`, `vortex.store.throttles`, `vortex.store.hedges` | how much latency and money the retries and hedges cost |
| `vortex.store.connections_opened` | whether the pool holds its connections |
| `vortex.store.first_byte`, a histogram in milliseconds | the latency the window and the hedges are sized from |

Tags never carry a key, a bucket name or a token, so their cardinality stays bounded. Below them, the
platform's HTTP client reports its own requests, connections and durations through `System.Net.Http`,
which OpenTelemetry already collects. `CountingObjectStore` wraps any store as before and still counts
dependent steps, which no meter can.

## 15. How it is tested

- The store contract, `ObjectStoreContractTests`, extended with the revised members, runs against
  the memory store, the file store, MinIO and Azurite in CI, and against S3, R2, OVHcloud, Scaleway,
  Azure and Cloud Storage themselves on a schedule, with credentials the CI holds. Those runs settle
  every capability this document marks to check, and a profile changes only when they do.
- The protocol is tested against published vectors: AWS's Signature Version 4 test suite, the Shared
  Key examples of the Azure reference, the CRC-64/NVME and CRC-32C check values, and the structured
  body examples.
- A message handler given to the store's `HttpClient` in the tests answers with delays, resets, 503s,
  429s, 409s and 412s, short bodies and wrong `Content-Range`s, so every row of [errors](#13-errors)
  and of the retry table is exercised without a network, including the put whose answer is lost after
  the object was created.
- The XML reader, the JSON readers and the structured body's decoder are fuzzed: no input crashes
  them, hangs them, or makes them allocate past their bounds.
- Ratchets: requests and dependent steps per operation, held by `CountingObjectStore` as the dataset's
  budget tests are, bytes allocated per request, a constant held by a ceiling, and nothing allocated
  per batch by this library's code.
- Throughput is measured locally against MinIO and against each service by hand, one component at a
  time, never the full benchmark matrix.

## 16. The order of the work

1. The interface in the core, revised, with the memory, file and counting stores. The dataset passes
   the entry's length when it opens an object, and vacuum uses the listing's heads. These two save
   requests on every store, whatever the service.
2. The transport over `HttpClient` and the S3 read path: reads, suffix reads, heads, listings,
   Signature Version 4, credentials from code and the environment, and `s3://` locations. The bytes
   a request allocates are measured here, and their ceiling becomes a ratchet.
3. The S3 write path: puts, multipart uploads, conditions, checksums, deletes, and datasets on S3
   with the contract tests on MinIO and S3.
4. The profiles of R2, OVHcloud and Scaleway, with their contract tests, which settle what this
   document marks to check.
5. Azure, every operation, its signatures and tokens, and the structured body.
6. Cloud Storage: its generation preconditions, its JSON listings, metadata and batches, its XML
   reads and multipart uploads, and composition.
7. The credential chains: web identity, container credentials, instance metadata, workload and
   managed identities, and Google's application default credentials.
8. The reads in flight counted per request, the window in bytes, the first batch on two lanes, and
   hedging.
9. Appends to stored files, and directory buckets.

## 17. Decisions to take

| decision | recommended | why |
|---|---|---|
| the store interface moves into the core | yes | a single file needs it, and the dataset package is experimental, so nothing stable breaks |
| the package's name | `Vorticity.ObjectStorage` | it says what it holds, and leaves `Storage` free for anything local |
| the default session resolves locations from the environment | no, one line opts in | a library does not open connections or read credentials on a host that did not ask, as it does not take its cores |
| the platform's `HttpClient` rather than a client of this repository | the platform's | it reads bodies without an extra copy and exposes every control the stores need. Its allocation per request is a constant, and a client of our own would carry the risks the platform's has already paid for |
| R2, OVHcloud and Scaleway | profiles of the S3 store | they speak the API, and differ in a few capabilities that a profile states and the contract tests check |
| Cloud Storage | its own APIs rather than its S3 interoperability | generation preconditions, a listing that carries retention and holds, deletes in batches, the platform's identities, and composition |
| checksums on the clouds that share only `Content-MD5` | none by default | TLS already protects the bytes on their way, and MD5 costs a pass over every byte |
| a locked dataset on R2 or an OVHcloud Local Zone | refused | neither keeps an object's retention or legal hold where a head reports it |
| the listing returns heads, a change to the interface | yes | vacuum stops asking for a head per object, one awaited after another |
| a listing that lags, on a store that does not document its consistency | allowed, by less than the retention window | a lag costs a dataset freshness and never a version, so OVHcloud and Scaleway can host datasets |
| hedged reads on by default | no, an option with a budget | each hedge is a request that costs money, and the request ratchets must stay exact |
| part threshold and size | 32 MiB and 16 MiB, then measured | every service's minimum part is 5 MiB, and parallel parts are what fill a link |
| directory buckets | single files only | they list without order, which the dataset's discovery needs |

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
| a lease is created once, under a key naming the end of its span, and only vacuum deletes it, a window after that end | code: `CompactionSchedule.cs` (`CompactionLoop`), `DatasetVacuum.cs` |
| the sink hashes every object with XXH3-128 | code: `ObjectSegmentSink.cs` |
| the dataset's object source allocates per batch: a list, task arrays, a task per read, an array of ranges, a delegate | code: `ObjectSegmentSource.cs`, `ReadManyAsync` |
| `System.IO.Hashing`'s `Crc64` is ECMA-182's, not CRC-64/NVME | code: the documentation of `System.IO.Hashing` 10.0.12 |
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
| appends on directory buckets, 10 000 parts | source: Amazon S3 user guide, appending data to objects |
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
| Cloud Storage: composition of 1 to 32 sources, CRC-32C only | source: Cloud Storage documentation, composite objects |
| Cloud Storage: soft delete on by default for 7 days, the deleted objects billed meanwhile | source: Cloud Storage documentation, soft delete, updated 2026-10-07 |
| Google's application default credentials and their order | source: Google Cloud documentation, application default credentials |
| the platform's client allocates a few kilobytes per request | estimate, measured in the second phase of the work |
| a single object's first batch issues about 14 requests at degree 14, against 2 at degree 1 | measured in this repository in October 2026 |
| a window in bytes sized by bandwidth and latency, the first batch on two lanes | proposal, measured against the source with injected latency |
| part threshold, part size, parts in flight, connection limits, hedging percentile | proposal, starting points to set by measurement |
