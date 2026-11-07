// The pruners' verdicts, on every filter up to a size and every zone up to a few rows over a domain of
// edge values: the real zone pruner answers, and a three-valued evaluator written apart from the engine,
// from docs/design/08-semantics.md, says which rows the filter selects.
//
// THE DOMAIN HOLDS EVERY EDGE BY CONSTRUCTION: for a long, null, its two extremes, -1, 0 and 1; for a
// double, null, NaN, both infinities, -1, -0.0, +0.0 and 1; for a text, null, the empty string, a string
// and a longer one it prefixes, a multibyte character and the last character of the plane. A zone is a
// multiset of such values. Its statistics are taken exact, then loosened in every way the format allows:
// a lower bound lowered, an upper bound raised, either dropped, the null count or the NaN count unknown.
// A bound over zeros takes either sign, since the two compare equal, and a text bound lowered to a value
// it starts with is a truncated one.
//
// THE CLAIMS: a zone the pruner says may hold no match holds none; a zone it proves whole is selected
// row by row; a count it decides is the count of rows the filter selects. Each holds for the zone pruner
// over its bounds and for the summary pruner a dataset asks of its objects and nodes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class PrunerBoundedTests
{
    private static readonly object?[] Longs = [null, long.MinValue, -1L, 0L, 1L, long.MaxValue];

    private static readonly object?[] Doubles =
        [null, double.NaN, double.NegativeInfinity, -1.0, -0.0, 0.0, 1.0, double.PositiveInfinity];

    private static readonly object?[] Texts = [null, "", "a", "ab", "b", "é", "￿"];

    /// <summary>The prefixes and patterns of the text predicates: the empty one, the domain's, and a multibyte one.</summary>
    private static readonly string[] Prefixes = ["", "a", "ab", "b", "é", "￿"];

    private static readonly string[] Patterns = ["%", "a%", "%a", "a%b", "é%", "%￿"];

    /// <summary>The deep ring, on demand: every compound filter over zones of three rows and every loosening.</summary>
    private static readonly bool Deep = Environment.GetEnvironmentVariable("VORTICITY_HEAVY") == "1";

    [Fact]
    public void EveryFilterOfALongColumnPrunesSoundly() => Explore(Column.Long);

    [Fact]
    public void EveryFilterOfADoubleColumnPrunesSoundly() => Explore(Column.Double);

    [Fact]
    public void EveryFilterOfATextColumnPrunesSoundly() => Explore(Column.Text);

    [Fact]
    public void EveryFilterOfTwoColumnsPrunesSoundly()
    {
        // A comparison or null check on each column, joined by AND or OR, the conjunction negated too, over zones of up to
        // two rows of both columns: the edges of each domain, fewer literals, and the loosenings that matter most.
        // Two comparisons and the null checks in CI, since each column's own atoms are covered above, and IN too on demand.
        object?[] longs = [null, long.MinValue, 0L, 1L];
        object?[] doubles = [null, double.NaN, -0.0, 0.0, 1.0];
        Op[] ops = Deep ? [Op.Eq, Op.Ne, Op.Lt, Op.Ge] : [Op.Eq, Op.Lt];
        List<Filter> filters = [];
        foreach (Filter left in Atoms(Column.Long, longs, ops).Where(atom => Deep || atom is not Filter.In))
        {
            foreach (Filter right in Atoms(Column.Double, doubles, ops).Where(atom => Deep || atom is not Filter.In))
            {
                filters.Add(new Filter.And(left, right));
                filters.Add(new Filter.Or(left, right));
                filters.Add(new Filter.Not(new Filter.And(left, right)));
            }
        }

        Row[] rows = [.. longs.SelectMany(x => doubles.Select(y => new Row(x, y, null)))];
        Tally tally = Check(filters, [.. Multisets(rows, Deep ? 3 : 2)], [Loosen.Few, Loosen.Few, Loosen.None]);
        Console.WriteLine($"PRUNER two columns: {tally}");
    }

    [Theory]
    [InlineData(Column.Long)]
    [InlineData(Column.Double)]
    [InlineData(Column.Text)]
    public void TheUnionOfTwoZonesSummariesBoundsTheirRows(Column column)
    {
        // A node of a dataset's tree carries the union of its children's summaries, and is pruned by it: the union of any
        // two legal summaries must be a legal summary of the rows of both.
        Row[] values = [.. Domain(column).Select(value => Row.Of(column, value))];
        List<Row[]> zones = [.. Multisets(values, 2)];
        long checks = 0;
        foreach (Row[] left in zones)
        {
            foreach (Row[] right in zones)
            {
                object?[] rows = [.. left.Concat(right).Select(row => row.Of(column))];
                foreach (Stats a in Variants([.. left.Select(row => row.Of(column))], Loosen.All, column))
                {
                    foreach (Stats b in Variants([.. right.Select(row => row.Of(column))], Loosen.All, column))
                    {
                        checks++;
                        Vorticity.Dataset.ObjectSummaries union = Vorticity.Dataset.ObjectSummaries.Union(
                            [Summaries(a.Summary(Name(column))), Summaries(b.Summary(Name(column)))]);
                        foreach (ColumnSummary merged in union.Columns)
                        {
                            Require(Legal(merged, rows), () => $"the union {Show(merged)} of {a} over {Describe(left)} and {b} over {Describe(right)} does not bound its rows");
                        }
                    }
                }
            }
        }

        Console.WriteLine($"UNION {column}: {checks} pairs of summaries");
    }

    [Theory]
    [InlineData(Column.Long)]
    [InlineData(Column.Double)]
    [InlineData(Column.Text)]
    public void ALoosenedSummaryBoundsTheRowsADeleteLeaves(Column column)
    {
        // An object whose rows a delete marks keeps its summaries loosened, and a scan prunes its live rows by them: for
        // every zone, every legal summary of it and every set of rows marked, they must bound the rows left.
        Row[] values = [.. Domain(column).Select(value => Row.Of(column, value))];
        long checks = 0;
        foreach (Row[] zone in Multisets(values, 3))
        {
            object?[] rows = [.. zone.Select(row => row.Of(column))];
            foreach (Stats stats in Variants(rows, Loosen.All, column))
            {
                if (stats.Summary(Name(column)) is not { } summary)
                {
                    continue;
                }

                Vorticity.Dataset.ObjectSummaries loosened = Summaries(summary).Loosened();
                for (int kept = 0; kept < 1 << rows.Length; kept++)
                {
                    checks++;
                    object?[] left = [.. rows.Where((_, i) => (kept & (1 << i)) != 0)];
                    foreach (ColumnSummary bound in loosened.Columns)
                    {
                        Require(!bound.IsExact && Legal(bound, left), () => $"{Show(bound)}, loosened from {stats} over {Describe(zone)}, does not bound the rows left {string.Join(", ", left.Select(Show))}");
                    }
                }
            }
        }

        Console.WriteLine($"LOOSENED {column}: {checks} zones, summaries and marks");
    }

    private static Vorticity.Dataset.ObjectSummaries Summaries(ColumnSummary? summary) =>
        Vorticity.Dataset.ObjectSummaries.From(summary is { } one ? [one] : []);

    /// <summary>
    /// Whether a summary is one the format allows for these values: bounds below and above every non-null, non-NaN value,
    /// exact ones equal to the extremes, and a null count, when known, the count of nulls.
    /// </summary>
    private static bool Legal(ColumnSummary summary, object?[] values)
    {
        object[] ordered = [.. values.Where(value => value is not null && !IsNaN(value))!];
        long nulls = values.Count(value => value is null);
        if (summary.HasNullCount && summary.NullCount != nulls)
        {
            return false;
        }

        foreach (object value in ordered)
        {
            if ((summary.HasMin && LiteralOrder(summary.Min, value) > 0) || (summary.HasMax && LiteralOrder(summary.Max, value) < 0))
            {
                return false;
            }
        }

        if (summary.IsExact && (summary.HasMin || summary.HasMax))
        {
            if (ordered.Length == 0)
            {
                return false;
            }

            object min = ordered.MinBy(value => value, Comparer<object>.Create(Order))!;
            object max = ordered.MaxBy(value => value, Comparer<object>.Create(Order))!;
            if ((summary.HasMin && LiteralOrder(summary.Min, min) != 0) || (summary.HasMax && LiteralOrder(summary.Max, max) != 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The order of a bound against a value of its column.</summary>
    private static int LiteralOrder(FilterLiteral bound, object value) => value switch
    {
        long l => bound.SignedValue.CompareTo(l),
        double d => bound.FloatValue < d ? -1 : bound.FloatValue > d ? 1 : 0,
        string s => Math.Sign(bound.BytesValue.SequenceCompareTo(Encoding.UTF8.GetBytes(s))),
        _ => throw new InvalidOperationException($"no order for {value}"),
    };

    private static string Show(ColumnSummary summary) =>
        $"{summary.Path} [{(summary.HasMin ? summary.Min.ToString() : "-")}, {(summary.HasMax ? summary.Max.ToString() : "-")}]" +
        $"{(summary.IsExact ? " exact" : "")}, nulls {(summary.HasNullCount ? summary.NullCount.ToString(CultureInfo.InvariantCulture) : "?")}";

    private static void Explore(Column column)
    {
        object?[] domain = Domain(column);
        Row[] values = [.. domain.Select(value => Row.Of(column, value))];
        Loosen[] all = [.. Enum.GetValues<Column>().Select(other => other == column ? Loosen.All : Loosen.None)];
        Loosen[] few = [.. Enum.GetValues<Column>().Select(other => other == column ? (Deep ? Loosen.All : Loosen.Few) : Loosen.None)];

        // Each atom and its negation, over zones of up to three rows and every loosening.
        List<Filter> atoms = [.. Atoms(column, domain, Enum.GetValues<Op>())];
        Tally single = Check([.. atoms, .. atoms.Select(atom => new Filter.Not(atom))], [.. Multisets(values, 3)], all);

        // Two atoms joined by AND or OR, the disjunction negated too: zones of two rows and fewer loosenings, all of
        // them on demand.
        List<Filter> joined = [];
        foreach (Filter left in atoms)
        {
            foreach (Filter right in atoms)
            {
                joined.Add(new Filter.And(left, right));
                joined.Add(new Filter.Or(left, right));
                joined.Add(new Filter.Not(new Filter.Or(left, right)));
            }
        }

        Tally pairs = Check(joined, [.. Multisets(values, Deep ? 3 : 2)], few);
        Console.WriteLine($"PRUNER {column}: atoms {single}; pairs {pairs}");
    }

    // ------------------------------ the checks ------------------------------

    private enum Loosen
    {
        /// <summary>A column the filters do not read: one zone with nothing known.</summary>
        None,

        /// <summary>The exact statistics and the counts unknown, each bound moved to the domain's edge or dropped.</summary>
        Few,

        /// <summary>Every sign of a zero bound, every count known or not, each bound moved to every value outward or dropped.</summary>
        All,
    }

    private static Tally Check(List<Filter> filters, List<Row[]> zones, Loosen[] loosen)
    {
        Tally tally = new Tally();
        Column[] columns = Enum.GetValues<Column>();
        ZoneColumn[] zoned = [.. columns.Select(column => ZoneColumn.Single(Expr.Field(Name(column))))];
        IReadOnlyList<Stats>[][] variants = [.. zones.Select(zone => columns.Select(column =>
            Variants([.. zone.Select(row => row.Of(column))], loosen[(int)column], column)).ToArray())];
        foreach (Filter filter in filters)
        {
            VortexExpr expr = filter.Expr();
            ZonePruner pruner = new ZonePruner(expr, zoned);
            SummaryPruner summaries = new SummaryPruner(expr);
            tally.Filters++;
            for (int z = 0; z < zones.Count; z++)
            {
                Row[] zone = zones[z];
                int selected = zone.Count(row => filter.Evaluate(row) == Truth.True);
                foreach (Stats[] stats in Product(variants[z]))
                {
                    tally.Questions++;
                    for (int c = 0; c < zoned.Length; c++)
                    {
                        zoned[c].SetSingle(stats[c].Bounds, zone.Length);
                    }

                    pruner.Forget();
                    RowRange range = new RowRange(0, zone.Length);
                    string Where() => $"{filter} over {Describe(zone)} with {string.Join("; ", stats.Select((s, c) => $"{Name(columns[c])} {s}"))}";
                    if (!pruner.MayMatch(range))
                    {
                        tally.Pruned++;
                        Require(selected == 0, () => $"pruned, yet {selected} row(s) selected: {Where()}");
                    }

                    if (pruner.MustMatch(range))
                    {
                        tally.Whole++;
                        Require(selected == zone.Length, () => $"proven whole, yet {selected} of {zone.Length} selected: {Where()}");
                    }

                    if (pruner.TryCount(range, out long count))
                    {
                        tally.Counted++;
                        Require(count == selected, () => $"counted {count}, yet {selected} selected: {Where()}");
                    }

                    // The summary pruner, which knows no NaN count, asked of the same bounds.
                    if (stats.All(s => !s.NanKnown))
                    {
                        List<ColumnSummary> summarised = [.. stats.Select((s, c) => s.Summary(Name(columns[c]))).OfType<ColumnSummary>()];
                        Require(summaries.MayMatch(summarised, zone.Length) || selected == 0, () => $"summary pruned, yet {selected} selected: {Where()}");
                        Require(!summaries.MustMatch(summarised, zone.Length) || selected == zone.Length, () => $"summary proven whole, yet {selected} of {zone.Length} selected: {Where()}");
                    }
                }
            }
        }

        return tally;
    }

    /// <summary>Every choice of one statistics variant per column.</summary>
    private static IEnumerable<Stats[]> Product(IReadOnlyList<Stats>[] perColumn)
    {
        int[] at = new int[perColumn.Length];
        while (true)
        {
            yield return [.. perColumn.Select((variants, c) => variants[at[c]])];
            int c = perColumn.Length - 1;
            while (c >= 0 && ++at[c] == perColumn[c].Count)
            {
                at[c] = 0;
                c--;
            }

            if (c < 0)
            {
                yield break;
            }
        }
    }

    private static string Describe(Row[] zone) => "[" + string.Join(", ", zone.Select(row => row.ToString())) + "]";

    /// <summary>Fails with the message, built only then, when the claim does not hold.</summary>
    private static void Require(bool holds, Func<string> message)
    {
        if (!holds)
        {
            Assert.Fail(message());
        }
    }

    // ------------------------------ the filters ------------------------------

    /// <summary>
    /// Each comparison of the column with each literal, null included, its null checks, IN over one or two values, and for
    /// a text its prefix, pattern and substring tests.
    /// </summary>
    private static IEnumerable<Filter> Atoms(Column column, object?[] literals, Op[] ops)
    {
        foreach (Op op in ops)
        {
            foreach (object? literal in literals)
            {
                yield return new Filter.Compare(column, op, literal);
            }
        }

        yield return new Filter.IsNull(column, true);
        yield return new Filter.IsNull(column, false);
        object[] values = [.. literals.OfType<object>()];
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = i; j < values.Length; j++)
            {
                yield return new Filter.In(column, i == j ? [values[i]] : [values[i], values[j]]);
            }
        }

        if (column == Column.Text)
        {
            foreach (string prefix in Prefixes)
            {
                yield return new Filter.Match(MatchKind.StartsWith, prefix);
                yield return new Filter.Match(MatchKind.Contains, prefix);
            }

            foreach (string pattern in Patterns)
            {
                yield return new Filter.Match(MatchKind.Like, pattern);
            }
        }
    }

    // ------------------------------ the zones and their statistics ------------------------------

    private static IEnumerable<Row[]> Multisets(Row[] values, int most)
    {
        for (int size = 1; size <= most; size++)
        {
            foreach (int[] picks in Picks(values.Length, size, 0))
            {
                yield return [.. picks.Select(pick => values[pick])];
            }
        }
    }

    private static IEnumerable<int[]> Picks(int count, int size, int from)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }

        for (int i = from; i < count; i++)
        {
            foreach (int[] rest in Picks(count, size - 1, i))
            {
                yield return [i, .. rest];
            }
        }
    }

    /// <summary>
    /// The statistics a zone of these values may have: the exact ones, a zero bound of either sign, then each way to
    /// loosen them, a bound moved outward to another value of the domain or dropped, a count unknown.
    /// </summary>
    private static IReadOnlyList<Stats> Variants(object?[] values, Loosen loosen, Column column)
    {
        long nulls = values.Count(value => value is null);
        long nans = values.Count(IsNaN);
        if (loosen == Loosen.None)
        {
            return [new Stats(null, null, false, nulls, false, nans, false, column)];
        }

        bool fewer = loosen == Loosen.Few;
        object[] ordered = [.. values.Where(value => value is not null && !IsNaN(value))!];
        object[] bounds = [.. Domain(column).Where(value => value is not null && !IsNaN(value))!];
        List<Stats> variants = [];
        if (ordered.Length == 0)
        {
            // Nothing to bound: no bound, or bounds of any value, inexact, since they bound nothing.
            foreach ((bool nullsKnown, bool nansKnown) in Knowns(fewer))
            {
                variants.Add(new Stats(null, null, false, nulls, nullsKnown, nans, nansKnown, column));
            }

            if (!fewer)
            {
                foreach (object low in bounds)
                {
                    foreach (object high in bounds.Where(high => Order(high, low) >= 0))
                    {
                        variants.Add(new Stats(low, high, false, nulls, true, nans, true, column));
                    }
                }
            }

            return variants;
        }

        object min = ordered.MinBy(value => value, Comparer<object>.Create(Order))!;
        object max = ordered.MaxBy(value => value, Comparer<object>.Create(Order))!;
        foreach (object low in Signs(min))
        {
            foreach (object high in Signs(max))
            {
                foreach ((bool nullsKnown, bool nansKnown) in Knowns(fewer))
                {
                    variants.Add(new Stats(low, high, true, nulls, nullsKnown, nans, nansKnown, column));
                }
            }
        }

        // Loosened: each bound moved outward to another value of the domain, or dropped.
        object?[] lows = [null, .. bounds.Where(value => Order(value, min) <= 0)];
        object?[] highs = [null, .. bounds.Where(value => Order(value, max) >= 0)];
        if (fewer)
        {
            lows = [null, lows.OfType<object>().First()];
            highs = [null, highs.OfType<object>().Last()];
        }

        foreach (object? low in lows)
        {
            foreach (object? high in highs)
            {
                variants.Add(new Stats(low, high, false, nulls, true, nans, true, column));
            }
        }

        return variants;
    }

    private static IEnumerable<(bool Nulls, bool Nans)> Knowns(bool fewer) =>
        fewer ? [(true, true), (false, false)] : [(true, true), (true, false), (false, true), (false, false)];

    /// <summary>A bound and, when it is a zero, the zero of the other sign, which compares equal to it.</summary>
    private static IEnumerable<object> Signs(object value) =>
        value is double d && d == 0 ? [0.0, -0.0] : [value];

    private static bool IsNaN(object? value) => value is double d && double.IsNaN(d);

    private static object?[] Domain(Column column) => column switch
    {
        Column.Long => Longs,
        Column.Double => Doubles,
        _ => Texts,
    };

    private static string Name(Column column) => column switch
    {
        Column.Long => "x",
        Column.Double => "y",
        _ => "s",
    };

    /// <summary>
    /// The order of two non-null, non-NaN values of one column: numeric, -0.0 equal to +0.0, and a text's by its UTF-8
    /// bytes, the order its statistics and comparisons keep.
    /// </summary>
    private static int Order(object? left, object? right) => (left, right) switch
    {
        (long a, long b) => a.CompareTo(b),
        (double a, double b) => a < b ? -1 : a > b ? 1 : 0,
        (string a, string b) => Math.Sign(Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b))),
        _ => throw new InvalidOperationException($"{left} and {right} do not order"),
    };

    /// <summary>A zone's statistics for one column: its bounds, whether they are its extremes, its counts.</summary>
    private sealed record Stats(object? Min, object? Max, bool Exact, long Nulls, bool NullsKnown, long Nans, bool NanKnown, Column Column)
    {
        internal ZoneBounds Bounds => ZoneBounds.Create(
            Literal(Min), Min is not null, Literal(Max), Max is not null, Exact, Nulls, NullsKnown, Nans, NanKnown);

        internal ColumnSummary? Summary(string path) =>
            Min is null && Max is null && !NullsKnown
                ? null
                : new ColumnSummary(path, Literal(Min), Min is not null, Literal(Max), Max is not null, Exact, Nulls, NullsKnown);

        public override string ToString() =>
            $"[{Show(Min)}, {Show(Max)}]{(Exact ? " exact" : "")}, nulls {(NullsKnown ? Nulls.ToString(CultureInfo.InvariantCulture) : "?")}" +
            (Column == Column.Double ? $", NaN {(NanKnown ? Nans.ToString(CultureInfo.InvariantCulture) : "?")}" : "");
    }

    private static FilterLiteral Literal(object? value) => value switch
    {
        null => FilterLiteral.Null,
        long l => FilterLiteral.From(l),
        double d => FilterLiteral.From(d),
        string s => FilterLiteral.From(s),
        _ => throw new InvalidOperationException($"no literal of {value}"),
    };

    private static string Show(object? value) => value switch
    {
        null => "null",
        double d when d == 0 && double.IsNegative(d) => "-0.0",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string s => $"'{string.Concat(s.Select(c => c < 0x80 ? c.ToString() : $"\\u{(int)c:X4}"))}'",
        _ => value.ToString() ?? "?",
    };

    // ------------------------------ the reference ------------------------------

    public enum Column
    {
        Long,
        Double,
        Text,
    }

    private enum Op
    {
        Eq,
        Ne,
        Lt,
        Le,
        Gt,
        Ge,
    }

    private enum MatchKind
    {
        StartsWith,
        Contains,
        Like,
    }

    private enum Truth
    {
        False,
        Unknown,
        True,
    }

    /// <summary>A row of the three columns, x a long, y a double and s a text, any of them null.</summary>
    private readonly record struct Row(object? X, object? Y, object? S)
    {
        internal static Row Of(Column column, object? value) => column switch
        {
            Column.Long => new Row(value, null, null),
            Column.Double => new Row(null, value, null),
            _ => new Row(null, null, value),
        };

        internal object? Of(Column column) => column switch
        {
            Column.Long => X,
            Column.Double => Y,
            _ => S,
        };

        public override string ToString() => $"({Show(X)}, {Show(Y)}, {Show(S)})";
    }

    /// <summary>A filter, as the engine takes it and as the reference evaluates it.</summary>
    private abstract record Filter
    {
        internal abstract VortexExpr Expr();

        internal abstract Truth Evaluate(Row row);

        private static FieldExpr Field(Column column) => Vorticity.Expressions.Expr.Field(Name(column));

        /// <summary>A comparison: unknown on a null operand, false on NaN but for <c>!=</c>, which is true there.</summary>
        internal sealed record Compare(Column Column, Op Op, object? Literal) : Filter
        {
            internal override VortexExpr Expr()
            {
                VortexExpr field = Field(Column);
                VortexExpr value = Vorticity.Expressions.Expr.Literal(PrunerBoundedTests.Literal(Literal));
                return Op switch
                {
                    Op.Eq => Vorticity.Expressions.Expr.Eq(field, value),
                    Op.Ne => Vorticity.Expressions.Expr.Ne(field, value),
                    Op.Lt => Vorticity.Expressions.Expr.Lt(field, value),
                    Op.Le => Vorticity.Expressions.Expr.Le(field, value),
                    Op.Gt => Vorticity.Expressions.Expr.Gt(field, value),
                    _ => Vorticity.Expressions.Expr.Ge(field, value),
                };
            }

            internal override Truth Evaluate(Row row)
            {
                object? value = row.Of(Column);
                if (value is null || Literal is null)
                {
                    return Truth.Unknown;
                }

                if (IsNaN(value) || IsNaN(Literal))
                {
                    return Op == Op.Ne ? Truth.True : Truth.False;
                }

                int order = Order(value, Literal);
                bool holds = Op switch
                {
                    Op.Eq => order == 0,
                    Op.Ne => order != 0,
                    Op.Lt => order < 0,
                    Op.Le => order <= 0,
                    Op.Gt => order > 0,
                    _ => order >= 0,
                };
                return holds ? Truth.True : Truth.False;
            }

            public override string ToString() => $"{Name(Column)} {Op} {Show(Literal)}";
        }

        /// <summary><c>IS NULL</c> or <c>IS NOT NULL</c>: never unknown.</summary>
        internal sealed record IsNull(Column Column, bool Null) : Filter
        {
            internal override VortexExpr Expr() =>
                Null ? Vorticity.Expressions.Expr.IsNull(Field(Column)) : Vorticity.Expressions.Expr.IsNotNull(Field(Column));

            internal override Truth Evaluate(Row row) => (row.Of(Column) is null) == Null ? Truth.True : Truth.False;

            public override string ToString() => $"{Name(Column)} IS {(Null ? "" : "NOT ")}NULL";
        }

        /// <summary>Membership in non-null values: unknown on a null row, true on an equal value, NaN equal to none.</summary>
        internal sealed record In(Column Column, object[] Values) : Filter
        {
            internal override VortexExpr Expr() =>
                Vorticity.Expressions.Expr.In(Field(Column), [.. Values.Select(PrunerBoundedTests.Literal)]);

            internal override Truth Evaluate(Row row) =>
                row.Of(Column) is null ? Truth.Unknown
                : Values.Any(candidate => new Compare(Column, Op.Eq, candidate).Evaluate(row) == Truth.True) ? Truth.True
                : Truth.False;

            public override string ToString() => $"{Name(Column)} IN ({string.Join(", ", Values.Select(Show))})";
        }

        /// <summary>A text's prefix, substring or LIKE test, on its UTF-8 bytes: unknown on a null row.</summary>
        internal sealed record Match(MatchKind Kind, string Pattern) : Filter
        {
            internal override VortexExpr Expr()
            {
                FieldExpr field = Field(Column.Text);
                FilterLiteral pattern = FilterLiteral.From(Pattern);
                return Kind switch
                {
                    MatchKind.StartsWith => Vorticity.Expressions.Expr.StartsWith(field, pattern),
                    MatchKind.Contains => Vorticity.Expressions.Expr.Contains(field, pattern),
                    _ => Vorticity.Expressions.Expr.Like(field, pattern),
                };
            }

            internal override Truth Evaluate(Row row)
            {
                if (row.S is not string value)
                {
                    return Truth.Unknown;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(value);
                bool holds = Kind switch
                {
                    MatchKind.StartsWith => bytes.AsSpan().StartsWith(Encoding.UTF8.GetBytes(Pattern)),
                    MatchKind.Contains => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Pattern)) >= 0,
                    _ => Like(bytes, Pattern.Split('%').Select(Encoding.UTF8.GetBytes).ToArray()),
                };
                return holds ? Truth.True : Truth.False;
            }

            /// <summary>A LIKE whose only wildcard is <c>%</c>: the first piece leads, the last ends, the others follow in order.</summary>
            private static bool Like(byte[] value, byte[][] pieces)
            {
                ReadOnlySpan<byte> rest = value;
                if (pieces.Length == 1)
                {
                    return rest.SequenceEqual(pieces[0]);
                }

                if (!rest.StartsWith(pieces[0]))
                {
                    return false;
                }

                rest = rest[pieces[0].Length..];
                for (int i = 1; i < pieces.Length - 1; i++)
                {
                    int at = rest.IndexOf(pieces[i]);
                    if (at < 0)
                    {
                        return false;
                    }

                    rest = rest[(at + pieces[i].Length)..];
                }

                return rest.EndsWith(pieces[^1]);
            }

            public override string ToString() => $"s {Kind} {Show(Pattern)}";
        }

        internal sealed record Not(Filter Operand) : Filter
        {
            internal override VortexExpr Expr() => Vorticity.Expressions.Expr.Not(Operand.Expr());

            internal override Truth Evaluate(Row row) => Operand.Evaluate(row) switch
            {
                Truth.True => Truth.False,
                Truth.False => Truth.True,
                _ => Truth.Unknown,
            };

            public override string ToString() => $"NOT ({Operand})";
        }

        internal sealed record And(Filter Left, Filter Right) : Filter
        {
            internal override VortexExpr Expr() => Vorticity.Expressions.Expr.And(Left.Expr(), Right.Expr());

            internal override Truth Evaluate(Row row) => (Truth)Math.Min((int)Left.Evaluate(row), (int)Right.Evaluate(row));

            public override string ToString() => $"({Left}) AND ({Right})";
        }

        internal sealed record Or(Filter Left, Filter Right) : Filter
        {
            internal override VortexExpr Expr() => Vorticity.Expressions.Expr.Or(Left.Expr(), Right.Expr());

            internal override Truth Evaluate(Row row) => (Truth)Math.Max((int)Left.Evaluate(row), (int)Right.Evaluate(row));

            public override string ToString() => $"({Left}) OR ({Right})";
        }
    }

    private sealed class Tally
    {
        internal long Filters { get; set; }

        internal long Questions { get; set; }

        internal long Pruned { get; set; }

        internal long Whole { get; set; }

        internal long Counted { get; set; }

        public override string ToString() =>
            $"{Filters} filters, {Questions} questions, {Pruned} pruned, {Whole} proven whole, {Counted} counts decided";
    }
}
