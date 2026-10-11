using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage.Google;

/// <summary>What Cloud Storage's JSON API answers: an object's metadata, a listing's page, an error.</summary>
internal static class GoogleAnswers
{
    /// <summary>The fields a head asks for, and nothing else.</summary>
    internal const string ObjectFields = "size,generation,timeCreated,retentionExpirationTime,temporaryHold,eventBasedHold,metadata";

    /// <summary>The fields a listing asks for, and nothing else.</summary>
    internal const string ListFields = "nextPageToken,items(name,size,generation,timeCreated,retentionExpirationTime,temporaryHold,eventBasedHold)";

    /// <summary>An object's head from its metadata, and the put id the metadata carries.</summary>
    internal static (ObjectHead Head, string? PutId) ParseObject(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new Utf8JsonReader(json);
        try
        {
            Expect(ref reader, JsonTokenType.StartObject);
            (ObjectHead head, string? putId, _) = ReadObject(ref reader);
            return (head, putId);
        }
        catch (Exception malformed) when (malformed is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new ObjectStoreException("The service answered with malformed JSON.", malformed);
        }
    }

    /// <summary>
    /// A page of <c>objects.list</c>: each object, with its head and lock, the one named
    /// <paramref name="skip"/> left out since <c>startOffset</c> includes it; and the next page's token.
    /// </summary>
    internal static string? ParseListing(ReadOnlySpan<byte> json, string? skip, List<ObjectListing> into)
    {
        Utf8JsonReader reader = new Utf8JsonReader(json);
        string? next = null;
        try
        {
            Expect(ref reader, JsonTokenType.StartObject);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("nextPageToken"u8))
                {
                    reader.Read();
                    next = reader.GetString();
                }
                else if (reader.ValueTextEquals("items"u8))
                {
                    Expect(ref reader, JsonTokenType.StartArray);
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        (ObjectHead head, _, string? name) = ReadObject(ref reader);
                        if (name is null)
                        {
                            throw new ObjectStoreException("The service listed an object without its name.");
                        }

                        if (!string.Equals(name, skip, StringComparison.Ordinal))
                        {
                            into.Add(new ObjectListing(name, head, true));
                        }
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (Exception malformed) when (malformed is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new ObjectStoreException("The service answered with malformed JSON.", malformed);
        }

        return string.IsNullOrEmpty(next) ? null : next;
    }

    /// <summary>The reason and the message of a JSON error: <c>{"error": {"errors": [{"reason": …}], "message": …}}</c>.</summary>
    internal static (string? Reason, string? Message) ParseError(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new Utf8JsonReader(json);
        string? reason = null, message = null;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("reason"u8) && reason is null)
                {
                    reader.Read();
                    reason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("message"u8) && message is null)
                {
                    reader.Read();
                    message = reader.TokenType == JsonTokenType.String ? ServiceError.Clip(reader.GetString()!) : null;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the status says enough.
        }

        return (reason, message);
    }

    /// <summary>Reads an object's fields up to its end, the reader past its <c>{</c>.</summary>
    private static (ObjectHead Head, string? PutId, string? Name) ReadObject(ref Utf8JsonReader reader)
    {
        string? name = null, generation = null, putId = null;
        long size = -1;
        DateTimeOffset created = default;
        DateTimeOffset? retainUntil = null;
        bool held = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                name = reader.GetString();
            }
            else if (reader.ValueTextEquals("size"u8))
            {
                // The service writes a 64-bit number as a string; an emulator may not.
                reader.Read();
                size = reader.TokenType == JsonTokenType.Number
                    ? reader.GetInt64()
                    : long.Parse(reader.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (reader.ValueTextEquals("generation"u8))
            {
                reader.Read();
                generation = reader.TokenType == JsonTokenType.Number
                    ? reader.GetInt64().ToString(CultureInfo.InvariantCulture)
                    : reader.GetString();
            }
            else if (reader.ValueTextEquals("timeCreated"u8))
            {
                reader.Read();
                created = Date(reader.GetString()!);
            }
            else if (reader.ValueTextEquals("retentionExpirationTime"u8))
            {
                reader.Read();
                retainUntil = Date(reader.GetString()!);
            }
            else if (reader.ValueTextEquals("temporaryHold"u8) || reader.ValueTextEquals("eventBasedHold"u8))
            {
                reader.Read();
                held |= reader.TokenType == JsonTokenType.True;
            }
            else if (reader.ValueTextEquals("metadata"u8))
            {
                Expect(ref reader, JsonTokenType.StartObject);
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool ours = reader.ValueTextEquals("vxput"u8);
                    reader.Read();
                    if (ours)
                    {
                        putId = reader.GetString();
                    }
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (size < 0 || generation is null)
        {
            throw new ObjectStoreException("The service described an object without its size or its generation.");
        }

        return (new ObjectHead(size, generation, created) { RetainUntil = retainUntil, LegalHold = held }, putId, name);
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (!reader.Read() || reader.TokenType != token)
        {
            throw new JsonException($"A {token} was expected.");
        }
    }

    private static DateTimeOffset Date(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : throw new ObjectStoreException($"The service wrote a date that is not one: '{ServiceError.Clip(text)}'.");
}
