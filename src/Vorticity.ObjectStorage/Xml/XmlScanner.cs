using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Xml;

/// <summary>What <see cref="XmlScanner.Read"/> stopped on.</summary>
internal enum XmlToken
{
    /// <summary>An element's start; <see cref="XmlScanner.Name"/> is its name.</summary>
    Start,

    /// <summary>An element's end, a self-closing one's included; <see cref="XmlScanner.Name"/> is its name.</summary>
    End,

    /// <summary>Text between tags, its entities not yet decoded.</summary>
    Text,

    /// <summary>A CDATA section's text, taken as it is.</summary>
    CData,
}

/// <summary>
/// A pull reader of the XML the services answer with: elements, text and CDATA, over UTF-8 bytes,
/// with no allocation but the strings a caller asks for.
/// </summary>
/// <remarks>
/// Declarations, processing instructions and comments are skipped, attributes are passed over, and a
/// document type is refused, so that no entity is ever defined and none expands. A malformed document
/// throws an <see cref="ObjectStoreException"/>; nothing else escapes, whatever the bytes.
/// </remarks>
internal ref struct XmlScanner
{
    private static readonly SearchValues<byte> NameEnds = SearchValues.Create(" \t\r\n/>"u8);
    private readonly ReadOnlySpan<byte> _xml;
    private int _at;
    private bool _closePending;

    /// <summary>A scanner over <paramref name="xml"/>.</summary>
    internal XmlScanner(ReadOnlySpan<byte> xml)
    {
        _xml = xml;
        _at = xml.StartsWith("﻿"u8) ? 3 : 0;
    }

    /// <summary>What the last <see cref="Read"/> stopped on.</summary>
    internal XmlToken Token { get; private set; }

    /// <summary>The element's name, prefix included, for <see cref="XmlToken.Start"/> and <see cref="XmlToken.End"/>.</summary>
    internal ReadOnlySpan<byte> Name { get; private set; }

    /// <summary>The raw text, for <see cref="XmlToken.Text"/> and <see cref="XmlToken.CData"/>.</summary>
    internal ReadOnlySpan<byte> Text { get; private set; }

    /// <summary>The attributes of the last <see cref="XmlToken.Start"/>, raw, as the tag wrote them.</summary>
    internal ReadOnlySpan<byte> Attributes { get; private set; }

    /// <summary>The value of the attribute <paramref name="name"/> of the last start tag, decoded, or null.</summary>
    internal readonly string? Attribute(ReadOnlySpan<byte> name)
    {
        ReadOnlySpan<byte> rest = Attributes;
        while (true)
        {
            rest = rest.TrimStart(" \t\r\n/"u8);
            int equals = rest.IndexOf((byte)'=');
            if (equals <= 0)
            {
                return null;
            }

            ReadOnlySpan<byte> found = rest[..equals].Trim(" \t\r\n"u8);
            rest = rest[(equals + 1)..].TrimStart(" \t\r\n"u8);
            if (rest.IsEmpty || rest[0] is not ((byte)'"' or (byte)'\''))
            {
                throw Malformed("an attribute's value is not quoted");
            }

            int close = rest[1..].IndexOf(rest[0]);
            if (close < 0)
            {
                throw Malformed("an attribute's value does not end");
            }

            ReadOnlySpan<byte> value = rest.Slice(1, close);
            if (found.SequenceEqual(name))
            {
                return Decode(value);
            }

            rest = rest[(close + 2)..];
        }
    }

    /// <summary>Moves to the next start, end or text.</summary>
    /// <returns>False at the end of the document.</returns>
    internal bool Read()
    {
        if (_closePending)
        {
            _closePending = false;
            Token = XmlToken.End;
            return true;
        }

        while (_at < _xml.Length)
        {
            ReadOnlySpan<byte> rest = _xml[_at..];
            if (rest[0] != (byte)'<')
            {
                int end = rest.IndexOf((byte)'<');
                int length = end < 0 ? rest.Length : end;
                Text = rest[..length];
                Token = XmlToken.Text;
                _at += length;
                return true;
            }

            if (rest.StartsWith("<?"u8))
            {
                _at += Skip(rest, "?>"u8);
            }
            else if (rest.StartsWith("<!--"u8))
            {
                _at += Skip(rest, "-->"u8);
            }
            else if (rest.StartsWith("<![CDATA["u8))
            {
                int end = rest[9..].IndexOf("]]>"u8);
                if (end < 0)
                {
                    throw Malformed("a CDATA section does not end");
                }

                Text = rest.Slice(9, end);
                Token = XmlToken.CData;
                _at += 9 + end + 3;
                return true;
            }
            else if (rest.StartsWith("<!"u8))
            {
                throw Malformed("a document type or a declaration, which an answer never needs");
            }
            else if (rest.StartsWith("</"u8))
            {
                int end = rest.IndexOf((byte)'>');
                if (end < 0)
                {
                    throw Malformed("an end tag does not close");
                }

                Name = Trim(rest[2..end]);
                CheckName(Name);
                Token = XmlToken.End;
                _at += end + 1;
                return true;
            }
            else
            {
                int nameEnd = rest[1..].IndexOfAny(NameEnds);
                if (nameEnd <= 0)
                {
                    throw Malformed("a start tag has no name or does not close");
                }

                Name = rest.Slice(1, nameEnd);
                CheckName(Name);
                int tagEnd = TagEnd(rest, 1 + nameEnd);
                _closePending = rest[tagEnd - 1] == (byte)'/';
                Attributes = rest[(1 + nameEnd)..tagEnd];
                Token = XmlToken.Start;
                _at += tagEnd + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After a <see cref="XmlToken.Start"/>, reads the element's text up to its end, its entities
    /// decoded; an element with children is refused.
    /// </summary>
    internal string ReadText()
    {
        if (Token != XmlToken.Start)
        {
            throw Malformed("text was asked for outside an element");
        }

        string? single = null;
        StringBuilder? several = null;
        while (Read())
        {
            switch (Token)
            {
                case XmlToken.End:
                    return several?.ToString() ?? single ?? string.Empty;
                case XmlToken.Text:
                case XmlToken.CData:
                    string piece = Token == XmlToken.Text ? Decode(Text) : Utf8(Text);
                    if (single is null)
                    {
                        single = piece;
                    }
                    else
                    {
                        (several ??= new StringBuilder(single)).Append(piece);
                    }

                    break;
                default:
                    throw Malformed("an element holds another where text was expected");
            }
        }

        throw Malformed("the document ends inside an element");
    }

    /// <summary>After a <see cref="XmlToken.Start"/>, reads the element's text as a number.</summary>
    internal long ReadInt64()
    {
        string text = ReadText();
        return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : throw Malformed($"'{Clip(text)}' is not a number");
    }

    /// <summary>After a <see cref="XmlToken.Start"/>, skips the element and everything in it.</summary>
    internal void Skip()
    {
        if (Token != XmlToken.Start)
        {
            return;
        }

        int depth = 1;
        while (depth > 0 && Read())
        {
            if (Token == XmlToken.Start)
            {
                depth++;
            }
            else if (Token == XmlToken.End)
            {
                depth--;
            }
        }

        if (depth > 0)
        {
            throw Malformed("the document ends inside an element");
        }
    }

    /// <summary>Whether <paramref name="name"/>, its prefix set aside, is <paramref name="local"/>.</summary>
    internal static bool Is(ReadOnlySpan<byte> name, ReadOnlySpan<byte> local)
    {
        int colon = name.IndexOf((byte)':');
        return (colon < 0 ? name : name[(colon + 1)..]).SequenceEqual(local);
    }

    /// <summary>Text with its five named entities and its character references decoded.</summary>
    internal static string Decode(ReadOnlySpan<byte> raw)
    {
        int amp = raw.IndexOf((byte)'&');
        if (amp < 0)
        {
            return Utf8(raw);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(raw.Length);
        try
        {
            int written = 0;
            while (amp >= 0)
            {
                raw[..amp].CopyTo(buffer.AsSpan(written));
                written += amp;
                int semicolon = raw[amp..].IndexOf((byte)';');
                if (semicolon < 0 || semicolon > 12)
                {
                    throw Malformed("an entity does not end");
                }

                ReadOnlySpan<byte> entity = raw.Slice(amp + 1, semicolon - 1);
                written += Entity(entity, buffer.AsSpan(written));
                raw = raw[(amp + semicolon + 1)..];
                amp = raw.IndexOf((byte)'&');
            }

            raw.CopyTo(buffer.AsSpan(written));
            written += raw.Length;
            return Utf8(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes the UTF-8 of one entity, named or numeric, and returns its length, never more than the entity's own.</summary>
    private static int Entity(ReadOnlySpan<byte> entity, Span<byte> destination)
    {
        byte named = entity switch
        {
            _ when entity.SequenceEqual("lt"u8) => (byte)'<',
            _ when entity.SequenceEqual("gt"u8) => (byte)'>',
            _ when entity.SequenceEqual("amp"u8) => (byte)'&',
            _ when entity.SequenceEqual("quot"u8) => (byte)'"',
            _ when entity.SequenceEqual("apos"u8) => (byte)'\'',
            _ => 0,
        };
        if (named != 0)
        {
            destination[0] = named;
            return 1;
        }

        if (entity.Length < 2 || entity[0] != (byte)'#')
        {
            throw Malformed($"'&{Utf8(entity)};' is not an entity XML defines");
        }

        bool hex = entity[1] is (byte)'x' or (byte)'X';
        ReadOnlySpan<byte> digits = entity[(hex ? 2 : 1)..];
        if (!Utf8Parser.TryParse(digits, out uint code, out int consumed, hex ? 'x' : default) || consumed != digits.Length
            || code == 0 || !Rune.IsValid(code))
        {
            throw Malformed($"'&{Utf8(entity)};' is not a character");
        }

        // A reference is at least four bytes (&#9;), never shorter than the UTF-8 it stands for.
        return new Rune(code).EncodeToUtf8(destination);
    }

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException invalid)
        {
            throw new ObjectStoreException("The service answered with XML that is not UTF-8.", invalid);
        }
    }

    private static readonly UTF8Encoding Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int Skip(ReadOnlySpan<byte> rest, ReadOnlySpan<byte> terminator)
    {
        int end = rest.IndexOf(terminator);
        return end < 0 ? throw Malformed("a declaration or a comment does not end") : end + terminator.Length;
    }

    /// <summary>The index of the <c>&gt;</c> that ends a start tag, past its attributes and their quoted values.</summary>
    private static int TagEnd(ReadOnlySpan<byte> rest, int from)
    {
        byte quote = 0;
        for (int i = from; i < rest.Length; i++)
        {
            byte b = rest[i];
            if (quote != 0)
            {
                if (b == quote)
                {
                    quote = 0;
                }
            }
            else if (b is (byte)'"' or (byte)'\'')
            {
                quote = b;
            }
            else if (b == (byte)'<')
            {
                break;
            }
            else if (b == (byte)'>')
            {
                return i;
            }
        }

        throw Malformed("a start tag does not close");
    }

    private static void CheckName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty || name.IndexOfAny(" \t\r\n<>&\"'="u8) >= 0)
        {
            throw Malformed("an element's name is not one");
        }
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span) => span.Trim(" \t\r\n"u8);

    private static string Clip(string text) => text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 64), "...");

    private static ObjectStoreException Malformed(string what) =>
        new ObjectStoreException($"The service answered with malformed XML: {what}.");
}
