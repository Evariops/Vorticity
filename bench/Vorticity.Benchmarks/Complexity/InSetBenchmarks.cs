// What an IN over a column that is not an integer pays per candidate.
//
// `c IN (v1, ..., vL)` over one decoded batch of a utf8, float, uuid or wide decimal column.
// `Original` is the OR of equalities the kernel still falls back to, one pass over the batch per
// candidate; `Library` reads the batch against the candidates hashed once, as a scan prepares them.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One <c>IN</c> over one decoded batch, against its number of candidates.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class InSetBenchmarks
{
    /// <summary>Rows of the batch.</summary>
    [Params(65_536)]
    public int Rows { get; set; }

    /// <summary>The column's type.</summary>
    [Params(Column.Utf8, Column.F64, Column.Uuid, Column.Decimal128, Column.Decimal256)]
    public Column Of { get; set; }

    /// <summary>The candidates of the <c>IN</c>.</summary>
    [Params(2, 3, 4, 6, 16, 64, 256, 1024)]
    public int Candidates { get; set; }

    /// <summary>A column type the integer set did not cover.</summary>
    public enum Column
    {
        /// <summary>Strings of 2 to 20 bytes, half inline in their view, half in the heap.</summary>
        Utf8,

        /// <summary>Doubles.</summary>
        F64,

        /// <summary>Fixed-size lists of 16 bytes.</summary>
        Uuid,

        /// <summary>Decimals stored in 16 bytes.</summary>
        Decimal128,

        /// <summary>Decimals stored in 32 bytes.</summary>
        Decimal256,
    }

    /// <summary>The distinct values of the column, so that L candidates keep L in 4096 of its rows.</summary>
    private const int Distinct = 4096;

    private CanonicalArena _arena = null!;
    private int _column;
    private FilterLiteral[] _literals = [];
    private CandidateSet? _set;
    private byte[] _states = [];
    private byte[] _scratch = [];

    /// <summary>Builds the batch and the candidates.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        _column = Of switch
        {
            Column.Utf8 => Strings(types),
            Column.F64 => Doubles(types),
            Column.Uuid => Uuids(types),
            Column.Decimal128 => Decimals(types, DecimalStorageType.I128),
            _ => Decimals(types, DecimalStorageType.I256),
        };

        _literals = new FilterLiteral[Candidates];
        for (int k = 0; k < Candidates; k++)
        {
            _literals[k] = Literal(k * (Distinct / Candidates));
        }

        // Below the kind's threshold there is no set, and both arms are the same OR.
        _set = CandidateSet.For(_literals, ComparisonKernels.CandidatesFor(_arena, _column));
        _states = new byte[Rows];
        _scratch = new byte[Rows];
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>One equality per candidate, each over the whole batch, then their disjunction.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        ComparisonKernels.In(_arena, _column, _literals, _states, _scratch, prepared: null);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The candidates hashed once, each row looked up once.</summary>
    [Benchmark]
    public int Library()
    {
        ComparisonKernels.In(_arena, _column, _literals, _states, _scratch, _set);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The domain value row <paramref name="i"/> holds, scattered over the batch.</summary>
    private static int Row(int i) => (int)((uint)i * 2654435761u % Distinct);

    private FilterLiteral Literal(int d)
    {
        Span<byte> bytes = stackalloc byte[32];
        return Of switch
        {
            Column.Utf8 => FilterLiteral.From(bytes[..Text(d, bytes)]),
            Column.F64 => FilterLiteral.From(Double(d)),
            Column.Uuid => FilterLiteral.From(Uuid(d, bytes)),
            _ => FilterLiteral.From(Unscaled(d)),
        };
    }

    /// <summary>Domain value <paramref name="d"/> as text: short codes and long names, alternately.</summary>
    private static int Text(int d, Span<byte> destination)
    {
        if ((d & 1) == 0)
        {
            destination[0] = (byte)'c';
            d.TryFormat(destination[1..], out int written);
            return written + 1;
        }

        "customer-name-000000"u8.CopyTo(destination);
        d.TryFormat(destination.Slice(14, 6), out _, "D6");
        return 20;
    }

    private static double Double(int d) => 1000.0 + (d * 0.25);

    private static ReadOnlySpan<byte> Uuid(int d, Span<byte> destination)
    {
        ulong high = (ulong)d * 0x9E3779B97F4A7C15UL;
        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], high ^ 0xC2B2AE3D27D4EB4FUL);
        return destination[..16];
    }

    private static long Unscaled(int d) => (d * 1_000_000_007L) - 2_000_000_000_000L;

    private int Strings(DTypeArena types)
    {
        DType utf8 = types.Utf8(Nullability.NonNullable);
        VortexBuffer heap = _arena.Allocate(Rows * 20, 1, out Span<byte> heapBytes);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            Span<byte> value = heapBytes.Slice(used, 20);
            int length = Text(Row(i), value);
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                value[..length].CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += length;
        }

        return _arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [heap]);
    }

    private int Doubles(DTypeArena types)
    {
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        VortexBuffer values = _arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> doubles = MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            doubles[i] = Double(Row(i));
        }

        return _arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, values);
    }

    private int Uuids(DTypeArena types)
    {
        DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
        DType list = types.FixedSizeList(u8, 16, Nullability.NonNullable);
        VortexBuffer values = _arena.Allocate(Rows * 16, 16, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            Uuid(Row(i), bytes.Slice(i * 16, 16));
        }

        int elements = _arena.AddPrimitive(u8, Rows * 16, Validity.NonNullable, PType.U8, values);
        return _arena.AddFixedSizeList(list, Rows, Validity.NonNullable, elements, 16);
    }

    private int Decimals(DTypeArena types, DecimalStorageType storage)
    {
        int width = storage == DecimalStorageType.I128 ? 16 : Int256.ByteCount;
        byte precision = storage == DecimalStorageType.I128 ? (byte)38 : (byte)76;
        DType dtype = types.Decimal(precision, 2, Nullability.NonNullable);
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> row = bytes.Slice(i * width, width);
            if (width == 16)
            {
                BinaryPrimitives.WriteInt128LittleEndian(row, Unscaled(Row(i)));
            }
            else
            {
                new Int256(Unscaled(Row(i))).WriteLittleEndianBytes(row);
            }
        }

        return _arena.AddDecimal(dtype, Rows, Validity.NonNullable, storage, precision, 2, values);
    }
}
