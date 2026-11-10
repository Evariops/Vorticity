// Audit CP-14: what validities cost combined, a nullable struct's over its field's (a filter on a nested field), and taken
// by row indices (a selective read's), both a row at a time before.
//
// 65,536 rows, one in ten null on each side, from a bit off a byte; one row in three taken. The nodes are laid again in a
// work arena each call over the same buffers, a few nodes against the rows.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Validities combined and taken.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditValidityMaskBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _source = null!;
    private CanonicalArena _work = null!;
    private VortexBuffer _childBits;
    private VortexBuffer _parentBits;
    private VortexBuffer _values;
    private DType _bool;
    private DType _long;
    private int[] _indices = [];

    /// <summary>Draws the validities.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(179);
        _source = new CanonicalArena(64);
        _work = new CanonicalArena(64);
        DTypeArena types = new DTypeArena();
        _bool = types.Bool(Nullability.NonNullable);
        _long = types.Primitive(PType.I64, Nullability.Nullable);
        VortexBuffer Bits()
        {
            VortexBuffer bits = _source.Allocate((Rows / 8) + 1, 8, out Span<byte> into);
            for (int row = 0; row < Rows; row++)
            {
                if (random.Next(10) != 0)
                {
                    into[(row + 3) >> 3] |= (byte)(1 << ((row + 3) & 7));
                }
            }

            return bits;
        }

        _childBits = Bits();
        _parentBits = Bits();
        _values = _source.Allocate(Rows * 8, 8, out _);
        List<int> indices = [];
        for (int row = 0; row < Rows; row += 3)
        {
            indices.Add(row);
        }

        _indices = [.. indices];
    }

    /// <summary>The field's validity under the struct's, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Masked()
    {
        _work.ResetKeepingBlocks();
        Validity own = Validity.Bitmap(_work.AddBool(_bool, Rows, Validity.NonNullable, _childBits, 3));
        int child = _work.AddPrimitive(_long, Rows, own, PType.I64, _values);
        Validity parent = Validity.Bitmap(_work.AddBool(_bool, Rows, Validity.NonNullable, _parentBits, 3));
        return FilterEvaluator.MaskedBy(_work, child, parent);
    }

    /// <summary>A validity taken at one row in three, by row taken.</summary>
    [Benchmark(OperationsPerInvoke = Rows / 3)]
    public int Taken()
    {
        _work.ResetKeepingBlocks();
        Validity parent = Validity.Bitmap(_work.AddBool(_bool, Rows, Validity.NonNullable, _parentBits, 3));
        return (int)CanonicalFilter.FilterValidity(_work, parent, _indices).Kind;
    }
}
