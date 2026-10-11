# Files and datasets on S3, Azure and Cloud Storage

Open, write and scan files and datasets in a bucket of Amazon S3, Cloudflare R2, OVHcloud, Scaleway or
another service that speaks the S3 API, in a container of Azure Blob Storage, or in a bucket of Google
Cloud Storage, with the calls you use on a local path. The stores live in the `Vorticity.ObjectStorage`
package, written over the platform's HTTP client, with no cloud SDK.

```csharp
await using VortexSession session = VortexSession.Create(o =>
{
    o.ObjectStores.Add("s3://lake", S3ObjectStore.FromEnvironment("lake"));
    o.ObjectStores.Add("r2://media", S3ObjectStore.ForR2(accountId, "media", S3Credentials.FromEnvironment()));
    o.ObjectStores.Add("az://archive", new AzureBlobObjectStore(new AzureBlobStoreOptions
    {
        Account = "contosoarchive",
        Container = "archive",
        Credentials = AzureCredentials.FromEnvironment(),
    }));
    o.ObjectStores.Add("gs://warehouse", new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
    {
        Bucket = "warehouse",
        Credentials = GoogleCredentials.Token(tokenSource),
    }));
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
One line serves every `s3://` bucket and every `az://` container from the environment, a store built
on each one's first use:

```csharp
await using VortexSession session = VortexSession.Create(o => o.ObjectStores.UseEnvironment());
```

An `s3://` bucket takes the region of `AWS_REGION`, the endpoint of `AWS_ENDPOINT_URL_S3` or
`AWS_ENDPOINT_URL`, and the AWS credential chain. The endpoint is how a host points at R2, OVHcloud,
Scaleway or another service, and the store takes its profile from the endpoint's host. An `az://` container takes
the account of `AZURE_STORAGE_ACCOUNT` or of a connection string, and Azure's chain. A `gs://` bucket
takes Google's application default credentials, or the emulator `STORAGE_EMULATOR_HOST` names.

| chain | its sources, in order |
|---|---|
| AWS (`S3Credentials.Default()`) | `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`; a web identity, `AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN` exchanged with STS, as a Kubernetes service account reaches a role on EKS; a profile of `~/.aws/credentials` and `~/.aws/config`; the container endpoint of ECS and EKS Pod Identity; the instance metadata service, version 2 only |
| Azure (`AzureCredentials.FromEnvironment()`) | a client secret, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_CLIENT_SECRET`; a workload identity, `AZURE_FEDERATED_TOKEN_FILE`; the storage credentials Azure's tools read, `AZURE_STORAGE_CONNECTION_STRING`, or `AZURE_STORAGE_KEY` or `AZURE_STORAGE_SAS_TOKEN`; a managed identity, App Service's or the instance's |
| Google (`GoogleCredentials.FromEnvironment()`) | the file `GOOGLE_APPLICATION_CREDENTIALS` names: a service account's key, a user's refresh token, or an external account's configuration for workload identity federation; the file `gcloud auth application-default login` writes; the metadata server of Compute Engine, GKE and Cloud Run |

The first source that answers is kept. Credentials that expire are renewed five minutes before they
do, by one request while the others go on with the current ones, and a token file that Kubernetes
rotates is read again at each renewal.

The session disposes the stores it holds, closing their connections and wiping their keys. Dispose
every file opened through the session before the session itself.

## What each operation costs

| operation | requests |
|---|---|
| open a file at a location | 1 on Amazon S3: the last 64 KiB and the object's length in one answer. 2 on Azure, which reads no suffix, and elsewhere until the contract tests prove that the service answers one: the object's metadata, then the tail |
| a batch of a scan | the batch's segments coalesced into ranges, issued together |
| open an object of a dataset | 1, its length known from the dataset's entry |
| write a file up to 32 MiB | 1, sent at completion |
| write a larger file | 1 to start an upload, its parts of 16 MiB sent in parallel as they fill, 1 to complete it |
| a dataset's vacuum | the listing's pages, a head only for an object old enough to go when an S3 bucket is under Object Lock (Azure's and Cloud Storage's listings say the locks), a request per 1 000 deletes on S3, per 256 on Azure and per 100 on Cloud Storage, where each delete is billed as a request |

A read lands in the block the scan decodes from, with no copy but the one TLS makes, and allocates a
constant, about 3 KB, whatever its size. A file opened at a location keeps the ETag of its first answer
and asks the service for it on every read, so an object replaced under an open file fails that file's
next read with a `VortexFormatException` rather than mixing two objects.

A file written to a location appears whole when its writer completes, or not at all, and replaces the
object that was there, as a file written to a path replaces it. A writer given up aborts its upload.

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

Keys come from code, `S3Credentials.Static(accessKeyId, secretAccessKey)`, or from the environment,
`S3Credentials.FromEnvironment()`. The store copies the secret into native memory and wipes it when it
is disposed. No secret appears in an exception, a log or a trace, and a refusal names the access key
id and the service's request id.

A put carries a checksum the service verifies: CRC-64/NVME on Amazon S3, and none by default on R2,
OVHcloud and Scaleway, whose bytes TLS protects on their way. A bucket under Object Lock refuses a put
without a checksum, and the store then sends `Content-MD5` with that put again and with every put after
it. `S3StoreOptions.Checksum` chooses one from the start.

| | Amazon S3 | R2 | OVHcloud | Scaleway | another service |
|---|---|---|---|---|---|
| opens a file in one request | yes | once proven | once proven | once proven | yes |
| hosts a dataset | yes | yes | once its listing order is proven (`ListsInOrder`) | yes | yes |
| hosts a locked dataset | yes | no, it keeps no Object Lock | yes, not in a Local Zone (`KeepsLocks`) | yes | yes |
| parts of an upload | 10 000 | 10 000, a conditional put whole | 10 000 | 1 000 | 10 000 |

What a service is taken to do comes from its profile, which the endpoint's host names. A capability
marked as proven later is taken as absent until the contract tests have run against the service, and
an option of `S3StoreOptions` overrides it once you have checked it yourself.

`AzureBlobObjectStore` takes the account, the container and its credentials. Microsoft recommends a
bearer token from Microsoft Entra ID, which `AzureCredentials.Token` takes from a source of yours and
renews five minutes before it expires. `AzureCredentials.SharedAccessSignature` and
`AzureCredentials.SharedKey`, the account key, serve the accounts that use them and the emulators, and
`AzureCredentials.FromConnectionString` reads either from a connection string. Every put carries the
CRC-64 of its body, which the service checks, and a larger put goes up in blocks of 16 MiB sent in
parallel, committed by their list. `VerifyReads` asks the service to follow every 4 MiB of a read with
its CRC-64, checked as the bytes arrive, once the contract tests have proven it against the service.

`GoogleCloudStorageObjectStore` speaks Cloud Storage's own APIs rather than its S3 interoperability,
since what a dataset relies on lies there. An object's token is its generation: a read is bound to it,
and a put is conditioned on it, a generation of 0 meaning that no object may have the name. Every put
carries the CRC-32C the service checks. The service takes no precondition on an upload in parts, so a
conditional put goes in one request, held in memory up to 5 GiB, and a dataset's data objects, which
carry no condition, go up in parts. A listing says each object's retention and holds. A deleted object
stays billed for the bucket's soft-delete period, 7 days by default, so vacuum frees storage a week
later there unless the bucket's period is 0. `GoogleCredentials.Token` takes OAuth 2.0 tokens from a
source of yours, and `GoogleCredentials.Anonymous` sends none, for an emulator.

## When the network fails

Every attempt has a deadline for its first byte, 15 seconds by default, and a body that goes 30
seconds without a byte is abandoned. A read, a head, a listing or a delete is sent again after a
failure of the service, a throttle, a lost connection or a missed deadline, with an exponential and
random delay, at most 5 times, while the store's retry budget allows. The budget is spent by retries
and refilled by successes, so a service that is down costs each caller one attempt rather than a storm
of retries. `StoreTransportOptions` sets the connections, the deadlines and the attempts.

A put conditioned on the key's absence, as a dataset's commit is, is never sent again blindly. When
its answer is lost, the store asks the object's head, whose metadata carries the put's id: the put
created the object, another writer did, or nothing is there and the put goes again. When the head
fails too, the store throws an `ObjectStoreException` whose `OutcomeUnknown` is true, and the caller
decides.

## Watch out

* The listings of OVHcloud are not documented to be in order and to honour `start-after`, which a
  dataset needs to find its latest version. A dataset refuses such a store until
  `S3StoreOptions.ListsInOrder` says the bucket does, once you have checked it.
* R2 takes no condition on the parts of an upload, so a conditional put to R2 goes in one request,
  which caps it at 5 GiB. A dataset's data objects are not conditional and go up in parts.
* A dataset created with `LockedStore` needs a store that keeps locks, and refuses R2, which offers no
  Object Lock, and an OVHcloud Local Zone once its store says `KeepsLocks = false`.
* Parts in flight are held in memory, 4 of 16 MiB by default per upload (`PartsInFlight`, `PartSize`).
