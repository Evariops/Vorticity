using System;
using System.Text;
using Vorticity.Compute;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Expressions;

public sealed class FilterDepthTests
{
    private const int Deepest = FilterEvaluator.MaxDepth;

    [Fact]
    public void FilterTextNestedPastTheEvaluatorsDepthIsRefusedAsItIsRead()
    {
        // At the depth the evaluator takes, every shape reads.
        _ = VortexExpr.Parse(Repeat("not ", Deepest) + "x = 1");
        _ = VortexExpr.Parse(Repeat("(", Deepest) + "x = 1" + Repeat(")", Deepest));
        _ = VortexExpr.Parse("x = 1" + Repeat(" and x = 1", Deepest));

        // One past it, the text is refused.
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("not ", Deepest + 1) + "x = 1"));
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("(", Deepest + 1) + "x = 1" + Repeat(")", Deepest + 1)));
        Assert.Throws<FormatException>(() => VortexExpr.Parse("x = 1" + Repeat(" or x = 1", Deepest + 1)));

        // Far past it, refused without running out of stack.
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("not ", 200_000) + "x = 1"));
        Assert.Throws<FormatException>(() => VortexExpr.Parse(Repeat("(", 200_000) + "x = 1" + Repeat(")", 200_000)));
        Assert.Throws<FormatException>(() => VortexExpr.Parse("x = 1" + Repeat(" and x = 1", 200_000)));
    }

    [Fact]
    public void AFilterBuiltPastTheEvaluatorsDepthIsRefusedAsItIsBuilt()
    {
        VortexExpr leaf = Expr.Eq(Expr.Field("x"), Expr.Literal(FilterLiteral.From(1L)));
        VortexExpr negated = leaf;
        VortexExpr chained = leaf;
        for (int i = 0; i < Deepest; i++)
        {
            negated = !negated;
            chained &= leaf;
        }

        // At the depth the evaluator takes, a filter prints, and a scan can walk it.
        Assert.StartsWith("not ", negated.ToString(), StringComparison.Ordinal);
        Assert.Contains(" and ", chained.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => !negated);
        Assert.Throws<ArgumentException>(() => chained | leaf);
        Assert.Throws<ArgumentException>(() => leaf & chained);
    }

    private static string Repeat(string text, int count) => new StringBuilder(text.Length * count).Insert(0, text, count).ToString();
}
