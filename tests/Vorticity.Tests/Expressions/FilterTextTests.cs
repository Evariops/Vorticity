using System;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Expressions;

/// <summary>The text of a filter: what it prints is what the grammar reads back, and what the lexer refuses.</summary>
public sealed class FilterTextTests
{
    /// <summary>A whole-word match prints as the grammar reads it, and reads back as itself.</summary>
    [Fact]
    public void AWholeWordMatchReadsBackAsItself()
    {
        VortexExpr filter = Expr.HasToken(Expr.Field("message"), FilterLiteral.From("timeout"));
        Assert.Equal("message has token 'timeout'", filter.ToString());
        Assert.Equal(filter.ToString(), VortexExpr.Parse(filter.ToString()).ToString());
        Assert.Throws<FormatException>(() => VortexExpr.Parse("message has token 'two words'"));
        Assert.Throws<FormatException>(() => VortexExpr.Parse("message has token 42"));
    }

    /// <summary>An escape that is a quote, or a byte past ASCII, prints as a literal the lexer reads back, not as an open text.</summary>
    [Theory]
    [InlineData((byte)'\'')]
    [InlineData((byte)'!')]
    [InlineData((byte)0xA7)]
    public void AnEscapeReadsBackAsItself(byte escape)
    {
        VortexExpr filter = Expr.Like(Expr.Field("name"), FilterLiteral.From("50!%%"), escape);
        StringMatchExpr read = Assert.IsType<StringMatchExpr>(VortexExpr.Parse(filter.ToString()));
        Assert.Equal(escape, read.Escape);
        Assert.Equal(filter.ToString(), read.ToString());
    }

    /// <summary>A marker past the parameters given, or past an int, is a format error, not a null that keeps no row nor an overflow.</summary>
    [Fact]
    public void AMarkerThatNamesNoParameterIsAFormatError()
    {
        FilterLiteral[] parameters = [FilterLiteral.From(1L)];
        Assert.Equal("day = 1", ExprText.Parse("day = ?0", parameters).ToString());
        Assert.Throws<FormatException>(() => ExprText.Parse("day = ?1", parameters));
        Assert.Throws<FormatException>(() => ExprText.Parse("day = ?99999999999", parameters));
    }

    /// <summary>An integer past a ulong is a double, as the grammar says.</summary>
    [Fact]
    public void AnIntegerPastAULongIsADouble()
    {
        ComparisonExpr comparison = Assert.IsType<ComparisonExpr>(VortexExpr.Parse("big > 18446744073709551616"));
        Assert.Equal(FilterLiteralKind.Float, comparison.Value.Kind);
        Assert.Equal(18446744073709551616d, comparison.Value.FloatValue);
    }
}
