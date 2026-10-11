using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// Google's application default credentials, in their order: the file <c>GOOGLE_APPLICATION_CREDENTIALS</c>
/// names, the file <c>gcloud auth application-default login</c> writes, then the metadata server of
/// Compute Engine, GKE and Cloud Run. A file holds a service account's key, a user's refresh token, or
/// an external account's configuration for workload identity federation.
/// </summary>
internal sealed class GoogleChain : GoogleCredentials
{
    /// <summary>The scope the tokens are asked for: reading and writing objects, nothing else.</summary>
    internal const string Scope = "https://www.googleapis.com/auth/devstorage.read_write";

    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly CredentialHost _host;
    private readonly object _gate = new object();
    private RenewingToken? _tokens;
    private RSA? _key;
    private string _named = "Google's application default credentials";

    internal GoogleChain(CredentialHost host) => _host = host;

    internal override async ValueTask<string?> GetAsync(CancellationToken cancellationToken) =>
        await Resolved().GetAsync(cancellationToken).ConfigureAwait(false);

    internal override void Expired() => _tokens?.Expired();

    internal override bool UsesTokens => true;

    protected override void Dispose(bool disposing)
    {
        _tokens?.Dispose();
        _key?.Dispose();
    }

    public override string ToString() => _named;

    private RenewingToken Resolved()
    {
        lock (_gate)
        {
            return _tokens ??= new RenewingToken(Resolve(), _host.Time);
        }
    }

    private Func<CancellationToken, ValueTask<AccessToken>> Resolve()
    {
        if (_host.Set("GOOGLE_APPLICATION_CREDENTIALS") is { } path)
        {
            string json = _host.ReadFile(path) ?? throw new ObjectStoreException($"GOOGLE_APPLICATION_CREDENTIALS names '{path}', which does not exist.");
            return FromFile(json, path);
        }

        string? gcloud = _host.Set("APPDATA") is { } appData && OperatingSystem.IsWindows()
            ? Path.Combine(appData, "gcloud", "application_default_credentials.json")
            : _host.Home is { } home ? Path.Combine(home, ".config", "gcloud", "application_default_credentials.json") : null;
        if (gcloud is not null && _host.ReadFile(gcloud) is { } written)
        {
            return FromFile(written, gcloud);
        }

        string host = _host.Set("GCE_METADATA_HOST") ?? "metadata.google.internal";
        _named = "the metadata server's service account";
        return ct => MetadataAsync(host, ct);
    }

    /// <summary>The source a credentials file describes.</summary>
    private Func<CancellationToken, ValueTask<AccessToken>> FromFile(string json, string path)
    {
        using JsonDocument document = Parse(json, path);
        JsonElement root = document.RootElement;
        string type = Text(root, "type") ?? throw new ObjectStoreException($"'{path}' does not say what credentials it holds.");
        switch (type)
        {
            case "service_account":
            {
                string email = Required(root, "client_email", path);
                string pem = Required(root, "private_key", path);
                string? keyId = Text(root, "private_key_id");
                string tokenUri = Text(root, "token_uri") ?? DefaultTokenUri;
                RSA key = RSA.Create();
                try
                {
                    key.ImportFromPem(pem);
                }
                catch (Exception invalid) when (invalid is ArgumentException or CryptographicException)
                {
                    key.Dispose();
                    throw new ObjectStoreException($"The private key of '{path}' could not be read.", invalid);
                }

                _key = key;
                _named = $"the service account {email}";
                return ct => ServiceAccountAsync(key, email, keyId, tokenUri, ct);
            }

            case "authorized_user":
            {
                string clientId = Required(root, "client_id", path);
                string clientSecret = Required(root, "client_secret", path);
                string refresh = Required(root, "refresh_token", path);
                string tokenUri = Text(root, "token_uri") ?? DefaultTokenUri;
                _named = "the user's application default credentials";
                return ct => FormAsync(tokenUri, [new("grant_type", "refresh_token"), new("client_id", clientId), new("client_secret", clientSecret), new("refresh_token", refresh)], ct);
            }

            case "external_account":
                return ExternalAccount(root, path);

            default:
                throw new ObjectStoreException($"'{path}' holds credentials of the type '{type}', which this store does not take.");
        }
    }

    /// <summary>
    /// Workload identity federation: a subject token from a file or a URL, exchanged at Google's security
    /// token service, then for the service account it impersonates, if it names one.
    /// </summary>
    private Func<CancellationToken, ValueTask<AccessToken>> ExternalAccount(JsonElement root, string path)
    {
        string audience = Required(root, "audience", path);
        string subjectType = Required(root, "subject_token_type", path);
        string tokenUrl = Text(root, "token_url") ?? "https://sts.googleapis.com/v1/token";
        string? impersonation = Text(root, "service_account_impersonation_url");
        if (!root.TryGetProperty("credential_source", out JsonElement source))
        {
            throw new ObjectStoreException($"'{path}' names no credential source.");
        }

        if (source.TryGetProperty("environment_id", out _))
        {
            throw new ObjectStoreException($"'{path}' takes its subject token from an AWS identity, which this store does not exchange yet.");
        }

        string? file = Text(source, "file");
        string? url = Text(source, "url");
        Dictionary<string, string> headers = [];
        if (source.TryGetProperty("headers", out JsonElement given) && given.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty header in given.EnumerateObject())
            {
                headers[header.Name] = header.Value.GetString() ?? string.Empty;
            }
        }

        string? field = source.TryGetProperty("format", out JsonElement format) && Text(format, "type") == "json"
            ? Text(format, "subject_token_field_name")
            : null;
        if (file is null && url is null)
        {
            throw new ObjectStoreException($"'{path}' takes its subject token neither from a file nor from a URL.");
        }

        _named = impersonation is null ? $"the external account of {audience}" : "an external account impersonating a service account";
        return async ct =>
        {
            string subject = await SubjectAsync(file, url, headers, field, ct).ConfigureAwait(false);
            AccessToken federated = await FormAsync(
                tokenUrl,
                [
                    new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                    new("audience", audience),
                    new("scope", impersonation is null ? Scope : "https://www.googleapis.com/auth/cloud-platform"),
                    new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                    new("subject_token_type", subjectType),
                    new("subject_token", subject),
                ],
                ct).ConfigureAwait(false);
            if (impersonation is null)
            {
                return federated;
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, impersonation)
            {
                Content = new StringContent($"{{\"scope\":[\"{Scope}\"]}}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + federated.Value);
            byte[] answer = await _host.SendAsync(request, local: false, "The IAM credentials service", ct).ConfigureAwait(false);
            return _host.ParseToken(answer, "The IAM credentials service");
        };
    }

    private async ValueTask<string> SubjectAsync(string? file, string? url, Dictionary<string, string> headers, string? field, CancellationToken cancellationToken)
    {
        string text;
        if (file is not null)
        {
            text = _host.ReadFile(file) ?? throw new ObjectStoreException($"The subject token file '{file}' does not exist.");
        }
        else
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach ((string name, string value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            text = Encoding.UTF8.GetString(await _host.SendAsync(request, local: Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.IsLoopback, "The subject token's URL", cancellationToken).ConfigureAwait(false));
        }

        if (field is null)
        {
            return text.Trim();
        }

        using JsonDocument document = Parse(text, "the subject token");
        return Text(document.RootElement, field) ?? throw new ObjectStoreException($"The subject token has no field '{field}'.");
    }

    /// <summary>A service account's token: a JWT signed with its RSA key, exchanged at its token URI.</summary>
    private async ValueTask<AccessToken> ServiceAccountAsync(RSA key, string email, string? keyId, string tokenUri, CancellationToken cancellationToken)
    {
        long now = _host.Time.GetUtcNow().ToUnixTimeSeconds();
        string header = keyId is null ? "{\"alg\":\"RS256\",\"typ\":\"JWT\"}" : $"{{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":{JsonSerializer.Serialize(keyId, StoreJson.Default.String)}}}";
        string claims = $"{{\"iss\":{JsonSerializer.Serialize(email, StoreJson.Default.String)},\"scope\":\"{Scope}\",\"aud\":{JsonSerializer.Serialize(tokenUri, StoreJson.Default.String)},\"iat\":{now},\"exp\":{now + 3600}}}";
        string signed = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims));
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        string jwt = signed + "." + Base64Url.EncodeToString(signature);
        return await FormAsync(tokenUri, [new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), new("assertion", jwt)], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AccessToken> MetadataAsync(string host, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        try
        {
            byte[] answer = await _host.SendAsync(request, local: true, "The metadata server", cancellationToken).ConfigureAwait(false);
            return _host.ParseToken(answer, "The metadata server");
        }
        catch (ObjectStoreException unanswered)
        {
            throw new ObjectStoreException(
                "No Google credentials: GOOGLE_APPLICATION_CREDENTIALS is unset, gcloud wrote no application default credentials, and no metadata server answered.",
                unanswered);
        }
    }

    private async ValueTask<AccessToken> FormAsync(string uri, List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CredentialHost.Form(uri, fields);
        byte[] answer = await _host.SendAsync(request, local: false, "Google's token service", cancellationToken).ConfigureAwait(false);
        return _host.ParseToken(answer, "Google's token service");
    }

    private static JsonDocument Parse(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException malformed)
        {
            throw new ObjectStoreException($"{what} is not JSON.", malformed);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Required(JsonElement element, string name, string path) =>
        Text(element, name) ?? throw new ObjectStoreException($"'{path}' has no '{name}'.");
}
