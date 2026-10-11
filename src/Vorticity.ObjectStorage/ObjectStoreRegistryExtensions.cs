using System;
using Vorticity.IO;

namespace Vorticity.ObjectStorage;

/// <summary>The registrations of this package's stores in a session's <see cref="VortexSessionOptions.ObjectStores"/>.</summary>
public static class ObjectStoreRegistryExtensions
{
    /// <summary>
    /// Serves every <c>s3://</c> and <c>gs://</c> bucket and every <c>az://</c> container with a store
    /// configured from the environment, built on its first use, kept for the session's life and
    /// disposed with it. An <c>s3://</c> bucket takes the AWS region and the endpoint of
    /// <c>AWS_ENDPOINT_URL_S3</c> or <c>AWS_ENDPOINT_URL</c>, which is how a host points at R2,
    /// OVHcloud or Scaleway (<see cref="S3ObjectStore.FromEnvironment"/>). An <c>az://</c> container
    /// takes the account of Azure's environment (<see cref="AzureBlobObjectStore.FromEnvironment"/>). A
    /// <c>gs://</c> bucket takes Google's endpoint, or the emulator the environment names
    /// (<see cref="GoogleCloudStorageObjectStore.FromEnvironment"/>). Every store shares one set of the
    /// environment's credentials (<see cref="StoreCredentials.FromEnvironment"/>), so that each service's
    /// keys or tokens are asked for once, however many buckets the session reads.
    /// </summary>
    /// <param name="registry">The session's registry.</param>
    /// <returns>The registry, for chaining.</returns>
    /// <exception cref="ArgumentException">A scheme it registers has a builder already.</exception>
    public static ObjectStoreRegistry UseEnvironment(this ObjectStoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        StoreCredentials credentials = StoreCredentials.FromEnvironment();
        registry.AddScheme("s3", bucket => S3ObjectStore.FromEnvironment(bucket, credentials));
        registry.AddScheme("az", container => AzureBlobObjectStore.FromEnvironment(container, credentials));
        registry.AddScheme("gs", bucket => GoogleCloudStorageObjectStore.FromEnvironment(bucket, credentials));
        return registry;
    }
}
