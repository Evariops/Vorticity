using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>What signs an S3 store's requests: keys, kept or renewed, or none, for a public bucket.</summary>
internal abstract class AwsKeySource
{
    /// <summary>No keys: requests go unsigned, as a bucket open to anyone takes them.</summary>
    internal static AwsKeySource Unsigned { get; } = new UnsignedRequests();

    /// <summary>The keys to sign the next request with, or null to send it unsigned.</summary>
    /// <exception cref="ObjectStoreException">No keys can be had.</exception>
    internal abstract ValueTask<AwsKeys?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Says that the service found expired the keys whose session token is <paramref name="sessionToken"/>,
    /// read back from the request it refused, so that the next request takes others. Keys renewed since
    /// are kept.
    /// </summary>
    internal virtual void Expired(string sessionToken)
    {
    }

    /// <summary>The same keys for every request.</summary>
    internal sealed class Fixed(AwsKeys keys) : AwsKeySource
    {
        internal override ValueTask<AwsKeys?> GetAsync(CancellationToken cancellationToken) => new ValueTask<AwsKeys?>(keys);

        public override string ToString() => keys.ToString();
    }

    /// <summary>
    /// Keys from a source, kept until five minutes before they expire, or, without an expiry, until the
    /// service says they have.
    /// </summary>
    internal sealed class Renewed(Func<CancellationToken, ValueTask<AwsKeys>> source, string named, TimeProvider time) : AwsKeySource
    {
        private readonly Renewing<AwsKeys> _keys = new Renewing<AwsKeys>(source, static keys => keys.Expiration ?? DateTimeOffset.MaxValue, time);

        internal override async ValueTask<AwsKeys?> GetAsync(CancellationToken cancellationToken) =>
            await _keys.GetAsync(cancellationToken).ConfigureAwait(false);

        internal override void Expired(string sessionToken) =>
            _keys.Expired(sessionToken, static (keys, refused) => string.Equals(keys.SessionToken, refused, StringComparison.Ordinal));

        public override string ToString() => _keys.TryPeek(out AwsKeys? keys) ? $"{named} ({keys})" : named;
    }

    private sealed class UnsignedRequests : AwsKeySource
    {
        internal override ValueTask<AwsKeys?> GetAsync(CancellationToken cancellationToken) => new ValueTask<AwsKeys?>((AwsKeys?)null);

        public override string ToString() => "no credentials";
    }
}
