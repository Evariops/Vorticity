// A filter on a dictionary column, answered once per distinct value and spread over the rows by
// their codes: a byte of answer per row, looked up by its code.
//
// That lookup is the whole per-row cost of such a filter once the values are compared. With
// AVX-512 VBMI a dictionary of up to 128 values is a table in one or two registers and a byte
// permute answers 64 rows; past 128 the rows go one at a time. The sizes bracket both.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Answers per value spread over 65 536 rows of a dictionary node.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class DictAnswersBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Distinct values: one register, two, and more than two hold.</summary>
    [Params(16, 100, 1000)]
    public int Values { get; set; } = 16;

    /// <summary>Whether a quarter of the rows are null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private byte[] _answers = [];
    private byte[] _states = [];

    /// <summary>What one invocation reads and writes: a code and a verdict per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 5L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();

        VortexBuffer valuesBuffer = _arena.Allocate(Values * 8, 8, out Span<byte> valueBytes);
        random.NextBytes(valueBytes);
        int values = _arena.AddPrimitive(
            types.Primitive(PType.I64, Nullability.NonNullable), Values, Validity.NonNullable, PType.I64, valuesBuffer);

        VortexBuffer codesBuffer = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> codes = MemoryMarshal.Cast<byte, uint>(codeBytes);
        for (int i = 0; i < Rows; i++)
        {
            codes[i] = (uint)random.Next(Values);
        }

        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = (byte)(random.Next(256) | random.Next(256));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        _node = _arena.AddDictionary(
            types.Primitive(PType.I64, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, codesBuffer, values);
        _answers = new byte[Values];
        for (int i = 0; i < Values; i++)
        {
            _answers[i] = (byte)random.Next(2);
        }

        _states = new byte[Rows];
    }

    [Benchmark(Description = "answers by code")]
    public byte Expand()
    {
        EncodedAnswers.Expand(_arena, _node, _answers, _states);
        return _states[Rows - 1];
    }
}
