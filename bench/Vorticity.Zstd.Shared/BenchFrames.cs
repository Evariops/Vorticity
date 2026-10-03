using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Vorticity.Zstd.Tests.Support;

namespace Vorticity.Zstd.Bench;

/// <summary>
/// The frames the benchmarks decode. <c>reference</c> is the frame the native harness writes
/// (tools/native-ref): 65,536 f64 of a random walk, level 3. The others are generated
/// deterministically and compressed with the platform's libzstd, so they are the same bytes run after run.
/// </summary>
internal static class BenchFrames
{
    public static readonly string[] Names =
    [
        "reference", "text-L3", "json-L19", "urls-L1", "repeats-L3", "incompressible", "small-json-L3",
    ];

    public static byte[] Load(string name) => name switch
    {
        "reference" => File.ReadAllBytes(Path.Combine(RepositoryRoot, "tests", "Vorticity.Zstd.Tests", "testdata", "reference.zst")),
        "text-L3" => Compress("text", 1 << 20, 3),
        "json-L19" => Compress("json", 256 << 10, 19),
        "urls-L1" => Compress("urls", 1 << 20, 1),
        "repeats-L3" => Compress("repeats", 1 << 20, 3),
        "incompressible" => Compress("incompressible", 256 << 10, 3),
        "small-json-L3" => Compress("json", 4 << 10, 3),
        _ => Generic(name),
    };

    /// <summary>
    /// Any other kind at any level, 1 MiB: <c>kind-L&lt;level&gt;</c>; 16 KiB with a <c>small-</c>
    /// prefix, the size up to which libzstd's lazy levels search hash chains rather than rows; 1 KiB
    /// with a <c>tiny-</c> prefix, a record of the size dictionaries are made for; 80 MiB with a
    /// <c>big-</c> prefix, past which libzstd's level 22 turns its long-distance matcher on (that
    /// frame is compressed at level 1: it only carries the content).
    /// </summary>
    private static byte[] Generic(string name)
    {
        int dash = name.LastIndexOf("-L", StringComparison.Ordinal);
        if (dash <= 0 || !int.TryParse(name.AsSpan(dash + 2), out int level))
        {
            throw new ArgumentException("unknown frame " + name, nameof(name));
        }

        string kind = name[..dash];
        return kind.StartsWith("small-", StringComparison.Ordinal) ? Compress(kind["small-".Length..], 16 << 10, level)
            : kind.StartsWith("tiny-", StringComparison.Ordinal) ? Compress(kind["tiny-".Length..], 1 << 10, level)
            : kind.StartsWith("big-", StringComparison.Ordinal) ? Compress(kind["big-".Length..], 80 << 20, 1)
            : Compress(kind, 1 << 20, level);
    }

    /// <summary>
    /// A dictionary for a frame's content, by the frame's name: 16 KiB trained on 200 records of 1 KiB
    /// of its kind (other seeds than the content's), or 32 KiB of raw content for the kinds the trainer
    /// learns nothing from.
    /// </summary>
    public static byte[] Dictionary(string name)
    {
        int dash = name.LastIndexOf("-L", StringComparison.Ordinal);
        string kind = dash > 0 ? name[..dash] : throw new ArgumentException("no kind in " + name, nameof(name));
        foreach (string prefix in new[] { "small-", "tiny-", "big-" })
        {
            kind = kind.StartsWith(prefix, StringComparison.Ordinal) ? kind[prefix.Length..] : kind;
        }

        const int Samples = 200;
        byte[] samples = new byte[Samples * 1024];
        int[] lengths = new int[Samples];
        for (int i = 0; i < Samples; i++)
        {
            DataKinds.Generate(kind, 1024, 1000 + i).CopyTo(samples, i * 1024);
            lengths[i] = 1024;
        }

        try
        {
            using ZstandardDictionary trained = ZstandardDictionary.Train(samples, lengths, 16 << 10);
            return trained.Data.ToArray();
        }
        catch (Exception)
        {
            return DataKinds.Generate(kind, 32 << 10, 7);
        }
    }

    /// <summary>
    /// What a name means for compression: the content of its frame, and the level it was compressed
    /// at (3 for the reference frame, whose name has none).
    /// </summary>
    public static (byte[] Content, int Level) LoadContent(string name)
    {
        byte[] frame = Load(name);
        byte[] content = new byte[ContentSize(frame)];
        using (var decoder = new ZstandardDecoder())
        {
            decoder.Decompress(frame, content, out _, out _);
        }

        int dash = name.LastIndexOf("-L", StringComparison.Ordinal);
        int level = dash > 0 && int.TryParse(name.AsSpan(dash + 2), out int parsed) ? parsed : 3;
        return (content, level);
    }

    /// <summary>The content size a frame declares: every benchmark frame declares one.</summary>
    public static int ContentSize(byte[] frame) =>
        ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size) ? checked((int)size) : throw new InvalidDataException("no content size");

    private static byte[] Compress(string kind, int size, int level)
    {
        byte[] data = DataKinds.Generate(kind, size);
        byte[] output = new byte[size + (size >> 7) + 1024];
        if (!ZstandardEncoder.TryCompress(data, output, out int written, level, ZstandardCompressionOptions.DefaultWindowLog2))
        {
            throw new InvalidOperationException("compression failed");
        }

        return output.AsSpan(0, written).ToArray();
    }

    public static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot([CallerFilePath] string here = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(here) ?? "", Environment.CurrentDirectory })
        {
            string? directory = start;
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "Vorticity.Zstd.slnx")))
                {
                    return directory;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        throw new InvalidOperationException("repository root (Vorticity.Zstd.slnx) not found");
    }
}
