using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Azure;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage;

/// <summary>The write path: <c>Put Blob</c>, blocks and their list, and deletes in batches.</summary>
/// <remarks>
/// <para>
/// A put goes through <see cref="Upload"/>, as every store's does: up to the part threshold one
/// <c>Put Blob</c>, and past it blocks sent in parallel, then their list committed with the condition.
/// Each body carries <c>x-ms-content-crc64</c>, which the service checks. A block never committed is
/// dropped by the service a week after the last one was sent, so a put that fails leaves nothing to
/// clean.
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

    /// <summary>The size of block <paramref name="number"/> of a blob of <paramref name="length"/> bytes, -1 when unknown.</summary>
    internal int PartSizeFor(long length, int number = 1) => _limits.PartSizeOf(number, length);

    /// <summary>The header that carries a body's checksum, as the options choose it, or null for none.</summary>
    private (string Name, string Value)? ChecksumOf(ReadOnlySequence<byte> body) => _options.Checksum switch
    {
        PutChecksum.None => null,
        PutChecksum.ContentMd5 => ("Content-MD5", Convert.ToBase64String(Checksums.Md5(body))),
        _ => ("x-ms-content-crc64", Crc64Header(Checksums.Crc64Nvme(body))),
    };

    /// <inheritdoc/>
    string IUploadService.PutOperation => "PutBlob";

    /// <inheritdoc/>
    ValueTask<PutOutcome> IUploadService.PutWholeAsync(string key, ReadOnlySequence<byte> body, PutCondition condition, string? putId, CancellationToken cancellationToken)
    {
        List<(string, string)> headers = [("x-ms-blob-type", "BlockBlob")];
        if (ChecksumOf(body) is { } checksum)
        {
            headers.Add(checksum);
        }

        if (putId is not null)
        {
            headers.Add((PutIdHeader, putId));
        }

        AddCondition(headers, condition);
        return _transport.SendAsync(
            "PutBlob",
            new WriteRequest(this, key, string.Empty, body, [.. headers], condition),
            static (s, ct) => s.Store.BuildWriteAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeWriteAsync(response, s, "PutBlob", ct),
            idempotent: putId is null,
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Azure has no upload to start: the blocks' ids share a random part of the store's making, and the list carries the put id.</remarks>
    ValueTask<UploadState> IUploadService.StartAsync(string key, string putId, CancellationToken cancellationToken) =>
        new ValueTask<UploadState>(new UploadState(key, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), putId));

    /// <inheritdoc/>
    async ValueTask<PartReceipt> IUploadService.SendPartAsync(UploadState upload, int number, ReadOnlySequence<byte> body, CancellationToken cancellationToken)
    {
        string id = BlockId(upload.Id, number);
        (string, string)[] headers = ChecksumOf(body) is { } checksum ? [checksum] : [];
        await _transport.SendAsync(
            "PutBlock",
            new WriteRequest(this, upload.Key, "comp=block&blockid=" + UriEncoding.Query(id), body, headers, PutCondition.None),
            static (s, ct) => s.Store.BuildWriteAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeWriteAsync(response, s, "PutBlock", ct),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        return new PartReceipt(id);
    }

    /// <inheritdoc/>
    /// <remarks>The list of the blocks, in order, committed with the condition and the put id.</remarks>
    ValueTask<PutOutcome> IUploadService.CompleteAsync(UploadState upload, PutCondition condition, CancellationToken cancellationToken)
    {
        byte[] list = ServiceXml.Write(
            upload.Parts,
            static (xml, parts) =>
            {
                xml.WriteStartElement("BlockList");
                foreach (PartReceipt part in parts)
                {
                    xml.WriteElementString("Latest", part.Tag);
                }

                xml.WriteEndElement();
            },
            declared: true);
        List<(string, string)> headers = [(PutIdHeader, upload.PutId)];
        if (ChecksumOf(new ReadOnlySequence<byte>(list)) is { } checksum)
        {
            headers.Add(checksum);
        }

        AddCondition(headers, condition);
        return _transport.SendAsync(
            "PutBlockList",
            new WriteRequest(this, upload.Key, "comp=blocklist", new ReadOnlySequence<byte>(list), [.. headers], condition),
            static (s, ct) => s.Store.BuildWriteAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeWriteAsync(response, s, "PutBlockList", ct),
            idempotent: false,
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Nothing: the service drops a block never committed a week after the last one was sent.</remarks>
    ValueTask IUploadService.AbortAsync(UploadState upload) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    ValueTask<(ObjectHead? Head, string? PutId)> IUploadService.HeadAsync(string key, CancellationToken cancellationToken) =>
        _transport.SendAsync(
            "GetBlobProperties",
            new KeyRequest(this, key),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, string.Empty, null, [], ct),
            static (response, s, ct) => s.Store.ConsumeHeadAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken);

    /// <summary>What the service takes of a put: blocks of the option's size, up to 50 000 of 4 000 MiB, and a condition on their list.</summary>
    private static UploadLimits LimitsOf(AzureBlobStoreOptions options) => new UploadLimits
    {
        PartThreshold = options.PartThreshold,
        PartSize = Math.Max(options.PartSize, 1),
        MaxParts = MaxBlocks,
        MaxPartBytes = 4_000L << 20,
        PartsInFlight = options.PartsInFlight,
        MaxSinglePut = MaxSinglePut,
        ConditionalParts = true,
        GrowingParts = true,
        MaxHeldBytes = options.Transport.MaxHeldBytes,
        SpoolDirectory = options.Transport.SpoolDirectory,
    };

    /// <summary>The id of block <paramref name="number"/> of an upload, all of one length as Azure asks.</summary>
    private static string BlockId(string upload, int number) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{upload}{number - 1:D6}")));

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
        AzureAuthorization authorization = await _authorizer.GetAsync(cancellationToken).ConfigureAwait(false);
        string date = _clock.Now.ToString("r", CultureInfo.InvariantCulture);
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
        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return AzureAnswers.ParseBatch(Encoding.UTF8.GetString(bytes, 0, length), boundary, s.Keys);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
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

    private readonly record struct WriteRequest(AzureBlobObjectStore Store, string Key, string Query, ReadOnlySequence<byte> Body, (string, string)[] Headers, PutCondition Condition);

    private readonly record struct BatchRequest(AzureBlobObjectStore Store, string[] Keys, string Boundary);
}
