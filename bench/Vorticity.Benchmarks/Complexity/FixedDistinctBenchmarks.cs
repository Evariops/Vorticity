// What the writer's distinct table pays for a fixed-width column of few values that come back.
//
// Eight columns of 65,536 rows, each drawing its rows at random among `Distinct` values of its own,
// probed into the distinct table a block of 8,192 rows at a time, as the writer probes them. A row
// whose value was pushed off its slot by another misses its first compare, which random order makes
// a mispredicted branch; eight sets of values average over which ones collide, which the table's
// seed, drawn once a process, decides. `I64` draws random integers, `F64` doubles below 1,000 with a
// full mantissa; `Progression` has every row distinct, in order, as the corpus's decimals are. Run
// in a build of each version, alternately.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Fixed-width columns of few repeated values probed into the writer's distinct table.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FixedDistinctBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The rows of a block, the writer's default, which it probes at once.</summary>
    private const int BlockRows = 8_192;

    /// <summary>The columns, each with values of its own.</summary>
    private const int Columns = 8;

    /// <summary>The distinct values of each column.</summary>
    [Params(4, 16, 64, 256, 4_096)]
    public int Distinct { get; set; }

    /// <summary>The values' type.</summary>
    [Params(Kind.I64, Kind.F64, Kind.Progression)]
    public Kind Type { get; set; }

    /// <summary>A column's values.</summary>
    public enum Kind
    {
        /// <summary>Random 64-bit integers.</summary>
        I64,

        /// <summary>Doubles below 1,000 with a full mantissa.</summary>
        F64,

        /// <summary>
        /// Every row distinct, in order, a step of 1,000,003 apart, as the per-encoding corpus's
        /// decimals are; <see cref="Distinct"/> does not apply.
        /// </summary>
        Progression,
    }

    private CanonicalArena _arena = null!;
    private readonly int[] _nodes = new int[Columns];
    private readonly DistinctTable[] _tables = new DistinctTable[Columns];

    /// <summary>Builds the columns and probes each once, so its table is rented at its size.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(Type == Kind.I64 ? PType.I64 : PType.F64, Nullability.NonNullable);
        for (int c = 0; c < Columns; c++)
        {
            Random random = new Random(22 + c);
            ulong[] values = new ulong[Distinct];
            for (int i = 0; i < Distinct; i++)
            {
                values[i] = Type == Kind.I64
                    ? (ulong)random.NextInt64()
                    : BitConverter.DoubleToUInt64Bits(((ulong)random.NextInt64() >> 11) * (1.0 / (1UL << 53)) * 1000);
            }

            VortexBuffer buffer = _arena.Allocate(Rows * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
            Span<ulong> column = MemoryMarshal.Cast<byte, ulong>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                column[row] = Type == Kind.Progression
                    ? (ulong)((((long)c << 32) + row) * 1_000_003L - 7)
                    : values[random.Next(Distinct)];
            }

            _nodes[c] = _arena.AddPrimitive(dtype, Rows, Validity.NonNullable, Type == Kind.I64 ? PType.I64 : PType.F64, buffer);
            _tables[c] = DistinctTable.For(_arena.GetNode(_nodes[c]))!;
        }

        if (Probe() != Columns * (Type == Kind.Progression ? Rows : Distinct))
        {
            throw new InvalidOperationException("A column's values are not all found, or not all distinct.");
        }
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (DistinctTable table in _tables)
        {
            table.Reset();
        }

        _arena.Reset();
    }

    /// <summary>Every column probed into its table a block at a time, as the writer probes them.</summary>
    [Benchmark]
    public int Probe()
    {
        int distinct = 0;
        for (int c = 0; c < Columns; c++)
        {
            DistinctTable table = _tables[c];
            table.Reset();
            for (int start = 0; start < Rows; start += BlockRows)
            {
                table.Probe(_arena, _arena.GetNode(_nodes[c]), start, BlockRows);
            }

            distinct += table.Distinct;
        }

        return distinct;
    }
}
