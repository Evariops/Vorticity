// A scan that keeps most of a split's rows reads the split whole and then filters the batch down
// to the rows kept: every column rebuilt over them.
//
// Both arms are shipped: `CanonicalFilter.Apply` by the kept rows' indices, a row gathered per
// index, and by their mask too, which packs values, views and codes a register at a time and bits
// 64 an instruction. The share kept decides which reads less.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>A batch of 65 536 rows -- an i64, a nullable f64, a nullable string, an i32 -- filtered to a share of them.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class MaskFilterBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Rows kept, in eighths.</summary>
    [Params(1, 2, 4, 7)]
    public int Eighths { get; set; }

    private readonly CanonicalArena _source = new CanonicalArena();
    private readonly CanonicalArena _work = new CanonicalArena();
    private readonly DTypeArena _types = new DTypeArena();
    private VortexBuffer _longs;
    private VortexBuffer _doubles;
    private VortexBuffer _ints;
    private VortexBuffer _views;
    private VortexBuffer _doubleNulls;
    private VortexBuffer _viewNulls;
    private int[] _kept = [];
    private ulong[] _mask = [];

    /// <summary>What one invocation reads: a row of 36 bytes, the view and the three values.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * 36L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _longs = _source.Allocate(Rows * 8, 8, out Span<byte> longBytes);
        random.NextBytes(longBytes);
        _doubles = _source.Allocate(Rows * 8, 8, out Span<byte> doubleBytes);
        random.NextBytes(doubleBytes);
        _ints = _source.Allocate(Rows * 4, 4, out Span<byte> intBytes);
        random.NextBytes(intBytes);
        _views = _source.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        for (int row = 0; row < Rows; row++)
        {
            viewBytes[row * 16] = (byte)random.Next(13);
        }

        _doubleNulls = Nulls(random);
        _viewNulls = Nulls(random);

        List<int> kept = [];
        _mask = new ulong[Rows / 64];
        for (int row = 0; row < Rows; row++)
        {
            if (random.Next(8) < Eighths)
            {
                kept.Add(row);
                _mask[row >> 6] |= 1UL << (row & 63);
            }
        }

        _kept = kept.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _work.Reset();
        _source.Reset();
    }

    [Benchmark(Baseline = true, Description = "filter, indices")]
    public int Indices()
    {
        int kept = _work.GetNode(CanonicalFilter.Apply(_work, Build(), _kept)).Length;
        _work.Reset();
        return kept;
    }

    [Benchmark(Description = "filter, mask")]
    public int Mask()
    {
        int kept = _work.GetNode(CanonicalFilter.Apply(_work, Build(), _kept, _mask)).Length;
        _work.Reset();
        return kept;
    }

    /// <summary>The batch's nodes over the source's buffers.</summary>
    private int Build()
    {
        DType i64 = _types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = _types.Primitive(PType.F64, Nullability.Nullable);
        DType i32 = _types.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = _types.Utf8(Nullability.Nullable);
        DType bits = _types.Bool(Nullability.NonNullable);
        Span<int> fields =
        [
            _work.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, _longs),
            _work.AddPrimitive(f64, Rows, Validity.Bitmap(_work.AddBool(bits, Rows, Validity.NonNullable, _doubleNulls, 0)), PType.F64, _doubles),
            _work.AddVarBinView(utf8, Rows, Validity.Bitmap(_work.AddBool(bits, Rows, Validity.NonNullable, _viewNulls, 0)), _views, default),
            _work.AddPrimitive(i32, Rows, Validity.NonNullable, PType.I32, _ints),
        ];
        DType schema = _types.Struct(["a", "b", "c", "d"], [i64, f64, utf8, i32], Nullability.NonNullable);
        return _work.AddStruct(schema, Rows, Validity.NonNullable, fields);
    }

    /// <summary>A validity with a row in eight null.</summary>
    private VortexBuffer Nulls(Random random)
    {
        VortexBuffer bits = _source.Allocate(Rows / 8, 1, out Span<byte> bytes);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(random.Next(256) | random.Next(256) | random.Next(256));
        }

        return bits;
    }
}
