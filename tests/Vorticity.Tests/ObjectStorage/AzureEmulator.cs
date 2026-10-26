// Azure Blob Storage in memory, under the store's HttpClient, in the manner of the S3 emulator.
//
// IT CHECKS EVERY SHARED KEY SIGNATURE against a canonical string it builds itself from the request it
// received, written apart from the store's, and the bearer token or the shared access signature of a
// store authorized so. Bodies' CRC-64s are checked, the conditions applied, and a batch's every
// sub-request authorized on its own.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Vorticity.Tests.ObjectStorage;

internal sealed class AzureEmulator : HttpMessageHandler
{
    internal const string Account = "vxaccount";
    internal static readonly string Key = Convert.ToBase64String(Encoding.ASCII.GetBytes("an emulator's account key, 64 bytes long, for HMAC-SHA-256 keys!!"));

    private readonly object _gate = new object();
    private readonly Dictionary<string, SortedDictionary<string, Blob>> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _blocks = new(StringComparer.Ordinal);
    private long _etags;

    /// <summary>The path the account's endpoint carries before the container, <c>/account</c> on an emulator that addresses accounts by path, or none.</summary>
    internal string BasePath { get; init; } = string.Empty;

    /// <summary>The bearer token a store authorized by token must send, or null to take none.</summary>
    internal string? Token { get; set; }

    /// <summary>The signature a store authorized by a shared access signature must send, or null to take none.</summary>
    internal string? Sas { get; init; }

    /// <summary>The most blobs a listing page holds.</summary>
    internal int PageSize { get; set; } = 5000;

    /// <summary>The service's clock.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Runs before each request is served: returns an answer to send instead, or throws to fail the connection.</summary>
    internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Runs after a write is applied: true loses its answer.</summary>
    internal Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

    internal ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

    internal int Requests => Log.Count;

    internal void Seed(string container, string blob, byte[] bytes, DateTimeOffset? retainUntil = null, bool legalHold = false)
    {
        lock (_gate)
        {
            Container(container)[blob] = new Blob(bytes, NextETag(), Stamp(), retainUntil, legalHold, []);
        }
    }

    internal byte[]? BytesOf(string container, string blob)
    {
        lock (_gate)
        {
            return Container(container).TryGetValue(blob, out Blob b) ? b.Bytes : null;
        }
    }

    internal string ETagOf(string container, string blob)
    {
        lock (_gate)
        {
            return Container(container)[blob].ETag;
        }
    }

    internal int Count(string container)
    {
        lock (_gate)
        {
            return Container(container).Count;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int index = Log.Count;
        Uri uri = request.RequestUri!;
        string query = Sas is null ? uri.Query : uri.Query.Replace("&" + Sas, string.Empty, StringComparison.Ordinal).Replace("?" + Sas, "?", StringComparison.Ordinal);
        Log.Enqueue($"{request.Method} {uri.AbsolutePath}{(query is "?" ? string.Empty : query)}{(request.Headers.TryGetValues("x-ms-range", out IEnumerable<string>? range) ? " " + range.First() : string.Empty)}");
        if (Intercept?.Invoke(request, index) is { } intercepted)
        {
            intercepted.RequestMessage = request;
            return intercepted;
        }

        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        (HttpResponseMessage response, bool applied) = Serve(request, body);
        if (applied && LoseAnswer?.Invoke(request) == true)
        {
            response.Dispose();
            throw new HttpRequestException("The connection was reset after the request was served.");
        }

        response.RequestMessage = request;
        response.Headers.TryAddWithoutValidation("x-ms-request-id", $"REQ{index:D6}");
        response.Headers.Date = Now();
        return response;
    }

    private (HttpResponseMessage, bool) Serve(HttpRequestMessage request, byte[] body)
    {
        if (Authorize(request) is { } refused)
        {
            return (refused, false);
        }

        if (!request.Headers.Contains("x-ms-version"))
        {
            return (Error(HttpStatusCode.BadRequest, "MissingRequiredHeader", "x-ms-version"), false);
        }

        (string container, string blob) = Address(request.RequestUri!);
        Dictionary<string, string> query = Query(request.RequestUri!);
        HttpMethod method = request.Method;
        if (blob.Length == 0 && query.GetValueOrDefault("restype") == "container")
        {
            if (method == HttpMethod.Get && query.GetValueOrDefault("comp") == "list")
            {
                return (List(container, query), false);
            }

            if (method == HttpMethod.Post && query.GetValueOrDefault("comp") == "batch")
            {
                return Batch(request, container, body);
            }
        }
        else if (method == HttpMethod.Get)
        {
            return (Get(request, container, blob), false);
        }
        else if (method == HttpMethod.Head)
        {
            return (Head(container, blob), false);
        }
        else if (method == HttpMethod.Put && query.GetValueOrDefault("comp") == "block")
        {
            if (Damaged(request, body) is { } damaged)
            {
                return (damaged, false);
            }

            lock (_gate)
            {
                _blocks[$"{container}/{blob}/{query["blockid"]}"] = body;
            }

            return (new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) }, true);
        }
        else if (method == HttpMethod.Put && query.GetValueOrDefault("comp") == "blocklist")
        {
            if (Damaged(request, body) is { } damaged)
            {
                return (damaged, false);
            }

            List<byte> assembled = [];
            lock (_gate)
            {
                foreach (XElement latest in XDocument.Parse(Encoding.UTF8.GetString(body)).Root!.Elements("Latest"))
                {
                    if (!_blocks.TryGetValue($"{container}/{blob}/{latest.Value}", out byte[]? block))
                    {
                        return (Error(HttpStatusCode.BadRequest, "InvalidBlockList", "The specified block list is invalid."), false);
                    }

                    assembled.AddRange(block);
                }
            }

            return Store(request, container, blob, [.. assembled]);
        }
        else if (method == HttpMethod.Put)
        {
            if (request.Headers.TryGetValues("x-ms-blob-type", out IEnumerable<string>? type) is false || type.First() != "BlockBlob")
            {
                return (Error(HttpStatusCode.BadRequest, "MissingRequiredHeader", "x-ms-blob-type"), false);
            }

            if (Damaged(request, body) is { } damaged)
            {
                return (damaged, false);
            }

            return Store(request, container, blob, body);
        }

        return (Error(HttpStatusCode.NotImplemented, "NotImplemented", "The emulator does not serve this request."), false);
    }

    private (HttpResponseMessage, bool) Store(HttpRequestMessage request, string container, string blob, byte[] bytes)
    {
        lock (_gate)
        {
            SortedDictionary<string, Blob> blobs = Container(container);
            bool exists = blobs.TryGetValue(blob, out Blob current);
            if (Header(request, "If-None-Match") == "*" && exists)
            {
                return (Error(HttpStatusCode.Conflict, "BlobAlreadyExists", "The specified blob already exists."), false);
            }

            if (Header(request, "If-Match") is { } match)
            {
                if (!exists)
                {
                    return (Error(HttpStatusCode.NotFound, "BlobNotFound", "The specified blob does not exist."), false);
                }

                if (match != current.ETag)
                {
                    return (Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet", "The condition specified using HTTP conditional header(s) is not met."), false);
                }
            }

            if (exists && (current.LegalHold || current.RetainUntil > Now()))
            {
                return (Error(HttpStatusCode.Conflict, "BlobImmutableDueToPolicy", "This operation is not permitted as the blob is immutable due to a policy."), false);
            }

            Dictionary<string, string> metadata = request.Headers
                .Where(h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.First(), StringComparer.Ordinal);
            Blob stored = new Blob(bytes, NextETag(), Stamp(), null, false, metadata);
            blobs[blob] = stored;
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("ETag", stored.ETag);
            return (response, true);
        }
    }

    private HttpResponseMessage Get(HttpRequestMessage request, string container, string blob)
    {
        Blob? found = Find(container, blob);
        if (found is not { } b)
        {
            return Error(HttpStatusCode.NotFound, "BlobNotFound", "The specified blob does not exist.");
        }

        if (Header(request, "If-Match") is { } match && match != b.ETag)
        {
            return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet", "The condition specified using HTTP conditional header(s) is not met.");
        }

        long first = 0, last = b.Bytes.Length - 1;
        if (Header(request, "x-ms-range") is { } range)
        {
            string[] parts = range[6..].Split('-');
            first = long.Parse(parts[0], CultureInfo.InvariantCulture);
            last = Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), b.Bytes.Length - 1);
            if (first >= b.Bytes.Length)
            {
                HttpResponseMessage refused = Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "The range specified is invalid for the current size of the resource.");
                refused.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{b.Bytes.Length}");
                return refused;
            }
        }

        byte[] content = b.Bytes.AsSpan((int)first, (int)(last - first + 1)).ToArray();
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content) };
        response.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {first}-{last}/{b.Bytes.Length}");
        response.Headers.TryAddWithoutValidation("ETag", b.ETag);
        return response;
    }

    private HttpResponseMessage Head(string container, string blob)
    {
        Blob? found = Find(container, blob);
        if (found is not { } b)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }

        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = b.Bytes.Length;
        response.Content.Headers.LastModified = b.Modified;
        response.Headers.TryAddWithoutValidation("ETag", b.ETag);
        if (b.RetainUntil is { } until)
        {
            response.Headers.TryAddWithoutValidation("x-ms-immutability-policy-until-date", until.ToString("r", CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("x-ms-immutability-policy-mode", "locked");
        }

        response.Headers.TryAddWithoutValidation("x-ms-legal-hold", b.LegalHold ? "true" : "false");
        foreach ((string name, string value) in b.Metadata)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private HttpResponseMessage List(string container, Dictionary<string, string> query)
    {
        string prefix = query.GetValueOrDefault("prefix", string.Empty);

        // A marker is opaque, as the service's are: here the last name of the page before, in base64.
        string? from = query.GetValueOrDefault("marker") is { } marker
            ? Encoding.UTF8.GetString(Convert.FromBase64String(marker))
            : query.GetValueOrDefault("startFrom");
        bool inclusive = !query.ContainsKey("marker");
        int max = Math.Min(int.Parse(query.GetValueOrDefault("maxresults", "5000"), CultureInfo.InvariantCulture), PageSize);
        List<(string Name, Blob Blob)> blobs;
        lock (_gate)
        {
            blobs = [.. Container(container)
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && (from is null || (inclusive ? string.CompareOrdinal(pair.Key, from) >= 0 : string.CompareOrdinal(pair.Key, from) > 0)))
                .Select(pair => (pair.Key, pair.Value))];
        }

        StringBuilder xml = new StringBuilder($"<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ServiceEndpoint=\"https://{Account}.blob.core.windows.net/\" ContainerName=\"{container}\"><Prefix>{Escape(prefix)}</Prefix><MaxResults>{max}</MaxResults><Blobs>");
        foreach ((string name, Blob b) in blobs.Take(max))
        {
            // A name XML cannot carry is URL-encoded, and says so.
            bool odd = name.Any(c => c < ' ');
            string written = odd ? $"<Name Encoded=\"true\">{Uri.EscapeDataString(name)}</Name>" : $"<Name>{Escape(name)}</Name>";
            xml.Append(CultureInfo.InvariantCulture, $"<Blob>{written}<Properties><Creation-Time>{b.Modified:r}</Creation-Time><Last-Modified>{b.Modified:r}</Last-Modified><Etag>{b.ETag.Trim('"')}</Etag><Content-Length>{b.Bytes.Length}</Content-Length><BlobType>BlockBlob</BlobType>");
            if (b.RetainUntil is { } until)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<ImmutabilityPolicyUntilDate>{until:r}</ImmutabilityPolicyUntilDate><ImmutabilityPolicyMode>locked</ImmutabilityPolicyMode>");
            }

            xml.Append(CultureInfo.InvariantCulture, $"<LegalHold>{(b.LegalHold ? "true" : "false")}</LegalHold></Properties></Blob>");
        }

        xml.Append("</Blobs>");
        xml.Append(blobs.Count > max ? $"<NextMarker>{Convert.ToBase64String(Encoding.UTF8.GetBytes(blobs[max - 1].Name))}</NextMarker>" : "<NextMarker />");
        xml.Append("</EnumerationResults>");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private (HttpResponseMessage, bool) Batch(HttpRequestMessage request, string container, byte[] body)
    {
        string type = request.Content!.Headers.ContentType!.ToString();
        string boundary = type[(type.IndexOf("boundary=", StringComparison.Ordinal) + 9)..];
        string text = Encoding.UTF8.GetString(body);
        string[] parts = text.Split("--" + boundary, StringSplitOptions.None);
        StringBuilder answer = new StringBuilder();
        const string Back = "batchresponse_emulator";
        int count = 0;
        foreach (string part in parts)
        {
            int line = part.IndexOf("DELETE ", StringComparison.Ordinal);
            if (line < 0)
            {
                continue;
            }

            if (++count > 256)
            {
                return (Error(HttpStatusCode.BadRequest, "ExceedsMaxBatchRequestCount", "More than 256 sub-requests."), false);
            }

            string id = Field(part, "Content-ID")!;
            string target = part[(line + 7)..part.IndexOf(" HTTP/1.1", line, StringComparison.Ordinal)];
            string path = target.Split('?')[0];
            string date = Field(part, "x-ms-date")!;
            string? authorization = Field(part, "Authorization");
            int status;
            string? code = null;
            if (!SubRequestAuthorized(path, target, date, authorization))
            {
                (status, code) = (403, "AuthenticationFailed");
            }
            else
            {
                string blob = Uri.UnescapeDataString(path[(BasePath.Length + container.Length + 2)..]);
                lock (_gate)
                {
                    SortedDictionary<string, Blob> blobs = Container(container);
                    if (!blobs.TryGetValue(blob, out Blob b))
                    {
                        (status, code) = (404, "BlobNotFound");
                    }
                    else if (b.LegalHold || b.RetainUntil > Now())
                    {
                        (status, code) = (409, "BlobImmutableDueToPolicy");
                    }
                    else
                    {
                        blobs.Remove(blob);
                        status = 202;
                    }
                }
            }

            answer.Append("--").Append(Back).Append("\r\nContent-Type: application/http\r\n").Append(CultureInfo.InvariantCulture, $"Content-ID: {id}\r\n\r\n")
                .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {(status == 202 ? "Accepted" : "Refused")}\r\n");
            if (code is not null)
            {
                answer.Append("x-ms-error-code: ").Append(code).Append("\r\n");
            }

            answer.Append("x-ms-version: 2025-01-05\r\n\r\n");
        }

        answer.Append("--").Append(Back).Append("--\r\n");
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(answer.ToString(), Encoding.UTF8) };
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={Back}");
        return (response, true);
    }

    /// <summary>Checks the authorization of a request: its Shared Key against the emulator's own canonical string, its token, or its signature.</summary>
    private HttpResponseMessage? Authorize(HttpRequestMessage request)
    {
        if (Token is not null || Sas is not null)
        {
            bool tokenHolds = Token is not null && request.Headers.Authorization?.ToString() == "Bearer " + Token;
            bool sasHolds = Sas is not null && request.RequestUri!.Query.Contains(Sas, StringComparison.Ordinal);
            return tokenHolds || sasHolds ? null : Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationInfo", "Server failed to authenticate the request.");
        }

        string? date = Header(request, "x-ms-date");
        if (date is null || (DateTimeOffset.ParseExact(date, "r", CultureInfo.InvariantCulture) - Now()).Duration() > TimeSpan.FromMinutes(15))
        {
            return Error(HttpStatusCode.Forbidden, "AuthenticationFailed", "Request date header too old.");
        }

        string expected = $"SharedKey {Account}:{Sign(CanonicalString(request))}";
        return request.Headers.Authorization?.ToString() == expected
            ? null
            : Error(HttpStatusCode.Forbidden, "AuthenticationFailed", $"Server failed to authenticate the request. The string to sign was {CanonicalString(request).Replace("\n", "\\n", StringComparison.Ordinal)}.");
    }

    private bool SubRequestAuthorized(string path, string target, string date, string? authorization)
    {
        if (Token is not null)
        {
            return authorization == "Bearer " + Token;
        }

        if (Sas is not null)
        {
            return target.Contains(Sas, StringComparison.Ordinal);
        }

        return authorization == $"SharedKey {Account}:{Sign($"DELETE\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:{date}\n/{Account}{path}")}";
    }

    /// <summary>The emulator's own Shared Key string: the method, the twelve standard headers, the x-ms- headers sorted, the resource and its query sorted.</summary>
    private static string CanonicalString(HttpRequestMessage request)
    {
        string Standard(string name) => Header(request, name) ?? string.Empty;
        long length = request.Content?.Headers.ContentLength ?? 0;
        string[] standard =
        [
            request.Method.Method, Standard("Content-Encoding"), Standard("Content-Language"), length == 0 ? string.Empty : length.ToString(CultureInfo.InvariantCulture),
            Standard("Content-MD5"), Standard("Content-Type"), string.Empty, Standard("If-Modified-Since"), Standard("If-Match"), Standard("If-None-Match"),
            Standard("If-Unmodified-Since"), Standard("Range"),
        ];
        IEnumerable<string> msHeaders = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: string.Join(",", h.Value).Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .Select(h => $"{h.Name}:{h.Value}\n");
        IEnumerable<string> query = Query(request.RequestUri!)
            .OrderBy(pair => pair.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(pair => $"\n{pair.Key.ToLowerInvariant()}:{pair.Value}");
        return string.Join("\n", standard) + "\n" + string.Concat(msHeaders) + $"/{Account}{request.RequestUri!.AbsolutePath}" + string.Concat(query);
    }

    private static string Sign(string text) =>
        Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(Key), Encoding.UTF8.GetBytes(text)));

    /// <summary>The refusal of a body whose <c>x-ms-content-crc64</c> does not match it, or null.</summary>
    private static HttpResponseMessage? Damaged(HttpRequestMessage request, byte[] body)
    {
        if (Header(request, "x-ms-content-crc64") is not { } crc)
        {
            return null;
        }

        byte[] expected = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(expected, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, body));
        return crc == Convert.ToBase64String(expected)
            ? null
            : Error(HttpStatusCode.BadRequest, "Crc64Mismatch", "The CRC64 value specified in the request did not match with the CRC64 value calculated by the server.");
    }

    private (string Container, string Blob) Address(Uri uri)
    {
        string path = uri.AbsolutePath[BasePath.Length..].TrimStart('/');
        int slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (path, string.Empty) : (path[..slash], Uri.UnescapeDataString(path[(slash + 1)..]));
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            query[Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals])] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return query;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) || request.Content?.Headers.TryGetValues(name, out values) == true
            ? string.Join(",", values!)
            : null;

    private static string? Field(string part, string name)
    {
        foreach (string line in part.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(name.Length + 1)..].Trim();
            }
        }

        return null;
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code, string message)
    {
        HttpResponseMessage response = new HttpResponseMessage(status)
        {
            Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{Escape(message)}</Message></Error>", Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private SortedDictionary<string, Blob> Container(string container)
    {
        if (!_containers.TryGetValue(container, out SortedDictionary<string, Blob>? blobs))
        {
            blobs = new SortedDictionary<string, Blob>(StringComparer.Ordinal);
            _containers.Add(container, blobs);
        }

        return blobs;
    }

    private Blob? Find(string container, string blob)
    {
        lock (_gate)
        {
            return Container(container).TryGetValue(blob, out Blob b) ? b : null;
        }
    }

    private DateTimeOffset Stamp()
    {
        DateTimeOffset now = Now();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    private string NextETag() => $"\"0x8D{Interlocked.Increment(ref _etags):X12}\"";

    private readonly record struct Blob(byte[] Bytes, string ETag, DateTimeOffset Modified, DateTimeOffset? RetainUntil, bool LegalHold, Dictionary<string, string> Metadata);
}
