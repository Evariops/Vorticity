using System;

namespace Vorticity;

/// <summary>Why a sealed object could not be read, or a plain one was refused.</summary>
public enum VortexEncryptionError
{
    /// <summary>No key of the session's keyring unwraps the object's data key, or the session holds no keyring.</summary>
    NoKey = 0,

    /// <summary>
    /// A frame, a commitment, an epoch or a binding failed: the object was altered, truncated, moved or
    /// assembled from others. Tampering and corruption cannot be told apart, so they are reported alike.
    /// </summary>
    Unauthenticated = 1,

    /// <summary>A plain object where the policy asks for a sealed one.</summary>
    Refused = 2,
}

/// <summary>A sealed object could not be read with the session's keys, or a plain one was refused.</summary>
/// <remarks>
/// The message names the key id when a key is missing, and never carries key material. A format
/// version this build does not know is a <see cref="VortexUnsupportedException"/> of kind
/// <see cref="ComponentKind.Encryption"/> instead.
/// </remarks>
public sealed class VortexEncryptionException : VortexException
{
    /// <summary>A failure of kind <paramref name="error"/>, described by <paramref name="message"/>.</summary>
    /// <param name="error">Why the object could not be read.</param>
    /// <param name="message">What failed, and what would work.</param>
    public VortexEncryptionException(VortexEncryptionError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>A failure of kind <paramref name="error"/>, described by <paramref name="message"/>, found through <paramref name="innerException"/>.</summary>
    /// <param name="error">Why the object could not be read.</param>
    /// <param name="message">What failed, and what would work.</param>
    /// <param name="innerException">The cause.</param>
    public VortexEncryptionException(VortexEncryptionError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>Why the object could not be read.</summary>
    public VortexEncryptionError Error { get; }

    /// <summary>The key id the object names, when the failure is about its key; empty otherwise.</summary>
    public string KeyId { get; init; } = string.Empty;

    internal static VortexEncryptionException Unauthenticated(string message, Exception? cause = null) =>
        cause is null
            ? new VortexEncryptionException(VortexEncryptionError.Unauthenticated, message)
            : new VortexEncryptionException(VortexEncryptionError.Unauthenticated, message, cause);

    internal static VortexEncryptionException NoKey(string keyId, string message, Exception? cause = null) =>
        cause is null
            ? new VortexEncryptionException(VortexEncryptionError.NoKey, message) { KeyId = keyId }
            : new VortexEncryptionException(VortexEncryptionError.NoKey, message, cause) { KeyId = keyId };

    internal static VortexEncryptionException Refused(string message) =>
        new VortexEncryptionException(VortexEncryptionError.Refused, message);
}
