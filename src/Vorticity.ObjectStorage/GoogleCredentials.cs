using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage;

/// <summary>
/// How a Cloud Storage store authorizes its requests: OAuth 2.0 bearer tokens, from a source of the
/// caller's, or none, for an emulator or a public bucket. The store that is given them owns them.
/// </summary>
public abstract class GoogleCredentials : IDisposable
{
    private protected GoogleCredentials()
    {
    }

    /// <summary>Bearer tokens from a source of the caller's, asked again five minutes before the last expires.</summary>
    /// <param name="source">Gives a token for the scope <c>https://www.googleapis.com/auth/devstorage.read_write</c>.</param>
    /// <returns>The credentials.</returns>
    public static GoogleCredentials Token(Func<CancellationToken, ValueTask<AccessToken>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new TokenCredentials(new RenewingToken(source, TimeProvider.System));
    }

    /// <summary>No authorization: an emulator, or a bucket anyone may read.</summary>
    /// <returns>The credentials.</returns>
    public static GoogleCredentials Anonymous() => new AnonymousCredentials();

    /// <summary>
    /// Google's application default credentials, resolved at the first request, in their order: the file
    /// <c>GOOGLE_APPLICATION_CREDENTIALS</c> names (a service account's key, whose tokens are asked for
    /// with a JWT signed by its RSA key; a user's refresh token; or an external account's configuration
    /// for workload identity federation), the file <c>gcloud auth application-default login</c> writes,
    /// then the metadata server of Compute Engine, GKE and Cloud Run.
    /// </summary>
    /// <returns>The credentials.</returns>
    public static GoogleCredentials FromEnvironment() => new GoogleChain(CredentialHost.Real);

    /// <summary>The token to authorize the next request with, or null for none.</summary>
    internal abstract ValueTask<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Says that the service refused the last token, so that the next request takes another.</summary>
    internal virtual void Expired()
    {
    }

    /// <summary>Whether requests carry bearer tokens, which a refusal renews.</summary>
    internal virtual bool UsesTokens => false;

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

    /// <summary>Bearer tokens, renewed before they expire.</summary>
    internal sealed class TokenCredentials(RenewingToken tokens) : GoogleCredentials
    {
        internal override async ValueTask<string?> GetAsync(CancellationToken cancellationToken) =>
            await tokens.GetAsync(cancellationToken).ConfigureAwait(false);

        internal override void Expired() => tokens.Expired();

        internal override bool UsesTokens => true;

        protected override void Dispose(bool disposing) => tokens.Dispose();

        public override string ToString() => tokens.ExpiresOn is { } expires ? $"OAuth 2.0 tokens (one expiring {expires:O})" : "OAuth 2.0 tokens";
    }

    private sealed class AnonymousCredentials : GoogleCredentials
    {
        internal override ValueTask<string?> GetAsync(CancellationToken cancellationToken) => new ValueTask<string?>((string?)null);

        public override string ToString() => "no credentials";
    }
}
