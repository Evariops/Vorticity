namespace Vorticity.ObjectStorage;

/// <summary>The checksum a put carries, which the service verifies before it stores the bytes, on every store.</summary>
public enum PutChecksum
{
    /// <summary>
    /// The service's own: CRC-64/NVME on Amazon S3 and on Azure, CRC-32C on Cloud Storage, and none on
    /// R2, OVHcloud, Scaleway and the other services that speak S3, whose bytes TLS protects.
    /// </summary>
    Default = 0,

    /// <summary>None: TLS protects the bytes on their way, and the format's own checks the rest.</summary>
    None = 1,

    /// <summary>
    /// <c>Content-MD5</c>, which every service checks, and the one checksum R2, OVHcloud and Scaleway
    /// share: a bucket under Object Lock there refuses a put without one, so a locked dataset there
    /// needs it. Never for security.
    /// </summary>
    ContentMd5 = 2,
}
