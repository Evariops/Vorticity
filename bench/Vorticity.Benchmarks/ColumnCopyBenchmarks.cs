// A column read into CLR values -- `CopyTo(Span<bool>)`, `CopyTo(Span<bool?>)`, `CopyTo(Span<T?>)`
// -- or a record's `ReadRows`, which copies each column out before it converts a row.
//
// The ported arm is the loop as it was: a bit test, or a validity test and a nullable written, a
// row. The shipped arm is `ColumnKernels`, which writes 64 booleans a store and a nullable's flag
// and value side by side as one wider lane. Both run in one process, so tiered PGO is best turned
// off (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks;

/// <summary>65 536 rows copied out: ported loop against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ColumnCopyBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>The CLR type copied into; nullables have a row in eight null, but for "i64? valid".</summary>
    [Params("bool", "bool?", "u8?", "i32?", "i64?", "f64?", "i64? valid")]
    public string Type { get; set; } = "bool";

    private ulong[] _bits = [];
    private ulong[] _valid = [];
    private byte[] _values = [];
    private bool[] _bools = [];
    private bool?[] _nullableBools = [];
    private byte?[] _bytes = [];
    private int?[] _ints = [];
    private long?[] _longs = [];
    private double?[] _doubles = [];

    /// <summary>What one invocation writes: a CLR value per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Type), out object? t) ? t switch { "bool" => 1L, "bool?" or "u8?" => 2L, "i32?" => 8L, _ => 16L } : 16L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _bits = new ulong[Rows / 64];
        _valid = Type == "i64? valid" ? [] : new ulong[Rows / 64];
        for (int w = 0; w < _bits.Length; w++)
        {
            _bits[w] = (ulong)random.NextInt64();
            if (_valid.Length > 0)
            {
                // Seven rows in eight valid: three words of random bits or-ed.
                _valid[w] = (ulong)random.NextInt64() | (ulong)random.NextInt64() | (ulong)random.NextInt64();
            }
        }

        _values = new byte[Rows * 8];
        random.NextBytes(_values);
        _bools = new bool[Rows];
        _nullableBools = new bool?[Rows];
        _bytes = new byte?[Rows];
        _ints = new int?[Rows];
        _longs = new long?[Rows];
        _doubles = new double?[Rows];
    }

    [Benchmark(Baseline = true, Description = "copy, ported")]
    public int Ported()
    {
        switch (Type)
        {
            case "bool":
                for (int i = 0; i < Rows; i++)
                {
                    _bools[i] = ((_bits[i >> 6] >> (i & 63)) & 1UL) != 0;
                }

                break;
            case "bool?":
                for (int i = 0; i < Rows; i++)
                {
                    _nullableBools[i] = IsValid(_valid, i) ? ((_bits[i >> 6] >> (i & 63)) & 1UL) != 0 : null;
                }

                break;
            case "u8?":
                OldNullable<byte>(_values.AsSpan(0, Rows), _valid, _bytes);
                break;
            case "i32?":
                OldNullable(MemoryMarshal.Cast<byte, int>(_values)[..Rows], _valid, _ints);
                break;
            case "f64?":
                OldNullable(MemoryMarshal.Cast<byte, double>(_values)[..Rows], _valid, _doubles);
                break;
            default:
                OldNullable(MemoryMarshal.Cast<byte, long>(_values)[..Rows], _valid, _longs);
                break;
        }

        return Rows;
    }

    [Benchmark(Description = "copy, shipped")]
    public int Shipped()
    {
        switch (Type)
        {
            case "bool":
                ColumnKernels.Bools(_bits, _bools);
                break;
            case "bool?":
                ColumnKernels.NullableBools(_valid, _bits, _nullableBools);
                break;
            case "u8?":
                ColumnKernels.Nullables<byte>(_values.AsSpan(0, Rows), _valid, _bytes);
                break;
            case "i32?":
                ColumnKernels.Nullables(MemoryMarshal.Cast<byte, int>(_values)[..Rows], _valid, _ints);
                break;
            case "f64?":
                ColumnKernels.Nullables(MemoryMarshal.Cast<byte, double>(_values)[..Rows], _valid, _doubles);
                break;
            default:
                ColumnKernels.Nullables(MemoryMarshal.Cast<byte, long>(_values)[..Rows], _valid, _longs);
                break;
        }

        return Rows;
    }

    private static bool IsValid(ReadOnlySpan<ulong> valid, int index) =>
        valid.IsEmpty || ((valid[index >> 6] >> (index & 63)) & 1UL) != 0;

    /// <summary>`ColumnData.CopyNullable` as it was.</summary>
    private static void OldNullable<T>(ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, Span<T?> into)
        where T : unmanaged
    {
        if (valid.IsEmpty)
        {
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = values[i];
            }

            return;
        }

        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? values[i] : null;
        }
    }
}
