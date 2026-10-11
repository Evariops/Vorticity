using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Vorticity.ObjectStorage.Http;

/// <summary>What every service's answers say in their headers, read the same way by the three stores.</summary>
internal static class HttpAnswers
{
    /// <summary>The most a listing's or a batch's answer may hold: a page of a thousand long keys is well under it.</summary>
    internal const int MaxDocumentBytes = 16 << 20;

    /// <summary>The most any other answer read whole may hold: an error, or the start or completion of an upload.</summary>
    internal const int MaxAnswerBytes = 64 << 10;

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
    /// A header of the request an answer replies to, or null: what the refused request was signed with,
    /// which a request signed since with renewed credentials does not share.
    /// </summary>
    internal static string? RequestHeader(HttpResponseMessage response, string name)
    {
        if (response.RequestMessage?.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values) == true)
        {
            foreach (string value in values)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The bearer token the request an answer replies to carried, or null when it carried none.</summary>
    internal static string? BearerOf(HttpResponseMessage response) =>
        RequestHeader(response, "Authorization") is { } authorization && authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? authorization["Bearer ".Length..]
            : null;

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
