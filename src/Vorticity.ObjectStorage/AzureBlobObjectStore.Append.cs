using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Azure;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage;

/// <summary>
/// Appends: the old blob copied into the first blocks of the grown one by <c>Put Block From URL</c>, on
/// the service, the new bytes put as the blocks after them, and the list committed only while the blob
/// keeps its ETag.
/// </summary>
/// <remarks>
/// The copy's source is the blob itself, authorized as the store is: a shared access signature, the
/// bearer token in <c>x-ms-copy-source-authorization</c>, or, with an account key, a read-only service
/// SAS on that blob alone, valid for an hour. A blob smaller than <see cref="MinimumCopiedBytes"/> is
/// read back and sent again with the new bytes, in fewer requests.
/// </remarks>
public sealed partial class AzureBlobObjectStore : IAppendingObjectStore
{
    /// <summary>The smallest blob an append copies on the service rather than reads back.</summary>
    internal const long MinimumCopiedBytes = 5L << 20;

    /// <summary>
    /// The most one copied block holds: 100 MiB, which every version of the service copies in one
    /// request, and quickly enough for the request's deadline.
    /// </summary>
    internal const long MaxCopiedBlockBytes = 100L << 20;

    /// <inheritdoc/>
    async ValueTask<PutOutcome> IAppendingObjectStore.AppendAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (length < _options.MinimumCopiedBytes)
        {
            return await ObjectAppends.ResendAsync(this, key, token, length, content, cancellationToken).ConfigureAwait(false);
        }

        Exception? failure = null;
        try
        {
            CheckKey(key);
            ThrowIfDisposed();
            return await AppendByCopyAsync(key, token, length, content, cancellationToken).ConfigureAwait(false);
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
    /// The blocks of the grown blob: the old blob's copied while the new ones are put, then their list
    /// committed with <c>If-Match</c>. Blocks left uncommitted by a failure are dropped by the service.
    /// </summary>
    private async ValueTask<PutOutcome> AppendByCopyAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        int copies = (int)((length + MaxCopiedBlockBytes - 1) / MaxCopiedBlockBytes);
        long copySize = (length + copies - 1) / copies;
        if (copies >= MaxBlocks)
        {
            throw new ObjectStoreException($"'{key}' is {length} bytes, past the {MaxBlocks} blocks of {MaxCopiedBlockBytes} bytes an append copies it in.");
        }

        string upload = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> copying = CopyBlocksAsync(key, upload, token, length, copies, copySize, stop);
        List<long>? blocks = null;
        Exception? sendFailure = null;
        try
        {
            blocks = await PartPump.SendAsync(
                key,
                content,
                -1,
                BlockSizeFor(-1),
                MaxBlocks - copies,
                _options.BlocksInFlight,
                new Queue<ChunkedBuffer>(),
                (number, block, ct) => PutBlockAsync(key, BlockId(upload, number), block, ct),
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

        if (!copied)
        {
            return PutOutcome.Changed;
        }

        if (sendFailure is not null)
        {
            ExceptionDispatchInfo.Throw(sendFailure);
        }

        return await CommitBlocksAsync(key, upload, copies + blocks!.Count, PutCondition.IfMatch(token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the old blob into the first <paramref name="copies"/> blocks, each bound to its ETag, at
    /// most the blocks in flight at once. False when the blob changed or is gone, which stops the append.
    /// </summary>
    private async Task<bool> CopyBlocksAsync(string key, string upload, string token, long length, int copies, long copySize, CancellationTokenSource stop)
    {
        using SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, _options.BlocksInFlight));
        List<Task<bool>> sent = new List<Task<bool>>(copies);
        bool all = true;
        try
        {
            for (int i = 0; i < copies; i++)
            {
                long first = i * copySize;
                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                sent.Add(CopyBlockAsync(new CopyRequest(this, key, BlockId(upload, i + 1), token, first, Math.Min(copySize, length - first)), slots, stop));
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
                    // Stopped by another block, whose own outcome is the one to say.
                }
            }
        }

        return all;
    }

    private async Task<bool> CopyBlockAsync(CopyRequest request, SemaphoreSlim slots, CancellationTokenSource stop)
    {
        try
        {
            bool copied = await _transport.SendAsync(
                "PutBlockFromURL",
                request,
                static (s, ct) => s.Store.BuildCopyAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeCopyAsync(response, s, ct),
                idempotent: true,
                stop.Token).ConfigureAwait(false);
            if (!copied)
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }

            return copied;
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
        AzureAuthorization authorization = await _options.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        string source = _base + _containerPath + "/" + UriEncoding.Path(s.Key);
        List<(string, string)> headers =
        [
            ("x-ms-source-range", string.Create(CultureInfo.InvariantCulture, $"bytes={s.First}-{s.First + s.Length - 1}")),
            ("x-ms-source-if-match", s.Token),
        ];
        if (authorization.Sas is { } sas)
        {
            source += "?" + sas;
        }
        else if (authorization.Key is { } accountKey)
        {
            source += "?" + SharedKeySigner.BlobReadSas(authorization.Account!, Container, s.Key, accountKey, Now() + TimeSpan.FromHours(1));
        }
        else if (authorization.Bearer is { } bearer)
        {
            headers.Add(("x-ms-copy-source-authorization", "Bearer " + bearer));
        }

        headers.Add(("x-ms-copy-source", source));
        return await BuildAsync(HttpMethod.Put, s.Key, "comp=block&blockid=" + UriEncoding.Query(s.Id), null, [.. headers], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the block was copied; false when the old blob changed or is gone.</summary>
    private async ValueTask<bool> ConsumeCopyAsync(HttpResponseMessage response, CopyRequest s, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
                return true;
            case HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound:
                return false;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        throw MapFailure(response, error, "PutBlockFromURL", s.Key);
    }

    /// <summary>A block copied from the old blob: its id, and the range of the blob it holds.</summary>
    private readonly record struct CopyRequest(AzureBlobObjectStore Store, string Key, string Id, string Token, long First, long Length);
}
