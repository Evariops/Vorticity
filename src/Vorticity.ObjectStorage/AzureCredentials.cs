using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage;

/// <summary>A bearer token, from Microsoft Entra ID or Google's OAuth 2.0, and when it expires.</summary>
/// <param name="Value">The token.</param>
/// <param name="ExpiresOn">When it expires; the store asks for another five minutes before.</param>
public readonly record struct AccessToken(string Value, DateTimeOffset ExpiresOn)
{
    /// <summary>The token as a message may name it: its expiry, never its value.</summary>
    public override string ToString() => $"a token expiring {ExpiresOn:O}";
}

/// <summary>
/// How an Azure store authorizes its requests: a bearer token from Microsoft Entra ID, which Microsoft
/// recommends, a shared access signature, or the account key. The store that is given them owns them,
/// and wipes a key when it is disposed.
/// </summary>
public abstract class AzureCredentials : IDisposable
{
    private protected AzureCredentials()
    {
    }

    /// <summary>The account key, for an emulator and for the accounts that still allow Shared Key.</summary>
    /// <param name="account">The storage account.</param>
    /// <param name="accountKey">The key, in the base64 the portal shows it in; decoded into native memory at once.</param>
    /// <returns>The credentials.</returns>
    public static AzureCredentials SharedKey(string account, string accountKey) => new KeyCredentials(account, accountKey);

    /// <summary>A shared access signature, appended to every request's query.</summary>
    /// <param name="signature">The signature's query, with or without its leading <c>?</c>.</param>
    /// <returns>The credentials.</returns>
    public static AzureCredentials SharedAccessSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(signature);
        return new SasCredentials(signature.TrimStart('?'));
    }

    /// <summary>Bearer tokens from a source of the caller's, asked again five minutes before the last expires.</summary>
    /// <param name="source">Gives a token for the scope <c>https://storage.azure.com/.default</c>.</param>
    /// <returns>The credentials.</returns>
    public static AzureCredentials Token(Func<CancellationToken, ValueTask<AccessToken>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new TokenCredentials(new RenewingToken(source, TimeProvider.System));
    }

    /// <summary>The account key or the shared access signature a connection string holds.</summary>
    /// <param name="connectionString">A connection string, as the portal shows it.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="ArgumentException">The connection string holds neither.</exception>
    public static AzureCredentials FromConnectionString(string connectionString)
    {
        AzureConnection connection = AzureConnection.Parse(connectionString);
        return connection.AccountKey is { } key ? new KeyCredentials(connection.Account, key)
            : connection.Sas is { } sas ? new SasCredentials(sas)
            : throw new ArgumentException("The connection string holds neither an account key nor a shared access signature.", nameof(connectionString));
    }

    /// <summary>
    /// The credentials of the environment, as Azure's tools read them: <c>AZURE_STORAGE_CONNECTION_STRING</c>,
    /// or <c>AZURE_STORAGE_KEY</c> with <c>AZURE_STORAGE_ACCOUNT</c>, or <c>AZURE_STORAGE_SAS_TOKEN</c>.
    /// </summary>
    /// <returns>The credentials.</returns>
    /// <exception cref="ObjectStoreException">The environment holds none.</exception>
    public static AzureCredentials FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") is { Length: > 0 } connection)
        {
            return FromConnectionString(connection);
        }

        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY") is { Length: > 0 } key
            && Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT") is { Length: > 0 } account)
        {
            return SharedKey(account, key);
        }

        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_SAS_TOKEN") is { Length: > 0 } sas)
        {
            return SharedAccessSignature(sas);
        }

        throw new ObjectStoreException(
            "No Azure credentials in the environment: set AZURE_STORAGE_CONNECTION_STRING, AZURE_STORAGE_ACCOUNT and AZURE_STORAGE_KEY, or AZURE_STORAGE_SAS_TOKEN, or give them in code with AzureCredentials.");
    }

    /// <summary>What to authorize the next request with.</summary>
    internal abstract ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken);

    /// <summary>Says that the service refused the last token, so that the next request takes another.</summary>
    internal virtual void Expired()
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

    private sealed class KeyCredentials : AzureCredentials
    {
        private readonly AzureAuthorization _authorization;

        internal KeyCredentials(string account, string accountKey)
        {
            ArgumentException.ThrowIfNullOrEmpty(account);
            ArgumentException.ThrowIfNullOrEmpty(accountKey);
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(accountKey);
            }
            catch (FormatException invalid)
            {
                throw new ArgumentException("An account key is the base64 the portal shows.", nameof(accountKey), invalid);
            }

            try
            {
                _authorization = new AzureAuthorization(account, new NativeSecret(decoded), null, null);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            }
        }

        internal override ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) => new ValueTask<AzureAuthorization>(_authorization);

        protected override void Dispose(bool disposing) => _authorization.Key!.Dispose();

        public override string ToString() => $"the key of the account {_authorization.Account}";
    }

    private sealed class SasCredentials(string sas) : AzureCredentials
    {
        internal override ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) =>
            new ValueTask<AzureAuthorization>(new AzureAuthorization(null, null, sas, null));

        public override string ToString() => "a shared access signature";
    }

    /// <summary>Bearer tokens, renewed before they expire.</summary>
    internal sealed class TokenCredentials(RenewingToken tokens) : AzureCredentials
    {
        internal override async ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) =>
            new AzureAuthorization(null, null, null, await tokens.GetAsync(cancellationToken).ConfigureAwait(false));

        internal override void Expired() => tokens.Expired();

        protected override void Dispose(bool disposing) => tokens.Dispose();

        public override string ToString() => tokens.ExpiresOn is { } expires ? $"Entra ID tokens (one expiring {expires:O})" : "Entra ID tokens";
    }
}

/// <summary>What authorizes one request: a Shared Key account and key, a shared access signature, or a bearer token.</summary>
internal readonly record struct AzureAuthorization(string? Account, NativeSecret? Key, string? Sas, string? Bearer);

/// <summary>The parts of an Azure Storage connection string a blob store needs.</summary>
internal sealed record AzureConnection(string Account, Uri? BlobEndpoint, string? AccountKey, string? Sas)
{
    internal static AzureConnection Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                parts[part[..equals]] = part[(equals + 1)..];
            }
        }

        Uri? endpoint = parts.TryGetValue("BlobEndpoint", out string? blob) ? new Uri(blob.TrimEnd('/')) : null;
        string? account = parts.GetValueOrDefault("AccountName")
            ?? (endpoint is not null && endpoint.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                ? endpoint.Host[..endpoint.Host.IndexOf('.', StringComparison.Ordinal)]
                : null);
        if (account is null)
        {
            throw new ArgumentException("The connection string names no account.", nameof(connectionString));
        }

        if (endpoint is null && parts.TryGetValue("EndpointSuffix", out string? suffix))
        {
            string protocol = parts.GetValueOrDefault("DefaultEndpointsProtocol", "https");
            endpoint = new Uri($"{protocol}://{account}.blob.{suffix}");
        }

        return new AzureConnection(account, endpoint, parts.GetValueOrDefault("AccountKey"), parts.GetValueOrDefault("SharedAccessSignature"));
    }
}
