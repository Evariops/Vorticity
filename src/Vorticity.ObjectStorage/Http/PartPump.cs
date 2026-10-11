using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// What every store's upload in parts does the same way: the content read a part at a time, each part
/// sent as soon as it fills while at most a few are in flight, and the length checked at the end. What
/// a part's request is, a store's own delegate says.
/// </summary>
internal static class PartPump
{
    /// <summary>
    /// Reads content of unknown length a part at a time up to <paramref name="threshold"/>: the whole of
    /// it when it ends there, which goes in one request, or the parts read so far, which begin an upload.
    /// </summary>
    /// <returns>The whole content, or null and the parts read.</returns>
    internal static async ValueTask<(ChunkedBuffer? Whole, Queue<ChunkedBuffer> Carried)> HeadAsync(
        PipeReader content, int partSize, long threshold, CancellationToken cancellationToken)
    {
        Queue<ChunkedBuffer> carried = new Queue<ChunkedBuffer>();
        try
        {
            long held = 0;
            while (true)
            {
                (ChunkedBuffer part, bool ended) = await ChunkedBuffer.FillAsync(content, partSize, cancellationToken).ConfigureAwait(false);
                held += part.Length;
                carried.Enqueue(part);
                if (ended && held <= threshold)
                {
                    ChunkedBuffer all = carried.Dequeue();
                    while (carried.TryDequeue(out ChunkedBuffer? next))
                    {
                        all.Absorb(next);
                    }

                    return (all, carried);
                }

                if (ended || held + partSize > threshold)
                {
                    return (null, carried);
                }
            }
        }
        catch
        {
            Release(carried);
            throw;
        }
    }

    /// <summary>
    /// Sends the parts: those <paramref name="carried"/> first, then the content's a part at a time,
    /// each by <paramref name="send"/> as soon as it fills, at most <paramref name="inFlight"/> at once.
    /// The first part that fails stops the reading and is the failure that propagates.
    /// </summary>
    /// <param name="key">The object's key, for the messages.</param>
    /// <param name="content">The content past the parts carried.</param>
    /// <param name="length">The length announced, or -1.</param>
    /// <param name="partSize">The size of every part but the last.</param>
    /// <param name="maxParts">The most parts the service takes.</param>
    /// <param name="inFlight">The most parts in flight at once.</param>
    /// <param name="carried">The parts read before the upload began, each disposed once sent.</param>
    /// <param name="send">Sends a part, by its number from <paramref name="firstPart"/>; the part is disposed once it returns.</param>
    /// <param name="cancellationToken">Cancels the reading and the parts.</param>
    /// <param name="firstPart">The number of the first part: 1, or the one after the parts an append copied on the service.</param>
    /// <returns>The length of every part, in order.</returns>
    internal static async ValueTask<List<long>> SendAsync(
        string key,
        PipeReader content,
        long length,
        int partSize,
        int maxParts,
        int inFlight,
        Queue<ChunkedBuffer> carried,
        Func<int, ChunkedBuffer, CancellationToken, Task> send,
        CancellationToken cancellationToken,
        int firstPart = 1)
    {
        List<long> parts = [];
        List<Task> sending = [];
        using SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, inFlight));
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long total = 0;
        try
        {
            bool ended = false;
            while (true)
            {
                if (!carried.TryDequeue(out ChunkedBuffer? part))
                {
                    if (ended)
                    {
                        break;
                    }

                    (part, ended) = await ChunkedBuffer.FillAsync(content, partSize, failed.Token).ConfigureAwait(false);
                    if (part.Length == 0)
                    {
                        part.Dispose();
                        break;
                    }
                }

                if (parts.Count == maxParts)
                {
                    part.Dispose();
                    throw new ObjectStoreException($"'{key}' needs more than the {maxParts} parts the service takes at {partSize} bytes a part: give its length.");
                }

                total += part.Length;
                if (length >= 0 && total > length)
                {
                    part.Dispose();
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content runs past them.");
                }

                parts.Add(part.Length);
                try
                {
                    await slots.WaitAsync(failed.Token).ConfigureAwait(false);
                }
                catch
                {
                    part.Dispose();
                    throw;
                }

                sending.Add(SendOneAsync(firstPart - 1 + parts.Count, part, send, slots, failed));
                sending.RemoveAll(task => task.IsCompletedSuccessfully);
            }

            await Task.WhenAll(sending).ConfigureAwait(false);
            if (length >= 0 && total != length)
            {
                throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {total}.");
            }

            return parts;
        }
        catch (Exception caught)
        {
            await failed.CancelAsync().ConfigureAwait(false);
            Exception? partFailed = await SettleAsync(sending).ConfigureAwait(false);
            Release(carried);

            // A part that failed cancels the reading of the next: its own failure is the one to say.
            if (caught is OperationCanceledException && !cancellationToken.IsCancellationRequested && partFailed is not null)
            {
                ExceptionDispatchInfo.Throw(partFailed);
            }

            throw;
        }
    }

    /// <summary>Disposes the parts left.</summary>
    internal static void Release(Queue<ChunkedBuffer> parts)
    {
        while (parts.TryDequeue(out ChunkedBuffer? part))
        {
            part.Dispose();
        }
    }

    private static async Task SendOneAsync(int number, ChunkedBuffer part, Func<int, ChunkedBuffer, CancellationToken, Task> send, SemaphoreSlim slots, CancellationTokenSource failed)
    {
        try
        {
            await send(number, part, failed.Token).ConfigureAwait(false);
        }
        catch
        {
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            part.Dispose();
            slots.Release();
        }
    }

    /// <summary>Awaits every task, and returns the first failure that is not a cancellation, the one that started the others'.</summary>
    private static async Task<Exception?> SettleAsync(List<Task> tasks)
    {
        Exception? first = null;
        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                first ??= failure;
            }
            catch (OperationCanceledException)
            {
            }
        }

        return first;
    }
}
