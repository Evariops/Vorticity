// What grouping a batch by a key of two fixed-width columns packed into one word costs: each column's bits
// shifted into their place in the word, then the word looked up.
//
// Batches of 65,536 rows drawn among 1,024 tuples, or all one tuple, whose rows skip the lookup but where a null changes the word: an int and a byte (64 bits), a long and an unsigned int (96 bits, in 128),
// each with its second column nullable or not (a null in sixteen rows).
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A batch grouped by a raw key of two columns.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditRawKeysBenchmarks
{
    private const int Rows = 65_536;

    private readonly int[] _groups = new int[Rows];
    private CanonicalArena _arena = null!;
    private int[] _nodes = [];
    private GroupKeys _keys = null!;

    /// <summary>The word: 64 or 128 bits.</summary>
    [Params(64, 128)]
    public int Bits { get; set; }

    /// <summary>The tuples the rows are drawn among: one leaves the packing alone, every row's tuple the one before's.</summary>
    [Params(1, 1_024)]
    public int Tuples { get; set; }

    /// <summary>Whether the second column holds nulls.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    /// <summary>Draws the batch and groups it once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1013);
        int[] tuples = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            tuples[i] = random.Next(Tuples);
        }

        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena(Rows * 16 + Rows + 8192);
        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bits = _arena.Allocate(Rows / 8, 8, out Span<byte> into);
            into.Clear();
            for (int i = 0; i < Rows; i++)
            {
                into[i >> 3] |= (byte)((random.Next(16) == 0 ? 0 : 1) << (i & 7));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        Nullability second = Nulls ? Nullability.Nullable : Nullability.NonNullable;
        if (Bits == 64)
        {
            int a = Column(types, PType.I32, Nullability.NonNullable, Validity.NonNullable, Rows * 4, bytes =>
            {
                Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
                for (int i = 0; i < Rows; i++)
                {
                    values[i] = (tuples[i] >> 2) * 1_000_003;
                }
            });
            int b = Column(types, PType.U8, second, validity, Rows, bytes =>
            {
                for (int i = 0; i < Rows; i++)
                {
                    bytes[i] = (byte)(tuples[i] & 3);
                }
            });
            _nodes = [a, b];
            Make<ulong>([Shape(VortexType.Int32), Shape(Nulls ? VortexType.UInt8.Nullable : VortexType.UInt8)]);
        }
        else
        {
            int a = Column(types, PType.I64, Nullability.NonNullable, Validity.NonNullable, Rows * 8, bytes =>
            {
                Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < Rows; i++)
                {
                    values[i] = (tuples[i] >> 2) * 1_000_000_007L;
                }
            });
            int b = Column(types, PType.U32, second, validity, Rows * 4, bytes =>
            {
                Span<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
                for (int i = 0; i < Rows; i++)
                {
                    values[i] = (uint)(tuples[i] & 3) << 20;
                }
            });
            _nodes = [a, b];
            Make<UInt128>([Shape(VortexType.Int64), Shape(Nulls ? VortexType.UInt32.Nullable : VortexType.UInt32)]);
        }

        Assign();
    }

    /// <summary>The batch grouped, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Assign() => _keys.Assign(_arena, _nodes, Rows, [], _groups, new GroupRanges());

    private void Make<TWord>(ColumnShape[] shapes)
        where TWord : unmanaged, IEquatable<TWord>
    {
        RawLayout layout = RawLayout.Of(shapes) ?? throw new InvalidOperationException("No raw layout.");
        _keys = new RawKeys<TWord>(layout);
    }

    private int Column(DTypeArena types, PType ptype, Nullability nullability, Validity validity, int length, Action<Span<byte>> fill)
    {
        VortexBuffer buffer = _arena.Allocate(length, 8, out Span<byte> bytes);
        fill(bytes);
        return _arena.AddPrimitive(types.Primitive(ptype, nullability), Rows, validity, ptype, buffer);
    }

    private static ColumnShape Shape(VortexType type) =>
        new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));
}
