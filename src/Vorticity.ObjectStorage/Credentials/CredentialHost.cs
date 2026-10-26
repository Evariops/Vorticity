using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// What a credential chain reads of its host: environment variables, files, the clock, and the HTTP of
/// the token and metadata endpoints. Tests give their own; a store is given the process's.
/// </summary>
internal sealed class CredentialHost
{
    private static readonly Lazy<HttpClient> RealPublic = new Lazy<HttpClient>(() => Client(useProxy: true));
    private static readonly Lazy<HttpClient> RealLocal = new Lazy<HttpClient>(() => Client(useProxy: false));
    private readonly HttpClient? _client;

    /// <summary>A host over the given environment, files and handler.</summary>
    internal CredentialHost(Func<string, string?> variable, Func<string, string?> readFile, string? home, TimeProvider time, HttpMessageHandler? handler)
    {
        Variable = variable;
        ReadFile = readFile;
        Home = home;
        Time = time;
        _client = handler is null ? null : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The process: its environment, its files, the system clock and the network.</summary>
    internal static CredentialHost Real { get; } = new CredentialHost(
        Environment.GetEnvironmentVariable,
        static path => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        TimeProvider.System,
        null);

    /// <summary>An environment variable, null when unset or empty.</summary>
    internal Func<string, string?> Variable { get; }

    /// <summary>A file's text, null when it does not exist.</summary>
    internal Func<string, string?> ReadFile { get; }

    /// <summary>The user's home directory.</summary>
    internal string? Home { get; }

    internal TimeProvider Time { get; }

    /// <summary>A variable that is set and not empty, or null.</summary>
    internal string? Set(string name) => Variable(name) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// The HTTP client of a credential endpoint: through the environment's proxy for a public one, never
    /// through it for a link-local one, the instance metadata services, which a proxy cannot reach.
    /// </summary>
    internal HttpClient Http(bool local) => _client ?? (local ? RealLocal.Value : RealPublic.Value);

    /// <summary>Sends a request to a credential endpoint and reads its answer's body, failing on a status that is not a success.</summary>
    /// <exception cref="ObjectStoreException">The endpoint failed or refused.</exception>
    internal async ValueTask<byte[]> SendAsync(HttpRequestMessage request, bool local, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http(local).SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectStoreException($"{what} could not be reached: {unreachable.Message}", unreachable);
        }

        using (response)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The body of a refusal names the reason; it never echoes a secret sent in a form.
                string reason = Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 300)));
                throw new ObjectStoreException($"{what} refused the request with HTTP {(int)response.StatusCode}: {reason}");
            }

            return body;
        }
    }

    /// <summary>A form-encoded <c>POST</c>, as OAuth 2.0 token endpoints take them.</summary>
    internal static HttpRequestMessage Form(string uri, IEnumerable<KeyValuePair<string, string>> fields) =>
        new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(fields) };

    /// <summary>An OAuth 2.0 token answer: <c>access_token</c> and <c>expires_in</c> seconds, or <c>expires_on</c> as a Unix time.</summary>
    /// <exception cref="ObjectStoreException">The answer holds no token.</exception>
    internal AccessToken ParseToken(ReadOnlySpan<byte> json, string what)
    {
        string? token = null;
        DateTimeOffset? expires = null;
        try
        {
            Utf8JsonReader reader = new Utf8JsonReader(json);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (reader.ValueTextEquals("access_token"u8) || reader.ValueTextEquals("accessToken"u8))
                {
                    reader.Read();
                    token = reader.GetString();
                }
                else if (reader.ValueTextEquals("expires_in"u8))
                {
                    reader.Read();
                    expires = Time.GetUtcNow().AddSeconds(Number(ref reader));
                }
                else if (reader.ValueTextEquals("expires_on"u8))
                {
                    reader.Read();
                    expires = DateTimeOffset.FromUnixTimeSeconds((long)Number(ref reader));
                }
                else if (reader.ValueTextEquals("expireTime"u8))
                {
                    reader.Read();
                    expires = DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception malformed) when (malformed is JsonException or FormatException or InvalidOperationException)
        {
            throw new ObjectStoreException($"{what} answered with a token that could not be read.", malformed);
        }

        return token is null
            ? throw new ObjectStoreException($"{what} answered without a token.")
            : new AccessToken(token, expires ?? Time.GetUtcNow().AddMinutes(30));
    }

    /// <summary>A JSON number, or a string holding one, as the token endpoints write either.</summary>
    internal static double Number(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number
            ? reader.GetDouble()
            : double.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static HttpClient Client(bool useProxy) => new HttpClient(new SocketsHttpHandler
    {
        UseProxy = useProxy,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = useProxy ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(useProxy ? 30 : 5),
    };
}
