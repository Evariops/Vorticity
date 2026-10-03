using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// One frame of the differential corpus, named by everything needed to rebuild it:
/// <c>kind/size/level</c> then flags — <c>chk</c> (content checksum), <c>dict=trained|raw</c>,
/// <c>wlog=N</c> (window log), <c>ldm</c> (long-distance matching), <c>block=N</c> (target block size),
/// <c>nofcs</c> (streamed in chunks, so that the header declares no content size).
/// </summary>
internal sealed record CorpusCase(
    string Kind, int Size, int Level, bool Checksum, string? Dictionary, int WindowLog, bool LongDistance,
    int BlockSize, bool NoContentSize)
{
    public static CorpusCase Parse(string name)
    {
        string[] parts = name.Split('/');
        string? dictionary = null;
        bool checksum = false, ldm = false, noFcs = false;
        int windowLog = 0, blockSize = 0;
        for (int i = 3; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part == "chk") checksum = true;
            else if (part == "ldm") ldm = true;
            else if (part == "nofcs") noFcs = true;
            else if (part.StartsWith("dict=", StringComparison.Ordinal)) dictionary = part[5..];
            else if (part.StartsWith("wlog=", StringComparison.Ordinal)) windowLog = int.Parse(part[5..], CultureInfo.InvariantCulture);
            else if (part.StartsWith("block=", StringComparison.Ordinal)) blockSize = int.Parse(part[6..], CultureInfo.InvariantCulture);
            else throw new ArgumentException("unknown flag " + part, nameof(name));
        }

        return new CorpusCase(
            parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2][1..], CultureInfo.InvariantCulture),
            checksum, dictionary, windowLog, ldm, blockSize, noFcs);
    }

    public static string Name(string kind, int size, int level, params string[] flags) =>
        string.Join('/', [kind, size.ToString(CultureInfo.InvariantCulture), "L" + level.ToString(CultureInfo.InvariantCulture), .. flags]);

    public byte[] Data => DataKinds.Generate(Kind, Size);

    /// <summary>The dictionary bytes as the decoder receives them, and the platform's own object.</summary>
    public (byte[] Bytes, ZstandardDictionary Native)? GetDictionary() => Dictionary is null ? null : Dictionaries.Get(Kind, Dictionary);

    /// <summary>Compresses <see cref="Data"/> with the platform's libzstd.</summary>
    public byte[] Compress(byte[] data)
    {
        var options = new ZstandardCompressionOptions { Quality = Level, AppendChecksum = Checksum };
        if (WindowLog > 0) options.WindowLog2 = WindowLog;
        if (LongDistance) options.EnableLongDistanceMatching = true;
        if (BlockSize > 0) options.TargetBlockSize = BlockSize;
        if (GetDictionary() is { } dictionary) options.Dictionary = dictionary.Native;
        return NativeZstd.Compress(data, options, NoContentSize);
    }
}

/// <summary>Dictionaries per data kind, built once: trained on samples, or raw content.</summary>
internal static class Dictionaries
{
    private static readonly ConcurrentDictionary<string, (byte[], ZstandardDictionary)> Cache = new();

    public static (byte[] Bytes, ZstandardDictionary Native) Get(string kind, string type) =>
        Cache.GetOrAdd(kind + "/" + type, _ => Build(kind, type));

    private static (byte[], ZstandardDictionary) Build(string kind, string type)
    {
        switch (type)
        {
            case "trained":
            {
                const int sampleCount = 200;
                const int sampleSize = 1024;
                byte[] samples = new byte[sampleCount * sampleSize];
                int[] lengths = new int[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    DataKinds.Generate(kind, sampleSize, 1000 + i).CopyTo(samples, i * sampleSize);
                    lengths[i] = sampleSize;
                }

                try
                {
                    ZstandardDictionary trained = ZstandardDictionary.Train(samples, lengths, 16 * 1024);
                    return (trained.Data.ToArray(), trained);
                }
                catch (Exception)
                {
                    // Some kinds (zeros, noise) give the trainer nothing to learn: fall back on a
                    // dictionary trained on text, which is still a valid zstd-format dictionary.
                    return kind == "text" ? throw new InvalidOperationException("cannot train on text") : Get("text", "trained");
                }
            }

            case "raw":
            {
                byte[] raw = DataKinds.Generate(kind, 32 * 1024, 7);
                ZstandardDictionary native = ZstandardDictionary.Create(raw);
                return (raw, native);
            }

            default:
                throw new ArgumentException("unknown dictionary type " + type, nameof(type));
        }
    }
}

/// <summary>The platform's libzstd, through System.IO.Compression: the oracle.</summary>
internal static class NativeZstd
{
    public static byte[] Compress(ReadOnlySpan<byte> data, ZstandardCompressionOptions options, bool noContentSize = false)
    {
        using var encoder = new ZstandardEncoder(options);
        byte[] output = new byte[data.Length + (data.Length >> 7) + 1024];
        int total = 0;
        if (noContentSize)
        {
            // Fed in chunks, the encoder cannot know the size when it writes the header.
            const int chunk = 64 * 1024;
            int offset = 0;
            do
            {
                int length = Math.Min(chunk, data.Length - offset);
                bool final = offset + length == data.Length;
                var status = encoder.Compress(data.Slice(offset, length), output.AsSpan(total), out int consumed, out int written, final);
                if (status != System.Buffers.OperationStatus.Done || consumed != length)
                {
                    throw new InvalidOperationException("compression failed: " + status);
                }

                total += written;
                offset += length;
            }
            while (offset < data.Length);
        }
        else
        {
            var status = encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true);
            if (status != System.Buffers.OperationStatus.Done || consumed != data.Length)
            {
                throw new InvalidOperationException("compression failed: " + status);
            }

            total = written;
        }

        return output.AsSpan(0, total).ToArray();
    }

    public static System.Buffers.OperationStatus Decompress(
        ReadOnlySpan<byte> frame, Span<byte> destination, ZstandardDictionary? dictionary, out int consumed, out int written)
    {
        using var decoder = dictionary is null ? new ZstandardDecoder() : new ZstandardDecoder(dictionary);
        return decoder.Decompress(frame, destination, out consumed, out written);
    }

    public static IEnumerable<string> Levels => ["-5", "-1", "1", "3", "9", "19", "22"];
}
