// What a predicate over a dictionary column pays per batch, against what it would pay if the
// dictionary's answers were kept for the chunk.
//
// One batch of 65,536 rows whose column is a dictionary of V values, answered as the evaluator
// answers it: the predicate over the V values, then the answers spread over the rows by their codes.
// `Original` is that; `Cached` is the spreading alone, which is what every batch after the first of
// a chunk would pay if the answers were kept -- the most a cache could save, measured before one is
// built.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One predicate over one dictionary batch, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryAnswerBenchmarks
{
    /// <summary>The dictionary's values.</summary>
    [Params(1_024, 16_384, 32_768)]
    public int Values { get; set; }

    /// <summary>The predicate.</summary>
    [Params(Predicate.IntegerEqual, Predicate.TextEqual, Predicate.TextLike)]
    public Predicate Asks { get; set; }

    /// <summary>A predicate over the column.</summary>
    public enum Predicate
    {
        /// <summary><c>x = 17</c> over integers.</summary>
        IntegerEqual,

        /// <summary><c>s = 'value-17'</c> over strings.</summary>
        TextEqual,

        /// <summary><c>s LIKE '%lue-1%'</c> over strings.</summary>
        TextLike,
    }

    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _column;
    private int _values;
    private byte[] _answers = [];
    private byte[] _states = [];

    /// <summary>Builds the dictionary batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        bool text = Asks != Predicate.IntegerEqual;
        DType dtype = text ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
        _values = text ? Strings(dtype) : Integers(dtype);

        VortexBuffer codes = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(15);
        for (int row = 0; row < Rows; row++)
        {
            code[row] = (uint)random.Next(Values);
        }

        _column = _arena.AddDictionary(dtype, Rows, Validity.NonNullable, codes, _values);
        _answers = new byte[Values];
        _states = new byte[Rows];
        Answer(_answers, _values);
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The predicate over the dictionary, then its answers spread over the rows.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        Answer(_states, _column);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The answers spread over the rows, as a batch would with the dictionary's kept.</summary>
    [Benchmark]
    public int Cached()
    {
        EncodedAnswers.Expand(_arena, _column, _answers, _states);
        return Trilean.CountTrue(_states);
    }

    private void Answer(Span<byte> destination, int node)
    {
        switch (Asks)
        {
            case Predicate.IntegerEqual:
                ComparisonKernels.Compare(_arena, node, ComparisonOp.Equal, FilterLiteral.From(17L), destination);
                break;
            case Predicate.TextEqual:
                ComparisonKernels.Compare(_arena, node, ComparisonOp.Equal, FilterLiteral.From("value-17"), destination);
                break;
            default:
                ComparisonKernels.StringMatch(
                    _arena, node, StringMatchOp.Like, FilterLiteral.From("%lue-1%"), (byte)'\\', destination);
                break;
        }
    }

    private int Integers(DType dtype)
    {
        VortexBuffer buffer = _arena.Allocate(Values * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Values; i++)
        {
            values[i] = i;
        }

        return _arena.AddPrimitive(dtype, Values, Validity.NonNullable, PType.I64, buffer);
    }

    private int Strings(DType dtype)
    {
        VortexBuffer heap = _arena.Allocate(Values * 16, 1, out Span<byte> heapBytes);
        VortexBuffer views = _arena.Allocate(Values * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < Values; i++)
        {
            Span<byte> value = heapBytes.Slice(used, 16);
            "value-"u8.CopyTo(value);
            i.TryFormat(value[6..], out int digits);
            int length = 6 + digits;
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                value[..length].CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += length;
        }

        return _arena.AddVarBinView(dtype, Values, Validity.NonNullable, views, [heap]);
    }
}
