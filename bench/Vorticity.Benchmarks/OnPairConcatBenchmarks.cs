using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The OnPair token concatenation alone, the bulk of an OnPair decode: the library's
/// <c>OnPairDecoder.Concatenate</c> against a frozen copy of the kernel as it was, in one process
/// and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>corpus</c> is about the per-encoding corpus file's dictionary, 300 tokens of 4 to 16 bytes;
/// <c>wide</c> 4 096 tokens of 1 to 16 bytes, near the count OnPair's dictionaries reach. The codes
/// are <c>u16</c>, spread over the tokens at random.
/// </para>
/// <para>The arms are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class OnPairConcatBenchmarks
{
    private byte[] _dictionary = [];
    private long[] _tokens = [];
    private long[] _libraryTokens = [];
    private byte[] _codes = [];
    private byte[] _output = [];

    /// <summary>The dictionary: <c>corpus</c> or <c>wide</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "corpus";

    /// <summary>Codes concatenated in one call.</summary>
    [ParamsSource(nameof(CodeCounts))]
    public int Codes { get; set; } = 2048;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["corpus", "wide"];

    /// <summary>A node whose output stays in the first-level cache, and a scan window's.</summary>
    public static IEnumerable<int> CodeCounts => [2048, 262_144];

    /// <summary>What one invocation reads: a code and its token each.</summary>
    /// <param name="method">Unused; every arm moves the same.</param>
    /// <param name="parameters">The case's codes.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int codes = (int)parameters[nameof(Codes)]!;
        return (codes, (long)codes * sizeof(ushort));
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260925);
        (int count, int shortest) = Shape == "corpus" ? (300, 4) : (4096, 1);
        int[] offsets = new int[count + 1];
        for (int t = 0; t < count; t++)
        {
            offsets[t + 1] = offsets[t] + random.Next(shortest, 17);
        }

        // The blob carries a token's worth of padding past its last, as a file's does.
        _dictionary = new byte[offsets[count] + 16];
        random.NextBytes(_dictionary);
        _tokens = new long[count];
        for (int t = 0; t < count; t++)
        {
            _tokens[t] = (uint)offsets[t] | ((long)(offsets[t + 1] - offsets[t]) << 32);
        }

        // The library's table is its own builder's, which marks the tokens near the blob's end.
        byte[] offsetBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
        _libraryTokens = new long[count];
        OnPairDecoder.BuildTokenTable(offsetBytes, PType.I32, count, _libraryTokens, _dictionary.Length);

        ushort[] codes = new ushort[Codes];
        long total = 0;
        for (int i = 0; i < Codes; i++)
        {
            codes[i] = (ushort)random.Next(count);
            total += offsets[codes[i] + 1] - offsets[codes[i]];
        }

        _codes = MemoryMarshal.AsBytes(codes.AsSpan()).ToArray();
        _output = new byte[total];
        Original();
        byte[] expected = (byte[])_output.Clone();
        _output.AsSpan().Fill(0xA5);
        Current();
        if (!_output.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Shape}: the library's concatenation differs from the original at byte {_output.AsSpan().CommonPrefixLength(expected)}."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original() =>
        OnPairConcatOriginal.Concatenate(MemoryMarshal.Cast<byte, ushort>(_codes), _tokens, _dictionary, _output);

    [Benchmark]
    public int Current() =>
        OnPairDecoder.Concatenate(_codes, PType.U16, 0, Codes, _libraryTokens, _dictionary, _output);
}
