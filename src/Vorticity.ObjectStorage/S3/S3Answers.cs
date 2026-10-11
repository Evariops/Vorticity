using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage.S3;

/// <summary>What S3's answers say: its errors, its listings, its headers.</summary>
internal static class S3Answers
{
    /// <summary>The most an error's or a listing's body may hold: a page of a thousand long keys is well under it.</summary>
    internal const int MaxXmlBytes = 16 << 20;

    /// <summary>An error's code, message, request id and, for a range past the end, the object's size.</summary>
    internal static (string? Code, string? Message, string? RequestId, long? ActualObjectSize) ParseError(ReadOnlySpan<byte> xml)
    {
        string? code = null, message = null, requestId = null;
        long? size = null;
        XmlScanner scanner = new XmlScanner(xml);
        while (scanner.Read())
        {
            if (scanner.Token != XmlToken.Start)
            {
                continue;
            }

            if (XmlScanner.Is(scanner.Name, "Code"u8))
            {
                code = scanner.ReadText();
            }
            else if (XmlScanner.Is(scanner.Name, "Message"u8))
            {
                message = ServiceError.Clip(scanner.ReadText());
            }
            else if (XmlScanner.Is(scanner.Name, "RequestId"u8))
            {
                requestId = scanner.ReadText();
            }
            else if (XmlScanner.Is(scanner.Name, "ActualObjectSize"u8))
            {
                size = scanner.ReadInt64();
            }
        }

        return (code, message, requestId, size);
    }

    /// <summary>
    /// A page of <c>ListObjectsV2</c>: each object's key, decoded from the URL encoding asked for and
    /// stripped of <paramref name="strip"/>, with its length, ETag and date; and the token of the next
    /// page, or null for the last.
    /// </summary>
    internal static string? ParseListing(ReadOnlySpan<byte> xml, int strip, bool lockReported, List<ObjectListing> into)
    {
        string? next = null;
        bool truncated = false;
        XmlScanner scanner = new XmlScanner(xml);
        while (scanner.Read())
        {
            if (scanner.Token != XmlToken.Start)
            {
                continue;
            }

            if (XmlScanner.Is(scanner.Name, "Contents"u8))
            {
                string? key = null, etag = null;
                long size = -1;
                DateTimeOffset modified = default;
                while (scanner.Read() && scanner.Token != XmlToken.End)
                {
                    if (scanner.Token != XmlToken.Start)
                    {
                        continue;
                    }

                    if (XmlScanner.Is(scanner.Name, "Key"u8))
                    {
                        key = UriEncoding.DecodeForm(scanner.ReadText());
                    }
                    else if (XmlScanner.Is(scanner.Name, "ETag"u8))
                    {
                        etag = scanner.ReadText();
                    }
                    else if (XmlScanner.Is(scanner.Name, "Size"u8))
                    {
                        size = scanner.ReadInt64();
                    }
                    else if (XmlScanner.Is(scanner.Name, "LastModified"u8))
                    {
                        modified = IsoDate(scanner.ReadText());
                    }
                    else
                    {
                        scanner.Skip();
                    }
                }

                if (key is null || etag is null || size < 0 || key.Length < strip)
                {
                    throw new ObjectStoreException("The service listed an object without its key, its ETag or its size.");
                }

                into.Add(new ObjectListing(key[strip..], new ObjectHead(size, etag, modified), lockReported));
            }
            else if (XmlScanner.Is(scanner.Name, "IsTruncated"u8))
            {
                truncated = scanner.ReadText() == "true";
            }
            else if (XmlScanner.Is(scanner.Name, "NextContinuationToken"u8))
            {
                next = scanner.ReadText();
            }
            else if (!XmlScanner.Is(scanner.Name, "ListBucketResult"u8))
            {
                scanner.Skip();
            }
        }

        if (truncated && string.IsNullOrEmpty(next))
        {
            throw new ObjectStoreException("The service said its listing goes on and gave no token to resume it.");
        }

        return truncated ? next : null;
    }

    /// <summary>A header's first value, or null; read without validation, as the service wrote it.</summary>
    internal static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values)
            || response.Content.Headers.NonValidated.TryGetValues(name, out values))
        {
            foreach (string value in values)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The object's length from <c>Content-Range: bytes a-b/total</c>, or from <c>bytes */total</c>,
    /// and the first byte the answer holds; null when the header is missing or says no length.
    /// </summary>
    internal static (long First, long Total)? ContentRange(HttpResponseMessage response)
    {
        string? range = Header(response, "Content-Range");
        if (range is null || !range.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReadOnlySpan<char> spec = range.AsSpan(6).Trim();
        int slash = spec.IndexOf('/');
        if (slash < 0 || !long.TryParse(spec[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long total))
        {
            return null;
        }

        ReadOnlySpan<char> span = spec[..slash];
        if (span is "*")
        {
            return (0, total);
        }

        int dash = span.IndexOf('-');
        return dash > 0 && long.TryParse(span[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out long first)
            ? (first, total)
            : null;
    }

    /// <summary>A head's date, from <c>Last-Modified</c>.</summary>
    internal static DateTimeOffset LastModified(HttpResponseMessage response) =>
        Header(response, "Last-Modified") is { } text
        && DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
            ? date
            : default;

    /// <summary>An ISO 8601 date, as a listing and Object Lock write them.</summary>
    internal static DateTimeOffset IsoDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : throw new ObjectStoreException($"The service wrote a date that is not one: '{ServiceError.Clip(text)}'.");

    /// <summary>How long <c>Retry-After</c> asks to wait, in seconds or until a date.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        return retry?.Date is { } until ? until - DateTimeOffset.UtcNow : null;
    }
}
