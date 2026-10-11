using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage;

/// <summary>A bearer token, from Microsoft Entra ID or Google's OAuth 2.0, and when it expires.</summary>
/// <param name="Value">The token.</param>
/// <param name="ExpiresOn">When it expires: the store asks for another five minutes before.</param>
public readonly record struct AccessToken(string Value, DateTimeOffset ExpiresOn)
{
    /// <summary>The token as a message may name it: its expiry, never its value.</summary>
    public override string ToString() => $"a token expiring {ExpiresOn:O}";
}

/// <summary>An access key that signs S3 requests, as a source of the caller's hands it over.</summary>
/// <param name="AccessKeyId">The access key id.</param>
/// <param name="SecretAccessKey">The secret access key.</param>
/// <param name="SessionToken">The session token of temporary keys, or null.</param>
/// <param name="ExpiresOn">
/// When temporary keys expire: the store asks for others five minutes before. Null for keys whose
/// expiry the source does not say: the store asks for others when the service refuses their session
/// token as expired.
/// </param>
public readonly record struct AccessKey(string AccessKeyId, string SecretAccessKey, string? SessionToken = null, DateTimeOffset? ExpiresOn = null)
{
    /// <summary>The key as a message may name it: its id, never its secret.</summary>
    public override string ToString() => $"access key {AccessKeyId}";
}

/// <summary>
/// How a store authorizes its requests, whichever service it speaks to: an access key that signs S3
/// requests, an Azure account key or shared access signature, bearer tokens for Azure and Cloud
/// Storage, the credentials the environment holds for each service, or none.
/// </summary>
/// <remarks>
/// <para>
/// A store takes the credentials its service understands and refuses the others when it is built. An
/// S3 store takes an access key, keys from a source, the environment's credentials or none. An Azure
/// store takes an account key, a shared access signature, tokens, the environment's credentials or
/// none. A Cloud Storage store takes tokens, the environment's credentials or none.
/// </para>
/// <para>
/// Credentials hold no resource and are never disposed, so stores may share them. Stores that share
/// credentials share their keys and tokens, renewed once for all of them: the environment's
/// credentials, given to every store of a host, ask STS, a metadata service or a token endpoint once
/// rather than once per bucket. A secret stays in managed memory while the credentials live, as it
/// does in the platform's and the cloud SDKs' own clients.
/// </para>
/// </remarks>
public sealed class StoreCredentials
{
    private static readonly StoreCredentials None = new StoreCredentials("no credentials", AwsKeySource.Unsigned, AzureAuthorizer.None, TokenSource.None);

    private readonly string _named;

    private StoreCredentials(string named, AwsKeySource? s3, AzureAuthorizer? azure, TokenSource? google)
    {
        _named = named;
        ForS3 = s3;
        ForAzure = azure;
        ForGoogle = google;
    }

    /// <summary>What signs an S3 store's requests, or null when these credentials cannot.</summary>
    internal AwsKeySource? ForS3 { get; }

    /// <summary>What authorizes an Azure store's requests, or null when these credentials cannot.</summary>
    internal AzureAuthorizer? ForAzure { get; }

    /// <summary>The tokens of a Cloud Storage store's requests, or null when these credentials cannot give them.</summary>
    internal TokenSource? ForGoogle { get; }

    /// <summary>
    /// The credentials of the environment, each service's resolved at its first request, in the order
    /// its own SDKs follow. Keys and tokens that expire are renewed five minutes before they do.
    /// <list type="bullet">
    /// <item><description>S3: <c>AWS_ACCESS_KEY_ID</c> and <c>AWS_SECRET_ACCESS_KEY</c>, with
    /// <c>AWS_SESSION_TOKEN</c>; a web identity, <c>AWS_WEB_IDENTITY_TOKEN_FILE</c> and
    /// <c>AWS_ROLE_ARN</c> exchanged with STS, as a Kubernetes service account reaches a role on EKS; the
    /// profile <c>AWS_PROFILE</c> names in <c>~/.aws/credentials</c> and <c>~/.aws/config</c>, when it
    /// holds keys or a web identity; the container endpoint of ECS and EKS Pod Identity; then the
    /// instance metadata service, version 2 only. R2, OVHcloud and Scaleway keys come through the same
    /// variables or profiles.</description></item>
    /// <item><description>Azure: a client secret, <c>AZURE_CLIENT_ID</c>, <c>AZURE_TENANT_ID</c> and
    /// <c>AZURE_CLIENT_SECRET</c>; a workload identity, <c>AZURE_FEDERATED_TOKEN_FILE</c>, read again at
    /// each exchange; the storage credentials Azure's tools read, <c>AZURE_STORAGE_CONNECTION_STRING</c>,
    /// or <c>AZURE_STORAGE_KEY</c> with <c>AZURE_STORAGE_ACCOUNT</c>, or <c>AZURE_STORAGE_SAS_TOKEN</c>;
    /// then a managed identity, through App Service's <c>IDENTITY_ENDPOINT</c> or the instance
    /// metadata service.</description></item>
    /// <item><description>Cloud Storage: Google's application default credentials, the file
    /// <c>GOOGLE_APPLICATION_CREDENTIALS</c> names (a service account's key, a user's refresh token, or
    /// an external account's configuration for workload identity federation), the file
    /// <c>gcloud auth application-default login</c> writes, then the metadata server of Compute Engine,
    /// GKE and Cloud Run.</description></item>
    /// </list>
    /// </summary>
    /// <returns>The credentials, which every store of a host may share.</returns>
    public static StoreCredentials FromEnvironment() => FromHost(CredentialHost.Real);

    /// <summary>An access key that signs S3 requests, given in code. R2, OVHcloud and Scaleway issue keys of the same shape.</summary>
    /// <param name="accessKeyId">The access key id.</param>
    /// <param name="secretAccessKey">The secret access key.</param>
    /// <param name="sessionToken">The session token of temporary keys, or null.</param>
    /// <returns>The credentials.</returns>
    public static StoreCredentials AccessKey(string accessKeyId, string secretAccessKey, string? sessionToken = null)
    {
        AwsKeys keys = new AwsKeys(accessKeyId, secretAccessKey, sessionToken);
        return new StoreCredentials(keys.ToString(), new AwsKeySource.Fixed(keys), null, null);
    }

    /// <summary>
    /// Access keys from a source of the caller's, for keys that rotate or that another library holds:
    /// the AWS SDK's credentials, for instance, for IAM Identity Center, a credential process, or a role
    /// assumed from another profile, which the environment's credentials do not resolve.
    /// </summary>
    /// <param name="source">
    /// Gives the keys to sign with. Keys with an expiry are kept until five minutes before it, and keys
    /// without one until the service refuses their session token as expired.
    /// </param>
    /// <returns>The credentials.</returns>
    public static StoreCredentials AccessKey(Func<CancellationToken, ValueTask<AccessKey>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const string Named = "access keys from a source";
        AwsKeySource keys = new AwsKeySource.Renewed(async ct => AwsKeys.From(await source(ct).ConfigureAwait(false)), Named, TimeProvider.System);
        return new StoreCredentials(Named, keys, null, null);
    }

    /// <summary>The key of an Azure storage account, for an emulator and for the accounts that still allow Shared Key.</summary>
    /// <param name="account">The storage account.</param>
    /// <param name="accountKey">The key, in the base64 the portal shows it in.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="ArgumentException">The key is not base64.</exception>
    public static StoreCredentials SharedKey(string account, string accountKey)
    {
        AzureAuthorizer key = AzureAuthorizer.SharedKey(account, accountKey);
        return new StoreCredentials(key.ToString()!, null, key, null);
    }

    /// <summary>An Azure shared access signature, appended to every request's query.</summary>
    /// <param name="signature">The signature's query, with or without its leading <c>?</c>.</param>
    /// <returns>The credentials.</returns>
    public static StoreCredentials SharedAccessSignature(string signature) =>
        new StoreCredentials("a shared access signature", null, AzureAuthorizer.SharedAccessSignature(signature), null);

    /// <summary>
    /// Bearer tokens from a source of the caller's: Microsoft Entra ID tokens for Azure, which
    /// Microsoft recommends over keys, or Google OAuth 2.0 tokens for Cloud Storage, from the cloud's own
    /// credential libraries for instance. A token is kept until five minutes before it expires, or until
    /// the service refuses it.
    /// </summary>
    /// <param name="source">
    /// Gives a token for the scope of the one service the credentials serve:
    /// <c>https://storage.azure.com/.default</c> for Azure,
    /// <c>https://www.googleapis.com/auth/devstorage.read_write</c> for Cloud Storage.
    /// </param>
    /// <returns>The credentials.</returns>
    public static StoreCredentials Token(Func<CancellationToken, ValueTask<AccessToken>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const string Named = "tokens from a source";
        RenewingToken tokens = new RenewingToken(source, Named, TimeProvider.System);
        return new StoreCredentials(Named, null, AzureAuthorizer.Bearer(tokens), tokens);
    }

    /// <summary>No credentials: an emulator, or a bucket or a container that anyone may read.</summary>
    /// <returns>The credentials.</returns>
    public static StoreCredentials Anonymous() => None;

    /// <summary>The credentials as a message may name them: their kind, never a secret.</summary>
    /// <returns>The kind of credentials.</returns>
    public override string ToString() => _named;

    /// <summary>The environment's credentials, read from <paramref name="host"/>: its variables, its files, its clock and its network.</summary>
    internal static StoreCredentials FromHost(CredentialHost host) =>
        new StoreCredentials("the environment's credentials", new AwsChain(host), new AzureChain(host), new GoogleChain(host));
}
