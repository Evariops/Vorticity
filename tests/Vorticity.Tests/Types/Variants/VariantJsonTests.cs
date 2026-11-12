// A variant read from JSON text: the bytes FromJson writes in two passes without a tree are the bytes a tree of the
// document's nodes writes, written level by level (the way FromJson wrote them first, kept here as the reference), on
// documents of every shape; text that is not one JSON document of UTF-8 is refused.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class VariantJsonTests
{
    /// <summary>
    /// Random documents of nested objects and arrays, keys out of order, repeated across objects, escaped and of any
    /// script, strings short and long, escaped or not, integers of every width, integers past 64 bits, doubles, booleans
    /// and nulls: the same metadata and value bytes as the tree writes.
    /// </summary>
    [Fact]
    public void JsonIsWrittenAsATreeOfItsNodesWritesIt()
    {
        Random random = new Random(17);
        for (int document = 0; document < 2_000; document++)
        {
            StringBuilder json = new();
            Document(random, json, random.Next(6));
            byte[] utf8 = Encoding.UTF8.GetBytes(json.ToString());
            (byte[] metadata, byte[] value) = VariantEncoding.FromJson(utf8);
            (byte[] expectedMetadata, byte[] expectedValue) = TreeReference.FromJson(utf8);
            Assert.Equal(expectedMetadata, metadata);
            Assert.Equal(expectedValue, value);
        }
    }

    /// <summary>Objects and arrays of more than 255 members, and values whose sizes take wider offsets.</summary>
    [Fact]
    public void WideContainersAreWrittenAsTheTreeWritesThem()
    {
        StringBuilder json = new("{");
        for (int i = 0; i < 700; i++)
        {
            _ = json.Append(i == 0 ? "" : ",").Append(CultureInfo.InvariantCulture, $"\"k{699 - i:D4}\":[\"{new string('v', i % 90)}\",{i * 1_000_003L}]");
        }

        _ = json.Append(",\"big\":\"").Append(new string('b', 70_000)).Append("\"}");
        byte[] utf8 = Encoding.UTF8.GetBytes(json.ToString());
        (byte[] metadata, byte[] value) = VariantEncoding.FromJson(utf8);
        (byte[] expectedMetadata, byte[] expectedValue) = TreeReference.FromJson(utf8);
        Assert.Equal(expectedMetadata, metadata);
        Assert.Equal(expectedValue, value);
    }

    /// <summary>A repeated key, an empty text, two values, a cut document, and strings or keys not UTF-8, escaped or not.</summary>
    [Fact]
    public void TextThatIsNotOneDocumentOfUtf8IsRefused()
    {
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson("""{"b":1,"a":{"x":1,"x":2}}"""u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson("""{"a":1,"b":2,"a":3}"""u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson(""u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson("  "u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson("1 2"u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson("""{"a":[1,2}"""u8));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson([(byte)'"', 0xFF, (byte)'"']));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson([(byte)'"', (byte)'\\', (byte)'n', 0xC3, (byte)'"']));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson([(byte)'{', (byte)'"', 0xFE, (byte)'"', (byte)':', (byte)'1', (byte)'}']));
        Assert.Throws<ArgumentException>(() => VariantEncoding.FromJson([(byte)'{', (byte)'"', (byte)'\\', (byte)'t', 0xFE, (byte)'"', (byte)':', (byte)'1', (byte)'}']));

        // A key repeated in two objects is not a repeat, nor two keys one escaped.
        (byte[] metadata, _) = VariantEncoding.FromJson("""{"a":{"a":1},"b":{"a":2},"c":3,"cd":4}"""u8);
        Assert.Equal(4, VariantDictionary.Read(metadata).Count);
    }

    private static void Document(Random random, StringBuilder json, int depth)
    {
        switch (depth == 0 ? 2 + random.Next(7) : random.Next(9))
        {
            case 0:
            {
                // An object, its keys in no order.
                int count = random.Next(depth > 3 ? 4 : 12);
                HashSet<string> keys = [];
                _ = json.Append('{');
                for (int i = 0; i < count; i++)
                {
                    string key = Key(random);
                    if (!keys.Add(key))
                    {
                        continue;
                    }

                    _ = json.Append(keys.Count == 1 ? "" : ",").Append(JsonSerializer.Serialize(key)).Append(':');
                    Document(random, json, Math.Max(0, depth - 1));
                }

                _ = json.Append('}');
                break;
            }

            case 1:
            {
                int count = random.Next(8);
                _ = json.Append('[');
                for (int i = 0; i < count; i++)
                {
                    _ = json.Append(i == 0 ? "" : ",");
                    Document(random, json, Math.Max(0, depth - 1));
                }

                _ = json.Append(']');
                break;
            }

            case 2:
                _ = json.Append(JsonSerializer.Serialize(Text(random)));
                break;
            case 3:
                // Escaped as JSON may write it: \u for any character.
                _ = json.Append('"');
                foreach (char c in Text(random))
                {
                    _ = json.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                }

                _ = json.Append('"');
                break;
            case 4:
            {
                long[] widths = [0, 1, -1, 127, -128, 128, 300, -32_769, 70_000, int.MaxValue, int.MinValue, 5_000_000_000, long.MaxValue, long.MinValue];
                _ = json.Append(widths[random.Next(widths.Length)] + random.Next(3) - 1);
                break;
            }

            case 5:
                _ = json.Append(random.Next(2) == 0 ? "123456789012345678901234567890" : "-99999999999999999999999999999999999999");
                break;
            case 6:
                _ = json.Append((random.NextDouble() * 1e6).ToString("R", CultureInfo.InvariantCulture)).Append(random.Next(2) == 0 ? "" : "e-3");
                break;
            case 7:
                _ = json.Append(random.Next(2) == 0 ? "true" : "false");
                break;
            default:
                _ = json.Append("null");
                break;
        }
    }

    private static string Key(Random random)
    {
        string[] keys = ["a", "b", "z", "", "route", "ünïcode", "日本", "http.route", "k0", "k1", "\"q\"", "tab\t", "long-key-" + new string('x', 40)];
        return keys[random.Next(keys.Length)];
    }

    private static string Text(Random random)
    {
        string[] texts = ["", "x", "checkout", "é", "日本語", "line\nbreak", "quote\"d", new string('s', 63), new string('s', 64), new string('ü', 40), new string('l', 300)];
        return texts[random.Next(texts.Length)];
    }

    /// <summary>The reference: the document parsed into a tree of nodes, then written node by node under its dictionary.</summary>
    private static class TreeReference
    {
        internal static (byte[] Metadata, byte[] Value) FromJson(ReadOnlySpan<byte> utf8)
        {
            Utf8JsonReader reader = new(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            _ = reader.Read();
            Node root = Parse(ref reader);
            SortedSet<byte[]> names = new(VariantEncoding.KeyOrder);
            root.Collect(names);
            List<byte[]> keys = [.. names];
            return (VariantEncoding.Metadata(keys), root.Write(keys));
        }

        private static Node Parse(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                {
                    List<(byte[] Key, Node Value)> fields = [];
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        byte[] key = Encoding.UTF8.GetBytes(reader.GetString()!);
                        _ = reader.Read();
                        fields.Add((key, Parse(ref reader)));
                    }

                    fields.Sort(static (a, b) => VariantEncoding.KeyOrder.Compare(a.Key, b.Key));
                    return new Node(null, fields, null);
                }

                case JsonTokenType.StartArray:
                {
                    List<Node> elements = [];
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        elements.Add(Parse(ref reader));
                    }

                    return new Node(null, null, elements);
                }

                case JsonTokenType.String:
                    return new Node(VariantEncoding.String(Encoding.UTF8.GetBytes(reader.GetString()!)), null, null);
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long integer))
                    {
                        return new Node(VariantEncoding.Integer(integer), null, null);
                    }

                    ReadOnlySpan<byte> text = reader.ValueSpan;
                    return text.IndexOfAny(".eE"u8) < 0 && Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out Int128 wide)
                        ? new Node(VariantEncoding.Decimal(wide, 0), null, null)
                        : new Node(VariantEncoding.Double(reader.GetDouble()), null, null);
                case JsonTokenType.True:
                    return new Node(VariantEncoding.Bool(true), null, null);
                case JsonTokenType.False:
                    return new Node(VariantEncoding.Bool(false), null, null);
                default:
                    return new Node(VariantEncoding.NullValue, null, null);
            }
        }

        private sealed class Node(byte[]? primitive, List<(byte[] Key, Node Value)>? fields, List<Node>? elements)
        {
            internal void Collect(SortedSet<byte[]> names)
            {
                foreach ((byte[] key, Node value) in fields ?? [])
                {
                    _ = names.Add(key);
                    value.Collect(names);
                }

                foreach (Node element in elements ?? [])
                {
                    element.Collect(names);
                }
            }

            internal byte[] Write(List<byte[]> keys)
            {
                if (primitive is not null)
                {
                    return primitive;
                }

                if (elements is not null)
                {
                    List<byte[]> written = new(elements.Count);
                    foreach (Node element in elements)
                    {
                        written.Add(element.Write(keys));
                    }

                    return VariantEncoding.Array(written);
                }

                List<(int Id, byte[] Value)> members = new(fields!.Count);
                foreach ((byte[] key, Node value) in fields)
                {
                    members.Add((keys.BinarySearch(key, VariantEncoding.KeyOrder), value.Write(keys)));
                }

                return VariantEncoding.Object(members);
            }
        }
    }
}
