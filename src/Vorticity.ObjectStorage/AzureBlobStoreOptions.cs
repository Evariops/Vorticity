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

    /// <summary>How requests are authorized, which the store owns from then on.</summary>
    public required AzureCredentials Credentials { get; init; }

    /// <summary>
    /// The blob endpoint, its account's path included for an emulator that addresses accounts by path.
    /// Null is the public cloud's, <c>https://account.blob.core.windows.net</c>; a sovereign cloud's is
    /// given here. Plain HTTP is taken on the loopback only.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>The connections, deadlines and retries.</summary>
    public StoreTransportOptions Transport { get; init; } = new StoreTransportOptions();

    /// <summary>The size past which a put goes up in blocks sent in parallel rather than whole, 32 MiB by default.</summary>
    public long PartThreshold { get; init; } = 32L << 20;

    /// <summary>The size of a block, 16 MiB by default; larger when an object would need more than 50 000.</summary>
    public int BlockSize { get; init; } = 16 << 20;

    /// <summary>The blocks of one upload in flight at once, 4 by default.</summary>
    public int BlocksInFlight { get; init; } = 4;

    /// <summary>
    /// Whether a put carries <c>x-ms-content-crc64</c>, the CRC-64 of its body, which the service checks
    /// before it stores the bytes. True by default.
    /// </summary>
    public bool SendChecksums { get; init; } = true;

    /// <summary>
    /// Whether a read asks for the structured body, in which the service follows every 4 MiB with its
    /// CRC-64, checked as the bytes arrive. False until the contract tests against the service prove it,
    /// a read then relying on TLS and the format's own checks, as on S3.
    /// </summary>
    public bool VerifyReads { get; init; }

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }
}
