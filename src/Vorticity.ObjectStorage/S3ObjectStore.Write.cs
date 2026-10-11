using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Hashing;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.S3;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage;

/// <summary>The write path: puts, whole or in parts, their conditions and checksums, and deletes.</summary>
/// <remarks>
/// <para>
/// A put up to the part threshold is held whole, its checksum computed, and sent in one request; a
/// larger one, or one of unknown length past the threshold, goes up in parts sent in parallel, each
/// held only while it is in flight. A put conditioned on the key's absence carries
/// <c>If-None-Match: *</c>, one on a token <c>If-Match</c>, and both a put id in the object's metadata:
/// when the answer to such a put is lost, the object's head says whether this put created it, and the
/// put is sent again only when nothing did.
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
            return await PutCoreAsync(key, content, length, condition, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<PutOutcome> PutCoreAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        if (condition.Kind == PutConditionKind.IfMatch && (_capabilities & ObjectStoreCapabilities.ReplacesOnTokenMatch) == 0)
        {
            throw new NotSupportedException($"The directory bucket '{Bucket}' is not known to replace an object only while it keeps its ETag.");
        }

        // A service that takes no condition on parts gets a conditional put whole, whatever its size.
        bool whole = condition.Kind != PutConditionKind.None && HoldsConditionalPutsWhole;
        long threshold = whole ? MaxSinglePut : Math.Min(_options.PartThreshold, MaxSinglePut);
        if (length > threshold && whole)
        {
            throw new ObjectStoreException($"'{key}' is {length} bytes, past the {MaxSinglePut} a conditional put to {_profile} sends in one request.");
        }

        if (length >= 0 && length <= threshold)
        {
            (ChunkedBuffer body, bool ended) = await ChunkedBuffer.FillAsync(content, length, cancellationToken).ConfigureAwait(false);
            using (body)
            {
                CheckEnd(key, length, body.Length, ended);
                return await SendWholeAsync(key, body, condition, cancellationToken).ConfigureAwait(false);
            }
        }

        if (length < 0 && whole)
        {
            (ChunkedBuffer all, bool ended) = await ChunkedBuffer.FillAsync(content, MaxSinglePut, cancellationToken).ConfigureAwait(false);
            using (all)
            {
                if (!ended)
                {
                    throw new ObjectStoreException($"'{key}' runs past the {MaxSinglePut} bytes a conditional put to {_profile} sends in one request.");
                }

                return await SendWholeAsync(key, all, condition, cancellationToken).ConfigureAwait(false);
            }
        }

        Queue<ChunkedBuffer> carried = new Queue<ChunkedBuffer>();
        if (length < 0)
        {
            // Unknown: read a part at a time up to the threshold, and sent whole if it ends there.
            (ChunkedBuffer? ended, carried) = await PartPump.HeadAsync(content, PartSizeFor(-1), threshold, cancellationToken).ConfigureAwait(false);
            if (ended is not null)
            {
                using (ended)
                {
                    return await SendWholeAsync(key, ended, condition, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return await SendInPartsAsync(key, content, length, condition, carried, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One request, sent again while its outcome can be learned and it did not land.</summary>
    private async ValueTask<PutOutcome> SendWholeAsync(string key, ChunkedBuffer body, PutCondition condition, CancellationToken cancellationToken)
    {
        string? putId = condition.Kind == PutConditionKind.None ? null : NewPutId();
        (string Name, string Value)[] extra = PutHeaders(body, putId);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(
                    "PutObject",
                    new PutRequest(this, key, body.Sequence(), extra, condition),
                    static (s, ct) => s.Store.BuildPutAsync(s, ct),
                    static (response, s, ct) => s.Store.ConsumePutAsync(response, s.Key, s.Condition, ct),
                    idempotent: putId is null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (StoreAnswerLostException lost)
            {
                if (await LearnPutAsync(key, putId!, condition, lost, cancellationToken).ConfigureAwait(false) is { } learned)
                {
                    return learned;
                }

                if (!await _transport.RetryAfterAsync(attempt, cancellationToken).ConfigureAwait(false))
                {
                    throw new ObjectStoreException($"PutObject of '{key}' did not land after {attempt} attempts: {lost.Message}", lost.InnerException!);
                }
            }
        }
    }

    /// <summary>
    /// A multipart upload: started, its parts sent in parallel as they fill, and completed with the
    /// condition and the whole object's checksum; aborted whatever fails before the completion.
    /// </summary>
    private async ValueTask<PutOutcome> SendInPartsAsync(
        string key, PipeReader content, long length, PutCondition condition, Queue<ChunkedBuffer> carried, CancellationToken cancellationToken)
    {
        int partSize = PartSizeFor(length);
        int maxParts = MaxParts;
        string putId = NewPutId();
        bool crc = ChecksumOf() == S3Checksum.Crc64Nvme;
        string uploadId;
        try
        {
            uploadId = await CreateUploadAsync(key, putId, crc, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            PartPump.Release(carried);
            throw;
        }

        List<Part> parts = [];
        try
        {
            await PartPump.SendAsync(
                key,
                content,
                length,
                partSize,
                maxParts,
                _options.PartsInFlight,
                carried,
                (number, body, ct) =>
                {
                    Part part = new Part(number, body.Length);
                    lock (parts)
                    {
                        parts.Add(part);
                    }

                    return UploadPartAsync(key, uploadId, part, body, crc, ct);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }

        parts.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        PutOutcome outcome;
        try
        {
            outcome = await CompleteAsync(key, uploadId, putId, parts, crc, condition, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }

        // A completion refused on its condition leaves its upload, and its parts, behind.
        if (outcome != PutOutcome.Created)
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Starts an upload in parts carrying <paramref name="putId"/>, and the whole object's CRC-64/NVME when <paramref name="crc"/>.</summary>
    private ValueTask<string> CreateUploadAsync(string key, string putId, bool crc, CancellationToken cancellationToken) =>
        _transport.SendAsync(
            "CreateMultipartUpload",
            new UploadRequest(this, key, string.Empty, putId, crc),
            static (s, ct) => s.Store.BuildCreateUploadAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeCreateUploadAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken);

    private async Task UploadPartAsync(string key, string uploadId, Part part, ChunkedBuffer body, bool crc, CancellationToken cancellationToken)
    {
        ulong checksum = crc ? body.Crc64() : 0;
        List<(string, string)> extra = [];
        if (crc)
        {
            extra.Add(("x-amz-checksum-crc64nvme", Base64(checksum)));
        }
        else if (ChecksumOf() == S3Checksum.ContentMd5)
        {
            extra.Add(("content-md5", Convert.ToBase64String(body.Md5())));
        }

        part.Etag = await _transport.SendAsync(
            "UploadPart",
            new PartRequest(this, key, uploadId, part.Number, body.Sequence(), [.. extra]),
            static (s, ct) => s.Store.BuildPartAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumePartAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        part.Crc = checksum;
    }

    /// <summary>Completes an upload with its condition, learning its outcome from the head when the answer is lost.</summary>
    private async ValueTask<PutOutcome> CompleteAsync(string key, string uploadId, string putId, List<Part> parts, bool crc, PutCondition condition, CancellationToken cancellationToken)
    {
        StringBuilder xml = new StringBuilder("<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        ulong whole = ulong.MaxValue;
        bool first = true;
        foreach (Part part in parts)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Part><PartNumber>{part.Number}</PartNumber><ETag>").AppendEscaped(part.Etag!).Append("</ETag>");
            if (crc)
            {
                xml.Append("<ChecksumCRC64NVME>").Append(Base64(part.Crc)).Append("</ChecksumCRC64NVME>");
                whole = first ? part.Crc : Crc64Nvme.Combine(whole, part.Crc, part.Length);
                first = false;
            }

            xml.Append("</Part>");
        }

        xml.Append("</CompleteMultipartUpload>");
        byte[] body = Encoding.UTF8.GetBytes(xml.ToString());
        (string, string)[] extra = crc
            ? [("x-amz-checksum-crc64nvme", Base64(whole)), ("x-amz-checksum-type", "FULL_OBJECT")]
            : [];
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(
                    "CompleteMultipartUpload",
                    new CompleteRequest(this, key, uploadId, body, extra, condition),
                    static (s, ct) => s.Store.BuildCompleteAsync(s, ct),
                    static (response, s, ct) => s.Store.ConsumeCompleteAsync(response, s, ct),
                    idempotent: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (StoreAnswerLostException lost)
            {
                if (await LearnPutAsync(key, putId, condition, lost, cancellationToken).ConfigureAwait(false) is { } learned)
                {
                    return learned;
                }

                if (!await _transport.RetryAfterAsync(attempt, cancellationToken).ConfigureAwait(false))
                {
                    throw new ObjectStoreException($"CompleteMultipartUpload of '{key}' did not land after {attempt} attempts: {lost.Message}", lost.InnerException!);
                }
            }
        }
    }

    /// <summary>
    /// What became of a put whose answer was lost, from the object's head: created by this put when it
    /// carries its id, taken by another when not, and null, to send the put again, when nothing is there
    /// or the token it was to replace still is.
    /// </summary>
    /// <exception cref="ObjectStoreException">The head failed too, and the outcome is unknown.</exception>
    private async ValueTask<PutOutcome?> LearnPutAsync(string key, string putId, PutCondition condition, StoreAnswerLostException lost, CancellationToken cancellationToken)
    {
        (ObjectHead? head, string? id) found;
        try
        {
            found = await _transport.SendAsync(
                "HeadObject",
                new Request(this, key),
                static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, null, ct),
                static async (response, s, ct) => (await s.Store.ConsumeHeadAsync(response, s.Key, ct).ConfigureAwait(false), S3Answers.Header(response, PutIdHeader)),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception head) when (head is ObjectStoreException or StoreAnswerLostException)
        {
            throw new ObjectStoreException(
                $"A put of '{key}' lost its answer ({lost.Message}), and the head that would say whether it landed failed too: the outcome is unknown.",
                new AggregateException(lost, head))
            {
                OutcomeUnknown = true,
            };
        }

        if (found.head is not { } object_)
        {
            return condition.Kind == PutConditionKind.IfMatch ? PutOutcome.Changed : null;
        }

        if (string.Equals(found.id, putId, StringComparison.Ordinal))
        {
            return PutOutcome.Created;
        }

        if (condition.Kind == PutConditionKind.IfMatch)
        {
            return string.Equals(object_.Token, condition.Token, StringComparison.Ordinal) ? null : PutOutcome.Changed;
        }

        // Taken by another writer, or, unconditionally, a put that carried no id: an object is there.
        return condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : null;
    }

    private async ValueTask AbortAsync(string key, string uploadId)
    {
        try
        {
            await _transport.SendAsync(
                "AbortMultipartUpload",
                new UploadRequest(this, key, uploadId, string.Empty, false),
                static (s, ct) => s.Store.BuildAbortAsync(s, ct),
                static (response, _, _) => new ValueTask<bool>(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound),
                idempotent: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception abandoned) when (abandoned is ObjectStoreException or StoreAnswerLostException or OperationCanceledException)
        {
            // An upload left behind holds no key and is cleaned by the bucket's lifecycle rule: the
            // failure that is on its way out says more.
        }
    }

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
            return S3Answers.ParseElement(bytes.AsSpan(0, length), "UploadId"u8)
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
            return S3Answers.Header(response, "ETag") ?? throw new ObjectStoreException($"UploadPart of '{key}' was answered without an ETag.");
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
                    if (S3Answers.IsError(bytes.AsSpan(0, length), out string? code))
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
            case HttpStatusCode.NotFound when s.Condition.Kind == PutConditionKind.IfMatch && S3Answers.Header(response, "x-amz-error-code") != "NoSuchUpload":
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
            throw new ObjectStoreException(
                $"CompleteMultipartUpload of '{s.Key}' met a concurrent delete: the service asks for a new upload, so the put must be made again. {error}");
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
        // A directory bucket takes no Content-MD5: its delete carries the CRC-32 the SDKs send there.
        (string, string) digest = _zone is null ? DeleteDigest(body) : Crc32Digest(body);
        await SignAsync(request, target, path, "delete=", Convert.ToHexStringLower(SHA256.HashData(body)), cancellationToken, [digest]).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<string> ConsumeDeleteAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "DeleteObjects", Bucket, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, S3Answers.MaxXmlBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return S3Answers.ParseDeleteErrors(bytes.AsSpan(0, length));
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
        BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(body));
        return ("x-amz-checksum-crc32", Convert.ToBase64String(crc));
    }

    private static byte[] DeleteBody(IReadOnlyList<string> keys, int start, int count)
    {
        StringBuilder xml = new StringBuilder("<Delete xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Quiet>true</Quiet>");
        for (int i = start; i < start + count; i++)
        {
            xml.Append("<Object><Key>").AppendEscaped(keys[i]).Append("</Key></Object>");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</Delete>").ToString());
    }

    /// <summary>The checksum and the put id a put carries, signed with it.</summary>
    private (string Name, string Value)[] PutHeaders(ChunkedBuffer body, string? putId)
    {
        List<(string, string)> headers = [];
        switch (ChecksumOf())
        {
            case S3Checksum.Crc64Nvme:
                headers.Add(("x-amz-checksum-crc64nvme", Base64(body.Crc64())));
                break;
            case S3Checksum.ContentMd5:
                headers.Add(("content-md5", Convert.ToBase64String(body.Md5())));
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

        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            md5.AppendData(segment.Span);
        }

        return [.. extra, ("content-md5", Convert.ToBase64String(md5.GetHashAndReset()))];
    }

    /// <summary>The parts an upload may have: 1 000 on Scaleway, 10 000 elsewhere.</summary>
    private int MaxParts => _profile == S3Profile.Scaleway ? 1_000 : 10_000;

    /// <summary>
    /// A part's size: the option's, at least the service's minimum, and larger when the object would
    /// need more parts than the service takes, rounded up to a mebibyte.
    /// </summary>
    internal int PartSizeFor(long length)
    {
        long size = Math.Max(_options.PartSize, _options.MinimumPartSize);
        long needed = length > 0 ? (length + MaxParts - 1) / MaxParts : 0;
        if (needed > size)
        {
            size = (needed + (1 << 20) - 1) >> 20 << 20;
        }

        return (int)Math.Min(size, MaxSinglePut);
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

    private static void CheckEnd(string key, long length, long read, bool ended)
    {
        if (read < length)
        {
            throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {read}.");
        }

        if (!ended)
        {
            throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content runs past them.");
        }
    }

    private static string NewPutId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string Base64(ulong crc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>A part of an upload: its number and length, then its ETag and CRC once sent.</summary>
    private sealed class Part(int number, long length)
    {
        internal int Number => number;

        internal long Length => length;

        internal string? Etag { get; set; }

        internal ulong Crc { get; set; }
    }

    private readonly record struct PutRequest(S3ObjectStore Store, string Key, ReadOnlySequence<byte> Body, (string, string)[] Extra, PutCondition Condition);

    private readonly record struct UploadRequest(S3ObjectStore Store, string Key, string UploadId, string PutId, bool Crc);

    private readonly record struct PartRequest(S3ObjectStore Store, string Key, string UploadId, int Number, ReadOnlySequence<byte> Body, (string, string)[] Extra);

    private readonly record struct CompleteRequest(S3ObjectStore Store, string Key, string UploadId, byte[] Body, (string, string)[] Extra, PutCondition Condition);

    private readonly record struct BodyRequest(S3ObjectStore Store, string Key, byte[] Body);
}
