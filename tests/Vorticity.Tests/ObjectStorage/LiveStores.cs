// The services the store contract also runs against when the environment names them: a bucket of S3
// or of a service that speaks its API, an Azure container, a Cloud Storage bucket. Each store is built
// from the variables each service's own tools read, as UseEnvironment builds it. No emulator of this
// repository can prove what these runs prove: each was written from the same reading of the
// documentation as its store.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.Tests.Dataset;

namespace Vorticity.Tests.ObjectStorage;

internal static class LiveStores
{
    /// <summary>The variable that names the bucket or container of each live kind.</summary>
    internal static string VariableOf(string kind) => kind switch
    {
        "s3-live" => "VORTICITY_CONTRACT_S3",
        "azure-live" => "VORTICITY_CONTRACT_AZURE",
        "gcs-live" => "VORTICITY_CONTRACT_GCS",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a live store."),
    };

    /// <summary>Whether the environment names the bucket or container of <paramref name="kind"/>.</summary>
    internal static bool Named(string kind) => Variable(VariableOf(kind)) is not null;

    /// <summary>The live kinds the environment names, which the contract suite adds to its stores.</summary>
    internal static IEnumerable<string> Kinds()
    {
        foreach (string kind in (string[])["s3-live", "azure-live", "gcs-live"])
        {
            if (Named(kind))
            {
                yield return kind;
            }
        }
    }

    /// <summary>
    /// A view of <paramref name="kind"/>'s bucket under a prefix of its own, so that tests running at once
    /// never meet, which deletes what it wrote when it is disposed.
    /// </summary>
    /// <param name="kind">The live kind.</param>
    /// <param name="readsSuffix">Whether the store is told that the service reads suffixes, to prove it.</param>
    internal static IObjectStore Open(string kind, bool readsSuffix = false)
    {
        string bucket = Variable(VariableOf(kind))
            ?? throw new InvalidOperationException($"{VariableOf(kind)} names no bucket: the test should have skipped.");
        IObjectStore store = kind switch
        {
            "s3-live" => S3(bucket, readsSuffix),
            "azure-live" => AzureBlobObjectStore.FromEnvironment(bucket),
            _ => Google(bucket, readsSuffix),
        };
        return new Scratch(store);
    }

    /// <summary>An S3 store as <see cref="S3ObjectStore.FromEnvironment"/> builds it, suffix reads asserted when asked.</summary>
    private static S3ObjectStore S3(string bucket, bool readsSuffix)
    {
        string? endpoint = Variable("AWS_ENDPOINT_URL_S3") ?? Variable("AWS_ENDPOINT_URL");
        return new S3ObjectStore(new S3StoreOptions
        {
            Bucket = bucket,
            Region = Variable("AWS_REGION") ?? Variable("AWS_DEFAULT_REGION"),
            Endpoint = endpoint is null ? null : new Uri(endpoint, UriKind.Absolute),
            Credentials = StoreCredentials.FromEnvironment(),
            ReadsSuffix = readsSuffix ? true : null,
        });
    }

    /// <summary>A Cloud Storage store as <see cref="GoogleCloudStorageObjectStore.FromEnvironment"/> builds it, the emulator included.</summary>
    private static GoogleCloudStorageObjectStore Google(string bucket, bool readsSuffix)
    {
        string? emulator = Variable("STORAGE_EMULATOR_HOST");
        return new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
        {
            Bucket = bucket,
            Endpoint = emulator is null ? null : new Uri(emulator.Contains("://", StringComparison.Ordinal) ? emulator : "http://" + emulator),
            Credentials = emulator is null ? StoreCredentials.FromEnvironment() : StoreCredentials.Anonymous(),
            ReadsSuffix = readsSuffix ? true : null,
        });
    }

    private static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>A bucket under a fresh prefix, emptied and its store disposed with the view.</summary>
    private sealed class Scratch : ForwardingStore
    {
        private readonly IObjectStore _bucket;

        internal Scratch(IObjectStore bucket)
            : base(new PrefixedObjectStore(bucket, $"vorticity-contract/{Guid.NewGuid():N}/")) => _bucket = bucket;

        public override async ValueTask DisposeAsync()
        {
            try
            {
                List<string> keys = [];
                await foreach (ObjectListing listed in Inner.ListAsync(string.Empty, null, CancellationToken.None).ConfigureAwait(false))
                {
                    keys.Add(listed.Key);
                }

                if (keys.Count > 0)
                {
                    await Inner.DeleteAsync(keys, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                await _bucket.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
