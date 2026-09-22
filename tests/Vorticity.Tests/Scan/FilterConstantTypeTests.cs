using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// A constant a column cannot be compared against is an error, not an empty result.
/// </summary>
/// <remarks>
/// The trap here is the quiet one: an ordering predicate whose constant is of another kind ruled
/// out every zone, because a bound that cannot be ordered against the constant answered zero and
/// zero was read as "equal". The scan yielded nothing, <c>MayMatch</c> answered false, and the
/// caller had every reason to believe the file empty. Equality and membership were refused, but
/// only once a batch had been decoded.
///
/// So each test asserts both halves: the mistyped predicate throws, and the comparisons that do
/// relate -- an integer constant against a float column, a float against an integer, an extension
/// taking what its storage takes -- still answer, with their rows.
/// </remarks>
public sealed class FilterConstantTypeTests
{
    /// <summary>{a=i32, b=utf8?, c=bool}.</summary>
    private const string Flat = "types/struct_flat_nonnull_r1024";

    /// <summary>Carries <c>stamp</c>, an extension over an i64.</summary>
    private const string Mixed = "encodings/table_mixed";

    [Theory]
    [InlineData(ComparisonOp.Greater)]
    [InlineData(ComparisonOp.GreaterOrEqual)]
    [InlineData(ComparisonOp.Less)]
    [InlineData(ComparisonOp.LessOrEqual)]
    [InlineData(ComparisonOp.Equal)]
    [InlineData(ComparisonOp.NotEqual)]
    internal async Task AConstantOfAnotherKindIsRefusedByEveryOperator(ComparisonOp op)
    {
        await using VortexFile file = await Open(Flat);
        VortexExpr wrong = Compare(op, Expr.Field("a"), FilterLiteral.From("900"));

        ArgumentException error = Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(wrong));
        Assert.Contains("'a'", error.Message, StringComparison.Ordinal);
        Assert.Contains("i32", error.Message, StringComparison.Ordinal);
        Assert.Contains("text or binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRefusalComesBeforeAnythingIsRead()
    {
        // The whole point: the rows are there, so an empty answer would have been believable.
        await using VortexFile file = await Open(Flat);
        Assert.Equal(1024, await file.ScanBuilder().Where(Expr.IsNotNull(Expr.Field("a"))).CountAsync());

        VortexExpr wrong = Expr.Gt(Expr.Field("a"), Expr.Literal(FilterLiteral.From("900")));
        Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(wrong));
        Assert.Throws<ArgumentException>(() => file.MayMatch(wrong));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await file.MayMatchAsync(wrong, CancellationToken.None));
    }

    [Fact]
    public async Task ATextColumnRefusesANumericConstant()
    {
        await using VortexFile file = await Open(Flat);
        VortexExpr wrong = Expr.Gt(Expr.Field("b"), Expr.Literal(FilterLiteral.From(900)));

        ArgumentException error = Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(wrong));
        Assert.Contains("signed integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABooleanColumnTakesABooleanConstantAndNothingElse()
    {
        await using VortexFile file = await Open(Flat);
        Assert.Throws<ArgumentException>(
            () => file.ScanBuilder().Where(Expr.Eq(Expr.Field("c"), Expr.Literal(FilterLiteral.From(1)))));

        VortexExpr right = Expr.Eq(Expr.Field("c"), Expr.Literal(FilterLiteral.From(true)));
        Assert.Equal(512, await file.ScanBuilder().Where(right).CountAsync());
    }

    [Fact]
    public async Task EveryCandidateOfAMembershipIsChecked()
    {
        await using VortexFile file = await Open(Flat);
        VortexExpr wrong = Expr.In(
            Expr.Field("a"), FilterLiteral.From(1), FilterLiteral.From(2), FilterLiteral.From("3"));

        Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(wrong));
    }

    [Fact]
    public async Task TextMatchingIsRefusedOnAColumnThatHoldsNoText()
    {
        await using VortexFile file = await Open(Flat);
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => file.ScanBuilder().Where(Expr.StartsWith(Expr.Field("a"), FilterLiteral.From("9"))));

        Assert.Contains("StartsWith", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalReachesThroughAndOrAndNot()
    {
        await using VortexFile file = await Open(Flat);
        VortexExpr wrong = Expr.Gt(Expr.Field("a"), Expr.Literal(FilterLiteral.From("900")));
        VortexExpr right = Expr.IsNotNull(Expr.Field("a"));

        Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(Expr.And(right, wrong)));
        Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(Expr.Or(wrong, right)));
        Assert.Throws<ArgumentException>(() => file.ScanBuilder().Where(Expr.Not(wrong)));
    }

    [Fact]
    public async Task AnExtensionColumnTakesWhatItsStorageTakes()
    {
        await using VortexFile file = await Open(Mixed);
        long rows = await file.ScanBuilder()
            .Where(Expr.Ge(Expr.Field("stamp"), Expr.Literal(FilterLiteral.From(0L))))
            .CountAsync();
        Assert.True(rows > 0);

        Assert.Throws<ArgumentException>(
            () => file.ScanBuilder().Where(
                Expr.Ge(Expr.Field("stamp"), Expr.Literal(FilterLiteral.From("0")))));
    }

    [Fact]
    public async Task NumbersOfDifferentWidthsStillCompare()
    {
        // An i32 column against a long and against a double, and an f64 column against an integer:
        // the kernels widen all three, and the check must not stand in their way.
        await using VortexFile file = await Open(Flat);
        long viaLong = await Count(file, Expr.Field("a"), FilterLiteral.From(0L));
        long viaDouble = await Count(file, Expr.Field("a"), FilterLiteral.From(0.0));
        Assert.Equal(viaLong, viaDouble);
        Assert.True(viaLong > 0);

        await using VortexFile mixed = await Open(Mixed);
        Assert.True(await Count(mixed, Expr.Field("price"), FilterLiteral.From(0)) > 0);
    }

    [Fact]
    public async Task ANullConstantIsStillCompared()
    {
        // A null constant is unknown against every column, whatever its type, and that is the
        // documented answer rather than a mistyped comparison.
        await using VortexFile file = await Open(Flat);
        VortexExpr nothing = Expr.Eq(Expr.Field("a"), Expr.Literal(FilterLiteral.Null));
        Assert.Equal(0, await file.ScanBuilder().Where(nothing).CountAsync());
        Assert.True(file.MayMatch(nothing));
    }

    [Fact]
    public async Task AnUnknownPathIsStillTheErrorItWas()
    {
        await using VortexFile file = await Open(Flat);
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => file.ScanBuilder().Where(
                Expr.Gt(Expr.Field("absent"), Expr.Literal(FilterLiteral.From("900")))));

        Assert.Contains("does not name a field", error.Message, StringComparison.Ordinal);
    }

    private static async Task<long> Count(VortexFile file, FieldExpr field, FilterLiteral value) =>
        await file.ScanBuilder().Where(Expr.Gt(field, Expr.Literal(value))).CountAsync();

    private static async Task<VortexFile> Open(string id)
    {
        Decoders.EnsureRegistered();
        return await VortexFile.OpenAsync(
            Corpus.Path(id), VortexOpenOptions.Default, CancellationToken.None);
    }

    private static VortexExpr Compare(ComparisonOp op, FieldExpr field, FilterLiteral value)
    {
        LiteralExpr literal = Expr.Literal(value);
        return op switch
        {
            ComparisonOp.Equal => Expr.Eq(field, literal),
            ComparisonOp.NotEqual => Expr.Ne(field, literal),
            ComparisonOp.Less => Expr.Lt(field, literal),
            ComparisonOp.LessOrEqual => Expr.Le(field, literal),
            ComparisonOp.Greater => Expr.Gt(field, literal),
            _ => Expr.Ge(field, literal),
        };
    }
}
