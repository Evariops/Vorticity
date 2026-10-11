using System;
using Vorticity.IO;

namespace Vorticity.ObjectStorage;

/// <summary>The registrations of this package's stores in a session's <see cref="VortexSessionOptions.ObjectStores"/>.</summary>
public static class ObjectStoreRegistryExtensions
{
    /// <summary>
    /// Serves every <c>s3://</c> bucket with a store configured from the environment
    /// (<see cref="S3ObjectStore.FromEnvironment"/>), built on the bucket's first use and kept for the
    /// session's life: the AWS keys and region, and the endpoint of <c>AWS_ENDPOINT_URL_S3</c> or
    /// <c>AWS_ENDPOINT_URL</c>, which is how a host points at R2, OVHcloud or Scaleway.
    /// </summary>
    /// <param name="registry">The session's registry.</param>
    /// <returns>The registry, for chaining.</returns>
    /// <exception cref="ArgumentException">A scheme it registers has a builder already.</exception>
    public static ObjectStoreRegistry UseEnvironment(this ObjectStoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddScheme("s3", S3ObjectStore.FromEnvironment);
        return registry;
    }
}
