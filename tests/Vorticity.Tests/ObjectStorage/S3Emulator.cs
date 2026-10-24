// An S3 service in memory, under the store's HttpClient: what a test sends is a request as the wire
// would carry it, signed, and what it gets back is an answer as S3 writes one.
//
// IT CHECKS EVERY SIGNATURE against its own canonical form of the request it received, its path and
// query re-encoded from the URI and its headers read from the message, and every signed payload and
// checksum against the body, so a store that signs or sums one thing and sends another fails here as it
// would against the service. The signer itself is held to AWS's published vectors elsewhere.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.Tests.ObjectStorage;

internal sealed class S3Emulator : HttpMessageHandler
{
    internal const string AccessKey = "AKIDEMULATOR";
    internal const string Secret = "emulator/secret+key";

    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly object _gate = new object();
    private readonly Dictionary<string, SortedDictionary<string, StoredObject>> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private long _etags;

    /// <summary>The region the buckets are in, which a head of a bucket says.</summary>
    internal string Region { get; init; } = "eu-west-3";

    /// <summary>The most keys a listing page holds, whatever the request asks.</summary>
    internal int PageSize { get; set; } = 1000;

    /// <summary>The service's clock, which a signature's date must be within five minutes of.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Runs before each request is served: returns an answer to send instead, or throws to fail the connection.</summary>
    internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Awaited before each request is served, once it is logged: a slow path, or a request parked until a test lets it go.</summary>
    internal Func<HttpRequestMessage, int, CancellationToken, Task>? Hold { get; set; }

    /// <summary>Runs after a write is applied: true loses its answer, the connection failing as the object lands.</summary>
    internal Func<HttpRequestMessage, bool>? LoseAnswer { get; set; }

    /// <summary>
    /// Runs before a write is applied: true loses its answer before it lands, as a slow service does, and
    /// the write lands right after the next request is answered, whatever that request found.
    /// </summary>
    internal Func<HttpRequestMessage, bool>? LandsLate { get; set; }

    // The write whose answer was lost before it landed, applied after the next request.
    private (HttpRequestMessage Request, byte[] Body)? _late;

    /// <summary>Whether a put must carry a checksum, as a bucket under Object Lock asks.</summary>
    internal bool RequireChecksum { get; set; }

    /// <summary>Whether anyone may read the buckets: an unsigned read or head is served, an unsigned write refused.</summary>
    internal bool PublicRead { get; set; }

    /// <summary>The least a part but the last holds, which a completion checks: 0, none, unless a test sets it.</summary>
    internal int MinimumPartSize { get; set; }

    /// <summary>The highest part number an upload takes: 10 000 on S3, 1 000 on Scaleway.</summary>
    internal int MaxParts { get; set; } = 10_000;

    /// <summary>Whether every part but the last must be of one size, the last no larger, as R2 asks of a completion.</summary>
    internal bool EqualParts { get; set; }

    /// <summary>The largest object a completion makes: 5 TiB on S3.</summary>
    internal long MaxObjectBytes { get; set; } = 5L << 40;

    /// <summary>The largest part, and the largest object one put makes: 5 GiB on S3.</summary>
    internal long MaxPartBytes { get; set; } = 5L << 30;

    /// <summary>
    /// The reads a second, gets and heads, the bucket serves, past which it answers 503 SlowDown, as S3 does per
    /// prefix and OVHcloud per bucket; 0 for no limit. A second's worth may come at once.
    /// </summary>
    internal double ReadsPerSecond
    {
        get => _reads.PerSecond;
        set => _reads.PerSecond = value;
    }

    /// <summary>The writes a second, puts, posts and deletes, the bucket serves, as <see cref="ReadsPerSecond"/>.</summary>
    internal double WritesPerSecond
    {
        get => _writes.PerSecond;
        set => _writes.PerSecond = value;
    }

    /// <summary>How long each request waits before it is served: a round trip to the service.</summary>
    internal TimeSpan Latency { get; set; }

    /// <summary>The requests refused for going past <see cref="ReadsPerSecond"/> or <see cref="WritesPerSecond"/>.</summary>
    internal int SlowedDown => _slowedDown;

    private readonly Rate _reads = new Rate();
    private readonly Rate _writes = new Rate();
    private int _slowedDown;

    /// <summary>
    /// An emulator under a service's rules as its documentation states them: the parts it takes, of one size on R2,
    /// a thousand on Scaleway; its largest object; its requests a second, 5 500 reads and 3 500 writes a prefix on
    /// S3, 900 reads and 300 writes a bucket on OVHcloud. Parts of 64 KiB at least, a test's scale, the store's own
    /// minimum then set alike.
    /// </summary>
    internal static S3Emulator For(S3Profile profile) => new S3Emulator
    {
        Region = profile == S3Profile.R2 ? "auto" : "eu-west-3",
        MinimumPartSize = 64 << 10,
        MaxParts = profile == S3Profile.Scaleway ? 1_000 : 10_000,
        EqualParts = profile == S3Profile.R2,
        ReadsPerSecond = profile switch
        {
            S3Profile.Amazon => 5_500,
            S3Profile.Ovhcloud => 900,
            _ => 0,
        },
        WritesPerSecond = profile switch
        {
            S3Profile.Amazon => 3_500,
            S3Profile.Ovhcloud => 300,
            _ => 0,
        },
    };

    /// <summary>
    /// Why the service refuses an object of parts of <paramref name="sizes"/> bytes, in order, or null when it takes
    /// it: more parts than it takes, a part past its largest, one but the last under its least, parts of unequal sizes
    /// where it wants them of one, or an object past its largest. A completion asks it of the parts it holds, and a
    /// test of the parts a store would send, which then need no byte.
    /// </summary>
    internal (string Code, string Message)? Refusal(IReadOnlyList<long> sizes)
    {
        if (sizes.Count > MaxParts)
        {
            return ("InvalidArgument", $"Part number must be an integer between 1 and {MaxParts}, inclusive.");
        }

        long total = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            bool last = i == sizes.Count - 1;
            if (sizes[i] > MaxPartBytes)
            {
                return ("EntityTooLarge", $"Part {i + 1} is {sizes[i]} bytes, past the maximum allowed size.");
            }

            if (!last && sizes[i] < MinimumPartSize)
            {
                return ("EntityTooSmall", $"Part {i + 1} is {sizes[i]} bytes, below the minimum allowed size.");
            }

            if (EqualParts && i > 0 && (last ? sizes[i] > sizes[0] : sizes[i] != sizes[0]))
            {
                return ("InvalidPart", "All non-trailing parts must have the same length.");
            }

            total += sizes[i];
        }

        return total > MaxObjectBytes
            ? ("EntityTooLarge", "Your proposed upload exceeds the maximum allowed size.")
            : null;
    }

    /// <summary>Whether <paramref name="request"/> may be served now under its rate, a token taken if so.</summary>
    private bool Admitted(HttpRequestMessage request)
    {
        Rate rate = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head ? _reads : _writes;
        if (rate.PerSecond <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (rate.TryTake(System.Diagnostics.Stopwatch.GetTimestamp()))
            {
                return true;
            }

            _slowedDown++;
            return false;
        }
    }

    /// <summary>Every request received, in order: its method, path and query, and the range it asked.</summary>
    internal ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

    /// <summary>The host each request was sent to, in order.</summary>
    internal ConcurrentQueue<string> Hosts { get; } = new ConcurrentQueue<string>();

    /// <summary>The requests received.</summary>
    internal int Requests => Log.Count;

    /// <summary>The uploads started and neither completed nor aborted.</summary>
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

    internal void Seed(string bucket, string key, byte[] bytes, DateTimeOffset? retainUntil = null, bool legalHold = false)
    {
        lock (_gate)
        {
            Bucket(bucket)[key] = new StoredObject(bytes, NextETag(), Stamp(), retainUntil, legalHold, []);
        }
    }

    internal string ETagOf(string bucket, string key)
    {
        lock (_gate)
        {
            return Bucket(bucket)[key].ETag;
        }
    }

    internal byte[]? BytesOf(string bucket, string key)
    {
        lock (_gate)
        {
            return Bucket(bucket).TryGetValue(key, out StoredObject o) ? o.Bytes : null;
        }
    }

    internal IReadOnlyDictionary<string, string> MetadataOf(string bucket, string key)
    {
        lock (_gate)
        {
            return Bucket(bucket)[key].Metadata;
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
        Hosts.Enqueue(request.Headers.Host ?? uri.Authority);
        Log.Enqueue($"{request.Method} {uri.AbsolutePath}{uri.Query}{(request.Headers.TryGetValues("Range", out IEnumerable<string>? range) ? " " + range.First() : string.Empty)}");
        if (Hold is { } hold)
        {
            await hold(request, index, cancellationToken);
        }

        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken);
        }

        if (!Admitted(request))
        {
            HttpResponseMessage slow = Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.");
            slow.RequestMessage = request;
            return slow;
        }

        if (Intercept?.Invoke(request, index) is { } intercepted)
        {
            intercepted.RequestMessage = request;
            return intercepted;
        }

        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && LandsLate?.Invoke(request) == true)
        {
            _late = (request, body);
            throw new HttpRequestException("The connection was reset before the request landed; it lands later.");
        }

        (HttpResponseMessage response, bool applied) = Serve(request, body);
        if (_late is { } late)
        {
            _late = null;
            Serve(late.Request, late.Body).Response.Dispose();
        }

        if (applied && LoseAnswer?.Invoke(request) == true)
        {
            response.Dispose();
            throw new HttpRequestException("The connection was reset after the request was served.");
        }

        response.RequestMessage = request;
        response.Headers.TryAddWithoutValidation("x-amz-request-id", $"REQ{index:D6}");
        response.Headers.Date = Now();
        return response;
    }

    private (HttpResponseMessage Response, bool Applied) Serve(HttpRequestMessage request, byte[] body)
    {
        (string bucket, string key) = Address(request.RequestUri!, request.Headers.Host ?? request.RequestUri!.Authority);
        if (request.Method == HttpMethod.Head && key.Length == 0)
        {
            // A bucket's head, unsigned: S3 says the region whatever else it answers.
            HttpResponseMessage head = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([]) };
            head.Headers.TryAddWithoutValidation("x-amz-bucket-region", Region);
            return (head, false);
        }

        Dictionary<string, string> query = Query(request.RequestUri!);
        HttpMethod method = request.Method;
        if (Verify(request, body) is { } refused)
        {
            return (refused, false);
        }

        if (key.Length == 0)
        {
            if (method == HttpMethod.Get && query.GetValueOrDefault("list-type") == "2")
            {
                return (List(bucket, query), false);
            }

            if (method == HttpMethod.Post && query.ContainsKey("delete"))
            {
                return Delete(request, bucket, body);
            }
        }
        else if (method == HttpMethod.Get)
        {
            return (Get(request, bucket, key), false);
        }
        else if (method == HttpMethod.Head)
        {
            return (Head(bucket, key), false);
        }
        else if (method == HttpMethod.Put && query.TryGetValue("uploadId", out string? partOf))
        {
            return UploadPart(request, partOf, int.Parse(query["partNumber"], CultureInfo.InvariantCulture), body);
        }
        else if (method == HttpMethod.Put)
        {
            return Put(request, bucket, key, body);
        }
        else if (method == HttpMethod.Post && query.ContainsKey("uploads"))
        {
            return CreateUpload(request, bucket, key);
        }
        else if (method == HttpMethod.Post && query.TryGetValue("uploadId", out string? completed))
        {
            return Complete(request, bucket, key, completed, body);
        }
        else if (method == HttpMethod.Delete && query.TryGetValue("uploadId", out string? aborted))
        {
            lock (_gate)
            {
                return _uploads.Remove(aborted)
                    ? (new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) }, true)
                    : (Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist."), false);
            }
        }

        return (Error(HttpStatusCode.NotImplemented, "NotImplemented", "The emulator does not serve this request."), false);
    }

    private (HttpResponseMessage, bool) Put(HttpRequestMessage request, string bucket, string key, byte[] body)
    {
        if (Checksums(request, body) is { } damaged)
        {
            return (damaged, false);
        }

        if (body.Length > MaxPartBytes)
        {
            return (Error(HttpStatusCode.BadRequest, "EntityTooLarge", "Your proposed upload exceeds the maximum allowed size."), false);
        }

        lock (_gate)
        {
            SortedDictionary<string, StoredObject> objects = Bucket(bucket);
            if (Condition(request, objects, key) is { } failed)
            {
                return (failed, false);
            }

            StoredObject stored = new StoredObject(body, NextETag(), Stamp(), null, false, Metadata(request));
            objects[key] = stored;
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("ETag", stored.ETag);
            return (response, true);
        }
    }

    private (HttpResponseMessage, bool) CreateUpload(HttpRequestMessage request, string bucket, string key)
    {
        string id = $"upload-{Interlocked.Increment(ref _etags)}";
        string? algorithm = Header(request, "x-amz-checksum-algorithm");
        lock (_gate)
        {
            _uploads[id] = new Upload(bucket, key, Metadata(request), algorithm, new SortedDictionary<int, (byte[], string)>());
        }

        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{bucket}</Bucket><Key>{Escape(key)}</Key><UploadId>{id}</UploadId></InitiateMultipartUploadResult>";
        return (new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }, true);
    }

    private (HttpResponseMessage, bool) UploadPart(HttpRequestMessage request, string uploadId, int number, byte[] body)
    {
        if (Checksums(request, body) is { } damaged)
        {
            return (damaged, false);
        }

        if (number < 1 || number > MaxParts)
        {
            return (Error(HttpStatusCode.BadRequest, "InvalidArgument", $"Part number must be an integer between 1 and {MaxParts}, inclusive."), false);
        }

        if (body.Length > MaxPartBytes)
        {
            return (Error(HttpStatusCode.BadRequest, "EntityTooLarge", "Your proposed upload exceeds the maximum allowed size."), false);
        }

        lock (_gate)
        {
            if (!_uploads.TryGetValue(uploadId, out Upload? upload))
            {
                return (Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist."), false);
            }

            string etag = NextETag();
            upload.Parts[number] = (body, etag);
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("ETag", etag);
            return (response, true);
        }
    }

    private (HttpResponseMessage, bool) Complete(HttpRequestMessage request, string bucket, string key, string uploadId, byte[] body)
    {
        XElement root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root!;
        List<(int Number, string ETag)> asked = [.. root.Elements(S3 + "Part").Select(p => (int.Parse(p.Element(S3 + "PartNumber")!.Value, CultureInfo.InvariantCulture), p.Element(S3 + "ETag")!.Value))];
        lock (_gate)
        {
            if (!_uploads.TryGetValue(uploadId, out Upload? upload))
            {
                return (Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist."), false);
            }

            if (asked.Count == 0 || asked.Select(p => p.Number).Zip(asked.Skip(1).Select(p => p.Number)).Any(pair => pair.First >= pair.Second))
            {
                return (Error(HttpStatusCode.BadRequest, "InvalidPartOrder", "The parts are not in ascending order."), false);
            }

            List<byte> whole = [];
            long[] sizes = new long[asked.Count];
            for (int i = 0; i < asked.Count; i++)
            {
                (int number, string etag) = asked[i];
                if (!upload.Parts.TryGetValue(number, out (byte[] Bytes, string ETag) part) || part.ETag != etag)
                {
                    return (Error(HttpStatusCode.BadRequest, "InvalidPart", $"Part {number} is not the one uploaded."), false);
                }

                sizes[i] = part.Bytes.Length;
            }

            if (Refusal(sizes) is { } refusal)
            {
                return (Error(HttpStatusCode.BadRequest, refusal.Code, refusal.Message), false);
            }

            foreach ((int number, _) in asked)
            {
                whole.AddRange(upload.Parts[number].Bytes);
            }

            byte[] bytes = [.. whole];
            if (Header(request, "x-amz-checksum-crc64nvme") is { } crc && crc != Crc(bytes))
            {
                return (Error(HttpStatusCode.BadRequest, "BadDigest", "The whole object's CRC64NVME does not match."), false);
            }

            SortedDictionary<string, StoredObject> objects = Bucket(bucket);
            if (Condition(request, objects, key) is { } failed)
            {
                return (failed, false);
            }

            _uploads.Remove(uploadId);
            StoredObject stored = new StoredObject(bytes, NextETag(), Stamp(), null, false, upload.Metadata);
            objects[key] = stored;
            string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\n<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{bucket}</Bucket><Key>{Escape(key)}</Key><ETag>{Escape(stored.ETag)}</ETag></CompleteMultipartUploadResult>";
            return (new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }, true);
        }
    }

    private (HttpResponseMessage, bool) Delete(HttpRequestMessage request, string bucket, byte[] body)
    {
        string? md5 = request.Content?.Headers.ContentMD5 is { } digest ? Convert.ToBase64String(digest) : null;
        string? crc32 = Header(request, "x-amz-checksum-crc32");
        if (md5 is null && crc32 is null)
        {
            return (Error(HttpStatusCode.BadRequest, "InvalidRequest", "Missing required header for this request: Content-MD5"), false);
        }

        if (md5 is not null && md5 != Convert.ToBase64String(MD5.HashData(body)))
        {
            return (Error(HttpStatusCode.BadRequest, "BadDigest", "The Content-MD5 you specified did not match what was received."), false);
        }

        XElement root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root!;
        List<string> keys = [.. root.Elements(S3 + "Object").Select(o => o.Element(S3 + "Key")!.Value)];
        if (keys.Count > 1000)
        {
            return (Error(HttpStatusCode.BadRequest, "MalformedXML", "More than 1 000 keys."), false);
        }

        StringBuilder xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        lock (_gate)
        {
            SortedDictionary<string, StoredObject> objects = Bucket(bucket);
            foreach (string key in keys)
            {
                if (objects.TryGetValue(key, out StoredObject o) && (o.LegalHold || o.RetainUntil > Now()))
                {
                    xml.Append(CultureInfo.InvariantCulture, $"<Error><Key>{Escape(key)}</Key><Code>AccessDenied</Code><Message>Access Denied because object protected by object lock.</Message></Error>");
                    continue;
                }

                objects.Remove(key);
            }
        }

        xml.Append("</DeleteResult>");
        return (new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") }, true);
    }

    /// <summary>The refusal of a write whose condition fails on the object under <paramref name="key"/>, or null.</summary>
    private static HttpResponseMessage? Condition(HttpRequestMessage request, SortedDictionary<string, StoredObject> objects, string key)
    {
        bool exists = objects.TryGetValue(key, out StoredObject current);
        if (Header(request, "If-None-Match") == "*" && exists)
        {
            return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        }

        if (Header(request, "If-Match") is { } match)
        {
            if (!exists)
            {
                return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
            }

            if (match != current.ETag)
            {
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
            }
        }

        if (exists && (current.LegalHold || current.RetainUntil > DateTimeOffset.UtcNow))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", "The object is protected by Object Lock.");
        }

        return null;
    }

    /// <summary>The refusal of a body whose checksum or payload hash does not match it, or null.</summary>
    private HttpResponseMessage? Checksums(HttpRequestMessage request, byte[] body)
    {
        string? crc = Header(request, "x-amz-checksum-crc64nvme");
        if (crc is not null && crc != Crc(body))
        {
            return Error(HttpStatusCode.BadRequest, "BadDigest", "The CRC64NVME you specified did not match the calculated checksum.");
        }

        byte[]? md5 = request.Content?.Headers.ContentMD5;
        if (md5 is not null && !md5.AsSpan().SequenceEqual(MD5.HashData(body)))
        {
            return Error(HttpStatusCode.BadRequest, "BadDigest", "The Content-MD5 you specified did not match what was received.");
        }

        if (RequireChecksum && crc is null && md5 is null)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidRequest", "Content-MD5 or x-amz-checksum- HTTP header is required for Put Object requests with Object Lock parameters");
        }

        return null;
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
        foreach ((string name, string value) in found.Value.Metadata)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

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

    /// <summary>Checks the signature and the signed payload against the request as received; null when they hold.</summary>
    private HttpResponseMessage? Verify(HttpRequestMessage request, byte[] body)
    {
        if (!request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values))
        {
            return PublicRead && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
                ? null
                : Error(HttpStatusCode.Forbidden, "AccessDenied", "Anonymous access is refused.");
        }

        string authorization = values.First();
        string amzDate = request.Headers.GetValues("x-amz-date").First();
        string payload = request.Headers.GetValues("x-amz-content-sha256").First();
        DateTimeOffset signedAt = DateTimeOffset.ParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if ((signedAt - Now()).Duration() > TimeSpan.FromMinutes(5))
        {
            return Error(HttpStatusCode.Forbidden, "RequestTimeTooSkewed", "The difference between the request time and the current time is too large.");
        }

        if (payload != SigV4Signer.UnsignedPayload && payload != Convert.ToHexStringLower(SHA256.HashData(body)))
        {
            return Error(HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch", "The provided 'x-amz-content-sha256' header does not match what was computed.");
        }

        string credential = Field(authorization, "Credential=");
        string[] scope = credential.Split('/');
        const string service = "s3";
        string secret = Secret;
        if (scope[0] != AccessKey)
        {
            return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", $"Credential scope {credential} is not this service's.");
        }

        if (scope[2] != Region || scope[3] != service)
        {
            return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", $"Credential scope {credential} is not this service's.");
        }

        string[] signed = Field(authorization, "SignedHeaders=").Split(';');
        if (!signed.Contains("host") || !signed.Contains("x-amz-date") || !signed.Contains("x-amz-content-sha256"))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", "The host, the date and the payload hash must be signed.");
        }

        IEnumerable<string> amz = request.Headers.Select(h => h.Key.ToLowerInvariant()).Where(name => name.StartsWith("x-amz-", StringComparison.Ordinal));
        if (amz.FirstOrDefault(name => !signed.Contains(name)) is { } unsigned)
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied", $"The header {unsigned} is sent and not signed.");
        }

        (string, string)[] headers = [.. signed.Select(name => (name, name == "host" ? request.Headers.Host ?? request.RequestUri!.Authority : Header(request, name) ?? string.Empty))];
        string canonicalQuery = string.Join("&", Query(request.RequestUri!)
            .Select(pair => (Name: UriEncoding.Query(pair.Key), Value: UriEncoding.Query(pair.Value)))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));
        AwsKeys keys = new AwsKeys(scope[0], secret, null);
        string expected = new SigV4Signer(Region, service).Authorization(keys, amzDate, request.Method.Method, request.RequestUri!.AbsolutePath, canonicalQuery, headers, payload);
        return expected == authorization
            ? null
            : Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", $"Expected {expected}, got {authorization}.");
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) || request.Content?.Headers.TryGetValues(name, out values) == true
            ? string.Join(",", values!)
            : null;

    private static Dictionary<string, string> Metadata(HttpRequestMessage request) =>
        request.Headers
            .Where(h => h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.First(), StringComparer.Ordinal);

    private static string Crc(byte[] bytes)
    {
        byte[] crc = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(crc, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, bytes));
        return Convert.ToBase64String(crc);
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
        response.Headers.TryAddWithoutValidation("ETag", o.ETag);
        response.Content.Headers.LastModified = o.Modified;
        return response;
    }

    private static HttpResponseMessage Unsatisfiable(long length) => Error(
        HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "The requested range is not satisfiable",
        $"<RangeRequested>bytes</RangeRequested><ActualObjectSize>{length}</ActualObjectSize>");

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

    /// <summary>The service's date for an object, to the second, as S3 keeps it.</summary>
    private DateTimeOffset Stamp()
    {
        DateTimeOffset now = Now();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    private string NextETag() => $"\"{Convert.ToHexStringLower(MD5.HashData(BitConverter.GetBytes(Interlocked.Increment(ref _etags))))}\"";

    private readonly record struct StoredObject(byte[] Bytes, string ETag, DateTimeOffset Modified, DateTimeOffset? RetainUntil, bool LegalHold, Dictionary<string, string> Metadata);

    private sealed record Upload(string Bucket, string Key, Dictionary<string, string> Metadata, string? Algorithm, SortedDictionary<int, (byte[] Bytes, string ETag)> Parts);

    /// <summary>A rate as a token bucket: a second's worth of tokens at most, refilled as time goes.</summary>
    private sealed class Rate
    {
        private double _tokens = double.NaN;
        private long _filledAt;

        /// <summary>The requests a second served, 0 for no limit.</summary>
        internal double PerSecond { get; set; }

        /// <summary>Takes a token at <paramref name="now"/>, a timestamp, or says none is left.</summary>
        internal bool TryTake(long now)
        {
            _tokens = double.IsNaN(_tokens)
                ? PerSecond
                : Math.Min(PerSecond, _tokens + (System.Diagnostics.Stopwatch.GetElapsedTime(_filledAt, now).TotalSeconds * PerSecond));
            _filledAt = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens--;
            return true;
        }
    }
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
