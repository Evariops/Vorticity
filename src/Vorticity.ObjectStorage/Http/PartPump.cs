using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// What every store's upload in parts does the same way: the parts taken from their source one at a
/// time, each sent as soon as it is there while at most a few are in flight, and the length checked at
/// the end. What a part's request is, the store's own delegate says.
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
    /// Sends the parts of <paramref name="source"/>, each by <paramref name="send"/> as soon as it is
    /// there, at most <paramref name="inFlight"/> at once. The first part that fails stops the others,
    /// and is the failure that propagates.
    /// </summary>
    /// <param name="key">The object's key, for the messages.</param>
    /// <param name="source">Where the parts come from.</param>
    /// <param name="length">The length announced, or -1.</param>
    /// <param name="sizeOf">The size of a part, by its number from 1: every part but the last has it.</param>
    /// <param name="maxParts">The most parts the service takes.</param>
    /// <param name="inFlight">The most parts in flight at once.</param>
    /// <param name="send">Sends a part, by its number from 1.</param>
    /// <param name="produced">
    /// Sees each part in order, before it is sent: what keeps a checksum of the whole object, which the
    /// parts sent in parallel could not give.
    /// </param>
    /// <param name="cancellationToken">Cancels the reading and the parts.</param>
    /// <returns>How many parts went.</returns>
    internal static async ValueTask<int> SendAsync(
        string key,
        PartSource source,
        long length,
        Func<int, int> sizeOf,
        int maxParts,
        int inFlight,
        Func<int, ReadOnlySequence<byte>, CancellationToken, Task> send,
        Action<ReadOnlySequence<byte>>? produced,
        CancellationToken cancellationToken)
    {
        int count = 0;
        List<Task> sending = [];
        using SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, inFlight));
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long total = 0;
        try
        {
            while (true)
            {
                SourcePart part = await source.NextAsync(sizeOf(count + 1), failed.Token).ConfigureAwait(false);
                if (part.Bytes.IsEmpty)
                {
                    part.Owner?.Dispose();
                    break;
                }

                if (count == maxParts)
                {
                    part.Owner?.Dispose();
                    throw new ObjectStoreException($"'{key}' needs more than the {maxParts} parts the service takes: give its length.");
                }

                total += part.Bytes.Length;
                if (length >= 0 && total > length)
                {
                    part.Owner?.Dispose();
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content runs past them.");
                }

                count++;
                try
                {
                    produced?.Invoke(part.Bytes);
                    await slots.WaitAsync(failed.Token).ConfigureAwait(false);
                }
                catch
                {
                    part.Owner?.Dispose();
                    throw;
                }

                sending.Add(SendOneAsync(count, part, send, slots, failed));
                sending.RemoveAll(task => task.IsCompletedSuccessfully);
                if (part.Ended)
                {
                    break;
                }
            }

            await Task.WhenAll(sending).ConfigureAwait(false);
            if (length >= 0 && total != length)
            {
                throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {total}.");
            }

            return count;
        }
        catch (Exception caught)
        {
            await failed.CancelAsync().ConfigureAwait(false);
            Exception? partFailed = await SettleAsync(sending).ConfigureAwait(false);
            source.Release();

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

    private static async Task SendOneAsync(int number, SourcePart part, Func<int, ReadOnlySequence<byte>, CancellationToken, Task> send, SemaphoreSlim slots, CancellationTokenSource failed)
    {
        try
        {
            await send(number, part.Bytes, failed.Token).ConfigureAwait(false);
        }
        catch
        {
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            part.Owner?.Dispose();
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

/// <summary>
/// A part: its bytes, what holds them until the part is sent, none for bytes the caller holds, and
/// whether the content ended with it.
/// </summary>
internal readonly record struct SourcePart(ReadOnlySequence<byte> Bytes, IDisposable? Owner, bool Ended);

/// <summary>Where the parts of an upload come from: bytes already held, or a stream read a part at a time.</summary>
internal abstract class PartSource
{
    /// <summary>The next part, of <paramref name="size"/> bytes or what is left; empty at the end.</summary>
    internal abstract ValueTask<SourcePart> NextAsync(int size, CancellationToken cancellationToken);

    /// <summary>Releases what the source read and did not hand out.</summary>
    internal virtual void Release()
    {
    }

    /// <summary>The same parts again from the first, for an upload made anew; null for a stream, read once.</summary>
    internal virtual PartSource? Replay() => null;

    /// <summary>Parts that are slices of <paramref name="bytes"/>, sent where they lie and sent again from there.</summary>
    internal static PartSource Of(ReadOnlySequence<byte> bytes) => new Held(bytes);

    /// <summary>Parts read from <paramref name="content"/>, those <paramref name="carried"/> from before the upload began first.</summary>
    internal static PartSource Of(PipeReader content, Queue<ChunkedBuffer> carried) => new Streamed(content, carried);

    private sealed class Held(ReadOnlySequence<byte> bytes) : PartSource
    {
        private long _offset;

        internal override ValueTask<SourcePart> NextAsync(int size, CancellationToken cancellationToken)
        {
            long take = Math.Min(size, bytes.Length - _offset);
            ReadOnlySequence<byte> part = bytes.Slice(_offset, take);
            _offset += take;
            return new ValueTask<SourcePart>(new SourcePart(part, null, _offset == bytes.Length));
        }

        internal override PartSource Replay() => new Held(bytes);
    }

    private sealed class Streamed(PipeReader content, Queue<ChunkedBuffer> carried) : PartSource
    {
        internal override async ValueTask<SourcePart> NextAsync(int size, CancellationToken cancellationToken)
        {
            if (carried.TryDequeue(out ChunkedBuffer? read))
            {
                return new SourcePart(read.Sequence(), read, Ended: false);
            }

            (ChunkedBuffer part, bool ended) = await ChunkedBuffer.FillAsync(content, size, cancellationToken).ConfigureAwait(false);
            return new SourcePart(part.Sequence(), part, ended);
        }

        internal override void Release() => PartPump.Release(carried);
    }
}
