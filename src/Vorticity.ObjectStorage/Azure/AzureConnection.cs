using System;
using System.Collections.Generic;

namespace Vorticity.ObjectStorage.Azure;

/// <summary>The parts of an Azure Storage connection string a blob store needs.</summary>
internal sealed record AzureConnection(string Account, Uri? BlobEndpoint, string? AccountKey, string? Sas)
{
    /// <exception cref="ArgumentException">The connection string names no account.</exception>
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

    /// <summary>What authorizes the requests: the account key, the shared access signature, or null when the string holds neither.</summary>
    internal StoreCredentials? Credentials() =>
        AccountKey is { } key ? StoreCredentials.SharedKey(Account, key)
        : Sas is { } sas ? StoreCredentials.SharedAccessSignature(sas)
        : null;
}
