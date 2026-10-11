using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage;

/// <summary>
/// Where an S3 store's keys come from: given in code, or read from the environment. The store that is
/// given them owns them, and wipes their secret when it is disposed.
/// </summary>
/// <remarks>
/// Cloudflare R2, OVHcloud and Scaleway issue key pairs of the same shape, an access key and a secret,
/// which come from code or from the same environment variables.
/// </remarks>
public abstract class S3Credentials : IDisposable
{
    private protected S3Credentials()
    {
    }

    /// <summary>Keys given in code.</summary>
    /// <param name="accessKeyId">The access key id.</param>
    /// <param name="secretAccessKey">The secret access key, copied into native memory at once.</param>
    /// <param name="sessionToken">The session token of temporary keys, or null.</param>
    /// <returns>The credentials.</returns>
    public static S3Credentials Static(string accessKeyId, string secretAccessKey, string? sessionToken = null) =>
        new StaticCredentials(new AwsKeys(accessKeyId, secretAccessKey, sessionToken));

    /// <summary>
    /// The keys of the environment, read at the first request: <c>AWS_ACCESS_KEY_ID</c>,
    /// <c>AWS_SECRET_ACCESS_KEY</c> and <c>AWS_SESSION_TOKEN</c>.
    /// </summary>
    /// <returns>The credentials.</returns>
    public static S3Credentials FromEnvironment() => new EnvironmentCredentials();

    /// <summary>The keys to sign the next request with.</summary>
    /// <exception cref="ObjectStoreException">No keys can be had.</exception>
    internal abstract ValueTask<AwsKeys> GetAsync(CancellationToken cancellationToken);

    /// <summary>Says that the service found <paramref name="keys"/> expired, so that the next request takes new ones.</summary>
    internal virtual void Expired(AwsKeys keys)
    {
    }

    /// <summary>Wipes the secrets held.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Wipes the secrets held.</summary>
    /// <param name="disposing">Whether called by <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    private sealed class StaticCredentials(AwsKeys keys) : S3Credentials
    {
        internal override ValueTask<AwsKeys> GetAsync(CancellationToken cancellationToken) => new ValueTask<AwsKeys>(keys);

        protected override void Dispose(bool disposing) => keys.Dispose();

        public override string ToString() => $"keys given in code ({keys})";
    }

    private sealed class EnvironmentCredentials : S3Credentials
    {
        private AwsKeys? _keys;

        internal override ValueTask<AwsKeys> GetAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _keys) is { } known)
            {
                return new ValueTask<AwsKeys>(known);
            }

            string? id = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            string? secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            {
                throw new ObjectStoreException(
                    "No S3 keys in the environment: set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY, or give the keys in code with S3Credentials.Static.");
            }

            AwsKeys keys = new AwsKeys(id, secret, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
            if (Interlocked.CompareExchange(ref _keys, keys, null) is { } raced)
            {
                keys.Dispose();
                return new ValueTask<AwsKeys>(raced);
            }

            return new ValueTask<AwsKeys>(keys);
        }

        protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _keys, null)?.Dispose();

        public override string ToString() => _keys is { } keys ? $"the environment's keys ({keys})" : "the environment's keys";
    }
}
