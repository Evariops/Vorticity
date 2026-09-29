// What a predicate over a dictionary column pays for the dictionary's size, batch after batch.
//
// One chunk of 1,048,576 rows whose column is a dictionary of V values, decoded once and cut into
// batches as a scan cuts a retained chunk: 8,192 rows, a zone, the batches a filtered scan
// evaluates, and 65,536. `Original` answers each batch through the kernel, the predicate over the
// V values then spread over the rows when they are fewer than the batch's, over the decoded rows
// otherwise. `Library` answers each batch through the scan's evaluator, whose lane keeps the
// values' answers for the chunk; the chunk is published anew at every invocation, as when a scan
// reads it again, so its first batches answer as the kernel does.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One predicate over the batches of one dictionary chunk, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryAnswerBenchmarks
{
    /// <summary>The dictionary's values.</summary>
    [Params(1_024, 16_384, 65_536)]
    public int Values { get; set; }

    /// <summary>The rows of a batch.</summary>
    [Params(8_192, 65_536)]
    public int BatchRows { get; set; }

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

    private const int ChunkRows = 1 << 20;

    private RetainingArena _chunk = null!;
    private RetainingArena _batch = null!;
    private int _dictionary;
    private byte[] _states = [];
    private FilterLiteral _literal;
    private FilterEvaluator _evaluator = null!;

    /// <summary>Builds the chunk and the evaluator.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _chunk = new RetainingArena();
        _batch = new RetainingArena();
        bool text = Asks != Predicate.IntegerEqual;
        DType dtype = text ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
        int values = text ? Strings(dtype) : Integers(dtype);

        VortexBuffer codes = _chunk.Allocate(ChunkRows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(15);
        for (int row = 0; row < ChunkRows; row++)
        {
            code[row] = (uint)random.Next(Values);
        }

        _dictionary = _chunk.AddDictionary(dtype, ChunkRows, Validity.NonNullable, codes, values);
        _states = new byte[BatchRows];
        _literal = Asks switch
        {
            Predicate.IntegerEqual => FilterLiteral.From(17L),
            Predicate.TextEqual => FilterLiteral.From("value-17"),
            _ => FilterLiteral.From("%lue-1%"),
        };

        FieldExpr field = Expr.Field("c");
        VortexExpr filter = Asks == Predicate.TextLike
            ? Expr.Like(field, _literal)
            : Expr.Eq(field, Expr.Literal(_literal));

        _evaluator = new FilterEvaluator(filter);
    }

    /// <summary>Gives the arenas' blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _batch.Reset();
        _chunk.Reset();
    }

    /// <summary>Each batch answered through the kernel.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        int selected = 0;
        for (int start = 0; start < ChunkRows; start += BatchRows)
        {
            int node = Batch(start);
            if (Asks == Predicate.TextLike)
            {
                ComparisonKernels.StringMatch(_batch, node, StringMatchOp.Like, _literal, (byte)'\\', _states);
            }
            else
            {
                ComparisonKernels.Compare(_batch, node, ComparisonOp.Equal, _literal, _states);
            }

            selected += Trilean.CountTrue(_states);
        }

        return selected;
    }

    /// <summary>Each batch answered through the evaluator, the chunk published.</summary>
    [Benchmark]
    public int Library()
    {
        _chunk.Seal();
        int selected = 0;
        for (int start = 0; start < ChunkRows; start += BatchRows)
        {
            int node = Batch(start);
            _evaluator.EvaluateColumn(_batch, node, BatchRows, _states);
            selected += Trilean.CountTrue(_states);
        }

        return selected;
    }

    private int Batch(int start)
    {
        _batch.ResetKeepingBlocks();
        return CanonicalSlice.SliceAcross(_chunk, _batch, _dictionary, start, BatchRows);
    }

    private int Integers(DType dtype)
    {
        VortexBuffer buffer = _chunk.Allocate(Values * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Values; i++)
        {
            values[i] = i;
        }

        return _chunk.AddPrimitive(dtype, Values, Validity.NonNullable, PType.I64, buffer);
    }

    private int Strings(DType dtype)
    {
        VortexBuffer heap = _chunk.Allocate(Values * 16, 1, out Span<byte> heapBytes);
        VortexBuffer views = _chunk.Allocate(Values * 16, 16, out Span<byte> viewBytes);
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

        return _chunk.AddVarBinView(dtype, Values, Validity.NonNullable, views, [heap]);
    }
}
