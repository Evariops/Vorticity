using System;
using System.Text;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// One set of AWS keys: the access key id, the secret as the first step of Signature Version 4 takes
/// it, prefixed with <c>AWS4</c>, and the session token of temporary keys.
/// </summary>
/// <remarks>
/// The secret stays in managed memory while the keys are in use, as it does in the string it came in
/// and in the SDKs' own clients: a copy kept elsewhere would not shorten its life on the heap.
/// </remarks>
internal sealed class AwsKeys
{
    private readonly byte[] _signingSecret;

    /// <summary>Keys from their parts.</summary>
    internal AwsKeys(string accessKeyId, string secretAccessKey, string? sessionToken, DateTimeOffset? expiration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessKeyId);
        ArgumentException.ThrowIfNullOrEmpty(secretAccessKey);
        AccessKeyId = accessKeyId;
        _signingSecret = new byte[4 + Encoding.UTF8.GetByteCount(secretAccessKey)];
        "AWS4"u8.CopyTo(_signingSecret);
        Encoding.UTF8.GetBytes(secretAccessKey, _signingSecret.AsSpan(4));
        SessionToken = string.IsNullOrEmpty(sessionToken) ? null : sessionToken;
        Expiration = expiration;
    }

    /// <summary>The keys a source of the caller's handed over.</summary>
    internal static AwsKeys From(AccessKey key) => new AwsKeys(key.AccessKeyId, key.SecretAccessKey, key.SessionToken, key.ExpiresOn);

    /// <summary>The access key id, which names the identity and is no secret.</summary>
    internal string AccessKeyId { get; }

    /// <summary><c>AWS4</c> then the secret access key, in UTF-8: the key of the first HMAC of a signature.</summary>
    internal ReadOnlySpan<byte> SigningSecret => _signingSecret;

    /// <summary>The session token of temporary keys, or null.</summary>
    internal string? SessionToken { get; }

    /// <summary>When temporary keys expire, or null for keys that do not.</summary>
    internal DateTimeOffset? Expiration { get; }

    /// <summary>The keys as a message may name them: the access key id, never the secret.</summary>
    public override string ToString() => $"access key {AccessKeyId}";
}
