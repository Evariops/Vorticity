// The path directory: every path and class a variant column's rows hold in an object, counted with its
// bounds, written and read back, and what it refutes: a question on a path the object lacks, on a class
// the path never holds, or on values outside its bounds.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class VariantDirectoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Integers, a text among them, a long text cut to the bound, a flag, numbers with a NaN, a time, a
    /// nested key, a list and a null: each path's classes, counts and bounds, the same once written and
    /// read back.
    /// </summary>
    [Fact]
    public void ADirectoryCountsAndBoundsEveryPathAndClass()
    {
        VariantDirectory directory = Directory();

        Assert.Equal(6, directory.Rows);
        PathEntry integers = Entry(directory, ["status"], PathClass.Integer);
        Assert.Equal((3, -4L, 503L), (integers.Values, integers.Low, integers.High));
        Assert.Equal(1, Entry(directory, ["status"], PathClass.String).Values);
        Assert.Equal(PathPlace.Typed, integers.Place);
        Assert.Equal(PathPlace.Value, Entry(directory, ["status"], PathClass.String).Place);

        // Text bounds are cut to four bytes: the least is a prefix, the greatest one past its prefix.
        PathEntry routes = Entry(directory, ["route"], PathClass.String);
        Assert.Equal("/a/b"u8.ToArray(), routes.MinText);
        Assert.Equal("/z/b"u8.ToArray(), routes.MaxText);
        Assert.Equal(PathPlace.Residual, routes.Place);

        Assert.Equal(2, Entry(directory, ["ok"], PathClass.Boolean).Low);
        PathEntry scores = Entry(directory, ["score"], PathClass.Double);
        Assert.True(scores.NaNs);
        Assert.Equal((0.5, 2.5), (BitConverter.Int64BitsToDouble(scores.Low), BitConverter.Int64BitsToDouble(scores.High)));
        Assert.Equal(Noon.ToUnixTimeMilliseconds() * 1000, Entry(directory, ["at"], PathClass.Timestamp).Low);
        Assert.Equal(2, Entry(directory, ["user", "id"], PathClass.Integer).Values);
        Assert.Equal(1, Entry(directory, ["tags"], PathClass.List).Values);
        Assert.Equal(1, Entry(directory, [], PathClass.Null).Values);
        Assert.True(directory.Of(["missing"]).IsEmpty);

        VariantDirectory read = VariantDirectory.From(directory.ToBytes())!;
        Assert.Equal(directory.Rows, read.Rows);
        Assert.Equal(directory.Entries.Count, read.Entries.Count);
        for (int i = 0; i < read.Entries.Count; i++)
        {
            PathEntry want = directory.Entries[i];
            PathEntry got = read.Entries[i];
            Assert.Equal(want.Path, got.Path);
            Assert.Equal((want.Class, want.Values, want.Place, want.Low, want.High, want.NaNs), (got.Class, got.Values, got.Place, got.Low, got.High, got.NaNs));
            Assert.Equal(want.MinText, got.MinText);
            Assert.Equal(want.MaxText, got.MaxText);
        }
    }

    /// <summary>A region cut short, or of another version, refutes nothing: it reads as no directory.</summary>
    [Fact]
    public void ARegionThatDoesNotDecodeIsNoDirectory()
    {
        byte[] bytes = Directory().ToBytes();
        Assert.Null(VariantDirectory.From(bytes.AsSpan(0, bytes.Length - 3)));
        bytes[0] = 9;
        Assert.Null(VariantDirectory.From(bytes));
    }

    /// <summary>
    /// What the directory refutes: an absent path, a class the path never holds, a value outside the
    /// bounds, under a conjunction; and what it keeps: a conversion of any class, a negation, an absent
    /// path asked absent, and a disjunction with a predicate it cannot rule on.
    /// </summary>
    [Fact]
    public void ADirectoryRefutesWhatNoRowCanMatch()
    {
        Dictionary<string, VariantDirectory> directories = new() { ["Attributes"] = Directory() };
        bool May(VortexExpr filter) => VariantDirectory.MayMatch(filter, directories);

        Assert.False(May(Has("missing")));
        Assert.True(May(Has("route")));
        Assert.True(May(Expr.Not(Has("missing"))));
        Assert.True(May(Path(VariantRead.Has, ComparisonOp.Equal, FilterLiteral.From(false), "missing")));
        Assert.True(May(Has("user", "id")));

        Assert.False(May(Path(VariantRead.AsInt64, ComparisonOp.Equal, FilterLiteral.From(1L), "missing")));
        Assert.True(May(Path(VariantRead.AsInt64, ComparisonOp.Equal, FilterLiteral.From(200L), "status")));
        Assert.False(May(Path(VariantRead.AsInt64, ComparisonOp.Greater, FilterLiteral.From(503L), "status")));
        Assert.True(May(Path(VariantRead.AsInt64, ComparisonOp.GreaterOrEqual, FilterLiteral.From(503L), "status")));
        Assert.False(May(Path(VariantRead.AsInt64, ComparisonOp.Less, FilterLiteral.From(-4L), "status")));
        Assert.True(May(Path(VariantRead.AsInt64, ComparisonOp.NotEqual, FilterLiteral.From(200L), "status")));

        // Each class answers for its own values: the text "502" is no integer, an integer no text.
        Assert.True(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("502"), "status")));
        Assert.False(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("x"), "ok")));
        Assert.False(May(Path(VariantRead.AsBool, ComparisonOp.Equal, FilterLiteral.From(true), "status")));
        Assert.True(May(Path(VariantRead.ToInt64, ComparisonOp.Equal, FilterLiteral.From(502L), "status")));
        Assert.False(May(Path(VariantRead.ToInt64, ComparisonOp.Equal, FilterLiteral.From(502L), "missing")));

        // Text bounds: below the least prefix, above the greatest bound.
        Assert.False(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("/0"), "route")));
        Assert.False(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("/zz"), "route")));
        Assert.True(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("/z/a-long-route-past-the-bound"), "route")));
        Assert.True(May(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("/a/b/c"), "route")));

        // A flag seen true only, numbers with a NaN, a time.
        Assert.False(May(Path(VariantRead.AsBool, ComparisonOp.Equal, FilterLiteral.From(false), "ok")));
        Assert.True(May(Path(VariantRead.AsDouble, ComparisonOp.Greater, FilterLiteral.From(100.0), "score")));
        Assert.False(May(Path(VariantRead.AsTimestamp, ComparisonOp.Less, FilterLiteral.From(Noon.ToUnixTimeMilliseconds() * 1000), "at")));

        Assert.False(May(Expr.And(Has("route"), Has("missing"))));
        Assert.True(May(Expr.Or(Has("missing"), Has("route"))));
        Assert.True(May(Expr.Or(Has("missing"), Expr.Eq(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(1L))))));
        Assert.True(VariantDirectory.MayMatch(Has("missing"), new Dictionary<string, VariantDirectory>()));
    }

    /// <summary>Six rows: integers and a text at "status", routes, a flag, numbers and a NaN, a time, a nested key, a list, a null.</summary>
    private static VariantDirectory Directory()
    {
        PathCensus census = new PathCensus(stringBoundBytes: 4);
        foreach (Variant row in new[]
        {
            Variant.Object(("status", Variant.From(200L)), ("route", Variant.From("/a/b/c")), ("ok", Variant.From(true)), ("score", Variant.From(0.5))),
            Variant.Object(("status", Variant.From(503L)), ("route", Variant.From("/z/a-long-route")), ("score", Variant.From(double.NaN)), ("at", Variant.From(Noon))),
            Variant.Object(("status", Variant.From(-4L)), ("user", Variant.Object(("id", Variant.From(7L)))), ("score", Variant.From(2.5f))),
            Variant.Object(("status", Variant.From("502")), ("user", Variant.Object(("id", Variant.From(8L)))), ("tags", Variant.Array(Variant.From("a")))),
            Variant.Null,
            Variant.Object(("ok", Variant.From(true))),
        })
        {
            census.Add(row.Metadata.Span, row.Value.Span);
        }

        return VariantDirectory.From(census, VariantShredding.Object(("status", VariantShredding.Primitive(VariantClass.Integer))));
    }

    private static PathEntry Entry(VariantDirectory directory, string[] path, PathClass @class) =>
        Assert.Single(directory.Of(path).ToArray(), entry => entry.Class == @class);

    private static VortexExpr Has(params string[] keys) => Path(VariantRead.Has, ComparisonOp.Equal, FilterLiteral.From(true), keys);

    private static VortexExpr Path(VariantRead read, ComparisonOp op, FilterLiteral value, params string[] keys) =>
        VariantFilterExpr.Of(new ComparisonExpr(
            new VariantFieldExpr(new FieldExpr("Attributes"), [.. keys.Select(VariantStep.Of)], read), op, value));
}
