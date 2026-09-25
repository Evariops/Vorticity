using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The scatter a zstd decode of a nullable primitive column ends with, alone: the dense valid
/// values spread over the rows, a null as zero. The library's <c>ZstdDecoder.Expand</c> against a
/// frozen copy of it, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>u64-seventh</c> is 64-bit values with one row in seven null, the corpus's nullable zstd
/// column; <c>u64-random</c> half the rows null at random; <c>u32-quarter</c> 32-bit values with
/// one row in four null; <c>u64-sparse</c> one row in a hundred null.
/// </para>
/// <para>The arms' rows are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ZstdExpandBenchmarks
{
    private CanonicalArena _arena = null!;
    private Validity _validity;
    private byte[] _source = [];
    private byte[] _destination = [];

    /// <summary>The column: <c>u64-seventh</c>, <c>u64-random</c>, <c>u32-quarter</c> or <c>u64-sparse</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "u64-seventh";

    /// <summary>Rows of the column.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["u64-seventh", "u64-random", "u32-quarter", "u64-sparse"];

    /// <summary>A small window, and a large one.</summary>
    public static IEnumerable<int> RowCounts => [8192, 131_072];

    /// <summary>What one invocation writes: a value a row.</summary>
    /// <param name="method">Unused; every arm writes the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ((string)parameters[nameof(Shape)]! == "u32-quarter" ? 4 : 8));
    }

    private int Width => Shape == "u32-quarter" ? 4 : 8;

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
        int valid = 0;
        for (int row = 0; row < Rows; row++)
        {
            bool on = Shape switch
            {
                "u64-seventh" => row % 7 != 3,
                "u32-quarter" => row % 4 != 1,
                "u64-sparse" => row % 100 != 42,
                _ => random.Next(2) == 0,
            };
            if (on)
            {
                bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                valid++;
            }
        }

        _validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        _source = new byte[valid * Width];
        random.NextBytes(_source);
        _destination = new byte[Rows * Width];

        byte[] expected = new byte[Rows * Width];
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        if (Width == 8)
        {
            Frozen<ulong>(_source, expected, in mask, Rows);
            ZstdDecoder.Expand<ulong>(_source, _destination, in mask, Rows);
        }
        else
        {
            Frozen<uint>(_source, expected, in mask, Rows);
            ZstdDecoder.Expand<uint>(_source, _destination, in mask, Rows);
        }

        if (!expected.AsSpan().SequenceEqual(_destination))
        {
            throw new InvalidOperationException($"{Shape}: the library's rows differ from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public byte Original()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        if (Width == 8)
        {
            Frozen<ulong>(_source, _destination, in mask, Rows);
        }
        else
        {
            Frozen<uint>(_source, _destination, in mask, Rows);
        }

        return _destination[^1];
    }

    [Benchmark]
    public byte Current()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        if (Width == 8)
        {
            ZstdDecoder.Expand<ulong>(_source, _destination, in mask, Rows);
        }
        else
        {
            ZstdDecoder.Expand<uint>(_source, _destination, in mask, Rows);
        }

        return _destination[^1];
    }

    /// <summary>A frozen copy of the scatter as it was: a mixed word walks its set bits.</summary>
    private static void Frozen<T>(ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length)
        where T : unmanaged
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> rows = MemoryMarshal.Cast<byte, T>(destination);
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        int next = 0;
        for (int row = 0; row < length; row += 64)
        {
            int span = Math.Min(64, length - row);
            ulong full = BitWords.Mask(span);
            ulong word = BitWords.Load(bits, bitOffset + row) & full;
            if (word == full)
            {
                values.Slice(next, span).CopyTo(rows.Slice(row, span));
                next += span;
                continue;
            }

            while (word != 0)
            {
                rows[row + BitOperations.TrailingZeroCount(word)] = values[next++];
                word &= word - 1;
            }
        }
    }
}
