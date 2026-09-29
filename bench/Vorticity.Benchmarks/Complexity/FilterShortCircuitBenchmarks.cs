// What a conjunction pays for its right side when its left side has already decided the batch.
//
// `k < T AND s LIKE '%needle%'` over one batch: the left side is an integer comparison, the right
// a pattern over strings, the costly one. `Original` (the evaluation as it was, below) evaluates
// both sides over every row whatever the left side said; `Library` is the evaluator itself. The
// disjunction case is the mirror: `k >= 0 OR s LIKE ...`, whose left side is true everywhere.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One filter over one decoded batch, against how much of it the left side decides.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FilterShortCircuitBenchmarks
{
    /// <summary>Rows of the batch.</summary>
    [Params(8_192, 65_536)]
    public int Rows { get; set; }

    /// <summary>What the left side leaves to the right one.</summary>
    [Params(Left.AndNone, Left.AndFew, Left.AndTenth, Left.AndHalf, Left.AndAll, Left.OrAll)]
    public Left Case { get; set; }

    /// <summary>The right side: a pattern over strings, or a comparison of integers.</summary>
    [Params(Side.Like, Side.Compare)]
    public Side Right { get; set; }

    /// <summary>The filter's shape, by what its left side decides.</summary>
    public enum Left
    {
        /// <summary>A conjunction whose left side keeps no row.</summary>
        AndNone,

        /// <summary>A conjunction whose left side keeps one row in a thousand.</summary>
        AndFew,

        /// <summary>A conjunction whose left side keeps one row in ten.</summary>
        AndTenth,

        /// <summary>A conjunction whose left side keeps one row in two.</summary>
        AndHalf,

        /// <summary>A conjunction whose left side keeps every row.</summary>
        AndAll,

        /// <summary>A disjunction whose left side keeps every row.</summary>
        OrAll,
    }

    /// <summary>What the right side evaluates.</summary>
    public enum Side
    {
        /// <summary><c>s LIKE '%needle%'</c>.</summary>
        Like,

        /// <summary><c>j &lt; 500</c>.</summary>
        Compare,
    }

    private const int StringBytes = 24;

    private CanonicalArena _arena = null!;
    private CanonicalArena _batch = new CanonicalArena();
    private int _root;
    private byte[] _states = [];
    private VortexExpr _filter = null!;
    private FilterEvaluator _library = null!;

    /// <summary>Builds the batch and the filter.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["k", "j", "s"], [i64, i64, utf8], Nullability.NonNullable);

        _arena = new CanonicalArena();
        VortexBuffer keys = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer others = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> otherBytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<long> spread = MemoryMarshal.Cast<byte, long>(otherBytes);
        for (int i = 0; i < Rows; i++)
        {
            // The left side's column is a permutation of the rows, so that the rows it keeps are
            // scattered over the batch rather than a prefix of it.
            values[i] = (long)((uint)i * 2654435761u % (uint)Rows);
            spread[i] = (i * 7919L) % 1000;
        }

        int k = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int j = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, others);

        VortexBuffer heap = _arena.Allocate(Rows * StringBytes, 1, out Span<byte> heapBytes);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> value = heapBytes.Slice(i * StringBytes, StringBytes);
            "row-00000000-of-a-batch."u8.CopyTo(value);
            i.TryFormat(value.Slice(4, 8), out _, "D8");
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, StringBytes);
            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view.Slice(8), 0);
            BinaryPrimitives.WriteInt32LittleEndian(view.Slice(12), i * StringBytes);
        }

        int s = _arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [heap]);
        _root = _arena.AddStruct(schema, Rows, Validity.NonNullable, [k, j, s]);
        _states = new byte[Rows];

        VortexExpr right = Right == Side.Like
            ? Expr.Like(Expr.Field("s"), FilterLiteral.From("%needle%"u8))
            : Expr.Lt(Expr.Field("j"), Expr.Literal(FilterLiteral.From(500L)));
        _filter = Case switch
        {
            Left.AndNone => Expr.And(KeptBelow(0), right),
            Left.AndFew => Expr.And(KeptBelow(Rows / 1000), right),
            Left.AndTenth => Expr.And(KeptBelow(Rows / 10), right),
            Left.AndHalf => Expr.And(KeptBelow(Rows / 2), right),
            Left.AndAll => Expr.And(KeptBelow(Rows), right),
            _ => Expr.Or(Expr.Ge(Expr.Field("k"), Expr.Literal(FilterLiteral.From(0L))), right),
        };

        _library = new FilterEvaluator(_filter);
    }

    /// <summary>Gives the arenas' blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _batch.Reset();
        _arena.Reset();
    }

    /// <summary>
    /// The batch for one evaluation, in an arena reset first as a scan resets its batch's: a gather
    /// adds nodes to the batch's arena, which a scan drops with the batch.
    /// </summary>
    private int Batch()
    {
        _batch.ResetKeepingBlocks();
        return _batch.ReferenceFrom(_arena, _root);
    }

    private static ComparisonExpr KeptBelow(int rows) =>
        Expr.Lt(Expr.Field("k"), Expr.Literal(FilterLiteral.From((long)rows)));

    /// <summary>Both sides over every row, then the combination.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        OriginalEvaluate(_filter, _batch, Batch(), Rows, _states, 0);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The evaluator of the library.</summary>
    [Benchmark]
    public int Library()
    {
        _library.Evaluate(_batch, Batch(), Rows, _states);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The evaluation as it was: each side of a logical node over every row.</summary>
    private static void OriginalEvaluate(
        VortexExpr filter, CanonicalArena arena, int rootIndex, int rows, Span<byte> destination, int depth)
    {
        switch (filter.Kind)
        {
            case ExprKind.Comparison:
            {
                ComparisonExpr comparison = (ComparisonExpr)filter;
                int column = FilterEvaluator.Resolve(arena, rootIndex, comparison.Field, rows);
                ComparisonKernels.Compare(arena, column, comparison.Op, comparison.Value, destination);
                return;
            }

            case ExprKind.StringMatch:
            {
                StringMatchExpr match = (StringMatchExpr)filter;
                int column = FilterEvaluator.Resolve(arena, rootIndex, match.Field, rows);
                ComparisonKernels.StringMatch(arena, column, match.Op, match.Pattern, match.Escape, destination);
                return;
            }

            case ExprKind.Not:
                OriginalEvaluate(((NotExpr)filter).Operand, arena, rootIndex, rows, destination, depth + 1);
                Trilean.Not(destination);
                return;

            case ExprKind.Logical:
            {
                LogicalExpr logical = (LogicalExpr)filter;
                OriginalEvaluate(logical.Left, arena, rootIndex, rows, destination, depth + 1);

                byte[] right = ArrayPool<byte>.Shared.Rent(rows);
                try
                {
                    Span<byte> other = right.AsSpan(0, rows);
                    OriginalEvaluate(logical.Right, arena, rootIndex, rows, other, depth + 1);
                    if (logical.IsAnd)
                    {
                        Trilean.And(destination, other);
                    }
                    else
                    {
                        Trilean.Or(destination, other);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(right);
                }

                return;
            }

            default:
                throw new ArgumentException($"The benchmark's filters hold no {filter.Kind}.", nameof(filter));
        }
    }
}
