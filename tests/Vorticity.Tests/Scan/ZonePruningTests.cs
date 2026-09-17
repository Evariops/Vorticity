// Zone-map pruning - F6, and the invariant docs/08-semantics.md §1 states as the whole point:
//
//     Pruning may never eliminate a row that full materialization would have returned.
//
// Two tests carry that between them, and they fail for OPPOSITE reasons, which is why both are
// needed. The equivalence test compares a pruned scan against the same scan with WithPruning(false)
// and fails when pruning drops a row it should have kept. The I/O test compares the segments the
// two read and fails when pruning drops nothing at all -- the failure mode the equivalence test
// cannot see, because a pruner that never prunes passes it perfectly.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ZonePruningTests
{
    /// <summary>65536 rows in 64 zones of 1024, over {monotone, banded, strs, nulls, nans}.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Theory]
    [InlineData(ComparisonOp.Greater)]
    [InlineData(ComparisonOp.GreaterOrEqual)]
    [InlineData(ComparisonOp.Less)]
    [InlineData(ComparisonOp.LessOrEqual)]
    [InlineData(ComparisonOp.Equal)]
    [InlineData(ComparisonOp.NotEqual)]
    public async Task APrunedScanReturnsExactlyWhatAnUnprunedOneDoes(ComparisonOp op)
    {
        // The property test docs/08-semantics.md §1 asks for, over a column whose zone maps are
        // informative: `monotone` is sorted, so most zones are excluded by most predicates.
        List<long> unpruned = await Read(Zoned, Predicate(op), prune: false);
        List<long> pruned = await Read(Zoned, Predicate(op), prune: true);

        Assert.Equal(unpruned, pruned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AndAndOrPruneTheSameRowsTheyFilter(bool isAnd)
    {
        VortexExpr left = Expr.Gt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(40_000L)));
        VortexExpr right = Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(41_000L)));
        VortexExpr filter = isAnd ? Expr.And(left, right) : Expr.Or(left, right);

        Assert.Equal(
            await Read(Zoned, filter, prune: false),
            await Read(Zoned, filter, prune: true));
    }

    [Fact]
    public async Task NotIsPrunedThroughDeMorganWithoutLosingRows()
    {
        VortexExpr filter = Expr.Not(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(30_000L))));

        Assert.Equal(
            await Read(Zoned, filter, prune: false),
            await Read(Zoned, filter, prune: true));
    }

    [Fact]
    public async Task IsNullPrunesAgainstTheNullCountWithoutLosingRows()
    {
        VortexExpr filter = Expr.IsNull(Expr.Field("nulls"));

        Assert.Equal(
            await Read(Zoned, filter, prune: false, column: "monotone"),
            await Read(Zoned, filter, prune: true, column: "monotone"));
    }

    [Fact]
    public async Task InPrunesAsTheDisjunctionItIs()
    {
        VortexExpr filter = Expr.In(
            Expr.Field("monotone"), FilterLiteral.From(7L), FilterLiteral.From(60_000L));

        Assert.Equal(
            await Read(Zoned, filter, prune: false),
            await Read(Zoned, filter, prune: true));
    }

    [Fact]
    public async Task PruningActuallySkipsSegmentsRatherThanJustAgreeing()
    {
        // The half the equivalence tests cannot see. A pruner that never prunes passes every one of
        // them; only the bytes say whether it does anything.
        //
        // The predicate selects a narrow band of a sorted column, so the great majority of the 64
        // zones cannot contain a match and their data segments should never be requested.
        //
        // `monotone` runs 1_000_000 upward in steps of 3, NOT 0 upward, and the band below is
        // chosen to match ~100 rows for that reason. An earlier version of this test used [1000,
        // 1100) and passed while matching NOTHING -- pruning a predicate no row satisfies proves
        // only that a pruner can skip everything, which is the easy half.
        VortexExpr narrow = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        Assert.NotEmpty(await Read(Zoned, narrow, prune: true));

        int withPruning = await CountSegments(Zoned, narrow, prune: true);
        int withoutPruning = await CountSegments(Zoned, narrow, prune: false);

        Assert.True(
            withPruning < withoutPruning,
            $"pruning requested {withPruning} segments and not pruning requested {withoutPruning}; " +
            "the pruner is not pruning anything");

        // And it is not a marginal saving: a band of ~100 rows out of 65536 should leave almost
        // every zone out. The zone maps' own segments are the floor this cannot go below.
        Assert.True(
            withPruning * 4 < withoutPruning,
            $"pruning only got the read down to {withPruning} of {withoutPruning} segments");
    }

    [Fact]
    public async Task AFilterOnAColumnWithNoZoneMapStillReturnsTheRightRows()
    {
        // containers/uncompressed_canonical has no zoned layout at all, so the pruner has nothing
        // to work with and must simply not engage.
        const string Flat = "containers/uncompressed_canonical";
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Flat), CancellationToken.None);

        string column = file.Schema.GetFieldName(0);
        VortexExpr filter = Expr.IsNotNull(Expr.Field(column));

        Assert.Equal(
            await Read(Flat, filter, prune: false, column),
            await Read(Flat, filter, prune: true, column));
    }

    private static VortexExpr Predicate(ComparisonOp op)
    {
        FieldExpr field = Expr.Field("monotone");
        LiteralExpr value = Expr.Literal(FilterLiteral.From(32_768L));
        return op switch
        {
            ComparisonOp.Equal => Expr.Eq(field, value),
            ComparisonOp.NotEqual => Expr.Ne(field, value),
            ComparisonOp.Less => Expr.Lt(field, value),
            ComparisonOp.LessOrEqual => Expr.Le(field, value),
            ComparisonOp.Greater => Expr.Gt(field, value),
            _ => Expr.Ge(field, value),
        };
    }

    private static async Task<List<long>> Read(
        string id, VortexExpr filter, bool prune, string column = "monotone")
    {
        Decoders.EnsureRegistered();
        byte[] name = System.Text.Encoding.UTF8.GetBytes(column);
        List<long> values = [];

        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(id), CancellationToken.None);

        IAsyncEnumerable<RecordBatch> scan = file.Scan()
            .Project(column)
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync();

        await foreach (RecordBatch batch in scan.WithCancellation(CancellationToken.None))
        {
            VortexColumn view = batch.Column(name);
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(view.IsValid(row) ? view.AsPrimitive<long>().Values[row] : long.MinValue);
            }
        }

        return values;
    }

    private static async Task<int> CountSegments(string id, VortexExpr filter, bool prune)
    {
        Decoders.EnsureRegistered();
        await using MemoryMappedSegmentSource inner = MemoryMappedSegmentSource.Open(Corpus.Path(id));
        RecordingSegmentSource counting = new RecordingSegmentSource(inner);

        await using VortexFile file = await VortexFile.OpenAsync(
            counting,
            new VortexOpenOptions { LeaveSourceOpen = true },
            CancellationToken.None);

        counting.ResetCounters();

        IAsyncEnumerable<RecordBatch> scan = file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync();

        await foreach (RecordBatch batch in scan.WithCancellation(CancellationToken.None))
        {
            Assert.True(batch.RowCount > 0);
        }

        return counting.Requested.Count;
    }
}
