// What laying a nullable column's valid values end to end before zstd costs a row, on the vector widths a
// machine has.
//
// 65,536 nullable longs, one in ten null. Run as it is (AVX-512 here: vpcompress), and under DOTNET_EnableAVX512=0 (AVX2: a
// row at a time, a store and an add).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A nullable column's valid values laid end to end, per row.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditZstdCompactBenchmarks
{
    private const int Rows = 65_536;

    private readonly CanonicalArena _arena = new CanonicalArena();
    private readonly byte[] _source = new byte[Rows * sizeof(long)];
    private readonly byte[] _stream = new byte[(Rows * sizeof(long)) + 128];
    private Validity _validity;

    /// <summary>Draws the values and their nulls.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(13);
        random.NextBytes(_source);
        VortexBuffer bits = _arena.Allocate(Rows / 8, 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < Rows; i++)
        {
            if (random.Next(10) != 0)
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        DTypeArena types = new DTypeArena();
        _validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        Console.WriteLine($"// {Compact()} values kept");
    }

    /// <summary>The valid values laid end to end.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Compact()
    {
        ValidityReader valid = ValidityReader.Of(_arena, _validity);
        return ZstdPlan.Compact<long>(_source, in valid, 0, Rows, _stream, 0);
    }
}
