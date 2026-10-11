using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.IO;

/// <summary>
/// A store that builds the object under a key on its service, from the object already there and the
/// bytes that follow it, so that the old bytes do not pass through the client again: what a file
/// appended to at a location needs, since an object cannot grow.
/// </summary>
internal interface IAppendingObjectStore
{
    /// <summary>
    /// Replaces the object under <paramref name="key"/>, of <paramref name="length"/> bytes and token
    /// <paramref name="token"/>, with its bytes followed by <paramref name="content"/>, only while it
    /// still has that token.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="token">The token the object was read with.</param>
    /// <param name="length">The object's length when it was read: where the new bytes start.</param>
    /// <param name="content">The new bytes, which the store completes whatever the outcome.</param>
    /// <param name="cancellationToken">Cancels the append, leaving the object as it was.</param>
    /// <returns>
    /// <see cref="PutOutcome.Created"/> when the object grew, <see cref="PutOutcome.Changed"/> when it
    /// no longer has the token or is gone, and nothing was written.
    /// </returns>
    /// <exception cref="ObjectStoreException">The store failed, or cannot tell whether the append landed.</exception>
    ValueTask<PutOutcome> AppendAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken);
}

/// <summary>Appends to an object: on the service when its store can, by reading the old bytes back and sending them again when not.</summary>
internal static class ObjectAppends
{
    /// <summary>The old bytes read back at once, in a request each, by an append that sends them again.</summary>
    internal const int ResendReadBytes = 8 << 20;

    /// <summary>
    /// Appends <paramref name="content"/> to the object under <paramref name="key"/>, through its
    /// store's own append, or by <see cref="ResendAsync"/>.
    /// </summary>
    internal static ValueTask<PutOutcome> AppendAsync(
        IObjectStore store, string key, string token, long length, PipeReader content, CancellationToken cancellationToken) =>
        store is IAppendingObjectStore appending
            ? appending.AppendAsync(key, token, length, content, cancellationToken)
            : ResendAsync(store, key, token, length, content, cancellationToken);

    /// <summary>
    /// The append of a store that copies nothing on its service: the old bytes read back, every read
    /// bound to their token, and put again ahead of the new ones, the put conditioned on the token.
    /// The bytes pass through the client twice, and nothing else is needed of the store.
    /// </summary>
    /// <exception cref="NotSupportedException">The store cannot replace an object only while it keeps a token.</exception>
    internal static async ValueTask<PutOutcome> ResendAsync(
        IObjectStore store, string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((store.Capabilities & ObjectStoreCapabilities.ReplacesOnTokenMatch) == 0)
        {
            NotSupportedException refused = new NotSupportedException(
                $"The store cannot replace '{key}' only while nobody else did, so a file there cannot be appended to: write it again instead.");
            await content.CompleteAsync(refused).ConfigureAwait(false);
            throw refused;
        }

        Pipe joined = new Pipe(new PipeOptions(
            pauseWriterThreshold: 2 * ResendReadBytes, resumeWriterThreshold: ResendReadBytes, useSynchronizationContext: false));
        StrongBox<bool> changed = new StrongBox<bool>();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task feeding = FeedAsync(store, key, token, length, content, joined.Writer, changed, stop.Token);
        PutOutcome outcome;
        try
        {
            outcome = await store.PutAsync(key, joined.Reader, -1, PutCondition.IfMatch(token), cancellationToken).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref changed.Value))
        {
            // The old bytes changed under the read: nothing was written, and the store's own failure,
            // the content it was given ending in error, says less than that.
            await SettleAsync(feeding).ConfigureAwait(false);
            return PutOutcome.Changed;
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await SettleAsync(feeding).ConfigureAwait(false);
            throw;
        }

        await SettleAsync(feeding).ConfigureAwait(false);
        return Volatile.Read(ref changed.Value) ? PutOutcome.Changed : outcome;
    }

    /// <summary>
    /// Writes the old bytes, then the new, into the put's content; completes both pipes, with the
    /// failure when there is one. A read that finds the object changed or gone marks it.
    /// </summary>
    private static async Task FeedAsync(
        IObjectStore store, string key, string token, long length, PipeReader content, PipeWriter joined, StrongBox<bool> changed, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            for (long at = 0; at < length;)
            {
                int wanted = (int)Math.Min(ResendReadBytes, length - at);
                Memory<byte> destination = joined.GetMemory(wanted)[..wanted];
                ObjectRead read = await store.ReadAsync(key, at, destination, token, cancellationToken).ConfigureAwait(false);
                if (read.BytesRead == 0 || read.ObjectLength != length)
                {
                    Volatile.Write(ref changed.Value, true);
                    throw new ObjectStoreException($"'{key}' is {read.ObjectLength} bytes now, where its append read {length}.");
                }

                joined.Advance(read.BytesRead);
                at += read.BytesRead;
                if ((await joined.FlushAsync(cancellationToken).ConfigureAwait(false)).IsCompleted)
                {
                    failure = new ObjectStoreException($"The put of '{key}' stopped reading its append.");
                    return;
                }
            }

            while (true)
            {
                ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    joined.Write(segment.Span);
                }

                content.AdvanceTo(result.Buffer.End);
                if ((await joined.FlushAsync(cancellationToken).ConfigureAwait(false)).IsCompleted && !result.IsCompleted)
                {
                    failure = new ObjectStoreException($"The put of '{key}' stopped reading its append.");
                    return;
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception caught)
        {
            if (caught is VortexFormatException or ObjectNotFoundException)
            {
                // The object's token is not the one the append read, or the object is gone.
                Volatile.Write(ref changed.Value, true);
            }

            failure = caught;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
            await joined.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private static async Task SettleAsync(Task feeding)
    {
        try
        {
            // Started by the append beside its put: awaiting it here is the point.
#pragma warning disable VSTHRD003
            await feeding.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // FeedAsync catches its own failures and completes its pipes with them; nothing is left.
        }
    }
}
