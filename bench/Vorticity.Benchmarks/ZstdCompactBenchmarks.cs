// The zstd candidate of a nullable primitive column lays its valid values out dense before it
// compresses them, and every nullable primitive column of every chunk tries zstd.
//
// The ported arm is the loop as it was: a validity test and a copy of the value's bytes a row. The
// shipped arm is `ZstdPlan.Compact`, which compresses a vector of rows to its valid lanes where
// AVX-512 is and writes a row whether valid or not elsewhere. Both run in one process, so tiered
// PGO is best turned off (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The valid values of 65 536 rows laid out dense: ported loop against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ZstdCompactBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Bytes per value.</summary>
    [Params(1, 4, 8)]
    public int Width { get; set; }

    private readonly CanonicalArena _arena = new CanonicalArena();
    private Validity _validity;
    private byte[] _values = [];
    private byte[] _stream = [];

    /// <summary>What one invocation reads: a value per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (long)(parameters.TryGetValue(nameof(Width), out object? w) && w is int width ? width : 4));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _values = new byte[Rows * Width];
        random.NextBytes(_values);
        _stream = new byte[(Rows * Width) + 64];
        VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
        for (int i = 0; i < valid.Length; i++)
        {
            valid[i] = (byte)(random.Next(256) | random.Next(256));
        }

        _validity = Validity.Bitmap(_arena.AddBool(new DTypeArena().Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
    }

    [Benchmark(Baseline = true, Description = "compact, ported")]
    public int Ported()
    {
        ValidityReader valid = ValidityReader.Of(_arena, _validity);
        int width = Width;
        int count = 0;
        for (int i = 0; i < Rows; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            _values.AsSpan(i * width, width).CopyTo(_stream.AsSpan(count * width, width));
            count++;
        }

        return count;
    }

    [Benchmark(Description = "compact, shipped")]
    public int Shipped()
    {
        ValidityReader valid = ValidityReader.Of(_arena, _validity);
        return Width switch
        {
            1 => ZstdPlan.Compact<byte>(_values, in valid, 0, Rows, _stream, 0),
            4 => ZstdPlan.Compact<uint>(_values, in valid, 0, Rows, _stream, 0),
            _ => ZstdPlan.Compact<ulong>(_values, in valid, 0, Rows, _stream, 0),
        };
    }
}
