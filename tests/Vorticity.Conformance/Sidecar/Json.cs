// A JSON reader written by hand, on purpose.
//
// The sidecar is the oracle for every value in the corpus. Parsing it with a serializer
// would put a third implementation between the two we are comparing, and the one property this
// harness cannot afford to lose is that `null`, `"0"`, `{"bits": "0x8000"}` and `{"b64": ""}` are
// four different things: a JSON binder that maps them onto CLR types decides, silently, that
// -0.0 is 0.0 and that an empty string is a null. So this reads JSON as JSON - ordered keys,
// duplicates preserved, numbers and strings kept as raw text - and every interpretation happens in
// the comparer, where it is visible.
//
// It is not a general-purpose parser: no comments, no NaN literals, no big-number extensions. It is
// exactly RFC 8259, which is what the generator emits.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vorticity.Conformance.Sidecar;

/// <summary>The six JSON value kinds. `true` and `false` share one kind.</summary>
internal enum JsonKind : byte
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// One parsed JSON value. Object keys keep their document order and their duplicates: a Vortex
/// struct may have two fields with the same name, and a dictionary would drop one of them.
/// </summary>
internal sealed class JsonValue
{
    private static readonly JsonValue[] NoItems = [];
    private static readonly string[] NoKeys = [];

    internal static readonly JsonValue Null = new JsonValue(JsonKind.Null);
    internal static readonly JsonValue True = new JsonValue(JsonKind.Boolean) { Boolean = true };
    internal static readonly JsonValue False = new JsonValue(JsonKind.Boolean) { Boolean = false };

    private JsonValue(JsonKind kind)
    {
        Kind = kind;
        Items = NoItems;
        Keys = NoKeys;
    }

    internal JsonKind Kind { get; }

    internal bool Boolean { get; private init; }

    /// <summary>The decoded characters of a string, or the raw text of a number.</summary>
    internal string Text { get; private init; } = string.Empty;

    /// <summary>Array elements, in order. Empty for every other kind.</summary>
    internal JsonValue[] Items { get; private init; }

    /// <summary>Object keys, in document order, duplicates included.</summary>
    internal string[] Keys { get; private init; }

    /// <summary>Object values, parallel to <see cref="Keys"/>.</summary>
    internal JsonValue[] Values => Items;

    internal bool IsNull => Kind == JsonKind.Null;

    internal static JsonValue MakeString(string text) =>
        new JsonValue(JsonKind.String) { Text = text };

    internal static JsonValue MakeNumber(string text) =>
        new JsonValue(JsonKind.Number) { Text = text };

    internal static JsonValue MakeArray(JsonValue[] items) =>
        new JsonValue(JsonKind.Array) { Items = items };

    internal static JsonValue MakeObject(string[] keys, JsonValue[] values) =>
        new JsonValue(JsonKind.Object) { Keys = keys, Items = values };

    /// <summary>The first value stored under <paramref name="key"/>, or null when absent.</summary>
    internal JsonValue? Find(string key)
    {
        if (Kind != JsonKind.Object)
        {
            return null;
        }

        for (int i = 0; i < Keys.Length; i++)
        {
            if (string.Equals(Keys[i], key, StringComparison.Ordinal))
            {
                return Values[i];
            }
        }

        return null;
    }

    /// <summary>The value stored under <paramref name="key"/>; absence is a malformed sidecar.</summary>
    internal JsonValue Require(string key) =>
        Find(key) ?? throw new SidecarFormatException($"a JSON object has no '{key}' member: {Summary()}");

    internal string RequireString(string key)
    {
        JsonValue value = Require(key);
        if (value.Kind != JsonKind.String)
        {
            throw new SidecarFormatException($"'{key}' is {value.Kind}, not a string");
        }

        return value.Text;
    }

    internal long RequireInt64(string key)
    {
        JsonValue value = Require(key);
        if (value.Kind != JsonKind.Number ||
            !long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            throw new SidecarFormatException($"'{key}' is not an integer: {value.Summary()}");
        }

        return parsed;
    }

    internal bool RequireBoolean(string key)
    {
        JsonValue value = Require(key);
        if (value.Kind != JsonKind.Boolean)
        {
            throw new SidecarFormatException($"'{key}' is {value.Kind}, not a boolean");
        }

        return value.Boolean;
    }

    /// <summary>A short, safe rendering for an assertion message. Never the whole subtree.</summary>
    internal string Summary()
    {
        StringBuilder builder = new StringBuilder();
        Write(builder, this, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonValue value, int depth)
    {
        const int MaxDepth = 3;
        const int MaxItems = 6;

        switch (value.Kind)
        {
            case JsonKind.Null:
                builder.Append("null");
                return;
            case JsonKind.Boolean:
                builder.Append(value.Boolean ? "true" : "false");
                return;
            case JsonKind.Number:
                builder.Append(value.Text);
                return;
            case JsonKind.String:
                builder.Append('"').Append(Escape(value.Text)).Append('"');
                return;
            case JsonKind.Array:
                if (depth >= MaxDepth)
                {
                    builder.Append("[...]");
                    return;
                }

                builder.Append('[');
                for (int i = 0; i < value.Items.Length && i < MaxItems; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    Write(builder, value.Items[i], depth + 1);
                }

                if (value.Items.Length > MaxItems)
                {
                    builder.Append(", ...");
                }

                builder.Append(']');
                return;
            default:
                if (depth >= MaxDepth)
                {
                    builder.Append("{...}");
                    return;
                }

                builder.Append('{');
                for (int i = 0; i < value.Keys.Length && i < MaxItems; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append('"').Append(Escape(value.Keys[i])).Append("\": ");
                    Write(builder, value.Values[i], depth + 1);
                }

                if (value.Keys.Length > MaxItems)
                {
                    builder.Append(", ...");
                }

                builder.Append('}');
                return;
        }
    }

    private static string Escape(string text)
    {
        StringBuilder builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '"' || c == '\\')
            {
                builder.Append('\\').Append(c);
            }
            else if (c < ' ' || c > '~')
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>A sidecar - or the JSON inside it - that does not match the sidecar grammar.</summary>
internal sealed class SidecarFormatException : Exception
{
    internal SidecarFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A recursive-descent RFC 8259 reader over one JSON document held in a string. One instance per
/// document; <see cref="Parse(string)"/> is the whole API.
/// </summary>
internal sealed class JsonParser
{
    private readonly string _text;
    private int _at;

    private JsonParser(string text)
    {
        _text = text;
    }

    /// <summary>Parses one complete JSON document. Trailing non-whitespace is an error.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The parsed value.</returns>
    /// <exception cref="SidecarFormatException">The text is not one well-formed JSON document.</exception>
    internal static JsonValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonParser parser = new JsonParser(text);
        parser.SkipWhitespace();
        JsonValue value = parser.ReadValue(0);
        parser.SkipWhitespace();
        if (parser._at != text.Length)
        {
            throw parser.Error("trailing characters after the document");
        }

        return value;
    }

    private JsonValue ReadValue(int depth)
    {
        // The corpus's deepest dtype nests a handful of levels; 128 is far above anything legal and
        // stops a forged sidecar from turning into a stack overflow.
        if (depth > 128)
        {
            throw Error("nesting deeper than 128 levels");
        }

        if (_at >= _text.Length)
        {
            throw Error("the document ended where a value was expected");
        }

        char c = _text[_at];
        switch (c)
        {
            case '{':
                return ReadObject(depth);
            case '[':
                return ReadArray(depth);
            case '"':
                return JsonValue.MakeString(ReadString());
            case 't':
                Expect("true");
                return JsonValue.True;
            case 'f':
                Expect("false");
                return JsonValue.False;
            case 'n':
                Expect("null");
                return JsonValue.Null;
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return JsonValue.MakeNumber(ReadNumber());
                }

                throw Error($"'{c}' does not start a JSON value");
        }
    }

    private JsonValue ReadObject(int depth)
    {
        _at++;
        SkipWhitespace();

        List<string> keys = new List<string>();
        List<JsonValue> values = new List<JsonValue>();

        if (Peek() == '}')
        {
            _at++;
            return JsonValue.MakeObject([], []);
        }

        while (true)
        {
            SkipWhitespace();
            if (Peek() != '"')
            {
                throw Error("a member name must be a string");
            }

            keys.Add(ReadString());
            SkipWhitespace();
            if (Peek() != ':')
            {
                throw Error("expected ':' after a member name");
            }

            _at++;
            SkipWhitespace();
            values.Add(ReadValue(depth + 1));
            SkipWhitespace();

            char c = Peek();
            if (c == ',')
            {
                _at++;
                continue;
            }

            if (c == '}')
            {
                _at++;
                return JsonValue.MakeObject(keys.ToArray(), values.ToArray());
            }

            throw Error("expected ',' or '}' in an object");
        }
    }

    private JsonValue ReadArray(int depth)
    {
        _at++;
        SkipWhitespace();

        if (Peek() == ']')
        {
            _at++;
            return JsonValue.MakeArray([]);
        }

        List<JsonValue> items = new List<JsonValue>();
        while (true)
        {
            SkipWhitespace();
            items.Add(ReadValue(depth + 1));
            SkipWhitespace();

            char c = Peek();
            if (c == ',')
            {
                _at++;
                continue;
            }

            if (c == ']')
            {
                _at++;
                return JsonValue.MakeArray(items.ToArray());
            }

            throw Error("expected ',' or ']' in an array");
        }
    }

    private string ReadString()
    {
        // Opening quote already checked by the caller.
        _at++;
        int start = _at;
        StringBuilder? builder = null;

        while (true)
        {
            if (_at >= _text.Length)
            {
                throw Error("an unterminated string");
            }

            char c = _text[_at];
            if (c == '"')
            {
                if (builder is null)
                {
                    string plain = _text.Substring(start, _at - start);
                    _at++;
                    return plain;
                }

                builder.Append(_text, start, _at - start);
                _at++;
                return builder.ToString();
            }

            if (c == '\\')
            {
                builder ??= new StringBuilder();
                builder.Append(_text, start, _at - start);
                _at++;
                builder.Append(ReadEscape());
                start = _at;
                continue;
            }

            if (c < ' ')
            {
                throw Error($"an unescaped control character U+{(int)c:X4} in a string");
            }

            _at++;
        }
    }

    private char ReadEscape()
    {
        if (_at >= _text.Length)
        {
            throw Error("a truncated escape");
        }

        char c = _text[_at++];
        switch (c)
        {
            case '"': return '"';
            case '\\': return '\\';
            case '/': return '/';
            case 'b': return '\b';
            case 'f': return '\f';
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'u':
                if (_at + 4 > _text.Length)
                {
                    throw Error("a truncated \\u escape");
                }

                int code = 0;
                for (int i = 0; i < 4; i++)
                {
                    int digit = HexDigit(_text[_at + i]);
                    if (digit < 0)
                    {
                        throw Error("a \\u escape with a non-hex digit");
                    }

                    code = (code << 4) | digit;
                }

                _at += 4;

                // Surrogate halves are returned as-is: a well-formed pair reassembles into one
                // char pair in the StringBuilder, which is exactly what .NET's UTF-16 wants.
                return (char)code;
            default:
                throw Error($"'\\{c}' is not a JSON escape");
        }
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private string ReadNumber()
    {
        int start = _at;
        if (Peek() == '-')
        {
            _at++;
        }

        int digits = 0;
        while (_at < _text.Length && _text[_at] >= '0' && _text[_at] <= '9')
        {
            _at++;
            digits++;
        }

        if (digits == 0)
        {
            throw Error("a number with no integer digits");
        }

        if (_at < _text.Length && _text[_at] == '.')
        {
            _at++;
            int fraction = 0;
            while (_at < _text.Length && _text[_at] >= '0' && _text[_at] <= '9')
            {
                _at++;
                fraction++;
            }

            if (fraction == 0)
            {
                throw Error("a number with no fraction digits after '.'");
            }
        }

        if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
        {
            _at++;
            if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-'))
            {
                _at++;
            }

            int exponent = 0;
            while (_at < _text.Length && _text[_at] >= '0' && _text[_at] <= '9')
            {
                _at++;
                exponent++;
            }

            if (exponent == 0)
            {
                throw Error("a number with no exponent digits");
            }
        }

        return _text.Substring(start, _at - start);
    }

    private char Peek() => _at < _text.Length ? _text[_at] : '\0';

    private void Expect(string literal)
    {
        if (_at + literal.Length > _text.Length ||
            string.CompareOrdinal(_text, _at, literal, 0, literal.Length) != 0)
        {
            throw Error($"expected '{literal}'");
        }

        _at += literal.Length;
    }

    private void SkipWhitespace()
    {
        while (_at < _text.Length)
        {
            char c = _text[_at];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
            {
                _at++;
                continue;
            }

            return;
        }
    }

    private SidecarFormatException Error(string what)
    {
        int from = Math.Max(0, _at - 24);
        int length = Math.Min(48, _text.Length - from);
        return new SidecarFormatException(
            $"JSON at offset {_at}: {what}. Near: {_text.Substring(from, length)}");
    }
}
