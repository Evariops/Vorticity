using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class KeptAnswerTests
{
    private const int Rows = 64;

    private static readonly DTypeArena Types = new DTypeArena();

    [Fact]
    public void TheBatchesOfOnePublishedDictionaryAnswerAsTheKernel()
    {
        RetainingArena chunk = new RetainingArena();
        int dictionary = Strings(chunk, ["red", "green", "blue", "a value longer than a view holds"], Rows);
        chunk.Seal();
        VortexExpr like = Expr.Like(Expr.Field("c"), FilterLiteral.From("%e%"));

        AssertAnswersAsTheKernel(new FilterEvaluator(like), chunk, dictionary, batchRows: 16, 4);
    }

    // The arena of a chunk is refilled for the next chunk, which puts other values under the same
    // codes at the same node: only the publication tells the two apart.
    [Fact]
    public void ADictionaryPublishedAgainIsAnsweredByItsNewValues()
    {
        RetainingArena chunk = new RetainingArena();
        FilterEvaluator evaluator = new FilterEvaluator(
            Expr.Eq(Expr.Field("c"), Expr.Literal(FilterLiteral.From(20L))));
        RetainingArena lane = new RetainingArena();
        int dictionary = Integers(chunk, [10, 20, 30, 40], Rows);
        chunk.Seal();
        AssertAnswersAsTheKernel(evaluator, chunk, dictionary, batchRows: 16, 4, lane);

        chunk.Reset();
        Assert.Equal(dictionary, Integers(chunk, [20, 10, 40, 30], Rows));
        chunk.Seal();

        AssertAnswersAsTheKernel(evaluator, chunk, dictionary, batchRows: 16, 4, lane);
    }

    [Fact]
    public void TwoPredicatesOverOneColumnKeepTheirOwnAnswers()
    {
        RetainingArena chunk = new RetainingArena();
        int dictionary = Integers(chunk, [10, 20, 30, 40], Rows);
        chunk.Seal();
        FieldExpr field = Expr.Field("c");
        VortexExpr either = Expr.Or(
            Expr.Eq(field, Expr.Literal(FilterLiteral.From(10L))),
            Expr.In(field, FilterLiteral.From(30L), FilterLiteral.From(40L)));

        AssertAnswersAsTheKernel(new FilterEvaluator(either), chunk, dictionary, batchRows: 16, 4);
    }

    // More values than a batch has rows are answered once the lane has answered as many rows of the
    // chunk one by one, and from then on spread from the answers kept.
    [Fact]
    public void ADictionaryLargerThanItsBatchesIsAnsweredOnceItsRowsCatchUp()
    {
        RetainingArena chunk = new RetainingArena();
        long[] values = new long[40];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * 3L;
        }

        int dictionary = Integers(chunk, values, Rows);
        chunk.Seal();
        VortexExpr filter = Expr.Lt(Expr.Field("c"), Expr.Literal(FilterLiteral.From(60L)));
        FilterEvaluator evaluator = new FilterEvaluator(filter);
        RetainingArena lane = new RetainingArena();
        CanonicalOrigin origin = chunk.OriginOf(chunk.GetNode(dictionary).EncodedValuesIndex);

        AssertAnswersAsTheKernel(evaluator, chunk, dictionary, batchRows: 16, 2, lane);
        Assert.False(lane.TryKept(filter, origin, out _));

        AssertAnswersAsTheKernel(evaluator, chunk, dictionary, batchRows: 16, 2, lane, firstBatch: 2);
        Assert.True(lane.TryKept(filter, origin, out ReadOnlySpan<byte> kept));
        Assert.Equal(values.Length, kept.Length);
    }

    [Fact]
    public void AnArenaThatKeepsNothingAnswersThroughTheKernel()
    {
        RetainingArena chunk = new RetainingArena();
        int dictionary = Integers(chunk, [10, 20, 30, 40], Rows);
        chunk.Seal();
        FilterEvaluator evaluator = new FilterEvaluator(
            Expr.Ge(Expr.Field("c"), Expr.Literal(FilterLiteral.From(30L))));
        CanonicalArena owned = new CanonicalArena();

        int node = CanonicalSlice.SliceAcross(chunk, owned, dictionary, 0, 16);
        byte[] answered = new byte[16];
        evaluator.EvaluateColumn(owned, node, 16, answered);

        byte[] expected = new byte[16];
        ComparisonKernels.Compare(owned, node, ComparisonOp.GreaterOrEqual, FilterLiteral.From(30L), expected);
        Assert.Equal(expected, answered);
    }

    // A filter of more dictionary predicates than a lane keeps answers for evicts some at every
    // batch, and still answers each as the kernel does.
    [Fact]
    public void MorePredicatesThanALaneKeepsStillAnswerAsTheKernel()
    {
        RetainingArena chunk = new RetainingArena();
        int dictionary = Integers(chunk, [10, 20, 30, 40, 50, 60], Rows);
        chunk.Seal();
        FieldExpr field = Expr.Field("c");
        VortexExpr filter = Expr.Or(
            Expr.Or(
                Expr.Or(EqualTo(field, 10), EqualTo(field, 20)),
                Expr.Or(EqualTo(field, 30), EqualTo(field, 40))),
            Expr.Not(EqualTo(field, 50)));

        AssertAnswersAsTheKernel(new FilterEvaluator(filter), chunk, dictionary, batchRows: 16, 4);
    }

    private static ComparisonExpr EqualTo(FieldExpr field, long value) =>
        Expr.Eq(field, Expr.Literal(FilterLiteral.From(value)));

    /// <summary>
    /// Evaluates <paramref name="batches"/> batches of the chunk through <paramref name="evaluator"/>
    /// in one lane's arena, each against the same filter evaluated in an arena that keeps nothing.
    /// </summary>
    private static void AssertAnswersAsTheKernel(
        FilterEvaluator evaluator, CanonicalArena chunk, int dictionary, int batchRows, int batches,
        RetainingArena? lane = null, int firstBatch = 0)
    {
        lane ??= new RetainingArena();
        CanonicalArena plain = new CanonicalArena();
        FilterEvaluator fresh = new FilterEvaluator(evaluator.Filter);
        for (int b = firstBatch; b < firstBatch + batches; b++)
        {
            lane.ResetKeepingBlocks();
            plain.Reset();
            int node = CanonicalSlice.SliceAcross(chunk, lane, dictionary, b * batchRows, batchRows);
            int twin = CanonicalSlice.SliceAcross(chunk, plain, dictionary, b * batchRows, batchRows);
            byte[] answered = new byte[batchRows];
            byte[] expected = new byte[batchRows];
            evaluator.EvaluateColumn(lane, node, batchRows, answered);
            fresh.EvaluateColumn(plain, twin, batchRows, expected);
            Assert.Equal(expected, answered);
        }
    }

    private static int Integers(CanonicalArena arena, long[] values, int rows)
    {
        DType dtype = Types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        int node = arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I64, buffer);
        return Dictionary(arena, dtype, node, values.Length, rows);
    }

    private static int Strings(CanonicalArena arena, string[] values, int rows)
    {
        DType dtype = Types.Utf8(Nullability.NonNullable);
        VortexBuffer heap = arena.Allocate(values.Length * 64, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < values.Length; i++)
        {
            byte[] value = Encoding.UTF8.GetBytes(values[i]);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.CopyTo(heapBytes[used..]);
            value.AsSpan(0, 4).CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += value.Length;
        }

        int node = arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, [heap]);
        return Dictionary(arena, dtype, node, values.Length, rows);
    }

    private static int Dictionary(CanonicalArena arena, DType dtype, int values, int entries, int rows)
    {
        VortexBuffer codes = arena.Allocate(rows * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(bytes);
        for (int row = 0; row < rows; row++)
        {
            code[row] = (uint)((row * 7) % entries);
        }

        return arena.AddDictionary(dtype, rows, Validity.NonNullable, codes, values);
    }
}
