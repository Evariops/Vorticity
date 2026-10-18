using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The requests of one service's put, which <see cref="Upload"/> orders the same way for every store:
/// the whole object in one request, or an upload in parts started, sent and completed.
/// </summary>
internal interface IUploadService
{
    /// <summary>The service's name for a put of the whole object, in messages.</summary>
    string PutOperation { get; }

    /// <summary>
    /// Sends <paramref name="body"/> whole under <paramref name="condition"/>, carrying
    /// <paramref name="putId"/> in the object's metadata when it is not null. The transport sends it
    /// again while it may.
    /// </summary>
    /// <exception cref="StoreAnswerLostException">The answer was lost after the request may have been applied.</exception>
    ValueTask<PutOutcome> PutWholeAsync(string key, ReadOnlySequence<byte> body, PutCondition condition, string? putId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts an upload in parts whose object carries <paramref name="putId"/>, the request that starts
    /// it, or nothing on a service that needs none.
    /// </summary>
    ValueTask<UploadState> StartAsync(string key, string putId, CancellationToken cancellationToken);

    /// <summary>Sends part <paramref name="number"/> of <paramref name="upload"/>, and says what the completion names it by.</summary>
    ValueTask<PartReceipt> SendPartAsync(UploadState upload, int number, ReadOnlySequence<byte> body, CancellationToken cancellationToken);

    /// <summary>Completes <paramref name="upload"/> from its parts, in order, under <paramref name="condition"/>.</summary>
    /// <exception cref="StoreAnswerLostException">The answer was lost after the request may have been applied.</exception>
    ValueTask<PutOutcome> CompleteAsync(UploadState upload, PutCondition condition, CancellationToken cancellationToken);

    /// <summary>Gives <paramref name="upload"/> up, under the transport's abort deadline; nothing on a service whose parts expire.</summary>
    ValueTask AbortAsync(UploadState upload);

    /// <summary>The object's head and the put id its metadata carries, or nulls when no object has the key.</summary>
    /// <exception cref="ObjectStoreException">The head failed.</exception>
    ValueTask<(ObjectHead? Head, string? PutId)> HeadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>What a service takes of a put, which the upload sizes its requests by.</summary>
internal sealed record UploadLimits
{
    /// <summary>The size past which a put goes up in parts unless a store's options say otherwise, the same on every store.</summary>
    internal const long DefaultPartThreshold = 32L << 20;

    /// <summary>The size of a part unless a store's options say otherwise.</summary>
    internal const int DefaultPartSize = 16 << 20;

    /// <summary>The parts of one upload in flight at once unless a store's options say otherwise.</summary>
    internal const int DefaultPartsInFlight = 4;

    /// <summary>The size past which a put goes up in parts.</summary>
    internal required long PartThreshold { get; init; }

    /// <summary>The size of a part, before an unknown length grows it or a known one needs it larger.</summary>
    internal required int PartSize { get; init; }

    /// <summary>The most parts an upload may have.</summary>
    internal required int MaxParts { get; init; }

    /// <summary>The most bytes a part may hold.</summary>
    internal required long MaxPartBytes { get; init; }

    /// <summary>The parts of one upload in flight at once.</summary>
    internal required int PartsInFlight { get; init; }

    /// <summary>The most one request puts.</summary>
    internal required long MaxSinglePut { get; init; }

    /// <summary>
    /// Whether a condition rides an upload in parts, on its completion. R2 and Cloud Storage take
    /// none, so a conditional put to them goes whole.
    /// </summary>
    internal required bool ConditionalParts { get; init; }

    /// <summary>
    /// Whether the parts of an object of unknown length grow with their number, so that a few
    /// thousand parts reach a large object. R2 wants every part but the last of one size.
    /// </summary>
    internal required bool GrowingParts { get; init; }

    /// <summary>The most a put read from a stream holds in memory to send it whole.</summary>
    internal required long MaxHeldBytes { get; init; }

    /// <summary>
    /// Whether parts sized by a bound of <paramref name="bound"/> bytes reach it: the most parts the
    /// service takes, each as large as it takes.
    /// </summary>
    internal bool Reaches(long bound) =>
        bound <= (long)MaxParts * Math.Min(MaxPartBytes, int.MaxValue & ~((1 << 20) - 1));

    /// <summary>Where a put read from a stream is held whole, on disk, before it is sent; null to send as it is read.</summary>
    internal required string? SpoolDirectory { get; init; }

    /// <summary>
    /// The size of part <paramref name="number"/> of an object of <paramref name="length"/> bytes, -1
    /// when unknown: <see cref="PartSize"/>, larger for a known object that would need more parts than
    /// the service takes, rounded up to a mebibyte, and for an unknown one doubled every tenth of the
    /// parts the service takes, up to eight times, when <see cref="GrowingParts"/>.
    /// </summary>
    internal int PartSizeOf(int number, long length)
    {
        long size = PartSize;
        if (length >= 0)
        {
            long needed = (length + MaxParts - 1) / MaxParts;
            if (needed > size)
            {
                size = (needed + (1 << 20) - 1) >> 20 << 20;
            }
        }
        else if (GrowingParts)
        {
            size <<= Math.Min(3, (number - 1) / Math.Max(1, MaxParts / 10));
        }

        return (int)Math.Min(size, Math.Min(MaxPartBytes, int.MaxValue & ~((1 << 20) - 1)));
    }
}

/// <summary>An upload in parts under way: its object, its id, its put id, and the receipts of its parts.</summary>
internal class UploadState(string key, string id, string putId)
{
    private readonly List<PartReceipt> _parts = [];

    /// <summary>The object's key.</summary>
    internal string Key => key;

    /// <summary>The upload's id at the service, or the one the store made where the service has none.</summary>
    internal string Id => id;

    /// <summary>The put id the object carries once complete.</summary>
    internal string PutId => putId;

    /// <summary>The receipts of the parts, in their order.</summary>
    internal IReadOnlyList<PartReceipt> Parts
    {
        get
        {
            lock (_parts)
            {
                return [.. _parts];
            }
        }
    }

    /// <summary>Sees each part in order, before it is sent: what a checksum of the whole object takes.</summary>
    internal virtual void Produced(ReadOnlySequence<byte> part)
    {
    }

    /// <summary>Records the receipt of part <paramref name="number"/>, from 1, whatever order the parts end in.</summary>
    internal void Received(int number, PartReceipt receipt)
    {
        lock (_parts)
        {
            while (_parts.Count < number)
            {
                _parts.Add(default);
            }

            _parts[number - 1] = receipt;
        }
    }
}

/// <summary>What the completion names a part by: its tag (an ETag, a block id) and, where the service checks it, its CRC.</summary>
internal readonly record struct PartReceipt(string Tag, ulong Crc = 0);

/// <summary>
/// The put every store makes the same way: whole up to the part threshold, in parts past it, sent from
/// bytes already in memory where they lie, from a stream as it is read, or from a spool on disk, and
/// settled by the object's head and the put's id when an answer is lost.
/// </summary>
internal static class Upload
{
    /// <summary>A put of bytes already in memory: sent where they lie, whole or as slices, and sent again from there.</summary>
    internal static ValueTask<PutOutcome> PutAsync(
        IUploadService service, HttpTransport transport, UploadLimits limits, string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken)
    {
        bool inParts = condition.Kind == PutConditionKind.None || limits.ConditionalParts;
        long length = content.Length;
        if (length <= Math.Min(limits.PartThreshold, limits.MaxSinglePut) || (!inParts && length <= limits.MaxSinglePut))
        {
            return WholeAsync(service, transport, key, content, condition, cancellationToken);
        }

        if (!inParts)
        {
            throw new ObjectStoreException($"'{key}' is {length} bytes, past the {limits.MaxSinglePut} a conditional {service.PutOperation} sends in one request.");
        }

        return PartsAsync(service, transport, limits, key, PartSource.Of(content), length, -1, condition, cancellationToken);
    }

    /// <summary>
    /// A put read from a stream: spooled whole to disk first when the store has a spool directory, and
    /// sent from there; otherwise held whole up to the part threshold, or, when the service takes the
    /// condition only on a whole put, up to <see cref="UploadLimits.MaxHeldBytes"/>, and sent in parts as
    /// they fill past it.
    /// </summary>
    internal static ValueTask<PutOutcome> PutAsync(
        IUploadService service, HttpTransport transport, UploadLimits limits, string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken) =>
        PutAsync(service, transport, limits, key, content, length, -1, condition, cancellationToken);

    /// <summary>
    /// A put read from a stream whose length is unknown but at most <paramref name="bound"/>, or -1 for no
    /// bound: held whole when the bound is under the part threshold, and otherwise sent in parts sized by
    /// the bound, of one size on a service that wants them so, rather than spooled; a bound past what the
    /// service's parts reach goes to the spool, or is refused without one. Without a bound, as
    /// <see cref="PutAsync(IUploadService, HttpTransport, UploadLimits, string, PipeReader, long, PutCondition, CancellationToken)"/>.
    /// </summary>
    internal static async ValueTask<PutOutcome> PutAsync(
        IUploadService service, HttpTransport transport, UploadLimits limits, string key, PipeReader content, long length, long bound, PutCondition condition, CancellationToken cancellationToken)
    {
        bool bounded = length < 0 && bound >= 0;
        bool inParts = condition.Kind == PutConditionKind.None || limits.ConditionalParts;
        if (limits.SpoolDirectory is { } directory && !(bounded && inParts && limits.Reaches(bound)))
        {
            using SpoolFile spool = await SpoolFile.FillAsync(directory, content, length, key, cancellationToken).ConfigureAwait(false);
            return await PutAsync(service, transport, limits, key, spool.Bytes, condition, cancellationToken).ConfigureAwait(false);
        }

        if (bounded && inParts)
        {
            if (!limits.Reaches(bound))
            {
                throw new ObjectStoreException(
                    $"'{key}' may take {bound} bytes, past what the {limits.MaxParts} parts the service takes reach: set StoreTransportOptions.SpoolDirectory to hold it on disk first.");
            }

            if (bound <= Math.Min(limits.PartThreshold, limits.MaxSinglePut))
            {
                // Under the threshold whatever it holds: held whole, and sent in one request.
                (ChunkedBuffer body, bool ended) = await ChunkedBuffer.FillAsync(content, bound, cancellationToken).ConfigureAwait(false);
                using (body)
                {
                    if (!ended)
                    {
                        throw PastItsBound(key, bound);
                    }

                    return await WholeAsync(service, transport, key, body.Sequence(), condition, cancellationToken).ConfigureAwait(false);
                }
            }

            return await PartsAsync(service, transport, limits, key, PartSource.Of(content, new Queue<ChunkedBuffer>()), -1, bound, condition, cancellationToken).ConfigureAwait(false);
        }

        long threshold = inParts
            ? Math.Min(limits.PartThreshold, limits.MaxSinglePut)
            : Math.Min(limits.MaxHeldBytes, limits.MaxSinglePut);
        if (!inParts && length > threshold)
        {
            throw TooLargeToHold(service, limits, key, length);
        }

        if (length >= 0 && length <= threshold)
        {
            (ChunkedBuffer body, bool ended) = await ChunkedBuffer.FillAsync(content, length, cancellationToken).ConfigureAwait(false);
            using (body)
            {
                CheckEnd(key, length, body.Length, ended);
                return await WholeAsync(service, transport, key, body.Sequence(), condition, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!inParts)
        {
            // Of unknown length, and bound to go whole: held up to the ceiling, and refused past it.
            (ChunkedBuffer all, bool ended) = await ChunkedBuffer.FillAsync(content, threshold, cancellationToken).ConfigureAwait(false);
            using (all)
            {
                if (!ended)
                {
                    throw TooLargeToHold(service, limits, key, -1);
                }

                return await WholeAsync(service, transport, key, all.Sequence(), condition, cancellationToken).ConfigureAwait(false);
            }
        }

        Queue<ChunkedBuffer> carried = new Queue<ChunkedBuffer>();
        if (length < 0)
        {
            // Unknown: read a part at a time up to the threshold, and sent whole if it ends there.
            (ChunkedBuffer? whole, carried) = await PartPump.HeadAsync(content, limits.PartSizeOf(1, -1), threshold, cancellationToken).ConfigureAwait(false);
            if (whole is not null)
            {
                using (whole)
                {
                    return await WholeAsync(service, transport, key, whole.Sequence(), condition, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return await PartsAsync(service, transport, limits, key, PartSource.Of(content, carried), length, -1, condition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One request, whose outcome is settled by the head when its answer is lost.</summary>
    private static ValueTask<PutOutcome> WholeAsync(
        IUploadService service, HttpTransport transport, string key, ReadOnlySequence<byte> body, PutCondition condition, CancellationToken cancellationToken)
    {
        // A put without a condition goes again blindly, as a retry of the same bytes may: it needs no id.
        string? putId = condition.Kind == PutConditionKind.None ? null : NewPutId();
        return SettleAsync(
            service,
            transport,
            key,
            putId,
            condition,
            service.PutOperation,
            ct => service.PutWholeAsync(key, body, condition, putId, ct),
            cancellationToken);
    }

    /// <summary>
    /// An upload in parts: started, its parts sent in parallel, completed with the condition, its
    /// outcome settled by the head when the completion's answer is lost, and aborted whatever fails, or
    /// whatever refuses it, before it lands. An upload the service asks to make again is made again from
    /// its first part when its content can be sent again, held in memory or spooled on disk.
    /// </summary>
    private static async ValueTask<PutOutcome> PartsAsync(
        IUploadService service, HttpTransport transport, UploadLimits limits, string key, PartSource source, long length, long bound, PutCondition condition, CancellationToken cancellationToken)
    {
        for (int made = 1; ; made++)
        {
            try
            {
                return await PartsOnceAsync(service, transport, limits, key, source, length, bound, condition, cancellationToken).ConfigureAwait(false);
            }
            catch (StoreRestartException again)
            {
                if (source.Replay() is not { } replay)
                {
                    throw new ObjectStoreException(
                        $"{again.Message} The content was read as it was sent, so the put must be made again: set StoreTransportOptions.SpoolDirectory to let the store make it again itself.",
                        again);
                }

                if (!await transport.RetryAfterAsync(made, cancellationToken).ConfigureAwait(false))
                {
                    throw new ObjectStoreException($"{again.Message} The upload was made {made} times.", again);
                }

                source = replay;
            }
        }
    }

    /// <remarks>
    /// The parts are sized by the length when it is known, by the bound when only that is, and grow with
    /// their number when neither is; content past the bound is refused, and the upload given up.
    /// </remarks>
    private static async ValueTask<PutOutcome> PartsOnceAsync(
        IUploadService service, HttpTransport transport, UploadLimits limits, string key, PartSource source, long length, long bound, PutCondition condition, CancellationToken cancellationToken)
    {
        long sizedBy = length >= 0 ? length : bound;
        UploadState upload;
        try
        {
            upload = await service.StartAsync(key, NewPutId(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            source.Release();
            throw;
        }

        try
        {
            await PartPump.SendAsync(
                key,
                source,
                length,
                bound,
                number => limits.PartSizeOf(number, sizedBy),
                limits.MaxParts,
                limits.PartsInFlight,
                async (number, body, ct) => upload.Received(number, await service.SendPartAsync(upload, number, body, ct).ConfigureAwait(false)),
                upload.Produced,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await service.AbortAsync(upload).ConfigureAwait(false);
            throw;
        }

        PutOutcome outcome;
        try
        {
            outcome = await SettleAsync(
                service,
                transport,
                key,
                upload.PutId,
                condition,
                "the completion of an upload",
                ct => service.CompleteAsync(upload, condition, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await service.AbortAsync(upload).ConfigureAwait(false);
            throw;
        }

        // A completion refused on its condition leaves its upload, and its parts, behind.
        if (outcome != PutOutcome.Created)
        {
            await service.AbortAsync(upload).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Sends a write until its outcome is known. A lost answer, after the write may have been applied,
    /// is settled by the object's head: created by this put when it carries its id, taken by another
    /// when it does not, and sent again when nothing is there or the token it was to replace still is.
    /// A refusal on the condition after such a loss is checked against the id too, since the attempt
    /// whose answer was lost may have landed after the head that found nothing, and taken the key itself.
    /// </summary>
    private static async ValueTask<PutOutcome> SettleAsync(
        IUploadService service,
        HttpTransport transport,
        string key,
        string? putId,
        PutCondition condition,
        string operation,
        Func<CancellationToken, ValueTask<PutOutcome>> attempt,
        CancellationToken cancellationToken)
    {
        bool ambiguous = false;
        for (int tried = 1; ; tried++)
        {
            PutOutcome outcome;
            try
            {
                outcome = await attempt(cancellationToken).ConfigureAwait(false);
            }
            catch (StoreAnswerLostException lost) when (putId is not null)
            {
                ambiguous = true;
                if (await LearnAsync(service, key, putId, condition, lost, cancellationToken).ConfigureAwait(false) is { } learned)
                {
                    return learned;
                }

                if (!await transport.RetryAfterAsync(tried, cancellationToken).ConfigureAwait(false))
                {
                    throw new ObjectStoreException($"{operation} of '{key}' did not land after {tried} attempts: {lost.Message}", lost.InnerException!);
                }

                continue;
            }

            return ambiguous && outcome != PutOutcome.Created
                ? await RecheckAsync(service, key, putId!, outcome, cancellationToken).ConfigureAwait(false)
                : outcome;
        }
    }

    /// <summary>What became of a write whose answer was lost, from the head; null to send it again.</summary>
    /// <exception cref="ObjectStoreException">The head failed too, and the outcome is unknown.</exception>
    private static async ValueTask<PutOutcome?> LearnAsync(
        IUploadService service, string key, string putId, PutCondition condition, StoreAnswerLostException lost, CancellationToken cancellationToken)
    {
        (ObjectHead? head, string? id) found;
        try
        {
            found = await service.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is ObjectStoreException or StoreAnswerLostException)
        {
            throw new ObjectStoreException(
                $"A put of '{key}' lost its answer ({lost.Message}), and the head that would say whether it landed failed too: the outcome is unknown.",
                new AggregateException(lost, failed))
            {
                OutcomeUnknown = true,
            };
        }

        if (found.head is not { } existing)
        {
            return condition.Kind == PutConditionKind.IfMatch ? PutOutcome.Changed : null;
        }

        if (string.Equals(found.id, putId, StringComparison.Ordinal))
        {
            return PutOutcome.Created;
        }

        if (condition.Kind == PutConditionKind.IfMatch)
        {
            return string.Equals(existing.Token, condition.Token, StringComparison.Ordinal) ? null : PutOutcome.Changed;
        }

        // Taken by another writer, or, unconditionally, a write that carried no id: an object is there.
        return condition.Kind == PutConditionKind.IfAbsent ? PutOutcome.Exists : null;
    }

    /// <summary>
    /// A refusal on the condition after an attempt whose answer was lost: this put's own earlier attempt
    /// when the head finds its id, the refusal otherwise.
    /// </summary>
    /// <exception cref="ObjectStoreException">The head failed, and the outcome is unknown.</exception>
    private static async ValueTask<PutOutcome> RecheckAsync(IUploadService service, string key, string putId, PutOutcome refused, CancellationToken cancellationToken)
    {
        (ObjectHead? head, string? id) found;
        try
        {
            found = await service.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is ObjectStoreException or StoreAnswerLostException)
        {
            throw new ObjectStoreException(
                $"A put of '{key}' was refused on its condition after an attempt whose answer was lost, and the head that would say whether that attempt took the key failed: the outcome is unknown.",
                failed)
            {
                OutcomeUnknown = true,
            };
        }

        return found.head is not null && string.Equals(found.id, putId, StringComparison.Ordinal) ? PutOutcome.Created : refused;
    }

    private static ObjectStoreException TooLargeToHold(IUploadService service, UploadLimits limits, string key, long length) =>
        new ObjectStoreException(
            $"A conditional {service.PutOperation} goes in one request, and '{key}' is {(length < 0 ? "longer than" : $"{length} bytes, past")} " +
            $"the {Math.Min(limits.MaxHeldBytes, limits.MaxSinglePut)} bytes a put holds in memory to send whole: " +
            "set StoreTransportOptions.SpoolDirectory to hold it on disk instead.");

    private static ObjectStoreException PastItsBound(string key, long bound) =>
        new ObjectStoreException($"'{key}' was announced as at most {bound} bytes and its content runs past them.");

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

    /// <summary>A random id a put carries in its object's metadata, by which a head recognises it.</summary>
    private static string NewPutId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
