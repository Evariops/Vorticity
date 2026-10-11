using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>What authorizes one Azure request: a Shared Key account and key, a shared access signature, a bearer token, or nothing.</summary>
internal readonly record struct AzureAuthorization(string? Account, byte[]? Key, string? Sas, string? Bearer);

/// <summary>
/// How an Azure store authorizes its requests: a bearer token, a shared access signature, the account
/// key, or nothing, for a container open to anyone.
/// </summary>
internal abstract class AzureAuthorizer
{
    /// <summary>No authorization: requests go anonymous, as a container open to anyone takes them.</summary>
    internal static AzureAuthorizer None { get; } = new Fixed(default, "no credentials");

    /// <summary>What to authorize the next request with.</summary>
    /// <exception cref="ObjectStoreException">No token can be had.</exception>
    internal abstract ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Says that the service refused <paramref name="token"/>, read back from the request it refused,
    /// so that the next request takes another. A token renewed since is kept.
    /// </summary>
    internal virtual void Expired(string token)
    {
    }

    /// <summary>The account key, in the base64 the portal shows it in.</summary>
    /// <exception cref="ArgumentException">The key is not base64.</exception>
    internal static AzureAuthorizer SharedKey(string account, string accountKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ArgumentException.ThrowIfNullOrEmpty(accountKey);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(accountKey);
        }
        catch (FormatException invalid)
        {
            throw new ArgumentException("An account key is the base64 the portal shows.", nameof(accountKey), invalid);
        }

        return new Fixed(new AzureAuthorization(account, key, null, null), $"the key of the account {account}");
    }

    /// <summary>A shared access signature, appended to every request's query.</summary>
    internal static AzureAuthorizer SharedAccessSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(signature);
        return new Fixed(new AzureAuthorization(null, null, signature.TrimStart('?'), null), "a shared access signature");
    }

    /// <summary>Bearer tokens.</summary>
    internal static AzureAuthorizer Bearer(TokenSource tokens) => new Tokens(tokens);

    /// <summary>The same authorization for every request.</summary>
    private sealed class Fixed(AzureAuthorization authorization, string named) : AzureAuthorizer
    {
        internal override ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) => new ValueTask<AzureAuthorization>(authorization);

        public override string ToString() => named;
    }

    private sealed class Tokens(TokenSource tokens) : AzureAuthorizer
    {
        internal override async ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) =>
            new AzureAuthorization(null, null, null, await tokens.GetAsync(cancellationToken).ConfigureAwait(false));

        internal override void Expired(string token) => tokens.Expired(token);

        public override string ToString() => tokens.ToString()!;
    }
}
