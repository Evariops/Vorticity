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

    /// <summary>
    /// A hundred rows over four slices of 25: a key held by the first ten rows, one by the last ten, one
    /// by every row. Each names the slices it lies in, the one held everywhere none, and a question asked
    /// of one slice is refuted where its key is not.
    /// </summary>
    [Fact]
    public void ADirectoryNamesTheSlicesEachPathLiesIn()
    {
        PathCensus census = new PathCensus();
        for (int i = 0; i < 100; i++)
        {
            Variant row = Variant.Object([("all", Variant.From((long)i)), .. (i < 10 ? [("early", Variant.From(true))] : (i >= 90 ? new[] { ("late", Variant.From("x")) } : []))]);
            census.Add(row.Metadata.Span, row.Value.Span, instant: i * 1_000L);
        }

        TimeSlice[] slices = [.. Enumerable.Range(0, 4).Select(s => new TimeSlice(s * 25_000L, (s + 1) * 25_000L, s * 25L, 25))];
        VariantDirectory directory = VariantDirectory.From(census, null, slices);
        Assert.Equal(4, directory.SliceCount);
        Assert.Null(Entry(directory, ["all"], PathClass.Integer).Slices);
        Assert.Equal([0b0001], Entry(directory, ["early"], PathClass.Boolean).Slices);
        Assert.Equal([0b1000], Entry(directory, ["late"], PathClass.String).Slices);

        Dictionary<string, VariantDirectory> directories = new() { ["Attributes"] = VariantDirectory.From(directory.ToBytes())! };
        Assert.Equal(
            [true, false, false, false],
            Enumerable.Range(0, 4).Select(s => VariantDirectory.MayMatch(Has("early"), directories, s)));
        Assert.Equal(
            [false, false, false, true],
            Enumerable.Range(0, 4).Select(s => VariantDirectory.MayMatch(Expr.Or(Has("late"), Has("missing")), directories, s)));
        Assert.True(VariantDirectory.MayMatch(Has("early"), directories));
        Assert.All(Enumerable.Range(0, 4), s => Assert.True(VariantDirectory.MayMatch(Expr.Not(Has("early")), directories, s)));
    }

    /// <summary>
    /// Two thousand keys, each held by one row but a few held by many: over its budget the directory
    /// leaves the rarest out, keeps those held most, and refutes nothing by a path's absence any more.
    /// </summary>
    [Fact]
    public void ADirectoryOverItsBudgetLeavesTheRarestPathsOut()
    {
        PathCensus census = new PathCensus();
        for (int i = 0; i < 2_000; i++)
        {
            Variant row = Variant.Object(($"user.{i}.score", Variant.From((long)i)), ("route", Variant.From("/a")));
            census.Add(row.Metadata.Span, row.Value.Span);
        }

        VariantDirectory whole = VariantDirectory.From(census, null);
        Dictionary<string, VariantDirectory> complete = new() { ["Attributes"] = whole };
        Assert.False(VariantDirectory.MayMatch(Has("never"), complete));

        int bytes = whole.ToBytes().Length;
        VariantDirectory trimmed = whole.Within(bytes / 4);
        Assert.True(trimmed.Partial);
        Assert.InRange(trimmed.ToBytes().Length, 1, bytes / 4);
        Assert.Equal(1, trimmed.Of(["route"]).Length);
        Assert.InRange(trimmed.Entries.Count, 2, whole.Entries.Count - 1);
        Dictionary<string, VariantDirectory> partial = new() { ["Attributes"] = VariantDirectory.From(trimmed.ToBytes())! };
        Assert.True(VariantDirectory.MayMatch(Has("never"), partial));

        // A class the trimmed directory holds no entry of may be one it left out: it refutes nothing, where the whole
        // directory, which holds every class, does.
        VortexExpr integers = Path(VariantRead.AsInt64, ComparisonOp.Equal, FilterLiteral.From(1L), "route");
        Assert.True(VariantDirectory.MayMatch(integers, partial));
        Assert.False(VariantDirectory.MayMatch(integers, complete));
        Assert.Same(whole, whole.Within(bytes));
    }

    /// <summary>
    /// A directory read for some paths holds their entries alone, as the whole one holds them, slices included, and
    /// answers every question on those paths as the whole one does, slice by slice too; it holds nothing of a path it
    /// was not read for, and a region that does not decode is no directory either way.
    /// </summary>
    [Fact]
    public void ADirectoryReadForSomePathsAnswersForThemAsTheWholeOne()
    {
        VariantDirectory whole = Directory();
        byte[] bytes = whole.ToBytes();
        string[][] paths = [["status"], ["user", "id"], ["missing"]];
        VariantDirectory some = VariantDirectory.From(bytes, Utf8(paths))!;
        Assert.Equal((whole.Rows, whole.SliceCount, whole.Partial), (some.Rows, some.SliceCount, some.Partial));
        foreach (string[] path in paths)
        {
            AssertSameEntries(whole.Of(path).ToArray(), some.Of(path).ToArray());
        }

        Assert.False(whole.Of(["route"]).IsEmpty);
        Assert.True(some.Of(["route"]).IsEmpty);
        Assert.Equal(whole.Of(["status"]).Length + whole.Of(["user", "id"]).Length, some.Entries.Count);

        Dictionary<string, VariantDirectory> all = new() { ["Attributes"] = whole };
        Dictionary<string, VariantDirectory> read = new() { ["Attributes"] = some };
        foreach (VortexExpr filter in new[]
        {
            Has("missing"), Has("user", "id"), Expr.Not(Has("missing")),
            Path(VariantRead.AsInt64, ComparisonOp.Greater, FilterLiteral.From(503L), "status"),
            Path(VariantRead.AsInt64, ComparisonOp.GreaterOrEqual, FilterLiteral.From(503L), "status"),
            Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("502"), "status"),
            Path(VariantRead.AsBool, ComparisonOp.Equal, FilterLiteral.From(true), "status"),
            Expr.And(Has("status"), Has("missing")), Expr.Or(Has("missing"), Has("user", "id")),
        })
        {
            Assert.Equal(VariantDirectory.MayMatch(filter, all), VariantDirectory.MayMatch(filter, read));
        }

        Assert.Null(VariantDirectory.From(bytes.AsSpan(0, bytes.Length - 3), Utf8(paths)));

        // The slices a path lies in, read for it alone.
        PathCensus census = new PathCensus();
        for (int i = 0; i < 100; i++)
        {
            Variant row = Variant.Object([("all", Variant.From((long)i)), .. (i < 10 ? [("early", Variant.From(true))] : (i >= 90 ? new[] { ("late", Variant.From("x")) } : []))]);
            census.Add(row.Metadata.Span, row.Value.Span, instant: i * 1_000L);
        }

        TimeSlice[] slices = [.. Enumerable.Range(0, 4).Select(s => new TimeSlice(s * 25_000L, (s + 1) * 25_000L, s * 25L, 25))];
        byte[] sliced = VariantDirectory.From(census, null, slices).ToBytes();
        Dictionary<string, VariantDirectory> early = new() { ["Attributes"] = VariantDirectory.From(sliced, Utf8([["early"]]))! };
        Dictionary<string, VariantDirectory> everything = new() { ["Attributes"] = VariantDirectory.From(sliced)! };
        Assert.All(Enumerable.Range(-1, 5), s => Assert.Equal(
            VariantDirectory.MayMatch(Has("early"), everything, s), VariantDirectory.MayMatch(Has("early"), early, s)));
        AssertSameEntries(everything["Attributes"].Of(["early"]).ToArray(), early["Attributes"].Of(["early"]).ToArray());
    }

    /// <summary>
    /// A path that keeps its integers' entry but whose rare texts a directory over its budget leaves out: a question on its
    /// texts may match, over the object and over the slice they lie in, while the classes and bounds a whole directory
    /// holds still refute what they refute.
    /// </summary>
    [Fact]
    public void ATrimmedDirectoryRefutesNoClassItLeftOut()
    {
        PathCensus census = new PathCensus(stringBoundBytes: 8);
        for (int i = 0; i < 3_000; i++)
        {
            Variant code = i == 2_999 ? Variant.From("x") : Variant.From((long)(i % 7));
            Variant row = Variant.Object(("code", code), ($"k{i % 600}", Variant.From(1L)), ($"u{i}", Variant.From(true)));
            census.Add(row.Metadata.Span, row.Value.Span, instant: i * 1_000L);
        }

        TimeSlice[] slices = [.. Enumerable.Range(0, 4).Select(s => new TimeSlice(s * 750_000L, (s + 1) * 750_000L, s * 750L, 750))];
        VariantDirectory whole = VariantDirectory.From(census, null, slices);
        VariantDirectory trimmed = VariantDirectory.From(whole.Within(whole.ToBytes().Length / 8).ToBytes())!;
        Assert.True(trimmed.Partial);
        Assert.Single(trimmed.Of(["code"]).ToArray(), entry => entry.Class == PathClass.Integer);
        Assert.DoesNotContain(trimmed.Of(["code"]).ToArray(), entry => entry.Class == PathClass.String);

        Dictionary<string, VariantDirectory> complete = new() { ["Attributes"] = whole };
        Dictionary<string, VariantDirectory> partial = new() { ["Attributes"] = trimmed };
        VortexExpr text = Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("x"), "code");
        VortexExpr present = VariantFilterExpr.Of(new NullCheckExpr(new VariantFieldExpr(new FieldExpr("Attributes"), [VariantStep.Of("code")], VariantRead.AsString), isNull: false));
        foreach (int slice in new[] { -1, 3 })
        {
            Assert.True(VariantDirectory.MayMatch(text, complete, slice));
            Assert.True(VariantDirectory.MayMatch(text, partial, slice));
            Assert.True(VariantDirectory.MayMatch(present, partial, slice));
        }

        // The whole directory refutes by class and by bounds; the trimmed one by the bounds of the entries it keeps.
        Assert.False(VariantDirectory.MayMatch(Path(VariantRead.AsBool, ComparisonOp.Equal, FilterLiteral.From(true), "code"), complete));
        Assert.False(VariantDirectory.MayMatch(Path(VariantRead.AsString, ComparisonOp.Equal, FilterLiteral.From("zzz"), "code"), complete));
        Assert.False(VariantDirectory.MayMatch(Path(VariantRead.AsInt64, ComparisonOp.Equal, FilterLiteral.From(9L), "code"), partial));
    }

    /// <summary>
    /// Random rows of mixed classes over five slices, their directory trimmed to random budgets: every value a row holds may
    /// match, over the object and over the row's slice, whatever the directory left out.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NoTrimmingRefutesAValueARowHolds(int seed)
    {
        Random random = new Random(seed);
        const int Rows = 600;
        PathCensus census = new PathCensus();
        List<(int Row, string Key, VariantRead Read, FilterLiteral Value)> held = [];
        for (int i = 0; i < Rows; i++)
        {
            List<(string, Variant)> fields = [];
            foreach (string key in new[] { "a", "b", "c", $"r{random.Next(200)}", $"u{i}" })
            {
                if (random.Next(4) == 0)
                {
                    continue;
                }

                (Variant value, VariantRead read, FilterLiteral literal) = random.Next(4) switch
                {
                    0 => Long(random.Next(-50, 50)),
                    1 => Text($"v{random.Next(30)}"),
                    2 => (Variant.From(random.Next(2) == 0), VariantRead.AsBool, FilterLiteral.From(false)),
                    _ => Real(random.Next(-100, 100) / 4.0),
                };
                literal = read == VariantRead.AsBool ? FilterLiteral.From(value.AsBool()!.Value) : literal;
                fields.Add((key, value));
                held.Add((i, key, read, literal));
            }

            Variant row = Variant.Object([.. fields]);
            census.Add(row.Metadata.Span, row.Value.Span, instant: i * 1_000L);
        }

        TimeSlice[] slices = [.. Enumerable.Range(0, 5).Select(s => new TimeSlice(s * 120_000L, (s + 1) * 120_000L, s * 120L, 120))];
        VariantDirectory whole = VariantDirectory.From(census, null, slices);
        int bytes = whole.ToBytes().Length;
        foreach (double share in new[] { 1.0, 0.5, 0.2, 0.05, 0.0 })
        {
            Dictionary<string, VariantDirectory> directories = new()
            {
                ["Attributes"] = VariantDirectory.From(whole.Within((long)(bytes * share)).ToBytes())!,
            };
            foreach ((int row, string key, VariantRead read, FilterLiteral value) in held)
            {
                VortexExpr equal = Path(read, ComparisonOp.Equal, value, key);
                Assert.True(VariantDirectory.MayMatch(equal, directories), $"row {row}, {key} at {share}");
                Assert.True(VariantDirectory.MayMatch(equal, directories, row / 120), $"row {row}, {key} at {share}, its slice");
            }
        }

        static (Variant, VariantRead, FilterLiteral) Long(long value) => (Variant.From(value), VariantRead.AsInt64, FilterLiteral.From(value));

        static (Variant, VariantRead, FilterLiteral) Text(string value) => (Variant.From(value), VariantRead.AsString, FilterLiteral.From(value));

        static (Variant, VariantRead, FilterLiteral) Real(double value) => (Variant.From(value), VariantRead.AsDouble, FilterLiteral.From(value));
    }

    private static byte[][][] Utf8(string[][] paths) => [.. paths.Select(path => path.Select(Encoding.UTF8.GetBytes).ToArray())];

    private static void AssertSameEntries(PathEntry[] want, PathEntry[] got)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
        {
            Assert.Equal(want[i].Path, got[i].Path);
            Assert.Equal(
                (want[i].Class, want[i].Values, want[i].Place, want[i].Low, want[i].High, want[i].NaNs),
                (got[i].Class, got[i].Values, got[i].Place, got[i].Low, got[i].High, got[i].NaNs));
            Assert.Equal(want[i].MinText, got[i].MinText);
            Assert.Equal(want[i].MaxText, got[i].MaxText);
            Assert.Equal(want[i].Slices, got[i].Slices);
        }
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
