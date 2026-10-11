// An S3 service in memory, under the store's HttpClient: what a test sends is a request as the wire
// would carry it, signed, and what it gets back is an answer as S3 writes one.
//
// IT CHECKS EVERY SIGNATURE against its own canonical form of the request it received, its path and
// query re-encoded from the URI and its headers read from the message, so a store that signs one thing
// and sends another fails here as it would against the service. The signer itself is held to AWS's
// published vectors elsewhere.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.Tests.ObjectStorage;

internal sealed class S3Emulator : HttpMessageHandler
{
    internal const string AccessKey = "AKIDEMULATOR";
    internal const string Secret = "emulator/secret+key";

    private readonly object _gate = new object();
    private readonly Dictionary<string, SortedDictionary<string, StoredObject>> _buckets = new(StringComparer.Ordinal);
    private long _etags;

    /// <summary>The region the buckets are in, which a head of a bucket says.</summary>
    internal string Region { get; init; } = "eu-west-3";

    /// <summary>The most keys a listing page holds, whatever the request asks.</summary>
    internal int PageSize { get; set; } = 1000;

    /// <summary>The service's clock, which a signature's date must be within five minutes of.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Runs before each request is served: returns an answer to send instead, or throws to fail the connection.</summary>
    internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Every request received, in order: its method, path and query, and the range it asked.</summary>
    internal ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

    /// <summary>The requests received.</summary>
    internal int Requests => Log.Count;

    internal void Seed(string bucket, string key, byte[] bytes, DateTimeOffset? retainUntil = null, bool legalHold = false)
    {
        lock (_gate)
        {
            Bucket(bucket)[key] = new StoredObject(bytes, NextETag(), Now(), retainUntil, legalHold);
        }
    }

    internal string ETagOf(string bucket, string key)
    {
        lock (_gate)
        {
            return Bucket(bucket)[key].ETag;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int index = Log.Count;
        Uri uri = request.RequestUri!;
        Log.Enqueue($"{request.Method} {uri.AbsolutePath}{uri.Query}{(request.Headers.TryGetValues("Range", out IEnumerable<string>? range) ? " " + range.First() : string.Empty)}");
        if (Intercept?.Invoke(request, index) is { } intercepted)
        {
            intercepted.RequestMessage = request;
            return Task.FromResult(intercepted);
        }

        HttpResponseMessage response = Serve(request);
        response.RequestMessage = request;
        response.Headers.TryAddWithoutValidation("x-amz-request-id", $"REQ{index:D6}");
        response.Headers.Date = Now();
        return Task.FromResult(response);
    }

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        (string bucket, string key) = Address(request.RequestUri!, request.Headers.Host ?? request.RequestUri!.Authority);
        if (request.Method == HttpMethod.Head && key.Length == 0)
        {
            // A bucket's head, unsigned: S3 says the region whatever else it answers.
            HttpResponseMessage head = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([]) };
            head.Headers.TryAddWithoutValidation("x-amz-bucket-region", Region);
            return head;
        }

        if (Verify(request) is { } refused)
        {
            return refused;
        }

        Dictionary<string, string> query = Query(request.RequestUri!);
        if (request.Method == HttpMethod.Get && key.Length == 0 && query.TryGetValue("list-type", out string? listType) && listType == "2")
        {
            return List(bucket, query);
        }

        if (request.Method == HttpMethod.Get)
        {
            return Get(request, bucket, key);
        }

        if (request.Method == HttpMethod.Head)
        {
            return Head(bucket, key);
        }

        return Error(HttpStatusCode.NotImplemented, "NotImplemented", "The emulator does not serve this request.");
    }

    private HttpResponseMessage Get(HttpRequestMessage request, string bucket, string key)
    {
        StoredObject? found = Find(bucket, key);
        if (found is null)
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
        }

        StoredObject o = found.Value;
        if (request.Headers.TryGetValues("If-Match", out IEnumerable<string>? match) && match.First() != o.ETag)
        {
            return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        }

        long length = o.Bytes.Length;
        if (!request.Headers.TryGetValues("Range", out IEnumerable<string>? ranges))
        {
            return Body(HttpStatusCode.OK, o, 0, length);
        }

        string range = ranges.First();
        if (!range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidArgument", "Bad range.");
        }

        string spec = range[6..];
        long first, last;
        if (spec.StartsWith('-'))
        {
            long suffix = long.Parse(spec[1..], CultureInfo.InvariantCulture);
            if (length == 0 || suffix == 0)
            {
                return Unsatisfiable(length);
            }

            first = Math.Max(0, length - suffix);
            last = length - 1;
        }
        else
        {
            string[] parts = spec.Split('-');
            first = long.Parse(parts[0], CultureInfo.InvariantCulture);
            last = parts[1].Length == 0 ? length - 1 : Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), length - 1);
            if (first >= length)
            {
                return Unsatisfiable(length);
            }
        }

        HttpResponseMessage partial = Body(HttpStatusCode.PartialContent, o, first, last - first + 1);
        partial.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {first}-{last}/{length}");
        return partial;
    }

    private HttpResponseMessage Head(string bucket, string key)
    {
        StoredObject? found = Find(bucket, key);
        if (found is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }

        HttpResponseMessage response = Body(HttpStatusCode.OK, found.Value, 0, 0);
        response.Content.Headers.ContentLength = found.Value.Bytes.Length;
        if (found.Value.RetainUntil is { } until)
        {
            response.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", "COMPLIANCE");
            response.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }

        response.Headers.TryAddWithoutValidation("x-amz-object-lock-legal-hold", found.Value.LegalHold ? "ON" : "OFF");
        return response;
    }

    private HttpResponseMessage List(string bucket, Dictionary<string, string> query)
    {
        string prefix = query.GetValueOrDefault("prefix", string.Empty);
        string? after = query.TryGetValue("continuation-token", out string? token)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(token))
            : query.GetValueOrDefault("start-after");
        int max = Math.Min(int.Parse(query.GetValueOrDefault("max-keys", "1000"), CultureInfo.InvariantCulture), PageSize);
        bool encode = query.GetValueOrDefault("encoding-type") == "url";
        List<(string Key, StoredObject Object)> keys;
        lock (_gate)
        {
            keys = [.. Bucket(bucket)
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && (after is null || string.CompareOrdinal(pair.Key, after) > 0))
                .Select(pair => (pair.Key, pair.Value))];
        }

        bool truncated = keys.Count > max;
        StringBuilder xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        xml.Append(CultureInfo.InvariantCulture, $"<Name>{bucket}</Name><Prefix>{Escape(encode ? FormEncode(prefix) : prefix)}</Prefix><KeyCount>{Math.Min(max, keys.Count)}</KeyCount><MaxKeys>{max}</MaxKeys>");
        if (encode)
        {
            xml.Append("<EncodingType>url</EncodingType>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        foreach ((string key, StoredObject o) in keys.Take(max))
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Contents><Key>{Escape(encode ? FormEncode(key) : key)}</Key><LastModified>{o.Modified.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}</LastModified><ETag>{Escape(o.ETag)}</ETag><Size>{o.Bytes.Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
        }

        if (truncated)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<NextContinuationToken>{Convert.ToBase64String(Encoding.UTF8.GetBytes(keys[max - 1].Key))}</NextContinuationToken>");
        }

        xml.Append("</ListBucketResult>");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    /// <summary>Checks the signature against the request as received; null when it holds.</summary>
    private HttpResponseMessage? Verify(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", "Anonymous access is refused.");
        }

        string authorization = values.First();
        string amzDate = request.Headers.GetValues("x-amz-date").First();
        string payload = request.Headers.GetValues("x-amz-content-sha256").First();
        DateTimeOffset signedAt = DateTimeOffset.ParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if ((signedAt - Now()).Duration() > TimeSpan.FromMinutes(5))
        {
            return Error(HttpStatusCode.Forbidden, "RequestTimeTooSkewed", "The difference between the request time and the current time is too large.");
        }

        string credential = Field(authorization, "Credential=");
        string[] scope = credential.Split('/');
        if (scope[0] != AccessKey || scope[2] != Region || scope[3] != "s3")
        {
            return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", $"Credential scope {credential} is not this service's.");
        }

        string[] signed = Field(authorization, "SignedHeaders=").Split(';');
        if (!signed.Contains("host") || !signed.Contains("x-amz-date") || !signed.Contains("x-amz-content-sha256"))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", "The host, the date and the payload hash must be signed.");
        }

        (string, string)[] headers = [.. signed.Select(name => (name, name == "host"
            ? request.Headers.Host ?? request.RequestUri!.Authority
            : string.Join(",", request.Headers.TryGetValues(name, out IEnumerable<string>? v) ? v : request.Content?.Headers.TryGetValues(name, out v) == true ? v : [])))];
        string canonicalQuery = string.Join("&", Query(request.RequestUri!)
            .Select(pair => (Name: UriEncoding.Query(pair.Key), Value: UriEncoding.Query(pair.Value)))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));
        using AwsKeys keys = new AwsKeys(AccessKey, Secret, null);
        string expected = new SigV4Signer(Region).Authorization(keys, amzDate, request.Method.Method, request.RequestUri!.AbsolutePath, canonicalQuery, headers, payload);
        return expected == authorization
            ? null
            : Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", $"Expected {expected}, got {authorization}.");
    }

    private static string Field(string authorization, string name)
    {
        int at = authorization.IndexOf(name, StringComparison.Ordinal) + name.Length;
        int end = authorization.IndexOf(',', at);
        return end < 0 ? authorization[at..] : authorization[at..end];
    }

    /// <summary>The bucket and the key a request addresses, by host or by path.</summary>
    private static (string Bucket, string Key) Address(Uri uri, string host)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (host.Contains(".s3.", StringComparison.Ordinal) && !host.StartsWith("s3.", StringComparison.Ordinal))
        {
            return (host[..host.IndexOf(".s3.", StringComparison.Ordinal)], path.TrimStart('/'));
        }

        string rest = path.TrimStart('/');
        int slash = rest.IndexOf('/');
        return slash < 0 ? (rest, string.Empty) : (rest[..slash], rest[(slash + 1)..]);
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            query[Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals])] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return query;
    }

    private static HttpResponseMessage Body(HttpStatusCode status, StoredObject o, long first, long count)
    {
        HttpResponseMessage response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(o.Bytes, (int)first, (int)count),
        };
        response.Headers.ETag = null;
        response.Headers.TryAddWithoutValidation("ETag", o.ETag);
        response.Content.Headers.LastModified = o.Modified;
        return response;
    }

    private static HttpResponseMessage Unsatisfiable(long length)
    {
        HttpResponseMessage response = Error(
            HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "The requested range is not satisfiable",
            $"<RangeRequested>bytes</RangeRequested><ActualObjectSize>{length}</ActualObjectSize>");
        return response;
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code, string message, string extra = "") =>
        new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>{code}</Code><Message>{Escape(message)}</Message>{extra}<RequestId>EMU</RequestId><HostId>emulator</HostId></Error>",
                Encoding.UTF8,
                "application/xml"),
        };

    /// <summary>A key as S3 encodes it in a listing asked for URL encoding: a space as <c>+</c>, <c>/</c> kept, the rest of what is not unreserved as <c>%XY</c>.</summary>
    private static string FormEncode(string key)
    {
        StringBuilder encoded = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(key))
        {
            if (b == (byte)' ')
            {
                encoded.Append('+');
            }
            else if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/')
            {
                encoded.Append((char)b);
            }
            else
            {
                encoded.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return encoded.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private SortedDictionary<string, StoredObject> Bucket(string bucket)
    {
        if (!_buckets.TryGetValue(bucket, out SortedDictionary<string, StoredObject>? objects))
        {
            objects = new SortedDictionary<string, StoredObject>(StringComparer.Ordinal);
            _buckets.Add(bucket, objects);
        }

        return objects;
    }

    private StoredObject? Find(string bucket, string key)
    {
        lock (_gate)
        {
            return Bucket(bucket).TryGetValue(key, out StoredObject o) ? o : null;
        }
    }

    private string NextETag() => $"\"{Convert.ToHexStringLower(MD5.HashData(BitConverter.GetBytes(Interlocked.Increment(ref _etags))))}\"";

    private readonly record struct StoredObject(byte[] Bytes, string ETag, DateTimeOffset Modified, DateTimeOffset? RetainUntil, bool LegalHold);
}

/// <summary>A body that sends some bytes, then stops: short, or stalled until cancelled.</summary>
internal sealed class FaultyContent(byte[] bytes, int sent, bool stall) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        await stream.WriteAsync(bytes.AsMemory(0, sent));

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Body(bytes, sent, stall));

    protected override bool TryComputeLength(out long length)
    {
        length = bytes.Length;
        return true;
    }

    private sealed class Body(byte[] bytes, int sent, bool stall) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at >= sent)
            {
                if (stall)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return 0;
            }

            int count = Math.Min(buffer.Length, sent - _at);
            bytes.AsSpan(_at, count).CopyTo(buffer.Span);
            _at += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
