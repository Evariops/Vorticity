using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage.S3;

/// <summary>What S3's answers say beyond the errors every service writes alike: its listings and what its batch deletes left.</summary>
internal static class S3Answers
{
    /// <summary>The namespace of S3's documents, which its requests' bodies are written in.</summary>
    internal const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// A page of <c>ListObjectsV2</c>: each object's key, decoded from the URL encoding asked for and
    /// stripped of <paramref name="strip"/>, with its length, ETag and date; and the token of the next
    /// page, or null for the last.
    /// </summary>
    /// <exception cref="ObjectStoreException">The answer is not a listing, or an object in it lacks its key, its ETag or its size.</exception>
    internal static string? ParseListing(ReadOnlyMemory<byte> xml, int strip, bool lockReported, List<ObjectListing> into)
    {
        string? next = null;
        bool truncated = false;
        try
        {
            using XmlReader reader = ServiceXml.Open(xml);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "ListBucketResult")
            {
                throw new ObjectStoreException($"The service answered a listing with <{reader.LocalName}> rather than <ListBucketResult>.");
            }

            if (ServiceXml.Enter(reader, out int depth))
            {
                while (ServiceXml.NextChild(reader, depth))
                {
                    switch (reader.LocalName)
                    {
                        case "Contents":
                            into.Add(Contents(reader, strip, lockReported));
                            break;
                        case "IsTruncated":
                            truncated = reader.ReadElementContentAsString() == "true";
                            break;
                        case "NextContinuationToken":
                            next = reader.ReadElementContentAsString();
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
            }
        }
        catch (Exception malformed) when (ServiceXml.IsMalformed(malformed))
        {
            throw ServiceXml.Malformed(malformed);
        }

        if (truncated && string.IsNullOrEmpty(next))
        {
            throw new ObjectStoreException("The service said its listing goes on and gave no token to resume it.");
        }

        return truncated ? next : null;
    }

    /// <summary>
    /// The keys a batch delete left in place, each with its code, from a quiet <c>DeleteResult</c>; an
    /// absent key is no failure. Empty when every key is gone.
    /// </summary>
    /// <exception cref="ObjectStoreException">The answer is not XML.</exception>
    internal static string ParseDeleteErrors(ReadOnlyMemory<byte> xml)
    {
        List<string> failures = [];
        int count = 0;
        if (ServiceXml.IsBlank(xml))
        {
            return string.Empty;
        }

        try
        {
            using XmlReader reader = ServiceXml.Open(xml);
            if (reader.MoveToContent() == XmlNodeType.Element && ServiceXml.Enter(reader, out int depth))
            {
                while (ServiceXml.NextChild(reader, depth))
                {
                    if (reader.LocalName != "Error")
                    {
                        reader.Skip();
                        continue;
                    }

                    (string? key, string? code, string? message) = DeleteError(reader);
                    if (code == "NoSuchKey")
                    {
                        continue;
                    }

                    count++;
                    if (failures.Count < 3)
                    {
                        failures.Add($"'{key}' ({code}: {message})");
                    }
                }
            }
        }
        catch (Exception malformed) when (ServiceXml.IsMalformed(malformed))
        {
            throw ServiceXml.Malformed(malformed);
        }

        return count == 0 ? string.Empty : $"{count} key(s), among them {string.Join(", ", failures)}";
    }

    /// <summary>An ISO 8601 date, as a listing and Object Lock write them.</summary>
    internal static DateTimeOffset IsoDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : throw new ObjectStoreException($"The service wrote a date that is not one: '{ServiceError.Clip(text)}'.");

    /// <summary>One object of a listing, the reader on its <c>Contents</c> and left past it.</summary>
    private static ObjectListing Contents(XmlReader reader, int strip, bool lockReported)
    {
        string? key = null, etag = null;
        long size = -1;
        DateTimeOffset modified = default;
        if (ServiceXml.Enter(reader, out int depth))
        {
            while (ServiceXml.NextChild(reader, depth))
            {
                switch (reader.LocalName)
                {
                    case "Key":
                        key = UriEncoding.DecodeForm(reader.ReadElementContentAsString());
                        break;
                    case "ETag":
                        etag = reader.ReadElementContentAsString();
                        break;
                    case "Size":
                        size = reader.ReadElementContentAsLong();
                        break;
                    case "LastModified":
                        // To the second, as a head says it: a service that lists milliseconds would
                        // otherwise list another head than its own.
                        DateTimeOffset listed = IsoDate(reader.ReadElementContentAsString());
                        modified = listed.AddTicks(-(listed.Ticks % TimeSpan.TicksPerSecond));
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        if (key is null || etag is null || size < 0 || key.Length < strip)
        {
            throw new ObjectStoreException("The service listed an object without its key, its ETag or its size.");
        }

        return new ObjectListing(key[strip..], new ObjectHead(size, etag, modified), lockReported);
    }

    /// <summary>One key a batch delete left, the reader on its <c>Error</c> and left past it.</summary>
    private static (string? Key, string? Code, string? Message) DeleteError(XmlReader reader)
    {
        string? key = null, code = null, message = null;
        if (ServiceXml.Enter(reader, out int depth))
        {
            while (ServiceXml.NextChild(reader, depth))
            {
                switch (reader.LocalName)
                {
                    case "Key":
                        key = reader.ReadElementContentAsString();
                        break;
                    case "Code":
                        code = reader.ReadElementContentAsString();
                        break;
                    case "Message":
                        message = ServiceError.Clip(reader.ReadElementContentAsString());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        return (key, code, message);
    }
}
