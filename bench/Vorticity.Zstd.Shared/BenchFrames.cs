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
        _ => throw new ArgumentException("unknown frame " + name, nameof(name)),
    };

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
