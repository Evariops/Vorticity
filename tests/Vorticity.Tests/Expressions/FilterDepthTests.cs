using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Expressions;

public sealed class FilterDepthTests
{
    private const int Deepest = FilterEvaluator.MaxDepth;

    [Fact]
    public void FilterTextNestedPastTheEvaluatorsDepthIsRefusedAsItIsRead()
    {
        // At the depth the evaluator takes, every nesting reads.
        _ = VortexExpr.Parse(Repeat("not ", Deepest) + "x = 1");
        _ = VortexExpr.Parse(Repeat("(", Deepest) + "x = 1" + Repeat(")", Deepest));

        // One past it, the text is refused.
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("not ", Deepest + 1) + "x = 1"));
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("(", Deepest + 1) + "x = 1" + Repeat(")", Deepest + 1)));

        // Far past it, refused without running out of stack.
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("not ", 200_000) + "x = 1"));
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("(", 200_000) + "x = 1" + Repeat(")", 200_000)));

        // A run of one operator nests as deep as the log of its length, however long it is, and a
        // deep operand in it goes as deep as it would as the run leans, and no deeper.
        Assert.InRange(VortexExpr.Parse("x = 1" + Repeat(" or x = 1", 200_000)).Height, 1, 18);
        _ = VortexExpr.Parse("x = 1 and x = 1 and x = 1 and x = 1 and " + Repeat("not ", Deepest - 1) + "x = 1");
        Assert.Throws<FormatException>(() => VortexExpr.Parse("x = 1 and " + Repeat("not ", Deepest) + "x = 1"));
    }

    [Fact]
    public void AFilterBuiltPastTheEvaluatorsDepthIsRefusedAsItIsBuilt()
    {
        VortexExpr leaf = Expr.Eq(Expr.Field("x"), Expr.Literal(FilterLiteral.From(1L)));
        VortexExpr negated = leaf;
        for (int i = 0; i < Deepest; i++)
        {
            negated = !negated;
        }

        // At the depth the evaluator takes, a filter prints, and one past it is refused.
        Assert.StartsWith("not ", negated.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => !negated);
        Assert.Throws<ArgumentException>(() => negated & !leaf & !leaf);

        // A chain joined one operand at a time, from either side, stays as deep as an AVL tree of
        // its operands, and prints as the flat run it is.
        VortexExpr chained = leaf;
        VortexExpr prepended = leaf;
        for (int i = 0; i < 10_000; i++)
        {
            chained &= leaf;
            prepended = leaf | prepended;
        }

        Assert.InRange(chained.Height, 1, 20);
        Assert.InRange(prepended.Height, 1, 20);
        Assert.DoesNotContain("(", chained.ToString(), StringComparison.Ordinal);
        Assert.InRange((chained | prepended).Height, 1, 21);

        // The rotations keep the operands in the order they were joined, which evaluation follows.
        VortexExpr ordered = Term(0);
        VortexExpr reversed = Term(0);
        StringBuilder forward = new StringBuilder("x = 0");
        StringBuilder backward = new StringBuilder("x = 0");
        for (int i = 1; i < 300; i++)
        {
            ordered &= Term(i);
            reversed = Term(i) | reversed;
            forward.Append(" and x = ").Append(i);
            backward.Insert(0, $"x = {i} or ");
        }

        Assert.Equal(forward.ToString(), ordered.ToString());
        Assert.Equal(backward.ToString(), reversed.ToString());
        Assert.Equal(forward.ToString(), VortexExpr.Parse(forward.ToString()).ToString());

        // Neighbouring runs of every size joined in a random order, which takes every rotation:
        // the operands keep their order, and the run stays within an AVL tree's height.
        Random random = new Random(45);
        for (int round = 0; round < 50; round++)
        {
            int count = random.Next(2, 600);
            List<VortexExpr> runs = [];
            for (int i = 0; i < count; i++)
            {
                runs.Add(Term(i));
            }

            while (runs.Count > 1)
            {
                int at = random.Next(runs.Count - 1);
                runs[at] = runs[at] & runs[at + 1];
                runs.RemoveAt(at + 1);
            }

            Assert.Equal(string.Join(" and ", Enumerable.Range(0, count).Select(i => $"x = {i}")), runs[0].ToString());
            Assert.InRange(runs[0].Height, 1, (int)((1.45 * Math.Log2(count + 2)) + 1));
        }

        static VortexExpr Term(int value) => Expr.Eq(Expr.Field("x"), Expr.Literal(FilterLiteral.From((long)value)));
    }

    [Fact]
    public async Task AChainOfThousandsOfTermsKeepsTheRowsEveryTermKeeps()
    {
        // Built one conjunct at a time and read from text, a conjunction of 2,000 terms keeps what
        // each of them keeps, and a disjunction of as many what any of them keeps.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(await WriteAsync(4_096)), new VortexOpenOptions(), TestContext.Current.CancellationToken);
        FieldExpr x = Expr.Field("x");
        VortexExpr all = Expr.Ne(x, Expr.Literal(FilterLiteral.From(0L)));
        VortexExpr any = Expr.Eq(x, Expr.Literal(FilterLiteral.From(0L)));
        StringBuilder text = new StringBuilder("x != 0");
        for (long k = 1; k < 2_000; k++)
        {
            all = Expr.And(all, Expr.Ne(x, Expr.Literal(FilterLiteral.From(k * 2))));
            any = Expr.Or(Expr.Eq(x, Expr.Literal(FilterLiteral.From(k * 2))), any);
            text.Append(" and x != ").Append(k * 2);
        }

        Assert.Equal(4_096 - 2_000, await file.ScanBuilder().Where(all).CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4_096 - 2_000, await file.ScanBuilder().Where(VortexExpr.Parse(text.ToString())).CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2_000, await file.ScanBuilder().Where(any).CountAsync(TestContext.Current.CancellationToken));
    }

    private static string Repeat(string text, int count) => new StringBuilder(text.Length * count).Insert(0, text, count).ToString();

    /// <summary>A file of one Int64 column <c>x</c> holding 0 to <paramref name="rows"/> − 1.</summary>
    private static async Task<byte[]> WriteAsync(int rows)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["x"], [i64], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < rows; i++)
        {
            values[i] = i;
        }

        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, new VortexWriteOptions()))
        {
            int column = arena.AddPrimitive(i64, rows, Validity.NonNullable, PType.I64, buffer);
            using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, rows, Validity.NonNullable, [column]), 0))
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }
}
