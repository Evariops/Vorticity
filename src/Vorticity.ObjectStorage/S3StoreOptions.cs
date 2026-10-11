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

    /// <summary>A handler to send through instead of the platform's: an emulator, in tests.</summary>
    internal HttpMessageHandler? Handler { get; init; }
}
