using System;
using System.Net;
using System.Text;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The refusals every store words the same way as the core's stores do, written here so that the
/// package rests on the core's public surface only.
/// </summary>
internal static class StoreFailures
{
    /// <summary>The refusal of a read past an object's end.</summary>
    internal static ArgumentOutOfRangeException PastEnd(string key, long offset, long length) =>
        new ArgumentOutOfRangeException(nameof(offset), offset, $"'{key}' holds {length} bytes, so the range starts past its end.");

    /// <summary>The refusal of a read whose token condition failed: the object changed under its reader.</summary>
    internal static VortexFormatException Changed(string key, string expected, string actual) =>
        new VortexFormatException($"'{key}' changed under this reader: it was {expected} and is now {actual}.");

    /// <summary>
    /// What a refusal every service words alike becomes: an absent key, or a token that no longer
    /// matches a request bound to one, the object being <paramref name="now"/>; null for any other,
    /// which the service's error says more of.
    /// </summary>
    internal static Exception? OfStatus(HttpStatusCode status, string key, string? ifToken, string now) =>
        status == HttpStatusCode.NotFound ? ObjectNotFoundException.For(key)
        : status == HttpStatusCode.PreconditionFailed && ifToken is not null ? Changed(key, ifToken, now)
        : null;

    /// <summary>Refuses a key that is empty or longer than the 1 024 bytes of UTF-8 that S3 and Cloud Storage take.</summary>
    internal static void CheckKey(string key, string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // A character is at most three bytes of UTF-8, so a key of 341 or fewer is never counted.
        if (key.Length > 1024 / 3 && Encoding.UTF8.GetByteCount(key) > 1024)
        {
            throw new ArgumentException($"A key is at most 1 024 bytes of UTF-8 on {service}.", nameof(key));
        }
    }
}
