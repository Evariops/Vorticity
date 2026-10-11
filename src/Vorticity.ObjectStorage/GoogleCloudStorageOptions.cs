using System;
using System.Net.Http;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage;

/// <summary>A Cloud Storage store's bucket, credentials and connections.</summary>
public sealed class GoogleCloudStorageOptions
{
    /// <summary>The bucket.</summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// What authorizes the requests: tokens, the environment's credentials, or none for an emulator or
    /// a bucket anyone may read. Other stores may share them, and the store never disposes them.
    /// </summary>
    public required StoreCredentials Credentials { get; init; }

    /// <summary>
    /// The endpoint, <c>https://storage.googleapis.com</c> when null, or an emulator's on the loopback,
    /// where plain HTTP is taken.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>The connections, deadlines and retries.</summary>
    public StoreTransportOptions Transport { get; init; } = new StoreTransportOptions();

    /// <summary>
    /// The size past which a put that carries no condition goes up in parts sent in parallel, 32 MiB by
    /// default. The service takes no condition on an upload in parts, so a conditional put goes whole.
    /// </summary>
    public long PartThreshold { get; init; } = UploadLimits.DefaultPartThreshold;

    /// <summary>
    /// The size of a part, 16 MiB by default; larger when an object would need more than 10 000, and
    /// growing with the parts of one of unknown length.
    /// </summary>
    public int PartSize { get; init; } = UploadLimits.DefaultPartSize;

    /// <summary>The parts of one upload in flight at once, 4 by default, each held in memory until it is sent.</summary>
    public int PartsInFlight { get; init; } = UploadLimits.DefaultPartsInFlight;

    /// <summary>
    /// Whether the service answers a suffix range (<c>bytes=-n</c>) with the object's length, which opens
    /// a file of unknown length in one request. Null takes it as absent until the contract tests against
    /// the service prove it: the object's metadata comes first.
    /// </summary>
    public bool? ReadsSuffix { get; init; }

    /// <summary>
    /// The checksum each put carries, which the service checks before it stores the bytes: by default
    /// the CRC-32C of the body in <c>x-goog-hash</c>, otherwise <c>Content-MD5</c> or none.
    /// </summary>
    public PutChecksum Checksum { get; init; }

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }
}
