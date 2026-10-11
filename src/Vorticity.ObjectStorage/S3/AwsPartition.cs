using System;

namespace Vorticity.ObjectStorage.S3;

/// <summary>The partition of an AWS region, which names the domain of its endpoints.</summary>
internal static class AwsPartition
{
    /// <summary>The domain of the endpoints of <paramref name="region"/>: <c>amazonaws.com.cn</c> in China, <c>amazonaws.com</c> elsewhere.</summary>
    internal static string DomainOf(string region) =>
        region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
}
