using System;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// One set of AWS keys: the access key id, the secret in native memory, prefixed with <c>AWS4</c> as
/// the first step of Signature Version 4 takes it, and the session token of temporary keys.
/// </summary>
internal sealed class AwsKeys : IDisposable
{
    /// <summary>Keys from their parts; the secret is copied into native memory.</summary>
    internal AwsKeys(string accessKeyId, string secretAccessKey, string? sessionToken, DateTimeOffset? expiration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessKeyId);
        ArgumentException.ThrowIfNullOrEmpty(secretAccessKey);
        AccessKeyId = accessKeyId;
        SigningSecret = new NativeSecret("AWS4", secretAccessKey);
        SessionToken = string.IsNullOrEmpty(sessionToken) ? null : sessionToken;
        Expiration = expiration;
    }

    /// <summary>The access key id, which names the identity and is no secret.</summary>
    internal string AccessKeyId { get; }

    /// <summary><c>AWS4</c> then the secret access key, the key of the first HMAC of a signature.</summary>
    internal NativeSecret SigningSecret { get; }

    /// <summary>The session token of temporary keys, or null.</summary>
    internal string? SessionToken { get; }

    /// <summary>When temporary keys expire, or null for keys that do not.</summary>
    internal DateTimeOffset? Expiration { get; }

    public void Dispose() => SigningSecret.Dispose();

    /// <summary>The keys as a message may name them: the access key id, never the secret.</summary>
    public override string ToString() => $"access key {AccessKeyId}";
}
