using System;
using System.Net.Http;

namespace Vorticity.ObjectStorage;

/// <summary>An Azure Blob Storage store's account, container, credentials and connections.</summary>
public sealed class AzureBlobStoreOptions
{
    /// <summary>The storage account.</summary>
    public required string Account { get; init; }

    /// <summary>The container.</summary>
    public required string Container { get; init; }

    /// <summary>
    /// What authorizes the requests: tokens, a shared access signature, the account key, the
    /// environment's credentials, or none for a container anyone may read. Other stores may share them,
    /// and the store never disposes them.
    /// </summary>
    public required StoreCredentials Credentials { get; init; }

    /// <summary>
    /// The blob endpoint, its account's path included for an emulator that addresses accounts by path.
    /// Null is the public cloud's, <c>https://account.blob.core.windows.net</c>; a sovereign cloud's is
    /// given here. Plain HTTP is taken on the loopback only.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>The connections, deadlines and retries.</summary>
    public StoreTransportOptions Transport { get; init; } = new StoreTransportOptions();

    /// <summary>The size past which a put goes up in parts, Azure's blocks, sent in parallel rather than whole, 32 MiB by default.</summary>
    public long PartThreshold { get; init; } = 32L << 20;

    /// <summary>
    /// The size of a part, a block, 16 MiB by default; larger when a blob of known length would need more
    /// than 50 000, and growing with the parts of one of unknown length.
    /// </summary>
    public int PartSize { get; init; } = 16 << 20;

    /// <summary>The parts of one upload in flight at once, 4 by default, each held in memory until it is sent.</summary>
    public int PartsInFlight { get; init; } = 4;

    /// <summary>
    /// The checksum each put carries, which the service checks before it stores the bytes: by default
    /// <c>x-ms-content-crc64</c>, the CRC-64 of the body, otherwise <c>Content-MD5</c> or none.
    /// </summary>
    public PutChecksum Checksum { get; init; }

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }
}
