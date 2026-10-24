# Files and datasets on S3, Azure and Cloud Storage

Open, write and scan files and datasets in a bucket of Amazon S3, Cloudflare R2, OVHcloud, Scaleway or
another service that speaks the S3 API, in a container of Azure Blob Storage, or in a bucket of Google
Cloud Storage, with the calls you use on a local path. The stores live in the `Vorticity.ObjectStorage`
package, written over the platform's HTTP client, with no cloud SDK.

```csharp
StoreCredentials environment = StoreCredentials.FromEnvironment();
await using S3ObjectStore lake = S3ObjectStore.FromEnvironment("lake", environment);
await using S3ObjectStore media = S3ObjectStore.ForR2(accountId, "media", StoreCredentials.AccessKey(r2KeyId, r2Secret));
await using AzureBlobObjectStore archive = new AzureBlobObjectStore(new AzureBlobStoreOptions
{
    Account = "contosoarchive",
    Container = "archive",
    Credentials = environment,
});
await using GoogleCloudStorageObjectStore warehouse = new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
{
    Bucket = "warehouse",
    Credentials = StoreCredentials.Token(tokenSource),
});

await using VortexSession session = VortexSession.Create(o =>
{
    o.ObjectStores.Add("s3://lake", lake);
    o.ObjectStores.Add("r2://media", media);
    o.ObjectStores.Add("az://archive", archive);
    o.ObjectStores.Add("gs://warehouse", warehouse);
});

await using (VortexFileWriter writer = session.CreateWriter<Reading>("s3://lake/readings/2026-10.vortex"))
{
    await writer.WriteAsync<Reading>(readings);
    await writer.CompleteAsync();
}

await using VortexFile file = await session.OpenAsync("s3://lake/readings/2026-10.vortex");
double? mean = await file.Scan<Reading>().Where(r => r.Day >= 900).AverageAsync(r => r.Celsius);

await using VortexDataset dataset = await VortexDataset.OpenAsync(
    "r2://media/tables/readings/", new DatasetOptions { Session = session });
```

## Locations

A location is a scheme, a bucket and a key: `s3://lake/readings/2026-10.vortex`. A session resolves it
with the stores its options hold, and a string without a scheme is a local path, as before. The
scheme is your choice, which keeps two buckets of one name at two providers apart, `r2://lake` beside
`s3://lake`. When two registered prefixes match a location, the longer one serves it, so a bucket can
be served by one store and one of its prefixes by another, with other credentials.

`VortexSession.Default` holds no store, and a location fails there with a message that says how to
register one. A library does not open connections or read credentials on a host that did not ask.
One line serves every `s3://` and `gs://` bucket and every `az://` container from the environment, a
store built on each one's first use:

```csharp
await using VortexSession session = VortexSession.Create(o => o.ObjectStores.UseEnvironment());
```

An `s3://` bucket takes the region of `AWS_REGION` and the endpoint of `AWS_ENDPOINT_URL_S3` or
`AWS_ENDPOINT_URL`. The endpoint is how a host points at R2, OVHcloud, Scaleway or another service
that speaks the S3 API, and the store takes its profile from the endpoint's host. An `az://` container
takes the account of `AZURE_STORAGE_ACCOUNT` or of a connection string. A `gs://` bucket takes Google's
endpoint, or the emulator `STORAGE_EMULATOR_HOST` names. Every store shares one set of the
environment's credentials, so that each service's keys or tokens are asked for once, however many
buckets the session reads.

## Credentials

`StoreCredentials` says how a store authorizes its requests, whichever service it speaks to, and a
store refuses when it is built the credentials its service does not take.

| credentials | S3 | Azure | Cloud Storage |
|---|---|---|---|
| `StoreCredentials.FromEnvironment()`, the chain each service's SDKs follow | yes | yes | yes |
| `StoreCredentials.AccessKey(accessKeyId, secretAccessKey)`, keys given in code | yes | | |
| `StoreCredentials.AccessKey(source)`, keys from a source of yours | yes | | |
| `StoreCredentials.SharedKey(account, accountKey)`, an account key | | yes | |
| `StoreCredentials.SharedAccessSignature(signature)` | | yes | |
| `StoreCredentials.Token(source)`, bearer tokens from a source of yours | | yes | yes |
| `StoreCredentials.Anonymous()`, for an emulator or a bucket anyone may read | yes | yes | yes |

`StoreCredentials.FromEnvironment()` resolves each service's chain at its first request:

| service | its sources, in order |
|---|---|
| S3 | `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`, then a web identity, `AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN` exchanged with STS, as a Kubernetes service account reaches a role on EKS, then a profile of `~/.aws/credentials` and `~/.aws/config` that holds keys or a web identity, then the container endpoint of ECS and EKS Pod Identity, then the instance metadata service, version 2 only |
| Azure | a client secret, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_CLIENT_SECRET`, then a workload identity, `AZURE_FEDERATED_TOKEN_FILE`, then the storage credentials Azure's tools read, `AZURE_STORAGE_CONNECTION_STRING`, or `AZURE_STORAGE_KEY` or `AZURE_STORAGE_SAS_TOKEN`, then a managed identity, App Service's or the instance's |
| Cloud Storage | the file `GOOGLE_APPLICATION_CREDENTIALS` names, a service account's key, a user's refresh token, or an external account's configuration for workload identity federation, then the file `gcloud auth application-default login` writes, then the metadata server of Compute Engine, GKE and Cloud Run |

The first source the environment configures is kept. Credentials that expire are renewed five minutes
before they do, by one request while the others go on with the current ones, and a token file that
Kubernetes rotates is read again at each renewal. An AWS profile that takes its keys from IAM Identity
Center, a credential process or another profile is refused with a message that says so: the AWS SDK's
own credentials serve it through `StoreCredentials.AccessKey(source)`. The container endpoint named by
`AWS_CONTAINER_CREDENTIALS_FULL_URI` is taken over HTTPS, or over HTTP on the loopback and the
container hosts of ECS and EKS only, so that its authorization token never leaves the host.

Keys and tokens from a source of yours are kept until five minutes before they expire, and asked for
again when the service refuses them as expired. Keys whose expiry the source does not say are kept
until then. No secret appears in an exception, a log or a trace, and a refusal names the kind of
credentials, the access key id on S3, and the service's request id.

Credentials hold no resource. Stores share them freely, and the stores that share a set renew its keys
and tokens once for all of them. The session disposes the stores `UseEnvironment` builds. A store you
register stays yours: dispose it after the session, which lets one store serve several sessions.
Dispose every file opened through the session before the session itself.

## What each operation costs

| operation | requests |
|---|---|
| open a file at a location | 1 on Amazon S3: the last 64 KiB and the object's length in one answer. 2 on Azure, which reads no suffix, and elsewhere until a test against the service proves that it answers one: the object's metadata, then the tail |
| a batch of a scan | the batch's segments coalesced into ranges of up to 16 MiB, issued together |
| open an object of a dataset | 1, its length known from the dataset's entry |
| write a file up to 32 MiB | 1, sent at completion |
| write a larger file | 1 to start an upload, its parts of 16 MiB sent in parallel as they fill, 1 to complete it |
| commit a dataset's object | at the commit, from the dataset's buffer where it lies: 1 put up to 32 MiB, past it 1 request to start an upload, its parts in parallel and 1 to complete it. A data object carries no condition, which its fresh uid makes needless |
| a dataset's vacuum | the listing's pages, a head only for an object old enough to go when an S3 bucket is under Object Lock (Azure's and Cloud Storage's listings say the locks), a request per 1 000 deletes on S3, per 256 on Azure and per 100 on Cloud Storage, where each delete is billed as a request |

A read lands in the block the scan decodes from, with no copy but the one TLS makes, and allocates a
constant, about 3 KB, whatever its size. A file opened at a location keeps the ETag of its first answer
and asks the service for it on every read, so an object replaced under an open file fails that file's
next read with a `VortexFormatException` rather than mixing two objects.

A file written to a location appears whole when its writer completes, or not at all, and replaces the
object that was there, as a file written to a path replaces it. A writer given up cancels its upload
at once, parts in flight included, and aborts it.

A file at a location is not appended to: an object cannot grow, so `session.OpenWriterAsync(location)`
throws a `NotSupportedException`. Write the file again, or keep rows that keep coming in a dataset,
which is made for them.

## Reads in flight

A file at a location is read as a file on disk is: the session's `MaxConcurrentReads` bounds the
batches in flight, 128 by default, which also bounds the memory their reads hold, and each batch issues
its coalesced ranges together. The store sends at most `StoreTransportOptions.MaxConnections` requests
at once, 64 by default, and a request past them waits for a connection.

A service that throttles, `SlowDown` on S3, `ServerBusy` on Azure, a 429 elsewhere, halves the
requests the store sends at once, once per episode: the requests in flight when it halved do not
halve it again. Once the service has been calm for 10 seconds, each success gives one back, up to
`StoreTransportOptions.MaxConnections`.

## The stores

`S3ObjectStore` takes its options, or comes from a factory:

| factory | for |
|---|---|
| `S3ObjectStore.FromEnvironment(bucket)` | the bucket the environment describes, on any service |
| `S3ObjectStore.ForR2(accountId, bucket, credentials)` | Cloudflare R2, signed for the region `auto` |
| `S3ObjectStore.ForOvhcloud(region, bucket, credentials)` | OVHcloud Object Storage, `gra` or `sbg` for instance |
| `S3ObjectStore.ForScaleway(region, bucket, credentials)` | Scaleway Object Storage, `fr-par` for instance |
| `new S3ObjectStore(new S3StoreOptions { ... })` | any other service that speaks the API, Ceph for instance |

On Amazon S3, a store given no region learns the bucket's from the bucket's own answer at the first
request. A bucket is addressed by host, `lake.s3.eu-west-3.amazonaws.com`, unless its name holds a
dot, or the endpoint is an address or the loopback, where it is addressed by path. The endpoint is
reached over HTTPS, and plain HTTP is taken on the loopback only, for an emulator.

A put carries a checksum the service verifies: CRC-64/NVME on Amazon S3, and none by default elsewhere,
the bytes being protected by TLS on their way. `S3StoreOptions.Checksum` chooses another,
`PutChecksum.ContentMd5`, which every service checks, or `PutChecksum.None`. A bucket under Object Lock
on OVHcloud or Scaleway refuses a put that carries no `Content-MD5`: set the option to
`PutChecksum.ContentMd5` for such a bucket, as the store's refusal says.

| | Amazon S3 | R2 | OVHcloud | Scaleway | another service |
|---|---|---|---|---|---|
| opens a file in one request (`ReadsSuffix`) | yes | once proven | once proven | once proven | once proven |
| hosts a dataset (`ListsInOrder`) | yes | yes | once its listing order is proven | yes | yes |
| hosts a dataset's writers (`CreatesConditionally`) | yes | yes | yes | yes | once proven |
| hosts a locked dataset (`KeepsLocks`) | yes | no, it keeps no Object Lock | once proven, never in a Local Zone | once proven | once proven |
| parts of an upload | 10 000 | 10 000, a conditional put whole | 10 000 | 1 000 | 10 000 |

What a service is taken to do comes from its profile, which the endpoint's host names. A capability
marked as proven later is taken as absent until a test against the service proves it, and the option
of `S3StoreOptions` named in the table sets it once you have checked it yourself.

`AzureBlobObjectStore` takes the account, the container and its credentials. Microsoft recommends a
bearer token from Microsoft Entra ID, which `StoreCredentials.Token` takes from a source of yours.
`StoreCredentials.SharedAccessSignature` and `StoreCredentials.SharedKey`, the account key, serve the
accounts that use them and the emulators. `AzureBlobObjectStore.FromConnectionString` takes the
account, the endpoint and the key or the signature from a connection string. Every put carries the
CRC-64 of its body, which the service checks, unless `AzureBlobStoreOptions.Checksum` chooses
`Content-MD5` or none, and a larger put goes up in blocks of 16 MiB sent in parallel, committed by
their list. A read relies on TLS and on the format's own checks, as on S3.

A directory bucket of S3 Express One Zone, whose name ends in `--x-s3`, is refused: it lists in no
order, signs with sessions of its own, and serves no dataset.

`GoogleCloudStorageObjectStore` speaks Cloud Storage's own APIs rather than its S3 interoperability,
since what a dataset relies on lies there. An object's token is its generation: a read is bound to it,
and a put is conditioned on it, a generation of 0 meaning that no object may have the name. Every put
carries the CRC-32C the service checks, unless `GoogleCloudStorageOptions.Checksum` chooses
`Content-MD5` or none. The service takes no precondition on an upload in parts, so a
conditional put goes in one request, and a dataset's data objects, which carry no condition, go up in
parts. A listing says each object's retention and holds. A deleted object
stays billed for the bucket's soft-delete period, 7 days by default, so vacuum frees storage a week
later there unless the bucket's period is 0. `StoreCredentials.Token` takes OAuth 2.0 tokens from a
source of yours, and `StoreCredentials.Anonymous` sends none, for an emulator.

## When the network fails

Every attempt has a deadline for the first byte of its answer, 15 seconds by default, counted from
the end of its body when it sends one. A body, sent or received, that goes 30 seconds without a byte
is abandoned, so that a slow link slows a put rather than failing it. A read, a head, a listing or a
delete is sent again after a failure of the service, a throttle, a lost connection or a missed
deadline, with an exponential and random delay, at most 5 times, while the store's retry budget
allows. The budget is spent by retries and refilled by successes, so a service that is down costs
each caller one attempt rather than a storm of retries. `StoreTransportOptions` sets the connections,
the deadlines and the attempts.

The `ObjectStoreException` a service's refusal becomes carries its HTTP status (`StatusCode`), the
service's code (`ErrorCode`, `AccessDenied` or `SlowDown`) and the id its support asks for
(`RequestId`), so a caller tells a refusal of its rights from a service that stays down without
reading the message.

A put conditioned on the key's absence, as a dataset's commit is, is never sent again blindly. A put
the service throttled, or whose body was cut short, was never applied, and goes again. When its answer
is lost after its body was sent, the store asks the object's head, whose metadata carries the put's
id: the put created the object, another writer did, or nothing is there and the put goes again. A
refusal that follows such a loss is checked the same way, since a slow service may apply the lost
attempt after the head, and the put then finds the key taken by itself. When the head fails too, the
store throws an `ObjectStoreException` whose `OutcomeUnknown` is true, and the caller decides.

A put of bytes already in memory, `PutAsync(key, ReadOnlySequence<byte>, condition)`, sends them
where they lie and sends them again from there. A put read from a stream holds what a retry needs, a
part in flight or the whole of a small put. S3 may refuse the completion of an upload and ask for it
to be made again, which a stream read once cannot do: the put then throws. Give the store
`StoreTransportOptions.SpoolDirectory` and every put read from a stream is written whole to a file
there first, sent from that file, sent again from it by any retry, of a part, of the object or of the
upload, and the file deleted when the put ends. The upload then starts once the stream has ended.

## Watch out

* The listings of OVHcloud are not documented to be in order and to honour `start-after`, which a
  dataset needs to find its latest version. A dataset refuses such a store until
  `S3StoreOptions.ListsInOrder` says the bucket does, once you have checked it.
* A dataset commits by a put that creates its key only while it is free, in one step. Amazon S3,
  R2, OVHcloud and Scaleway document it. Another service is taken not to: a dataset there reads, and
  refuses to write until `S3StoreOptions.CreatesConditionally` says the service does. Check it first
  with the store contract tests, the concurrent creation many times over, since a service that
  checked whether the key exists and then wrote would pass it once and fail when two puts meet.
* R2 and Cloud Storage take no condition on the parts of an upload, so a conditional put to them goes
  in one request, 5 GiB at most on R2. Read from a stream, it is held in memory up to
  `StoreTransportOptions.MaxHeldBytes`, 256 MiB by default, and refused past it, unless
  `SpoolDirectory` holds it on disk. A dataset's data object goes up in parts without the condition,
  which its key, ending with a fresh uid, makes needless.
* A dataset created with `LockedStore` needs a store that keeps locks. Among the S3 services, only
  Amazon S3 is taken to keep them: set `S3StoreOptions.KeepsLocks` once you have checked that the
  bucket does, never on R2, which offers no Object Lock, nor in an OVHcloud Local Zone, which keeps
  none.
* Parts in flight are held in memory, 4 of 16 MiB by default per upload (`PartsInFlight`, `PartSize`).
  The parts of an object of unknown length, a file written to a location, double every tenth of the
  parts the service takes, up to 128 MiB, so that 10 000 reach about 1 TiB on S3. R2's stay of one
  size, as R2 asks, which caps such an object at about 156 GiB there.
