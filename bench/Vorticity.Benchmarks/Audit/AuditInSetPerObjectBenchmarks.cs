// Audit SC-03: what an IN of a thousand texts costs each object a question reads, an evaluator made for each object's scan
// and the candidates hashed for it, over a batch of 1,024 rows.
using System;
using System.Buffers.Binary;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An IN evaluated by an evaluator per object.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditInSetPerObjectBenchmarks
{
    private const int Rows = 1_024;

    private readonly byte[] _states = new byte[Rows];
    private CanonicalArena _arena = null!;
    private int _root;
    private VortexExpr _filter = null!;

    /// <summary>Builds the batch and the filter.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(211);
        _arena = new CanonicalArena(64);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> view);
        for (int row = 0; row < Rows; row++)
        {
            byte[] value = Encoding.ASCII.GetBytes($"user-{random.Next(5_000):D5}");
            BinaryPrimitives.WriteInt32LittleEndian(view.Slice(row * 16), value.Length);
            value.CopyTo(view.Slice((row * 16) + 4));
        }

        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        int column = _arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, []);
        _root = _arena.AddStruct(types.Struct(["c"], [utf8], Nullability.NonNullable), Rows, Validity.NonNullable, [column]);
        FilterLiteral[] members = new FilterLiteral[1_000];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = FilterLiteral.From($"user-{i * 5:D5}");
        }

        _filter = new InExpr(new ColumnFieldExpr("c"), members);
    }

    /// <summary>An object's evaluator made and the batch evaluated.</summary>
    [Benchmark]
    public byte PerObject()
    {
        new FilterEvaluator(_filter).Evaluate(_arena, _root, Rows, _states);
        return _states[^1];
    }
}
