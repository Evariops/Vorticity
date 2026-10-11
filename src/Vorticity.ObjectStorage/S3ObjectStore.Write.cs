using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.S3;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage;

/// <summary>The write path: puts, whole or in parts, their conditions and checksums, and deletes.</summary>
/// <remarks>
/// <para>
/// A put goes through <see cref="Upload"/>, as every store's does: up to the part threshold in one
/// request, its checksum computed, and past it in parts sent in parallel. Bytes already in memory are
/// sent where they lie, and a stream is held a part at a time, or whole on disk when the store has a
/// spool directory. A put conditioned on the key's absence carries <c>If-None-Match: *</c>, one on a
/// token <c>If-Match</c>, on its one request or on the completion of its upload, and a put id in the
/// object's metadata, by which a head settles a put whose answer was lost.
/// </para>
/// <para>
/// A delete goes in batches of a thousand keys, each with the <c>Content-MD5</c> the service asks for,
/// or a CRC-32 on a host that forbids MD5.
/// </para>
/// </remarks>
public sealed partial class S3ObjectStore
{
    /// <summary>The most one request puts: 5 GiB on every profile.</summary>
    internal const long MaxSinglePut = 5L << 30;

    /// <summary>The metadata a conditional put carries its id in.</summary>
    internal const string PutIdHeader = "x-amz-meta-vx-put";

    private const int MaxDeleteKeys = 1000;

    /// <summary>
    /// The body from which a put waits for the service's <c>100 Continue</c> before it sends: a request
    /// refused on its headers, denied or badly signed, then costs no body.
    /// </summary>
    private const int ExpectContinueBytes = 1 << 20;

    // 1 once a bucket has refused a put for want of a checksum.
    private int _md5Required;

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Exception? failure = null;
        try
        {
            CheckKey(key);
            ArgumentOutOfRangeException.ThrowIfLessThan(length, -1);
            ThrowIfDisposed();
            return await Upload.PutAsync(this, _transport, _limits, key, content, length, condition, cancellationToken).ConfigureAwait(false);
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
        for (int start = 0; start < keys.Count; start += MaxDeleteKeys)
        {
            byte[] body = DeleteBody(keys, start, Math.Min(MaxDeleteKeys, keys.Count - start));
            string failed = await _transport.SendAsync(
                "DeleteObjects",
                new BodyRequest(this, string.Empty, body),
                static (s, ct) => s.Store.BuildDeleteAsync(s.Body, ct),
                static (response, s, ct) => s.Store.ConsumeDeleteAsync(response, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            if (failed.Length > 0)
            {
                throw new ObjectStoreException($"DeleteObjects left keys in place: {failed}");
            }
        }
    }

    /// <summary>The size of part <paramref name="number"/> of an object of <paramref name="length"/> bytes, -1 when unknown.</summary>
    internal int PartSizeFor(long length, int number = 1) => _limits.PartSizeOf(number, length);

    /// <inheritdoc/>
    string IUploadService.PutOperation => "PutObject";

    /// <inheritdoc/>
    ValueTask<PutOutcome> IUploadService.PutWholeAsync(string key, ReadOnlySequence<byte> body, PutCondition condition, string? putId, CancellationToken cancellationToken) =>
        _transport.SendAsync(
            "PutObject",
            new PutRequest(this, key, body, PutHeaders(body, putId), condition),
            static (s, ct) => s.Store.BuildPutAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumePutAsync(response, s.Key, s.Condition, ct),
            idempotent: putId is null,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The upload carries the put id, and asks for the whole object's CRC-64/NVME when its parts carry theirs.</remarks>
    async ValueTask<UploadState> IUploadService.StartAsync(string key, string putId, CancellationToken cancellationToken)
    {
        bool crc = ChecksumOf() == S3Checksum.Crc64Nvme;
        string uploadId = await _transport.SendAsync(
            "CreateMultipartUpload",
            new UploadRequest(this, key, string.Empty, putId, crc),
            static (s, ct) => s.Store.BuildCreateUploadAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeCreateUploadAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        return new S3Upload(key, uploadId, putId, crc);
    }

    /// <inheritdoc/>
    async ValueTask<PartReceipt> IUploadService.SendPartAsync(UploadState upload, int number, ReadOnlySequence<byte> body, CancellationToken cancellationToken)
    {
        bool crc = ((S3Upload)upload).Crc;
        ulong checksum = crc ? Checksums.Crc64Nvme(body) : 0;
        List<(string, string)> extra = [];
        if (crc)
        {
            extra.Add(("x-amz-checksum-crc64nvme", Base64(checksum)));
        }
        else if (ChecksumOf() == S3Checksum.ContentMd5)
        {
            extra.Add(("content-md5", Convert.ToBase64String(Checksums.Md5(body))));
        }

        string etag = await _transport.SendAsync(
            "UploadPart",
            new PartRequest(this, upload.Key, upload.Id, number, body, [.. extra]),
            static (s, ct) => s.Store.BuildPartAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumePartAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        return new PartReceipt(etag, checksum);
    }

    /// <inheritdoc/>
    /// <remarks>The completion carries the condition and, when the parts carry theirs, the whole object's CRC-64/NVME.</remarks>
    ValueTask<PutOutcome> IUploadService.CompleteAsync(UploadState upload, PutCondition condition, CancellationToken cancellationToken)
    {
        S3Upload s3 = (S3Upload)upload;
        byte[] body = ServiceXml.Write((Parts: upload.Parts, s3.Crc), static (xml, s) =>
        {
            xml.WriteStartElement("CompleteMultipartUpload", S3Answers.Namespace);
            for (int i = 0; i < s.Parts.Count; i++)
            {
                xml.WriteStartElement("Part", S3Answers.Namespace);
                xml.WriteElementString("PartNumber", S3Answers.Namespace, (i + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteElementString("ETag", S3Answers.Namespace, s.Parts[i].Tag);
                if (s.Crc)
                {
                    xml.WriteElementString("ChecksumCRC64NVME", S3Answers.Namespace, Base64(s.Parts[i].Crc));
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        });
        (string, string)[] extra = s3.Whole is { } sum
            ? [("x-amz-checksum-crc64nvme", Base64(sum)), ("x-amz-checksum-type", "FULL_OBJECT")]
            : [];
        return _transport.SendAsync(
            "CompleteMultipartUpload",
            new CompleteRequest(this, upload.Key, upload.Id, body, extra, condition),
            static (s, ct) => s.Store.BuildCompleteAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeCompleteAsync(response, s, ct),
            idempotent: false,
            cancellationToken);
    }

    /// <inheritdoc/>
    ValueTask IUploadService.AbortAsync(UploadState upload) => AbortAsync(upload.Key, upload.Id);

    /// <inheritdoc/>
    ValueTask<(ObjectHead? Head, string? PutId)> IUploadService.HeadAsync(string key, CancellationToken cancellationToken) =>
        _transport.SendAsync(
            "HeadObject",
            new Request(this, key),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, null, ct),
            static async (response, s, ct) => (await s.Store.ConsumeHeadAsync(response, s.Key, ct).ConfigureAwait(false), HttpAnswers.Header(response, PutIdHeader)),
            idempotent: true,
            cancellationToken);

    /// <summary>Aborts an upload given up, under the transport's abort deadline rather than the caller's token.</summary>
    private ValueTask AbortAsync(string key, string uploadId) =>
        _transport.GiveUpAsync(
            "AbortMultipartUpload",
            new UploadRequest(this, key, uploadId, string.Empty, false),
            static (s, ct) => s.Store.BuildAbortAsync(s, ct));

    /// <summary>
    /// What the service takes of a put: parts of the option's size, at least the service's minimum, up
    /// to 1 000 on Scaleway and 10 000 elsewhere; on R2, a condition only on a whole put, and parts of
    /// one size.
    /// </summary>
    private static UploadLimits LimitsOf(S3Profile profile, S3StoreOptions options) => new UploadLimits
    {
        PartThreshold = options.PartThreshold,
        PartSize = Math.Max(options.PartSize, options.MinimumPartSize),
        MaxParts = profile == S3Profile.Scaleway ? 1_000 : 10_000,
        MaxPartBytes = MaxSinglePut,
        PartsInFlight = options.PartsInFlight,
        MaxSinglePut = MaxSinglePut,
        ConditionalParts = profile != S3Profile.R2,
        GrowingParts = profile != S3Profile.R2,
        MaxHeldBytes = options.Transport.MaxHeldBytes,
        SpoolDirectory = options.Transport.SpoolDirectory,
    };

    private async ValueTask<HttpRequestMessage> BuildPutAsync(PutRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, new Uri(target.Base + path, in Verbatim))
        {
            Content = new SequenceContent(s.Body),
        };
        request.Headers.ExpectContinue = s.Body.Length >= ExpectContinueBytes;
        AddCondition(request, s.Condition);
        await SignAsync(request, target, path, string.Empty, SigV4Signer.UnsignedPayload, cancellationToken, WithDigest(s.Extra, s.Body)).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<PutOutcome> ConsumePutAsync(HttpResponseMessage response, string key, PutCondition condition, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed:
                return condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : PutOutcome.Changed;
            case HttpStatusCode.NotFound when condition.Kind == PutConditionKind.IfMatch:
                return PutOutcome.Changed;
            case HttpStatusCode.Conflict:
                // A conditional put that lost a race with a delete: refused, and safe to send again.
                throw new StoreRetryException($"PutObject of '{key}' met a concurrent delete", RetryBudget.RetryCost, notApplied: true);
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (error.Code is "BadDigest" or "InvalidDigest" or "XAmzContentChecksumMismatch" or "IncompleteBody")
        {
            throw new StoreRetryException($"PutObject of '{key}' arrived damaged: {error}", RetryBudget.RetryCost, notApplied: true);
        }

        if (LearnsChecksum(error))
        {
            throw new StoreRetryException($"PutObject of '{key}' was refused for want of a checksum: {error}", cost: 0);
        }

        throw MapFailure(response, error, "PutObject", key);
    }

    private async ValueTask<HttpRequestMessage> BuildCreateUploadAsync(UploadRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.Base + path + "?uploads", in Verbatim));
        (string, string)[] extra = s.Crc
            ? [("x-amz-checksum-algorithm", "CRC64NVME"), ("x-amz-checksum-type", "FULL_OBJECT"), (PutIdHeader, s.PutId)]
            : [(PutIdHeader, s.PutId)];
        await SignAsync(request, target, path, "uploads=", SigV4Signer.EmptyPayloadHash, cancellationToken, extra).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<string> ConsumeCreateUploadAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "CreateMultipartUpload", key, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 64 << 10, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<HttpRequestMessage> BuildPartAsync(PartRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        string query = string.Create(CultureInfo.InvariantCulture, $"partNumber={s.Number}&uploadId={UriEncoding.Query(s.UploadId)}");
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, new Uri(target.Base + path + "?" + query, in Verbatim))
        {
            Content = new SequenceContent(s.Body),
        };
        request.Headers.ExpectContinue = s.Body.Length >= ExpectContinueBytes;
        await SignAsync(request, target, path, query, SigV4Signer.UnsignedPayload, cancellationToken, WithDigest(s.Extra, s.Body)).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<string> ConsumePartAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return HttpAnswers.Header(response, "ETag") ?? throw new ObjectStoreException($"UploadPart of '{key}' was answered without an ETag.");
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (error.Code is "BadDigest" or "InvalidDigest" or "XAmzContentChecksumMismatch" or "IncompleteBody")
        {
            throw new StoreRetryException($"UploadPart of '{key}' arrived damaged: {error}", RetryBudget.RetryCost, notApplied: true);
        }

        if (LearnsChecksum(error))
        {
            throw new StoreRetryException($"UploadPart of '{key}' was refused for want of a checksum: {error}", cost: 0);
        }

        throw MapFailure(response, error, "UploadPart", key);
    }

    private async ValueTask<HttpRequestMessage> BuildCompleteAsync(CompleteRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        string query = "uploadId=" + UriEncoding.Query(s.UploadId);
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.Base + path + "?" + query, in Verbatim))
        {
            Content = new SequenceContent(new ReadOnlySequence<byte>(s.Body)),
        };
        AddCondition(request, s.Condition);
        await SignAsync(request, target, path, query, Convert.ToHexStringLower(SHA256.HashData(s.Body)), cancellationToken, s.Extra).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<PutOutcome> ConsumeCompleteAsync(HttpResponseMessage response, CompleteRequest s, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                // A completion may fail after its 200 is sent, its error then in the body.
                (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 64 << 10, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (ServiceXml.IsError(bytes.AsMemory(0, length), out string? code))
                    {
                        throw new StoreRetryException($"CompleteMultipartUpload of '{s.Key}' failed after its answer began: {code}", RetryBudget.RetryCost, notApplied: true);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }

                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed:
                return s.Condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : PutOutcome.Changed;
            case HttpStatusCode.NotFound when s.Condition.Kind == PutConditionKind.IfMatch && HttpAnswers.Header(response, "x-amz-error-code") != "NoSuchUpload":
                break;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (error.Code == "NoSuchUpload")
        {
            // A completion sent again after its answer was lost finds its upload gone: the head says why.
            throw new StoreAnswerLostException($"CompleteMultipartUpload of '{s.Key}' found its upload gone: {error}", new ObjectStoreException(error.ToString()));
        }

        if (response.StatusCode == HttpStatusCode.NotFound && s.Condition.Kind == PutConditionKind.IfMatch)
        {
            return PutOutcome.Changed;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new StoreRestartException($"CompleteMultipartUpload of '{s.Key}' met a concurrent operation, and the service asks for a new upload: {error}.");
        }

        throw MapFailure(response, error, "CompleteMultipartUpload", s.Key);
    }

    private async ValueTask<HttpRequestMessage> BuildAbortAsync(UploadRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        string query = "uploadId=" + UriEncoding.Query(s.UploadId);
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, new Uri(target.Base + path + "?" + query, in Verbatim));
        await SignAsync(request, target, path, query, SigV4Signer.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<HttpRequestMessage> BuildDeleteAsync(byte[] body, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix.Length == 0 ? "/" : target.PathPrefix;
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.Base + path + "?delete", in Verbatim))
        {
            Content = new SequenceContent(new ReadOnlySequence<byte>(body)),
        };
        (string, string) digest = DeleteDigest(body);
        await SignAsync(request, target, path, "delete=", Convert.ToHexStringLower(SHA256.HashData(body)), cancellationToken, [digest]).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<string> ConsumeDeleteAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "DeleteObjects", Bucket, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return S3Answers.ParseDeleteErrors(bytes.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// The <c>Content-MD5</c> a batch delete asks for, or, on a host whose operating system forbids
    /// MD5, the CRC-32 checksum header the AWS SDKs send instead.
    /// </summary>
    private static (string, string) DeleteDigest(byte[] body)
    {
        try
        {
            return ("content-md5", Convert.ToBase64String(MD5.HashData(body)));
        }
        catch (Exception forbidden) when (forbidden is CryptographicException or PlatformNotSupportedException)
        {
            return Crc32Digest(body);
        }
    }

    /// <summary>The CRC-32 checksum header of a body, high byte first, in base64.</summary>
    private static (string, string) Crc32Digest(byte[] body)
    {
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(body));
        return ("x-amz-checksum-crc32", Convert.ToBase64String(crc));
    }

    /// <summary>The body of a quiet <c>DeleteObjects</c> over <paramref name="count"/> keys from <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentException">A key holds a character XML cannot carry.</exception>
    private static byte[] DeleteBody(IReadOnlyList<string> keys, int start, int count) =>
        ServiceXml.Write((Keys: keys, Start: start, Count: count), static (xml, s) =>
        {
            xml.WriteStartElement("Delete", S3Answers.Namespace);
            xml.WriteElementString("Quiet", S3Answers.Namespace, "true");
            for (int i = s.Start; i < s.Start + s.Count; i++)
            {
                xml.WriteStartElement("Object", S3Answers.Namespace);
                xml.WriteElementString("Key", S3Answers.Namespace, s.Keys[i]);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        });

    /// <summary>The checksum and the put id a put carries, signed with it.</summary>
    private (string Name, string Value)[] PutHeaders(ReadOnlySequence<byte> body, string? putId)
    {
        List<(string, string)> headers = [];
        switch (ChecksumOf())
        {
            case S3Checksum.Crc64Nvme:
                headers.Add(("x-amz-checksum-crc64nvme", Base64(Checksums.Crc64Nvme(body))));
                break;
            case S3Checksum.ContentMd5:
                headers.Add(("content-md5", Convert.ToBase64String(Checksums.Md5(body))));
                break;
        }

        if (putId is not null)
        {
            headers.Add((PutIdHeader, putId));
        }

        return [.. headers];
    }

    /// <summary>
    /// The checksum a put carries: the option's, or the profile's, which is none on the clouds that
    /// share only <c>Content-MD5</c> until a bucket under Object Lock refuses a put without one, after
    /// which every put of the store carries it.
    /// </summary>
    private S3Checksum ChecksumOf()
    {
        S3Checksum chosen = _options.Checksum != S3Checksum.Default
            ? _options.Checksum
            : _profile is S3Profile.Amazon ? S3Checksum.Crc64Nvme : S3Checksum.None;
        return chosen == S3Checksum.None && Volatile.Read(ref _md5Required) != 0 ? S3Checksum.ContentMd5 : chosen;
    }

    /// <summary>
    /// Whether the service refused a put for want of a checksum, as a bucket under Object Lock does:
    /// the store then sends <c>Content-MD5</c> with every put, and this one again.
    /// </summary>
    private bool LearnsChecksum(ServiceError error)
    {
        if (error.Code != "InvalidRequest" || error.Message?.Contains("Content-MD5", StringComparison.OrdinalIgnoreCase) != true
            || ChecksumOf() != S3Checksum.None)
        {
            return false;
        }

        Volatile.Write(ref _md5Required, 1);
        return true;
    }

    /// <summary>The headers a body's put signs, <c>Content-MD5</c> added when the store has learned its bucket asks for one.</summary>
    private (string Name, string Value)[] WithDigest((string Name, string Value)[] extra, ReadOnlySequence<byte> body)
    {
        if (ChecksumOf() != S3Checksum.ContentMd5)
        {
            return extra;
        }

        foreach ((string name, _) in extra)
        {
            if (name == "content-md5")
            {
                return extra;
            }
        }

        return [.. extra, ("content-md5", Convert.ToBase64String(Checksums.Md5(body)))];
    }

    private static void AddCondition(HttpRequestMessage request, PutCondition condition)
    {
        if (condition.Kind == PutConditionKind.IfAbsent)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else if (condition.Kind == PutConditionKind.IfMatch)
        {
            request.Headers.TryAddWithoutValidation("If-Match", condition.Token);
        }
    }

    private static string Base64(ulong crc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>An S3 upload, and, when its parts carry CRCs, the whole object's, kept as the parts fill in order.</summary>
    private sealed class S3Upload(string key, string id, string putId, bool crc) : UploadState(key, id, putId)
    {
        private readonly Crc64? _whole = crc ? new Crc64(Crc64ParameterSet.Nvme) : null;

        /// <summary>Whether the parts carry their CRC-64/NVME, and the completion the whole object's.</summary>
        internal bool Crc => crc;

        /// <summary>The whole object's CRC-64/NVME once every part is produced, or null.</summary>
        internal ulong? Whole => _whole?.GetCurrentHashAsUInt64();

        internal override void Produced(ReadOnlySequence<byte> part)
        {
            if (_whole is { } whole)
            {
                Checksums.Append(whole, part);
            }
        }
    }

    private readonly record struct PutRequest(S3ObjectStore Store, string Key, ReadOnlySequence<byte> Body, (string, string)[] Extra, PutCondition Condition);

    private readonly record struct UploadRequest(S3ObjectStore Store, string Key, string UploadId, string PutId, bool Crc);

    private readonly record struct PartRequest(S3ObjectStore Store, string Key, string UploadId, int Number, ReadOnlySequence<byte> Body, (string, string)[] Extra);

    private readonly record struct CompleteRequest(S3ObjectStore Store, string Key, string UploadId, byte[] Body, (string, string)[] Extra, PutCondition Condition);

    private readonly record struct BodyRequest(S3ObjectStore Store, string Key, byte[] Body);
}
