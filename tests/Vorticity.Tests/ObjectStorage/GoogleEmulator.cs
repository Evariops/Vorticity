// Cloud Storage in memory, under the store's HttpClient: the XML API for data and uploads in parts,
// the JSON API for metadata, listings and batches, as the store speaks them. Generations are tokens,
// preconditions are applied, CRC-32Cs checked, and a bearer token required when one is set.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Vorticity.Tests.ObjectStorage;

internal sealed class GoogleEmulator : HttpMessageHandler
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, SortedDictionary<string, StoredObject>> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Bucket, string Name, Dictionary<string, string> Metadata, SortedDictionary<int, byte[]> Parts)> _uploads = new(StringComparer.Ordinal);
    private long _generation = 1_700_000_000_000_000;

    /// <summary>The bearer token every request must carry, or null to take any.</summary>
    internal string? Token { get; set; }

    internal int PageSize { get; set; } = 1000;

    internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Intercept { get; set; }

    internal Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

    internal ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

    internal int Requests => Log.Count;

    internal int OpenUploads
    {
        get
        {
            lock (_gate)
            {
                return _uploads.Count;
            }
        }
    }

    internal void Seed(string bucket, string name, byte[] bytes, DateTimeOffset? retainUntil = null, bool held = false)
    {
        lock (_gate)
        {
            Bucket(bucket)[name] = new StoredObject(bytes, NextGeneration(), Stamp(), retainUntil, held, []);
        }
    }

    internal string GenerationOf(string bucket, string name)
    {
        lock (_gate)
        {
            return Bucket(bucket)[name].Generation;
        }
    }

    internal byte[]? BytesOf(string bucket, string name)
    {
        lock (_gate)
        {
            return Bucket(bucket).TryGetValue(name, out StoredObject o) ? o.Bytes : null;
        }
    }

    internal int Count(string bucket)
    {
        lock (_gate)
        {
            return Bucket(bucket).Count;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int index = Log.Count;
        Uri uri = request.RequestUri!;
        string query = uri.Query.Contains("fields=", StringComparison.Ordinal) ? Strip(uri.Query, "fields") : uri.Query;
        Log.Enqueue($"{request.Method} {uri.AbsolutePath}{query}{(request.Headers.TryGetValues("Range", out IEnumerable<string>? range) ? " " + range.First() : string.Empty)}");
        if (Intercept?.Invoke(request, index) is { } intercepted)
        {
            intercepted.RequestMessage = request;
            return intercepted;
        }

        if (Token is not null && request.Headers.Authorization?.ToString() != "Bearer " + Token)
        {
            // The answer names its request, as the platform's handler's do: the store reads the token it refused there.
            HttpResponseMessage refused = Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""");
            refused.RequestMessage = request;
            return refused;
        }

        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        (HttpResponseMessage response, bool applied) = Serve(request, body);
        if (applied && LoseAnswer?.Invoke(request) == true)
        {
            response.Dispose();
            throw new HttpRequestException("The connection was reset after the request was served.");
        }

        response.RequestMessage = request;
        response.Headers.TryAddWithoutValidation("x-guploader-uploadid", $"UP{index:D6}");
        return response;
    }

    private (HttpResponseMessage, bool) Serve(HttpRequestMessage request, byte[] body)
    {
        string path = request.RequestUri!.AbsolutePath;
        Dictionary<string, string> query = Query(request.RequestUri!);
        HttpMethod method = request.Method;
        if (path == "/batch/storage/v1" && method == HttpMethod.Post)
        {
            return Batch(request, body);
        }

        if (path.StartsWith("/storage/v1/b/", StringComparison.Ordinal))
        {
            string[] parts = path["/storage/v1/b/".Length..].Split('/', 3);
            string bucket = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2 && method == HttpMethod.Get)
            {
                return (List(bucket, query), false);
            }

            string name = Uri.UnescapeDataString(parts[2]);
            return (Metadata(bucket, name), false);
        }

        string rest = path.TrimStart('/');
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string xmlBucket = rest[..slash];
        string objectName = Uri.UnescapeDataString(rest[(slash + 1)..]);
        if (method == HttpMethod.Get)
        {
            return (Get(request, xmlBucket, objectName), false);
        }

        if (method == HttpMethod.Post && query.ContainsKey("uploads"))
        {
            string id = $"upload-{Interlocked.Increment(ref _generation)}";
            lock (_gate)
            {
                _uploads[id] = (xmlBucket, objectName, MetadataOf(request), new SortedDictionary<int, byte[]>());
            }

            return (Xml(HttpStatusCode.OK, $"<?xml version='1.0' encoding='UTF-8'?><InitiateMultipartUploadResult xmlns='http://s3.amazonaws.com/doc/2006-03-01/'><Bucket>{xmlBucket}</Bucket><Key>{objectName}</Key><UploadId>{id}</UploadId></InitiateMultipartUploadResult>"), true);
        }

        if (method == HttpMethod.Put && query.TryGetValue("uploadId", out string? upload))
        {
            if (Damaged(request, body) is { } damaged)
            {
                return (damaged, false);
            }

            lock (_gate)
            {
                if (!_uploads.TryGetValue(upload, out var open))
                {
                    return (XmlError(HttpStatusCode.NotFound, "NoSuchUpload"), false);
                }

                int number = int.Parse(query["partNumber"], CultureInfo.InvariantCulture);
                open.Parts[number] = body;
                HttpResponseMessage part = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                part.Headers.TryAddWithoutValidation("ETag", $"\"part-{number}-{body.Length}\"");
                return (part, true);
            }
        }

        if (method == HttpMethod.Post && query.TryGetValue("uploadId", out string? completed))
        {
            lock (_gate)
            {
                if (!_uploads.Remove(completed, out var open))
                {
                    return (XmlError(HttpStatusCode.NotFound, "NoSuchUpload"), false);
                }

                List<byte> whole = [];
                foreach (XElement part in XDocument.Parse(Encoding.UTF8.GetString(body)).Root!.Elements("Part"))
                {
                    int number = int.Parse(part.Element("PartNumber")!.Value, CultureInfo.InvariantCulture);
                    if (part.Element("ETag")!.Value != $"\"part-{number}-{open.Parts[number].Length}\"")
                    {
                        return (XmlError(HttpStatusCode.BadRequest, "InvalidPart"), false);
                    }

                    whole.AddRange(open.Parts[number]);
                }

                Bucket(open.Bucket)[open.Name] = new StoredObject([.. whole], NextGeneration(), Stamp(), null, false, open.Metadata);
                return (Xml(HttpStatusCode.OK, "<?xml version='1.0' encoding='UTF-8'?><CompleteMultipartUploadResult><Bucket>b</Bucket><Key>k</Key><ETag>\"e\"</ETag></CompleteMultipartUploadResult>"), true);
            }
        }

        if (method == HttpMethod.Delete && query.TryGetValue("uploadId", out string? aborted))
        {
            lock (_gate)
            {
                return (_uploads.Remove(aborted) ? new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) } : XmlError(HttpStatusCode.NotFound, "NoSuchUpload"), true);
            }
        }

        if (method == HttpMethod.Put)
        {
            if (Damaged(request, body) is { } damaged)
            {
                return (damaged, false);
            }

            lock (_gate)
            {
                SortedDictionary<string, StoredObject> objects = Bucket(xmlBucket);
                bool exists = objects.TryGetValue(objectName, out StoredObject current);
                if (Header(request, "x-goog-if-generation-match") is { } match && (match == "0" ? exists : !exists || match != current.Generation))
                {
                    return (XmlError(HttpStatusCode.PreconditionFailed, "PreconditionFailed"), false);
                }

                if (exists && (current.Held || current.RetainUntil > DateTimeOffset.UtcNow))
                {
                    return (XmlError(HttpStatusCode.Forbidden, "AccessDenied"), false);
                }

                StoredObject stored = new StoredObject(body, NextGeneration(), Stamp(), null, false, MetadataOf(request));
                objects[objectName] = stored;
                HttpResponseMessage created = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                created.Headers.TryAddWithoutValidation("x-goog-generation", stored.Generation);
                return (created, true);
            }
        }

        return (XmlError(HttpStatusCode.NotImplemented, "NotImplemented"), false);
    }

    private HttpResponseMessage Get(HttpRequestMessage request, string bucket, string name)
    {
        StoredObject? found = Find(bucket, name);
        if (found is not { } o)
        {
            return XmlError(HttpStatusCode.NotFound, "NoSuchKey");
        }

        if (Header(request, "x-goog-if-generation-match") is { } match && match != o.Generation)
        {
            return XmlError(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }

        long first = 0, last = o.Bytes.Length - 1;
        if (Header(request, "Range") is { } range)
        {
            string spec = range[6..];
            if (spec.StartsWith('-'))
            {
                first = Math.Max(0, o.Bytes.Length - long.Parse(spec[1..], CultureInfo.InvariantCulture));
            }
            else
            {
                string[] parts = spec.Split('-');
                first = long.Parse(parts[0], CultureInfo.InvariantCulture);
                last = Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), o.Bytes.Length - 1);
            }

            if (first >= o.Bytes.Length)
            {
                HttpResponseMessage refused = XmlError(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");
                refused.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{o.Bytes.Length}");
                return refused;
            }
        }

        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(o.Bytes, (int)first, (int)(last - first + 1)) };
        response.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {first}-{last}/{o.Bytes.Length}");
        response.Headers.TryAddWithoutValidation("x-goog-generation", o.Generation);
        return response;
    }

    private HttpResponseMessage Metadata(string bucket, string name)
    {
        StoredObject? found = Find(bucket, name);
        return found is { } o
            ? Json(HttpStatusCode.OK, Describe(name, o, withMetadata: true))
            : Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No such object","errors":[{"reason":"notFound"}]}}""");
    }

    private HttpResponseMessage List(string bucket, Dictionary<string, string> query)
    {
        string prefix = query.GetValueOrDefault("prefix", string.Empty);
        string? after = query.TryGetValue("pageToken", out string? token) ? Encoding.UTF8.GetString(Convert.FromBase64String(token)) : null;
        string? from = query.GetValueOrDefault("startOffset");
        int max = Math.Min(int.Parse(query.GetValueOrDefault("maxResults", "1000"), CultureInfo.InvariantCulture), PageSize);
        List<(string Name, StoredObject Object)> found;
        lock (_gate)
        {
            found = [.. Bucket(bucket)
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && (from is null || string.CompareOrdinal(pair.Key, from) >= 0)
                    && (after is null || string.CompareOrdinal(pair.Key, after) > 0))
                .Select(pair => (pair.Key, pair.Value))];
        }

        StringBuilder json = new StringBuilder("{\"kind\":\"storage#objects\",\"items\":[");
        json.AppendJoin(',', found.Take(max).Select(pair => Describe(pair.Name, pair.Object, withMetadata: false)));
        json.Append(']');
        if (found.Count > max)
        {
            json.Append(",\"nextPageToken\":\"").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(found[max - 1].Name))).Append('"');
        }

        return Json(HttpStatusCode.OK, json.Append('}').ToString());
    }

    private (HttpResponseMessage, bool) Batch(HttpRequestMessage request, byte[] body)
    {
        string type = request.Content!.Headers.ContentType!.ToString();
        string boundary = type[(type.IndexOf("boundary=", StringComparison.Ordinal) + 9)..];
        string[] parts = Encoding.UTF8.GetString(body).Split("--" + boundary, StringSplitOptions.None);
        StringBuilder answer = new StringBuilder();
        const string Back = "batch_emulator_response";
        int count = 0;
        foreach (string part in parts)
        {
            int line = part.IndexOf("DELETE ", StringComparison.Ordinal);
            if (line < 0)
            {
                continue;
            }

            if (++count > 100)
            {
                return (Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Too many requests in batch."}}"""), false);
            }

            string id = part[(part.IndexOf("Content-ID: <", StringComparison.Ordinal) + 13)..];
            id = id[..id.IndexOf('>', StringComparison.Ordinal)];
            string target = part[(line + 7)..part.IndexOf(" HTTP/1.1", line, StringComparison.Ordinal)];
            string[] segments = target["/storage/v1/b/".Length..].Split('/', 3);
            string bucket = Uri.UnescapeDataString(segments[0]);
            string name = Uri.UnescapeDataString(segments[2]);
            bool authorized = Token is null || part.Contains("Authorization: Bearer " + Token, StringComparison.Ordinal);
            int status;
            lock (_gate)
            {
                SortedDictionary<string, StoredObject> objects = Bucket(bucket);
                status = !authorized ? 401
                    : !objects.TryGetValue(name, out StoredObject o) ? 404
                    : o.Held || o.RetainUntil > DateTimeOffset.UtcNow ? 403
                    : objects.Remove(name) ? 204 : 500;
            }

            answer.Append("--").Append(Back).Append("\r\nContent-Type: application/http\r\n")
                .Append(CultureInfo.InvariantCulture, $"Content-ID: <response-{id}>\r\n\r\n")
                .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {(status == 204 ? "No Content" : "Refused")}\r\n\r\n");
        }

        answer.Append("--").Append(Back).Append("--\r\n");
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToString(), Encoding.UTF8) };
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={Back}");
        return (response, true);
    }

    private static string Describe(string name, StoredObject o, bool withMetadata)
    {
        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "storage#object");
            writer.WriteString("name", name);
            writer.WriteString("size", o.Bytes.Length.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("generation", o.Generation);
            writer.WriteString("timeCreated", o.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            if (o.RetainUntil is { } until)
            {
                writer.WriteString("retentionExpirationTime", until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }

            writer.WriteBoolean("temporaryHold", o.Held);
            if (withMetadata && o.Metadata.Count > 0)
            {
                writer.WriteStartObject("metadata");
                foreach ((string key, string value) in o.Metadata)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The refusal of a body whose <c>x-goog-hash</c> CRC-32C does not match it, or null.</summary>
    private static HttpResponseMessage? Damaged(HttpRequestMessage request, byte[] body)
    {
        if (Header(request, "x-goog-hash") is not { } hash || !hash.StartsWith("crc32c=", StringComparison.Ordinal))
        {
            return null;
        }

        byte[] expected = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(expected, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body));
        return hash[7..] == Convert.ToBase64String(expected) ? null : XmlError(HttpStatusCode.BadRequest, "BadDigest");
    }

    private static Dictionary<string, string> MetadataOf(HttpRequestMessage request) =>
        request.Headers
            .Where(h => h.Key.StartsWith("x-goog-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key["x-goog-meta-".Length..].ToLowerInvariant(), h => h.Value.First(), StringComparer.Ordinal);

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;

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

    private static string Strip(string query, string name)
    {
        string rest = string.Join("&", query.TrimStart('?').Split('&').Where(pair => !pair.StartsWith(name + "=", StringComparison.Ordinal)));
        return rest.Length == 0 ? string.Empty : "?" + rest;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new HttpResponseMessage(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    internal static HttpResponseMessage XmlError(HttpStatusCode status, string code) =>
        Xml(status, $"<?xml version='1.0' encoding='UTF-8'?><Error><Code>{code}</Code><Message>{code}</Message></Error>");

    private SortedDictionary<string, StoredObject> Bucket(string bucket)
    {
        if (!_buckets.TryGetValue(bucket, out SortedDictionary<string, StoredObject>? objects))
        {
            objects = new SortedDictionary<string, StoredObject>(StringComparer.Ordinal);
            _buckets.Add(bucket, objects);
        }

        return objects;
    }

    private StoredObject? Find(string bucket, string name)
    {
        lock (_gate)
        {
            return Bucket(bucket).TryGetValue(name, out StoredObject o) ? o : null;
        }
    }

    private static DateTimeOffset Stamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private string NextGeneration() => Interlocked.Increment(ref _generation).ToString(CultureInfo.InvariantCulture);

    private readonly record struct StoredObject(byte[] Bytes, string Generation, DateTimeOffset Created, DateTimeOffset? RetainUntil, bool Held, Dictionary<string, string> Metadata);
}
