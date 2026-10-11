using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// Azure's chain: a client secret in the environment, a workload identity, the storage credentials
/// the environment names (a connection string, an account key, a shared access signature), then a
/// managed identity, App Service's endpoint or the instance metadata service. The first source that
/// answers is kept, and its tokens renewed five minutes before they expire.
/// </summary>
/// <remarks>
/// The storage credentials the environment names come before the managed identity, so that a host that
/// sets them never waits for a metadata service it does not have.
/// </remarks>
internal sealed class AzureChain : AzureCredentials
{
    private const string Scope = "https://storage.azure.com/.default";
    private const string Resource = "https://storage.azure.com/";

    private readonly CredentialHost _host;
    private readonly object _gate = new object();
    private AzureCredentials? _resolved;
    private string _named = "the Azure credential chain";

    internal AzureChain(CredentialHost host) => _host = host;

    internal override ValueTask<AzureAuthorization> GetAsync(CancellationToken cancellationToken) => Resolved().GetAsync(cancellationToken);

    internal override void Expired() => _resolved?.Expired();

    internal override bool UsesTokens => _resolved?.UsesTokens == true;

    protected override void Dispose(bool disposing) => _resolved?.Dispose();

    public override string ToString() => _resolved is { } resolved ? $"{_named}: {resolved}" : _named;

    private AzureCredentials Resolved()
    {
        lock (_gate)
        {
            return _resolved ??= Resolve();
        }
    }

    private AzureCredentials Resolve()
    {
        string authority = (_host.Set("AZURE_AUTHORITY_HOST") ?? "https://login.microsoftonline.com/").TrimEnd('/') + "/";
        if (_host.Set("AZURE_CLIENT_ID") is { } client && _host.Set("AZURE_TENANT_ID") is { } tenant)
        {
            if (_host.Set("AZURE_CLIENT_SECRET") is { } secret)
            {
                _named = $"the client secret of {client}";
                return Tokens(ct => ExchangeAsync(authority, tenant, [new("client_id", client), new("client_secret", secret)], ct));
            }

            if (_host.Set("AZURE_FEDERATED_TOKEN_FILE") is { } file)
            {
                // The file is read again at each exchange: Kubernetes rotates it in place.
                _named = $"the workload identity of {client}";
                return Tokens(ct => ExchangeAsync(
                    authority,
                    tenant,
                    [
                        new("client_id", client),
                        new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                        new("client_assertion", _host.ReadFile(file)?.Trim() ?? throw new ObjectStoreException($"The federated token file '{file}' does not exist.")),
                    ],
                    ct));
            }
        }

        if (_host.Set("AZURE_STORAGE_CONNECTION_STRING") is { } connection)
        {
            _named = "the environment's connection string";
            return FromConnectionString(connection);
        }

        if (_host.Set("AZURE_STORAGE_KEY") is { } key && _host.Set("AZURE_STORAGE_ACCOUNT") is { } account)
        {
            _named = "the environment's account key";
            return SharedKey(account, key);
        }

        if (_host.Set("AZURE_STORAGE_SAS_TOKEN") is { } sas)
        {
            _named = "the environment's shared access signature";
            return SharedAccessSignature(sas);
        }

        if (_host.Set("IDENTITY_ENDPOINT") is { } endpoint && _host.Set("IDENTITY_HEADER") is { } header)
        {
            _named = "the App Service's managed identity";
            return Tokens(ct => AppServiceAsync(endpoint, header, ct));
        }

        _named = "the instance's managed identity";
        return Tokens(InstanceAsync);
    }

    private TokenCredentials Tokens(Func<CancellationToken, ValueTask<AccessToken>> source) =>
        new TokenCredentials(new RenewingToken(source, _host.Time));

    /// <summary>A client credentials exchange at the version 2 token endpoint, for the storage scope.</summary>
    private async ValueTask<AccessToken> ExchangeAsync(string authority, string tenant, List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        fields.Add(new("grant_type", "client_credentials"));
        fields.Add(new("scope", Scope));
        using HttpRequestMessage request = CredentialHost.Form($"{authority}{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", fields);
        byte[] answer = await _host.SendAsync(request, local: false, "Microsoft Entra ID", cancellationToken).ConfigureAwait(false);
        return _host.ParseToken(answer, "Microsoft Entra ID");
    }

    private async ValueTask<AccessToken> AppServiceAsync(string endpoint, string header, CancellationToken cancellationToken)
    {
        string client = _host.Set("AZURE_CLIENT_ID") is { } id ? "&client_id=" + Uri.EscapeDataString(id) : string.Empty;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(Resource)}{client}");
        request.Headers.TryAddWithoutValidation("X-IDENTITY-HEADER", header);
        byte[] answer = await _host.SendAsync(request, local: true, "The App Service identity endpoint", cancellationToken).ConfigureAwait(false);
        return _host.ParseToken(answer, "The App Service identity endpoint");
    }

    private async ValueTask<AccessToken> InstanceAsync(CancellationToken cancellationToken)
    {
        string client = _host.Set("AZURE_CLIENT_ID") is { } id ? "&client_id=" + Uri.EscapeDataString(id) : string.Empty;
        using HttpRequestMessage request = new HttpRequestMessage(
            HttpMethod.Get, $"http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(Resource)}{client}");
        request.Headers.TryAddWithoutValidation("Metadata", "true");
        try
        {
            byte[] answer = await _host.SendAsync(request, local: true, "The instance metadata service", cancellationToken).ConfigureAwait(false);
            return _host.ParseToken(answer, "The instance metadata service");
        }
        catch (ObjectStoreException unanswered)
        {
            throw new ObjectStoreException(
                "No Azure credentials: no client secret or workload identity, no storage credentials in the environment, and no managed identity answered.",
                unanswered);
        }
    }
}
