using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage.Azure;

/// <summary>What Azure Blob Storage's answers say: its listings and its batch responses.</summary>
internal static class AzureAnswers
{
    /// <summary>
    /// A page of <c>List Blobs</c>: each blob's name, decoded when the service URL-encoded it, stripped of
    /// nothing, with its length, ETag, date and lock; and the marker of the next page, or null for the last.
    /// A blob named <paramref name="skip"/> is left out, since <c>startFrom</c> includes the key it names.
    /// </summary>
    /// <exception cref="ObjectStoreException">The answer is not a listing, or a blob in it lacks its name, its ETag or its length.</exception>
    internal static string? ParseListing(ReadOnlyMemory<byte> xml, string? skip, List<ObjectListing> into)
    {
        string? next = null;
        try
        {
            using XmlReader reader = ServiceXml.Open(xml);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "EnumerationResults")
            {
                throw new ObjectStoreException($"The service answered a listing with <{reader.LocalName}> rather than <EnumerationResults>.");
            }

            if (ServiceXml.Enter(reader, out int depth))
            {
                while (ServiceXml.NextChild(reader, depth))
                {
                    if (reader.LocalName == "NextMarker")
                    {
                        string marker = reader.ReadElementContentAsString();
                        next = marker.Length == 0 ? null : marker;
                    }
                    else if (reader.LocalName != "Blobs")
                    {
                        reader.Skip();
                    }
                    else if (ServiceXml.Enter(reader, out int blobs))
                    {
                        while (ServiceXml.NextChild(reader, blobs))
                        {
                            if (reader.LocalName != "Blob")
                            {
                                reader.Skip();
                                continue;
                            }

                            ObjectListing listed = Blob(reader);
                            if (!string.Equals(listed.Key, skip, StringComparison.Ordinal))
                            {
                                into.Add(listed);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception malformed) when (ServiceXml.IsMalformed(malformed))
        {
            throw ServiceXml.Malformed(malformed);
        }

        return next;
    }

    /// <summary>One blob of a listing, with its lock, the reader on its <c>Blob</c> and left past it.</summary>
    private static ObjectListing Blob(XmlReader reader)
    {
        string? name = null, etag = null;
        long length = -1;
        DateTimeOffset modified = default;
        DateTimeOffset? retainUntil = null;
        bool legalHold = false;
        if (ServiceXml.Enter(reader, out int depth))
        {
            while (ServiceXml.NextChild(reader, depth))
            {
                if (reader.LocalName == "Name")
                {
                    bool encoded = reader.GetAttribute("Encoded") == "true";
                    string text = reader.ReadElementContentAsString();
                    name = encoded ? Uri.UnescapeDataString(text) : text;
                }
                else if (reader.LocalName != "Properties")
                {
                    reader.Skip();
                }
                else if (ServiceXml.Enter(reader, out int properties))
                {
                    while (ServiceXml.NextChild(reader, properties))
                    {
                        switch (reader.LocalName)
                        {
                            case "Content-Length":
                                length = reader.ReadElementContentAsLong();
                                break;
                            case "Etag":
                                etag = Quoted(reader.ReadElementContentAsString());
                                break;
                            case "Last-Modified":
                                modified = Date(reader.ReadElementContentAsString());
                                break;
                            case "ImmutabilityPolicyUntilDate":
                                string until = reader.ReadElementContentAsString();
                                retainUntil = until.Length == 0 ? null : Date(until);
                                break;
                            case "LegalHold":
                                legalHold = reader.ReadElementContentAsString() == "true";
                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }
                }
                else if (reader.LocalName != "Properties")
                {
                    reader.Skip();
                }
            }
        }

        if (name is null || etag is null || length < 0)
        {
            throw new ObjectStoreException("The service listed a blob without its name, its ETag or its length.");
        }

        return new ObjectListing(name, new ObjectHead(length, etag, modified) { RetainUntil = retainUntil, LegalHold = legalHold }, true);
    }

    /// <summary>
    /// The deletes of a <c>Blob Batch</c> that left a blob in place, from its multipart response: each
    /// sub-response's status and error code; an absent blob is no failure. Empty when every blob is gone.
    /// </summary>
    internal static string ParseBatch(string body, string boundary, IReadOnlyList<string> keys)
    {
        List<string> failures = [];
        int count = 0;
        int index = 0;
        foreach (string part in body.Split("--" + boundary, StringSplitOptions.None))
        {
            int http = part.IndexOf("HTTP/1.1 ", StringComparison.Ordinal);
            if (http < 0)
            {
                continue;
            }

            if (part.Length < http + 12 || !int.TryParse(part.AsSpan(http + 9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            {
                throw new ObjectStoreException("The service answered a batch with a part that holds no status.");
            }

            string? id = Field(part, "Content-ID");
            int which = id is not null && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int named) ? named : index;
            index++;
            if (status is 202 or 404)
            {
                continue;
            }

            count++;
            if (failures.Count < 3)
            {
                string key = which < keys.Count ? keys[which] : $"#{which}";
                failures.Add($"'{key}' (HTTP {status} {Field(part, "x-ms-error-code")})");
            }
        }

        return count == 0 ? string.Empty : $"{count} blob(s), among them {string.Join(", ", failures)}";
    }

    /// <summary>A token as a head says it, in quotes, whatever a listing wrote.</summary>
    internal static string Quoted(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    /// <summary>A date as the service writes it, RFC 1123 in a listing and a head.</summary>
    internal static DateTimeOffset Date(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : throw new ObjectStoreException($"The service wrote a date that is not one: '{ServiceError.Clip(text)}'.");

    private static string? Field(string part, string name)
    {
        foreach (string line in part.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(name.Length + 1)..].Trim();
            }
        }

        return null;
    }
}
