using System;
using System.Net.Http;

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
    public long PartThreshold { get; init; } = 32L << 20;

    /// <summary>The size of a part, 16 MiB by default.</summary>
    public int PartSize { get; init; } = 16 << 20;

    /// <summary>The parts of one upload in flight at once, 4 by default.</summary>
    public int PartsInFlight { get; init; } = 4;

    /// <summary>
    /// Whether the service answers a suffix range (<c>bytes=-n</c>) with the object's length, which opens
    /// a file of unknown length in one request. False until the contract tests against the service
    /// prove it: the object's metadata comes first.
    /// </summary>
    public bool ReadsSuffix { get; init; }

    /// <summary>Whether a put carries the CRC-32C of its body, which the service checks. True by default.</summary>
    public bool SendChecksums { get; init; } = true;

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }
}
