// What an equality costs on a text column that no dictionary holds, its rows views of 16 bytes, a value of up
// to 12 bytes inline in its view.
//
// 65,536 rows of 50 service names of 6 to 12 bytes, one name in fifty equal to the literal; and a literal of 20 bytes,
// out of line, against rows of 20 bytes.
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

/// <summary>An equality over text views.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditEqualViewsBenchmarks
{
    private const int Rows = 65_536;

    private readonly byte[] _states = new byte[Rows];
    private CanonicalArena _arena = null!;
    private int _node;
    private FilterLiteral _literal;

    /// <summary>The values' length: inline (12 at most) or out of line (20).</summary>
    [Params(12, 20)]
    public int Longest { get; set; }

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(131);
        string[] names = new string[50];
        for (int i = 0; i < names.Length; i++)
        {
            string name = $"svc-{i:D2}-" + new string('x', random.Next(0, Longest - 6));
            names[i] = Longest > 12 ? name.PadRight(20, 'y') : name;
        }

        _arena = new CanonicalArena(64);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> view);
        VortexBuffer heap = _arena.Allocate(Rows * 20, 16, out Span<byte> bytes);
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            byte[] value = Encoding.ASCII.GetBytes(names[random.Next(names.Length)]);
            Span<byte> v = view.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(v[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(v[4..]);
                BinaryPrimitives.WriteInt32LittleEndian(v[8..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(v[12..], at);
                value.CopyTo(bytes[at..]);
                at += value.Length;
            }
        }

        DTypeArena types = new DTypeArena();
        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
        _literal = FilterLiteral.From(names[7]);
    }

    /// <summary>The rows equal to the literal, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Equal()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Equal, _literal, _states);
        return _states[^1];
    }
}
