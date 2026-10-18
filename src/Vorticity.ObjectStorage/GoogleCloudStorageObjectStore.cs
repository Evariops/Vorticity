using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Google;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage;

/// <summary>A bucket of Google Cloud Storage as an <see cref="IObjectStore"/>, through the service's own APIs.</summary>
/// <remarks>
/// <para>
/// Data goes through the XML API: a read asks for one range, bound once known to the object's
/// generation, which is its token, and a put is conditioned on the generation, 0 for an absent key, with
/// the CRC-32C the service checks. Metadata, listings and deletes go through the JSON API: a listing
/// carries each object's retention and holds, so vacuum asks no head, and deletes go in batches of 100.
/// </para>
/// <para>
/// The service takes no precondition on an upload in parts, so a conditional put goes whole; a put
/// without one, a dataset's data object, goes up in parts past the threshold.
/// </para>
/// <para>
/// The store owns its connections, which disposing it closes. Its credentials are the caller's, never
/// disposed, and other stores may share them.
/// </para>
/// </remarks>
public sealed partial class GoogleCloudStorageObjectStore : IObjectStore, IUploadService
{
    /// <summary>The metadata a put carries its id in, read back as <c>metadata.vxput</c>.</summary>
    internal const string PutIdHeader = "x-goog-meta-vxput";

    /// <summary>The most one XML <c>PUT</c> takes: 5 TiB, from bytes held in memory or spooled on disk.</summary>
    internal const long MaxSinglePut = 5L << 40;

    private const int MaxParts = 10_000;
    private const int MaxBatch = 100;

    private static readonly UriCreationOptions Verbatim = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };

    private readonly GoogleCloudStorageOptions _options;
    private readonly TokenSource _tokens;
    private readonly HttpTransport _transport;
    private readonly UploadLimits _limits;
    private readonly string _base;
    private readonly string _bucketPath;
    private readonly string _jsonObjects;
    private int _disposed;

    /// <summary>A store over <see cref="GoogleCloudStorageOptions.Bucket"/>.</summary>
    /// <param name="options">The bucket, the credentials and the connections.</param>
    /// <exception cref="ArgumentException">The endpoint is plain HTTP off the loopback, or the credentials cannot authorize Cloud Storage requests.</exception>
    public GoogleCloudStorageObjectStore(GoogleCloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Bucket);
        ArgumentNullException.ThrowIfNull(options.Credentials);
        Uri endpoint = options.Endpoint ?? new Uri("https://storage.googleapis.com");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new ArgumentException($"An endpoint is reached over HTTPS, or plain HTTP on the loopback for an emulator: '{endpoint}'.", nameof(options));
        }

        _tokens = options.Credentials.ForGoogle ?? throw new ArgumentException(
            $"A Cloud Storage store authorizes its requests with tokens, the environment's credentials or none, not {options.Credentials}.",
            nameof(options));
        _options = options;
        _limits = LimitsOf(options);
        _base = endpoint.GetLeftPart(UriPartial.Authority);
        _bucketPath = "/" + UriEncoding.Path(options.Bucket);
        _jsonObjects = "/storage/v1/b/" + UriEncoding.Query(options.Bucket) + "/o";
        _transport = new HttpTransport(options.Transport, "gcs", ReadErrorAsync, options.Handler);
    }

    /// <summary>
    /// A store over <paramref name="bucket"/> configured from the environment: Google's application
    /// default credentials, <see cref="StoreCredentials.FromEnvironment"/>, or, when
    /// <c>STORAGE_EMULATOR_HOST</c> names an emulator, that emulator without credentials, as Google's
    /// own clients do.
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="credentials">
    /// The credentials, the environment's when null: credentials shared by several stores renew their
    /// tokens once for all of them. An emulator takes none.
    /// </param>
    /// <returns>The store, which the caller disposes.</returns>
    public static GoogleCloudStorageObjectStore FromEnvironment(string bucket, StoreCredentials? credentials = null)
    {
        if (Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST") is { Length: > 0 } emulator)
        {
            return new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
            {
                Bucket = bucket,
                Endpoint = new Uri(emulator.Contains("://", StringComparison.Ordinal) ? emulator : "http://" + emulator),
                Credentials = StoreCredentials.Anonymous(),
            });
        }

        return new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
        {
            Bucket = bucket,
            Credentials = credentials ?? StoreCredentials.FromEnvironment(),
        });
    }

    /// <summary>The bucket.</summary>
    public string Bucket => _options.Bucket;

    /// <summary>
    /// Lists in order, replaces on a token's match, keeps locks, creates a key only while it is free,
    /// by a generation match of 0, which Google documents, and reads suffixes once its options say so.
    /// </summary>
    public ObjectStoreCapabilities Capabilities =>
        ObjectStoreCapabilities.ListsInOrder | ObjectStoreCapabilities.ReplacesOnTokenMatch | ObjectStoreCapabilities.KeepsLocks
        | ObjectStoreCapabilities.CreatesConditionally
        | (_options.ReadsSuffix == true ? ObjectStoreCapabilities.ReadsSuffix : 0);

    /// <summary>The transport, for tests.</summary>
    internal HttpTransport Transport => _transport;

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ThrowIfDisposed();
        if (destination.IsEmpty)
        {
            return StoreReads.EmptyAsync(this, key, offset, ifToken, cancellationToken);
        }

        return _transport.SendAsync(
            "GetObject",
            new ReadRequest(this, key, offset, destination, ifToken),
            static (s, ct) => s.Store.BuildReadAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeReadAsync(response, s, ct),
            idempotent: true,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        if (_options.ReadsSuffix == true && !destination.IsEmpty)
        {
            return await _transport.SendAsync(
                "GetObject",
                new ReadRequest(this, key, -1, destination, null),
                static (s, ct) => s.Store.BuildReadAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeReadAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
        }

        ObjectHead head = await HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
        int wanted = (int)Math.Min(destination.Length, head.Length);
        return wanted == 0
            ? new ObjectRead(0, head.Length, head.Token)
            : await ReadAsync(key, head.Length - wanted, destination[..wanted], head.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        return (await MetadataAsync(key, cancellationToken).ConfigureAwait(false)).Head;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ThrowIfDisposed();
        List<ObjectListing> page = [];
        string? token = null;
        do
        {
            page.Clear();
            token = await _transport.SendAsync(
                "ListObjects",
                new ListRequest(this, prefix, token is null ? startAfter : null, token, page, startAfter),
                static (s, ct) => s.Store.BuildListAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeListAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            foreach (ObjectListing listed in page)
            {
                yield return listed;
            }
        }
        while (token is not null);
    }

    /// <inheritdoc/>
    public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken) =>
        PutAsync(key, content, length, -1, condition, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Exception? failure = null;
        try
        {
            CheckKey(key);
            ArgumentOutOfRangeException.ThrowIfLessThan(length, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, -1);
            ThrowIfDisposed();
            return await Upload.PutAsync(this, _transport, _limits, key, content, length, maxLength, condition, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            failure = caught;
            throw;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        return Upload.PutAsync(this, _transport, _limits, key, content, condition, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys)
        {
            CheckKey(key);
        }

        ThrowIfDisposed();
        for (int start = 0; start < keys.Count; start += MaxBatch)
        {
            string[] batch = new string[Math.Min(MaxBatch, keys.Count - start)];
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = keys[start + i];
            }

            string failed = await _transport.SendAsync(
                "BatchDelete",
                new BatchRequest(this, batch, $"batch_{Guid.NewGuid():N}"),
                static (s, ct) => s.Store.BuildBatchAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeBatchAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            if (failed.Length > 0)
            {
                throw new ObjectStoreException($"A batch of deletes left objects in place: {failed}");
            }
        }
    }

    /// <summary>Closes the connections. The credentials are left to the caller, and to the other stores that share them.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>An object's head and the put id its metadata carries, from the JSON API, both null for an absent key.</summary>
    private ValueTask<(ObjectHead? Head, string? PutId)> MetadataAsync(string key, CancellationToken cancellationToken) =>
        _transport.SendAsync(
            "GetObjectMetadata",
            new KeyRequest(this, key),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Get, s.Store.JsonObject(s.Key) + "?fields=" + UriEncoding.Query(GoogleAnswers.ObjectFields), null, [], ct),
            static (response, s, ct) => s.Store.ConsumeMetadataAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken);

    private ValueTask<HttpRequestMessage> BuildReadAsync(ReadRequest s, CancellationToken cancellationToken)
    {
        string range = s.Offset < 0
            ? string.Create(CultureInfo.InvariantCulture, $"bytes=-{s.Destination.Length}")
            : string.Create(CultureInfo.InvariantCulture, $"bytes={s.Offset}-{s.Offset + s.Destination.Length - 1}");
        (string, string)[] headers = s.IfToken is { } token
            ? [("Range", range), ("x-goog-if-generation-match", token)]
            : [("Range", range)];
        return BuildAsync(HttpMethod.Get, XmlObject(s.Key), null, headers, cancellationToken);
    }

    private async ValueTask<ObjectRead> ConsumeReadAsync(HttpResponseMessage response, ReadRequest s, CancellationToken cancellationToken)
    {
        HttpStatusCode status = response.StatusCode;
        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            long? size = HttpAnswers.ContentRange(response)?.Total;
            await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            if (s.Offset <= 0 && size is null or 0)
            {
                return await StoreReads.EmptyAsync(this, s.Key, 0, s.IfToken, cancellationToken).ConfigureAwait(false);
            }

            throw StoreFailures.PastEnd(s.Key, s.Offset, size ?? -1);
        }

        if (status is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            throw await FailureAsync(response, "GetObject", s.Key, s.IfToken, cancellationToken).ConfigureAwait(false);
        }

        long announced = response.Content.Headers.ContentLength
            ?? throw new ObjectStoreException($"GetObject on '{s.Key}' was answered without a Content-Length.");
        (long first, long total) = status == HttpStatusCode.PartialContent
            ? HttpAnswers.ContentRange(response) ?? throw new ObjectStoreException($"GetObject on '{s.Key}' was answered with a partial body and no Content-Range.")
            : (0, announced);
        if (s.Offset >= 0)
        {
            StoreFailures.RefusePastEnd(s.Key, s.Offset, total);
        }

        if ((s.Offset >= 0 && first != s.Offset) || (status == HttpStatusCode.OK && s.Offset != 0 && !(s.Offset < 0 && announced <= s.Destination.Length)))
        {
            throw new ObjectStoreException($"GetObject on '{s.Key}' was answered with bytes from {first} where {s.Offset} was asked.");
        }

        int wanted = (int)Math.Min(announced, s.Destination.Length);
        int read = await _transport.ReadBodyAsync(response, s.Destination[..wanted], cancellationToken).ConfigureAwait(false);
        if (read < wanted)
        {
            throw new StoreRetryException($"the body of GetObject on '{s.Key}' ended after {read} of {wanted} bytes", RetryBudget.RetryCost);
        }

        string generation = HttpAnswers.Header(response, "x-goog-generation")
            ?? throw new ObjectStoreException($"GetObject on '{s.Key}' was answered without its generation.");
        return new ObjectRead(read, total, s.IfToken is not null && generation == s.IfToken ? s.IfToken : generation);
    }

    private async ValueTask<(ObjectHead? Head, string? PutId)> ConsumeMetadataAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "GetObjectMetadata", key, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 1 << 20, cancellationToken).ConfigureAwait(false);
        try
        {
            (ObjectHead head, string? putId) = GoogleAnswers.ParseObject(bytes.AsSpan(0, length));
            return (head, putId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private ValueTask<HttpRequestMessage> BuildListAsync(ListRequest s, CancellationToken cancellationToken)
    {
        string query = string.Concat(
            "?maxResults=1000&fields=", UriEncoding.Query(GoogleAnswers.ListFields),
            s.Prefix.Length == 0 ? string.Empty : "&prefix=" + UriEncoding.Query(s.Prefix),
            s.StartOffset is null ? string.Empty : "&startOffset=" + UriEncoding.Query(s.StartOffset),
            s.PageToken is null ? string.Empty : "&pageToken=" + UriEncoding.Query(s.PageToken));
        return BuildAsync(HttpMethod.Get, _jsonObjects + query, null, [], cancellationToken);
    }

    private async ValueTask<string?> ConsumeListAsync(HttpResponseMessage response, ListRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "ListObjects", s.Prefix, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return GoogleAnswers.ParseListing(bytes.AsSpan(0, length), s.Skip, s.Page);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>The size of part <paramref name="number"/> of an object of <paramref name="length"/> bytes, -1 when unknown.</summary>
    internal int PartSizeFor(long length, int number = 1) => _limits.PartSizeOf(number, length);

    /// <summary>The header that carries a body's checksum, as the options choose it, or null for none.</summary>
    private (string Name, string Value)? ChecksumOf(ReadOnlySequence<byte> body) => _options.Checksum switch
    {
        PutChecksum.None => null,
        PutChecksum.ContentMd5 => ("Content-MD5", Convert.ToBase64String(Checksums.Md5(body))),
        _ => ("x-goog-hash", "crc32c=" + Crc32CHeader(body)),
    };

    /// <inheritdoc/>
    string IUploadService.PutOperation => "PutObject";

    /// <inheritdoc/>
    /// <remarks>An XML <c>PUT</c> with its generation precondition and the CRC-32C the service checks.</remarks>
    ValueTask<PutOutcome> IUploadService.PutWholeAsync(string key, ReadOnlySequence<byte> body, PutCondition condition, string? putId, CancellationToken cancellationToken)
    {
        List<(string, string)> headers = [];
        if (ChecksumOf(body) is { } checksum)
        {
            headers.Add(checksum);
        }

        if (putId is not null)
        {
            headers.Add((PutIdHeader, putId));
        }

        if (condition.Kind == PutConditionKind.IfAbsent)
        {
            headers.Add(("x-goog-if-generation-match", "0"));
        }
        else if (condition.Kind == PutConditionKind.IfMatch)
        {
            headers.Add(("x-goog-if-generation-match", condition.Token!));
        }

        return _transport.SendAsync(
            "PutObject",
            new WriteRequest(this, key, body, [.. headers], condition),
            static (s, ct) => s.Store.BuildPutAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumePutAsync(response, s, ct),
            idempotent: putId is null,
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>An XML multipart upload, whose object carries the put id: the service takes no condition on it.</remarks>
    async ValueTask<UploadState> IUploadService.StartAsync(string key, string putId, CancellationToken cancellationToken)
    {
        string uploadId = await _transport.SendAsync(
            "CreateMultipartUpload",
            new PartRequest(this, key, string.Empty, 0, ReadOnlySequence<byte>.Empty, [(PutIdHeader, putId)]),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Post, s.Store.XmlObject(s.Key) + "?uploads", new ByteArrayContent([]), s.Headers, ct),
            static (response, s, ct) => s.Store.ConsumeCreateUploadAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        return new UploadState(key, uploadId, putId);
    }

    /// <inheritdoc/>
    async ValueTask<PartReceipt> IUploadService.SendPartAsync(UploadState upload, int number, ReadOnlySequence<byte> body, CancellationToken cancellationToken)
    {
        string etag = await _transport.SendAsync(
            "UploadPart",
            new PartRequest(this, upload.Key, upload.Id, number, body, ChecksumOf(body) is { } checksum ? [checksum] : []),
            static (s, ct) => s.Store.BuildAsync(
                HttpMethod.Put,
                s.Store.XmlObject(s.Key) + string.Create(CultureInfo.InvariantCulture, $"?partNumber={s.Number}&uploadId={UriEncoding.Query(s.UploadId)}"),
                new SequenceContent(s.Body),
                s.Headers,
                ct),
            static async (response, s, ct) => response.StatusCode == HttpStatusCode.OK
                ? HttpAnswers.Header(response, "ETag") ?? throw new ObjectStoreException($"UploadPart of '{s.Key}' was answered without an ETag.")
                : throw await s.Store.FailureAsync(response, "UploadPart", s.Key, null, ct).ConfigureAwait(false),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        return new PartReceipt(etag);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Never sent again blindly: a completion whose answer was lost and that landed finds its upload gone,
    /// so the object's metadata and the put id it carries settle it.
    /// </remarks>
    ValueTask<PutOutcome> IUploadService.CompleteAsync(UploadState upload, PutCondition condition, CancellationToken cancellationToken)
    {
        byte[] body = ServiceXml.Write(upload.Parts, static (xml, parts) =>
        {
            xml.WriteStartElement("CompleteMultipartUpload");
            for (int i = 0; i < parts.Count; i++)
            {
                xml.WriteStartElement("Part");
                xml.WriteElementString("PartNumber", (i + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteElementString("ETag", parts[i].Tag);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        });
        return _transport.SendAsync(
            "CompleteMultipartUpload",
            new UploadRequest(this, upload.Key, upload.Id, body),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Post, s.Store.XmlObject(s.Key) + "?uploadId=" + UriEncoding.Query(s.UploadId), new SequenceContent(new ReadOnlySequence<byte>(s.Body)), [], ct),
            static (response, s, ct) => s.Store.ConsumeCompleteAsync(response, s.Key, ct),
            idempotent: false,
            cancellationToken);
    }

    /// <inheritdoc/>
    ValueTask IUploadService.AbortAsync(UploadState upload) => AbortAsync(upload.Key, upload.Id);

    /// <inheritdoc/>
    ValueTask<(ObjectHead? Head, string? PutId)> IUploadService.HeadAsync(string key, CancellationToken cancellationToken) => MetadataAsync(key, cancellationToken);

    /// <summary>Aborts an upload given up, under the transport's abort deadline rather than the caller's token.</summary>
    private ValueTask AbortAsync(string key, string uploadId) =>
        _transport.GiveUpAsync(
            "AbortMultipartUpload",
            new UploadRequest(this, key, uploadId, []),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Delete, s.Store.XmlObject(s.Key) + "?uploadId=" + UriEncoding.Query(s.UploadId), null, [], ct));

    /// <summary>
    /// What the service takes of a put: up to 10 000 parts of 5 GiB, an object of 5 TiB in one request,
    /// and a condition only on a whole object.
    /// </summary>
    private static UploadLimits LimitsOf(GoogleCloudStorageOptions options) => new UploadLimits
    {
        PartThreshold = options.PartThreshold,
        PartSize = Math.Max(options.PartSize, 1),
        MaxParts = MaxParts,
        MaxPartBytes = 5L << 30,
        PartsInFlight = options.PartsInFlight,
        MaxSinglePut = MaxSinglePut,
        ConditionalParts = false,
        GrowingParts = true,
        MaxHeldBytes = options.Transport.MaxHeldBytes,
        SpoolDirectory = options.Transport.SpoolDirectory,
    };

    private async ValueTask<HttpRequestMessage> BuildPutAsync(WriteRequest s, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = await BuildAsync(HttpMethod.Put, XmlObject(s.Key), new SequenceContent(s.Body), s.Headers, cancellationToken).ConfigureAwait(false);
        request.Headers.ExpectContinue = s.Body.Length >= 1 << 20;
        return request;
    }

    private async ValueTask<PutOutcome> ConsumePutAsync(HttpResponseMessage response, WriteRequest s, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed:
                return s.Condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : PutOutcome.Changed;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (error.Code is "BadDigest" or "InvalidDigest")
        {
            throw new StoreRetryException($"PutObject of '{s.Key}' arrived damaged: {error}", RetryBudget.RetryCost, notApplied: true);
        }

        throw MapFailure(response, error, "PutObject", s.Key);
    }

    private async ValueTask<string> ConsumeCreateUploadAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "CreateMultipartUpload", key, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxAnswerBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return ServiceXml.ParseElement(bytes.AsMemory(0, length), "UploadId")
                ?? throw new ObjectStoreException($"CreateMultipartUpload of '{key}' was answered without an upload id.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private async ValueTask<PutOutcome> ConsumeCompleteAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "CompleteMultipartUpload", key, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxAnswerBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            if (ServiceXml.IsError(bytes.AsMemory(0, length), out string? code))
            {
                throw new StoreRetryException($"CompleteMultipartUpload of '{key}' failed after its answer began: {code}", RetryBudget.RetryCost);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }

        return PutOutcome.Created;
    }

    /// <summary>A JSON batch of up to 100 deletes, each a sub-request of the multipart body.</summary>
    private async ValueTask<HttpRequestMessage> BuildBatchAsync(BatchRequest s, CancellationToken cancellationToken)
    {
        string? token = await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        StringBuilder body = new StringBuilder();
        for (int i = 0; i < s.Keys.Length; i++)
        {
            body.Append("--").Append(s.Boundary).Append("\r\n")
                .Append("Content-Type: application/http\r\n")
                .Append(CultureInfo.InvariantCulture, $"Content-ID: <{i}>\r\n\r\n")
                .Append(CultureInfo.InvariantCulture, $"DELETE {JsonObject(s.Keys[i])} HTTP/1.1\r\n");
            if (token is not null)
            {
                body.Append("Authorization: Bearer ").Append(token).Append("\r\n");
            }

            body.Append("\r\n");
        }

        body.Append("--").Append(s.Boundary).Append("--\r\n");
        return await BuildAsync(
            HttpMethod.Post,
            "/batch/storage/v1",
            new SequenceContent(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(body.ToString()))),
            [("Content-Type", $"multipart/mixed; boundary={s.Boundary}")],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> ConsumeBatchAsync(HttpResponseMessage response, BatchRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "BatchDelete", Bucket, null, cancellationToken).ConfigureAwait(false);
        }

        string? type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out HeaderStringValues values) ? values.ToString() : null;
        int at = type?.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (type is null || at < 0)
        {
            throw new ObjectStoreException("A batch was answered without the boundary of its parts.");
        }

        string boundary = type[(at + 9)..].Trim('"', ' ');
        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return BatchFailures(Encoding.UTF8.GetString(bytes, 0, length), boundary, s.Keys);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>The deletes of a batch that left an object in place: an absent object is no failure.</summary>
    internal static string BatchFailures(string body, string boundary, IReadOnlyList<string> keys)
    {
        List<string> failures = [];
        int count = 0;
        int index = 0;
        foreach (string part in body.Split("--" + boundary, StringSplitOptions.None))
        {
            int http = part.IndexOf("HTTP/1.1 ", StringComparison.Ordinal);
            if (http < 0)
            {
                continue;
            }

            if (part.Length < http + 12 || !int.TryParse(part.AsSpan(http + 9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            {
                throw new ObjectStoreException("The service answered a batch with a part that holds no status.");
            }

            int which = index++;
            int id = part.IndexOf("Content-ID: <response-", StringComparison.OrdinalIgnoreCase);
            if (id >= 0)
            {
                int end = part.IndexOf('>', id);
                if (end > id && int.TryParse(part.AsSpan(id + 22, end - id - 22), NumberStyles.None, CultureInfo.InvariantCulture, out int named))
                {
                    which = named;
                }
            }

            if (status is 204 or 200 or 404)
            {
                continue;
            }

            count++;
            if (failures.Count < 3)
            {
                failures.Add($"'{(which < keys.Count ? keys[which] : $"#{which}")}' (HTTP {status})");
            }
        }

        return count == 0 ? string.Empty : $"{count} object(s), among them {string.Join(", ", failures)}";
    }

    /// <summary>A request to <paramref name="target"/>, a path and its query, authorized by the current token.</summary>
    private async ValueTask<HttpRequestMessage> BuildAsync(
        HttpMethod method, string target, HttpContent? content, (string Name, string Value)[] headers, CancellationToken cancellationToken)
    {
        string? token = await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        HttpRequestMessage request = new HttpRequestMessage(method, new Uri(_base + target, in Verbatim)) { Content = content };
        foreach ((string name, string value) in headers)
        {
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                content!.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return request;
    }

    private string XmlObject(string key) => _bucketPath + "/" + UriEncoding.Path(key);

    private string JsonObject(string key) => _jsonObjects + "/" + UriEncoding.Query(key);

    private async ValueTask<Exception> FailureAsync(HttpResponseMessage response, string operation, string key, string? ifToken, CancellationToken cancellationToken)
    {
        if (StoreFailures.OfStatus(response.StatusCode, key, ifToken, "another generation") is { } common)
        {
            return common;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return MapFailure(response, error, operation, key);
    }

    private Exception MapFailure(HttpResponseMessage response, ServiceError error, string operation, string key)
    {
        // The token of the refused request, read back from it: a request sent since with a renewed one keeps it.
        if (response.StatusCode == HttpStatusCode.Unauthorized && HttpAnswers.BearerOf(response) is { } refused)
        {
            _tokens.Expired(refused);
            throw new StoreRetryException($"{operation} was refused for an expired token: {error}", cost: 0);
        }

        return error.ToException($"{operation} on '{key}' was refused, as {_tokens}: {error}");
    }

    /// <summary>The service's error, from the XML API's body or the JSON API's.</summary>
    private async ValueTask<ServiceError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? requestId = HttpAnswers.Header(response, "x-guploader-uploadid");
        TimeSpan? retryAfter = HttpAnswers.RetryAfter(response);
        if (response.RequestMessage?.Method == HttpMethod.Head || response.Content.Headers.ContentLength == 0)
        {
            return new ServiceError(status, null, null, requestId, retryAfter);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxAnswerBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            ReadOnlyMemory<byte> body = bytes.AsMemory(0, length);
            if (body.Span.TrimStart(" \t\r\n"u8).StartsWith("{"u8))
            {
                (string? reason, string? message) = GoogleAnswers.ParseError(body.Span);
                return new ServiceError(status, reason, message, requestId, retryAfter);
            }

            (string? code, string? text, _, _) = ServiceXml.ParseError(body);
            return new ServiceError(status, code, text, requestId, retryAfter);
        }
        catch (ObjectStoreException)
        {
            return new ServiceError(status, null, null, requestId, retryAfter);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary><c>x-goog-hash</c>'s CRC-32C: its four bytes, high byte first, in base64.</summary>
    private static string Crc32CHeader(ReadOnlySequence<byte> body)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Checksums.Crc32C(body));
        return Convert.ToBase64String(bytes);
    }

    private static void CheckKey(string key) => StoreFailures.CheckKey(key, "Cloud Storage");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct KeyRequest(GoogleCloudStorageObjectStore Store, string Key);

    /// <summary>A read: from <see cref="Offset"/>, or the suffix the destination holds when it is negative.</summary>
    private readonly record struct ReadRequest(GoogleCloudStorageObjectStore Store, string Key, long Offset, Memory<byte> Destination, string? IfToken);

    private readonly record struct ListRequest(GoogleCloudStorageObjectStore Store, string Prefix, string? StartOffset, string? PageToken, List<ObjectListing> Page, string? Skip);

    private readonly record struct WriteRequest(GoogleCloudStorageObjectStore Store, string Key, ReadOnlySequence<byte> Body, (string, string)[] Headers, PutCondition Condition);

    private readonly record struct PartRequest(GoogleCloudStorageObjectStore Store, string Key, string UploadId, int Number, ReadOnlySequence<byte> Body, (string, string)[] Headers);

    private readonly record struct UploadRequest(GoogleCloudStorageObjectStore Store, string Key, string UploadId, byte[] Body);

    private readonly record struct BatchRequest(GoogleCloudStorageObjectStore Store, string[] Keys, string Boundary);
}
