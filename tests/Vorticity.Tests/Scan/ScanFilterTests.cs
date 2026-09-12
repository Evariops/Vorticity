// Filter pushdown, end to end over real files - docs/01-scope.md F7.
//
// THE SHAPE OF EVERY TEST HERE IS THE SAME, and it is the only shape worth using: read the file
// twice, once unfiltered and once filtered, apply the predicate to the unfiltered result in plain
// C#, and assert the two agree. A test that asserts a filtered scan returns "the right rows"
// against hand-written expectations proves the predicate was implemented the way the test author
// understood it; this proves it was implemented the way the DATA says, over whatever the corpus
// actually contains, including its nulls and its NaNs.
//
// That makes these differential tests against the library's own unfiltered read, which is already
// verified value-for-value against the Rust reference by the conformance suite. The filter
// therefore inherits that anchor rather than needing its own.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanFilterTests
{
    /// <summary>{monotone=i64, banded=i32, strs=utf8, nulls=i64?, nans=f64}: nulls and NaNs both.</summary>
    private const string Mixed = "containers/zoned_many_zones_nulls";

    [Theory]
    [InlineData(ComparisonOp.Greater)]
    [InlineData(ComparisonOp.GreaterOrEqual)]
    [InlineData(ComparisonOp.Less)]
    [InlineData(ComparisonOp.LessOrEqual)]
    [InlineData(ComparisonOp.Equal)]
    [InlineData(ComparisonOp.NotEqual)]
    public async Task EveryComparisonAgreesWithTheSamePredicateInCSharp(ComparisonOp op)
    {
        List<long?> all = await ReadInt64(Mixed, "monotone", filter: null);
        long pivot = all[all.Count / 2] ?? 0;

        VortexExpr filter = Build(Expr.Field("monotone"), op, FilterLiteral.From(pivot));
        List<long?> filtered = await ReadInt64(Mixed, "monotone", filter);

        List<long?> expected = [];
        foreach (long? value in all)
        {
            if (value is long v && Matches(op, v.CompareTo(pivot)))
            {
                expected.Add(v);
            }
        }

        Assert.Equal(expected, filtered);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task ANullRowMatchesNeitherAPredicateNorItsNegation()
    {
        // The observable face of three-valued logic (docs/08-semantics.md §3): `x = k` and
        // `x != k` are not complements over a nullable column, and their results do not add up to
        // the row count.
        List<long?> all = await ReadInt64(Mixed, "nulls", filter: null);
        int nulls = 0;
        foreach (long? value in all)
        {
            if (value is null)
            {
                nulls++;
            }
        }

        Assert.True(nulls > 0, "the fixture is supposed to have null rows");

        long pivot = 0;
        int equal = (await ReadInt64(Mixed, "nulls", Compare(ComparisonOp.Equal, pivot))).Count;
        int notEqual = (await ReadInt64(Mixed, "nulls", Compare(ComparisonOp.NotEqual, pivot))).Count;

        Assert.Equal(all.Count - nulls, equal + notEqual);
    }

    [Fact]
    public async Task IsNullAndIsNotNullPartitionTheRows()
    {
        // Unlike a comparison, these two ARE complements: neither ever yields unknown.
        List<long?> all = await ReadInt64(Mixed, "nulls", filter: null);
        List<long?> isNull = await ReadInt64(Mixed, "nulls", Expr.IsNull(Expr.Field("nulls")));
        List<long?> isNotNull = await ReadInt64(Mixed, "nulls", Expr.IsNotNull(Expr.Field("nulls")));

        Assert.Equal(all.Count, isNull.Count + isNotNull.Count);
        Assert.All(isNull, value => Assert.Null(value));
        Assert.All(isNotNull, value => Assert.NotNull(value));
        Assert.NotEmpty(isNull);
        Assert.NotEmpty(isNotNull);
    }

    [Fact]
    public async Task AndOrAndNotAgreeWithTheSameLogicInCSharp()
    {
        List<long?> all = await ReadInt64(Mixed, "monotone", filter: null);
        long low = all[all.Count / 4] ?? 0;
        long high = all[3 * all.Count / 4] ?? 0;

        VortexExpr between = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(low))),
            Expr.Le(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(high))));

        List<long?> inRange = await ReadInt64(Mixed, "monotone", between);
        List<long?> outOfRange = await ReadInt64(Mixed, "monotone", Expr.Not(between));

        List<long?> expectedIn = [];
        foreach (long? value in all)
        {
            if (value is long v && v >= low && v <= high)
            {
                expectedIn.Add(v);
            }
        }

        Assert.Equal(expectedIn, inRange);

        // NOT over a column with no nulls is a true complement; `monotone` is non-nullable.
        Assert.Equal(all.Count, inRange.Count + outOfRange.Count);
    }

    [Fact]
    public async Task InIsAnOrOfEqualities()
    {
        List<long?> all = await ReadInt64(Mixed, "monotone", filter: null);
        long a = all[10] ?? 0;
        long b = all[200] ?? 0;

        List<long?> membership = await ReadInt64(
            Mixed, "monotone",
            Expr.In(Expr.Field("monotone"), FilterLiteral.From(a), FilterLiteral.From(b)));

        List<long?> disjunction = await ReadInt64(
            Mixed, "monotone",
            Expr.Or(
                Expr.Eq(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(a))),
                Expr.Eq(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(b)))));

        Assert.Equal(disjunction, membership);
        Assert.NotEmpty(membership);
    }

    [Fact]
    public async Task NaNIsFalseForEveryOrderingPredicateAndTrueForNotEqual()
    {
        // IEEE 754 exactly, not the folk version. The five ordering-and-equality predicates are
        // false when an operand is NaN; `!=` is the negation of `==` and is therefore TRUE
        // (docs/08-semantics.md §2, whose first draft said otherwise and cost this test a run).
        List<double?> all = await ReadDouble(Mixed, "nans", filter: null);
        int nans = 0;
        foreach (double? value in all)
        {
            if (value is double v && double.IsNaN(v))
            {
                nans++;
            }
        }

        Assert.True(nans > 0, "the fixture is supposed to have NaN rows");

        FilterLiteral zero = FilterLiteral.From(0.0);
        List<double?> greater = await ReadDouble(
            Mixed, "nans", Expr.Gt(Expr.Field("nans"), Expr.Literal(zero)));
        List<double?> notGreater = await ReadDouble(
            Mixed, "nans", Expr.Le(Expr.Field("nans"), Expr.Literal(zero)));
        List<double?> equal = await ReadDouble(
            Mixed, "nans", Expr.Eq(Expr.Field("nans"), Expr.Literal(zero)));
        List<double?> notEqual = await ReadDouble(
            Mixed, "nans", Expr.Ne(Expr.Field("nans"), Expr.Literal(zero)));

        Assert.DoesNotContain(greater, value => double.IsNaN(value!.Value));
        Assert.DoesNotContain(notGreater, value => double.IsNaN(value!.Value));
        Assert.DoesNotContain(equal, value => double.IsNaN(value!.Value));

        // Every NaN row, and only those, is what `!=` adds over the complement of `==`.
        int notEqualNans = 0;
        foreach (double? value in notEqual)
        {
            if (double.IsNaN(value!.Value))
            {
                notEqualNans++;
            }
        }

        Assert.Equal(nans, notEqualNans);
        Assert.Equal(all.Count, equal.Count + notEqual.Count);

        // `>` and `<=` do partition the non-NaN rows and leave the NaN ones out of both.
        Assert.Equal(all.Count - nans, greater.Count + notGreater.Count);
    }

    [Fact]
    public async Task AFilterColumnIsReadAndThenDroppedFromTheBatch()
    {
        // docs/03-architecture.md §3.4: "the filter sees columns that are not projected; they are
        // read for filtering and discarded before the batch is produced".
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Mixed), CancellationToken.None);

        IAsyncEnumerable<RecordBatch> scan = file.Scan()
            .Project("strs")
            .Where(Expr.Gt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(0L))))
            .ExecuteAsync();

        int batches = 0;
        await foreach (RecordBatch batch in scan.WithCancellation(CancellationToken.None))
        {
            batches++;
            Assert.Equal(1, batch.Schema.FieldCount);
            Assert.Equal("strs", batch.Schema.GetFieldName(0));
            Assert.True(batch.RowCount > 0, "an emptied batch must not be produced");
        }

        Assert.True(batches > 0);
    }

    [Fact]
    public async Task AFilterThatMatchesNothingProducesNoBatchesAtAll()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Mixed), CancellationToken.None);

        IAsyncEnumerable<RecordBatch> scan = file.Scan()
            .Where(Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(long.MinValue))))
            .ExecuteAsync();

        await foreach (RecordBatch batch in scan.WithCancellation(CancellationToken.None))
        {
            Assert.Fail($"a filter matching nothing produced a batch of {batch.RowCount} rows");
        }
    }

    [Fact]
    public async Task AStringFilterComparesBytesOrdinally()
    {
        List<string?> all = await ReadString(Mixed, "strs", filter: null);
        string pivot = all[all.Count / 2]!;

        List<string?> filtered = await ReadString(
            Mixed, "strs", Expr.Ge(Expr.Field("strs"), Expr.Literal(FilterLiteral.From(pivot))));

        List<string?> expected = [];
        foreach (string? value in all)
        {
            if (value is not null && string.CompareOrdinal(value, pivot) >= 0)
            {
                expected.Add(value);
            }
        }

        Assert.Equal(expected, filtered);
    }

    [Fact]
    public void ComparingTwoColumnsIsRefusedWhenTheFilterIsBuilt()
    {
        // At build time, with a message that names the limit, rather than from inside a scan.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Expr.Gt(Expr.Field("a"), Expr.Field("b")));
        Assert.Contains("two columns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilterOnAFieldTheSchemaLacksIsRefusedWhenItIsSet()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Mixed), CancellationToken.None);

        Assert.Throws<ArgumentException>(
            () => file.Scan().Where(
                Expr.Eq(Expr.Field("nosuchcolumn"), Expr.Literal(FilterLiteral.From(1L)))));
    }

    // ------------------------------------------------------------------------------- fixtures

    private static VortexExpr Compare(ComparisonOp op, long value) =>
        Build(Expr.Field("nulls"), op, FilterLiteral.From(value));

    private static VortexExpr Build(FieldExpr field, ComparisonOp op, FilterLiteral value)
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

    private static bool Matches(ComparisonOp op, int order) => op switch
    {
        ComparisonOp.Equal => order == 0,
        ComparisonOp.NotEqual => order != 0,
        ComparisonOp.Less => order < 0,
        ComparisonOp.LessOrEqual => order <= 0,
        ComparisonOp.Greater => order > 0,
        _ => order >= 0,
    };

    private static async Task<List<long?>> ReadInt64(string id, string column, VortexExpr? filter)
    {
        List<long?> values = [];
        await Read(id, column, filter, (batch, name, row) =>
        {
            PrimitiveColumn<long> typed = batch.Column(name).AsPrimitive<long>();
            values.Add(batch.Column(name).IsValid(row) ? typed.Values[row] : null);
        });

        return values;
    }

    private static async Task<List<double?>> ReadDouble(string id, string column, VortexExpr? filter)
    {
        List<double?> values = [];
        await Read(id, column, filter, (batch, name, row) =>
        {
            PrimitiveColumn<double> typed = batch.Column(name).AsPrimitive<double>();
            values.Add(batch.Column(name).IsValid(row) ? typed.Values[row] : null);
        });

        return values;
    }

    private static async Task<List<string?>> ReadString(string id, string column, VortexExpr? filter)
    {
        List<string?> values = [];
        await Read(id, column, filter, (batch, name, row) =>
            values.Add(batch.Column(name).AsBinary().GetString(row)));

        return values;
    }

    private static async Task Read(
        string id, string column, VortexExpr? filter, Action<RecordBatch, byte[], int> read)
    {
        Decoders.EnsureRegistered();
        byte[] name = System.Text.Encoding.UTF8.GetBytes(column);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(id), CancellationToken.None);
        ScanBuilder builder = file.Scan().Project(column);
        if (filter is not null)
        {
            builder = builder.Where(filter);
        }

        await foreach (RecordBatch batch in builder.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                read(batch, name, row);
            }
        }
    }
}
