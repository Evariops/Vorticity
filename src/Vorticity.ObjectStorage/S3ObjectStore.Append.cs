using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.S3;

namespace Vorticity.ObjectStorage;

/// <summary>
/// Appends: the grown object built on the service as an upload in parts, the old object copied into
/// its first parts and the new bytes following, the completion conditioned on the old object's ETag.
/// </summary>
/// <remarks>
/// A copied part, like every part but the last, holds at least 5 MiB, so an object smaller than that
/// is read back and sent again with the new bytes, and so is any object on a service that copies no
/// part. The copies run on the service, at most <see cref="S3StoreOptions.PartsInFlight"/> at once,
/// while the new parts are sent.
/// </remarks>
public sealed partial class S3ObjectStore : IAppendingObjectStore
{
    /// <summary>
    /// The most one copied part holds: a gibibyte, far below the 5 GiB the service takes, so that a large
    /// object is copied in parts the service works on side by side.
    /// </summary>
    internal const long MaxCopiedPartBytes = 1L << 30;

    /// <summary>Whether an append copies the old object on the service, as <see cref="S3StoreOptions.CopiesParts"/> says.</summary>
    private bool CopiesParts => _options.CopiesParts ?? _profile is not (S3Profile.Ovhcloud or S3Profile.R2);

    /// <inheritdoc/>
    async ValueTask<PutOutcome> IAppendingObjectStore.AppendAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (_zone is null && (!CopiesParts || length < _options.MinimumPartSize))
        {
            return await ObjectAppends.ResendAsync(this, key, token, length, content, cancellationToken).ConfigureAwait(false);
        }

        Exception? failure = null;
        try
        {
            CheckKey(key);
            ThrowIfDisposed();
            return _zone is not null
                ? await AppendNativelyAsync(key, length, content, cancellationToken).ConfigureAwait(false)
                : await AppendByCopyAsync(key, token, length, content, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The upload that grows the object: started, its first parts copied from the old object bound to
    /// its ETag while the new parts are sent, and completed only while the object keeps that ETag.
    /// Aborted whatever fails, and when the object changed.
    /// </summary>
    private async ValueTask<PutOutcome> AppendByCopyAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        int copies = (int)((length + MaxCopiedPartBytes - 1) / MaxCopiedPartBytes);
        long copySize = (length + copies - 1) / copies;
        if (copies >= MaxParts)
        {
            throw new ObjectStoreException($"'{key}' is {length} bytes, past the {MaxParts} parts of {MaxCopiedPartBytes} bytes an append copies it in.");
        }

        string putId = NewPutId();
        bool crc = ChecksumOf() == S3Checksum.Crc64Nvme;
        string uploadId = await CreateUploadAsync(key, putId, crc, cancellationToken).ConfigureAwait(false);
        List<Part> parts = [];
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> copying = CopyPartsAsync(key, uploadId, token, length, copies, copySize, parts, stop);
        Exception? sendFailure = null;
        try
        {
            await PartPump.SendAsync(
                key,
                content,
                -1,
                PartSizeFor(-1),
                MaxParts - copies,
                _options.PartsInFlight,
                new Queue<ChunkedBuffer>(),
                (number, body, ct) =>
                {
                    Part part = new Part(number, body.Length);
                    lock (parts)
                    {
                        parts.Add(part);
                    }

                    return UploadPartAsync(key, uploadId, part, body, crc, ct);
                },
                stop.Token,
                firstPart: copies + 1).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            sendFailure = caught;
            await stop.CancelAsync().ConfigureAwait(false);
        }

        bool copied;
        try
        {
            copied = await copying.ConfigureAwait(false);
        }
        catch (Exception) when (sendFailure is not null)
        {
            // The send's failure stopped the copies: it is the one to say.
            copied = true;
        }
        catch
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }

        if (!copied)
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            return PutOutcome.Changed;
        }

        if (sendFailure is not null)
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(sendFailure);
        }

        parts.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        PutOutcome outcome;
        try
        {
            outcome = await CompleteAsync(key, uploadId, putId, parts, crc, PutCondition.IfMatch(token), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }

        if (outcome != PutOutcome.Created)
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Copies the old object into the upload's first <paramref name="copies"/> parts, each bound to its
    /// ETag, at most the parts in flight at once. False when the object changed or is gone, which stops
    /// the append.
    /// </summary>
    private async Task<bool> CopyPartsAsync(
        string key, string uploadId, string token, long length, int copies, long copySize, List<Part> parts, CancellationTokenSource stop)
    {
        using SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, _options.PartsInFlight));
        List<Task<bool>> sent = new List<Task<bool>>(copies);
        bool all = true;
        try
        {
            for (int i = 0; i < copies; i++)
            {
                long first = i * copySize;
                Part part = new Part(i + 1, Math.Min(copySize, length - first));
                lock (parts)
                {
                    parts.Add(part);
                }

                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                sent.Add(CopyPartAsync(key, uploadId, token, part, first, slots, stop));
            }
        }
        finally
        {
            foreach (Task<bool> copy in sent)
            {
                try
                {
                    all &= await copy.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Stopped by another part, whose own outcome is the one to say.
                }
            }
        }

        if (!all)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        return all;
    }

    private async Task<bool> CopyPartAsync(string key, string uploadId, string token, Part part, long first, SemaphoreSlim slots, CancellationTokenSource stop)
    {
        try
        {
            (string? etag, ulong crc) = await _transport.SendAsync(
                "UploadPartCopy",
                new CopyRequest(this, key, uploadId, part.Number, token, first, part.Length),
                static (s, ct) => s.Store.BuildCopyAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeCopyAsync(response, s, ct),
                idempotent: true,
                stop.Token).ConfigureAwait(false);
            if (etag is null)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                return false;
            }

            part.Etag = etag;
            part.Crc = crc;
            return true;
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            slots.Release();
        }
    }

    private async ValueTask<HttpRequestMessage> BuildCopyAsync(CopyRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(s.Key);
        string query = string.Create(CultureInfo.InvariantCulture, $"partNumber={s.Number}&uploadId={UriEncoding.Query(s.UploadId)}");
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, new Uri(target.Base + path + "?" + query, in Verbatim));
        (string, string)[] extra =
        [
            ("x-amz-copy-source", "/" + UriEncoding.Path(Bucket) + "/" + UriEncoding.Path(s.Key)),
            ("x-amz-copy-source-if-match", s.Token),
            ("x-amz-copy-source-range", string.Create(CultureInfo.InvariantCulture, $"bytes={s.First}-{s.First + s.Length - 1}")),
        ];
        await SignAsync(request, target, path, query, SigV4Signer.EmptyPayloadHash, cancellationToken, extra).ConfigureAwait(false);
        return request;
    }

    /// <summary>The copied part's ETag and CRC-64/NVME; a null ETag when the old object changed or is gone.</summary>
    private async ValueTask<(string? ETag, ulong Crc)> ConsumeCopyAsync(HttpResponseMessage response, CopyRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return (null, 0);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The old object gone is the append's refusal; the upload gone is a failure.
            ServiceError missing = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return missing.Code == "NoSuchUpload" ? throw MapFailure(response, missing, "UploadPartCopy", s.Key) : (null, 0);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "UploadPartCopy", s.Key, null, cancellationToken).ConfigureAwait(false);
        }

        // A copy may fail after its 200 is sent, its error then in the body.
        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 64 << 10, cancellationToken).ConfigureAwait(false);
        try
        {
            ReadOnlySpan<byte> xml = bytes.AsSpan(0, length);
            if (S3Answers.IsError(xml, out string? code))
            {
                if (code is "PreconditionFailed" or "NoSuchKey")
                {
                    return (null, 0);
                }

                throw new StoreRetryException($"UploadPartCopy of '{s.Key}' failed after its answer began: {code}", RetryBudget.RetryCost, notApplied: true);
            }

            string etag = S3Answers.ParseElement(xml, "ETag"u8)
                ?? throw new ObjectStoreException($"UploadPartCopy of '{s.Key}' was answered without an ETag.");
            ulong crc = 0;
            if (ChecksumOf() == S3Checksum.Crc64Nvme)
            {
                string text = S3Answers.ParseElement(xml, "ChecksumCRC64NVME"u8)
                    ?? throw new ObjectStoreException(
                        $"UploadPartCopy of '{s.Key}' was answered without the part's CRC-64/NVME, which the whole object's checksum is combined from. Set S3StoreOptions.CopiesParts to false, or choose another checksum.");
                Span<byte> raw = stackalloc byte[8];
                if (!Convert.TryFromBase64String(text, raw, out int written) || written != 8)
                {
                    throw new ObjectStoreException($"UploadPartCopy of '{s.Key}' was answered with a CRC-64/NVME that is not 8 bytes of base64: '{text}'.");
                }

                crc = BinaryPrimitives.ReadUInt64BigEndian(raw);
            }

            return (etag, crc);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// The append of a directory bucket, which grows an object in place: the new bytes put at the
    /// object's end (<c>x-amz-write-offset-bytes</c>), which the service refuses when the object is no
    /// longer that long. Up to the part threshold, the new bytes go in one request, whole or not at all;
    /// past it, in pieces of the threshold each, between which the file opens at its version before the
    /// append.
    /// </summary>
    private async ValueTask<PutOutcome> AppendNativelyAsync(string key, long length, PipeReader content, CancellationToken cancellationToken)
    {
        long piece = Math.Min(_options.PartThreshold, MaxSinglePut);
        long offset = length;
        while (true)
        {
            (ChunkedBuffer body, bool ended) = await ChunkedBuffer.FillAsync(content, piece, cancellationToken).ConfigureAwait(false);
            using (body)
            {
                if (body.Length > 0)
                {
                    PutOutcome outcome = await AppendPieceAsync(key, offset, body, cancellationToken).ConfigureAwait(false);
                    if (outcome != PutOutcome.Created)
                    {
                        return outcome;
                    }

                    offset += body.Length;
                }
            }

            if (ended)
            {
                return PutOutcome.Created;
            }
        }
    }

    /// <summary>One piece put at <paramref name="offset"/>; when its answer is lost, the object's length says whether it landed.</summary>
    private async ValueTask<PutOutcome> AppendPieceAsync(string key, long offset, ChunkedBuffer body, CancellationToken cancellationToken)
    {
        (string Name, string Value)[] extra =
            [.. PutHeaders(body, null), ("x-amz-write-offset-bytes", offset.ToString(CultureInfo.InvariantCulture))];
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(
                    "PutObject",
                    new PutRequest(this, key, body.Sequence(), extra, PutCondition.None),
                    static (s, ct) => s.Store.BuildPutAsync(s, ct),
                    static (response, s, ct) => s.Store.ConsumeAppendAsync(response, s.Key, ct),
                    idempotent: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (StoreAnswerLostException lost)
            {
                ObjectHead? head;
                try
                {
                    head = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failed) when (failed is ObjectStoreException or StoreAnswerLostException)
                {
                    throw new ObjectStoreException(
                        $"An append to '{key}' lost its answer ({lost.Message}), and the head that would say whether it landed failed too: the outcome is unknown.",
                        new AggregateException(lost, failed))
                    {
                        OutcomeUnknown = true,
                    };
                }

                if (head is { } grown && grown.Length == offset + body.Length)
                {
                    return PutOutcome.Created;
                }

                if (head is not { } still || still.Length != offset)
                {
                    return PutOutcome.Changed;
                }

                if (!await _transport.RetryAfterAsync(attempt, cancellationToken).ConfigureAwait(false))
                {
                    throw new ObjectStoreException($"An append to '{key}' did not land after {attempt} attempts: {lost.Message}", lost.InnerException!);
                }
            }
        }
    }

    private async ValueTask<PutOutcome> ConsumeAppendAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound:
                return PutOutcome.Changed;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (error.Code == "InvalidWriteOffset")
        {
            // The object is no longer as long as it was read: another writer replaced or grew it.
            return PutOutcome.Changed;
        }

        if (error.Code is "BadDigest" or "InvalidDigest" or "XAmzContentChecksumMismatch" or "IncompleteBody")
        {
            throw new StoreRetryException($"An append to '{key}' arrived damaged: {error}", RetryBudget.RetryCost, notApplied: true);
        }

        throw MapFailure(response, error, "PutObject", key);
    }

    /// <summary>A part copied from the old object: its number, and the range of the object it holds.</summary>
    private readonly record struct CopyRequest(S3ObjectStore Store, string Key, string UploadId, int Number, string Token, long First, long Length);
}
