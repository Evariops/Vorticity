// Audit AG-10: what grouping a batch by a key of two columns costs when the tuple of their numbers indexes a table of
// groups: each column numbered by its own index, then each row's slot made of its numbers, then the table read.
//
// Batches of 65,536 rows of two int columns, each drawn among 32 values, or all one tuple; the columns bound by
// statistics, as a file's are, or not.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A batch grouped by a packed key of two columns through its table.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPackedKeysBenchmarks
{
    private const int Rows = 65_536;

    private readonly int[] _groups = new int[Rows];
    private CanonicalArena _arena = null!;
    private int[] _nodes = [];
    private GroupKeys _keys = null!;

    /// <summary>The values each column is drawn among.</summary>
    [Params(1, 32)]
    public int Values { get; set; }

    /// <summary>Whether the statistics bound each column, which numbers its values directly, as a file's do.</summary>
    [Params(false, true)]
    public bool Bounded { get; set; }

    /// <summary>Draws the batch and groups it once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1014);
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(PType.I32, Nullability.NonNullable);
        _arena = new CanonicalArena(Rows * 8 + 8192);
        int[] nodes = new int[2];
        for (int c = 0; c < 2; c++)
        {
            VortexBuffer buffer = _arena.Allocate(Rows * 4, 8, out Span<byte> bytes);
            Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < Rows; i++)
            {
                values[i] = random.Next(Values);
            }

            nodes[c] = _arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I32, buffer);
        }

        _nodes = nodes;
        ColumnShape Shape(string name) => new ColumnShape(new ColumnSym(Expr.Field(name), VortexType.Int32, null, null, -1, []));
        KeyFacts? facts = Bounded ? new KeyFacts([false, false], [new KeyBounds(0, 31), new KeyBounds(0, 31)]) : null;
        _keys = new PackedKeys<ulong>([Shape("a"), Shape("b")], facts);
        Assign();
    }

    /// <summary>The batch grouped, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Assign() => _keys.Assign(_arena, _nodes, Rows, [], _groups, new GroupRanges());
}
