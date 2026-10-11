using System;
using System.Net.Http;

namespace Vorticity.ObjectStorage;

/// <summary>Which service an S3 store speaks to, each with what it does differently.</summary>
public enum S3Profile
{
    /// <summary>Amazon S3.</summary>
    Amazon = 0,

    /// <summary>Cloudflare R2, signed for the region <c>auto</c>.</summary>
    R2 = 1,

    /// <summary>OVHcloud Object Storage.</summary>
    Ovhcloud = 2,

    /// <summary>Scaleway Object Storage.</summary>
    Scaleway = 3,

    /// <summary>Another store that speaks the API, Ceph for instance: its options say what it does.</summary>
    Other = 4,
}

/// <summary>The checksum a put carries, which the service verifies before it creates the object.</summary>
public enum S3Checksum
{
    /// <summary>The profile's: CRC-64/NVME on Amazon S3, none on the clouds that share only <c>Content-MD5</c>, whose bytes TLS already protects.</summary>
    Default = 0,

    /// <summary>None: TLS protects the bytes on their way, and the format's own checks the rest.</summary>
    None = 1,

    /// <summary>
    /// <c>Content-MD5</c>, the one checksum R2, OVHcloud and Scaleway share; a bucket under Object Lock
    /// refuses a put without a checksum, so a locked dataset there needs it. Never for security.
    /// </summary>
    ContentMd5 = 2,

    /// <summary>CRC-64/NVME, of a put sent whole or of the whole object of a multipart upload.</summary>
    Crc64Nvme = 3,
}

/// <summary>An S3 store's bucket, endpoint, keys and connections.</summary>
public sealed class S3StoreOptions
{
    /// <summary>The bucket.</summary>
    public required string Bucket { get; init; }

    /// <summary>The keys, which the store owns from then on and wipes when it is disposed.</summary>
    public required S3Credentials Credentials { get; init; }

    /// <summary>
    /// The region requests are signed for. Null takes it, on Amazon S3, from the bucket's own answer
    /// at the first request, and is <c>auto</c> on R2.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// The service's endpoint, <c>https://</c> and nothing after the host and port. Null is Amazon
    /// S3's for the region. Plain <c>http://</c> is taken only on the loopback, for an emulator.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Whether the bucket is addressed by path, <c>endpoint/bucket/key</c>, rather than by host,
    /// <c>bucket.endpoint/key</c>. Null decides: by path on an endpoint that is an address or the
    /// loopback, or for a bucket whose name holds a dot, which a certificate does not cover.
    /// </summary>
    public bool? PathStyle { get; init; }

    /// <summary>The service, or null to take it from the endpoint's host.</summary>
    public S3Profile? Profile { get; init; }

    /// <summary>The connections, deadlines and retries.</summary>
    public StoreTransportOptions Transport { get; init; } = new StoreTransportOptions();

    /// <summary>
    /// Whether the service answers a suffix range (<c>bytes=-n</c>) with the object's length, which
    /// opens a file of unknown length in one request. Null takes the profile's: proven on Amazon S3,
    /// taken as absent elsewhere until the contract tests prove it.
    /// </summary>
    public bool? ReadsSuffix { get; init; }

    /// <summary>
    /// Whether the service lists in ordinal order and honours <c>start-after</c>, which a dataset needs.
    /// Null takes the profile's.
    /// </summary>
    public bool? ListsInOrder { get; init; }

    /// <summary>
    /// Whether the service keeps objects under Object Lock's retention and legal hold, which a dataset
    /// created for a locked store needs. Null takes the profile's: no on R2, yes elsewhere. Set it false
    /// for an OVHcloud Local Zone, which keeps neither.
    /// </summary>
    public bool? KeepsLocks { get; init; }

    /// <summary>The checksum each put carries; <see cref="S3Checksum.Default"/> takes the profile's.</summary>
    public S3Checksum Checksum { get; init; }

    /// <summary>
    /// The size past which a put goes up in parts sent in parallel rather than whole, 32 MiB by default.
    /// A put whose condition the service takes on no part, on R2, goes whole whatever its size.
    /// </summary>
    public long PartThreshold { get; init; } = 32L << 20;

    /// <summary>
    /// The size of a part, 16 MiB by default and at least 5 MiB; larger when the object would otherwise
    /// need more parts than the service takes.
    /// </summary>
    public int PartSize { get; init; } = 16 << 20;

    /// <summary>The parts of one upload in flight at once, 4 by default, each held in memory until it is sent.</summary>
    public int PartsInFlight { get; init; } = 4;

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }

    /// <summary>The smallest part a service takes, but the last: 5 MiB, which tests lower.</summary>
    internal int MinimumPartSize { get; init; } = 5 << 20;
}
