using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage.Azure;

/// <summary>
/// Shared Key, Azure Storage's HMAC-SHA-256 of a canonical string with the account key: the method,
/// the standard headers in a fixed order, the <c>x-ms-</c> headers sorted, and the resource with its
/// query sorted.
/// </summary>
/// <remarks>Microsoft recommends disallowing Shared Key on an account; the store takes it for an emulator and for the accounts that still use it.</remarks>
internal static class SharedKeySigner
{
    /// <summary>The <c>Authorization</c> header of <paramref name="request"/>, whose headers are all set.</summary>
    /// <param name="request">The request, its <c>x-ms-date</c> and every header it sends set.</param>
    /// <param name="account">The account.</param>
    /// <param name="key">The account key, decoded from its base64.</param>
    /// <param name="contentLength">The body's length, 0 for none.</param>
    internal static string Authorization(HttpRequestMessage request, string account, NativeSecret key, long contentLength) =>
        $"SharedKey {account}:{Signature(StringToSign(request, account, contentLength), key)}";

    /// <summary>
    /// Shared Key over a sub-request of a batch: its method, its date and the resource it names, with
    /// no body and no standard header.
    /// </summary>
    internal static string SubRequestAuthorization(string method, string path, string date, string account, NativeSecret key)
    {
        string toSign = $"{method}\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:{date}\n/{account}{path}";
        return $"SharedKey {account}:{Signature(toSign, key)}";
    }

    /// <summary>The canonical string of <paramref name="request"/>, which the signature is the HMAC of.</summary>
    internal static string StringToSign(HttpRequestMessage request, string account, long contentLength)
    {
        StringBuilder text = new StringBuilder(256);
        text.Append(request.Method.Method).Append('\n');
        text.Append(Standard(request, "Content-Encoding")).Append('\n');
        text.Append(Standard(request, "Content-Language")).Append('\n');
        text.Append(contentLength == 0 ? string.Empty : contentLength.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(Standard(request, "Content-MD5")).Append('\n');
        text.Append(Standard(request, "Content-Type")).Append('\n');
        text.Append('\n'); // Date: x-ms-date stands for it.
        text.Append(Standard(request, "If-Modified-Since")).Append('\n');
        text.Append(Standard(request, "If-Match")).Append('\n');
        text.Append(Standard(request, "If-None-Match")).Append('\n');
        text.Append(Standard(request, "If-Unmodified-Since")).Append('\n');
        text.Append(Standard(request, "Range")).Append('\n');

        List<(string Name, string Value)> headers = [];
        foreach (KeyValuePair<string, HeaderStringValues> header in request.Headers.NonValidated)
        {
            if (header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            {
                headers.Add((header.Key.ToLowerInvariant(), string.Join(',', header.Value).Trim()));
            }
        }

        if (request.Content is { } content)
        {
            foreach (KeyValuePair<string, HeaderStringValues> header in content.Headers.NonValidated)
            {
                if (header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add((header.Key.ToLowerInvariant(), string.Join(',', header.Value).Trim()));
                }
            }
        }

        headers.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach ((string name, string value) in headers)
        {
            text.Append(name).Append(':').Append(value).Append('\n');
        }

        Uri uri = request.RequestUri!;
        text.Append('/').Append(account).Append(uri.AbsolutePath);
        SortedDictionary<string, List<string>> query = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]).ToLowerInvariant();
            string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
            if (!query.TryGetValue(name, out List<string>? values))
            {
                values = [];
                query.Add(name, values);
            }

            values.Add(value);
        }

        foreach ((string name, List<string> values) in query)
        {
            values.Sort(StringComparer.Ordinal);
            text.Append('\n').Append(name).Append(':').AppendJoin(',', values);
        }

        return text.ToString();
    }

    /// <summary>The version of the service SAS <see cref="BlobReadSas"/> signs, whose string has sixteen fields.</summary>
    internal const string SasVersion = "2022-11-02";

    /// <summary>
    /// A service SAS that lets the service read one blob until <paramref name="expiry"/>, as a copy from
    /// a URL needs its source authorized: the query that follows the blob's URL.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="container">The container.</param>
    /// <param name="blob">The blob's name, unencoded.</param>
    /// <param name="key">The account key.</param>
    /// <param name="expiry">When the signature stops holding.</param>
    internal static string BlobReadSas(string account, string container, string blob, NativeSecret key, DateTimeOffset expiry)
    {
        string se = expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string toSign = BlobReadSasString(account, container, blob, se);
        return $"sv={SasVersion}&sr=b&sp=r&se={Uri.EscapeDataString(se)}&sig={Uri.EscapeDataString(Signature(toSign, key))}";
    }

    /// <summary>
    /// The string a read-only service SAS on a blob signs, for versions from 2020-12-06: the permissions,
    /// the start, the expiry, the resource, the identifier, the addresses, the protocol, the version, the
    /// resource type, the snapshot, the encryption scope and the five response headers, empty but these.
    /// </summary>
    internal static string BlobReadSasString(string account, string container, string blob, string expiry) =>
        string.Join('\n', "r", string.Empty, expiry, $"/blob/{account}/{container}/{blob}", string.Empty, string.Empty, string.Empty,
            SasVersion, "b", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static string Signature(string toSign, NativeSecret key)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key.Span, Encoding.UTF8.GetBytes(toSign), mac);
        return Convert.ToBase64String(mac);
    }

    private static string Standard(HttpRequestMessage request, string name)
    {
        if (request.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values)
            || (request.Content is not null && request.Content.Headers.NonValidated.TryGetValues(name, out values)))
        {
            return string.Join(',', values);
        }

        return string.Empty;
    }
}
