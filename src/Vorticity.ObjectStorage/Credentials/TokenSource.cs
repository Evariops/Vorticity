using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>The bearer tokens of a store's requests, or none, for an emulator or a public bucket.</summary>
internal abstract class TokenSource
{
    /// <summary>No token: requests go without authorization.</summary>
    internal static TokenSource None { get; } = new NoTokens();

    /// <summary>The token to authorize the next request with, or null for none.</summary>
    /// <exception cref="ObjectStoreException">No token can be had.</exception>
    internal abstract ValueTask<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Says that the service refused <paramref name="token"/>, read back from the request it refused,
    /// so that the next request takes another. A token renewed since is kept.
    /// </summary>
    internal virtual void Expired(string token)
    {
    }

    private sealed class NoTokens : TokenSource
    {
        internal override ValueTask<string?> GetAsync(CancellationToken cancellationToken) => new ValueTask<string?>((string?)null);

        public override string ToString() => "no credentials";
    }
}
