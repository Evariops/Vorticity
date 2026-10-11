using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Vorticity.IO;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage.Xml;

/// <summary>
/// The services' XML, read by the platform's <see cref="XmlReader"/> and written by its
/// <see cref="XmlWriter"/>. A document type is refused, so no entity is ever defined and none expands,
/// and a document the reader cannot read becomes the store's exception.
/// </summary>
/// <remarks>
/// A reader walks an element's children with <see cref="Enter"/> and <see cref="NextChild"/>: each
/// child it stops on is read whole (<c>ReadElementContentAs…</c>) or skipped (<see cref="XmlReader.Skip"/>)
/// before the next, so that no child is passed over.
/// </remarks>
internal static class ServiceXml
{
    private static readonly XmlReaderSettings Reading = new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    // A line break in a value is written as a reference, so that a key holding one reaches the service
    // as it is rather than normalized.
    private static readonly XmlWriterSettings Writing = new XmlWriterSettings
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        OmitXmlDeclaration = true,
        NewLineHandling = NewLineHandling.Entitize,
    };

    private static readonly XmlWriterSettings WritingDeclared = new XmlWriterSettings
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        NewLineHandling = NewLineHandling.Entitize,
    };

    /// <summary>Whether an answer holds nothing but the whitespace a service may send while it works.</summary>
    internal static bool IsBlank(ReadOnlyMemory<byte> xml) => Trimmed(xml).IsEmpty;

    /// <summary>A reader over an answer, from its first byte past the whitespace a service may send before its document.</summary>
    internal static XmlReader Open(ReadOnlyMemory<byte> xml)
    {
        ReadOnlyMemory<byte> document = Trimmed(xml);
        MemoryStream stream = MemoryMarshal.TryGetArray(document, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(document.ToArray(), writable: false);
        return XmlReader.Create(stream, Reading);
    }

    /// <summary>
    /// Steps into the element the reader stands on: true when it holds something, which
    /// <see cref="NextChild"/> then walks; false for an empty element, which the reader has passed.
    /// </summary>
    internal static bool Enter(XmlReader reader, out int depth)
    {
        depth = reader.Depth;
        bool empty = reader.IsEmptyElement;
        reader.Read();
        return !empty;
    }

    /// <summary>
    /// Moves to the next element directly under the one entered at <paramref name="depth"/>, staying on
    /// one the reader already stands on; false at that element's end, which the reader then passes.
    /// </summary>
    internal static bool NextChild(XmlReader reader, int depth)
    {
        while (!reader.EOF)
        {
            if (reader.Depth <= depth)
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    reader.Read();
                }

                return false;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                return true;
            }

            reader.Read();
        }

        return false;
    }

    /// <summary>An error document's code, message, request id and, for a range past an object's end, the object's size.</summary>
    /// <exception cref="ObjectStoreException">The answer is not XML.</exception>
    internal static (string? Code, string? Message, string? RequestId, long? ActualObjectSize) ParseError(ReadOnlyMemory<byte> xml)
    {
        string? code = null, message = null, requestId = null;
        long? size = null;
        if (IsBlank(xml))
        {
            return (code, message, requestId, size);
        }

        try
        {
            using XmlReader reader = Open(xml);
            if (reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "Error" && Enter(reader, out int depth))
            {
                while (NextChild(reader, depth))
                {
                    switch (reader.LocalName)
                    {
                        case "Code":
                            code = reader.ReadElementContentAsString();
                            break;
                        case "Message":
                            message = ServiceError.Clip(reader.ReadElementContentAsString());
                            break;
                        case "RequestId":
                            requestId = reader.ReadElementContentAsString();
                            break;
                        case "ActualObjectSize":
                            size = reader.ReadElementContentAsLong();
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
            }
        }
        catch (Exception malformed) when (IsMalformed(malformed))
        {
            throw Malformed(malformed);
        }

        return (code, message, requestId, size);
    }

    /// <summary>The text of the first element named <paramref name="name"/>, at any depth, or null.</summary>
    /// <exception cref="ObjectStoreException">The answer is not XML.</exception>
    internal static string? ParseElement(ReadOnlyMemory<byte> xml, string name)
    {
        if (IsBlank(xml))
        {
            return null;
        }

        try
        {
            using XmlReader reader = Open(xml);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == name)
                {
                    return reader.ReadElementContentAsString();
                }
            }

            return null;
        }
        catch (Exception malformed) when (IsMalformed(malformed))
        {
            throw Malformed(malformed);
        }
    }

    /// <summary>Whether the document is an error, as an answer that began with 200 may end with one, and its code.</summary>
    /// <exception cref="ObjectStoreException">The answer is not XML.</exception>
    internal static bool IsError(ReadOnlyMemory<byte> xml, out string? code)
    {
        code = null;
        if (IsBlank(xml))
        {
            return false;
        }

        bool error;
        try
        {
            using XmlReader reader = Open(xml);
            error = reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "Error";
        }
        catch (Exception malformed) when (IsMalformed(malformed))
        {
            throw Malformed(malformed);
        }

        if (error)
        {
            code = ParseError(xml).Code;
        }

        return error;
    }

    /// <summary>
    /// A request's body, written by <paramref name="write"/>: UTF-8 without a byte order mark, a line break
    /// in a value as a reference, and the XML declaration first when <paramref name="declared"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A value holds a character XML cannot carry.</exception>
    internal static byte[] Write<TState>(TState state, Action<XmlWriter, TState> write, bool declared = false)
    {
        using MemoryStream stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, declared ? WritingDeclared : Writing))
        {
            write(writer, state);
        }

        return stream.ToArray();
    }

    /// <summary>Whether a failure is the reader's, meeting a document it cannot read.</summary>
    internal static bool IsMalformed(Exception caught) => caught is XmlException or FormatException or OverflowException;

    /// <summary>The store's exception for a document the reader could not read.</summary>
    internal static ObjectStoreException Malformed(Exception cause) =>
        new ObjectStoreException($"The service answered with XML this store cannot read: {ServiceError.Clip(cause.Message)}", cause);

    private static ReadOnlyMemory<byte> Trimmed(ReadOnlyMemory<byte> xml)
    {
        ReadOnlySpan<byte> span = xml.Span;
        int start = 0;
        while (start < span.Length && span[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        return xml[start..];
    }
}
