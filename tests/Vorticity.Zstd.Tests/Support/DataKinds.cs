using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// The kinds of content the corpus is made of, each generated deterministically from a seed so that
/// a failing case can be rebuilt from its name alone.
/// </summary>
internal static class DataKinds
{
    public static readonly string[] All =
    [
        "walk64", "ascending32", "random64", "text", "urls", "json", "incompressible", "repeats", "zeros", "mixed",
    ];

    public static byte[] Generate(string kind, int size, int seed = 1)
    {
        var random = new Random(StableSeed(kind, size, seed));
        byte[] data = new byte[size];
        switch (kind)
        {
            case "walk64": Walk64(data, random); break;
            case "ascending32": Ascending32(data, random); break;
            case "random64": random.NextBytes(data); MaskHigh(data); break;
            case "text": Text(data, random, Words); break;
            case "urls": Urls(data, random); break;
            case "json": Json(data, random); break;
            case "incompressible": random.NextBytes(data); break;
            case "repeats": Repeats(data, random); break;
            case "zeros": break;
            case "mixed": Mixed(data, random); break;
            default: throw new ArgumentException("unknown kind " + kind, nameof(kind));
        }

        return data;
    }

    /// <summary>
    /// A seed that is the same in every process: string.GetHashCode and HashCode are randomized per
    /// process, which would make a failing case impossible to rebuild.
    /// </summary>
    private static int StableSeed(string kind, int size, int seed)
    {
        uint hash = 2166136261;
        foreach (char c in kind)
        {
            hash = (hash ^ c) * 16777619;
        }

        hash = (hash ^ (uint)size) * 16777619;
        hash = (hash ^ (uint)seed) * 16777619;
        return (int)(hash & 0x7FFFFFFF);
    }

    /// <summary>The reference data's shape: a random walk of f64 rounded to the hundredth.</summary>
    private static void Walk64(Span<byte> data, Random random)
    {
        double x = 1000.0;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            x += (random.Next(2001) - 1000) / 100.0;
            BinaryPrimitives.WriteDoubleLittleEndian(data.Slice(i), Math.Round(x * 100) / 100);
        }

        random.NextBytes(data.Slice(i));
    }

    private static void Ascending32(Span<byte> data, Random random)
    {
        uint value = (uint)random.Next(1000);
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            value += (uint)random.Next(16);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i), value);
        }

        random.NextBytes(data.Slice(i));
    }

    /// <summary>Random 64-bit integers below 2^40: the high bytes are zero, the rest is noise.</summary>
    private static void MaskHigh(Span<byte> data)
    {
        for (int i = 5; i < data.Length; i += 8)
        {
            data[i] = 0;
            if (i + 1 < data.Length)
            {
                data[i + 1] = 0;
            }

            if (i + 2 < data.Length)
            {
                data[i + 2] = 0;
            }
        }
    }

    private static readonly string[] Words =
    [
        "the", "of", "and", "to", "in", "a", "is", "that", "for", "it", "as", "was", "with", "be", "by", "on", "not",
        "he", "this", "are", "or", "his", "from", "at", "which", "but", "have", "an", "had", "they", "you", "were",
        "their", "one", "all", "we", "can", "her", "has", "there", "been", "if", "more", "when", "will", "would",
        "who", "so", "no", "compression", "entropy", "sequence", "literal", "window", "frame", "block", "stream",
        "dictionary", "Huffman", "decoder", "column", "vortex", "laminar", "turbulence", "viscosity", "Reynolds",
    ];

    private static void Text(Span<byte> data, Random random, string[] words)
    {
        var builder = new StringBuilder(data.Length + 64);
        while (builder.Length < data.Length)
        {
            int sentence = 4 + random.Next(14);
            for (int w = 0; w < sentence; w++)
            {
                string word = words[(int)(Math.Pow(random.NextDouble(), 2.5) * words.Length)];
                builder.Append(w == 0 ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word);
                builder.Append(w == sentence - 1 ? ". " : " ");
            }

            if (random.Next(8) == 0)
            {
                builder.Append('\n');
            }
        }

        Encoding.ASCII.GetBytes(builder.ToString(0, data.Length), data);
    }

    /// <summary>The first bytes of <paramref name="text"/> in UTF-8: a character may take several.</summary>
    private static void CopyUtf8(StringBuilder text, Span<byte> data) =>
        Encoding.UTF8.GetBytes(text.ToString()).AsSpan(0, data.Length).CopyTo(data);

    private static void Urls(Span<byte> data, Random random)
    {
        string[] hosts = ["example.com", "cdn.example.net", "api.vortex.dev", "github.com", "docs.microsoft.com", "www.lucca.fr"];
        string[] segments = ["users", "items", "v1", "v2", "search", "assets", "img", "static", "orders", "reports"];
        var builder = new StringBuilder(data.Length + 128);
        while (builder.Length < data.Length)
        {
            builder.Append(random.Next(4) == 0 ? "http://" : "https://").Append(hosts[random.Next(hosts.Length)]);
            int depth = 1 + random.Next(4);
            for (int d = 0; d < depth; d++)
            {
                builder.Append('/').Append(segments[random.Next(segments.Length)]);
                if (random.Next(3) == 0)
                {
                    builder.Append('/').Append(random.Next(100000).ToString(CultureInfo.InvariantCulture));
                }
            }

            if (random.Next(2) == 0)
            {
                builder.Append("?id=").Append(random.Next(1_000_000).ToString("x", CultureInfo.InvariantCulture));
            }

            builder.Append('\n');
        }

        Encoding.ASCII.GetBytes(builder.ToString(0, data.Length), data);
    }

    private static void Json(Span<byte> data, Random random)
    {
        string[] names = ["Alice", "Bob", "Chloé", "David", "Eve", "Farid", "Grace", "Hugo"];
        var builder = new StringBuilder(data.Length + 256);
        int id = random.Next(1000);
        while (builder.Length < data.Length)
        {
            id += 1 + random.Next(3);
            builder.Append("{\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
                .Append(",\"name\":\"").Append(names[random.Next(names.Length)])
                .Append("\",\"score\":").Append((random.NextDouble() * 100).ToString("F2", CultureInfo.InvariantCulture))
                .Append(",\"active\":").Append(random.Next(2) == 0 ? "true" : "false")
                .Append(",\"tags\":[\"a").Append(random.Next(10).ToString(CultureInfo.InvariantCulture))
                .Append("\",\"b").Append(random.Next(10).ToString(CultureInfo.InvariantCulture)).Append("\"]}\n");
        }

        CopyUtf8(builder, data);
    }

    /// <summary>Long repetitions at every distance: short and long periods, runs, and far copies.</summary>
    private static void Repeats(Span<byte> data, Random random)
    {
        int i = 0;
        while (i < data.Length)
        {
            int choice = random.Next(4);
            int length = Math.Min(data.Length - i, 16 + random.Next(4096));
            if (choice == 0 || i < 64)
            {
                for (int k = 0; k < length; k++)
                {
                    data[i + k] = (byte)random.Next(256);
                }
            }
            else if (choice == 1)
            {
                byte b = (byte)random.Next(256);
                data.Slice(i, length).Fill(b);
            }
            else
            {
                // A period from 1 to 64, or a far copy: both are overlapping or distant matches.
                int distance = choice == 2 ? 1 + random.Next(Math.Min(64, i)) : 1 + random.Next(i);
                for (int k = 0; k < length; k++)
                {
                    data[i + k] = data[i + k - distance];
                }
            }

            i += length;
        }
    }

    private static void Mixed(Span<byte> data, Random random)
    {
        int i = 0;
        string[] kinds = ["walk64", "text", "incompressible", "repeats", "json"];
        while (i < data.Length)
        {
            int length = Math.Min(data.Length - i, 1000 + random.Next(40000));
            Generate(kinds[random.Next(kinds.Length)], length, random.Next()).CopyTo(data.Slice(i));
            i += length;
        }
    }
}
