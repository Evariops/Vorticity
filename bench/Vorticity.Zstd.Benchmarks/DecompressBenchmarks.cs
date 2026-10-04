using System;
using System.Collections.Generic;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using Vorticity.Zstd.Bench;

namespace Vorticity.Zstd.Benchmarks;

/// <summary>
/// One frame decoded by each candidate. The platform's <see cref="ZstandardDecoder"/> is the
/// baseline; the speedup column reads as its time divided by the candidate's.
/// </summary>
public class DecompressBenchmarks
{
    private byte[] _frame = [];
    private byte[] _output = [];
    private ZstdDecompressor _zstd = null!;
    private ZstandardDecoder _platform = null!;
    private NativeReference? _native;

    [ParamsSource(nameof(Frames))]
    public string Frame { get; set; } = "reference";

    public static IEnumerable<string> Frames => BenchFrames.Names;

    [GlobalSetup]
    public void Setup()
    {
        _frame = BenchFrames.Load(Frame);
        _output = new byte[BenchFrames.ContentSize(_frame)];
        _zstd = new ZstdDecompressor();
        _platform = new ZstandardDecoder();
        _native = NativeReference.TryLoad();
    }

    [GlobalCleanup]
    public void Cleanup() => _platform.Dispose();

    [Benchmark(Baseline = true)]
    public int Platform()
    {
        _platform.Reset();
        _platform.Decompress(_frame, _output, out _, out int written);
        return written;
    }

    [Benchmark]
    public int Zstd()
    {
        _zstd.Reset();
        _zstd.Decompress(_frame, _output, out _, out int written);
        return written;
    }

    [Benchmark]
    public int NativeRef() => _native is null ? throw new InvalidOperationException("run tools/native-ref/build.sh") : _native.Decompress(_frame, _output);
}
