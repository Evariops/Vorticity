using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    internal static string? ParseListing(ReadOnlySpan<byte> xml, string? skip, List<ObjectListing> into)
    {
        string? next = null;
        XmlScanner scanner = new XmlScanner(xml);
        while (scanner.Read())
        {
            if (scanner.Token != XmlToken.Start)
            {
                continue;
            }

            if (XmlScanner.Is(scanner.Name, "Blob"u8))
            {
                string? name = null, etag = null;
                long length = -1;
                DateTimeOffset modified = default;
                DateTimeOffset? retainUntil = null;
                bool legalHold = false;
                while (scanner.Read() && scanner.Token != XmlToken.End)
                {
                    if (scanner.Token != XmlToken.Start)
                    {
                        continue;
                    }

                    if (XmlScanner.Is(scanner.Name, "Name"u8))
                    {
                        bool encoded = scanner.Attribute("Encoded"u8) == "true";
                        string text = scanner.ReadText();
                        name = encoded ? Uri.UnescapeDataString(text) : text;
                    }
                    else if (XmlScanner.Is(scanner.Name, "Properties"u8))
                    {
                        while (scanner.Read() && scanner.Token != XmlToken.End)
                        {
                            if (scanner.Token != XmlToken.Start)
                            {
                                continue;
                            }

                            if (XmlScanner.Is(scanner.Name, "Content-Length"u8))
                            {
                                length = scanner.ReadInt64();
                            }
                            else if (XmlScanner.Is(scanner.Name, "Etag"u8))
                            {
                                etag = Quoted(scanner.ReadText());
                            }
                            else if (XmlScanner.Is(scanner.Name, "Last-Modified"u8))
                            {
                                modified = Date(scanner.ReadText());
                            }
                            else if (XmlScanner.Is(scanner.Name, "ImmutabilityPolicyUntilDate"u8))
                            {
                                string until = scanner.ReadText();
                                retainUntil = until.Length == 0 ? null : Date(until);
                            }
                            else if (XmlScanner.Is(scanner.Name, "LegalHold"u8))
                            {
                                legalHold = scanner.ReadText() == "true";
                            }
                            else
                            {
                                scanner.Skip();
                            }
                        }
                    }
                    else
                    {
                        scanner.Skip();
                    }
                }

                if (name is null || etag is null || length < 0)
                {
                    throw new ObjectStoreException("The service listed a blob without its name, its ETag or its length.");
                }

                if (!string.Equals(name, skip, StringComparison.Ordinal))
                {
                    into.Add(new ObjectListing(name, new ObjectHead(length, etag, modified) { RetainUntil = retainUntil, LegalHold = legalHold }, true));
                }
            }
            else if (XmlScanner.Is(scanner.Name, "NextMarker"u8))
            {
                string marker = scanner.ReadText();
                next = marker.Length == 0 ? null : marker;
            }
            else if (!XmlScanner.Is(scanner.Name, "EnumerationResults"u8) && !XmlScanner.Is(scanner.Name, "Blobs"u8))
            {
                scanner.Skip();
            }
        }

        return next;
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
