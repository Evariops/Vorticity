using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.S3;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// The AWS credential chain, in the order of the SDK for Java's default chain: the environment, a web
/// identity, a profile of the shared files, the container endpoint, and the instance metadata service
/// version 2. The first source that answers is kept; its keys are renewed five minutes before they
/// expire, by one caller while the others keep using them.
/// </summary>
internal sealed class AwsChain : S3Credentials
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly CredentialHost _host;
    private readonly SemaphoreSlim _renewing = new SemaphoreSlim(1, 1);
    private Func<CancellationToken, ValueTask<AwsKeys>>? _source;
    private string _named = "the AWS credential chain";
    private AwsKeys? _keys;

    internal AwsChain(CredentialHost host) => _host = host;

    internal override async ValueTask<AwsKeys> GetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _host.Time.GetUtcNow();
        if (_keys is { } current && (current.Expiration is not { } expires || expires - Margin > now))
        {
            return current;
        }

        bool expired = _keys is not { } held || held.Expiration <= now;
        if (!expired && !await _renewing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _keys!;
        }

        if (expired)
        {
            await _renewing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (_keys is { } renewed && (renewed.Expiration is not { } until || until - Margin > _host.Time.GetUtcNow()))
            {
                return renewed;
            }

            _source ??= Resolve();

            // The keys replaced may be signing a request still: their secret is wiped when collected.
            AwsKeys keys = await _source(cancellationToken).ConfigureAwait(false);
            _keys = keys;
            return keys;
        }
        catch (Exception failed) when (failed is not OperationCanceledException && !expired && _keys is { } stillValid)
        {
            return stillValid;
        }
        finally
        {
            _renewing.Release();
        }
    }

    internal override void Expired(AwsKeys keys)
    {
        if (ReferenceEquals(_keys, keys) && keys.Expiration is not null)
        {
            _keys = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _keys?.Dispose();
        _renewing.Dispose();
    }

    public override string ToString() => _keys is { } keys ? $"{_named} ({keys})" : _named;

    /// <summary>The first source of the chain whose configuration the host holds; the instance's role when none.</summary>
    private Func<CancellationToken, ValueTask<AwsKeys>> Resolve()
    {
        if (_host.Set("AWS_ACCESS_KEY_ID") is { } id && _host.Set("AWS_SECRET_ACCESS_KEY") is { } secret)
        {
            _named = "the environment's keys";
            string? token = _host.Set("AWS_SESSION_TOKEN");
            return _ => new ValueTask<AwsKeys>(new AwsKeys(id, secret, token));
        }

        if (_host.Set("AWS_WEB_IDENTITY_TOKEN_FILE") is { } tokenFile && _host.Set("AWS_ROLE_ARN") is { } role)
        {
            _named = $"the web identity of {role}";
            string session = _host.Set("AWS_ROLE_SESSION_NAME") ?? "vorticity";
            return ct => AssumeRoleAsync(role, tokenFile, session, ct);
        }

        Dictionary<string, string>? profile = Profile();
        if (profile is not null)
        {
            if (profile.TryGetValue("aws_access_key_id", out string? profileId) && profile.TryGetValue("aws_secret_access_key", out string? profileSecret))
            {
                _named = "the profile's keys";
                string? token = profile.GetValueOrDefault("aws_session_token");
                return _ => new ValueTask<AwsKeys>(new AwsKeys(profileId, profileSecret, token));
            }

            if (profile.TryGetValue("role_arn", out string? profileRole) && profile.TryGetValue("web_identity_token_file", out string? profileFile))
            {
                _named = $"the profile's web identity of {profileRole}";
                string session = profile.GetValueOrDefault("role_session_name") ?? "vorticity";
                return ct => AssumeRoleAsync(profileRole, profileFile, session, ct);
            }
        }

        if (_host.Set("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is { } relative)
        {
            _named = "the container's credentials";
            return ct => ContainerAsync("http://169.254.170.2" + relative, ct);
        }

        if (_host.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI") is { } full)
        {
            _named = "the container's credentials";
            return ct => ContainerAsync(full, ct);
        }

        if (!string.Equals(_host.Set("AWS_EC2_METADATA_DISABLED"), "true", StringComparison.OrdinalIgnoreCase))
        {
            _named = "the instance's role";
            return async ct =>
            {
                try
                {
                    return await InstanceAsync(ct).ConfigureAwait(false);
                }
                catch (ObjectStoreException unanswered)
                {
                    throw new ObjectStoreException(
                        "No AWS credentials: none in the environment, no web identity, no profile, no container endpoint, and the instance metadata service did not answer.",
                        unanswered);
                }
            };
        }

        throw new ObjectStoreException(
            "No AWS credentials: none in the environment, no web identity, no profile, no container endpoint, and the instance metadata service is disabled.");
    }

    /// <summary>
    /// <c>AssumeRoleWithWebIdentity</c>, unsigned, the token file read again at each renewal since the
    /// platform rotates it in place.
    /// </summary>
    private async ValueTask<AwsKeys> AssumeRoleAsync(string role, string tokenFile, string session, CancellationToken cancellationToken)
    {
        string token = _host.ReadFile(tokenFile)?.Trim()
            ?? throw new ObjectStoreException($"The web identity token file '{tokenFile}' does not exist.");
        string? region = _host.Set("AWS_REGION") ?? _host.Set("AWS_DEFAULT_REGION");
        string endpoint = region is null ? "https://sts.amazonaws.com/" : $"https://sts.{region}.amazonaws.com/";
        using HttpRequestMessage request = CredentialHost.Form(endpoint,
        [
            new("Action", "AssumeRoleWithWebIdentity"),
            new("Version", "2011-06-15"),
            new("RoleArn", role),
            new("RoleSessionName", session),
            new("WebIdentityToken", token),
        ]);
        byte[] answer = await _host.SendAsync(request, local: false, "STS", cancellationToken).ConfigureAwait(false);
        string id = ServiceXml.ParseElement(answer, "AccessKeyId") ?? throw new ObjectStoreException("STS answered without an access key id.");
        string secret = ServiceXml.ParseElement(answer, "SecretAccessKey") ?? throw new ObjectStoreException("STS answered without a secret.");
        string sessionToken = ServiceXml.ParseElement(answer, "SessionToken") ?? throw new ObjectStoreException("STS answered without a session token.");
        DateTimeOffset? expiration = ServiceXml.ParseElement(answer, "Expiration") is { } text ? S3Answers.IsoDate(text) : null;
        return new AwsKeys(id, secret, sessionToken, expiration);
    }

    /// <summary>The container endpoint of ECS and EKS Pod Identity, with its authorization token or the file that holds it.</summary>
    private async ValueTask<AwsKeys> ContainerAsync(string uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
        string? authorization = _host.Set("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE") is { } file
            ? _host.ReadFile(file)?.Trim()
            : _host.Set("AWS_CONTAINER_AUTHORIZATION_TOKEN");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        bool local = Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && (parsed.IsLoopback || parsed.Host.StartsWith("169.254.", StringComparison.Ordinal));
        return ParseKeys(await _host.SendAsync(request, local, "The container credentials endpoint", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The instance metadata service version 2: a session token first, then the role's name, then its keys.</summary>
    private async ValueTask<AwsKeys> InstanceAsync(CancellationToken cancellationToken)
    {
        string host = _host.Set("AWS_EC2_METADATA_SERVICE_ENDPOINT")?.TrimEnd('/') ?? "http://169.254.169.254";
        using HttpRequestMessage start = new HttpRequestMessage(HttpMethod.Put, host + "/latest/api/token");
        start.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "21600");
        string session = Encoding.UTF8.GetString(await _host.SendAsync(start, local: true, "The instance metadata service", cancellationToken).ConfigureAwait(false));

        using HttpRequestMessage list = new HttpRequestMessage(HttpMethod.Get, host + "/latest/meta-data/iam/security-credentials/");
        list.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", session);
        string role = Encoding.UTF8.GetString(await _host.SendAsync(list, local: true, "The instance metadata service", cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var first, ..]
            ? first
            : throw new ObjectStoreException("The instance has no role.");

        using HttpRequestMessage keys = new HttpRequestMessage(HttpMethod.Get, host + "/latest/meta-data/iam/security-credentials/" + role);
        keys.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", session);
        return ParseKeys(await _host.SendAsync(keys, local: true, "The instance metadata service", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The keys the container endpoint and the instance metadata service answer with, in JSON.</summary>
    private static AwsKeys ParseKeys(byte[] json)
    {
        string? id = null, secret = null, token = null;
        DateTimeOffset? expiration = null;
        try
        {
            Utf8JsonReader reader = new Utf8JsonReader(json);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                string name = reader.GetString()!;
                reader.Read();
                switch (name)
                {
                    case "AccessKeyId":
                        id = reader.GetString();
                        break;
                    case "SecretAccessKey":
                        secret = reader.GetString();
                        break;
                    case "Token":
                        token = reader.GetString();
                        break;
                    case "Expiration":
                        expiration = DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (Exception malformed) when (malformed is JsonException or FormatException or InvalidOperationException)
        {
            throw new ObjectStoreException("A credentials endpoint answered with keys that could not be read.", malformed);
        }

        return id is null || secret is null
            ? throw new ObjectStoreException("A credentials endpoint answered without keys.")
            : new AwsKeys(id, secret, token, expiration);
    }

    /// <summary>
    /// The profile <c>AWS_PROFILE</c> names, <c>default</c> otherwise: its keys from the credentials file,
    /// <c>[name]</c>, and its role from the config file, <c>[profile name]</c>.
    /// </summary>
    private Dictionary<string, string>? Profile()
    {
        string name = _host.Set("AWS_PROFILE") ?? "default";
        string? home = _host.Home;
        string? credentialsPath = _host.Set("AWS_SHARED_CREDENTIALS_FILE") ?? (home is null ? null : Path.Combine(home, ".aws", "credentials"));
        string? configPath = _host.Set("AWS_CONFIG_FILE") ?? (home is null ? null : Path.Combine(home, ".aws", "config"));
        Dictionary<string, string> merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool found = false;
        if (configPath is not null && _host.ReadFile(configPath) is { } config)
        {
            found |= Section(config, name == "default" ? "default" : "profile " + name, merged);
        }

        if (credentialsPath is not null && _host.ReadFile(credentialsPath) is { } credentials)
        {
            found |= Section(credentials, name, merged);
        }

        return found ? merged : null;
    }

    /// <summary>Reads the keys of an INI section into <paramref name="into"/>, the credentials file's winning.</summary>
    internal static bool Section(string text, string section, Dictionary<string, string> into)
    {
        bool inside = false, found = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inside = string.Equals(line[1..^1].Trim(), section, StringComparison.Ordinal);
                found |= inside;
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (inside && equals > 0)
            {
                into[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return found;
    }
}
