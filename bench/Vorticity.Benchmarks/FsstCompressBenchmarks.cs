// The writer's FSST candidate for one text column: the training over a sample, then the compression
// of every row, which a chunk pays whether FSST wins or not.
//
// Two columns: URLs, long and alike, the kind FSST is for; and short labels, where a value is a few
// symbols and the end of each value is most of what the compressor reads.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>FsstPlan.TryBuild</c> over 65 536 strings.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class FsstCompressBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("urls", "labels")]
    public string Column { get; set; } = "urls";

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;

    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 40L);

    [GlobalSetup]
    public void Setup()
    {
        const int ViewSize = 16;
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        byte[][] encoded = new byte[Rows][];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            string text = Column == "urls"
                ? string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/{words[random.Next(8)]}/{words[random.Next(8)]}/{random.Next(1_000_000):D7}")
                : string.Create(CultureInfo.InvariantCulture, $"{words[random.Next(8)]}-{random.Next(100)}");
            encoded[i] = System.Text.Encoding.UTF8.GetBytes(text);
            heapBytes += encoded[i].Length > 12 ? encoded[i].Length : 0;
        }

        VortexBuffer views = _arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        int offset = 0;
        for (int i = 0; i < Rows; i++)
        {
            byte[] bytes = encoded[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapSpan[offset..]);
            offset += bytes.Length;
        }

        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }

    [Benchmark(Description = "fsst plan")]
    public long Build()
    {
        FsstPlan? plan = FsstPlan.TryBuild(_arena, _node, long.MaxValue);
        long size = plan?.EncodedSize ?? -1;
        plan?.Release();
        return size;
    }
}
