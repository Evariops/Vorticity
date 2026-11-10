// Audit CP-11: what an IN of a few texts costs a row of a text column no dictionary holds, each row's value hashed and
// looked up among the candidates.
//
// 65,536 rows of 50 service names of 6 to 12 bytes, all inline in their views; five candidates.
using System;
using System.Buffers.Binary;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An IN of texts over views.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBytesInSetBenchmarks
{
    private const int Rows = 65_536;

    private readonly byte[] _states = new byte[Rows];
    private CanonicalArena _arena = null!;
    private int _node;
    private BytesInSet _set = null!;

    /// <summary>Builds the column and the set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(167);
        string[] names = new string[50];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"svc-{i:D2}-" + new string('x', random.Next(0, 6));
        }

        _arena = new CanonicalArena(64);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> view);
        for (int row = 0; row < Rows; row++)
        {
            byte[] value = Encoding.ASCII.GetBytes(names[random.Next(names.Length)]);
            Span<byte> v = view.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            value.CopyTo(v[4..]);
        }

        _node = _arena.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, []);
        _set = BytesInSet.TryBuild([FilterLiteral.From(names[3]), FilterLiteral.From(names[11]), FilterLiteral.From(names[19]), FilterLiteral.From(names[27]), FilterLiteral.From(names[42])], CandidateKind.Bytes)!;
    }

    /// <summary>The rows in the set, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte In()
    {
        _set.Apply(_arena.GetNode(_node), ValidityMask.NonNullable, _states);
        return _states[^1];
    }
}
