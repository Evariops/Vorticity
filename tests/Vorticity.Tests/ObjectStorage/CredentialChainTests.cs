// The credential chains of AWS, Azure and Google, each against a host the test plays: its environment,
// its files, its clock, and the token and metadata endpoints it would reach, each request checked for
// what it carries.
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class CredentialChainTests
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheAwsChainTakesTheEnvironmentsKeysFirst()
    {
        FakeHost host = new FakeHost();
        host.Env["AWS_ACCESS_KEY_ID"] = "AKIDENV";
        host.Env["AWS_SECRET_ACCESS_KEY"] = "secret";
        host.Env["AWS_WEB_IDENTITY_TOKEN_FILE"] = "/never/read";
        using AwsChain chain = new AwsChain(host.Build());
        AwsKeys keys = await chain.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("AKIDENV", (string?)null), (keys.AccessKeyId, keys.SessionToken));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task AWebIdentityIsExchangedWithStsAndItsFileReadAgainAtEachRenewal()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeHost host = new FakeHost();
        host.Env["AWS_WEB_IDENTITY_TOKEN_FILE"] = "/var/run/token";
        host.Env["AWS_ROLE_ARN"] = "arn:aws:iam::123:role/reader";
        host.Env["AWS_REGION"] = "eu-west-3";
        host.Files["/var/run/token"] = "jwt-1";
        int issued = 0;
        host.Answer = request => request.Uri == "https://sts.eu-west-3.amazonaws.com/"
            ? Ok($"<AssumeRoleWithWebIdentityResponse><AssumeRoleWithWebIdentityResult><Credentials><AccessKeyId>ASIA{++issued}</AccessKeyId><SecretAccessKey>s</SecretAccessKey><SessionToken>tok</SessionToken><Expiration>{host.Clock.Now.AddHours(1):yyyy-MM-dd'T'HH:mm:ss'Z'}</Expiration></Credentials></AssumeRoleWithWebIdentityResult></AssumeRoleWithWebIdentityResponse>")
            : null;
        using AwsChain chain = new AwsChain(host.Build());

        AwsKeys first = await chain.GetAsync(ct);
        Assert.Equal(("ASIA1", "tok"), (first.AccessKeyId, first.SessionToken));
        Assert.Equal("AssumeRoleWithWebIdentity", host.Requests[0].Form["Action"]);
        Assert.Equal(("arn:aws:iam::123:role/reader", "jwt-1"), (host.Requests[0].Form["RoleArn"], host.Requests[0].Form["WebIdentityToken"]));
        Assert.Same(first, await chain.GetAsync(ct));

        // Within five minutes of the expiry, the keys are renewed, the rotated file read again.
        host.Files["/var/run/token"] = "jwt-2";
        host.Clock.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal("ASIA2", (await chain.GetAsync(ct)).AccessKeyId);
        Assert.Equal("jwt-2", host.Requests[1].Form["WebIdentityToken"]);
    }

    [Fact]
    public async Task AProfileGivesItsKeysOrItsRole()
    {
        FakeHost host = new FakeHost();
        host.Env["AWS_PROFILE"] = "dev";
        host.Files["/home/u/.aws/credentials"] = "[default]\naws_access_key_id = AKIDDEFAULT\naws_secret_access_key = a\n\n[dev]\naws_access_key_id = AKIDDEV\naws_secret_access_key = b\n";
        host.Files["/home/u/.aws/config"] = "[profile dev]\nregion = eu-west-3\n";
        using AwsChain chain = new AwsChain(host.Build());
        Assert.Equal("AKIDDEV", (await chain.GetAsync(TestContext.Current.CancellationToken)).AccessKeyId);
    }

    [Fact]
    public async Task TheContainerEndpointIsAskedWithItsAuthorizationTokenFromItsFile()
    {
        FakeHost host = new FakeHost();
        host.Env["AWS_CONTAINER_CREDENTIALS_FULL_URI"] = "http://169.254.170.23/v1/credentials";
        host.Env["AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE"] = "/var/run/pod-identity";
        host.Files["/var/run/pod-identity"] = "pod-token\n";
        host.Answer = request => request.Uri == "http://169.254.170.23/v1/credentials" && request.Headers.GetValueOrDefault("Authorization") == "pod-token"
            ? Ok($"{{\"AccessKeyId\":\"ASIAPOD\",\"SecretAccessKey\":\"s\",\"Token\":\"t\",\"Expiration\":\"{host.Clock.Now.AddHours(6):O}\"}}")
            : null;
        using AwsChain chain = new AwsChain(host.Build());
        Assert.Equal("ASIAPOD", (await chain.GetAsync(TestContext.Current.CancellationToken)).AccessKeyId);
    }

    [Fact]
    public async Task TheInstanceMetadataServiceIsAskedForASessionThenTheRoleThenItsKeys()
    {
        FakeHost host = new FakeHost();
        host.Answer = request => (request.Method, request.Uri) switch
        {
            ("PUT", "http://169.254.169.254/latest/api/token") when request.Headers.ContainsKey("X-aws-ec2-metadata-token-ttl-seconds") => Ok("session-token"),
            ("GET", "http://169.254.169.254/latest/meta-data/iam/security-credentials/") when request.Headers.GetValueOrDefault("X-aws-ec2-metadata-token") == "session-token" => Ok("reader-role\n"),
            ("GET", "http://169.254.169.254/latest/meta-data/iam/security-credentials/reader-role") when request.Headers.GetValueOrDefault("X-aws-ec2-metadata-token") == "session-token"
                => Ok($"{{\"Code\":\"Success\",\"AccessKeyId\":\"ASIAEC2\",\"SecretAccessKey\":\"s\",\"Token\":\"t\",\"Expiration\":\"{host.Clock.Now.AddHours(6):O}\"}}"),
            _ => null,
        };
        using AwsChain chain = new AwsChain(host.Build());
        Assert.Equal("ASIAEC2", (await chain.GetAsync(TestContext.Current.CancellationToken)).AccessKeyId);
        Assert.Equal(3, host.Requests.Count);
        Assert.Contains("instance's role", chain.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAwsHostWithNoSourceSaysSo()
    {
        FakeHost host = new FakeHost();
        host.Env["AWS_EC2_METADATA_DISABLED"] = "true";
        using AwsChain disabled = new AwsChain(host.Build());
        ObjectStoreException none = await Assert.ThrowsAsync<ObjectStoreException>(async () => await disabled.GetAsync(TestContext.Current.CancellationToken));
        Assert.Contains("disabled", none.Message, StringComparison.Ordinal);

        FakeHost silent = new FakeHost();
        using AwsChain unanswered = new AwsChain(silent.Build());
        ObjectStoreException nothing = await Assert.ThrowsAsync<ObjectStoreException>(async () => await unanswered.GetAsync(TestContext.Current.CancellationToken));
        Assert.Contains("did not answer", nothing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChainsKeysSignTheStoresRequests()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeHost host = new FakeHost();
        host.Env["AWS_ACCESS_KEY_ID"] = S3Emulator.AccessKey;
        host.Env["AWS_SECRET_ACCESS_KEY"] = S3Emulator.Secret;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed("lake", "a", [1, 2, 3]);
        await using S3ObjectStore store = new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Region = "eu-west-3",
            Credentials = new AwsChain(host.Build()),
            Handler = emulator,
        });
        Assert.Equal(3, (await store.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
    }

    [Fact]
    public async Task AnAzureClientSecretIsExchangedForAStorageToken()
    {
        FakeHost host = new FakeHost();
        host.Env["AZURE_CLIENT_ID"] = "client";
        host.Env["AZURE_TENANT_ID"] = "tenant";
        host.Env["AZURE_CLIENT_SECRET"] = "very-secret";
        host.Env["AZURE_STORAGE_CONNECTION_STRING"] = "never used";
        host.Answer = request => request.Uri == "https://login.microsoftonline.com/tenant/oauth2/v2.0/token"
            ? Ok("{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"entra-token\"}")
            : null;
        using AzureChain chain = new AzureChain(host.Build());
        AzureAuthorization authorization = await chain.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("entra-token", authorization.Bearer);
        Dictionary<string, string> form = host.Requests.Single().Form;
        Assert.Equal(("client_credentials", "https://storage.azure.com/.default", "very-secret"), (form["grant_type"], form["scope"], form["client_secret"]));
        Assert.DoesNotContain("very-secret", chain.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAzureWorkloadIdentityReadsItsTokenFileAtEachExchange()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeHost host = new FakeHost();
        host.Env["AZURE_CLIENT_ID"] = "client";
        host.Env["AZURE_TENANT_ID"] = "tenant";
        host.Env["AZURE_FEDERATED_TOKEN_FILE"] = "/var/run/azure-token";
        host.Env["AZURE_AUTHORITY_HOST"] = "https://login.example/";
        host.Files["/var/run/azure-token"] = "federated-1";
        host.Answer = request => request.Uri == "https://login.example/tenant/oauth2/v2.0/token"
            ? Ok($"{{\"expires_in\":3600,\"access_token\":\"t-{request.Form["client_assertion"]}\"}}")
            : null;
        using AzureChain chain = new AzureChain(host.Build());
        Assert.Equal("t-federated-1", (await chain.GetAsync(ct)).Bearer);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", host.Requests[0].Form["client_assertion_type"]);

        host.Files["/var/run/azure-token"] = "federated-2";
        host.Clock.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal("t-federated-2", (await chain.GetAsync(ct)).Bearer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAzureManagedIdentityAnswersThroughAppServiceOrTheInstance(bool appService)
    {
        FakeHost host = new FakeHost();
        if (appService)
        {
            host.Env["IDENTITY_ENDPOINT"] = "http://localhost:42356/msi/token";
            host.Env["IDENTITY_HEADER"] = "app-secret";
        }

        long expires = host.Clock.Now.AddHours(1).ToUnixTimeSeconds();
        host.Answer = request =>
            (appService && request.Uri.StartsWith("http://localhost:42356/msi/token?api-version=2019-08-01&resource=https%3A%2F%2Fstorage.azure.com%2F", StringComparison.Ordinal)
                && request.Headers.GetValueOrDefault("X-IDENTITY-HEADER") == "app-secret")
            || (!appService && request.Uri.StartsWith("http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01", StringComparison.Ordinal)
                && request.Headers.GetValueOrDefault("Metadata") == "true")
                ? Ok($"{{\"access_token\":\"managed\",\"expires_on\":\"{expires}\"}}")
                : null;
        using AzureChain chain = new AzureChain(host.Build());
        Assert.Equal("managed", (await chain.GetAsync(TestContext.Current.CancellationToken)).Bearer);
    }

    [Fact]
    public async Task TheAzureStorageCredentialsOfTheEnvironmentComeBeforeAManagedIdentity()
    {
        FakeHost host = new FakeHost();
        host.Env["AZURE_STORAGE_CONNECTION_STRING"] = $"DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey={AzureEmulator.Key};EndpointSuffix=core.windows.net";
        using AzureChain chain = new AzureChain(host.Build());
        AzureAuthorization authorization = await chain.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("contoso", (string?)null), (authorization.Account, authorization.Bearer));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task AGoogleServiceAccountSignsAJwtWithItsKey()
    {
        using RSA key = RSA.Create(2048);
        FakeHost host = new FakeHost();
        host.Env["GOOGLE_APPLICATION_CREDENTIALS"] = "/secrets/sa.json";
        host.Files["/secrets/sa.json"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["client_email"] = "reader@project.iam.gserviceaccount.com",
            ["private_key_id"] = "key-1",
            ["private_key"] = key.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });
        string? assertion = null;
        host.Answer = request =>
        {
            if (request.Uri != "https://oauth2.googleapis.com/token" || request.Form.GetValueOrDefault("grant_type") != "urn:ietf:params:oauth:grant-type:jwt-bearer")
            {
                return null;
            }

            assertion = request.Form["assertion"];
            return Ok("{\"access_token\":\"sa-token\",\"expires_in\":3599,\"token_type\":\"Bearer\"}");
        };
        using GoogleChain chain = new GoogleChain(host.Build());
        Assert.Equal("sa-token", await chain.GetAsync(TestContext.Current.CancellationToken));

        // The JWT: its header names the key, its claims the account, the scope and the audience, and its
        // signature is the key's.
        string[] parts = assertion!.Split('.');
        using JsonDocument header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        Assert.Equal(("RS256", "key-1"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
        Assert.Equal("reader@project.iam.gserviceaccount.com", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/devstorage.read_write", claims.RootElement.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(3600, claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64());
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task GcloudsUserCredentialsAreRefreshedFromTheirFile()
    {
        FakeHost host = new FakeHost();
        host.Files["/home/u/.config/gcloud/application_default_credentials.json"] =
            "{\"type\":\"authorized_user\",\"client_id\":\"cid\",\"client_secret\":\"cs\",\"refresh_token\":\"rt\"}";
        host.Answer = request => request.Uri == "https://oauth2.googleapis.com/token" && request.Form.GetValueOrDefault("refresh_token") == "rt"
            ? Ok("{\"access_token\":\"user-token\",\"expires_in\":3599}")
            : null;
        using GoogleChain chain = new GoogleChain(host.Build());
        Assert.Equal("user-token", await chain.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("refresh_token", host.Requests.Single().Form["grant_type"]);
    }

    [Fact]
    public async Task TheMetadataServerServesTheAttachedServiceAccount()
    {
        FakeHost host = new FakeHost();
        host.Answer = request => request.Uri == "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token"
            && request.Headers.GetValueOrDefault("Metadata-Flavor") == "Google"
                ? Ok("{\"access_token\":\"gce-token\",\"expires_in\":3599,\"token_type\":\"Bearer\"}")
                : null;
        using GoogleChain chain = new GoogleChain(host.Build());
        Assert.Equal("gce-token", await chain.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExternalAccountExchangesItsSubjectTokenThenImpersonates()
    {
        FakeHost host = new FakeHost();
        host.Env["GOOGLE_APPLICATION_CREDENTIALS"] = "/secrets/wif.json";
        host.Files["/var/run/oidc"] = "{\"id_token\":\"oidc-subject\"}";
        host.Files["/secrets/wif.json"] = """
            {"type":"external_account","audience":"//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/k",
             "subject_token_type":"urn:ietf:params:oauth:token-type:jwt","token_url":"https://sts.googleapis.com/v1/token",
             "service_account_impersonation_url":"https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sa@p.iam.gserviceaccount.com:generateAccessToken",
             "credential_source":{"file":"/var/run/oidc","format":{"type":"json","subject_token_field_name":"id_token"}}}
            """;
        host.Answer = request => request.Uri switch
        {
            "https://sts.googleapis.com/v1/token" when request.Form.GetValueOrDefault("subject_token") == "oidc-subject"
                && request.Form["grant_type"] == "urn:ietf:params:oauth:grant-type:token-exchange" => Ok("{\"access_token\":\"federated\",\"expires_in\":3600}"),
            "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sa@p.iam.gserviceaccount.com:generateAccessToken"
                when request.Headers.GetValueOrDefault("Authorization") == "Bearer federated" && request.Body.Contains("devstorage.read_write", StringComparison.Ordinal)
                => Ok($"{{\"accessToken\":\"impersonated\",\"expireTime\":\"{host.Clock.Now.AddHours(1):yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}"),
            _ => null,
        };
        using GoogleChain chain = new GoogleChain(host.Build());
        Assert.Equal("impersonated", await chain.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, host.Requests.Count);
    }

    private static HttpResponseMessage Ok(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    /// <summary>A host the test plays: an environment, files, a clock, and the endpoints' answers.</summary>
    private sealed class FakeHost
    {
        internal Dictionary<string, string> Env { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal Dictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal ManualClock Clock { get; } = new ManualClock(Start);

        /// <summary>Answers a request, or null to refuse it as an unreachable endpoint would be.</summary>
        internal Func<Seen, HttpResponseMessage?> Answer { get; set; } = _ => null;

        internal List<Seen> Requests { get; } = [];

        internal CredentialHost Build() => new CredentialHost(
            name => Env.GetValueOrDefault(name), path => Files.GetValueOrDefault(path), "/home/u", Clock, new Handler(this));

        private sealed class Handler(FakeHost host) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                Dictionary<string, string> form = request.Content is FormUrlEncodedContent
                    ? body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')))
                    : [];
                Seen seen = new Seen(
                    request.Method.Method,
                    request.RequestUri!.ToString(),
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                    form,
                    body);
                lock (host.Requests)
                {
                    host.Requests.Add(seen);
                }

                return host.Answer(seen) ?? throw new HttpRequestException($"Nothing answers {seen.Method} {seen.Uri}.");
            }
        }
    }

    /// <summary>A request as the endpoint saw it.</summary>
    private sealed record Seen(string Method, string Uri, Dictionary<string, string> Headers, Dictionary<string, string> Form, string Body);
}
