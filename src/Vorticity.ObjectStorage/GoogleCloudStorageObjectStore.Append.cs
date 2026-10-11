using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage;

/// <summary>
/// Appends: the new bytes put as a temporary object beside the old one, which composition then joins
/// after the old object under its name, on the service, conditioned on the old generation; the
/// temporary object deleted whatever happens.
/// </summary>
/// <remarks>
/// An object composed is made of the components of its sources, and the service keeps at most 1 024:
/// a file appended to more than a thousand times is refused by the service, and is written again
/// instead. A process that dies between the two puts leaves its temporary object behind, named after
/// the file with <c>.vx-append-</c> and a random suffix. An object smaller than
/// <see cref="MinimumComposedBytes"/> is read back and sent again with the new bytes, in fewer requests.
/// </remarks>
public sealed partial class GoogleCloudStorageObjectStore : IAppendingObjectStore
{
    /// <summary>The smallest object an append composes on the service rather than reads back.</summary>
    internal const long MinimumComposedBytes = 5L << 20;

    /// <summary>What a temporary object of an append is named after the file's own name.</summary>
    internal const string AppendSuffix = ".vx-append-";

    /// <inheritdoc/>
    async ValueTask<PutOutcome> IAppendingObjectStore.AppendAsync(string key, string token, long length, PipeReader content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (length < _options.MinimumComposedBytes)
        {
            return await ObjectAppends.ResendAsync(this, key, token, length, content, cancellationToken).ConfigureAwait(false);
        }

        string temporary = key + AppendSuffix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        try
        {
            CheckKey(temporary);
        }
        catch (Exception refused)
        {
            await content.CompleteAsync(refused).ConfigureAwait(false);
            throw;
        }

        // A name nobody else writes: no condition, so that a large append goes up in parts.
        await PutAsync(temporary, content, -1, PutCondition.None, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ComposeAsync(key, token, temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await DeleteAsync([temporary], CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectStoreException)
            {
                // Left behind under its own name, which says what it is: the append's outcome says more.
            }
        }
    }

    /// <summary>
    /// Composes the old object, bound to <paramref name="token"/>, and <paramref name="temporary"/> under
    /// the old name, only while it keeps that generation; its outcome learned from the put id the
    /// destination's metadata carries when the answer is lost.
    /// </summary>
    private async ValueTask<PutOutcome> ComposeAsync(string key, string token, string temporary, CancellationToken cancellationToken)
    {
        string putId = NewPutId();
        byte[] body = ComposeBody(key, token, temporary, putId);
        PutCondition condition = PutCondition.IfMatch(token);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _transport.SendAsync(
                    "ComposeObject",
                    (Store: this, Key: key, Token: token, Body: body),
                    static (s, ct) => s.Store.BuildAsync(
                        HttpMethod.Post,
                        s.Store.JsonObject(s.Key) + "/compose?ifGenerationMatch=" + UriEncoding.Query(s.Token),
                        new SequenceContent(new ReadOnlySequence<byte>(s.Body)),
                        [("Content-Type", "application/json")],
                        ct),
                    static (response, s, ct) => s.Store.ConsumeComposeAsync(response, s.Key, ct),
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
                    throw new ObjectStoreException($"ComposeObject of '{key}' did not land after {attempt} attempts: {lost.Message}", lost.InnerException!);
                }
            }
        }
    }

    private async ValueTask<PutOutcome> ConsumeComposeAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return PutOutcome.Created;
            case HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound:
                // The old object has another generation, or is gone.
                return PutOutcome.Changed;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        throw MapFailure(response, error, "ComposeObject", key);
    }

    /// <summary>The composition's request: the old object bound to its generation, then the temporary one, and the destination's put id.</summary>
    private static byte[] ComposeBody(string key, string token, string temporary, string putId)
    {
        ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(256);
        using (Utf8JsonWriter json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("sourceObjects");
            json.WriteStartObject();
            json.WriteString("name", key);
            json.WriteStartObject("objectPreconditions");
            json.WriteString("ifGenerationMatch", token);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartObject();
            json.WriteString("name", temporary);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartObject("destination");
            json.WriteString("contentType", "application/octet-stream");
            json.WriteStartObject("metadata");
            json.WriteString("vxput", putId);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
