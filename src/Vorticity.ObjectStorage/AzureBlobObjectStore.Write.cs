using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Azure;
using Vorticity.ObjectStorage.Hashing;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage;

/// <summary>The write path: <c>Put Blob</c>, blocks and their list, and deletes in batches.</summary>
/// <remarks>
/// <para>
/// A put up to the part threshold is one <c>Put Blob</c>; a larger one sends its blocks in parallel as
/// they fill, then commits their list with the condition. Each body carries <c>x-ms-content-crc64</c>,
/// which the service checks. A block never committed is dropped by the service a week after the last
/// one was sent, so a put that fails leaves nothing to clean.
/// </para>
/// <para>
/// A conditional put carries a put id in the blob's metadata, and a put whose answer is lost learns its
/// outcome from the blob's properties, as on S3. A delete goes in batches of 256 blobs, each one
/// authorized in the batch.
/// </para>
/// </remarks>
public sealed partial class AzureBlobObjectStore
{
    /// <summary>The metadata a conditional put carries its id in.</summary>
    internal const string PutIdHeader = "x-ms-meta-vxput";

    /// <summary>The most one <c>Put Blob</c> takes: 5 000 MiB.</summary>
    internal const long MaxSinglePut = 5_000L << 20;

    private const int MaxBlocks = 50_000;
    private const int MaxBatch = 256;

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
        for (int start = 0; start < keys.Count; start += MaxBatch)
        {
            string[] batch = new string[Math.Min(MaxBatch, keys.Count - start)];
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = keys[start + i];
            }

            string failed = await _transport.SendAsync(
                "BlobBatch",
                new BatchRequest(this, batch, $"batch_{Guid.NewGuid():D}"),
                static (s, ct) => s.Store.BuildBatchAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeBatchAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            if (failed.Length > 0)
            {
                throw new ObjectStoreException($"Blob Batch left blobs in place: {failed}");
            }
        }
    }

    private async ValueTask<PutOutcome> PutCoreAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        long threshold = Math.Min(_options.PartThreshold, MaxSinglePut);
        if (length >= 0 && length <= threshold)
        {
            (ChunkedBuffer body, bool ended) = await ChunkedBuffer.FillAsync(content, length, cancellationToken).ConfigureAwait(false);
            using (body)
            {
                if (body.Length < length)
                {
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {body.Length}.");
                }

                if (!ended)
                {
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content runs past them.");
                }

                return await PutBlobAsync(key, body, condition, cancellationToken).ConfigureAwait(false);
            }
        }

        Queue<ChunkedBuffer> carried = new Queue<ChunkedBuffer>();
        int blockSize = BlockSizeFor(length);
        if (length < 0)
        {
            (ChunkedBuffer? whole, carried) = await PartPump.HeadAsync(content, blockSize, threshold, cancellationToken).ConfigureAwait(false);
            if (whole is not null)
            {
                using (whole)
                {
                    return await PutBlobAsync(key, whole, condition, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return await PutBlocksAsync(key, content, length, condition, blockSize, carried, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One <c>Put Blob</c>, sent again while its outcome can be learned and it did not land.</summary>
    private async ValueTask<PutOutcome> PutBlobAsync(string key, ChunkedBuffer body, PutCondition condition, CancellationToken cancellationToken)
    {
        string? putId = condition.Kind == PutConditionKind.None ? null : NewPutId();
        List<(string, string)> headers = [("x-ms-blob-type", "BlockBlob")];
        if (_options.SendChecksums)
        {
            headers.Add(("x-ms-content-crc64", Crc64Header(body.Crc64())));
        }

        if (putId is not null)
        {
            headers.Add((PutIdHeader, putId));
        }

        AddCondition(headers, condition);
        return await SendConditionalAsync(
            key,
            putId,
            condition,
            "PutBlob",
            new WriteRequest(this, key, string.Empty, body.Sequence(), [.. headers], condition),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Blocks sent in parallel as they fill, then their list committed with the condition; a failure
    /// leaves only uncommitted blocks, which the service drops.
    /// </summary>
    private async ValueTask<PutOutcome> PutBlocksAsync(
        string key, PipeReader content, long length, PutCondition condition, int blockSize, Queue<ChunkedBuffer> carried, CancellationToken cancellationToken)
    {
        // Every id of a blob's blocks is of one length: an upload's random part, then the block's number.
        string upload = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        List<long> blocks = await PartPump.SendAsync(
            key,
            content,
            length,
            blockSize,
            MaxBlocks,
            _options.BlocksInFlight,
            carried,
            (number, block, ct) => PutBlockAsync(key, BlockId(upload, number), block, ct),
            cancellationToken).ConfigureAwait(false);
        return await CommitBlocksAsync(key, upload, blocks.Count, condition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits the first <paramref name="count"/> blocks of an upload, in order, with the condition and a put id.</summary>
    private async ValueTask<PutOutcome> CommitBlocksAsync(string key, string upload, int count, PutCondition condition, CancellationToken cancellationToken)
    {
        StringBuilder xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        for (int number = 1; number <= count; number++)
        {
            xml.Append("<Latest>").Append(BlockId(upload, number)).Append("</Latest>");
        }

        byte[] list = Encoding.UTF8.GetBytes(xml.Append("</BlockList>").ToString());
        string putId = NewPutId();
        List<(string, string)> headers = [(PutIdHeader, putId)];
        if (_options.SendChecksums)
        {
            headers.Add(("x-ms-content-crc64", Crc64Header(Crc64Nvme.Hash(list))));
        }

        AddCondition(headers, condition);
        return await SendConditionalAsync(
            key,
            putId,
            condition,
            "PutBlockList",
            new WriteRequest(this, key, "comp=blocklist", new ReadOnlySequence<byte>(list), [.. headers], condition),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PutBlockAsync(string key, string id, ChunkedBuffer block, CancellationToken cancellationToken)
    {
        (string, string)[] headers = _options.SendChecksums ? [("x-ms-content-crc64", Crc64Header(block.Crc64()))] : [];
        await _transport.SendAsync(
            "PutBlock",
            new WriteRequest(this, key, "comp=block&blockid=" + UriEncoding.Query(id), block.Sequence(), headers, PutCondition.None),
            static (s, ct) => s.Store.BuildWriteAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeWriteAsync(response, s, "PutBlock", ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The id of block <paramref name="number"/> of an upload, all of one length as Azure asks.</summary>
    private static string BlockId(string upload, int number) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{upload}{number - 1:D6}")));

    /// <summary>A write that carries a condition: sent again only when its lost answer proves it did not land.</summary>
    private async ValueTask<PutOutcome> SendConditionalAsync(string key, string? putId, PutCondition condition, string operation, WriteRequest request, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(
                    operation,
                    request,
                    static (s, ct) => s.Store.BuildWriteAsync(s, ct),
                    static (response, s, ct) => s.Store.ConsumeWriteAsync(response, s, s.Query.Length == 0 ? "PutBlob" : "PutBlockList", ct),
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
                    throw new ObjectStoreException($"{operation} of '{key}' did not land after {attempt} attempts: {lost.Message}", lost.InnerException!);
                }
            }
        }
    }

    /// <summary>What became of a put whose answer was lost, from the blob's properties and the put id they carry.</summary>
    private async ValueTask<PutOutcome?> LearnPutAsync(string key, string putId, PutCondition condition, StoreAnswerLostException lost, CancellationToken cancellationToken)
    {
        (ObjectHead? head, string? id) found;
        try
        {
            found = await _transport.SendAsync(
                "GetBlobProperties",
                new KeyRequest(this, key),
                static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, string.Empty, null, [], ct),
                static (response, s, ct) => s.Store.ConsumeHeadAsync(response, s.Key, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception head) when (head is ObjectStoreException or StoreAnswerLostException)
        {
            throw new ObjectStoreException(
                $"A put of '{key}' lost its answer ({lost.Message}), and the properties that would say whether it landed failed too: the outcome is unknown.",
                new AggregateException(lost, head))
            {
                OutcomeUnknown = true,
            };
        }

        if (found.head is not { } blob)
        {
            return condition.Kind == PutConditionKind.IfMatch ? PutOutcome.Changed : null;
        }

        if (string.Equals(found.id, putId, StringComparison.Ordinal))
        {
            return PutOutcome.Created;
        }

        if (condition.Kind == PutConditionKind.IfMatch)
        {
            return string.Equals(blob.Token, condition.Token, StringComparison.Ordinal) ? null : PutOutcome.Changed;
        }

        return condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : null;
    }

    private async ValueTask<HttpRequestMessage> BuildWriteAsync(WriteRequest s, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = await BuildAsync(HttpMethod.Put, s.Key, s.Query, new SequenceContent(s.Body), s.Headers, cancellationToken).ConfigureAwait(false);

        // Outside what Shared Key signs: a body of a mebibyte or more waits for the service's 100 Continue.
        request.Headers.ExpectContinue = s.Body.Length >= 1 << 20;
        return request;
    }

    private async ValueTask<PutOutcome> ConsumeWriteAsync(HttpResponseMessage response, WriteRequest s, string operation, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed:
                return s.Condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : PutOutcome.Changed;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict && error.Code == "BlobAlreadyExists" && s.Condition.Kind == PutConditionKind.IfAbsent)
        {
            return PutOutcome.Exists;
        }

        if (response.StatusCode == HttpStatusCode.NotFound && error.Code == "BlobNotFound" && s.Condition.Kind == PutConditionKind.IfMatch)
        {
            return PutOutcome.Changed;
        }

        if (error.Code is "Crc64Mismatch" or "Md5Mismatch")
        {
            throw new StoreRetryException($"{operation} of '{s.Key}' arrived damaged: {error}", RetryBudget.RetryCost, notApplied: true);
        }

        throw MapFailure(response, error, operation, s.Key);
    }

    /// <summary>
    /// A <c>Blob Batch</c> scoped to the container: a multipart body of <c>DELETE</c> sub-requests, each
    /// authorized as a request of its own would be.
    /// </summary>
    private async ValueTask<HttpRequestMessage> BuildBatchAsync(BatchRequest s, CancellationToken cancellationToken)
    {
        AzureAuthorization authorization = await _options.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        string date = Now().ToString("r", CultureInfo.InvariantCulture);
        StringBuilder body = new StringBuilder();
        for (int i = 0; i < s.Keys.Length; i++)
        {
            string path = _containerPath + "/" + UriEncoding.Path(s.Keys[i]);
            string target = authorization.Sas is { } sas ? path + "?" + sas : path;
            body.Append("--").Append(s.Boundary).Append("\r\n")
                .Append("Content-Type: application/http\r\n")
                .Append("Content-Transfer-Encoding: binary\r\n")
                .Append(CultureInfo.InvariantCulture, $"Content-ID: {i}\r\n\r\n")
                .Append(CultureInfo.InvariantCulture, $"DELETE {target} HTTP/1.1\r\n")
                .Append(CultureInfo.InvariantCulture, $"x-ms-date: {date}\r\n");
            if (authorization.Bearer is { } token)
            {
                body.Append("Authorization: Bearer ").Append(token).Append("\r\n");
            }
            else if (authorization.Key is { } key)
            {
                body.Append("Authorization: ").Append(SharedKeySigner.SubRequestAuthorization("DELETE", path, date, authorization.Account!, key)).Append("\r\n");
            }

            body.Append("Content-Length: 0\r\n\r\n");
        }

        body.Append("--").Append(s.Boundary).Append("--\r\n");
        HttpContent content = new SequenceContent(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(body.ToString())));
        return await BuildAsync(
            HttpMethod.Post,
            null,
            "restype=container&comp=batch",
            content,
            [("Content-Type", $"multipart/mixed; boundary={s.Boundary}")],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> ConsumeBatchAsync(HttpResponseMessage response, BatchRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw await FailureAsync(response, "BlobBatch", Container, null, cancellationToken).ConfigureAwait(false);
        }

        string? type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out HeaderStringValues values) ? values.ToString() : null;
        int at = type?.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (type is null || at < 0)
        {
            throw new ObjectStoreException("Blob Batch was answered without the boundary of its parts.");
        }

        string boundary = type[(at + 9)..].Trim('"', ' ');
        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 16 << 20, cancellationToken).ConfigureAwait(false);
        try
        {
            return AzureAnswers.ParseBatch(Encoding.UTF8.GetString(bytes, 0, length), boundary, s.Keys);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>A block's size: the option's, larger when the object would need more than 50 000 blocks, rounded up to a mebibyte.</summary>
    internal int BlockSizeFor(long length)
    {
        long size = Math.Max(_options.BlockSize, 1);
        long needed = length > 0 ? (length + MaxBlocks - 1) / MaxBlocks : 0;
        if (needed > size)
        {
            size = (needed + (1 << 20) - 1) >> 20 << 20;
        }

        return (int)Math.Min(size, 4_000L << 20);
    }

    private static void AddCondition(List<(string, string)> headers, PutCondition condition)
    {
        if (condition.Kind == PutConditionKind.IfAbsent)
        {
            headers.Add(("If-None-Match", "*"));
        }
        else if (condition.Kind == PutConditionKind.IfMatch)
        {
            headers.Add(("If-Match", condition.Token!));
        }
    }

    /// <summary><c>x-ms-content-crc64</c>: the CRC-64's eight bytes, low byte first, in base64.</summary>
    private static string Crc64Header(ulong crc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }

    private static string NewPutId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private readonly record struct WriteRequest(AzureBlobObjectStore Store, string Key, string Query, ReadOnlySequence<byte> Body, (string, string)[] Headers, PutCondition Condition);

    private readonly record struct BatchRequest(AzureBlobObjectStore Store, string[] Keys, string Boundary);
}
